namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>
/// The strategy the agent is currently running. <b>The agent may rewrite this.</b>
/// </summary>
/// <remarks>
/// <para>
/// These are the numbers that <c>.lode/trading/strategy-parameters.md</c> lists as TBD. No
/// replay evidence sets them, because ADR-013 left the system with no forecaster that beats
/// the option price, so there was nothing to calibrate a threshold against.
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
    public int MinDaysToExpiration { get; init; } = 2;

    public int MaxDaysToExpiration { get; init; } = 10;

    /// <summary>
    /// The tradeable band of market probability, from the option ladder. A contract that is
    /// nearly certain either way is not worth an LLM call or a trade: the price already
    /// reflects the outcome.
    /// </summary>
    public decimal MinMarketProbability { get; init; } = 0.20m;

    public decimal MaxMarketProbability { get; init; } = 0.80m;

    /// <summary>Close a winner at this gain over the entry premium.</summary>
    public decimal TakeProfitFraction { get; init; } = 0.50m;

    /// <summary>Close a loser at this loss against the entry premium.</summary>
    public decimal StopLossFraction { get; init; } = 0.40m;

    public int MaxContractsPerTrade { get; init; } = 1;

    /// <summary>
    /// Whether a symbol must have recent news before the agent will consider it. After
    /// ADR-013 the agents reading news text are the only remaining alpha hypothesis, so the
    /// budget should go where text exists.
    /// </summary>
    public bool RequireFreshNews { get; init; } = true;

    public int FreshNewsWithinHours { get; init; } = 48;

    /// <summary>Why the agent chose this policy. Recorded in the audit trail.</summary>
    public string Rationale { get; init; } = "Opening defaults. Chosen, not measured.";

    /// <summary>
    /// Forces the policy inside the hard bounds and repairs an incoherent one.
    /// </summary>
    /// <remarks>
    /// Called on every policy the agent produces, before anything reads it. An agent that asks
    /// for a 90-day expiration, twenty contracts, or an inverted probability band gets a
    /// clamped policy rather than a rejected cycle — the run continues, more conservatively
    /// than asked, and the clamp is recorded.
    /// </remarks>
    public StrategyPolicy ClampTo(RiskOptions risk)
    {
        ArgumentNullException.ThrowIfNull(risk);

        var minDte = Math.Clamp(
            MinDaysToExpiration, risk.HardMinDaysToExpiration, risk.HardMaxDaysToExpiration);

        var maxDte = Math.Clamp(
            MaxDaysToExpiration, risk.HardMinDaysToExpiration, risk.HardMaxDaysToExpiration);

        // An inverted window would select nothing and look like a data fault later.
        if (maxDte < minDte)
        {
            (minDte, maxDte) = (maxDte, minDte);
        }

        var minProbability = Math.Clamp(MinMarketProbability, 0m, 1m);
        var maxProbability = Math.Clamp(MaxMarketProbability, 0m, 1m);

        if (maxProbability < minProbability)
        {
            (minProbability, maxProbability) = (maxProbability, minProbability);
        }

        return this with
        {
            MinDaysToExpiration = minDte,
            MaxDaysToExpiration = maxDte,
            MinMarketProbability = minProbability,
            MaxMarketProbability = maxProbability,
            // A non-positive take-profit or stop would close every position at once.
            TakeProfitFraction = Math.Clamp(TakeProfitFraction, 0.05m, 10m),
            StopLossFraction = Math.Clamp(StopLossFraction, 0.05m, 1m),
            MaxContractsPerTrade = Math.Clamp(MaxContractsPerTrade, 1, risk.HardMaxContractsPerTrade),
            FreshNewsWithinHours = Math.Clamp(FreshNewsWithinHours, 1, 24 * 14),
        };
    }

    /// <summary>True when this policy differs from the one the agent was given.</summary>
    public bool DiffersFrom(StrategyPolicy other) => this != other;
}
