namespace Xakpc.Alpaca.NøIdea.Replay;

/// <summary>
/// A <see cref="TimeProvider"/> the replay runner advances by hand.
/// </summary>
/// <remarks>
/// <para>
/// Every part of the system takes time from an injected <see cref="TimeProvider"/> rather
/// than from <c>DateTimeOffset.UtcNow</c>, which is what lets the identical strategy code run
/// against history. This class is the replay half of that seam;
/// <see cref="TimeProvider.System"/> is the live half.
/// </para>
/// <para>
/// <see cref="UtcNow"/> is the no-leak boundary. Every replay gateway and every replay agent
/// tool clamps its reads to this instant, so a caller cannot see data that did not exist yet
/// no matter what range it asks for.
/// </para>
/// </remarks>
public sealed class ReplayClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    /// <summary>The current replay instant. Reads are atomic so a test can observe it safely.</summary>
    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    /// <summary>The replay runs in UTC. The market calendar converts to Eastern where needed.</summary>
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>Moves the clock forward. Moving it backwards is a bug, not a feature.</summary>
    public void AdvanceTo(DateTimeOffset instant)
    {
        var target = instant.UtcTicks;
        var current = Interlocked.Read(ref _ticks);

        if (target < current)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instant), instant,
                $"The replay clock cannot move backwards. It is at {new DateTimeOffset(current, TimeSpan.Zero):u}.");
        }

        Interlocked.Exchange(ref _ticks, target);
    }

    public void Advance(TimeSpan amount) => AdvanceTo(UtcNow + amount);
}
