using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ApiCollection.Postman.Tests;

/// <summary>
/// An in-memory Postman that behaves as documented for the calls this project uses, so create/update flows can be tested without the network:
/// workspaces, listing by name (an unknown workspace gives an empty list), reading (a missing collection is HTTP 400), creating and replacing.
/// Like the real service it gives items without an id a NEW id on every replace, and it rejects a payload carrying the collection's own ids.
/// </summary>
internal sealed class FakePostmanCloud : HttpMessageHandler
{
    public const string ApiKey = "PMAK-fake-test-key-not-real";
    public const string SchemaUrl = "https://schema.postman.com/json/collection/v2.1.0/collection.json";

    public sealed record Call(HttpMethod Method, string PathAndQuery, string? Body, string? Key);

    public sealed class Stored
    {
        public required string Id { get; init; }

        public required string Uid { get; init; }

        public required string WorkspaceId { get; init; }

        public required JsonObject Collection { get; set; }
    }

    private int _counter;

    public List<Call> Calls { get; } = [];

    public HashSet<string> Workspaces { get; } = [];

    public List<Stored> Collections { get; } = [];

    /// <summary>Lets a test answer a call itself (to inject an error); return null to fall through to the normal behaviour.</summary>
    public Func<Call, HttpResponseMessage?>? Intercept { get; set; }

    public IEnumerable<Call> Writes => Calls.Where(c => c.Method != HttpMethod.Get);

    public Stored Add(string workspaceId, string name, bool managed, string? id = null)
    {
        var identifier = id ?? NextId();
        var stored = new Stored
        {
            Id = identifier,
            Uid = $"1234567-{identifier}",
            WorkspaceId = workspaceId,
            Collection = new JsonObject
            {
                ["info"] = new JsonObject { ["name"] = name, ["schema"] = SchemaUrl },
                ["item"] = new JsonArray(),
                ["variable"] = managed
                    ? new JsonArray(new JsonObject { ["key"] = "automationManaged", ["value"] = "api-collection-automation" })
                    : new JsonArray()
            }
        };
        Collections.Add(stored);
        return stored;
    }

    private string NextId() => $"00000000-0000-4000-8000-{++_counter:000000000000}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        var key = request.Headers.TryGetValues("X-API-Key", out var values) ? values.Single() : null;
        var call = new Call(request.Method, request.RequestUri!.PathAndQuery, body, key);
        Calls.Add(call);

        if (Intercept?.Invoke(call) is { } injected)
        {
            return Task.FromResult(injected);
        }

