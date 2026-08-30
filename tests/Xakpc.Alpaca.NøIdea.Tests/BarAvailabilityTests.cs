using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Replay;
using Xakpc.Alpaca.NøIdea.Storage;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// When a bar becomes knowable, and the leak that follows from getting it wrong.
/// </summary>
/// <remarks>
/// This exists because of a real defect. The first replay run filtered on the bar timestamp,
/// which is the START of the interval, and so served a 09:30 cycle the option premiums from
/// that same session's 16:00 close. The market probabilities it produced looked plausible
/// enough to miss at a glance -- they were near 0% and 10% where 50% was expected -- which is
/// exactly why the rule needs a test rather than a comment.
/// </remarks>
public class BarAvailabilityTests
{
    /// <summary>2026-06-03 05:00Z: how Alpaca stamps a daily bar for the 3 June session.</summary>
    private static readonly long DailyBarStamp =
        new DateTimeOffset(2026, 6, 3, 5, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void ADailyBarIsNotKnowableDuringItsOwnSession()
    {
        var available = DateTimeOffset.FromUnixTimeSeconds(
            BarAvailability.ForBar(DailyBarStamp, "1Day"));

        // 09:30 ET on the same session is 13:30Z in June. The bar must not be visible.
        var duringSession = new DateTimeOffset(2026, 6, 3, 13, 30, 0, TimeSpan.Zero);
        Assert.True(available > duringSession);

        // It must be knowable by the next session's open.
        var nextOpen = new DateTimeOffset(2026, 6, 4, 13, 30, 0, TimeSpan.Zero);
        Assert.True(available < nextOpen);
    }

    [Fact]
    public void ADailyOptionBarIsNotKnowableDuringItsOwnSession()
    {
        var available = DateTimeOffset.FromUnixTimeSeconds(
            BarAvailability.ForOptionBar(DailyBarStamp));

        var duringSession = new DateTimeOffset(2026, 6, 3, 13, 30, 0, TimeSpan.Zero);
        var nextOpen = new DateTimeOffset(2026, 6, 4, 13, 30, 0, TimeSpan.Zero);

        Assert.True(available > duringSession);
        Assert.True(available < nextOpen);
    }

    [Theory]
    [InlineData("1Min", 60)]
    [InlineData("5Min", 300)]
    [InlineData("15Min", 900)]
    [InlineData("1Hour", 3600)]
    public void AnIntradayBarIsKnowableWhenItsIntervalEnds(string timeframe, int seconds)
    {
        // A 15-minute bar stamped 09:30 closes at 09:45, so reading it at 09:30 would leak
        // fifteen minutes.
        Assert.Equal(DailyBarStamp + seconds, BarAvailability.ForBar(DailyBarStamp, timeframe));
    }

    [Fact]
    public void AnUnknownTimeframeThrowsRatherThanGuessingAnAvailability()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BarAvailability.ForBar(DailyBarStamp, "3Day"));
    }

    [Fact]
    public void DaylightSavingIsTakenFromTheZoneNotAssumed()
    {
        // A January session settles at 20:00 EST (01:00Z next day); a June one at 20:00 EDT
        // (00:00Z next day). A fixed offset would put one of them an hour wrong.
        var winter = new DateTimeOffset(2026, 1, 15, 5, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var summer = new DateTimeOffset(2026, 6, 15, 5, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var winterAvailable = DateTimeOffset.FromUnixTimeSeconds(BarAvailability.ForBar(winter, "1Day"));
        var summerAvailable = DateTimeOffset.FromUnixTimeSeconds(BarAvailability.ForBar(summer, "1Day"));

        Assert.Equal(new DateTimeOffset(2026, 1, 16, 1, 0, 0, TimeSpan.Zero), winterAvailable);
        Assert.Equal(new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero), summerAvailable);
    }

    /// <summary>
    /// The end-to-end form of the same rule: a session's own option bar must not reach a
    /// cycle running inside that session.
    /// </summary>
    [Fact]
    public async Task ASessionsOwnClosingPremiumIsNotVisibleAtThatSessionsOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"leak-{Guid.NewGuid():N}.db");
        var connectionString = TradingStore.ConnectionStringForFile(path);

        try
        {
            var store = new TradingStore(connectionString);
            await store.CreateSchemaAsync(CancellationToken.None);

            await store.UpsertOptionContractsAsync(
            [
                new OptionContractRow
                {
                    ContractSymbol = "TEST260619C00100000",
                    Underlying = "TEST",
                    Expiration = "2026-06-19",
                    Strike = 100m,
                    OptionType = "call",
                },
            ], CancellationToken.None);

            // Yesterday's close, and today's close. Both stamped at 05:00Z of their session.
            await store.UpsertOptionBarsAsync(
            [
                OptionBar("TEST260619C00100000", new DateOnly(2026, 6, 2), close: 5m),
                OptionBar("TEST260619C00100000", new DateOnly(2026, 6, 3), close: 42m),
            ], CancellationToken.None);

            // A cycle at 09:30 ET on 3 June must see 2 June's close, not 3 June's.
            var clock = new ReplayClock(new DateTimeOffset(2026, 6, 3, 13, 30, 0, TimeSpan.Zero));
            var gateway = new ReplayMarketDataGateway(connectionString, clock);

            var candidates = await gateway.GetOptionCandidatesAsync(
                new OptionChainQuery { Underlying = "TEST" }, CancellationToken.None);

            var candidate = Assert.Single(candidates);
            Assert.Equal(5m, candidate.ReferencePrice);

            // After that session settles, the later close becomes visible.
            clock.AdvanceTo(new DateTimeOffset(2026, 6, 4, 13, 30, 0, TimeSpan.Zero));
            var afterwards = await gateway.GetOptionCandidatesAsync(
                new OptionChainQuery { Underlying = "TEST" }, CancellationToken.None);

            Assert.Equal(42m, Assert.Single(afterwards).ReferencePrice);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static OptionBarRow OptionBar(string symbol, DateOnly session, decimal close)
    {
        var stamp = new DateTimeOffset(session.ToDateTime(new TimeOnly(5, 0)), TimeSpan.Zero)
            .ToUnixTimeSeconds();

        return new OptionBarRow
        {
            ContractSymbol = symbol,
            SessionUtc = stamp,
            AvailableUtc = BarAvailability.ForOptionBar(stamp),
            Open = close,
            High = close,
            Low = close,
            Close = close,
            Volume = 10,
        };
    }
}
