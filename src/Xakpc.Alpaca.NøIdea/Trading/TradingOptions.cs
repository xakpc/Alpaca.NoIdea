namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>What the loop watches, and how often.</summary>
public sealed record TradingOptions
{
    /// <summary>
    /// The fixed tracked universe. The runtime does not scan for new symbols. See
    /// <c>.lode/trading/summary.md</c> for the current catalog contract.
    /// </summary>
    public IReadOnlyList<string> TrackedSymbols { get; init; } =
    [
        "SPY", "QQQ", "IWM", "AAPL", "MSFT", "NVDA", "AMZN",
        "META", "GOOGL", "TSLA", "AMD", "MU", "INTC",
    ];

    /// <summary>How long the live loop waits between cycles.</summary>
    public TimeSpan CycleInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long to wait before another clock check after a recoverable cycle fault.</summary>
    public TimeSpan FaultRetryInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the live loop waits between deterministic exit passes.</summary>
    /// <remarks>
    /// <para>
    /// The stop-loss, the take-profit and the competition flatten are C# rules that consult no
    /// model, so they must not run at the war-room cadence. A pass costs two Alpaca reads and
    /// no model tokens, so it is paced by how fast a 1-to-3 day option can move, not by cost.
    /// </para>
    /// <para>
    /// The measured cycle spacing is 38 to 41 minutes. At that spacing a 40 percent stop on a
    /// 0.89 premium contract with delta -0.28 is a poll and not a stop: a 0.18 percent move in
    /// the underlying passes it, which is a matter of minutes. Alpaca has no stop order type
    /// for options and no bracket order class for options, so this loop is the only mechanism
    /// available. See <c>.lode/trading/hard-exit-loop.md</c>.
    /// </para>
    /// </remarks>
    public TimeSpan HardExitInterval { get; init; } = TimeSpan.FromMinutes(1);

    public decimal StartingEquity { get; init; } = 100_000m;

    /// <summary>An API-size boundary, not a statement that these strikes are attractive.</summary>
    public decimal OptionScanMaxMoneynessFraction { get; init; } = 0.20m;

    public TimeSpan HeadlineLookback { get; init; } = TimeSpan.FromHours(48);

    public int HeadlineLimit { get; init; } = 25;

    public int MaxHeadlinesPerSymbol { get; init; } = 3;

    public int InlineCatalogCharacterLimit { get; init; } = 60_000;

    public int CatalogToolPageSize { get; init; } = 200;
}
