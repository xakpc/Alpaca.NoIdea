namespace Xakpc.Alpaca.NøIdea.Observability;

/// <summary>A stable, structured count breakdown carried by one log event.</summary>
public sealed class EventCountBreakdown
{
    public EventCountBreakdown(IReadOnlyDictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        Counts = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The counts, largest first and then ordered by code.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> Counts { get; }

    public override string ToString() => string.Join(
        ", ",
        Counts.Select(pair => $"{pair.Key} {pair.Value}"));
}
