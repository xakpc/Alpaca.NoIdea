namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>How much a caller is allowed to believe about a live option quote.</summary>
public enum QuoteQuality
{
    /// <summary>No quote at all. Fail closed.</summary>
    Missing = 0,

    /// <summary>A bid or an ask, but not both. Fail closed.</summary>
    OneSided = 1,

    /// <summary>A two-sided quote. The only value a spread rule can judge.</summary>
    TwoSided = 2,
}

/// <summary>One underlying price bar.</summary>
public sealed record PriceBar(
    string Symbol,
    DateTimeOffset TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>The last trade for an underlying.</summary>
public sealed record LatestTrade(string Symbol, decimal Price, DateTimeOffset TimestampUtc);

/// <summary>Whether the exchange is open, and when it opens next.</summary>
public sealed record MarketClock(
    DateTimeOffset NowUtc,
    bool IsOpen,
    DateTimeOffset NextOpenUtc,
    DateTimeOffset NextCloseUtc);

/// <summary>One current news item.</summary>
public sealed record NewsItem(
    long Id,
    DateTimeOffset PublishedUtc,
    string Headline,
    string? Summary,
    string? Source,
    IReadOnlyList<string> Symbols);

/// <summary>
/// One option contract offered for evaluation, with everything the evaluator may read.
/// </summary>
public sealed record OptionCandidate
{
    public required string ContractSymbol { get; init; }
    public required string Underlying { get; init; }
    public required string OptionType { get; init; }
    public required decimal Strike { get; init; }
    public required DateOnly Expiration { get; init; }
    public required QuoteQuality Quality { get; init; }

    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public DateTimeOffset? QuoteTimestampUtc { get; init; }

    public decimal? Delta { get; init; }

    /// <summary>Live option snapshot metadata. Missing IV does not make a contract untradeable.</summary>
    public decimal? ImpliedVolatility { get; init; }

    public decimal? Spread => Bid is { } bid && Ask is { } ask ? ask - bid : null;

    /// <summary>
    /// True only for a two-sided quote with a positive bid and a non-crossed ask.
    /// </summary>
    public bool IsTradeableQuote =>
        Quality == QuoteQuality.TwoSided && Bid > 0 && Ask >= Bid;
}
