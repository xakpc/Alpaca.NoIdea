using Microsoft.Extensions.AI;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>What one model charged for, per million tokens, in USD.</summary>
public sealed record ModelRate(decimal InputPerMillion, decimal OutputPerMillion)
{
    /// <summary>Cached input is normally billed well below fresh input.</summary>
    public decimal CachedInputPerMillion { get; init; } = 0m;
}

/// <summary>
/// The price list. <b>Hardcoded, and therefore the part of the cost report that goes stale.</b>
/// </summary>
/// <remarks>
/// <para>
/// No provider returns money. They return token counts, which is why
/// <see cref="TokenLedger"/> counts tokens as fact and dollars as an estimate. Check these
/// rates against the provider's price page before quoting a figure to anyone.
/// </para>
/// <para>
/// <b>Server-side tools are not in here.</b> Hosted web search is usually billed per call,
/// outside the token counts, so a room that searches heavily costs more than this reports.
/// The undercount is silent, so treat these numbers as a floor.
/// </para>
/// </remarks>
public static class ModelPricing
{
    private static readonly Dictionary<string, ModelRate> Rates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5"] = new(15.00m, 75.00m) { CachedInputPerMillion = 1.50m },
            ["claude-sonnet-5"] = new(3.00m, 15.00m) { CachedInputPerMillion = 0.30m },
            ["claude-haiku-4-5-20251001"] = new(1.00m, 5.00m) { CachedInputPerMillion = 0.10m },
            ["gpt-5"] = new(1.25m, 10.00m) { CachedInputPerMillion = 0.125m },
            ["grok-4"] = new(3.00m, 15.00m),
        };

    /// <summary>The rate for a model, or null when the model is not priced here.</summary>
    public static ModelRate? For(string? modelId) =>
        modelId is not null && Rates.TryGetValue(modelId, out var rate) ? rate : null;

    /// <summary>Estimated USD, or null when the model has no rate. Never guesses.</summary>
    public static decimal? Estimate(string? modelId, long input, long output, long cachedInput)
    {
        if (For(modelId) is not { } rate)
        {
            return null;
        }

        var freshInput = Math.Max(0, input - cachedInput);

        return ((freshInput * rate.InputPerMillion)
                + (cachedInput * rate.CachedInputPerMillion)
                + (output * rate.OutputPerMillion)) / 1_000_000m;
    }
}

/// <summary>What one persona spent.</summary>
public sealed record PersonaCost
{
    public required string Persona { get; init; }
    public required string Model { get; init; }
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long OutputTokens { get; init; }

    /// <summary>Null when the model has no rate in <see cref="ModelPricing"/>.</summary>
    public decimal? EstimatedUsd =>
        ModelPricing.Estimate(Model, InputTokens, OutputTokens, CachedInputTokens);

    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>What a war-room session cost in total.</summary>
public sealed record RoomCost
{
    public IReadOnlyList<PersonaCost> PerPersona { get; init; } = [];

    public long Calls => PerPersona.Sum(cost => cost.Calls);

    public long TotalTokens => PerPersona.Sum(cost => cost.TotalTokens);

    /// <summary>The sum of what could be priced. Null when nothing could.</summary>
    public decimal? EstimatedUsd
    {
        get
        {
            var priced = PerPersona.Select(cost => cost.EstimatedUsd).OfType<decimal>().ToArray();
            return priced.Length == 0 ? null : priced.Sum();
        }
    }

    /// <summary>Models that ran but could not be priced. The estimate excludes them.</summary>
    public IReadOnlyList<string> UnpricedModels =>
        PerPersona
            .Where(cost => cost.EstimatedUsd is null && cost.Calls > 0)
            .Select(cost => cost.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public override string ToString() =>
        EstimatedUsd is { } usd
            ? $"{Calls} calls, {TotalTokens:N0} tokens, about {usd:F4} USD"
            : $"{Calls} calls, {TotalTokens:N0} tokens, price unknown";
}

/// <summary>
/// Counts what the room spends, per persona and model.
/// </summary>
/// <remarks>
/// Token counts come from <see cref="ChatResponse.Usage"/> and are fact. A provider that
/// reports no usage contributes zero, which under-reports rather than throws — so a missing
/// number never stops a trading cycle, and the ledger is a floor rather than a guarantee.
/// </remarks>
public sealed class TokenLedger
{
    private readonly Dictionary<(string Persona, string Model), Entry> _entries = [];
    private readonly Lock _gate = new();

    private sealed class Entry
    {
        public long Calls;
        public long Input;
        public long CachedInput;
        public long Output;
    }

    /// <summary>Records one model call. Safe to call with a null response or null usage.</summary>
    public void Record(string persona, string model, ChatResponse? response)
    {
        lock (_gate)
        {
            var key = (persona, model);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Calls++;

            if (response?.Usage is not { } usage)
            {
                return;
            }

            entry.Input += usage.InputTokenCount ?? 0;
            entry.Output += usage.OutputTokenCount ?? 0;
            entry.CachedInput += usage.CachedInputTokenCount ?? 0;
        }
    }

    /// <summary>Reads the totals and resets, so a caller folding these in cannot double-count.</summary>
    public RoomCost Drain()
    {
        lock (_gate)
        {
            var snapshot = SnapshotLocked();
            _entries.Clear();
            return snapshot;
        }
    }

    public RoomCost Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    private RoomCost SnapshotLocked()
    {
        {
            return new RoomCost
            {
                PerPersona = _entries
                    .Select(pair => new PersonaCost
                    {
                        Persona = pair.Key.Persona,
                        Model = pair.Key.Model,
                        Calls = pair.Value.Calls,
                        InputTokens = pair.Value.Input,
                        CachedInputTokens = pair.Value.CachedInput,
                        OutputTokens = pair.Value.Output,
                    })
                    .OrderBy(cost => cost.Persona, StringComparer.Ordinal)
                    .ToArray(),
            };
        }
    }

    /// <summary>Folds another ledger in, so a cycle can total several sessions.</summary>
    public void Add(RoomCost other)
    {
        ArgumentNullException.ThrowIfNull(other);

        lock (_gate)
        {
            foreach (var cost in other.PerPersona)
            {
                var key = (cost.Persona, cost.Model);
                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }

                entry.Calls += cost.Calls;
                entry.Input += cost.InputTokens;
                entry.CachedInput += cost.CachedInputTokens;
                entry.Output += cost.OutputTokens;
            }
        }
    }
}
