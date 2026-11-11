using System.Text.Json.Nodes;

namespace ApiCollection.Postman.Sync;

/// <summary>
/// What matters about a collection, as plain facts keyed by where an item sits (folder name, request name), so that what we meant to
/// publish can be compared with what Postman holds afterwards even though Postman adds fields of its own (timestamps, uids, owners).
/// Item ids are deliberately NOT part of the comparison: Postman assigns its own ids when it creates a collection and does not keep the
/// ones we send (observed against the real service), so comparing by id would report a correct collection as different.
/// Differences are reported by fact path and kind only, never with values: a value may be a secret.
/// </summary>
public static class CollectionProjection
{
    /// <summary>Facts of a collection object (not wrapped in <c>{ "collection": ... }</c>).</summary>
    public static SortedDictionary<string, string> Of(string collectionJson)
    {
        var facts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (JsonNode.Parse(collectionJson) is not JsonObject collection)
        {
            return facts;
        }

        var info = collection["info"] as JsonObject;
        facts["collection.name"] = Normalise(Text(info?["name"]));
        facts["collection.description"] = Normalise(Text(info?["description"]));

        foreach (var variable in (collection["variable"] as JsonArray ?? []).OfType<JsonObject>())
        {
            facts[$"variable[{Text(variable["key"])}]"] = Normalise(Text(variable["value"]));
        }

        AddItems(collection["item"] as JsonArray ?? [], null, facts);
        return facts;
    }

    /// <summary>
    /// An item is identified by its path (<c>Folder/Request</c>). Siblings that share a name are told apart by their order
    /// (<c>Name</c>, <c>Name#2</c>, ...), which Postman keeps.
    /// </summary>
    private static void AddItems(JsonArray items, string? parentKey, SortedDictionary<string, string> facts)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items.OfType<JsonObject>())
        {
            var name = Text(item["name"]);
            var occurrence = seen[name] = seen.GetValueOrDefault(name) + 1;
            var local = occurrence == 1 ? name : $"{name}#{occurrence}";
            AddItem(item, name, parentKey is null ? local : $"{parentKey}/{local}", parentKey, facts);
        }
    }

    private static void AddItem(JsonObject item, string name, string key, string? parentKey, SortedDictionary<string, string> facts)
    {
        if (item["item"] is JsonArray children)
        {
            facts[$"folder[{key}].name"] = Normalise(name);
            if (parentKey is not null)
            {
                facts[$"folder[{key}].parent"] = parentKey;
            }

            AddItems(children, key, facts);
            return;
        }

        var request = item["request"] as JsonObject;
        var prefix = $"request[{key}]";
        facts[$"{prefix}.name"] = Normalise(name);
        facts[$"{prefix}.parent"] = parentKey ?? string.Empty;
        facts[$"{prefix}.method"] = Text(request?["method"]).ToUpperInvariant();
        facts[$"{prefix}.url"] = Normalise(request?["url"] is JsonObject url ? Text(url["raw"]) : Text(request?["url"]));
        facts[$"{prefix}.description"] = Normalise(Text(request?["description"]));
        facts[$"{prefix}.body"] = Normalise(request?["body"] is JsonObject body ? Text(body["raw"]) : string.Empty);
        facts[$"{prefix}.headers"] = string.Join(
            ";", (request?["header"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(h => $"{Text(h["key"])}={Normalise(Text(h["value"]))}{(h["disabled"] is JsonValue d && d.TryGetValue<bool>(out var off) && off ? "|disabled" : string.Empty)}")
                .OrderBy(h => h, StringComparer.Ordinal));

        var auth = request?["auth"] as JsonObject;
        facts[$"{prefix}.auth"] = Text(auth?["type"]);
        facts[$"{prefix}.authToken"] = auth?["bearer"] is JsonArray bearer
            ? Normalise(Text(bearer.OfType<JsonObject>().FirstOrDefault(e => Text(e["key"]) == "token")?["value"]))
            : string.Empty;
    }

    /// <summary>
    /// Lists how <paramref name="actual"/> differs from <paramref name="expected"/>: <c>missing</c> (expected but absent), <c>unexpected</c>
    /// (present but not expected) and <c>changed</c>, each with the fact path only. Empty means they agree.
    /// </summary>
    public static IReadOnlyList<string> Differences(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        var differences = new List<string>();
        foreach (var key in expected.Keys.Union(actual.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var has = expected.TryGetValue(key, out var wanted);
            var got = actual.TryGetValue(key, out var found);
            if (has && !got)
            {
                differences.Add($"missing {key}");
            }
            else if (!has && got)
            {
                differences.Add($"unexpected {key}");
            }
            else if (wanted != found)
            {
                differences.Add($"changed {key}");
            }
        }

        return differences;
    }

    /// <summary>Postman may answer a description as plain text or as <c>{ "content": ..., "type": ... }</c>.</summary>
    private static string Text(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonObject { } obj when obj["content"] is JsonValue content && content.TryGetValue<string>(out var inner) => inner,
        _ => string.Empty
    };

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}
