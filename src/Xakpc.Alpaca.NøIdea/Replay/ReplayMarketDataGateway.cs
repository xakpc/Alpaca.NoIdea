using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Replay;

/// <summary>
/// <see cref="IMarketDataGateway"/> over the SQLite cache, clamped to the replay clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>The no-leak rule lives here.</b> Every query carries
/// <c>available_utc &lt;= @asOf</c> where <c>asOf</c> comes from
/// <see cref="ReplayClock.UtcNow"/> and never from the caller. A caller that asks for a range
/// ending next week still sees nothing past the replay instant, so a leak cannot be
/// introduced by passing a wider argument.
/// </para>
/// <para>
/// The filter is <c>available_utc</c> and never <c>timestamp_utc</c>. A bar timestamp is the
/// START of its interval while the bar carries the interval CLOSE, so a daily bar stamped
/// 05:00Z holds a 16:00 ET price. Filtering on the timestamp would hand a 09:30 cycle most of
/// a session of the future. See <see cref="Storage.BarAvailability"/>.
/// </para>
/// <para>
/// This gateway opens no network connection. A replay run that reaches Alpaca is a test
/// failure (<c>.lode/replay/replay-mode.md</c>).
/// </para>
/// </remarks>
public sealed class ReplayMarketDataGateway(string connectionString, ReplayClock clock) : IMarketDataGateway
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    private readonly ReplayClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The no-leak boundary, in the units the cache tables store.</summary>
    private long AsOf => _clock.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Derived from the clock, not from Alpaca. Regular US hours are 09:30 to 16:00 Eastern;
    /// the zone carries daylight saving, so no fixed offset appears here.
    /// </summary>
    public Task<MarketClock> GetClockAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var eastern = TimeZoneInfo.ConvertTime(now.UtcDateTime, TimeZoneInfo.Utc, MarketCalendar.Eastern);
        var isOpen = MarketCalendar.IsRegularHours(now);

        var openToday = eastern.Date.AddHours(9).AddMinutes(30);
        var closeToday = eastern.Date.AddHours(16);

        return Task.FromResult(new MarketClock(
            now,
            isOpen,
            MarketCalendar.ToUtc(eastern < openToday ? openToday : NextSessionOpen(openToday)),
            MarketCalendar.ToUtc(closeToday)));
    }

    public async Task<LatestTrade> GetLatestTradeAsync(string symbol, CancellationToken cancellationToken)
    {
        // The most recent close at or before the replay instant. There is no tick data in the
        // cache, so a bar close is the best available stand-in for a last trade.
        const string sql =
            """
            SELECT close AS Close, timestamp_utc AS TimestampUtc
            FROM bars
            WHERE symbol = @symbol AND timeframe = @timeframe AND available_utc <= @asOf
            ORDER BY timestamp_utc DESC
            LIMIT 1
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(decimal Close, long TimestampUtc)?>(
            new CommandDefinition(
                sql, new { symbol, timeframe = "15Min", asOf = AsOf }, cancellationToken: cancellationToken));

        if (row is not { } bar)
        {
            throw new InvalidOperationException(
                $"No cached bar for {symbol} at or before {_clock.UtcNow:u}. Import the history first.");
        }

        return new LatestTrade(symbol, bar.Close, DateTimeOffset.FromUnixTimeSeconds(bar.TimestampUtc));
    }

    public async Task<IReadOnlyList<PriceBar>> GetBarsAsync(
        string symbol,
        string timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        // `available_utc <= @asOf` is not redundant with @to: the caller controls @to and the
        // clock controls @asOf. Only the second one is a guarantee.
        const string sql =
            """
            SELECT timestamp_utc AS TimestampUtc, open AS Open, high AS High,
                   low AS Low, close AS Close, volume AS Volume
            FROM bars
            WHERE symbol = @symbol
              AND timeframe = @timeframe
              AND timestamp_utc >= @from
              AND timestamp_utc < @to
              AND available_utc <= @asOf
            ORDER BY timestamp_utc
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(long TimestampUtc, decimal Open, decimal High,
            decimal Low, decimal Close, double Volume)>(new CommandDefinition(
            sql,
            new
            {
                symbol,
                timeframe,
                from = from.ToUnixTimeSeconds(),
                to = to.ToUnixTimeSeconds(),
                asOf = AsOf,
            },
            cancellationToken: cancellationToken));

        return rows
            .Select(row => new PriceBar(
                symbol,
                DateTimeOffset.FromUnixTimeSeconds(row.TimestampUtc),
                row.Open, row.High, row.Low, row.Close, (decimal)row.Volume))
            .ToArray();
    }

    /// <summary>
    /// Rebuilds an option chain from the contract catalog joined to the most recent daily
    /// option bar at or before the replay instant.
    /// </summary>
    /// <remarks>
    /// Every candidate carries <see cref="QuoteQuality.UnknownHistorical"/> and a null bid,
    /// ask and delta, because none of those exist in history. That is deliberate: it stops a
    /// spread rule or a quote-age rule from silently passing on data that cannot support it.
    /// </remarks>
    public async Task<IReadOnlyList<OptionCandidate>> GetOptionCandidatesAsync(
        OptionChainQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        const string sql =
            """
            SELECT c.contract_symbol AS ContractSymbol,
                   c.underlying      AS Underlying,
                   c.option_type     AS OptionType,
                   c.strike          AS Strike,
                   c.expiration      AS Expiration,
                   b.close           AS Close,
                   b.session_utc     AS SessionUtc
            FROM option_contracts c
            JOIN option_bars b ON b.contract_symbol = c.contract_symbol
            WHERE c.underlying = @underlying
              AND (@optionType IS NULL OR c.option_type = @optionType)
              AND (@expirationFrom IS NULL OR c.expiration >= @expirationFrom)
              AND (@expirationTo IS NULL OR c.expiration <= @expirationTo)
              AND (@strikeFrom IS NULL OR c.strike >= @strikeFrom)
              AND (@strikeTo IS NULL OR c.strike <= @strikeTo)
              AND b.available_utc <= @asOf
              AND b.session_utc = (
                    SELECT MAX(b2.session_utc) FROM option_bars b2
                    WHERE b2.contract_symbol = c.contract_symbol AND b2.available_utc <= @asOf)
            ORDER BY c.expiration, c.strike
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ChainRow>(new CommandDefinition(
            sql,
            new
            {
                underlying = query.Underlying,
                optionType = query.OptionType,
                expirationFrom = query.ExpirationFrom?.ToString("yyyy-MM-dd"),
                expirationTo = query.ExpirationTo?.ToString("yyyy-MM-dd"),
                strikeFrom = query.StrikeFrom,
                strikeTo = query.StrikeTo,
                asOf = AsOf,
            },
            cancellationToken: cancellationToken));

        return rows
            .Select(row => new OptionCandidate
            {
                ContractSymbol = row.ContractSymbol,
                Underlying = row.Underlying,
                OptionType = row.OptionType,
                Strike = row.Strike,
                Expiration = DateOnly.Parse(row.Expiration),
                Quality = QuoteQuality.UnknownHistorical,
                ReferencePrice = row.Close,
                QuoteTimestampUtc = DateTimeOffset.FromUnixTimeSeconds(row.SessionUtc),
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(
        IReadOnlyCollection<string> symbols,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        if (symbols.Count == 0)
        {
            return [];
        }

        const string sql =
            """
            SELECT DISTINCT n.id AS Id, n.published_utc AS PublishedUtc, n.headline AS Headline,
                   n.summary AS Summary, n.source AS Source, n.symbols_json AS SymbolsJson
            FROM news n
            JOIN news_symbols s ON s.news_id = n.id
            WHERE s.symbol IN @symbols
              AND n.published_utc >= @from
              AND n.published_utc < @to
              AND n.published_utc <= @asOf
            ORDER BY n.published_utc DESC
            LIMIT @limit
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<NewsQueryRow>(new CommandDefinition(
            sql,
            new
            {
                symbols,
                from = from.ToUnixTimeSeconds(),
                to = to.ToUnixTimeSeconds(),
                asOf = AsOf,
                limit,
            },
            cancellationToken: cancellationToken));

        return rows
            .Select(row => new NewsItem(
                row.Id,
                DateTimeOffset.FromUnixTimeSeconds(row.PublishedUtc),
                row.Headline,
                row.Summary,
                row.Source,
                JsonSerializer.Deserialize<string[]>(row.SymbolsJson) ?? []))
            .ToArray();
    }

    private static DateTime NextSessionOpen(DateTime openToday)
    {
        var next = openToday.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }

        return next;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed record ChainRow
    {
        public string ContractSymbol { get; init; } = "";
        public string Underlying { get; init; } = "";
        public string OptionType { get; init; } = "";
        public decimal Strike { get; init; }
        public string Expiration { get; init; } = "";
        public decimal Close { get; init; }
        public long SessionUtc { get; init; }
    }

    private sealed record NewsQueryRow
    {
        public long Id { get; init; }
        public long PublishedUtc { get; init; }
        public string Headline { get; init; } = "";
        public string? Summary { get; init; }
        public string? Source { get; init; }
        public string SymbolsJson { get; init; } = "[]";
    }
}
