using System.Text.Json;

namespace Xakpc.Alpaca.NøIdea.Storage;

/// <summary>
/// Reads the raw Alpaca API pages that the <c>scripts/acquire-*.sh</c> scripts write.
/// </summary>
/// <remarks>
/// <para>
/// A raw file is <b>not</b> one JSON document. Each script appends one page object per API
/// page, so <c>SPY.15Min.json</c> holds 77 concatenated objects and
/// <see cref="JsonSerializer"/> over the whole file fails on the second.
/// <see cref="JsonReaderOptions.AllowMultipleValues"/> is what makes the concatenated form
/// readable. The pages are pretty-printed when the Alpaca CLI wrote them and compact when
/// curl did; both parse the same way.
/// </para>
/// <para>
/// Two entry points, because the endpoints answer different shapes. Stock bars and news
/// arrive as an array — <c>"bars": [ … ]</c>. Option bars arrive as an object keyed by
/// contract symbol — <c>"bars": { "AAPL240119C00180000": [ … ] }</c> — because one request
/// covers many contracts.
/// </para>
/// <para>
/// Restated from <c>FeatureGenerator/RawJsonPages.cs</c> on branch
/// <c>phase-3-historical-ml-expert</c>, which stays out of this project by decision.
/// </para>
/// </remarks>
public static class RawJsonPages
{
    /// <summary>
    /// Calls <paramref name="onItem"/> for every element of the
    /// <paramref name="arrayProperty"/> array of every page in the file, in file order.
    /// </summary>
    /// <remarks>
    /// A callback rather than an iterator: <see cref="Utf8JsonReader"/> is a ref struct and
    /// cannot cross a <c>yield</c> boundary. The callback form also bounds the lifetime of
    /// each <see cref="JsonElement"/> to the call, so a caller cannot keep one past the
    /// disposal of its document.
    /// </remarks>
    public static void ForEachItem(string path, string arrayProperty, Action<JsonElement> onItem)
    {
        ArgumentNullException.ThrowIfNull(onItem);

        var bytes = File.ReadAllBytes(path);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });

        while (reader.Read())
        {
            using var page = JsonDocument.ParseValue(ref reader);
            if (!page.RootElement.TryGetProperty(arrayProperty, out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in array.EnumerateArray())
            {
                onItem(item);
            }
        }
    }

    /// <summary>
    /// Calls <paramref name="onItem"/> for every element of every array inside the
    /// <paramref name="mapProperty"/> <b>object</b>, passing the key the array hangs from.
    /// </summary>
    public static void ForEachInMap(string path, string mapProperty, Action<string, JsonElement> onItem)
    {
        ArgumentNullException.ThrowIfNull(onItem);

        var bytes = File.ReadAllBytes(path);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });

        while (reader.Read())
        {
            using var page = JsonDocument.ParseValue(ref reader);
            if (!page.RootElement.TryGetProperty(mapProperty, out var map)
                || map.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in entry.Value.EnumerateArray())
                {
                    onItem(entry.Name, item);
                }
            }
        }
    }
}
