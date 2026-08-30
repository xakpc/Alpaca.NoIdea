namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>What the loop watches, and how often.</summary>
public sealed record TradingOptions
{
    /// <summary>
    /// The tracked universe. <b>Measured, not chosen:</b> it is the output of the four
    /// admission rules in <c>.lode/trading/universe.md</c>. Rebuild it with
    /// <c>scripts/screen-universe.sh</c>; do not edit the list by hand.
    /// </summary>
    public IReadOnlyList<string> TrackedSymbols { get; init; } =
    [
        "SPY", "QQQ", "IWM", "AAPL", "MSFT", "NVDA", "AMZN",
        "META", "GOOGL", "TSLA", "AMD", "MU", "INTC",
    ];

    /// <summary>How long the live loop waits between cycles.</summary>
    public TimeSpan CycleInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long to wait between cycles while the exchange is closed. The loop stays up so a
    /// restart is not needed at the open, but it does not spend an agent call on a closed
    /// market.
    /// </summary>
    public TimeSpan ClosedMarketInterval { get; init; } = TimeSpan.FromMinutes(5);

    public decimal StartingEquity { get; init; } = 100_000m;
}
