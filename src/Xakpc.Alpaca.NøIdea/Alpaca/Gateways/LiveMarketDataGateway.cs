using Alpaca.Markets;

namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>
/// <see cref="IMarketDataGateway"/> over the typed Alpaca SDK clients.
/// </summary>
/// <remarks>
/// This class is the only place SDK market-data types are converted into project records. No
/// request names a feed; the account default applies (ADR-010).
/// </remarks>
public sealed class LiveMarketDataGateway(AlpacaClients clients) : IMarketDataGateway
{
    private readonly AlpacaClients _clients = clients ?? throw new ArgumentNullException(nameof(clients));

    public async Task<MarketClock> GetClockAsync(CancellationToken cancellationToken)
    {
        var clock = await _clients.Trading.GetClockAsync(cancellationToken);
        return new MarketClock(clock.TimestampUtc, clock.IsOpen, clock.NextOpenUtc, clock.NextCloseUtc);
    }

    public async Task<LatestTrade> GetLatestTradeAsync(string symbol, CancellationToken cancellationToken)
    {
        var trade = await _clients.StockData.GetLatestTradeAsync(
            new LatestMarketDataRequest(symbol), cancellationToken);

        return new LatestTrade(symbol, trade.Price, trade.TimestampUtc);
    }

    public async Task<IReadOnlyList<PriceBar>> GetBarsAsync(
        string symbol,
        string timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var page = await _clients.StockData.ListHistoricalBarsAsync(
            new HistoricalBarsRequest(symbol, from.UtcDateTime, to.UtcDateTime, ParseTimeFrame(timeframe)), cancellationToken);

        return page.Items
            .Select(bar => new PriceBar(
                symbol, bar.TimeUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume))
            .ToArray();
    }

    public async Task<IReadOnlyList<OptionCandidate>> GetOptionCandidatesAsync(
        OptionChainQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var request = new OptionChainRequest(query.Underlying);

        if (query.OptionType is not null)
        {
            request.OptionType = query.OptionType.Equals("put", StringComparison.OrdinalIgnoreCase)
                ? OptionType.Put
                : OptionType.Call;
        }

        if (query.ExpirationFrom is { } expirationFrom)
        {
            request.ExpirationDateGreaterThanOrEqualTo = expirationFrom;
        }

        if (query.ExpirationTo is { } expirationTo)
        {
            request.ExpirationDateLessThanOrEqualTo = expirationTo;
        }

        if (query.StrikeFrom is { } strikeFrom)
        {
            request.StrikePriceGreaterThanOrEqualTo = strikeFrom;
        }

        if (query.StrikeTo is { } strikeTo)
        {
            request.StrikePriceLessThanOrEqualTo = strikeTo;
        }

        request.Pagination.Size = 1_000;
        var candidates = new List<OptionCandidate>();

        do
        {
            var chain = await _clients.OptionsData.GetOptionChainAsync(request, cancellationToken);

            foreach (var (contractSymbol, snapshot) in chain.Items)
            {
                if (!OccOptionSymbol.TryParse(contractSymbol, out var parsed))
                {
                    continue;
                }

                var quote = snapshot.Quote;
                var quality = quote is null
                    ? QuoteQuality.Missing
                    : quote.BidPrice > 0 && quote.AskPrice > 0
                        ? QuoteQuality.TwoSided
                        : quote.BidPrice > 0 || quote.AskPrice > 0
                            ? QuoteQuality.OneSided
                            : QuoteQuality.Missing;

                candidates.Add(new OptionCandidate
                {
                    ContractSymbol = contractSymbol,
                    Underlying = parsed.Underlying,
                    OptionType = parsed.IsCall ? "call" : "put",
                    Strike = parsed.Strike,
                    Expiration = parsed.Expiration,
                    Quality = quality,
                    Bid = quote?.BidPrice,
                    Ask = quote?.AskPrice,
                    QuoteTimestampUtc = quote?.TimestampUtc,
                    Delta = snapshot.Greeks?.Delta,
                    ImpliedVolatility = snapshot.ImpliedVolatility,
                });
            }

            request.Pagination.Token = chain.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(request.Pagination.Token));

        return candidates;
    }

    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(
        IReadOnlyCollection<string> symbols,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var request = new NewsArticlesRequest(symbols)
        {
            SortDirection = SortDirection.Descending,
        };

        request.TimeInterval = new Interval<DateTime>(from.UtcDateTime, to.UtcDateTime);
        request.Pagination.Size = (uint)Math.Clamp(limit, 1, 50);

        var items = new List<NewsItem>(limit);
        do
        {
            var page = await _clients.StockData.ListNewsArticlesAsync(request, cancellationToken);
            items.AddRange(page.Items.Select(article => new NewsItem(
                article.Id,
                article.CreatedAtUtc,
                article.Headline ?? "",
                article.Summary,
                article.Source,
                article.Symbols.ToArray())));
            request.Pagination.Token = page.NextPageToken;
        }
        while (items.Count < limit && !string.IsNullOrWhiteSpace(request.Pagination.Token));

        return [.. items.Take(limit)];
    }

    private static BarTimeFrame ParseTimeFrame(string timeframe) => timeframe switch
    {
        "1Min" => BarTimeFrame.Minute,
        "15Min" => new BarTimeFrame(15, BarTimeFrameUnit.Minute),
        "1Hour" => BarTimeFrame.Hour,
        "1Day" => BarTimeFrame.Day,
        _ => throw new ArgumentOutOfRangeException(
            nameof(timeframe), timeframe, "Unsupported timeframe."),
    };
}
