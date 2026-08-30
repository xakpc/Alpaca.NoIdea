using Xakpc.Alpaca.NøIdea.Replay;

namespace Xakpc.Alpaca.NøIdea.Storage;

/// <summary>
/// When a bar became knowable. The single definition the no-leak rule rests on.
/// </summary>
/// <remarks>
/// <para>
/// A bar timestamp is the <b>start</b> of its interval, but a bar carries the close of that
/// interval. So a bar is not knowable at its own timestamp — it is knowable when the interval
/// ends. Filtering replay reads on the timestamp therefore leaks the future: a 1Day bar
/// stamped 05:00Z holds the 16:00 ET close, and a cycle running at 09:30 ET that read it
/// would see six and a half hours ahead.
/// </para>
/// <para>
/// The rule is deliberately conservative. Where the exact end of an interval is uncertain —
/// whether a daily bar includes extended hours, when a late print settles — this rounds
/// <em>later</em>. Being late costs a cycle one session of information; being early
/// invalidates every measurement taken from the run.
/// </para>
/// </remarks>
public static class BarAvailability
{
    /// <summary>
    /// Daily bars are treated as knowable at 20:00 Eastern, the end of extended trading.
    /// Alpaca's daily bar can include extended-hours activity, so the regular 16:00 close is
    /// not late enough to be safe.
    /// </summary>
    private static readonly TimeOnly DailyBarSettles = new(20, 0);

    /// <summary>
    /// Option daily bars settle with the option market at 16:00 Eastern, plus a margin for
    /// late prints. Options do not trade in extended hours.
    /// </summary>
    private static readonly TimeOnly OptionDailyBarSettles = new(16, 15);

    /// <summary>The instant an underlying bar of <paramref name="timeframe"/> becomes knowable.</summary>
    public static long ForBar(long timestampUtc, string timeframe)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(timestampUtc);

        return timeframe switch
        {
            "1Day" => SettlementOf(start, DailyBarSettles),
            "1Min" => timestampUtc + 60,
            "5Min" => timestampUtc + (5 * 60),
            "15Min" => timestampUtc + (15 * 60),
            "1Hour" => timestampUtc + (60 * 60),
            _ => throw new ArgumentOutOfRangeException(
                nameof(timeframe), timeframe, "No availability rule for this timeframe."),
        };
    }

    /// <summary>The instant a daily option bar for a session becomes knowable.</summary>
    public static long ForOptionBar(long sessionUtc) =>
        SettlementOf(DateTimeOffset.FromUnixTimeSeconds(sessionUtc), OptionDailyBarSettles);

    /// <summary>
    /// The settlement instant on the session the bar belongs to.
    /// </summary>
    /// <remarks>
    /// A daily bar is stamped at 05:00 UTC, which is before the New York date rolls, so the
    /// UTC date is the session date. The settlement time is Eastern, so the conversion goes
    /// through the zone and daylight saving is handled rather than assumed.
    /// </remarks>
    private static long SettlementOf(DateTimeOffset barStart, TimeOnly settles)
    {
        var session = DateOnly.FromDateTime(barStart.UtcDateTime);
        return MarketCalendar.ToUtc(session.ToDateTime(settles)).ToUnixTimeSeconds();
    }
}
