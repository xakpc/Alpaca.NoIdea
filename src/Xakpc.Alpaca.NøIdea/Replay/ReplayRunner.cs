using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Replay;

/// <summary>What one replay cycle is given. The same shape the live loop receives.</summary>
public sealed record ReplayCycle
{
    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>The replay clock, so a cycle takes time from the same source its reads do.</summary>
    public required TimeProvider Clock { get; init; }
    public required IMarketDataGateway MarketData { get; init; }
    public required ITradingGateway Trading { get; init; }
}

/// <summary>
/// Steps a replay clock through historical sessions and runs one cycle at each step.
/// </summary>
/// <remarks>
/// <para>
/// The runner owns time and nothing else. It does not know what a cycle does, so the trading
/// loop can be handed to it unchanged once Phase 7 builds it — which is the whole point of
/// the gateway seam: replay must exercise the same strategy code the live path runs, not a
/// parallel copy of it.
/// </para>
/// <para>
/// Sessions come from the cache rather than from a calendar, so a market holiday is simply a
/// day with no bars and needs no holiday table.
/// </para>
/// </remarks>
public sealed class ReplayRunner(string connectionString, ILogger logger)
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Runs <paramref name="cycle"/> once per step between <paramref name="from"/> and
    /// <paramref name="to"/>.
    /// </summary>
    /// <param name="stepsPerSession">
    /// How many cycles run inside one session. Historical option prices are daily closes, so
    /// more than one step per session gives a cycle no new option information — the default
    /// of 1 reflects what the data can actually support.
    /// </param>
    public async Task<ReplayResult> RunAsync(
        DateOnly from,
        DateOnly to,
        int stepsPerSession,
        decimal startingEquity,
        Func<ReplayCycle, CancellationToken, Task> cycle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepsPerSession, 1);

        var sessions = await LoadSessionsAsync(from, to, cancellationToken);
        if (sessions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No cached sessions between {from} and {to}. Run --import-history first.");
        }

        _logger.LogInformation(
            "Replay {From} to {To}: {Count} sessions, {Steps} step(s) each.",
            sessions[0], sessions[^1], sessions.Count, stepsPerSession);

        // The clock starts at the first session open and only ever moves forward.
        var clock = new ReplayClock(SessionInstant(sessions[0], 0, stepsPerSession));
        var marketData = new ReplayMarketDataGateway(_connectionString, clock);
        var trading = new ReplayTradingGateway(
            clock, startingEquity,
            (contract, token) => LatestOptionCloseAsync(contract, clock, token));

        var cycles = 0;

        foreach (var session in sessions)
        {
            for (var step = 0; step < stepsPerSession; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                clock.AdvanceTo(SessionInstant(session, step, stepsPerSession));

                await cycle(
                    new ReplayCycle
                    {
                        AtUtc = clock.UtcNow,
                        Clock = clock,
                        MarketData = marketData,
                        Trading = trading,
                    },
                    cancellationToken);

                cycles++;
            }
        }

        var account = await trading.GetAccountAsync(cancellationToken);

        return new ReplayResult
        {
            Sessions = sessions.Count,
            Cycles = cycles,
            StartingEquity = startingEquity,
            FinalEquity = account.Equity,
            RealizedPnl = trading.RealizedPnl,
            Fills = trading.Fills,
        };
    }

    /// <summary>
    /// Spreads <paramref name="stepsPerSession"/> cycles across regular trading hours, so a
    /// single-step run lands at the open rather than at midnight.
    /// </summary>
    private static DateTimeOffset SessionInstant(DateOnly session, int step, int stepsPerSession)
    {
        var open = session.ToDateTime(MarketCalendar.OpenTime);
        var minutes = (MarketCalendar.CloseTime - MarketCalendar.OpenTime).TotalMinutes;
        var offset = minutes * step / stepsPerSession;

        return MarketCalendar.ToUtc(open.AddMinutes(offset));
    }

    /// <summary>Sessions that actually hold data, so holidays need no calendar.</summary>
    private async Task<IReadOnlyList<DateOnly>> LoadSessionsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT DISTINCT timestamp_utc AS TimestampUtc
            FROM bars
            WHERE timeframe = '1Day' AND timestamp_utc >= @from AND timestamp_utc < @to
            ORDER BY timestamp_utc
            """;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var stamps = await connection.QueryAsync<long>(new CommandDefinition(
            sql,
            new
            {
                from = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds(),
                to = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds(),
            },
            cancellationToken: cancellationToken));

        return stamps
            .Select(stamp => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(stamp).UtcDateTime))
            .Distinct()
            .OrderBy(session => session)
            .ToArray();
    }

    /// <summary>
    /// The most recent KNOWABLE daily close for one contract, which is what a simulated fill
    /// pays. It is a close rather than an ask because history holds no quote.
    /// </summary>
    /// <remarks>
    /// The filter is <c>available_utc</c>, not <c>session_utc</c> (ADR-015). Filtering on the
    /// session would fill an order at the closing price of the session the cycle is still
    /// inside, which is the same future-data leak the read path had.
    /// </summary>
    private async Task<decimal?> LatestOptionCloseAsync(
        string contractSymbol, ReplayClock clock, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT close
            FROM option_bars
            WHERE contract_symbol = @contractSymbol AND available_utc <= @asOf
            ORDER BY session_utc DESC
            LIMIT 1
            """;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<decimal?>(new CommandDefinition(
            sql,
            new { contractSymbol, asOf = clock.UtcNow.ToUnixTimeSeconds() },
            cancellationToken: cancellationToken));
    }
}

/// <summary>What one replay run produced.</summary>
/// <remarks>
/// <b>Replay P&amp;L is evidence about the logic, not a forecast of live P&amp;L.</b> Fills
/// are at daily closes with no spread paid, so a live result would be worse by roughly the
/// bid/ask on every trade.
/// </remarks>
public sealed record ReplayResult
{
    public required int Sessions { get; init; }
    public required int Cycles { get; init; }
    public required decimal StartingEquity { get; init; }
    public required decimal FinalEquity { get; init; }
    public required decimal RealizedPnl { get; init; }
    public required IReadOnlyList<SimulatedFill> Fills { get; init; }
}
