using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Storage;

/// <summary>How much of <c>data/raw</c> to load, and for which symbols.</summary>
/// <remarks>
/// The window exists because <c>data/raw/contracts</c> is 370 MB across 416 files and replay
/// needs one slice of it. Files are named <c>&lt;SYM&gt;.&lt;YYYY-MM&gt;.json</c> by
/// expiration month, so the filter runs on the file name and never opens a file outside the
/// window.
/// </remarks>
public sealed record ImportWindow
{
    /// <summary>The first session to load. Bars and news before this are skipped.</summary>
    public DateOnly From { get; init; } = new(2026, 2, 1);

    /// <summary>
    /// The last session to load. Never today: the account refuses SIP data at or after the
    /// current session, so the acquisition scripts stop at the last completed session and the
    /// import matches them.
    /// </summary>
    public DateOnly To { get; init; } = new(2026, 8, 28);

    /// <summary>
    /// Contracts expiring after <see cref="To"/> still matter, because a position opened
    /// inside the window can expire outside it.
    /// </summary>
    public int ExpirationSlackDays { get; init; } = 60;

    public IReadOnlyList<string> Symbols { get; init; } =
    [
        "SPY", "QQQ", "IWM", "AAPL", "MSFT", "NVDA", "AMZN",
        "META", "GOOGL", "TSLA", "AMD", "MU", "INTC",
    ];
}

/// <summary>Row counts from one import, so a caller can prove it was idempotent.</summary>
public sealed record ImportSummary
{
    public long Bars { get; init; }
    public long News { get; init; }
    public long OptionContracts { get; init; }
    public long OptionBars { get; init; }

    public override string ToString() =>
        $"{Bars:N0} bars, {News:N0} news, {OptionContracts:N0} contracts, {OptionBars:N0} option bars";
}

/// <summary>
/// Loads <c>data/raw</c> into the SQLite cache tables so replay never calls Alpaca for the
/// same history twice.
/// </summary>
/// <remarks>
/// Deterministic and offline: this class opens no network connection. Every insert is
/// <c>INSERT OR REPLACE</c> against a composite primary key, so running the import twice
/// leaves the same rows and the same counts. That is the property the verification step
/// checks.
/// </remarks>
public sealed class HistoryImporter(TradingStore store, ILogger logger)
{
    private readonly TradingStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<ImportSummary> ImportAsync(
        string rawDirectory,
        ImportWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!Directory.Exists(rawDirectory))
        {
            throw new DirectoryNotFoundException(
                $"No raw data at {rawDirectory}. Run the scripts in scripts/ first.");
        }

        var symbols = new HashSet<string>(window.Symbols, StringComparer.Ordinal);

        var bars = await ImportBarsAsync(rawDirectory, window, symbols, cancellationToken);
        var news = await ImportNewsAsync(rawDirectory, window, symbols, cancellationToken);
        var contracts = await ImportContractsAsync(rawDirectory, window, symbols, cancellationToken);
        var optionBars = await ImportOptionBarsAsync(rawDirectory, window, symbols, cancellationToken);

        var summary = new ImportSummary
        {
            Bars = bars,
            News = news,
            OptionContracts = contracts,
            OptionBars = optionBars,
        };

