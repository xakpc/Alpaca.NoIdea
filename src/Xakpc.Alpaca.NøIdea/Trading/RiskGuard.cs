using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>The answer to one risk question. A rejection always carries a reason.</summary>
public sealed record RiskVerdict(bool Allowed, string Reason)
{
    public static RiskVerdict Allow() => new(true, "allowed");
    public static RiskVerdict Reject(string reason) => new(false, reason);
}

/// <summary>What the account looks like right now, for the limit checks.</summary>
public sealed record RiskSnapshot
{
    public required decimal Equity { get; init; }
    public required decimal Cash { get; init; }
    public required decimal DayOpeningEquity { get; init; }
    public required int OpenPositions { get; init; }
    public required decimal OpenPositionCost { get; init; }
    public required int PositionsOpenedToday { get; init; }
    public int PendingOpenPositions { get; init; }
    public decimal PendingOrderCost { get; init; }
    public bool PendingRiskKnown { get; init; } = true;
}

/// <summary>
/// The hard risk rules. <b>Only this class can allow an order.</b>
/// </summary>
/// <remarks>
/// <para>
/// Every rule here is deterministic C# reading numbers. No rule consults an LLM, and no LLM
/// output can relax one: the agent's request arrives as data and is checked against
/// <see cref="RiskOptions"/>, which the agent cannot write to (ADR-006).
/// </para>
/// <para>
/// The class fails closed. A missing quote, an unparseable contract, a stale price, or a
/// number it cannot verify is a rejection, never a pass. A skipped trade is better than an
/// unknown trade.
/// </para>
/// </remarks>
public sealed class RiskGuard(RiskOptions options, TimeProvider time)
{
    private readonly RiskOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>Checks the account-wide conditions that can halt all new positions.</summary>
    public RiskVerdict CanConsiderNewPositions(RiskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.DayOpeningEquity <= 0m)
        {
            return RiskVerdict.Reject("prior-close equity is unavailable");
        }

        if (!snapshot.PendingRiskKnown)
        {
            return RiskVerdict.Reject("a pending buy order has unknown remaining risk");
        }

        var loss = (snapshot.DayOpeningEquity - snapshot.Equity) / snapshot.DayOpeningEquity;
        if (loss >= _options.MaxDailyLossFraction)
        {
            return RiskVerdict.Reject(
                $"daily loss {(loss * 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}% "
                + $"reached the {(_options.MaxDailyLossFraction * 100m).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}% limit");
        }

