using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>Builds the compact, broad-discovery headline index for one cycle.</summary>
public static class HeadlineIndexSelector
{
    public static IReadOnlyList<NewsItem> Select(
        IReadOnlyList<NewsItem> news,
        IReadOnlyList<string> trackedSymbols,
        int limit,
        int maxPerSymbol)
    {
        ArgumentNullException.ThrowIfNull(news);
        ArgumentNullException.ThrowIfNull(trackedSymbols);

        if (limit < trackedSymbols.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), "The headline limit must allow one nomination per tracked symbol.");
        }

        if (maxPerSymbol < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPerSymbol));
        }

        var tracked = trackedSymbols.ToHashSet(StringComparer.Ordinal);
        var ordered = news
            .GroupBy(item => item.Id)
            .Select(group => group.OrderByDescending(item => item.PublishedUtc).First())
            .OrderByDescending(item => item.PublishedUtc)
            .ThenByDescending(item => item.Id)
            .ToArray();

        var selected = new List<NewsItem>(limit);
        var selectedIds = new HashSet<long>();
        var perSymbol = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(NewsItem item)
        {
            if (!selectedIds.Add(item.Id))
            {
                return;
            }

            selected.Add(item);
            foreach (var symbol in item.Symbols.Where(tracked.Contains).Distinct(StringComparer.Ordinal))
            {
                perSymbol[symbol] = perSymbol.GetValueOrDefault(symbol) + 1;
            }
        }

        // Nominate independently first. A multi-symbol article can win several nominations,
        // but its article id can occupy only one row.
        foreach (var symbol in trackedSymbols)
        {
            var nominee = ordered.FirstOrDefault(item => item.Symbols.Contains(symbol));
            if (nominee is not null)
            {
                Add(nominee);
            }
        }

        foreach (var item in ordered)
        {
            if (selected.Count >= limit)
            {
                break;
            }

            var mentioned = item.Symbols.Where(tracked.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (mentioned.Any(symbol => perSymbol.GetValueOrDefault(symbol) >= maxPerSymbol))
            {
                continue;
            }

            Add(item);
        }

        return [.. selected
            .OrderByDescending(item => item.PublishedUtc)
            .ThenByDescending(item => item.Id)];
    }
}
