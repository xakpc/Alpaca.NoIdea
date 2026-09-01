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

    public decimal StartingEquity { get; init; } = 100_000m;

    /// <summary>An API-size boundary, not a statement that these strikes are attractive.</summary>
    public decimal OptionScanMaxMoneynessFraction { get; init; } = 0.20m;

    public TimeSpan HeadlineLookback { get; init; } = TimeSpan.FromHours(48);

    public int HeadlineLimit { get; init; } = 25;

    public int MaxHeadlinesPerSymbol { get; init; } = 3;

    public int InlineCatalogCharacterLimit { get; init; } = 60_000;

    public int CatalogToolPageSize { get; init; } = 200;
}
