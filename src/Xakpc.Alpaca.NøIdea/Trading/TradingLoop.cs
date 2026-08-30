using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Replay;
using Xakpc.Alpaca.NøIdea.Storage;

namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>What one cycle did, for the log and the tests.</summary>
public sealed record CycleResult
{
    public required DateTimeOffset AtUtc { get; init; }
    public int CandidatesOffered { get; init; }
    public int PositionsClosed { get; init; }
    public int OrdersSubmitted { get; init; }
    public int ActionsRejected { get; init; }
    public bool PolicyRevised { get; init; }
    public decimal Equity { get; init; }
}

/// <summary>
/// One pass of the trading cycle. The same code runs live and in replay.
/// </summary>
/// <remarks>
/// <para>
/// The order of the steps is the safety property. Deterministic exits run <em>before</em> the
/// agent is asked anything, so a hung or misbehaving agent cannot stop a stop-loss. The risk
/// guard runs <em>after</em> the agent, on every action, so nothing the agent returns can
/// exceed a hard limit. The agent sits in the middle and only ever produces data.
/// </para>
/// <para>
/// Live and replay differ only in which gateways and which agent are injected. There is no
/// mode flag inside this class, because a branch on mode is how the two paths drift apart.
/// </para>
/// </remarks>
public sealed class TradingLoop(
    IMarketDataGateway marketData,
    ITradingGateway trading,
    IStrategyAgent agent,
    RiskGuard riskGuard,
    RiskOptions riskOptions,
    TradingStore store,
    TimeProvider time,
    ILogger logger)
{
    private readonly IMarketDataGateway _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
    private readonly ITradingGateway _trading = trading ?? throw new ArgumentNullException(nameof(trading));
    private readonly IStrategyAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    private readonly RiskGuard _riskGuard = riskGuard ?? throw new ArgumentNullException(nameof(riskGuard));
    private readonly RiskOptions _riskOptions = riskOptions ?? throw new ArgumentNullException(nameof(riskOptions));
    private readonly TradingStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IPositionReviewer? _reviewer = agent as IPositionReviewer;

    /// <summary>Null when no reviewer is present, which leaves the deterministic exits alone.</summary>
    private readonly PositionReviewTriggers? _triggers =
        agent is IPositionReviewer ? new PositionReviewTriggers(new ReviewTriggerOptions(), time) : null;

    private readonly Dictionary<DateOnly, int> _openedPerDay = [];
    private readonly Dictionary<DateOnly, decimal> _dayOpeningEquity = [];

    private StrategyPolicy _policy = new();

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
        init => _policy = (value ?? throw new ArgumentNullException(nameof(value))).ClampTo(riskOptions);
    }

    /// <summary>The symbols the loop looks at each cycle.</summary>
    public required IReadOnlyList<string> TrackedSymbols { get; init; }

    /// <summary>The mode string written to the audit trail: <c>live</c> or <c>replay</c>.</summary>
    public required string Mode { get; init; }

    public async Task<CycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // 1. Account state. Alpaca is the source of truth, never SQLite.
        var account = await _trading.GetAccountAsync(cancellationToken);
        var positions = await _trading.ListPositionsAsync(cancellationToken);

        if (account.IsTradingBlocked || account.IsAccountBlocked)
        {
            _logger.LogError("Trading is blocked on the account. Skipping the cycle.");
            return new CycleResult { AtUtc = now, Equity = account.Equity };
        }

        if (!_dayOpeningEquity.ContainsKey(today))
        {
            _dayOpeningEquity[today] = account.Equity;
        }

        // 2. Deterministic exits, BEFORE the agent is consulted. A stop-loss must not depend
        //    on a model answering.
        var closed = await ManageOpenPositionsAsync(positions, cancellationToken);

        // 2b. War-room position review (spec §10, §11). Runs only where a trigger fired and
        //     only after the hard exits, so a stop-loss never waits on a model answering.
        if (_reviewer is not null && _triggers is not null)
        {
            closed += await ReviewPositionsAsync(
                positions, account, cancellationToken);
        }
        if (closed > 0)
        {
            positions = await _trading.ListPositionsAsync(cancellationToken);
            account = await _trading.GetAccountAsync(cancellationToken);
        }

        var snapshot = new RiskSnapshot
        {
            Equity = account.Equity,
            DayOpeningEquity = _dayOpeningEquity[today],
            OpenPositions = positions.Count,
            OpenPositionCost = positions.Sum(p => p.AverageEntryPrice * Math.Abs(p.Quantity) * 100m),
            PositionsOpenedToday = _openedPerDay.GetValueOrDefault(today),
        };

        var halted = _riskGuard.NewPositionsHalted(snapshot);

        // 3. Cheap filter. Everything offered to the agent already passes contract quality,
        //    the expiration window, and the tradeable probability band, so no token is spent
        //    on a candidate that could not trade anyway.
        var candidates = halted
            ? []
            : await BuildCandidatesAsync(positions, cancellationToken);

        var news = await SafeNewsAsync(cancellationToken);

        // 4. The agent decides. It receives data and returns data.
        var context = new StrategyContext
        {
            NowUtc = now,
            Account = account,
            Positions = positions,
            Candidates = candidates,
            Policy = Policy,
            News = news,
            RecentOutcomes = [],
            RemainingPositionSlots = Math.Max(
                0, _riskOptions.MaxConcurrentPositions - positions.Count),
            NewPositionsHalted = halted,
        };

        StrategyDecision decision;
        try
        {
            decision = await _agent.DecideAsync(context, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Fail closed: an agent fault skips the cycle. It never opens a position.
            _logger.LogError(error, "The agent failed. Skipping new positions this cycle.");
            return new CycleResult
            {
                AtUtc = now,
                CandidatesOffered = candidates.Count,
                PositionsClosed = closed,
                Equity = account.Equity,
            };
        }

        // 5. A revised policy is clamped before anything reads it.
        var revised = false;
        if (decision.RevisedPolicy is { } proposed)
        {
            var clamped = proposed.ClampTo(_riskOptions);
            revised = clamped.DiffersFrom(Policy);

            if (revised)
            {
                _logger.LogInformation(
                    "Policy revised: DTE {MinDte}-{MaxDte}, band {MinP:P0}-{MaxP:P0}, "
                    + "take {Take:P0}, stop {Stop:P0}. {Rationale}",
                    clamped.MinDaysToExpiration, clamped.MaxDaysToExpiration,
                    clamped.MinMarketProbability, clamped.MaxMarketProbability,
                    clamped.TakeProfitFraction, clamped.StopLossFraction, clamped.Rationale);
            }

            _policy = clamped;
        }

        // 6. Every action through the risk guard, then execute.
        var submitted = 0;
        var rejected = 0;

        foreach (var action in decision.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (action.Kind)
            {
                case StrategyActionKind.Hold:
                    break;

                case StrategyActionKind.ClosePosition:
                    if (await TryCloseAsync(action, positions, cancellationToken))
                    {
                        closed++;
                    }
                    else
                    {
                        rejected++;
                    }

                    break;

                case StrategyActionKind.OpenCall:
                case StrategyActionKind.OpenPut:
                    if (await TryOpenAsync(action, candidates, snapshot, today, cancellationToken))
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
            CandidatesOffered = candidates.Count,
            PositionsClosed = closed,
            OrdersSubmitted = submitted,
            ActionsRejected = rejected,
            PolicyRevised = revised,
            Equity = finalAccount.Equity,
        };
    }

    // ------------------------------------------------------------------ exits

    private async Task<int> ManageOpenPositionsAsync(
        IReadOnlyList<PositionState> positions, CancellationToken cancellationToken)
    {
        var closed = 0;

        foreach (var position in positions)
        {
            var reason = _riskGuard.MandatoryExitReason(position, Policy, position.CurrentPrice);
            if (reason is null)
            {
                continue;
            }

            _logger.LogInformation("Closing {Symbol}: {Reason}.", position.Symbol, reason);

            try
            {
                await _trading.ClosePositionAsync(position.Symbol, cancellationToken);
                closed++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A failed close is not fatal to the cycle; the next cycle tries again.
                _logger.LogError(error, "Could not close {Symbol}.", position.Symbol);
            }
        }

        return closed;
    }

    private async Task<bool> TryCloseAsync(
        StrategyAction action, IReadOnlyList<PositionState> positions, CancellationToken cancellationToken)
    {
        if (action.ContractSymbol is not { } symbol
            || positions.All(p => !string.Equals(p.Symbol, symbol, StringComparison.Ordinal)))
        {
            _logger.LogWarning(
                "The agent asked to close {Symbol}, which is not an open position. Ignored.",
                action.ContractSymbol ?? "(none)");
            return false;
        }

        _logger.LogInformation("Closing {Symbol} on the agent's request: {Why}", symbol, action.Reasoning);
        await _trading.ClosePositionAsync(symbol, cancellationToken);
        return true;
    }


    /// <summary>
    /// Sends positions that a trigger picked out to the war room. Spec §10 and §11.
    /// </summary>
    /// <remarks>
    /// A review asks whether the thesis still holds. It does not close anything by itself:
    /// the room proposes, the room votes, and only an approved close reaches the broker. A
    /// position that fails to review is simply left alone, still guarded by the hard exits.
    /// </remarks>
    private async Task<int> ReviewPositionsAsync(
        IReadOnlyList<PositionState> positions,
        AccountState account,
        CancellationToken cancellationToken)
    {
        var closed = 0;
        var now = _time.GetUtcNow();

        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                decision = await _reviewer!.ReviewPositionAsync(
                    BuildReviewContext(account, positions, now),
                    position, trigger.ToString(), unrealizedFraction, daysToExpiration,
                    cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A failed review leaves the position alone. It is still covered by the hard
                // exits, so a broken room cannot strand a position without protection.
                _logger.LogError(error, "The review of {Symbol} failed. Holding.", position.Symbol);
                _triggers.MarkReviewed(position.Symbol, newsCount);
                continue;
            }

            _triggers.MarkReviewed(position.Symbol, newsCount);

            foreach (var action in decision.Actions
                         .Where(item => item.Kind == StrategyActionKind.ClosePosition))
            {
                if (!string.Equals(action.ContractSymbol, position.Symbol, StringComparison.Ordinal))
                {
                    continue;
                }

                _logger.LogInformation(
                    "Closing {Symbol} on the room's decision: {Why}", position.Symbol, action.Reasoning);

                try
                {
                    await _trading.ClosePositionAsync(position.Symbol, cancellationToken);
                    closed++;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogError(error, "Could not close {Symbol}.", position.Symbol);
                }
            }
        }

        return closed;
    }

    /// <summary>
    /// A context for a review. It carries no candidates: a review judges an existing thesis,
    /// not a shopping list.
    /// </summary>
    private StrategyContext BuildReviewContext(
        AccountState account, IReadOnlyList<PositionState> positions, DateTimeOffset now) => new()
    {
        NowUtc = now,
        Account = account,
        Positions = positions,
        Candidates = [],
        Policy = Policy,
        RemainingPositionSlots = Math.Max(0, _riskOptions.MaxConcurrentPositions - positions.Count),
        NewPositionsHalted = false,
    };

    // ------------------------------------------------------------------ entries

    private async Task<bool> TryOpenAsync(
        StrategyAction action,
        IReadOnlyList<CandidateView> candidates,
        RiskSnapshot snapshot,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        // The contract must be one the harness offered this cycle. A symbol the agent
        // invented is rejected here and never reaches the broker.
        var candidate = candidates.FirstOrDefault(view =>
            string.Equals(view.Candidate.ContractSymbol, action.ContractSymbol, StringComparison.Ordinal));

        if (candidate is null)
        {
            _logger.LogWarning(
                "The agent asked for {Symbol}, which was not offered this cycle. Rejected.",
                action.ContractSymbol ?? "(none)");
            return false;
        }

        var wantedType = action.Kind == StrategyActionKind.OpenCall ? "call" : "put";
        if (!string.Equals(candidate.Candidate.OptionType, wantedType, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "The agent asked to {Kind} but {Symbol} is a {Type}. Rejected.",
                action.Kind, candidate.Candidate.ContractSymbol, candidate.Candidate.OptionType);
            return false;
        }

        var verdict = _riskGuard.CanOpen(action, candidate, snapshot, Policy);
        if (!verdict.Allowed)
        {
            _logger.LogInformation(
                "Risk rejected {Symbol}: {Reason}.", candidate.Candidate.ContractSymbol, verdict.Reason);
            return false;
        }

        // Reserve the client order id BEFORE submitting. If the submit then fails with an
        // uncertain result the id is already durable, so recovery asks the broker what
        // happened instead of sending a second order.
        var clientOrderId = $"{Mode}-{Guid.NewGuid():N}"[..32];
        var limitPrice = decimal.Round(candidate.Candidate.ReferencePrice, 2);

        try
        {
            await _store.ReserveAsync(new OrderRecord
            {
                ClientOrderId = clientOrderId,
                OptionSymbol = candidate.Candidate.ContractSymbol,
                Side = "Buy",
                Quantity = action.Contracts,
                OrderType = "Limit",
                LimitPrice = limitPrice,
                SubmittedUtc = _time.GetUtcNow().ToUnixTimeSeconds(),
                Status = "reserved",
            }, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A failed write means no durable id, so no order. Fail closed.
            _logger.LogError(error, "Could not reserve an order id. Not submitting.");
            return false;
        }

        var order = await _trading.SubmitOrderAsync(
            new OrderRequest
            {
                ClientOrderId = clientOrderId,
                ContractSymbol = candidate.Candidate.ContractSymbol,
                Quantity = action.Contracts,
                IsBuy = true,
                LimitPrice = limitPrice,
            },
            cancellationToken);

        await _store.RecordResultAsync(
            clientOrderId, order.BrokerOrderId, order.RawStatus, null, cancellationToken);

        if (order.Lifecycle == OrderLifecycle.Rejected)
        {
            _logger.LogWarning(
                "{Symbol} was rejected by the broker: {Status}.",
                candidate.Candidate.ContractSymbol, order.RawStatus);
            return false;
        }

        _openedPerDay[today] = _openedPerDay.GetValueOrDefault(today) + 1;

        _logger.LogInformation(
            "Opened {Contracts}x {Symbol} at {Price:N2} ({Cost:N2} USD). {Why}",
            action.Contracts, candidate.Candidate.ContractSymbol, limitPrice,
            limitPrice * action.Contracts * 100m, action.Reasoning);

        return true;
    }

    // ------------------------------------------------------------------ candidates

    private async Task<IReadOnlyList<CandidateView>> BuildCandidatesAsync(
        IReadOnlyList<PositionState> positions, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var held = positions.Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);
        var views = new List<CandidateView>();

        foreach (var symbol in TrackedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            decimal spot;
            try
            {
                spot = (await _marketData.GetLatestTradeAsync(symbol, cancellationToken)).Price;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // No price means no candidate for this symbol. Fail closed and continue.
                _logger.LogDebug(error, "No price for {Symbol} this cycle.", symbol);
                continue;
            }

            var newsCount = await CountRecentNewsAsync(symbol, now, cancellationToken);
            if (Policy.RequireFreshNews && newsCount == 0)
            {
                continue;
            }

            foreach (var optionType in (string[])["call", "put"])
            {
                var chain = await _marketData.GetOptionCandidatesAsync(
                    new OptionChainQuery
                    {
                        Underlying = symbol,
                        OptionType = optionType,
                        ExpirationFrom = today.AddDays(Policy.MinDaysToExpiration),
                        ExpirationTo = today.AddDays(Policy.MaxDaysToExpiration),
                        StrikeFrom = spot * 0.90m,
                        StrikeTo = spot * 1.10m,
                    },
                    cancellationToken);

                var ladder = chain
                    .Where(c => string.Equals(c.OptionType, "call", StringComparison.Ordinal))
                    .Select(c => new LadderPoint(c.Strike, c.ReferencePrice))
                    .OrderBy(point => point.Strike)
                    .ToArray();

                foreach (var contract in chain)
                {
                    if (held.Contains(contract.ContractSymbol))
                    {
                        continue;   // No duplicate exposure in the same contract.
                    }

                    var view = new CandidateView
                    {
                        Candidate = contract,
                        UnderlyingPrice = spot,
                        MarketProbability = OptionLadder.ProbabilityAbove(ladder, contract.Strike),
                        RecentNewsCount = newsCount,
                    };

                    if (!_riskGuard.CheckContract(view, Policy).Allowed)
                    {
                        continue;
                    }

                    // The tradeable band: a contract the market prices as nearly certain
                    // either way carries no opportunity worth an agent call.
                    if (view.MarketProbability is { } probability
                        && (probability < Policy.MinMarketProbability
                            || probability > Policy.MaxMarketProbability))
                    {
                        continue;
                    }

                    views.Add(view);
                }
            }
        }

        // Cheapest first: with a 2% per-trade cap the affordable contracts are the ones the
        // agent can actually act on.
        return views
            .OrderBy(view => view.CostPerContract)
            .Take(40)
            .ToArray();
    }

    private async Task<int> CountRecentNewsAsync(
        string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var news = await _marketData.GetNewsAsync(
                [symbol], now.AddHours(-Policy.FreshNewsWithinHours), now, 20, cancellationToken);

            return news.Count;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "No news for {Symbol} this cycle.", symbol);
            return 0;
        }
    }

    private async Task<IReadOnlyList<NewsItem>> SafeNewsAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        try
        {
            return await _marketData.GetNewsAsync(
                TrackedSymbols, now.AddHours(-Policy.FreshNewsWithinHours), now, 30, cancellationToken);
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
            _logger.LogWarning(error, "Could not record the equity snapshot.");
        }
    }
}
