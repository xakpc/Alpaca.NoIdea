namespace Xakpc.Alpaca.NøIdea.Replay;

/// <summary>
/// Regular trading hours for the US equity market: 09:30 to 16:00 America/New_York.
/// </summary>
/// <remarks>
/// <para>
/// Alpaca returns 15-minute bars from 04:00 to 20:00 ET, so a session holds 64 bars and only
/// 26 of them are regular hours. The system trades in regular hours only, so replay steps
/// through regular-hours instants. Overnight movement is not lost: the 1Day bars already span
/// the gap.
/// </para>
/// <para>
/// This type holds no holiday table. A session exists when the cache holds bars for it, which
/// is why the replay runner enumerates sessions from the data rather than from a calendar.
/// </para>
/// <para>
/// Restated from <c>FeatureGenerator/MarketCalendar.cs</c> on branch
/// <c>phase-3-historical-ml-expert</c>.
/// </para>
/// </remarks>
public static class MarketCalendar
{
    /// <summary>The exchange time zone. Daylight saving is handled by the zone, not an offset.</summary>
    public static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static readonly TimeOnly OpenTime = new(9, 30);
    public static readonly TimeOnly CloseTime = new(16, 0);

    /// <summary>The instant expressed in exchange local time.</summary>
    public static DateTime ToEastern(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc.UtcDateTime, TimeZoneInfo.Utc, Eastern);

    /// <summary>An exchange local time expressed as an instant.</summary>
    public static DateTimeOffset ToUtc(DateTime eastern) =>
        new(TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(eastern, DateTimeKind.Unspecified), Eastern), TimeSpan.Zero);

    /// <summary>
    /// True when the bar starting at <paramref name="utc"/> lies inside regular trading
    /// hours. A bar timestamp is the start of its interval, so the 15:45 bar is the last
    /// regular one and the 16:00 bar is not.
    /// </summary>
    public static bool IsRegularHours(DateTimeOffset utc)
    {
        var eastern = ToEastern(utc);
        if (eastern.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(eastern);
        return time >= OpenTime && time < CloseTime;
    }
}
