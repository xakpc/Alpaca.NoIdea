using Microsoft.Extensions.AI;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>What one model charged for, per million tokens, in USD.</summary>
/// <remarks>
/// One flat rate per token kind. Two real billing features do not fit, and both make this an
/// under-report rather than an over-report:
/// <list type="bullet">
/// <item><b>Cache writes.</b> <see cref="CachedInputPerMillion"/> is the cache <i>read</i>
/// rate. Anthropic bills a cache <i>write</i> above fresh input (2.50 or 4.00 against 2.00
/// per million on Sonnet 5, by TTL), and the usage figures do not separate the two.</item>
/// <item><b>Context tiers.</b> xAI doubles every rate above 200K input tokens in a single
/// call. Tiering correctly would have to happen when a call is recorded, because by the time
/// the ledger totals a persona the per-call context length is gone, and a cumulative total
/// crossing 200K does not mean any one call did.</item>
/// </list>
/// </remarks>
public sealed record ModelRate(decimal InputPerMillion, decimal OutputPerMillion)
{
    /// <summary>The cache <b>read</b> rate. Normally well below fresh input.</summary>
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
            // ---- seated today ----

            // Read off the published table 2026-08-31. Two seats: the proposer, whose INPUT
            // is what to watch because it loops over tools and resends the conversation each
            // turn, and the skeptic. Cache writes are 2.50 (5m) and 4.00 (1h) against the
            // 2.00 base; only the 0.20 read rate is modelled. Was 3.00/15.00 here, which
            // over-reported by 50%.
            ["claude-sonnet-5"] = new(2.00m, 10.00m) { CachedInputPerMillion = 0.20m },

            // Checked 2026-08-31, standard context tier. Above 200K input tokens xAI
            // charges double (4.00 / 1.00 / 12.00) and this reports the cheaper tier, so a
            // very long sitting is under-reported rather than over-reported.
            ["grok-4.6"] = new(2.00m, 6.00m) { CachedInputPerMillion = 0.50m },

            // Checked 2026-08-31. Note the unusual shape: input is cheap and output is dear,
            // 6x rather than the 3-5x the others charge. The quant seat is the one asked for
            // numbers and structured reasoning, so its output is what to watch.
            ["gpt-5.6-terra"] = new(2.00m, 12.00m) { CachedInputPerMillion = 0.20m },

            // ---- not seated. Kept for a quick switch back, and equally stale. ----

            // Read off the published table 2026-08-31. This entry held 15.00/75.00/1.50,
            // which is the RETIRED Opus 4.1 and Opus 4 rate: an old table carried forward
            // under a new model name. It over-reported every Opus call by exactly 3x, and
            // two measured proposer searches were reported as 2.93 and 5.65 USD when they
            // cost 0.98 and 1.88. Cache writes are 6.25 (5m) and 10.00 (1h) against the 5.00
            // base; only the 0.50 read rate is modelled.
            ["claude-opus-5"] = new(5.00m, 25.00m) { CachedInputPerMillion = 0.50m },

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
