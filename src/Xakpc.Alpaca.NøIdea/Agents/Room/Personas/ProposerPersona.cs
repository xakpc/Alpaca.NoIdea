using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Agents.Room.Personas;

/// <summary>
/// The seat that opens the room: it searches the allowed universe and puts one operation
/// forward, or returns NO_TRADE.
/// </summary>
/// <remarks>
/// <para>
/// It carries the full read-only Alpaca toolset because it is the only seat that has to look
/// at the market rather than react to somebody else's reading of it. It runs on the strongest
/// model available, since finding a trade is harder than judging one.
/// </para>
/// <para>
/// <b>It does not get final authority over quantity</b> (spec §16). It states the size it
/// wants; the room's conviction scales it and <c>RiskGuard</c> caps it.
/// </para>
/// </remarks>
public sealed class ProposerPersona(
    ChatClientFactory clients, ILogger logger, IReadOnlyList<AITool> alpacaTools)
    : LlmPersona(clients, logger, alpacaTools), IProposingPersona
{
    private const string ProposeTool = "submit_proposal";
    private const string RebutTool = "submit_rebuttal";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string Name => "proposer";

    public override ModelProvider Provider => ModelProvider.Anthropic;

    protected override string Model => "claude-opus-5";

    /// <summary>Claude Opus 5 takes no temperature. Sending one is a 400.</summary>
    protected override float? SamplingTemperature => null;

    protected override int MaxOutputTokens => 4000;

    protected override string RolePrompt =>
        """
        You are the PROPOSER in an autonomous options trading war room. You find the best
        trade currently available inside the limits you are given, or you say there is none.
        """;

    // ------------------------------------------------------------------ §15 propose

    public async Task<ProposedOperation> ProposeAsync(
        StrategyContext market,
        WarRoomPurpose purpose,
        PositionUnderReview? position,
        IReadOnlyList<StrategyActionKind> allowedActions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(allowedActions);

        ProposedOperation? captured = null;

        var offered = market.Candidates
            .Select(view => view.Candidate.ContractSymbol)
            .ToHashSet(StringComparer.Ordinal);

        var held = market.Positions
            .Select(item => item.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        var tool = AIFunctionFactory.Create(
            (ProposalArguments arguments) =>
            {
                captured = ToOperation(arguments, offered, held, allowedActions);
                return "Proposal recorded.";
            },
            ProposeTool,
            "Submit your single best proposal, or NO_TRADE. Call this exactly once, last.");

        var payload = purpose == WarRoomPurpose.PositionReview
            ? DescribeReview(market, position, allowedActions)
            : DescribeSearch(market, allowedActions);

        var response = await SafeCallAsync(
            purpose == WarRoomPurpose.PositionReview
                ? ReviewPrompt(allowedActions)
                : SearchPrompt(allowedActions),
            payload,
            [.. ResearchTools, tool],
            MaxOutputTokens,
            cancellationToken);

        if (response is not null)
        {
            Logger.LogError("The proposer failed: {Fault}. Treating as NO_TRADE.", response);
            return ProposedOperation.Nothing("the proposer failed");
        }

        return captured ?? ProposedOperation.Nothing("the proposer submitted nothing");
    }

    // ------------------------------------------------------------------ §20 rebuttal

    public async Task<ProposedOperation> RebutAsync(
        RoomContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ProposedOperation? captured = null;

        var offered = context.Market.Candidates
            .Select(view => view.Candidate.ContractSymbol)
            .ToHashSet(StringComparer.Ordinal);

        var held = context.Market.Positions
            .Select(item => item.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        var tool = AIFunctionFactory.Create(
            (ProposalArguments arguments) =>
            {
                captured = ToOperation(arguments, offered, held, context.AllowedActions);
                return "Rebuttal recorded.";
            },
            RebutTool,
            "Defend, modify, or withdraw your proposal. Call this exactly once, last.");

        var fault = await SafeCallAsync(
            $"""
            {Preamble}

            THE ROOM HAS ANSWERED
            Your proposal has been analysed independently and then debated. Read what was
            said and decide.

            You may:
            - DEFEND: resubmit the same operation unchanged.
            - MODIFY: submit a different contract, strike, expiration or size. A modification
              is treated as a new proposal and is re-validated by code.
            - WITHDRAW: submit no actions.

            Weigh the reviewers, do not count them. A reviewer naming something concrete you
            missed has earned a change. One offering generic caution has not: every trade can
            lose, and that observation carries no information. Several reviewers repeating one
            another is still one argument.

            Do not capitulate automatically and do not dig in automatically. Both are
            failures. Withdrawing every challenged trade produces a system that holds cash for
            four days and returns nothing.

            Call `{RebutTool}` once, last.
            """,
            DescribeRebuttal(context),
            [tool],
            2500,
            cancellationToken);

        if (fault is not null)
        {
            // The original stands. A broken rebuttal must not silently withdraw a proposal.
            return context.Operation;
        }

        return captured ?? context.Operation;
    }

    // ------------------------------------------------------------------ shaping

    private ProposedOperation ToOperation(
        ProposalArguments arguments,
        HashSet<string> offered,
        HashSet<string> held,
        IReadOnlyList<StrategyActionKind> allowedActions)
    {
        if (arguments.NoTrade == true || arguments.Actions is null || arguments.Actions.Count == 0)
        {
            return ProposedOperation.Nothing(
                string.IsNullOrWhiteSpace(arguments.Thesis) ? "NO_TRADE" : arguments.Thesis!);
        }

        var actions = new List<StrategyAction>();

        foreach (var item in arguments.Actions)
        {
            var kind = item.Action?.ToLowerInvariant() switch
            {
                "open_call" => StrategyActionKind.OpenCall,
                "open_put" => StrategyActionKind.OpenPut,
                "close" or "close_position" => StrategyActionKind.ClosePosition,
                _ => StrategyActionKind.Hold,
            };

            if (kind == StrategyActionKind.Hold || !allowedActions.Contains(kind))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.ContractSymbol))
            {
                continue;
            }

            // A symbol the harness did not offer never reaches the broker. Dropping it here
            // records it as a proposer fault rather than a risk rejection later.
            var known = kind == StrategyActionKind.ClosePosition ? held : offered;
            if (!known.Contains(item.ContractSymbol))
            {
                Logger.LogWarning(
                    "The proposer named {Symbol}, which was not available this cycle. Dropped.",
                    item.ContractSymbol);
                continue;
            }

            actions.Add(new StrategyAction
            {
                Kind = kind,
                ContractSymbol = item.ContractSymbol,
                Contracts = Math.Max(1, item.Contracts ?? 1),
                Probability = item.Probability is { } probability
                    ? Math.Clamp(probability, 0m, 1m)
                    : null,
                Reasoning = string.IsNullOrWhiteSpace(item.Reasoning)
                    ? arguments.Thesis ?? "(no reasoning given)"
                    : item.Reasoning,
            });
        }

        if (actions.Count == 0)
        {
            return ProposedOperation.Nothing("nothing survived validation");
        }

        return new ProposedOperation
        {
            Actions = actions,
            Thesis = string.IsNullOrWhiteSpace(arguments.Thesis) ? "(no thesis given)" : arguments.Thesis!,
            ThesisConditions = arguments.ThesisConditions ?? [],
            MainRisks = arguments.MainRisks ?? [],
        };
    }

    private async Task<string?> SafeCallAsync(
        string systemPrompt,
        string payload,
        IReadOnlyList<AITool> tools,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        ChatResponse? response = null;

        try
        {
            response = await Clients.For(Provider, Name).GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, payload),
                ],
                new ChatOptions
                {
                    ModelId = Model,
                    Temperature = SamplingTemperature,
                    MaxOutputTokens = maxOutputTokens,
                    Tools = [.. tools],
                    ToolMode = ChatToolMode.Auto,
                },
                cancellationToken);

            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return error.Message;
        }
        finally
        {
            RecordCall(Model, response);
        }
    }

    private string SearchPrompt(IReadOnlyList<StrategyActionKind> allowed) =>
        $"""
        {Preamble}

        YOUR TASK
        Find the single best trade available right now among the candidates you are given,
        or return NO_TRADE.

        Only these actions are permitted: {string.Join(", ", allowed)}.
        You may only name a contract from `candidates`. A symbol you invent is discarded.

        WHAT MAKES A GOOD PROPOSAL
        - A specific reason to expect a move, not a general feeling about the company.
        - `market_probability` is the option market's own view: risk-neutral, so slightly low,
          and well calibrated. To make money you must find where it is wrong, and you must be
          able to say why.
        - Long options decay. Buying a short-dated option and waiting loses money on average;
          a measured run of exactly that lost on nearly every trade.
        - `thesis_conditions` must be checkable later. "NVDA stays above 182" is checkable.
          "Sentiment stays positive" is not.

        NO_TRADE is a good answer when nothing is mispriced. Holding cash costs nothing.
        You state the size you want; the room's conviction and hard risk rules decide the
        final quantity.

        Call `{ProposeTool}` once, last.
        """;

    private string ReviewPrompt(IReadOnlyList<StrategyActionKind> allowed) =>
        $"""
        {Preamble}

        YOUR TASK
        An open position needs judging. Decide what to do with it and put that forward for
        the room to debate.

        Only these actions are permitted: {string.Join(", ", allowed)}.

        The question is: **is the original thesis still valid, and what changed?** Read the
        original thesis and its conditions, then the trigger that convened this review.

        Proposing no action means holding. That is a decision, and it needs a reason like any
        other. Do not close a position only because time has passed.

        Call `{ProposeTool}` once, last.
        """;

    private static string DescribeSearch(
        StrategyContext market, IReadOnlyList<StrategyActionKind> allowed) =>
        JsonSerializer.Serialize(new
        {
            now_utc = market.NowUtc,
            allowed_actions = allowed.Select(kind => kind.ToString()),
            account = new { equity = market.Account.Equity, cash = market.Account.Cash },
            remaining_position_slots = market.RemainingPositionSlots,
            new_positions_halted = market.NewPositionsHalted,
            policy = market.Policy,
            open_positions = market.Positions.Select(position => new
            {
                symbol = position.Symbol,
                quantity = position.Quantity,
                entry = position.AverageEntryPrice,
                current = position.CurrentPrice,
                unrealized = position.UnrealizedPnl,
            }),
            candidates = market.Candidates.Select(view => new
            {
                symbol = view.Candidate.ContractSymbol,
                underlying = view.Candidate.Underlying,
                type = view.Candidate.OptionType,
                strike = view.Candidate.Strike,
                expiration = view.Candidate.Expiration,
                underlying_price = view.UnderlyingPrice,
                cost_usd = view.CostPerContract,
                market_probability = view.MarketProbability,
                recent_news = view.RecentNewsCount,
            }),
            headlines = market.News.Take(25).Select(item => new
            {
                at = item.PublishedUtc,
                symbols = item.Symbols,
                headline = item.Headline,
            }),
            recent_outcomes = market.RecentOutcomes.Select(outcome => new
            {
                contract = outcome.ContractSymbol,
                pnl = outcome.RealizedPnl,
                why = outcome.Reasoning,
            }),
        }, Json);

    private static string DescribeReview(
        StrategyContext market,
        PositionUnderReview? position,
        IReadOnlyList<StrategyActionKind> allowed) =>
        JsonSerializer.Serialize(new
        {
            now_utc = market.NowUtc,
            allowed_actions = allowed.Select(kind => kind.ToString()),
            account_equity = market.Account.Equity,
            position = position is null ? null : new
            {
                symbol = position.Position.Symbol,
                quantity = position.Position.Quantity,
                entry = position.Position.AverageEntryPrice,
                current = position.Position.CurrentPrice,
                unrealized = position.Position.UnrealizedPnl,
                unrealized_fraction = position.UnrealizedPnlFraction,
                days_to_expiration = position.DaysToExpiration,
                why_reviewing = position.TriggerReason,
                original_thesis = position.OriginalThesis,
                original_thesis_conditions = position.OriginalThesisConditions,
            },
            headlines = market.News.Take(25).Select(item => new
            {
                at = item.PublishedUtc,
                symbols = item.Symbols,
                headline = item.Headline,
            }),
        }, Json);

    private static string DescribeRebuttal(RoomContext context) =>
        JsonSerializer.Serialize(new
        {
            proposal_id = context.ProposalId,
            your_proposal = new
            {
                thesis = context.Operation.Thesis,
                actions = context.Operation.Actions
                    .Where(action => action.Kind != StrategyActionKind.Hold)
                    .Select(action => new
                    {
                        action = action.Kind.ToString(),
                        contract_symbol = action.ContractSymbol,
                        contracts = action.Contracts,
                        your_probability = action.Probability,
                        your_reasoning = action.Reasoning,
                    }),
            },
            independent_analyses = context.Analyses
                .Where(analysis => analysis.Completed)
                .Select(analysis => new
                {
                    persona = analysis.Persona,
                    initial_vote = analysis.InitialVote.ToString(),
                    confidence = analysis.Confidence,
                    analysis = analysis.Analysis,
                    risks = analysis.Risks,
                }),
            discussion = context.Said
                .Where(contribution => contribution.Spoke)
                .Select(contribution => new
                {
                    speaker = contribution.Speaker,
                    round = contribution.Round,
                    summary = contribution.Summary,
                }),
            candidates = context.Market.Candidates.Select(view => new
            {
                symbol = view.Candidate.ContractSymbol,
                underlying = view.Candidate.Underlying,
                type = view.Candidate.OptionType,
                strike = view.Candidate.Strike,
                expiration = view.Candidate.Expiration,
                cost_usd = view.CostPerContract,
                market_probability = view.MarketProbability,
            }),
        }, Json);

    [Description("Your trade proposal, or NO_TRADE.")]
    public sealed class ProposalArguments
    {
        [Description("True when no trade is worth making. Leave actions empty.")]
        public bool? NoTrade { get; set; }

        [Description("Why, in a short statement. This is what the room debates.")]
        public string? Thesis { get; set; }

        [Description("What must stay true for the thesis to hold. Each must be checkable later.")]
        public List<string>? ThesisConditions { get; set; }

        [Description("The main ways this trade loses.")]
        public List<string>? MainRisks { get; set; }

        [Description("The operations to carry out. One trade at a time.")]
        public List<ProposalAction>? Actions { get; set; }
    }

    public sealed class ProposalAction
    {
        [Description("One of: open_call, open_put, close.")]
        public string? Action { get; set; }

        [Description("The exact contract symbol from candidates, or an open position to close.")]
        public string? ContractSymbol { get; set; }

        [Description("How many contracts you want. The room and the risk rules may reduce it.")]
        public int? Contracts { get; set; }

        [Description("Your honest probability from 0 to 1 that this finishes profitable.")]
        public decimal? Probability { get; set; }

        [Description("Why this contract specifically.")]
        public string? Reasoning { get; set; }
    }
}
