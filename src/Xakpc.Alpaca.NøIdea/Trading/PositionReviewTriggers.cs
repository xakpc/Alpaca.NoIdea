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
    public decimal LossMilestone { get; init; } = 0.20m;

    /// <summary>§10.12. Review when this little time remains.</summary>
    public int ExpirationReviewDays { get; init; } = 2;

    /// <summary>§10.6. Review when this many fresh headlines appeared since the last review.</summary>
    public int NewsCountTrigger { get; init; } = 3;
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

        // §10.12 first: time is the one thing that cannot be recovered.
        if (daysToExpiration is { } days && days <= _options.ExpirationReviewDays)
        {
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

        if (!_lastReviewed.TryGetValue(position.Symbol, out var last))
        {
            return new ReviewTrigger("first review", "this position has never been reviewed");
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
}
