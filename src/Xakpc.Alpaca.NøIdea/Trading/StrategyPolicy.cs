namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>
/// The strategy the agent is currently running. <b>The agent may rewrite this.</b>
/// </summary>
/// <remarks>
/// <para>
/// These defaults have no real-trade calibration. ADR-013 left the system with no historical
/// forecaster that beats the option price. See <c>.lode/trading/risk-guardrails.md</c>.
/// </para>
/// <para>
/// Rather than freeze guessed constants, the agent owns them and revises them from its own
/// measured results. <see cref="ClampTo"/> then forces the result inside
/// <see cref="RiskOptions"/>, so a revision can narrow the strategy but never widen the risk.
/// The defaults below are the opening position, and they are <b>chosen, not measured</b>.
/// </para>
/// </remarks>
public sealed record StrategyPolicy
{
    /// <summary>
    /// The shortest contract life the policy will select. One, not two, because the contest
    /// flatten bounds the holding period anyway: a two-day floor on the day before the flatten
    /// permits only contracts that outlive the contest, which is the opposite of what the
    /// floor is for.
    /// </summary>
    public int MinDaysToExpiration { get; init; } = 1;

    public int MaxDaysToExpiration { get; init; } = 10;

    /// <summary>Close a winner at this gain over the entry premium.</summary>
    public decimal TakeProfitFraction { get; init; } = 0.50m;

    /// <summary>Close a loser at this loss against the entry premium.</summary>
    public decimal StopLossFraction { get; init; } = 0.40m;

    public int MaxContractsPerTrade { get; init; } = 1;

    /// <summary>Why the agent chose this policy. Recorded in the audit trail.</summary>
    public string Rationale { get; init; } = "Opening defaults. Chosen, not measured.";

    /// <summary>
    /// Forces the policy inside the hard bounds and repairs an incoherent one.
    /// </summary>
    /// <remarks>
    /// Called on every policy the agent produces, before anything reads it. An agent that asks
    /// for a 90-day expiration, twenty contracts, or an inverted expiration window gets a
    /// clamped policy rather than a rejected cycle — the run continues, more conservatively
    /// than asked, and the clamp is recorded.
    /// </remarks>
    /// <param name="risk">The hard bounds. Nothing here can widen them.</param>
    /// <param name="today">
    /// The current date. When supplied, the expiration floor is additionally lowered to fit the
    /// contest: a policy must never demand more contract life than the flatten permits, or it
    /// selects nothing. Omit it to clamp against the hard bounds alone.
    /// </param>
    public StrategyPolicy ClampTo(RiskOptions risk, DateOnly? today = null)
    {
        ArgumentNullException.ThrowIfNull(risk);

        var minDte = Math.Clamp(
            MinDaysToExpiration, risk.HardMinDaysToExpiration, risk.HardMaxDaysToExpiration);

        // A policy loaded from SQLite carries whatever floor a previous run saved. Clamping to
        // the hard bounds cannot lower it, because the saved value is already inside them — so
        // a stale floor would quietly outlive the change that lowered the default. Bound it by
        // the contest instead. RiskGuard admits a contract expiring the day after the flatten,
        // so the floor may reach one day past it and no further.
        if (today is { } date)
        {
            var lastUsefulDay = DateOnly.FromDateTime(
                risk.CompetitionFlattenUtc.UtcDateTime.Date.AddDays(1));
            var lastUsefulDte = lastUsefulDay.DayNumber - date.DayNumber;

            minDte = Math.Clamp(
                Math.Min(minDte, Math.Max(lastUsefulDte, risk.HardMinDaysToExpiration)),
                risk.HardMinDaysToExpiration,
                risk.HardMaxDaysToExpiration);
        }

        var maxDte = Math.Clamp(
            MaxDaysToExpiration, risk.HardMinDaysToExpiration, risk.HardMaxDaysToExpiration);

        // An inverted window would select nothing and look like a data fault later.
        if (maxDte < minDte)
        {
            (minDte, maxDte) = (maxDte, minDte);
        }

        return this with
        {
            MinDaysToExpiration = minDte,
            MaxDaysToExpiration = maxDte,
            // A non-positive take-profit or stop would close every position at once.
            TakeProfitFraction = Math.Clamp(TakeProfitFraction, 0.05m, 10m),
            StopLossFraction = Math.Clamp(StopLossFraction, 0.05m, 1m),
            MaxContractsPerTrade = Math.Clamp(MaxContractsPerTrade, 1, risk.HardMaxContractsPerTrade),
        };
    }

    /// <summary>True when this policy differs from the one the agent was given.</summary>
    public bool DiffersFrom(StrategyPolicy other) => this != other;
}
