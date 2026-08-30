namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>
/// The hard limits. <b>An LLM cannot change any value in this record.</b>
/// </summary>
/// <remarks>
/// <para>
/// These are the bounds the agent operates inside. The agent chooses direction, contract, and
/// timing; this decides how much of the account it can ever put at risk. The split is the core
/// rule of the project: AI can research and forecast, deterministic C# controls risk and money
/// (ADR-006).
/// </para>
/// <para>
/// A <see cref="StrategyPolicy"/> the agent writes is clamped to these bounds before it is
/// used, so a policy revision can only ever make the system <em>more</em> conservative than
/// this record allows.
/// </para>
/// <para>
/// The fractional limits are <b>chosen, not measured</b>. No replay evidence sets them,
/// because risk appetite is a decision rather than a prediction. They are recorded as chosen
/// in <c>.lode/trading/strategy-parameters.md</c>.
/// </para>
/// </remarks>
public sealed record RiskOptions
{
    /// <summary>The most one position may cost, as a fraction of account equity.</summary>
    /// <remarks>
    /// A long option cannot lose more than its premium, so the premium paid <em>is</em> the
    /// risk. That makes this limit exact rather than an estimate.
    /// </remarks>
    public decimal MaxRiskPerTradeFraction { get; init; } = 0.02m;

    /// <summary>The most every open position together may be worth at entry.</summary>
    public decimal MaxTotalRiskFraction { get; init; } = 0.10m;

    /// <summary>
    /// New positions stop for the day once equity falls this far below the day's opening
    /// equity. This is the circuit breaker: it fires no matter what the policy says, which is
    /// what stops a bad policy revision from compounding.
    /// </summary>
    public decimal MaxDailyLossFraction { get; init; } = 0.05m;

    public int MaxConcurrentPositions { get; init; } = 4;

    public int MaxNewPositionsPerDay { get; init; } = 4;

    /// <summary>The widest expiration window any policy may select inside.</summary>
    public int HardMinDaysToExpiration { get; init; } = 1;

    public int HardMaxDaysToExpiration { get; init; } = 21;

    /// <summary>The most contracts one order may carry, whatever the policy asks for.</summary>
    public int HardMaxContractsPerTrade { get; init; } = 5;

    /// <summary>
    /// The widest bid/ask spread that can still be traded, as a fraction of the ask. Live
    /// only: replay cannot measure a spread, because Alpaca serves no historical quote.
    /// </summary>
    public decimal MaxSpreadFraction { get; init; } = 0.15m;

    /// <summary>How old a quote may be before the candidate fails closed.</summary>
    public TimeSpan MaxQuoteAge { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Positions close before this moment, because Alpaca scores total equity at the end of
    /// Thursday 2026-09-03 and the system must not depend on Friday option activity.
    /// </summary>
    public DateTimeOffset CompetitionFlattenUtc { get; init; } =
        new(2026, 9, 3, 19, 30, 0, TimeSpan.Zero);   // 15:30 ET, half an hour before the close
}