        _logger.LogInformation("Import complete: {Summary}.", summary);
        return summary;
    }

    // ------------------------------------------------------------------ bars

    private async Task<long> ImportBarsAsync(
        string raw, ImportWindow window, HashSet<string> symbols, CancellationToken ct)
    {
        var directory = Path.Combine(raw, "bars");
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("No bars directory at {Path}. Skipping.", directory);
            return 0;
        }

        var from = ToUnixSeconds(window.From);
        var to = ToUnixSeconds(window.To.AddDays(1));
        long total = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            // <SYM>.<timeframe>.json
            var parts = Path.GetFileNameWithoutExtension(path).Split('.');
            if (parts.Length != 2 || !symbols.Contains(parts[0]))
            {
                continue;
            }

            var symbol = parts[0];
            var timeframe = parts[1];
            var rows = new List<BarRow>(capacity: 8192);

            RawJsonPages.ForEachItem(path, "bars", bar =>
            {
                var timestamp = ParseInstant(bar.GetProperty("t").GetString()!);
                if (timestamp < from || timestamp >= to)
                {
                    return;
                }

                rows.Add(new BarRow
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    TimestampUtc = timestamp,
                    AvailableUtc = BarAvailability.ForBar(timestamp, timeframe),
                    Open = bar.GetProperty("o").GetDecimal(),
                    High = bar.GetProperty("h").GetDecimal(),
                    Low = bar.GetProperty("l").GetDecimal(),
                    Close = bar.GetProperty("c").GetDecimal(),
                    Volume = bar.GetProperty("v").GetDouble(),
                    TradeCount = OptionalDouble(bar, "n"),
                    Vwap = OptionalDecimal(bar, "vw"),
                });
            });

            total += await _store.UpsertBarsAsync(rows, ct);
            _logger.LogInformation("  bars {Symbol} {Timeframe}: {Count:N0} rows.", symbol, timeframe, rows.Count);
        }

        return total;
    }

    // ------------------------------------------------------------------ news

    private async Task<long> ImportNewsAsync(
        string raw, ImportWindow window, HashSet<string> symbols, CancellationToken ct)
    {
        var directory = Path.Combine(raw, "news");
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("No news directory at {Path}. Skipping.", directory);
            return 0;
        }

        var from = ToUnixSeconds(window.From);
        var to = ToUnixSeconds(window.To.AddDays(1));

        // One news item names many symbols and the per-symbol files overlap heavily, so
        // deduplicate by id across every file before writing.
        var items = new Dictionary<long, NewsRow>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            if (!symbols.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

            RawJsonPages.ForEachItem(path, "news", item =>
            {
                var published = ParseInstant(item.GetProperty("created_at").GetString()!);
                if (published < from || published >= to)
                {
                    return;
                }

                var id = item.GetProperty("id").GetInt64();
                if (items.ContainsKey(id))
                {
                    return;
                }

                var itemSymbols = item.TryGetProperty("symbols", out var list)
                    && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Select(s => s.GetString()!).ToArray()
                        : [];

                items[id] = new NewsRow
                {
                    Id = id,
                    PublishedUtc = published,
                    Headline = item.GetProperty("headline").GetString() ?? "",
                    Summary = OptionalString(item, "summary"),
                    Source = OptionalString(item, "source"),
                    Author = OptionalString(item, "author"),
                    Url = OptionalString(item, "url"),
                    Symbols = itemSymbols,
                };
            });
        }

        var written = await _store.UpsertNewsAsync(items.Values, ct);
        _logger.LogInformation("  news: {Count:N0} distinct items.", written);
        return written;
    }

    // ------------------------------------------------------------------ contracts

    private async Task<long> ImportContractsAsync(
        string raw, ImportWindow window, HashSet<string> symbols, CancellationToken ct)
    {
        var directory = Path.Combine(raw, "contracts");
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("No contracts directory at {Path}. Skipping.", directory);
            return 0;
        }

        var earliest = window.From;
        var latest = window.To.AddDays(window.ExpirationSlackDays);
        long total = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            if (!TryMatchMonthFile(path, symbols, earliest, latest, out _))
            {
                continue;
            }

            var rows = new List<OptionContractRow>(capacity: 4096);

            RawJsonPages.ForEachItem(path, "option_contracts", contract =>
            {
                var expiration = DateOnly.ParseExact(
                    contract.GetProperty("expiration_date").GetString()!, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

                if (expiration < earliest || expiration > latest)
                {
                    return;
                }

                var underlying = contract.GetProperty("underlying_symbol").GetString()!;
                if (!symbols.Contains(underlying))
                {
                    return;
                }

                rows.Add(new OptionContractRow
                {
                    ContractSymbol = contract.GetProperty("symbol").GetString()!,
                    Underlying = underlying,
                    Expiration = expiration.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Strike = decimal.Parse(
                        contract.GetProperty("strike_price").GetString()!, CultureInfo.InvariantCulture),
                    OptionType = contract.GetProperty("type").GetString()!,
                    Style = OptionalString(contract, "style"),
                    // Alpaca answers multiplier "0" on an expired contract and carries the
                    // real contract size in `size`. Prefer size; a 0 multiplier would make
                    // every premium calculation silently wrong.
                    Multiplier = ParseIntOrNull(OptionalString(contract, "size"))
                                 ?? ParseIntOrNull(OptionalString(contract, "multiplier")),
                });
            });

            total += await _store.UpsertOptionContractsAsync(rows, ct);
        }

        _logger.LogInformation("  option contracts: {Count:N0} rows.", total);
        return total;
    }

    // ------------------------------------------------------------------ option bars

    private async Task<long> ImportOptionBarsAsync(
        string raw, ImportWindow window, HashSet<string> symbols, CancellationToken ct)
    {
        var directory = Path.Combine(raw, "option-bars");
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("No option-bars directory at {Path}. Skipping.", directory);
            return 0;
        }

        var earliest = window.From;
        var latest = window.To.AddDays(window.ExpirationSlackDays);
        var from = ToUnixSeconds(window.From);
        var to = ToUnixSeconds(window.To.AddDays(1));
        long total = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            if (!TryMatchMonthFile(path, symbols, earliest, latest, out _))
            {
                continue;
            }

            var rows = new List<OptionBarRow>(capacity: 8192);

            RawJsonPages.ForEachInMap(path, "bars", (contractSymbol, bar) =>
            {
                // A daily option bar is stamped at the start of its session in UTC, which is
                // before the New York date rolls, so the UTC date is the session date.
                var session = ParseInstant(bar.GetProperty("t").GetString()!);
                if (session < from || session >= to)
                {
                    return;
                }

                rows.Add(new OptionBarRow
                {
                    ContractSymbol = contractSymbol,
                    SessionUtc = session,
                    AvailableUtc = BarAvailability.ForOptionBar(session),
                    Open = bar.GetProperty("o").GetDecimal(),
                    High = bar.GetProperty("h").GetDecimal(),
                    Low = bar.GetProperty("l").GetDecimal(),
                    Close = bar.GetProperty("c").GetDecimal(),
                    Volume = bar.GetProperty("v").GetDouble(),
                    TradeCount = OptionalDouble(bar, "n"),
                    Vwap = OptionalDecimal(bar, "vw"),
                });
            });

            total += await _store.UpsertOptionBarsAsync(rows, ct);
        }

        _logger.LogInformation("  option bars: {Count:N0} rows.", total);
        return total;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Matches a <c>&lt;SYM&gt;.&lt;YYYY-MM&gt;.json</c> file against the symbol set and the
    /// month range, so a file outside the window is never opened.
    /// </summary>
    private static bool TryMatchMonthFile(
        string path, HashSet<string> symbols, DateOnly earliest, DateOnly latest, out string symbol)
    {
        symbol = "";
        var parts = Path.GetFileNameWithoutExtension(path).Split('.');
        if (parts.Length != 2 || !symbols.Contains(parts[0]))
        {
            return false;
        }

        if (!DateOnly.TryParseExact(
                parts[1] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var month))
        {
            return false;
        }

        // The file name is the expiration month, so compare against the month range with the
        // whole month included at the far end.
        if (month.AddMonths(1).AddDays(-1) < earliest || month > latest)
        {
            return false;
        }

        symbol = parts[0];
        return true;
    }

    private static long ParseInstant(string value) =>
        DateTimeOffset.Parse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds();

    private static long ToUnixSeconds(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? OptionalDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static decimal? OptionalDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    private static int? ParseIntOrNull(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? parsed
            : null;
}
