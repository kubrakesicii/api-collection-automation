using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApiCollection.Postman.Sync;

/// <summary>
/// Replaces secret-looking values in a collection before a copy of it is written to disk: values of variables named like tokens,
/// passwords or API keys, the secret fields of auth blocks, and authorization-type headers. A value that is empty or a variable
/// reference (<c>{{token}}</c>) is not a secret and is kept. This is a best-effort safety net, not a guarantee: a secret hidden in a URL,
/// a body or an unusual field is not recognised. It reports <b>where</b> it redacted, never what.
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "REDACTED";

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "api-key", "apikey", "cookie", "set-cookie"
    };

    /// <summary>Returns the redacted collection (compact JSON) and the paths that were redacted. The input is not modified.</summary>
    public static (string Json, IReadOnlyList<string> RedactedPaths) Redact(string collectionJson)
    {
        var root = JsonNode.Parse(collectionJson) ?? throw new ArgumentException("The collection is not JSON.", nameof(collectionJson));
        var paths = new List<string>();

        if (root is JsonObject collection)
        {
            RedactVariables(collection, string.Empty, paths);
            RedactAuth(collection["auth"], "auth", paths);
            RedactItems(collection["item"] as JsonArray, string.Empty, paths);
        }

        return (root.ToJsonString(), paths);
    }

    private static void RedactItems(JsonArray? items, string parent, List<string> paths)
    {
        foreach (var item in (items ?? []).OfType<JsonObject>())
        {
            var path = parent.Length == 0 ? (string?)item["name"] ?? "?" : $"{parent}/{(string?)item["name"] ?? "?"}";
            RedactVariables(item, $"item[{path}].", paths);
            RedactAuth(item["auth"], $"item[{path}].auth", paths);

            if (item["request"] is JsonObject request)
            {
                RedactAuth(request["auth"], $"item[{path}].request.auth", paths);
                RedactHeaders(request["header"] as JsonArray, $"item[{path}].request.header", paths);
            }

            RedactItems(item["item"] as JsonArray, path, paths);
        }
    }

    private static void RedactVariables(JsonObject owner, string prefix, List<string> paths)
    {
        foreach (var variable in (owner["variable"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = (string?)variable["key"] ?? string.Empty;
            if (SecretName().IsMatch(key))
            {
                Redact(variable, "value", $"{prefix}variable[{key}]", paths);
            }
        }
    }

    private static void RedactHeaders(JsonArray? headers, string prefix, List<string> paths)
    {
        foreach (var header in (headers ?? []).OfType<JsonObject>())
        {
            var key = (string?)header["key"] ?? string.Empty;
            if (SecretHeaders.Contains(key) || SecretName().IsMatch(key))
            {
                Redact(header, "value", $"{prefix}[{key}]", paths);
            }
        }
    }

    /// <summary>An auth block is <c>{ "type": "bearer", "bearer": [ { "key": "token", "value": "..." } ] }</c>; every entry that carries a secret is redacted.</summary>
    private static void RedactAuth(JsonNode? auth, string prefix, List<string> paths)
    {
        if (auth is not JsonObject block)
        {
            return;
        }

        var type = (string?)block["type"] ?? string.Empty;
        foreach (var (name, value) in block)
        {
            if (name == "type" || value is not JsonArray entries)
            {
                continue;
            }

            foreach (var entry in entries.OfType<JsonObject>())
            {
                var key = (string?)entry["key"] ?? string.Empty;
                if (SecretName().IsMatch(key) || (type == "apikey" && key == "value"))
                {
                    Redact(entry, "value", $"{prefix}.{name}[{key}]", paths);
                }
            }
        }
    }

    private static void Redact(JsonObject owner, string property, string path, List<string> paths)
    {
        if (owner[property] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 && !Reference().IsMatch(text))
        {
            owner[property] = Placeholder;
            paths.Add(path);
        }
    }

    [GeneratedRegex("(token|secret|passw|pwd|credential|api[-_]?key|apikey|authorization|bearer)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"^\s*\{\{[^{}]+\}\}\s*$")]
    private static partial Regex Reference();
}
