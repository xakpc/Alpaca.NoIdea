using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// A trigger convenes a paid war-room sitting, and every sitting is another chance to close a
/// position. These cover the rate limit that stops a standing condition from firing forever.
/// </summary>
public class PositionReviewTriggerTests
{
    private static readonly DateTimeOffset Open =
        new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);   // 10:00 ET

    private static PositionState Position(decimal current = 2m) => new()
    {
        Symbol = "SPY260904C00770000",
        Quantity = 1,
        AverageEntryPrice = 2m,
        CurrentPrice = current,
    };

    private static (PositionReviewTriggers Triggers, MovableClock Time) Build()
    {
        var time = new MovableClock(Open);
        return (new PositionReviewTriggers(new ReviewTriggerOptions(), time), time);
    }

    [Fact]
    public void AnUnreviewedPositionIsJudgedOnce()
    {
        var (triggers, _) = Build();

        var first = triggers.Evaluate(Position(), 2m, daysToExpiration: 2, freshNewsCount: 0);

        Assert.NotNull(first);
        Assert.Equal("first review", first.Name);
    }

    [Fact]
    public void ExpirationDoesNotFireAgainOnTheNextCycle()
    {
        var (triggers, time) = Build();
        var position = Position();

        // The first review, then the room records that it happened.
        Assert.NotNull(triggers.Evaluate(position, 2m, 2, 0));
        triggers.MarkReviewed(position.Symbol, 0);

        // Every contract this system may buy expires within the review window, so before the
        // gap existed this condition re-fired on every cycle for the rest of the day.
        time.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(triggers.Evaluate(position, 2m, 2, 0));

        time.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(triggers.Evaluate(position, 2m, 2, 0));
    }

    [Fact]
    public void ExpirationFiresAtMostOncePerTradingDay()
    {
        var (triggers, time) = Build();
        var position = Position();

        Assert.NotNull(triggers.Evaluate(position, 2m, 2, 0));
        triggers.MarkReviewed(position.Symbol, 0);

        // Past the minimum gap, so only the once-per-day rule can hold it back.
        time.Advance(TimeSpan.FromMinutes(75));
        var again = triggers.Evaluate(position, 2m, 2, 0);

        Assert.NotEqual("expiration", again?.Name);
    }

    [Fact]
    public void ALossMilestoneWaitsForTheMinimumGap()
    {
        var (triggers, time) = Build();

        // Down 45 percent: past the milestone, still short of the 60 percent hard stop.
        var losing = Position(current: 1.1m);

        Assert.NotNull(triggers.Evaluate(losing, 1.1m, null, 0));
        triggers.MarkReviewed(losing.Symbol, 0);

        time.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(triggers.Evaluate(losing, 1.1m, null, 0));

        time.Advance(TimeSpan.FromMinutes(45));
        var later = triggers.Evaluate(losing, 1.1m, null, 0);

        Assert.Equal("loss milestone", later?.Name);
    }

    [Fact]
    public void ALossInsideTheMilestoneNeverConvenesTheRoom()
    {
        var (triggers, time) = Build();

        // Down 25 percent. Under the old 20 percent milestone this asked the room to
        // reconsider a position the hard exit is deliberately still holding.
        var losing = Position(current: 1.5m);

        Assert.NotNull(triggers.Evaluate(losing, 1.5m, null, 0));
        triggers.MarkReviewed(losing.Symbol, 0);

        time.Advance(TimeSpan.FromMinutes(75));
        var later = triggers.Evaluate(losing, 1.5m, null, 0);

        Assert.NotEqual("loss milestone", later?.Name);
    }

    [Fact]
    public void TheScheduledIntervalStillRevisitsAQuietPosition()
    {
        var (triggers, time) = Build();
        var position = Position();

        Assert.NotNull(triggers.Evaluate(position, 2m, null, 0));
        triggers.MarkReviewed(position.Symbol, 0);

        time.Advance(TimeSpan.FromMinutes(95));
        var later = triggers.Evaluate(position, 2m, null, 0);

        Assert.Equal("scheduled", later?.Name);
    }

    [Fact]
    public void ARestoredCursorSuppressesTheFirstReviewAfterRestart()
    {
        var (triggers, _) = Build();
        var position = Position();

        // What a fill now records, and what startup replays from SQLite.
        triggers.Restore(position.Symbol, Open, lastNewsSeen: 0);

        Assert.Null(triggers.Evaluate(position, 2m, 2, 0));
    }
}
