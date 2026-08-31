namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>US equity regular hours and exchange-time conversion.</summary>
public static class MarketCalendar
{
    public static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static readonly TimeOnly OpenTime = new(9, 30);
    public static readonly TimeOnly CloseTime = new(16, 0);

    public static DateTime ToEastern(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc.UtcDateTime, TimeZoneInfo.Utc, Eastern);

    public static DateTimeOffset ToUtc(DateTime eastern) =>
        new(TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(eastern, DateTimeKind.Unspecified), Eastern), TimeSpan.Zero);

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
