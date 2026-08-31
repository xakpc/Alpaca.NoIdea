namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>
/// Everything the strategy reads from the live Alpaca market-data service.
/// </summary>
/// <remarks>
/// <para>
/// The contract returns project-owned records rather than <c>Alpaca.Markets</c> interfaces.
/// Project-owned records keep Alpaca SDK types at the gateway boundary and make the live
/// trading loop easy to test.
/// </para>
/// <para>
/// No method names a market-data feed. The account default applies (ADR-010).
/// </para>
/// </remarks>
public interface IMarketDataGateway
{
    /// <summary>Current exchange state.</summary>
    Task<MarketClock> GetClockAsync(CancellationToken cancellationToken);

    Task<LatestTrade> GetLatestTradeAsync(string symbol, CancellationToken cancellationToken);

    /// <summary>
    /// Bars in <c>[from, to)</c>.
    /// </summary>
    Task<IReadOnlyList<PriceBar>> GetBarsAsync(
        string symbol,
        string timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>
    /// Candidate contracts for one underlying. Callers filter further; this returns what the
    /// data source can offer inside the strike and expiration bounds.
    /// </summary>
    Task<IReadOnlyList<OptionCandidate>> GetOptionCandidatesAsync(
        OptionChainQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// News for the symbols, newest first. This is the alpha channel after ADR-013, so it is
    /// a first-class read.
    /// </summary>
    Task<IReadOnlyList<NewsItem>> GetNewsAsync(
        IReadOnlyCollection<string> symbols,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>The bounds of one option-chain read.</summary>
public sealed record OptionChainQuery
{
    public required string Underlying { get; init; }

    /// <summary>call, put, or null for both.</summary>
    public string? OptionType { get; init; }

    public DateOnly? ExpirationFrom { get; init; }
    public DateOnly? ExpirationTo { get; init; }
    public decimal? StrikeFrom { get; init; }
    public decimal? StrikeTo { get; init; }
}
