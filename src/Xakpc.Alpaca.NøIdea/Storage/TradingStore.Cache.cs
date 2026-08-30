using System.Text.Json;
using Dapper;

namespace Xakpc.Alpaca.NøIdea.Storage;

/// <summary>One cached underlying bar.</summary>
/// <remarks>
/// Init-only properties, not a positional record: SQLite returns REAL as double and INTEGER
/// as long, and Dapper converts those per property setter but not through strict constructor
/// matching. The same rule holds for every record in this file.
/// </remarks>
public sealed record BarRow
{
    public string Symbol { get; init; } = "";
    public string Timeframe { get; init; } = "";
    public long TimestampUtc { get; init; }

    /// <summary>When this bar became knowable. Replay filters on this, never on the timestamp.</summary>
    public long AvailableUtc { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public double Volume { get; init; }
    public double? TradeCount { get; init; }
    public decimal? Vwap { get; init; }
}

/// <summary>One cached news item. <see cref="Symbols"/> fans out to <c>news_symbols</c>.</summary>
public sealed record NewsRow
{
    public long Id { get; init; }
    public long PublishedUtc { get; init; }
    public string Headline { get; init; } = "";
    public string? Summary { get; init; }
    public string? Source { get; init; }
    public string? Author { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = [];
}

/// <summary>One contract from the expired-contract catalog.</summary>
public sealed record OptionContractRow
{
    public string ContractSymbol { get; init; } = "";
    public string Underlying { get; init; } = "";
    public string Expiration { get; init; } = "";
    public decimal Strike { get; init; }
    public string OptionType { get; init; } = "";
    public string? Style { get; init; }
    public int? Multiplier { get; init; }
}

/// <summary>
/// One daily option bar. There is no bid, ask, or delta: Alpaca serves none of them for
/// history. See <c>.lode/replay/option-data-availability.md</c>.
/// </summary>
public sealed record OptionBarRow
{
    public string ContractSymbol { get; init; } = "";
    public long SessionUtc { get; init; }

    /// <summary>When this bar became knowable. Replay filters on this, never on the session.</summary>
    public long AvailableUtc { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public double Volume { get; init; }
    public double? TradeCount { get; init; }
    public decimal? Vwap { get; init; }
}

/// <summary>
/// The cache half of the store: the historical data replay reads instead of calling Alpaca.
/// </summary>
/// <remarks>
/// Every write is <c>INSERT OR REPLACE</c> against a composite primary key, so importing the
/// same raw files twice produces the same rows and the same counts.
/// </remarks>
public sealed partial class TradingStore
{
    public async Task<long> UpsertBarsAsync(
        IReadOnlyCollection<BarRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        const string sql =
            """
            INSERT OR REPLACE INTO bars (
                symbol, timeframe, timestamp_utc, available_utc, open, high, low, close, volume,
                trade_count, vwap)
            VALUES (
                @Symbol, @Timeframe, @TimestampUtc, @AvailableUtc, @Open, @High, @Low, @Close, @Volume,
                @TradeCount, @Vwap)
            """;

        return await ExecuteBatchAsync(sql, rows, cancellationToken);
    }

    public async Task<long> UpsertNewsAsync(
        IReadOnlyCollection<NewsRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        const string newsSql =
            """
            INSERT OR REPLACE INTO news (
                id, published_utc, headline, summary, source, author, url, symbols_json)
            VALUES (
                @Id, @PublishedUtc, @Headline, @Summary, @Source, @Author, @Url, @SymbolsJson)
            """;

        const string linkSql =
            """
            INSERT OR REPLACE INTO news_symbols (news_id, symbol)
            VALUES (@NewsId, @Symbol)
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long written = 0;
        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                newsSql,
                new
                {
                    row.Id,
                    row.PublishedUtc,
                    row.Headline,
                    row.Summary,
                    row.Source,
                    row.Author,
                    row.Url,
                    SymbolsJson = JsonSerializer.Serialize(row.Symbols),
                },
                transaction,
                cancellationToken: cancellationToken));

            foreach (var symbol in row.Symbols)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    linkSql, new { NewsId = row.Id, Symbol = symbol },
                    transaction, cancellationToken: cancellationToken));
            }

            written++;
        }

        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    public async Task<long> UpsertOptionContractsAsync(
        IReadOnlyCollection<OptionContractRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        const string sql =
            """
            INSERT OR REPLACE INTO option_contracts (
                contract_symbol, underlying, expiration, strike, option_type, style, multiplier)
            VALUES (
                @ContractSymbol, @Underlying, @Expiration, @Strike, @OptionType, @Style, @Multiplier)
            """;

        return await ExecuteBatchAsync(sql, rows, cancellationToken);
    }

    public async Task<long> UpsertOptionBarsAsync(
        IReadOnlyCollection<OptionBarRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        const string sql =
            """
            INSERT OR REPLACE INTO option_bars (
                contract_symbol, session_utc, available_utc, open, high, low, close, volume, trade_count, vwap)
            VALUES (
                @ContractSymbol, @SessionUtc, @AvailableUtc, @Open, @High, @Low, @Close, @Volume, @TradeCount, @Vwap)
            """;

        return await ExecuteBatchAsync(sql, rows, cancellationToken);
    }

    /// <summary>Row counts per cache table, so an import can be proved idempotent.</summary>
    public async Task<IReadOnlyDictionary<string, long>> CacheRowCountsAsync(
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT 'bars' AS name, COUNT(*) AS total FROM bars
            UNION ALL SELECT 'news', COUNT(*) FROM news
            UNION ALL SELECT 'news_symbols', COUNT(*) FROM news_symbols
            UNION ALL SELECT 'option_contracts', COUNT(*) FROM option_contracts
            UNION ALL SELECT 'option_bars', COUNT(*) FROM option_bars
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string Name, long Total)>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));

        return rows.ToDictionary(row => row.Name, row => row.Total, StringComparer.Ordinal);
    }

    /// <summary>
    /// One transaction for the whole batch. Without it SQLite commits per row and a
    /// million-row import takes minutes rather than seconds.
    /// </summary>
    private async Task<long> ExecuteBatchAsync<T>(
        string sql, IReadOnlyCollection<T> rows, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            sql, rows, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return rows.Count;
    }

    /// <summary>Records one equity snapshot. Replay and live rows stay separate by mode.</summary>
    public async Task RecordEquityAsync(
        long timestampUtc,
        string mode,
        decimal equity,
        decimal cash,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT OR REPLACE INTO equity_snapshots (timestamp_utc, mode, equity, cash)
            VALUES (@timestampUtc, @mode, @equity, @cash)
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { timestampUtc, mode, equity, cash },
            cancellationToken: cancellationToken));
    }
}
