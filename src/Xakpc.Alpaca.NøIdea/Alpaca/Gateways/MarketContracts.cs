namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>
/// How much a caller is allowed to believe about an option quote.
/// </summary>
/// <remarks>
/// <para>
/// This exists so replay cannot pass a check it is incapable of making. Alpaca serves no
/// historical option quote and no historical greek, so a replayed contract has a close price
/// and nothing else — no bid, no ask, no spread, no quote age. A nullable bid and ask would
/// let a spread rule read <c>null</c> and skip, or worse, read a default and pass.
/// </para>
/// <para>
/// The Options Evaluator must therefore treat <see cref="UnknownHistorical"/> as "this rule
/// does not apply here", and must never treat it as a passing quote. Quote-quality rules are
/// testable in live paper trading only. See
/// <c>.lode/replay/option-data-availability.md</c>.
/// </para>
/// </remarks>
public enum QuoteQuality
{
    /// <summary>No quote at all. Fail closed.</summary>
    Missing = 0,

    /// <summary>A bid or an ask, but not both. Fail closed.</summary>
    OneSided = 1,

    /// <summary>A live two-sided quote. The only value a spread rule can judge.</summary>
    TwoSided = 2,

    /// <summary>
    /// A historical close price with no quote behind it. Not tradeable, and not judgeable by
    /// any spread or quote-age rule.
    /// </summary>
    UnknownHistorical = 3,
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

/// <summary>One news item, live or replayed.</summary>
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
/// <remarks>
/// <see cref="Quality"/> governs which fields carry meaning. Under
/// <see cref="QuoteQuality.UnknownHistorical"/>, <see cref="Bid"/>, <see cref="Ask"/> and
/// <see cref="Delta"/> are all null and <see cref="ReferencePrice"/> holds the session close.
/// </remarks>
public sealed record OptionCandidate
{
    public required string ContractSymbol { get; init; }
    public required string Underlying { get; init; }
    public required string OptionType { get; init; }
    public required decimal Strike { get; init; }
    public required DateOnly Expiration { get; init; }
    public required QuoteQuality Quality { get; init; }

    /// <summary>The price a caller should reason about: the ask when live, the close in replay.</summary>
    public required decimal ReferencePrice { get; init; }

    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public DateTimeOffset? QuoteTimestampUtc { get; init; }

    /// <summary>Live only. Alpaca serves no historical greek.</summary>
    public decimal? Delta { get; init; }

    /// <summary>Live option snapshot metadata. Missing IV does not make a contract untradeable.</summary>
    public decimal? ImpliedVolatility { get; init; }

    public decimal? Spread => Bid is { } bid && Ask is { } ask ? ask - bid : null;

    /// <summary>
    /// True only for a live two-sided quote with a positive bid and a non-crossed ask. A
    /// historical candidate is never tradeable, which is what keeps replay honest about the
    /// checks it cannot run.
    /// </summary>
    public bool IsTradeableQuote =>
        Quality == QuoteQuality.TwoSided && Bid > 0 && Ask >= Bid;
}