        return RiskVerdict.Allow();
    }

    /// <summary>True when an account-wide rule prevents all new positions.</summary>
    public bool NewPositionsHalted(RiskSnapshot snapshot) =>
        !CanConsiderNewPositions(snapshot).Allowed;

    /// <summary>
    /// Whether one proposed opening trade may be submitted.
    /// </summary>
    /// <param name="candidate">
    /// The contract as the harness offered it. The caller must have matched this to the
    /// agent's requested symbol; an unmatched symbol never reaches this method.
    /// </param>
    public RiskVerdict CanOpen(
        StrategyAction action,
        TradeableContractView candidate,
        RiskSnapshot snapshot,
        StrategyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);

        if (snapshot.Equity <= 0m)
        {
            return RiskVerdict.Reject("account equity is not positive");
        }

        var accountVerdict = CanConsiderNewPositions(snapshot);
        if (!accountVerdict.Allowed)
        {
            return accountVerdict;
        }

        if (snapshot.OpenPositions + snapshot.PendingOpenPositions >= _options.MaxConcurrentPositions)
        {
            return RiskVerdict.Reject(
                $"open and pending positions use all {_options.MaxConcurrentPositions} slots");
        }

        if (snapshot.PositionsOpenedToday >= _options.MaxNewPositionsPerDay)
        {
            return RiskVerdict.Reject(
                $"already opened {snapshot.PositionsOpenedToday} positions today");
        }

        var contracts = action.Contracts;
        if (contracts < 1)
        {
            return RiskVerdict.Reject("contract count is not positive");
        }

        if (contracts > policy.MaxContractsPerTrade || contracts > _options.HardMaxContractsPerTrade)
        {
            return RiskVerdict.Reject($"{contracts} contracts exceeds the per-trade limit");
        }

        // A long option cannot lose more than the premium, so the cost IS the risk.
        var cost = candidate.CostPerContract * contracts;

        if (cost <= 0m)
        {
            return RiskVerdict.Reject("contract price is not positive");
        }

        if (cost > snapshot.Equity * _options.MaxRiskPerTradeFraction)
        {
            return RiskVerdict.Reject(
                $"cost {cost:N2} exceeds {_options.MaxRiskPerTradeFraction:P0} of equity");
        }

        if (snapshot.OpenPositionCost + snapshot.PendingOrderCost + cost
            > snapshot.Equity * _options.MaxTotalRiskFraction)
        {
            return RiskVerdict.Reject(
                $"total exposure would exceed {_options.MaxTotalRiskFraction:P0} of equity");
        }

        // Alpaca rejects an order the account cannot pay for; catching it here keeps the
        // rejection in the audit trail with a reason instead of as a broker error.
        var availableCash = Math.Max(0m, snapshot.Cash - snapshot.PendingOrderCost);
        if (cost > availableCash)
        {
            return RiskVerdict.Reject("not enough cash after pending orders");
        }

        return CheckContract(candidate, policy);
    }

    /// <summary>
    /// The contract-quality and expiration rules, shared by the cheap filter and the final
    /// check. The cheap filter runs this before an agent call to avoid paying for a candidate
    /// that cannot trade; the guard runs it again at submit time because the quote may have
    /// moved.
    /// </summary>
    public RiskVerdict CheckContract(TradeableContractView candidate, StrategyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);

        var contract = candidate.Contract;
        var now = _time.GetUtcNow();

        var daysToExpiration = contract.Expiration.DayNumber
                               - DateOnly.FromDateTime(now.UtcDateTime).DayNumber;

        if (daysToExpiration < policy.MinDaysToExpiration
            || daysToExpiration > policy.MaxDaysToExpiration)
        {
            return RiskVerdict.Reject($"{daysToExpiration} days to expiration is outside the policy window");
        }

        if (daysToExpiration < _options.HardMinDaysToExpiration
            || daysToExpiration > _options.HardMaxDaysToExpiration)
        {
            return RiskVerdict.Reject($"{daysToExpiration} days to expiration is outside the hard window");
        }

        // The position must be closable before Alpaca takes its final equity reading.
        var expiresUtc = contract.Expiration.ToDateTime(TimeOnly.MinValue);
        if (expiresUtc > _options.CompetitionFlattenUtc.UtcDateTime.Date.AddDays(1))
        {
            return RiskVerdict.Reject("expires after the competition measurement point");
        }

        if (contract.Quality != QuoteQuality.TwoSided)
        {
            return RiskVerdict.Reject($"quote is {contract.Quality}");
        }

        if (!contract.IsTradeableQuote)
        {
            return RiskVerdict.Reject("quote is not tradeable");
        }

        if (contract.Ask is not { } ask || ask <= 0m || contract.Spread is not { } spread)
        {
            return RiskVerdict.Reject("quote is incomplete");
        }

        if (spread / ask > _options.MaxSpreadFraction)
        {
            return RiskVerdict.Reject($"spread {spread / ask:P1} is too wide");
        }

        if (contract.QuoteTimestampUtc is not { } quotedAt)
        {
            return RiskVerdict.Reject("quote has no timestamp");
        }

        if (!_options.AllowStaleQuotes && now - quotedAt > _options.MaxQuoteAge)
        {
            return RiskVerdict.Reject($"quote is {(now - quotedAt).TotalMinutes:N0} minutes old");
        }

        return RiskVerdict.Allow();
    }

    /// <summary>
    /// Whether a position must close now, regardless of what the agent thinks.
    /// </summary>
    /// <remarks>
    /// Deterministic exits run before the agent is consulted. The take-profit and stop-loss
    /// levels come from the policy the agent wrote; the competition flatten does not, because
    /// missing the measurement point cannot be recovered from.
    /// </remarks>
    public string? MandatoryExitReason(
        PositionState position, StrategyPolicy policy, decimal? currentPrice)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(policy);

        if (_time.GetUtcNow() >= _options.CompetitionFlattenUtc)
        {
            return "competition flatten";
        }

        if (currentPrice is not { } price || price <= 0m || position.AverageEntryPrice <= 0m)
        {
            return null;   // Cannot judge without a price. Hold rather than close blindly.
        }

        var change = (price - position.AverageEntryPrice) / position.AverageEntryPrice;

        if (change >= policy.TakeProfitFraction)
        {
            return $"take profit at {change:P0}";
        }

        if (change <= -policy.StopLossFraction)
        {
            return $"stop loss at {change:P0}";
        }

        return null;
    }
}