        return Task.FromResult(key != ApiKey ? Error(HttpStatusCode.Unauthorized, "AuthenticationError", "Invalid API Key. Every request requires a valid API Key to be sent.") : Route(request, call));
    }

    private HttpResponseMessage Route(HttpRequestMessage request, Call call)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        var query = ParseQuery(request.RequestUri.Query);

        if (request.Method == HttpMethod.Get && path is ["workspaces", var workspaceId])
        {
            return Workspaces.Contains(workspaceId)
                ? Json(HttpStatusCode.OK, new JsonObject { ["workspace"] = new JsonObject { ["id"] = workspaceId, ["name"] = "Test Workspace", ["type"] = "team" } })
                : Error(HttpStatusCode.NotFound, "instanceNotFoundError", "Workspace not found");
        }

        if (request.Method == HttpMethod.Get && path is ["collections"])
        {
            var matches = Collections
                .Where(c => query.GetValueOrDefault("workspace") == c.WorkspaceId)
                .Where(c => !query.TryGetValue("name", out var name) || (string?)c.Collection["info"]!["name"] == name)
                .Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id, ["name"] = (string?)c.Collection["info"]!["name"], ["owner"] = "1234567", ["uid"] = c.Uid, ["isPublic"] = false
                });
            return Json(HttpStatusCode.OK, new JsonObject { ["collections"] = new JsonArray(matches.ToArray()) });
        }

        if (request.Method == HttpMethod.Get && path is ["collections", var readId])
        {
            return Find(readId) is { } found
                ? Json(HttpStatusCode.OK, new JsonObject { ["collection"] = found.Collection.DeepClone() })
                : Error(HttpStatusCode.BadRequest, "collectionNotFoundError", "Collection Not Found");
        }

        if (request.Method == HttpMethod.Post && path is ["collections"])
        {
            if (!Workspaces.Contains(query.GetValueOrDefault("workspace") ?? string.Empty))
            {
                return Error(HttpStatusCode.BadRequest, "malformedRequestError", "Invalid workspace");
            }

            if (Validate(call.Body) is { } rejected)
            {
                return rejected;
            }

            var id = NextId();
            var stored = new Stored { Id = id, Uid = $"1234567-{id}", WorkspaceId = query["workspace"], Collection = Normalise(call.Body!, null) };
            Collections.Add(stored);
            return Reference(stored);
        }

        if (request.Method == HttpMethod.Put && path is ["collections", var updateId])
        {
            if (Find(updateId) is not { } existing)
            {
                return Json(HttpStatusCode.NotFound, new JsonObject { ["type"] = "about:blank", ["title"] = "Not Found", ["detail"] = "Collection not found", ["status"] = 404 });
            }

            if (Validate(call.Body) is { } rejected)
            {
                return rejected;
            }

            existing.Collection = Normalise(call.Body!, existing);
            return Reference(existing);
        }

        return Error(HttpStatusCode.NotFound, "routeNotFound", $"{request.Method} {request.RequestUri.AbsolutePath} is not part of this fake");
    }

    private Stored? Find(string id) => Collections.FirstOrDefault(c => c.Uid == id || c.Id == id);

    private static HttpResponseMessage? Validate(string? body)
    {
        var info = JsonNode.Parse(body ?? "{}")?["collection"]?["info"];
        if (info is null || info["name"] is null)
        {
            return Error(HttpStatusCode.BadRequest, "malformedRequestError", "collection.info.name is required");
        }

        if ((string?)info["schema"] != SchemaUrl)
        {
            return Error(HttpStatusCode.BadRequest, "malformedRequestError", "info.schema must be " + SchemaUrl);
        }

        return info["_postman_id"] is not null || info["id"] is not null || info["uid"] is not null
            ? Error(HttpStatusCode.BadRequest, "malformedRequestError", "The collection's own ids must not be sent")
            : null;
    }

    /// <summary>Stores the collection; items that arrive without an id get a fresh one, as the real service does when it replaces a collection.</summary>
    private JsonObject Normalise(string body, Stored? existing)
    {
        var collection = (JsonObject)JsonNode.Parse(body)!["collection"]!.DeepClone();
        AssignIds(collection["item"]!.AsArray());
        if (existing is not null)
        {
            ((JsonObject)collection["info"]!)["_postman_id"] = existing.Id;
        }

        return collection;
    }

    private void AssignIds(JsonArray items)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            item["id"] ??= $"fresh-{Guid.NewGuid()}";
            if (item["item"] is JsonArray children)
            {
                AssignIds(children);
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : string.Empty));

    private static HttpResponseMessage Reference(Stored stored) => Json(HttpStatusCode.OK, new JsonObject
    {
        ["collection"] = new JsonObject { ["id"] = stored.Id, ["name"] = (string?)stored.Collection["info"]!["name"], ["uid"] = stored.Uid }
    });

    public static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string name, string message) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["name"] = name, ["message"] = message } });

    /// <summary>Request and folder ids of a stored collection, by name (folders as "Folder", requests as "Folder/Request").</summary>
    public static Dictionary<string, string> ItemIds(Stored stored)
    {
        var ids = new Dictionary<string, string>();
        foreach (var folder in stored.Collection["item"]!.AsArray().OfType<JsonObject>())
        {
            var folderName = (string)folder["name"]!;
            ids[folderName] = (string)folder["id"]!;
            foreach (var request in folder["item"]!.AsArray().OfType<JsonObject>())
            {
                ids[$"{folderName}/{(string)request["name"]!}"] = (string)request["id"]!;
            }
        }

        return ids;
    }
}
