using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Replay;
using Xakpc.Alpaca.NøIdea.Storage;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The replay guarantees from .lode/replay/replay-mode.md.
/// </summary>
/// <remarks>
/// The no-leak rule is the one that matters: at replay time T every expert must see only
/// data that existed at or before T. These tests build a real SQLite database with rows on
/// both sides of T and prove the gateway cannot be talked into returning the later ones.
/// </remarks>
public sealed class ReplayTests : IAsyncLifetime
{
    private static readonly DateTimeOffset ReplayInstant =
        new(2026, 6, 15, 14, 30, 0, TimeSpan.Zero);

    private string _databasePath = "";
    private string _connectionString = "";
    private TradingStore _store = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"replay-{Guid.NewGuid():N}.db");
        _connectionString = TradingStore.ConnectionStringForFile(_databasePath);
        _store = new TradingStore(_connectionString);
        await _store.CreateSchemaAsync(CancellationToken.None);

        // One bar an hour before T, one an hour after. Same symbol, same timeframe.
        await _store.UpsertBarsAsync(
        [
            Bar(ReplayInstant.AddHours(-1), close: 100m),
            Bar(ReplayInstant.AddHours(1), close: 999m),
        ], CancellationToken.None);

        await _store.UpsertNewsAsync(
        [
            News(1, ReplayInstant.AddHours(-1), "Before the replay instant"),
            News(2, ReplayInstant.AddHours(1), "After the replay instant"),
        ], CancellationToken.None);

        await _store.UpsertOptionContractsAsync(
        [
            Contract("TEST260619C00100000", 100m),
            Contract("TEST260619C00105000", 105m),
            Contract("TEST260619C00110000", 110m),
        ], CancellationToken.None);

        // A ladder before T, and a different ladder after it.
        await _store.UpsertOptionBarsAsync(
        [
            OptionBar("TEST260619C00100000", ReplayInstant.AddHours(-1), close: 6m),
            OptionBar("TEST260619C00105000", ReplayInstant.AddHours(-1), close: 3m),
            OptionBar("TEST260619C00110000", ReplayInstant.AddHours(-1), close: 1m),
            OptionBar("TEST260619C00105000", ReplayInstant.AddHours(1), close: 888m),
        ], CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    private ReplayMarketDataGateway Gateway() =>
        new(_connectionString, new ReplayClock(ReplayInstant));

    // ---------------------------------------------------------------- no leak

    [Fact]
    public async Task ABarAfterTheReplayInstantIsNotVisibleEvenWhenTheCallerAsksForIt()
    {
        // The caller deliberately asks for a window reaching well past T. The clamp is the
        // clock's, not the caller's, so widening the request must change nothing.
        var bars = await Gateway().GetBarsAsync(
            "TEST", "15Min", ReplayInstant.AddDays(-1), ReplayInstant.AddDays(1), CancellationToken.None);

        Assert.Single(bars);
        Assert.Equal(100m, bars[0].Close);
    }

    [Fact]
    public async Task NewsAfterTheReplayInstantIsNotVisibleEvenWhenTheCallerAsksForIt()
    {
        var news = await Gateway().GetNewsAsync(
            ["TEST"], ReplayInstant.AddDays(-1), ReplayInstant.AddDays(1), 100, CancellationToken.None);

        Assert.Single(news);
        Assert.Equal("Before the replay instant", news[0].Headline);
    }

    [Fact]
    public async Task AnOptionBarAfterTheReplayInstantIsNotVisible()
    {
        var candidates = await Gateway().GetOptionCandidatesAsync(
            new OptionChainQuery { Underlying = "TEST" }, CancellationToken.None);

        // The 105 strike has a bar on both sides of T. The later one must not win.
        var strike105 = Assert.Single(candidates, c => c.Strike == 105m);
        Assert.Equal(3m, strike105.ReferencePrice);
        Assert.DoesNotContain(candidates, c => c.ReferencePrice == 888m);
    }

    [Fact]
    public async Task AdvancingTheClockRevealsTheLaterData()
    {
        // The mirror of the tests above: the clamp must be the clock, not a fixed filter.
        // If this passed while the clock stood still, the others would prove nothing.
        var clock = new ReplayClock(ReplayInstant);
        var gateway = new ReplayMarketDataGateway(_connectionString, clock);

        clock.AdvanceTo(ReplayInstant.AddHours(2));

        var bars = await gateway.GetBarsAsync(
            "TEST", "15Min", ReplayInstant.AddDays(-1), ReplayInstant.AddDays(1), CancellationToken.None);

        Assert.Equal(2, bars.Count);
    }

    [Fact]
    public void TheReplayClockRefusesToMoveBackwards()
    {
        var clock = new ReplayClock(ReplayInstant);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTo(ReplayInstant.AddSeconds(-1)));
    }

    // ---------------------------------------------------------------- quote honesty

    [Fact]
    public async Task AReplayedCandidateNeverClaimsATradeableQuote()
    {
        // Alpaca serves no historical bid or ask, so no historical candidate may pass a
        // spread or quote-age rule. This is what stops replay from proving a check it
        // cannot actually run.
        var candidates = await Gateway().GetOptionCandidatesAsync(
            new OptionChainQuery { Underlying = "TEST" }, CancellationToken.None);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(QuoteQuality.UnknownHistorical, candidate.Quality);
            Assert.False(candidate.IsTradeableQuote);
            Assert.Null(candidate.Bid);
            Assert.Null(candidate.Ask);
            Assert.Null(candidate.Delta);
            Assert.Null(candidate.Spread);
        });
    }

    // ---------------------------------------------------------------- never sends

    [Fact]
    public async Task TheReplayTradingGatewaySimulatesAFillAndHoldsNoBrokerClient()
    {
        var clock = new ReplayClock(ReplayInstant);
        var gateway = new ReplayTradingGateway(clock, 100_000m, (_, _) => Task.FromResult<decimal?>(3m));

        var order = await gateway.SubmitOrderAsync(
            new OrderRequest
            {
                ClientOrderId = "test-1",
                ContractSymbol = "TEST260619C00105000",
                Quantity = 1,
                IsBuy = true,
            },
            CancellationToken.None);

        Assert.Equal(OrderLifecycle.Filled, order.Lifecycle);

        // One contract at 3.00 with a 100 multiplier costs 300.
        var account = await gateway.GetAccountAsync(CancellationToken.None);
        Assert.Equal(99_700m, account.Cash);
        Assert.Equal(100_000m, account.Equity);

        // The type carries no Alpaca client at all, so it cannot reach a broker.
        Assert.DoesNotContain(
            typeof(ReplayTradingGateway).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType.Namespace?.StartsWith("Alpaca", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ARepeatedClientOrderIdDoesNotBookASecondFill()
    {
        var gateway = new ReplayTradingGateway(
            new ReplayClock(ReplayInstant), 100_000m, (_, _) => Task.FromResult<decimal?>(3m));

        var request = new OrderRequest
        {
            ClientOrderId = "test-idempotent",
            ContractSymbol = "TEST260619C00105000",
            Quantity = 1,
            IsBuy = true,
        };

        await gateway.SubmitOrderAsync(request, CancellationToken.None);
        await gateway.SubmitOrderAsync(request, CancellationToken.None);

        var account = await gateway.GetAccountAsync(CancellationToken.None);
        Assert.Equal(99_700m, account.Cash);
        Assert.Single(gateway.Fills);
    }

    [Fact]
    public async Task AnOrderWithNoCachedPriceIsRejectedRatherThanFilledAtAGuess()
    {
        var gateway = new ReplayTradingGateway(
            new ReplayClock(ReplayInstant), 100_000m, (_, _) => Task.FromResult<decimal?>(null));

        var order = await gateway.SubmitOrderAsync(
            new OrderRequest
            {
                ClientOrderId = "test-no-price",
                ContractSymbol = "TEST260619C00105000",
                Quantity = 1,
                IsBuy = true,
            },
            CancellationToken.None);

        Assert.Equal(OrderLifecycle.Rejected, order.Lifecycle);
        Assert.Empty(gateway.Fills);
    }

    // ---------------------------------------------------------------- helpers

    // AvailableUtc is set equal to the timestamp on purpose: these tests isolate the clock
    // clamp. The interval semantics -- that a bar is knowable only when its interval ends --
    // are covered by BarAvailabilityTests.
    private static BarRow Bar(DateTimeOffset at, decimal close) => new()
    {
        Symbol = "TEST",
        Timeframe = "15Min",
        TimestampUtc = at.ToUnixTimeSeconds(),
        AvailableUtc = at.ToUnixTimeSeconds(),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 1000,
    };

    private static NewsRow News(long id, DateTimeOffset at, string headline) => new()
    {
        Id = id,
        PublishedUtc = at.ToUnixTimeSeconds(),
        Headline = headline,
        Symbols = ["TEST"],
    };

    private static OptionContractRow Contract(string symbol, decimal strike) => new()
    {
        ContractSymbol = symbol,
        Underlying = "TEST",
        Expiration = "2026-06-19",
        Strike = strike,
        OptionType = "call",
        Multiplier = 100,
    };

    private static OptionBarRow OptionBar(string symbol, DateTimeOffset at, decimal close) => new()
    {
        ContractSymbol = symbol,
        SessionUtc = at.ToUnixTimeSeconds(),
        AvailableUtc = at.ToUnixTimeSeconds(),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 10,
    };
}
