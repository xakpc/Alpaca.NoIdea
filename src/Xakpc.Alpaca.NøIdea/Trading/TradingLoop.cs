using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Observability;
using Xakpc.Alpaca.NøIdea.Storage;

namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>What one cycle did, for the log and the tests.</summary>
public sealed record CycleResult
{
    public required DateTimeOffset AtUtc { get; init; }
    public int CandidatesOffered { get; init; }
    public int PositionsClosed { get; init; }
    public int CloseOrdersSubmitted { get; init; }
    public int OrdersSubmitted { get; init; }
    public int ActionsRejected { get; init; }
    public bool PolicyRevised { get; init; }
    public decimal Equity { get; init; }
}

/// <summary>
/// One pass of the live-data trading cycle.
/// </summary>
/// <remarks>
/// <para>
/// The order of the steps is the safety property. Deterministic exits run <em>before</em> the
/// agent is asked anything, so a hung or misbehaving agent cannot stop a stop-loss. The risk
/// guard runs <em>after</em> the agent, on every action, so nothing the agent returns can
/// exceed a hard limit. The agent sits in the middle and only ever produces data.
/// </para>
/// <para>
/// A dry run uses the same market-data path and replaces only the trading gateway.
/// </para>
/// </remarks>
public sealed class TradingLoop(
    IMarketDataGateway marketData,
    ITradingGateway trading,
    IStrategyAgent agent,
    RiskGuard riskGuard,
    RiskOptions riskOptions,
    TradingOptions tradingOptions,
    TradingStore store,
    TimeProvider time,
    ILogger logger)
{
    private readonly IMarketDataGateway _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
    private readonly ITradingGateway _trading = trading ?? throw new ArgumentNullException(nameof(trading));
    private readonly IStrategyAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    private readonly RiskGuard _riskGuard = riskGuard ?? throw new ArgumentNullException(nameof(riskGuard));
    private readonly RiskOptions _riskOptions = riskOptions ?? throw new ArgumentNullException(nameof(riskOptions));
    private readonly TradingOptions _tradingOptions = tradingOptions ?? throw new ArgumentNullException(nameof(tradingOptions));
    private readonly TradingStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// How many refused operations the room is reminded of. Enough to stop it re-proposing
    /// the same thesis for the rest of a session, short enough not to crowd the catalog.
    /// </summary>
    private const int RecentRejectionLimit = 5;

    /// <summary>Today in UTC. The date the expiration rules count from.</summary>
    private DateOnly Today => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    /// <summary>True when a decorator is intercepting orders, so nothing reaches the broker.</summary>
    public bool DryRun { get; init; }

    private readonly IPositionReviewer? _reviewer = agent as IPositionReviewer;

    /// <summary>Null when no reviewer is present, which leaves the deterministic exits alone.</summary>
    private readonly PositionReviewTriggers? _triggers =
        agent is IPositionReviewer ? new PositionReviewTriggers(new ReviewTriggerOptions(), time) : null;

    /// <summary>
    /// Serialises every region that reads positions or orders and then acts on what it read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hard-exit loop runs on its own timer, so two callers can reach the broker at once.
    /// The race that matters is check-then-mutate: two paths both see no pending close for one
    /// symbol and both send a sell. Holding this gate across the read and the submission makes
    /// that impossible.
    /// </para>
    /// <para>
    /// <b>The war room never holds this gate.</b> A sitting takes 8 to 10 minutes and produces
    /// only data. Gating it would put the stop-loss back behind a model answering, which is the
    /// exact fault this design removes.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _brokerGate = new(1, 1);

    private StrategyPolicy _policy = new();
    private OrderCoordinator? _orderCoordinator;
    private bool _initialized;
    private bool _startupOrdersReconciled;
    private DateOnly? _fallbackBaselineDate;
    private decimal? _fallbackDayOpeningEquity;

    /// <summary>
    /// The policy in force. The agent revises it; the setter clamps it.
    /// </summary>
    /// <remarks>
    /// Clamping in the setter rather than at the call site means there is no path -- not the
    /// agent, not a caller seeding a run -- that can install a policy outside
    /// <see cref="RiskOptions"/>.
    /// </remarks>
    public StrategyPolicy Policy
    {
        get => _policy;
        init => _policy = (value ?? throw new ArgumentNullException(nameof(value)))
            .ClampTo(riskOptions, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
    }

    /// <summary>The symbols the loop looks at each cycle.</summary>
    public IReadOnlyList<string> TrackedSymbols => _tradingOptions.TrackedSymbols;

    /// <summary>The mode string written to the audit trail: <c>live</c> or <c>dry-run</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>Loads durable runtime state and reconciles outstanding orders once.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            if (await _store.LoadPolicyAsync(Mode, cancellationToken) is { } policy)
            {
                _policy = policy.ClampTo(_riskOptions, Today);
            }
            if (_triggers is not null)
            {
                foreach (var state in await _store.LoadPositionReviewStateAsync(Mode, cancellationToken))
                {
                    _triggers.Restore(
                        state.OptionSymbol, state.LastReviewedUtc, state.LastNewsSeen);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            throw new AuditPersistenceException("Could not restore durable trading state.", error);
        }

        _orderCoordinator = new OrderCoordinator(
            _trading, _store, _time, _logger, Mode);
        await _orderCoordinator.ReconcileAndListPendingAsync(
            replayMissingSells: true, cancellationToken);
        _startupOrdersReconciled = true;
        _initialized = true;
    }

    /// <summary>
    /// Runs the deterministic exits alone, without the catalog, the room, or any model call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the same work as step 2 of <see cref="RunCycleAsync"/> and it calls the same
    /// <c>ManageOpenPositionsAsync</c>. It exists so the exits can run on a one-minute timer
    /// while a cycle is somewhere in the middle of an eight-minute sitting. A stop-loss checked
    /// once every 38 to 41 minutes is a poll, not a stop.
    /// </para>
    /// <para>
    /// A pass is two Alpaca reads. <c>MandatoryExitReason</c> is a pure function and the price
    /// it judges comes from the position payload, so no option chain is read and no token is
    /// spent.
    /// </para>
    /// <para>
    /// It does not call <see cref="InitializeAsync"/>. That method guards itself with a plain
    /// boolean, which is not safe to enter from two threads, so the session initialises once
    /// before it starts this loop. An uninitialised loop reports no work rather than racing.
    /// </para>
    /// </remarks>
    public async Task<CloseBatchResult> RunHardExitsAsync(CancellationToken cancellationToken)
    {
        if (_orderCoordinator is not { } coordinator)
        {
            return new CloseBatchResult();
        }

        await _brokerGate.WaitAsync(cancellationToken);

        try
        {
            var positions = await _trading.ListPositionsAsync(cancellationToken);
            if (positions.Count == 0)
            {
                return new CloseBatchResult();
            }

            var pendingOrders = await coordinator.ReconcileAndListPendingAsync(
                replayMissingSells: false, cancellationToken);

            return await ManageOpenPositionsAsync(positions, pendingOrders, cancellationToken);
        }
        finally
        {
            _brokerGate.Release();
        }
    }

    public async Task<CycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var todayEastern = MarketCalendar.ToEastern(now).Date;
        var dayStartUtc = MarketCalendar.ToUtc(todayEastern);

        AccountState account;
        IReadOnlyList<PositionState> positions;
        IReadOnlyList<OrderState> pendingOrders;
        IReadOnlyList<OrderState> dailyOrders;
        CloseBatchResult mandatory;

        // Steps 1 and 2 read positions and orders and then act on what they read, so the
        // hard-exit loop must not interleave with them. The gate ends before the catalog is
        // built: everything after it is either a read or the room, and the room must never
        // hold a gate that a stop-loss waits on.
        await _brokerGate.WaitAsync(cancellationToken);

        try
        {
            // 1. Account state. Alpaca is the source of truth, never SQLite.
            account = await _trading.GetAccountAsync(cancellationToken);
            positions = await _trading.ListPositionsAsync(cancellationToken);
            pendingOrders = await _orderCoordinator!.ReconcileAndListPendingAsync(
                replayMissingSells: !_startupOrdersReconciled, cancellationToken);
            _startupOrdersReconciled = false;
            dailyOrders = await _trading.ListOrdersSinceAsync(dayStartUtc, cancellationToken);

            // 2. Deterministic exits, BEFORE the agent is consulted. A stop-loss must not depend
            //    on a model answering. The hard-exit loop runs the same method on its own timer.
            mandatory = await ManageOpenPositionsAsync(
                positions, pendingOrders, cancellationToken);

            if (mandatory.AttemptedSymbols.Count > 0)
            {
                positions = await _trading.ListPositionsAsync(cancellationToken);
                pendingOrders = await _orderCoordinator.ReconcileAndListPendingAsync(
                    replayMissingSells: false, cancellationToken);
                account = await _trading.GetAccountAsync(cancellationToken);
            }
        }
        finally
        {
            _brokerGate.Release();
        }

        var openedToday = dailyOrders
            .Where(order => order.IsBuy && order.FilledQuantity > 0)
            .Select(order => string.IsNullOrWhiteSpace(order.ClientOrderId)
                ? order.BrokerOrderId ?? order.ContractSymbol
                : order.ClientOrderId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var closed = mandatory.ConfirmedClosed;
        var closeSubmitted = mandatory.Submitted;
        var rejected = mandatory.Rejected;

        if (account.IsTradingBlocked || account.IsAccountBlocked
            || account.OptionsTradingLevel is null or <= 0)
        {
            var reason = account.IsTradingBlocked || account.IsAccountBlocked
                ? "trading is blocked on the account"
                : "options trading is disabled on the account";
            _logger.LogError("{Reason}. Mandatory exits were handled; skipping model work.", reason);
            await RecordDecisionAsync(
                StrategyAction.Hold(reason), null,
                "cycle", "held", "account restricted", reason, cancellationToken);
            await RecordEquityAsync(account, cancellationToken);
            return new CycleResult
            {
                AtUtc = now,
                PositionsClosed = closed,
                CloseOrdersSubmitted = closeSubmitted,
                ActionsRejected = rejected,
                Equity = account.Equity,
            };
        }

        // 2b. War-room position review (spec §10, §11). Runs only where a trigger fired and
        //     only after the hard exits, so a stop-loss never waits on a model answering.
        if (_reviewer is not null && _triggers is not null)
        {
            var reviewed = await ReviewPositionsAsync(
                positions, pendingOrders, mandatory.AttemptedSymbols, account, cancellationToken);
            closed += reviewed.ConfirmedClosed;
            closeSubmitted += reviewed.Submitted;
            rejected += reviewed.Rejected;
            if (reviewed.AttemptedSymbols.Count > 0)
            {
                positions = await _trading.ListPositionsAsync(cancellationToken);
                pendingOrders = await _orderCoordinator.ReconcileAndListPendingAsync(
                    replayMissingSells: false, cancellationToken);
                account = await _trading.GetAccountAsync(cancellationToken);
            }
        }

        var snapshot = BuildRiskSnapshot(
            DateOnly.FromDateTime(todayEastern), account, positions, pendingOrders, dailyOrders,
            openedToday);

        var newPositionVerdict = _riskGuard.CanConsiderNewPositions(snapshot);
        var halted = !newPositionVerdict.Allowed;

        if (halted)
        {
            _logger.LogWarning(
                "New positions halted before catalog build: {Reason}. "
                + "Equity {Equity:N2}, day baseline {Baseline:N2}, pending risk known {PendingRiskKnown}.",
                newPositionVerdict.Reason, snapshot.Equity, snapshot.DayOpeningEquity,
                snapshot.PendingRiskKnown);
        }

        // 3. Mechanical filter. Every row is executable under current hard constraints.
        //    C# does not decide if its probability, premium, or thesis is attractive.
        var catalogResult = halted
            ? CatalogBuildResult.Empty
            : await BuildTradeableCatalogAsync(positions, pendingOrders, snapshot, cancellationToken);
        var catalog = catalogResult.Contracts;

        _logger.LogInformation(
            RunEvents.AccountRead,
            "Equity {Equity:N2} USD, cash {Cash:N2}, {Positions} position(s).",
            account.Equity, account.Cash, positions.Count);

        _logger.LogInformation(
            RunEvents.CandidatesBuilt,
            "{Offered} candidate(s) from {Scanned} symbol(s).",
            catalog.Count, TrackedSymbols.Count);

        // An empty catalog ends the cycle before the room sits, so the tally of which gate
        // removed what is the only evidence of why. It is run narrative, not diagnostics.
        if (CatalogBuildResult.Tally(catalogResult.Dropped) is { } drops)
        {
            _logger.LogInformation(
                RunEvents.CatalogFiltered,
                "Dropped {Dropped} of {Examined} contract(s): {Reasons}.",
                catalogResult.DroppedCount, catalogResult.Examined, drops);
        }

        if (CatalogBuildResult.Tally(catalogResult.SkippedSymbols) is { } skips)
        {
            _logger.LogInformation(
                RunEvents.CatalogFiltered,
                "Skipped {Skipped} symbol(s) before any contract: {Reasons}.",
                catalogResult.SkippedSymbols.Values.Sum(), skips);
        }

        var news = HeadlineIndexSelector.Select(
            await SafeNewsAsync(cancellationToken),
            TrackedSymbols,
            _tradingOptions.HeadlineLimit,
            _tradingOptions.MaxHeadlinesPerSymbol);
        var freeSlots = Math.Max(
            0, _riskOptions.MaxConcurrentPositions
               - snapshot.OpenPositions - snapshot.PendingOpenPositions);
        var remainingRisk = Math.Max(
            0m, account.Equity * _riskOptions.MaxTotalRiskFraction
                - snapshot.OpenPositionCost - snapshot.PendingOrderCost);

        // 4. The agent decides. It receives data and returns data.
        var context = new StrategyContext
        {
            NowUtc = now,
            Account = account,
            Positions = positions,
            ContractCatalog = catalog,
            Policy = Policy,
            Underlyings = catalogResult.Underlyings,
            PortfolioPositions = await BuildPortfolioPositionsAsync(positions, cancellationToken),
            PendingOrders = pendingOrders,
            Capacity = new PortfolioCapacity(remainingRisk, freeSlots, snapshot.PendingRiskKnown),
            Constraints = BuildConstraints(account, now),
            News = news,
            RecentOutcomes = [],
            RecentRejections = await _store.RecentRejectionsAsync(
                RecentRejectionLimit, cancellationToken),
            RemainingPositionSlots = freeSlots,
            NewPositionsHalted = halted,
            NewPositionsHaltReason = halted ? newPositionVerdict.Reason : null,
        };

        StrategyDecision decision;
        try
        {
            decision = await _agent.DecideAsync(context, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            // Fail closed: an agent fault skips the cycle. It never opens a position.
            _logger.LogError(error, "The agent failed. Skipping new positions this cycle.");
            await RecordDecisionAsync(
                StrategyAction.Hold($"the agent failed: {error.Message}"), null,
                "new-trade", "held", "agent failure", null, cancellationToken);
            return new CycleResult
            {
                AtUtc = now,
                CandidatesOffered = catalog.Count,
                PositionsClosed = closed,
                Equity = account.Equity,
            };
        }

        // 5. A revised policy is clamped before anything reads it.
        var revised = false;
        if (decision.RevisedPolicy is { } proposed)
        {
            var clamped = proposed.ClampTo(_riskOptions, Today);
            revised = clamped.DiffersFrom(Policy);

            if (revised)
            {
                _logger.LogInformation(
                    "Policy revised: DTE {MinDte}-{MaxDte}, take {Take:P0}, stop {Stop:P0}. {Rationale}",
                    clamped.MinDaysToExpiration, clamped.MaxDaysToExpiration,
                    clamped.TakeProfitFraction, clamped.StopLossFraction, clamped.Rationale);
            }

            try
            {
                await _store.SavePolicyAsync(Mode, clamped, _time.GetUtcNow(), cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException
                                          and not AuditPersistenceException)
            {
                throw new AuditPersistenceException("Could not store the active strategy policy.", error);
            }
            _policy = clamped;
        }

        // 6. Every action through the risk guard, then execute.
        var submitted = 0;

        foreach (var action in decision.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (action.Kind)
            {
                case StrategyActionKind.Hold when decision.Rejection is { } rejection:
                    // A contract was judged and declined. That is a rejection, not a quiet
                    // cycle, and it must reach the count and the audit with the symbol it was
                    // judged on.
                    rejected++;
                    _logger.LogInformation(
                        RunEvents.ProposalRejectedEarly,
                        "{Stage} rejected {Symbol}: {Code}. {Why}",
                        rejection.Stage, action.ContractSymbol ?? "the proposal",
                        rejection.Code, action.Reasoning);
                    await RecordDecisionAsync(
                        action, null, "new-trade", "rejected", rejection.Stage, rejection.Code,
                        cancellationToken);
                    break;

                case StrategyActionKind.Hold:
                    // Say why nothing happened. The war room short-circuits before it ever
                    // sits — halted, no free slot, no candidate — and without this the run
                    // shows a cycle that does nothing and explains nothing, which reads as a
                    // broken agent rather than a working one with nothing to do.
                    _logger.LogInformation(RunEvents.Hold, "{Why}", action.Reasoning);
                    await RecordDecisionAsync(
                        action, null, "new-trade", "held", "hold", null, cancellationToken);
                    break;

                case StrategyActionKind.ClosePosition:
                    if (await TryCloseAsync(action, cancellationToken) is { } closeResult)
                    {
                        closeSubmitted += closeResult.Submitted;
                        closed += closeResult.ConfirmedClosed;
                        rejected += closeResult.Rejected;
                    }

                    break;

                case StrategyActionKind.OpenCall:
                case StrategyActionKind.OpenPut:
                    if (await TryOpenAsync(action, catalog, cancellationToken))
                    {
                        submitted++;
                        snapshot = snapshot with { OpenPositions = snapshot.OpenPositions + 1 };
                    }
                    else
                    {
                        rejected++;
                    }

                    break;
            }
        }

        var finalAccount = await _trading.GetAccountAsync(cancellationToken);
        await RecordEquityAsync(finalAccount, cancellationToken);

        return new CycleResult
        {
            AtUtc = now,
            CandidatesOffered = catalog.Count,
            PositionsClosed = closed,
            CloseOrdersSubmitted = closeSubmitted,
            OrdersSubmitted = submitted,
            ActionsRejected = rejected,
            PolicyRevised = revised,
            Equity = finalAccount.Equity,
        };
    }

    // ------------------------------------------------------------------ exits

    /// <summary>
    /// Builds the risk view that <see cref="RiskGuard"/> judges an opening against.
    /// </summary>
    /// <remarks>
    /// Extracted so the cycle can build it twice: once before the room sits, and once inside
    /// the broker gate immediately before a submission. A sitting takes 8 to 10 minutes and a
    /// hard exit can close a position while it runs, so the first snapshot is evidence for the
    /// room and only the second one may authorise money.
    /// </remarks>
    private RiskSnapshot BuildRiskSnapshot(
        DateOnly tradingDay,
        AccountState account,
        IReadOnlyList<PositionState> positions,
        IReadOnlyList<OrderState> pendingOrders,
        IReadOnlyList<OrderState> dailyOrders,
        int openedToday) => new()
        {
            Equity = account.Equity,
            Cash = account.Cash,
            DayOpeningEquity = ResolveDayOpeningEquity(tradingDay, account, positions, dailyOrders),
            OpenPositions = positions.Count,
            OpenPositionCost = positions.Sum(p => p.AverageEntryPrice * Math.Abs(p.Quantity) * 100m),
            PositionsOpenedToday = openedToday,
            PendingOpenPositions = pendingOrders
                .Where(order => order.IsBuy && order.RemainingQuantity > 0)
                .Select(order => order.ContractSymbol)
                .Except(positions.Select(position => position.Symbol), StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            PendingOrderCost = pendingOrders
                .Where(order => order.IsBuy)
                .Sum(order => order.RemainingNotional ?? 0m),
            PendingRiskKnown = pendingOrders
                .Where(order => order.IsBuy && order.RemainingQuantity > 0)
                .All(order => order.RemainingNotional is not null),
        };

    /// <summary>
    /// Reads the account again and rebuilds the risk view. Call inside the broker gate.
    /// </summary>
    private async Task<RiskSnapshot> RefreshRiskSnapshotAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var todayEastern = MarketCalendar.ToEastern(now).Date;
        var account = await _trading.GetAccountAsync(cancellationToken);
        var positions = await _trading.ListPositionsAsync(cancellationToken);
        var pendingOrders = await _orderCoordinator!.ReconcileAndListPendingAsync(
            replayMissingSells: false, cancellationToken);
        var dailyOrders = await _trading.ListOrdersSinceAsync(
            MarketCalendar.ToUtc(todayEastern), cancellationToken);
        var openedToday = dailyOrders
            .Where(order => order.IsBuy && order.FilledQuantity > 0)
            .Select(order => string.IsNullOrWhiteSpace(order.ClientOrderId)
                ? order.BrokerOrderId ?? order.ContractSymbol
                : order.ClientOrderId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return BuildRiskSnapshot(
            DateOnly.FromDateTime(todayEastern), account, positions, pendingOrders, dailyOrders,
            openedToday);
    }

    private async Task<CloseBatchResult> ManageOpenPositionsAsync(
        IReadOnlyList<PositionState> positions,
        IReadOnlyList<OrderState> pendingOrders,
        CancellationToken cancellationToken)
    {
        var result = new CloseBatchResult();

        foreach (var position in positions)
        {
            if (HasPendingClose(position.Symbol, pendingOrders))
            {
                _logger.LogInformation(
                    "A close for {Symbol} is already pending. No duplicate close was sent.",
                    position.Symbol);
                continue;
            }

            var reason = _riskGuard.MandatoryExitReason(position, Policy, position.CurrentPrice);
            if (reason is null)
            {
                continue;
            }

            _logger.LogInformation(
                RunEvents.PositionClosed,
                "{State} {Symbol}: {Reason}.",
                DryRun ? "Would close (dry run)" : "Closing", position.Symbol, reason);

            try
            {
                var submitted = await CloseWithAuditAsync(
                    new StrategyAction
                    {
                        Kind = StrategyActionKind.ClosePosition,
                        ContractSymbol = position.Symbol,
                        Contracts = Math.Abs(position.Quantity),
                        Reasoning = reason,
                    },
                    position,
                    "mandatory-exit",
                    reason,
                    cancellationToken);
                result = result.Add(position.Symbol, submitted.Order);
            }
            catch (AuditPersistenceException)
            {
                throw;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A failed close is not fatal to the cycle; the next cycle tries again.
                _logger.LogError(error, "Could not close {Symbol}.", position.Symbol);
            }
        }

        return result;
    }

    /// <remarks>
    /// The room decided to close this position minutes ago, and a hard exit may have closed it
    /// in the meantime. Positions and pending orders are therefore read again inside the broker
    /// gate: the caller's lists are evidence the room saw, not a basis for sending an order.
    /// </remarks>
    private async Task<CloseBatchResult?> TryCloseAsync(
        StrategyAction action,
        CancellationToken cancellationToken)
    {
        if (action.ContractSymbol is not { } symbol)
        {
            _logger.LogWarning("The agent asked to close nothing. Ignored.");
            await RecordDecisionAsync(
                action, null, "position-review", "rejected", "position", "not open",
                cancellationToken);
            return new CloseBatchResult { Rejected = 1 };
        }

        await _brokerGate.WaitAsync(cancellationToken);

        try
        {
            var current = await _trading.ListPositionsAsync(cancellationToken);
            if (current.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.Ordinal))
                is not { } position)
            {
                _logger.LogWarning(
                    "The agent asked to close {Symbol}, which is not an open position. Ignored.",
                    symbol);
                await RecordDecisionAsync(
                    action, null, "position-review", "rejected", "position", "not open",
                    cancellationToken);
                return new CloseBatchResult { Rejected = 1 };
            }

            var currentPending = await _orderCoordinator!.ReconcileAndListPendingAsync(
                replayMissingSells: false, cancellationToken);

            if (HasPendingClose(symbol, currentPending))
            {
                await RecordPositionDecisionAsync(
                    action, position, "position-review", "held", "close pending",
                    "a sell order is already pending", cancellationToken);
                return new CloseBatchResult();
            }

            _logger.LogInformation(
                "Closing {Symbol} on the agent's request: {Why}", symbol, action.Reasoning);
            var submitted = await CloseWithAuditAsync(
                action, position, "position-review", action.Reasoning, cancellationToken);
            return new CloseBatchResult().Add(symbol, submitted.Order);
        }
        finally
        {
            _brokerGate.Release();
        }
    }


    /// <summary>
    /// Sends positions that a trigger picked out to the war room. Spec §10 and §11.
    /// </summary>
    /// <remarks>
    /// A review asks whether the thesis still holds. It does not close anything by itself:
    /// the room proposes, the room votes, and only an approved close reaches the broker. A
    /// position that fails to review is simply left alone, still guarded by the hard exits.
    /// </remarks>
    private async Task<CloseBatchResult> ReviewPositionsAsync(
        IReadOnlyList<PositionState> positions,
        IReadOnlyList<OrderState> pendingOrders,
        IReadOnlySet<string> excludedSymbols,
        AccountState account,
        CancellationToken cancellationToken)
    {
        var result = new CloseBatchResult();
        var now = _time.GetUtcNow();
        var portfolio = await BuildPortfolioPositionsAsync(positions, cancellationToken);
        IReadOnlyList<NewsItem>? headlineIndex = null;
        var underlyingSnapshots = new Dictionary<string, UnderlyingSnapshot>(StringComparer.Ordinal);

        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (excludedSymbols.Contains(position.Symbol)
                || HasPendingClose(position.Symbol, pendingOrders))
            {
                continue;
            }

            int? daysToExpiration = OccOptionSymbol.TryParse(position.Symbol, out var parsed)
                ? parsed.Expiration.DayNumber - DateOnly.FromDateTime(now.UtcDateTime).DayNumber
                : null;

            var underlying = parsed.Underlying;
            var newsCount = string.IsNullOrEmpty(underlying)
                ? 0
                : await CountRecentNewsAsync(underlying, now, cancellationToken);

            var trigger = _triggers!.Evaluate(
                position, position.CurrentPrice, daysToExpiration, newsCount);

            if (trigger is null)
            {
                continue;
            }

            decimal? unrealizedFraction = position.CurrentPrice is { } price
                                          && position.AverageEntryPrice > 0m
                ? (price - position.AverageEntryPrice) / position.AverageEntryPrice
                : null;

            _logger.LogInformation(
                "Reviewing {Symbol}: {Trigger}.", position.Symbol, trigger);

            StrategyDecision decision;
            try
            {
                headlineIndex ??= HeadlineIndexSelector.Select(
                    await SafeNewsAsync(cancellationToken),
                    TrackedSymbols,
                    _tradingOptions.HeadlineLimit,
                    _tradingOptions.MaxHeadlinesPerSymbol);

                foreach (var contextSymbol in new[] { underlying, "SPY", "QQQ" }
                             .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                             .Distinct(StringComparer.Ordinal))
                {
                    if (underlyingSnapshots.ContainsKey(contextSymbol))
                    {
                        continue;
                    }

                    try
                    {
                        var latest = await _marketData.GetLatestTradeAsync(
                            contextSymbol, cancellationToken);
                        underlyingSnapshots[contextSymbol] = await BuildUnderlyingSnapshotAsync(
                            latest, now, cancellationToken);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        _logger.LogDebug(error, "No review snapshot for {Symbol}.", contextSymbol);
                    }
                }

                decision = await _reviewer!.ReviewPositionAsync(
                    BuildReviewContext(
                        account, positions, pendingOrders, portfolio,
                        [.. underlyingSnapshots.Values], headlineIndex, now),
                    position, trigger.ToString(), unrealizedFraction, daysToExpiration,
                    cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException
                                          and not AuditPersistenceException)
            {
                // A failed review leaves the position alone. It is still covered by the hard
                // exits, so a broken room cannot strand a position without protection.
                _logger.LogError(error, "The review of {Symbol} failed. Holding.", position.Symbol);
                await RecordPositionDecisionAsync(
                    StrategyAction.Hold($"the review failed: {error.Message}"), position,
                    "position-review", "held", "review failure", trigger.ToString(),
                    cancellationToken);
                await MarkReviewedAsync(position.Symbol, newsCount, cancellationToken);
                continue;
            }

            await MarkReviewedAsync(position.Symbol, newsCount, cancellationToken);

            if (decision.RevisedPolicy is { } revisedPolicy)
            {
                await ApplyPolicyAsync(revisedPolicy, cancellationToken);
            }

            foreach (var action in decision.Actions)
            {
                if (action.Kind == StrategyActionKind.Hold)
                {
                    await RecordPositionDecisionAsync(
                        action, position, "position-review", "held", "review", trigger.ToString(),
                        cancellationToken);
                    continue;
                }

                if (action.Kind != StrategyActionKind.ClosePosition
                    || !string.Equals(action.ContractSymbol, position.Symbol, StringComparison.Ordinal))
                {
                    await RecordPositionDecisionAsync(
                        action, position, "position-review", "rejected", "position",
                        "the review can only hold or close the reviewed position", cancellationToken);
                    continue;
                }

                _logger.LogInformation(
                    "Closing {Symbol} on the room's decision: {Why}", position.Symbol, action.Reasoning);

                try
                {
                    var submitted = await CloseWithAuditAsync(
                        action, position, "position-review", action.Reasoning, cancellationToken);
                    result = result.Add(position.Symbol, submitted.Order);
                }
                catch (AuditPersistenceException)
                {
                    throw;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogError(error, "Could not close {Symbol}.", position.Symbol);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// A context for a review. It carries no candidates: a review judges an existing thesis,
    /// not a shopping list.
    /// </summary>
    private StrategyContext BuildReviewContext(
        AccountState account,
        IReadOnlyList<PositionState> positions,
        IReadOnlyList<OrderState> pendingOrders,
        IReadOnlyList<PortfolioPositionView> portfolio,
        IReadOnlyList<UnderlyingSnapshot> underlyings,
        IReadOnlyList<NewsItem> news,
        DateTimeOffset now) => new()
    {
        NowUtc = now,
        Account = account,
        Positions = positions,
        ContractCatalog = [],
        Policy = Policy,
        Underlyings = underlyings,
        PortfolioPositions = portfolio,
        PendingOrders = pendingOrders,
        Capacity = new PortfolioCapacity(
            Math.Max(0m,
                account.Equity * _riskOptions.MaxTotalRiskFraction
                - portfolio.Sum(position => position.PremiumRisk)
                - pendingOrders.Where(order => order.IsBuy).Sum(order => order.RemainingNotional ?? 0m)),
            Math.Max(0, _riskOptions.MaxConcurrentPositions
                        - positions.Count
                        - pendingOrders.Where(order => order.IsBuy && order.RemainingQuantity > 0)
                            .Select(order => order.ContractSymbol)
                            .Except(positions.Select(position => position.Symbol), StringComparer.Ordinal)
                            .Distinct(StringComparer.Ordinal)
                            .Count()),
            pendingOrders.Where(order => order.IsBuy && order.RemainingQuantity > 0)
                .All(order => order.RemainingNotional is not null)),
        Constraints = BuildConstraints(account, now),
        News = news,
        RemainingPositionSlots = Math.Max(0, _riskOptions.MaxConcurrentPositions - positions.Count),
        NewPositionsHalted = false,
    };

    /// <summary>
    /// What the agent may not exceed, and the horizon it must value against.
    /// </summary>
    /// <remarks>
    /// <c>PositionsExitAtUtc</c> repeats the flatten deliberately. A seat reads
    /// <c>CompetitionFlattenUtc</c> as a deadline and then scores the contract's expiration
    /// payoff, which every permitted contract fails because the flatten always lands first.
    /// Naming the exit as the valuation moment is what makes the room able to judge a trade
    /// rather than reject the whole universe.
    /// </remarks>
    private TradingConstraints BuildConstraints(AccountState account, DateTimeOffset now) =>
        new(Policy.MinDaysToExpiration,
            Policy.MaxDaysToExpiration,
            Policy.MaxContractsPerTrade,
            account.Equity * _riskOptions.MaxRiskPerTradeFraction,
            account.Equity * _riskOptions.MaxTotalRiskFraction,
            _riskOptions.MaxSpreadFraction,
            _riskOptions.MaxQuoteAge,
            _riskOptions.CompetitionFlattenUtc,
            _riskOptions.CompetitionFlattenUtc,
            (decimal)Math.Max(0d, (_riskOptions.CompetitionFlattenUtc - now).TotalHours),
            // The flatten is half an hour before a close, and RiskGuard admits nothing that
            // expires earlier than the flatten day. No permitted contract can expire first.
            ExitIsAlwaysPreExpiry: true);

    // ------------------------------------------------------------------ entries

    private async Task<bool> TryOpenAsync(
        StrategyAction action,
        IReadOnlyList<TradeableContractView> candidates,
        CancellationToken cancellationToken)
    {
        // The contract must be one the harness offered this cycle. A symbol the agent
        // invented is rejected here and never reaches the broker.
        var candidate = candidates.FirstOrDefault(view =>
            string.Equals(view.Contract.ContractSymbol, action.ContractSymbol, StringComparison.Ordinal));

        if (candidate is null)
        {
            var reason = $"{action.ContractSymbol ?? "(none)"} was not offered this cycle";
            _logger.LogWarning(
                "The agent asked for {Symbol}, which was not offered this cycle. Rejected.",
                action.ContractSymbol ?? "(none)");
            await RecordDecisionAsync(
                action, null, "new-trade", "rejected", "catalog", reason, cancellationToken);
            return false;
        }

        var wantedType = action.Kind == StrategyActionKind.OpenCall ? "call" : "put";
        if (!string.Equals(candidate.Contract.OptionType, wantedType, StringComparison.Ordinal))
        {
            var mismatch = $"the agent asked to {action.Kind} but this is a {candidate.Contract.OptionType}";
            _logger.LogWarning(
                RunEvents.RiskRejected,
                "The agent asked to {Kind} but {Symbol} is a {Type}. Rejected.",
                action.Kind, candidate.Contract.ContractSymbol, candidate.Contract.OptionType);
            await RecordDecisionAsync(
                action, candidate, "new-trade", "rejected", "contract type", mismatch,
                cancellationToken);
            return false;
        }

        // Everything from the quote refresh to the submission is one check-then-act, so it runs
        // under the broker gate. A hard exit that fires mid-sitting changes both the quote's
        // context and the account, and neither may change between the check and the order.
        await _brokerGate.WaitAsync(cancellationToken);

        try
        {
            // The catalog was built at the start of the cycle and the room may debate for longer
            // than MaxQuoteAge. Judging the original row would reject every otherwise valid trade
            // on a stale quote, and the limit price below would be set from a price nobody is
            // offering any more. This is a safety read: the typed gateway, never an agent tool.
            if (await RefreshAsync(candidate, cancellationToken) is not { } refreshed)
            {
                var reason = $"no current quote for {candidate.Contract.ContractSymbol}";
                _logger.LogWarning(
                    RunEvents.RiskRejected,
                    "Could not refresh {Symbol} before the risk check. Rejected.",
                    candidate.Contract.ContractSymbol);
                await RecordDecisionAsync(
                    action, candidate, "new-trade", "rejected", "quote refresh", reason,
                    cancellationToken);
                return false;
            }

            candidate = refreshed;

            // The snapshot the room saw is 8 to 10 minutes old by now, and a hard exit may have
            // closed a position and moved equity since. Risk is judged on a snapshot read here,
            // not on the one that was evidence for the debate.
            var current = await RefreshRiskSnapshotAsync(_time.GetUtcNow(), cancellationToken);

            var verdict = _riskGuard.CanOpen(action, candidate, current, Policy);
            if (!verdict.Allowed)
            {
                _logger.LogInformation(
                    RunEvents.RiskRejected,
                    "Risk rejected {Symbol}: {Reason}.",
                    candidate.Contract.ContractSymbol, verdict.Reason);
                await RecordDecisionAsync(
                    action, candidate, "new-trade", "rejected", "risk guard", verdict.Reason,
                    cancellationToken);
                return false;
            }

            var clientOrderId = $"{Mode}-{Guid.NewGuid():N}"[..32];
            var limitPrice = decimal.Round(candidate.Contract.Ask!.Value, 2);

            var submitted = await _orderCoordinator!.SubmitAsync(
                BuildDecision(
                    action, candidate, "new-trade", "accepted", "allowed", verdict.Reason),
                new OrderRequest
                {
                    ClientOrderId = clientOrderId,
                    ContractSymbol = candidate.Contract.ContractSymbol,
                    Quantity = action.Contracts,
                    IsBuy = true,
                    LimitPrice = limitPrice,
                },
                riskReducing: false,
                cancellationToken);

            var order = submitted.Order;

            if (order.Lifecycle is OrderLifecycle.Rejected
                or OrderLifecycle.Canceled or OrderLifecycle.Expired)
            {
                _logger.LogWarning(
                    "{Symbol} was rejected by the broker: {Status}.",
                    candidate.Contract.ContractSymbol, order.RawStatus);
                return false;
            }

            _logger.LogInformation(
                RunEvents.OrderDecided,
                "{State} {Contracts}x {Symbol} at {Price:N2} ({Cost:N2} USD). {Why}",
                DryRun ? "Would open (dry run)" : "Opened",
                action.Contracts, candidate.Contract.ContractSymbol, limitPrice,
                limitPrice * action.Contracts * 100m, action.Reasoning);

            return true;
        }
        finally
        {
            _brokerGate.Release();
        }
    }

    /// <summary>
    /// Reads the current quote for one selected contract, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned to a single contract: the underlying, one option type, and the same date and
    /// strike on both bounds. That reuses the ordinary chain read rather than adding a gateway
    /// method, and it returns one row instead of a chain.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> A read that throws, returns nothing, or returns no matching symbol
    /// gives null, and the caller rejects the trade. A transient fault therefore costs one
    /// trade, which is the correct price for never sending a stale quote to the broker.
    /// </para>
    /// </remarks>
    private async Task<TradeableContractView?> RefreshAsync(
        TradeableContractView candidate, CancellationToken cancellationToken)
    {
        var contract = candidate.Contract;

        try
        {
            var rows = await _marketData.GetOptionCandidatesAsync(
                new OptionChainQuery
                {
                    Underlying = contract.Underlying,
                    OptionType = contract.OptionType,
                    ExpirationFrom = contract.Expiration,
                    ExpirationTo = contract.Expiration,
                    StrikeFrom = contract.Strike,
                    StrikeTo = contract.Strike,
                },
                cancellationToken);

            var fresh = rows.FirstOrDefault(row => string.Equals(
                row.ContractSymbol, contract.ContractSymbol, StringComparison.Ordinal));

            // UnderlyingPrice is carried over: the stale-quote rule guards the option quote,
            // and CostPerContract follows Contract.Ask on its own.
            return fresh is null ? null : candidate with { Contract = fresh };
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            _logger.LogWarning(
                error, "Could not read a current quote for {Symbol}.", contract.ContractSymbol);
            return null;
        }
    }

    // ------------------------------------------------------------------ audit

    private async Task<long> RecordDecisionAsync(
        StrategyAction action,
        TradeableContractView? candidate,
        string purpose,
        string outcome,
        string riskRule,
        string? riskDetail,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.RecordDecisionEventAsync(
                BuildDecision(action, candidate, purpose, outcome, riskRule, riskDetail),
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            throw new AuditPersistenceException("Could not store the decision event.", error);
        }
    }

    private DecisionEventRow BuildDecision(
        StrategyAction action,
        TradeableContractView? candidate,
        string purpose,
        string outcome,
        string riskRule,
        string? riskDetail)
    {
        var contract = candidate?.Contract;
        OccOptionSymbol.TryParse(action.ContractSymbol ?? "", out var parsed);
        var explains = _agent as IExplainsDecision;

        return new DecisionEventRow
        {
            TimestampUtc = _time.GetUtcNow().ToUnixTimeSeconds(),
            Mode = Mode,
            ProposalId = explains?.LastProposalId,
            Purpose = purpose,
            Action = action.Kind.ToString(),
            Outcome = outcome,
            Reason = action.Reasoning,
            RiskResult = riskDetail is null ? riskRule : $"{riskRule}: {riskDetail}",
            Symbol = contract?.Underlying ?? parsed.Underlying,
            OptionSymbol = contract?.ContractSymbol ?? action.ContractSymbol,
            OptionType = contract?.OptionType ??
                         (string.IsNullOrEmpty(parsed.Underlying) ? null : parsed.IsCall ? "call" : "put"),
            Strike = contract?.Strike ??
                     (string.IsNullOrEmpty(parsed.Underlying) ? null : parsed.Strike),
            ExpirationUtc = contract is not null
                ? new DateTimeOffset(contract.Expiration.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                    .ToUnixTimeSeconds()
                : string.IsNullOrEmpty(parsed.Underlying)
                    ? null
                    : new DateTimeOffset(parsed.Expiration.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                        .ToUnixTimeSeconds(),
            UnderlyingPrice = candidate?.UnderlyingPrice,
            Probability = action.ProfitProbability,
            NetVote = explains?.LastNetVote,
            MarketSnapshotJson = contract is null ? null :
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    contract.Bid,
                    contract.Ask,
                    contract.Delta,
                    contract.ImpliedVolatility,
                    quality = contract.Quality.ToString(),
                    contract.QuoteTimestampUtc,
                }),
        };
    }

    private async Task<long> RecordPositionDecisionAsync(
        StrategyAction action,
        PositionState position,
        string purpose,
        string outcome,
        string riskRule,
        string? riskDetail,
        CancellationToken cancellationToken)
    {
        var decision = BuildPositionDecision(
            action, position, purpose, outcome, riskRule, riskDetail);
        try
        {
            return await _store.RecordDecisionEventAsync(decision, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            throw new AuditPersistenceException(
                $"Could not store the decision for {position.Symbol}.", error);
        }
    }

    private DecisionEventRow BuildPositionDecision(
        StrategyAction action,
        PositionState position,
        string purpose,
        string outcome,
        string riskRule,
        string? riskDetail)
    {
        var baseDecision = BuildDecision(
            action with { ContractSymbol = position.Symbol }, null,
            purpose, outcome, riskRule, riskDetail);
        return baseDecision with
        {
            MarketSnapshotJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                position.Quantity,
                position.AverageEntryPrice,
                position.CurrentPrice,
                position.MarketValue,
                position.UnrealizedPnl,
            }),
        };
    }

    /// <summary>
    /// Audits a close and then sends it. If the first audit write fails, the risk-reducing
    /// close is still attempted and the session stops immediately after that attempt.
    /// </summary>
    private async Task<OrderSubmissionResult> CloseWithAuditAsync(
        StrategyAction action,
        PositionState position,
        string purpose,
        string reason,
        CancellationToken cancellationToken)
    {
        var clientOrderId = $"{Mode}-c-{Guid.NewGuid():N}"[..32];
        return await _orderCoordinator!.SubmitAsync(
            BuildPositionDecision(action, position, purpose, "accepted", "close", reason),
            new OrderRequest
            {
                ClientOrderId = clientOrderId,
                ContractSymbol = position.Symbol,
                Quantity = Math.Abs(position.Quantity),
                IsBuy = false,
                LimitPrice = null,
            },
            riskReducing: true,
            cancellationToken);
    }

    private static bool HasPendingClose(
        string symbol, IReadOnlyList<OrderState> pendingOrders) =>
        pendingOrders.Any(order =>
            !order.IsBuy
            && !order.IsTerminal
            && order.RemainingQuantity > 0
            && string.Equals(order.ContractSymbol, symbol, StringComparison.Ordinal));

    private async Task MarkReviewedAsync(
        string symbol, int newsCount, CancellationToken cancellationToken)
    {
        _triggers!.MarkReviewed(symbol, newsCount);
        try
        {
            await _store.SavePositionReviewStateAsync(
                Mode, symbol, _time.GetUtcNow(), newsCount, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            throw new AuditPersistenceException(
                $"Could not store the review cursor for {symbol}.", error);
        }
    }

    private async Task ApplyPolicyAsync(
        StrategyPolicy proposed, CancellationToken cancellationToken)
    {
        var clamped = proposed.ClampTo(_riskOptions, Today);
        try
        {
            await _store.SavePolicyAsync(Mode, clamped, _time.GetUtcNow(), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            throw new AuditPersistenceException("Could not store the active strategy policy.", error);
        }

        _policy = clamped;
    }

    /// <summary>What one batch of close attempts did. Public because the hard-exit loop reports it.</summary>
    public sealed record CloseBatchResult
    {
        public int Submitted { get; init; }
        public int ConfirmedClosed { get; init; }
        public int Rejected { get; init; }
        public IReadOnlySet<string> AttemptedSymbols { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public CloseBatchResult Add(string symbol, OrderState order)
        {
            var attempted = new HashSet<string>(AttemptedSymbols, StringComparer.Ordinal) { symbol };
            return this with
            {
                Submitted = Submitted + (order.Lifecycle is OrderLifecycle.Open
                    or OrderLifecycle.PartiallyFilled or OrderLifecycle.Filled
                    or OrderLifecycle.Uncertain ? 1 : 0),
                ConfirmedClosed = ConfirmedClosed + (order.Lifecycle == OrderLifecycle.Filled ? 1 : 0),
                Rejected = Rejected + (order.Lifecycle is OrderLifecycle.Rejected
                    or OrderLifecycle.Canceled or OrderLifecycle.Expired ? 1 : 0),
                AttemptedSymbols = attempted,
            };
        }
    }

    // ------------------------------------------------------------------ tradeable catalog

    private sealed record CatalogBuildResult(
        IReadOnlyList<TradeableContractView> Contracts,
        IReadOnlyList<UnderlyingSnapshot> Underlyings)
    {
        public static CatalogBuildResult Empty { get; } = new([], []);

        /// <summary>
        /// How many contracts each gate removed, by <see cref="RiskVerdict.Code"/>.
        /// </summary>
        /// <remarks>
        /// An empty catalog is normal out of hours and abnormal during a session, and this
        /// count is the only thing that separates the two. Without it, a cycle reports
        /// "0 candidate(s)" and says nothing about which rule emptied it.
        /// </remarks>
        public IReadOnlyDictionary<string, int> Dropped { get; init; } =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>The contracts that reached the gates, admitted and rejected together.</summary>
        public int Examined { get; init; }

        /// <summary>
        /// Symbols that produced no chain to examine, by reason. Counted apart from
        /// <see cref="Dropped"/> because one entry here is a symbol, not a contract, and
        /// adding the two totals together would report a number that means nothing.
        /// </summary>
        public IReadOnlyDictionary<string, int> SkippedSymbols { get; init; } =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>The contracts the gates removed.</summary>
        public int DroppedCount => Examined - Contracts.Count;

        /// <summary>A tally, worst offender first, or null when the tally is empty.</summary>
        public static EventCountBreakdown? Tally(IReadOnlyDictionary<string, int> counts) => counts.Count == 0
            ? null
            : new EventCountBreakdown(counts);
    }

    private async Task<CatalogBuildResult> BuildTradeableCatalogAsync(
        IReadOnlyList<PositionState> positions,
        IReadOnlyList<OrderState> pendingOrders,
        RiskSnapshot riskSnapshot,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var held = positions.Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);
        var pendingBuys = pendingOrders
            .Where(order => order.IsBuy && order.RemainingQuantity > 0)
            .Select(order => order.ContractSymbol)
            .ToHashSet(StringComparer.Ordinal);
        var views = new List<TradeableContractView>();
        var underlyings = new List<UnderlyingSnapshot>();
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        var skippedSymbols = new Dictionary<string, int>(StringComparer.Ordinal);
        var examined = 0;

        void Drop(string code) =>
            dropped[code] = dropped.GetValueOrDefault(code) + 1;

        void SkipSymbol(string code) =>
            skippedSymbols[code] = skippedSymbols.GetValueOrDefault(code) + 1;

        var scan = _tradingOptions.OptionScanMaxMoneynessFraction;
        if (scan is <= 0m or >= 1m)
        {
            throw new InvalidOperationException(
                $"OptionScanMaxMoneynessFraction must be between zero and one, not {scan}.");
        }

        foreach (var symbol in TrackedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LatestTrade latest;
            try
            {
                latest = await _marketData.GetLatestTradeAsync(symbol, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogDebug(error, "No price for {Symbol} this cycle.", symbol);
                SkipSymbol("no-underlying-price");
                continue;
            }

            var spot = latest.Price;
            if (spot <= 0m)
            {
                SkipSymbol("no-underlying-price");
                continue;
            }

            underlyings.Add(await BuildUnderlyingSnapshotAsync(latest, now, cancellationToken));

            IReadOnlyList<OptionCandidate> chain;
            try
            {
                chain = await _marketData.GetOptionCandidatesAsync(
                    new OptionChainQuery
                    {
                        Underlying = symbol,
                        ExpirationFrom = today.AddDays(Policy.MinDaysToExpiration),
                        ExpirationTo = today.AddDays(Policy.MaxDaysToExpiration),
                        StrikeFrom = spot * (1m - scan),
                        StrikeTo = spot * (1m + scan),
                    },
                    cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A partial chain is not authoritative. Drop the whole symbol for this cycle.
                _logger.LogDebug(error, "No complete option chain for {Symbol} this cycle.", symbol);
                SkipSymbol("no-option-chain");
                continue;
            }

            foreach (var contract in chain)
            {
                examined++;

                if (held.Contains(contract.ContractSymbol) || pendingBuys.Contains(contract.ContractSymbol))
                {
                    Drop("already-held-or-pending");
                    continue;
                }

                var kind = contract.OptionType switch
                {
                    "call" => StrategyActionKind.OpenCall,
                    "put" => StrategyActionKind.OpenPut,
                    _ => StrategyActionKind.Hold,
                };

                if (kind == StrategyActionKind.Hold)
                {
                    Drop("not-a-call-or-put");
                    continue;
                }

                var view = new TradeableContractView
                {
                    Contract = contract,
                    UnderlyingPrice = spot,
                };

                var oneContract = new StrategyAction
                {
                    Kind = kind,
                    ContractSymbol = contract.ContractSymbol,
                    Contracts = 1,
                    Reasoning = "mechanical catalog admission",
                };

                var verdict = _riskGuard.CanOpen(oneContract, view, riskSnapshot, Policy);
                if (verdict.Allowed)
                {
                    views.Add(view);
                }
                else
                {
                    Drop(verdict.Code);
                }
            }
        }

        var symbolOrder = TrackedSymbols
            .Select((symbol, index) => (symbol, index))
            .ToDictionary(item => item.symbol, item => item.index, StringComparer.Ordinal);

        return new CatalogBuildResult(
            [.. views
                .OrderBy(view => symbolOrder.GetValueOrDefault(view.Contract.Underlying, int.MaxValue))
                .ThenBy(view => view.Contract.Expiration)
                .ThenBy(view => view.Contract.OptionType, StringComparer.Ordinal)
                .ThenBy(view => view.Contract.Strike)
                .ThenBy(view => view.Contract.ContractSymbol, StringComparer.Ordinal)],
            underlyings)
        {
            Dropped = dropped,
            Examined = examined,
            SkippedSymbols = skippedSymbols,
        };
    }

    private async Task<UnderlyingSnapshot> BuildUnderlyingSnapshotAsync(
        LatestTrade latest, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<PriceBar> bars;
        try
        {
            bars = await _marketData.GetBarsAsync(
                latest.Symbol, "1Day", now.AddDays(-20), now, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "No completed daily bars for {Symbol}.", latest.Symbol);
            bars = [];
        }

        var todayEastern = MarketCalendar.ToEastern(now).Date;
        var completed = bars
            .Where(bar => MarketCalendar.ToEastern(bar.TimestampUtc).Date < todayEastern)
            .OrderBy(bar => bar.TimestampUtc)
            .ToArray();

        decimal? ReturnFrom(int sessions) => completed.Length >= sessions
            && completed[^sessions].Close > 0m
                ? latest.Price / completed[^sessions].Close - 1m
                : null;

        return new UnderlyingSnapshot(
            latest.Symbol, latest.Price, latest.TimestampUtc, ReturnFrom(1), ReturnFrom(5));
    }

    private async Task<IReadOnlyList<PortfolioPositionView>> BuildPortfolioPositionsAsync(
        IReadOnlyList<PositionState> positions,
        CancellationToken cancellationToken)
    {
        var theses = await _store.PositionThesesAsync(
            Mode,
            [.. positions.Select(position => position.Symbol)],
            cancellationToken);

        return
        [
            .. positions.Select(position =>
        {
            var parsed = OccOptionSymbol.TryParse(position.Symbol, out var option) ? option : default;
            theses.TryGetValue(position.Symbol, out var thesis);
            decimal? pnlFraction = position.CurrentPrice is { } current && position.AverageEntryPrice > 0m
                ? (current - position.AverageEntryPrice) / position.AverageEntryPrice
                : null;

            return new PortfolioPositionView
            {
                Position = position,
                Underlying = parsed.Underlying,
                OptionType = string.IsNullOrEmpty(parsed.Underlying) ? null : parsed.IsCall ? "call" : "put",
                Strike = string.IsNullOrEmpty(parsed.Underlying) ? null : parsed.Strike,
                Expiration = string.IsNullOrEmpty(parsed.Underlying) ? null : parsed.Expiration,
                UnrealizedPnlFraction = pnlFraction,
                PremiumRisk = position.AverageEntryPrice * Math.Abs(position.Quantity) * 100m,
                OriginalThesis = thesis?.Thesis,
                OriginalThesisConditions = thesis?.Conditions ?? [],
            };
        }),
        ];
    }

    private async Task<int> CountRecentNewsAsync(
        string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var news = await _marketData.GetNewsAsync(
                [symbol], now - _tradingOptions.HeadlineLookback, now, 20, cancellationToken);

            return news.Count;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "No news for {Symbol} this cycle.", symbol);
            return 0;
        }
    }

    private decimal ResolveDayOpeningEquity(
        DateOnly todayEastern,
        AccountState account,
        IReadOnlyList<PositionState> positions,
        IReadOnlyList<OrderState> dailyOrders)
    {
        if (account.PreviousCloseEquity is { } priorClose && priorClose > 0m)
        {
            return priorClose;
        }

        if (_fallbackBaselineDate == todayEastern
            && _fallbackDayOpeningEquity is { } cachedBaseline
            && cachedBaseline > 0m)
        {
            return cachedBaseline;
        }

        var hasFillToday = dailyOrders.Any(order => order.FilledQuantity > 0);
        if (account.Equity <= 0m || positions.Count > 0 || hasFillToday)
        {
            return 0m;
        }

        _fallbackBaselineDate = todayEastern;
        _fallbackDayOpeningEquity = account.Equity;
        _logger.LogWarning(
            "Alpaca did not supply prior-close equity. Using current equity {Equity:N2} "
            + "as this session's baseline because the account has no positions and no fills today.",
            account.Equity);
        return account.Equity;
    }

    private async Task<IReadOnlyList<NewsItem>> SafeNewsAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        try
        {
            return await _marketData.GetNewsAsync(
                TrackedSymbols, now - _tradingOptions.HeadlineLookback, now,
                _tradingOptions.HeadlineLimit * 4, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "No news this cycle.");
            return [];
        }
    }

    private async Task RecordEquityAsync(AccountState account, CancellationToken cancellationToken)
    {
        try
        {
            await _store.RecordEquityAsync(
                _time.GetUtcNow().ToUnixTimeSeconds(), Mode, account.Equity, account.Cash,
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new AuditPersistenceException("Could not record the equity snapshot.", error);
        }
    }
}
