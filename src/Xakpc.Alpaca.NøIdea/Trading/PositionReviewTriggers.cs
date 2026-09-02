using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>Why a position is being sent to the war room.</summary>
public sealed record ReviewTrigger(string Name, string Detail)
{
    public override string ToString() => $"{Name}: {Detail}";
}

/// <summary>When to convene the room over an open position. Spec §10.</summary>
/// <remarks>
/// The spec lists sixteen triggers. This implements the five that carry most of the value for
/// a four-day contest and leaves the rest deliberately unbuilt rather than half-built.
/// </remarks>
public sealed record ReviewTriggerOptions
{
    /// <summary>§10.1. How long before a position is reviewed again regardless.</summary>
    public TimeSpan ScheduledInterval { get; init; } = TimeSpan.FromMinutes(90);

    /// <summary>§10.4. A gain worth asking whether the remaining upside justifies the risk.</summary>
    public decimal ProfitMilestone { get; init; } = 0.30m;

    /// <summary>§10.5. A loss short of the hard stop, worth asking whether the thesis holds.</summary>
    /// <remarks>
    /// Kept inside <see cref="StrategyPolicy.StopLossFraction"/>, and moved with it. A
    /// milestone far below the stop asks the room to reconsider a position the hard exit is
    /// deliberately still holding, on every cycle, until one sitting decides to close it. The
    /// milestone should catch a position on its way to the stop, not replace the stop.
    /// </remarks>
    public decimal LossMilestone { get; init; } = 0.40m;

    /// <summary>§10.12. Review when this little time remains.</summary>
    public int ExpirationReviewDays { get; init; } = 2;

    /// <summary>§10.6. Review when this many fresh headlines appeared since the last review.</summary>
    public int NewsCountTrigger { get; init; } = 3;

    /// <summary>The shortest gap between two sittings over the same position.</summary>
    /// <remarks>
    /// Every trigger except the first review is rate-limited by this. Without it a condition
    /// that stays true fires on every cycle: each contract the system may buy expires within
    /// <see cref="ExpirationReviewDays"/>, so the expiration trigger alone would convene a
    /// paid sitting every cycle for the rest of the day, and each sitting is another chance to
    /// close a position nothing is actually wrong with. A trigger reports a condition, and a
    /// condition that has not changed does not need to be reported twice.
    /// </remarks>
    public TimeSpan MinimumReviewGap { get; init; } = TimeSpan.FromMinutes(60);
}

/// <summary>
/// Decides whether an open position needs judgement. Deterministic C#.
/// </summary>
/// <remarks>
/// <para>
/// <b>A trigger does not close anything.</b> It only asks whether the original thesis still
/// holds. Hard exits run before this and do not consult anybody (spec §9), which is the
/// division that keeps a stop-loss independent of whether a model answers.
/// </para>
/// <para>
/// Firing a trigger costs a full war-room sitting, so each one has to earn it. The scheduled
/// interval exists so a quiet position is still revisited; the others exist so a loud one is
/// revisited sooner.
/// </para>
/// </remarks>
public sealed class PositionReviewTriggers(ReviewTriggerOptions options, TimeProvider time)
{
    private readonly ReviewTriggerOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    private readonly Dictionary<string, DateTimeOffset> _lastReviewed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastNewsSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _expirationReviewedOn = new(StringComparer.Ordinal);

    /// <summary>The first trigger that fires, or null when the position needs nothing.</summary>
    /// <param name="freshNewsCount">Headlines for the underlying since the last review.</param>
    public ReviewTrigger? Evaluate(
        PositionState position,
        decimal? currentPrice,
        int? daysToExpiration,
        int freshNewsCount)
    {
        ArgumentNullException.ThrowIfNull(position);

        var now = _time.GetUtcNow();
        var reviewedBefore = _lastReviewed.TryGetValue(position.Symbol, out var last);

        // A position nobody has looked at yet is judged once, immediately.
        if (!reviewedBefore)
        {
            // That sitting reads the position whole, expiration included, so it settles the
            // expiration question for the day as well. Otherwise the room is called back an
            // hour later to consider a fact it has already seen.
            if (daysToExpiration is { } remaining && remaining <= _options.ExpirationReviewDays)
            {
                _expirationReviewedOn[position.Symbol] = MarketCalendar.ToEastern(now).Date;
            }

            return new ReviewTrigger("first review", "this position has never been reviewed");
        }

        // Everything below reports a market condition, and a condition that is still true is
        // not new information. Without this gate the standing ones re-fire every cycle.
        if (now - last < _options.MinimumReviewGap)
        {
            return null;
        }

        // §10.12 first: time is the one thing that cannot be recovered. Once per Eastern
        // trading day, because "the expiration is close" is true from the moment it becomes
        // true until the position is gone, and repeating it says nothing new.
        if (daysToExpiration is { } days
            && days <= _options.ExpirationReviewDays
            && MarketCalendar.ToEastern(now).Date
                != _expirationReviewedOn.GetValueOrDefault(position.Symbol))
        {
            _expirationReviewedOn[position.Symbol] = MarketCalendar.ToEastern(now).Date;
            return new ReviewTrigger("expiration", $"{days} day(s) to expiration");
        }

        if (currentPrice is { } price && position.AverageEntryPrice > 0m)
        {
            var change = (price - position.AverageEntryPrice) / position.AverageEntryPrice;

            if (change >= _options.ProfitMilestone)
            {
                return new ReviewTrigger("profit milestone", $"up {change:P0}");
            }

            if (change <= -_options.LossMilestone)
            {
                return new ReviewTrigger("loss milestone", $"down {change:P0}");
            }
        }

        // §10.6. Counted rather than compared by id, so the same story cannot re-trigger:
        // the count only grows when genuinely new items arrive after the last review.
        if (freshNewsCount >= _options.NewsCountTrigger
            && freshNewsCount > _lastNewsSeen.GetValueOrDefault(position.Symbol))
        {
            return new ReviewTrigger("new news", $"{freshNewsCount} fresh headline(s)");
        }

        if (now - last >= _options.ScheduledInterval)
        {
            return new ReviewTrigger("scheduled", $"{(now - last).TotalMinutes:N0} minutes since the last review");
        }

        return null;
    }

    /// <summary>Records that a review happened, so the same trigger does not fire twice.</summary>
    public void MarkReviewed(string symbol, int newsCountAtReview)
    {
        _lastReviewed[symbol] = _time.GetUtcNow();
        _lastNewsSeen[symbol] = newsCountAtReview;
    }

    /// <summary>Restores one durable review cursor during session startup.</summary>
    public void Restore(string symbol, DateTimeOffset lastReviewedUtc, long lastNewsSeen)
    {
        _lastReviewed[symbol] = lastReviewedUtc;
        _lastNewsSeen[symbol] = lastNewsSeen;
    }
}
