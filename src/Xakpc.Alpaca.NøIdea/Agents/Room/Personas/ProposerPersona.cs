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
    TradingOptions tradingOptions, IWarRoomAuditSink? audit = null)
    : LlmPersona(clients, logger, researchTools, audit), IProposingPersona
{
    private readonly TradingOptions _tradingOptions = tradingOptions
        ?? throw new ArgumentNullException(nameof(tradingOptions));
    private readonly ProposerTools _tools = new(logger, tradingOptions);

    public override string Name => "proposer";

    public override ModelProvider Provider => ModelProvider.Grok;

    protected override int MaxOutputTokens => 16000;

    /// <summary>The search phase is the long pole: 3:59 measured on 2026-09-01.</summary>
    protected override TimeSpan CallTimeout => TimeSpan.FromMinutes(9);

    protected override string RolePrompt =>
        """
        You are the PROPOSER in an autonomous options trading war room.

        Your job is to search the allowed market, compare plausible opportunities, and put forward
        the single best trade you can justify. You may also return NO_TRADE when no available
        contract has a sufficiently strong case.

        You are not a news screener. A trade does not require fresh company news or a scheduled
        catalyst. News is one source of evidence among many.

        Useful evidence can include:
        - price direction and reversal;
        - relative strength or weakness against SPY, QQQ, or related stocks;
        - broad market or sector movement;
        - company news and scheduled events;
        - macro events;
        - option price, strike, expiration, delta, implied volatility, and time decay;
        - unusual market behavior;
        - a mismatch between the expected stock move and the available option terms.

        Search first. Compare second. Propose last.

        C# has already removed contracts that are not mechanically permitted or cannot fit the hard
        account rules at one contract. Do not repeat those checks unless new market data makes them
        relevant.

        STATE THE NUMBERS YOU USED

        With your action, copy the bid, ask, underlying last price, delta, and implied volatility
        that you reasoned from, into the fields provided. Copy them from the candidate row. Do not
        estimate them and do not carry them over from a different contract.

        C# compares each value you supply with the catalog. A difference of more than one percent
        rejects the proposal before the room reads it. Omit a value you did not use.

        """;

    // ------------------------------------------------------------------ §15 propose

    public async Task<ProposedOperation> ProposeAsync(
        string proposalId,
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

        IReadOnlyList<AITool> tools = purpose == WarRoomPurpose.PositionReview
            ? [.. ResearchTools, proposalTool]
            : [.. ResearchTools, catalogueTool, proposalTool];

        var (fault, response) = await InvokeAsync(
            proposalId,
            purpose == WarRoomPurpose.PositionReview ? "review" : "search",
            purpose == WarRoomPurpose.PositionReview
                ? ReviewPrompt(allowedActions)
                : SearchPrompt(allowedActions),
            payload,
            tools,
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
            context.ProposalId,
            "rebuttal",
            $"""
            {Preamble}

            THE ROOM HAS ANSWERED

            Your proposal was analysed independently and then debated.

            Read the concrete objections and decide whether the original trade still has the strongest
            case.

            You may:

            - DEFEND:
              Resubmit the same operation unchanged when the criticisms do not materially weaken the
              thesis or contract choice.

            - MODIFY:
              Select one permitted nearby contract when a different strike or expiration expresses the
              same underlying thesis better. The modified proposal is treated as a new version and
              receives fresh validation and review.

            - WITHDRAW:
              Submit no action when the evidence no longer supports the trade.

            Set `decision` to `defend`, `modify`, or `withdraw`. For defend or modify, submit
            the active operation. For withdraw, leave actions empty.

            Evaluate arguments by evidence, not by vote count.

            Give more weight to:
            - new facts;
            - specific option or market data;
            - a concrete contradiction in the thesis;
            - evidence that the selected strike or expiration is poor;
            - evidence that the expected move is already reflected in price.

            Give less weight to:
            - generic statements that every trade can lose;
            - repeated versions of the same argument;
            - unsupported confidence.

            Do not defend a proposal only because activity is desirable.
            Do not withdraw a proposal only because it received criticism.

            If you modify the proposal, keep the same underlying thesis and choose only from
            `allowed_nearby_contracts`.

            Call `{ProposerTools.RebuttalToolName}` exactly once, last.
            """,
            DescribeRebuttal(context),
            [tool],
            ChatToolMode.RequireSpecific(ProposerTools.RebuttalToolName),
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

        Find the single best trade available now, or return NO_TRADE after a real search.

        Only these actions are permitted: {string.Join(", ", allowed)}.

        The contract catalog is authoritative. You may only submit a contract from that catalog.
        C# has already checked mechanical eligibility, current hard risk capacity, and basic quote
        validity.

        When `catalog_inline` is false, use `get_tradeable_contracts` to inspect contracts from the
        local in-memory catalog. Do not use Alpaca tools for contract discovery.

        SEARCH PROCESS

        1. Read the portfolio first.
           Consider current positions, pending orders, remaining risk, and free position slots.
           A new trade must make sense beside the positions that already exist.

        2. Read the compact market context.
           Use underlying price, 1-day return, 5-day return, and the headline index to identify
           plausible opportunities.

        3. Build a short list of the strongest underlyings.
           Do not require fresh company news.
           A valid thesis can come from price action, relative movement, market context, news,
           scheduled events, volatility, option terms, or another concrete market condition.

        4. Inspect actual tradeable contracts for the strongest ideas.
           When the catalog is not inline, call `get_tradeable_contracts`.
           Compare strike, expiration, premium, delta, implied volatility, and nearby choices.
           Do not choose a contract only because it is cheap.

        5. Use research tools when they can change the decision.
           Examples:
           - stock bars for direction or reversal;
           - stock or option snapshots for current state;
           - news for company-specific context;
           - SPY or QQQ for broader market context;
           - corporate actions or the calendar for known events;
           - web research when Alpaca data does not answer an important question.

           Do not call tools only to increase the amount of research.

        6. Compare up to three plausible finalists.
           Select the trade with the strongest case after considering:
           - why the underlying can move;
           - why the move can happen before expiration;
           - why this call or put is a sensible way to express the thesis;
           - premium at risk;
           - time decay;
           - implied volatility;
           - current portfolio exposure;
           - evidence against the trade.

        OPTION SELECTION

        The underlying thesis and the contract choice are separate decisions.

        A good stock thesis does not automatically make every option on that stock a good trade.

        Explain why the selected:
        - direction;
        - strike;
        - expiration;
        - contract

        fit the expected move better than the obvious nearby alternatives.

        Long-option time decay is a cost. It is not, by itself, a reason to reject every short-dated
        option.

        NEWS AND CATALYSTS

        Do not require earnings, M&A, FDA news, analyst news, or another named catalyst.

        A catalyst can strengthen a thesis, but absence of a catalyst does not prove NO_TRADE.

        Do not return NO_TRADE only because:
        - no company has earnings soon;
        - no fresh company headline exists;
        - a macro event can move the market in either direction;
        - options are short-dated;
        - time decay exists.

        NO_TRADE STANDARD

        NO_TRADE is a valid and useful result, but it must follow an actual comparison of available
        opportunities.

        Inspect actual contracts for two or three ideas when that many plausible ideas exist. Do
        not invent a candidate or continue research only to meet a quota.

        A valid NO_TRADE explanation must state:
        - which strongest opportunities you investigated;
        - why each failed;
        - why the remaining evidence does not justify paying option premium now.

        Do not invent precision. If evidence is uncertain, describe the uncertainty instead of
        producing a false exact probability.

        THESIS

        A trade proposal must contain:
        - one clear reason for the expected move;
        - why the timing fits the option expiration;
        - why the selected contract fits the thesis;
        - the strongest risk to the thesis;
        - checkable `thesis_conditions`.

        Good condition:
        "NVDA remains above 218 after the first trading hour."

        Bad condition:
        "Market sentiment remains positive."

        You state the size you want. The room and deterministic C# risk rules have final authority
        over execution and quantity.

        Record the selected and rejected finalists in `alternatives_considered`.

        Call `{ProposerTools.ProposeToolName}` exactly once, last.
        """;

    private string ReviewPrompt(IReadOnlyList<StrategyActionKind> allowed) =>
        $"""
        {Preamble}

        YOUR TASK

        An open position needs a new decision.

        Only these actions are permitted: {string.Join(", ", allowed)}.

        Compare closing now with continuing to hold under the existing exit policy. Determine
        whether the original thesis still holds after the change that triggered this review.

        Start with:
        - the original thesis;
        - the original thesis conditions;
        - the review trigger;
        - current P&L and time to expiration.

        Use research tools when current market, option, or news data can change the answer.

        Decide whether the best action is to hold, close, or use another allowed action.

        Do not close only because:
        - the position is currently losing;
        - the position is currently profitable;
        - time has passed;
        - time decay exists.

        Do not hold only because the original thesis once looked good.

        The entry premium is a sunk cost. Judge expected value from this moment forward. Leave
        `profit_probability` null for a close action.

        Ask:
        - What changed since entry or the previous review?
        - Is the original reason for owning this position still true?
        - Has contrary evidence become stronger?
        - Is the remaining possible reward worth the remaining premium risk and time?

        Proposing no action means HOLD. HOLD requires a concrete reason.

        Call `{ProposerTools.ProposeToolName}` exactly once, last.
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
                        your_profit_probability = action.ProfitProbability,
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
