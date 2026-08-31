using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ToonFormat;
using Xakpc.Alpaca.NøIdea.Agents.Tools;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Agents.Room.Personas;

/// <summary>
/// The seat that opens the room: it searches the allowed universe and puts one operation
/// forward, or returns NO_TRADE.
/// </summary>
/// <remarks>
/// <para>
/// It carries the full read-only Alpaca toolset because it is the only seat that has to look
/// at the market rather than react to somebody else's reading of it.
/// </para>
/// <para>
/// <b>It does not get final authority over quantity</b> (spec §16). It states the size it
/// wants; the room's conviction scales it and <c>RiskGuard</c> caps it.
/// </para>
/// </remarks>
public sealed class ProposerPersona(
    ChatClientFactory clients, ILogger logger, IReadOnlyList<AITool> researchTools,
    TradingOptions tradingOptions)
    : LlmPersona(clients, logger, researchTools), IProposingPersona
{
    private readonly TradingOptions _tradingOptions = tradingOptions
        ?? throw new ArgumentNullException(nameof(tradingOptions));
    private readonly ProposerTools _tools = new(logger, tradingOptions);

    public override string Name => "proposer";

    public override ModelProvider Provider => ModelProvider.Anthropic;

    protected override string Model => "claude-sonnet-5";

    /// <summary>Claude Sonnet 5 takes no temperature. Sending one is a 400.</summary>
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

        var offered = market.ContractCatalog
            .Select(view => view.Contract.ContractSymbol)
            .ToHashSet(StringComparer.Ordinal);

        var held = market.Positions
            .Select(item => item.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        var proposalTool = _tools.CreateProposalTool(
            offered, held, allowedActions, operation => captured = operation);

        var catalogueTool = _tools.CreateCatalogTool(market);

        var payload = purpose == WarRoomPurpose.PositionReview
            ? DescribeReview(market, position, allowedActions)
            : DescribeSearch(market, allowedActions);

        var (fault, response) = await InvokeAsync(
            purpose == WarRoomPurpose.PositionReview ? "review" : "search",
            purpose == WarRoomPurpose.PositionReview
                ? ReviewPrompt(allowedActions)
                : SearchPrompt(allowedActions),
            payload,
            [.. ResearchTools, catalogueTool, proposalTool],
            ChatToolMode.Auto,
            MaxOutputTokens,
            cancellationToken);

        if (fault is not null)
        {
            Logger.LogError("The proposer failed: {Fault}. Treating as NO_TRADE.", fault);
            return ProposedOperation.Nothing("the proposer failed");
        }

        if (captured is null)
        {
            // A call that ends without the tool costs the same as one that uses it, and
            // reads downstream as a considered NO_TRADE. Say why instead: a length finish
            // means the budget ran out mid-search, and the text is what it said in place of
            // the proposal.
            Logger.LogWarning(
                "The proposer ended its turn without calling {Tool}. Finish reason {Reason}. "
                + "It said: {Text}",
                ProposerTools.ProposeToolName,
                response?.FinishReason?.ToString() ?? "none reported",
                Truncate(response?.Text));
        }

        return captured ?? ProposedOperation.Nothing("the proposer submitted nothing");
    }

    // ------------------------------------------------------------------ §20 rebuttal

    public async Task<ProposedOperation> RebutAsync(
        RoomContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ProposedOperation? captured = null;

        var offered = ReviewContextSelector.NearbyContracts(context.Market, context.Operation)
            .Select(view => view.Contract.ContractSymbol)
            .ToHashSet(StringComparer.Ordinal);

        var held = context.Market.Positions
            .Select(item => item.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        var tool = _tools.CreateRebuttalTool(
            offered, held, context.AllowedActions, operation => captured = operation);

        var (fault, _) = await InvokeAsync(
            "rebuttal",
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

            Call `{ProposerTools.RebuttalToolName}` once, last.
            """,
            DescribeRebuttal(context),
            [tool],
            ChatToolMode.Auto,
            2500,
            cancellationToken);

        if (fault is not null)
        {
            // The original stands. A broken rebuttal must not silently withdraw a proposal.
            return context.Operation;
        }

        return captured ?? context.Operation;
    }

    private static string Truncate(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(nothing)"
        : text.Length <= 400 ? text
        : text[..400] + "…";

    // ------------------------------------------------------------------ shaping

    private string SearchPrompt(IReadOnlyList<StrategyActionKind> allowed) =>
        $"""
        {Preamble}

        YOUR TASK
        Find the single best trade available right now among the candidates you are given,
        or return NO_TRADE.

        Only these actions are permitted: {string.Join(", ", allowed)}.
        You may only name a contract from the authoritative `contracts` catalog. A symbol
        you invent is discarded. Use `get_tradeable_contracts` to query the in-memory catalog
        when the prompt contains only its index. Do not use Alpaca for contract discovery.

        WHAT MAKES A GOOD PROPOSAL
        - A specific reason to expect a move, not a general feeling about the company.
        - Long options decay. Buying a short-dated option and waiting loses money on average;
          a measured run of exactly that lost on nearly every trade.
        - `thesis_conditions` must be checkable later. "NVDA stays above 182" is checkable.
          "Sentiment stays positive" is not.

        NO_TRADE is a good answer when nothing is mispriced. Holding cash costs nothing.
        You state the size you want; the room's conviction and hard risk rules decide the
        final quantity.

        Call `{ProposerTools.ProposeToolName}` once, last.
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

        Call `{ProposerTools.ProposeToolName}` once, last.
        """;

    private string DescribeSearch(
        StrategyContext market, IReadOnlyList<StrategyActionKind> allowed)
    {
        var rows = _tools.ContractRows(market.ContractCatalog);
        var inline = Toon.Encode(rows).Length <= _tradingOptions.InlineCatalogCharacterLimit;

        return Toon.Encode(new
        {
            now_utc = market.NowUtc,
            allowed_actions = allowed.Select(kind => kind.ToString()),
            account = new { equity = market.Account.Equity, cash = market.Account.Cash },
            portfolio_capacity = new
            {
                remaining_risk = market.Capacity.RemainingRisk,
                free_position_slots = market.Capacity.FreePositionSlots,
                pending_risk_known = market.Capacity.PendingRiskKnown,
            },
            new_positions_halted = market.NewPositionsHalted,
            constraints = market.Constraints,
            allowed_tickers = _tradingOptions.TrackedSymbols,
            underlying_snapshots = market.Underlyings.Select(snapshot => new
            {
                symbol = snapshot.Symbol,
                last = snapshot.Last,
                last_at = snapshot.LastAt,
                return_1d = snapshot.Return1D,
                return_5d = snapshot.Return5D,
            }),
            open_positions = market.PortfolioPositions.Select(view => new
            {
                symbol = view.Position.Symbol,
                underlying = view.Underlying,
                type = view.OptionType,
                strike = view.Strike,
                expiration = view.Expiration,
                quantity = view.Position.Quantity,
                entry = view.Position.AverageEntryPrice,
                current = view.Position.CurrentPrice,
                unrealized = view.Position.UnrealizedPnl,
                unrealized_fraction = view.UnrealizedPnlFraction,
                premium_risk = view.PremiumRisk,
                original_thesis = view.OriginalThesis,
                original_thesis_conditions = view.OriginalThesisConditions,
            }),
            pending_orders = market.PendingOrders.Select(order => new
            {
                symbol = order.ContractSymbol,
                side = order.IsBuy ? "buy" : "sell",
                requested = order.RequestedQuantity,
                filled = order.FilledQuantity,
                remaining = order.RemainingQuantity,
                limit = order.LimitPrice,
                status = order.RawStatus,
            }),
            catalog_inline = inline,
            contracts = inline ? rows : null,
            catalog_index = inline ? null : _tools.CatalogIndex(market.ContractCatalog),
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
        });
    }

    private static string DescribeReview(
        StrategyContext market,
        PositionUnderReview? position,
        IReadOnlyList<StrategyActionKind> allowed) =>
        Toon.Encode(new
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
        });

    private static string DescribeRebuttal(RoomContext context) =>
        Toon.Encode(new
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
            allowed_nearby_contracts = ReviewContextSelector
                .NearbyContracts(context.Market, context.Operation)
                .Select(view => new
            {
                symbol = view.Contract.ContractSymbol,
                underlying = view.Contract.Underlying,
                type = view.Contract.OptionType,
                strike = view.Contract.Strike,
                expiration = view.Contract.Expiration,
                bid = view.Contract.Bid,
                ask = view.Contract.Ask,
                delta = view.Contract.Delta,
                implied_volatility = view.Contract.ImpliedVolatility,
            }),
        });

}
