using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ApiCollection.Postman.CloudApi.PostmanApiTransport;

namespace ApiCollection.Postman.CloudApi;

/// <summary>
/// The Postman API calls needed to keep one collection up to date: read a workspace, find collections by name, read, create and
/// update a collection. There is deliberately <b>no delete</b>. Every outcome is a result, never an exception (except the caller
/// cancelling), and no result ever contains the API key. Creating and updating write to Postman; callers go through
/// <see cref="PostmanCollectionRepository"/>, which decides whether a write is safe.
/// </summary>
public sealed partial class PostmanCloudClient(HttpClient http, PostmanCloudOptions options)
{
    /// <summary>The schema address Postman asks for in <c>info.schema</c>.</summary>
    public const string CollectionSchema = "https://schema.postman.com/json/collection/v2.1.0/collection.json";

    private const int LargeResponseBytes = 50 * 1024 * 1024;

    // ---- reads ----

    public Task<PostmanApiResult<PostmanWorkspaceInfo>> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        !IdPattern().IsMatch(workspaceId)
            ? Task.FromResult(Fail<PostmanWorkspaceInfo>(PostmanApiStatus.NotConfigured, "The workspace id may only contain letters, digits and '-'. Nothing was sent."))
            : SendAsync(
                HttpMethod.Get, $"workspaces/{Uri.EscapeDataString(workspaceId)}", null, SmallResponseBytes, notFoundOnBadRequest: false,
                reply =>
                {
                    var workspace = Section(reply.Body, "workspace");
                    return workspace is null
                        ? NotAPostmanAnswer<PostmanWorkspaceInfo>(reply)
                        : Ok(new PostmanWorkspaceInfo(Text(workspace.Value, "id") ?? workspaceId, Text(workspace.Value, "name"), Text(workspace.Value, "type")));
                },
                cancellationToken);

    /// <summary>
    /// Collections of a workspace whose name matches exactly. An unknown workspace is <b>not</b> an error for Postman (it answers with an
    /// empty list), so check the workspace with <see cref="GetWorkspaceAsync"/> before trusting an empty answer.
    /// </summary>
    public Task<PostmanApiResult<IReadOnlyList<PostmanCollectionRef>>> FindCollectionsByNameAsync(
        string workspaceId, string name, CancellationToken cancellationToken = default) =>
        !IdPattern().IsMatch(workspaceId)
            ? Task.FromResult(Fail<IReadOnlyList<PostmanCollectionRef>>(PostmanApiStatus.NotConfigured, "The workspace id may only contain letters, digits and '-'. Nothing was sent."))
            : SendAsync(
                HttpMethod.Get, $"collections?workspace={Uri.EscapeDataString(workspaceId)}&name={Uri.EscapeDataString(name)}", null, LargeResponseBytes,
                notFoundOnBadRequest: false,
                reply =>
                {
                    var list = new List<PostmanCollectionRef>();
                    try
                    {
                        using var document = JsonDocument.Parse(reply.Body);
                        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("collections", out var collections)
                            || collections.ValueKind != JsonValueKind.Array)
                        {
                            return NotAPostmanAnswer<IReadOnlyList<PostmanCollectionRef>>(reply);
                        }

                        foreach (var item in collections.EnumerateArray())
                        {
                            if (Text(item, "uid") is { } uid && Text(item, "name") is { } listed)
                            {
                                list.Add(new PostmanCollectionRef(uid, Text(item, "id"), listed));
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        return NotAPostmanAnswer<IReadOnlyList<PostmanCollectionRef>>(reply);
                    }

                    return Ok<IReadOnlyList<PostmanCollectionRef>>(list);
                },
                cancellationToken);

    /// <summary>Reads a collection; the value is the collection object as JSON text.</summary>
    public Task<PostmanApiResult<string>> GetCollectionAsync(string collectionId, CancellationToken cancellationToken = default) =>
        !IdPattern().IsMatch(collectionId)
            ? Task.FromResult(Fail<string>(PostmanApiStatus.NotConfigured, "The collection id may only contain letters, digits and '-'. Nothing was sent."))
            : SendAsync(
                HttpMethod.Get, $"collections/{Uri.EscapeDataString(collectionId)}", null, LargeResponseBytes,
                // Postman documents a missing collection as HTTP 400 ("Collection Not Found") for this call, so a 400 that says so is a "not found".
                notFoundOnBadRequest: true,
                reply =>
                {
                    try
                    {
                        using var document = JsonDocument.Parse(reply.Body);
                        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("collection", out var collection)
                            && collection.ValueKind == JsonValueKind.Object
                            ? Ok(collection.GetRawText())
                            : NotAPostmanAnswer<string>(reply);
                    }
                    catch (JsonException)
                    {
                        return NotAPostmanAnswer<string>(reply);
                    }
                },
                cancellationToken);

    // ---- writes ----

    /// <summary>Creates a collection in the workspace (a write). Returns where Postman put it.</summary>
    public Task<PostmanApiResult<PostmanCollectionRef>> CreateCollectionAsync(
        string workspaceId, string collectionJson, CancellationToken cancellationToken = default)
    {
        if (!IdPattern().IsMatch(workspaceId))
        {
            return Task.FromResult(Fail<PostmanCollectionRef>(PostmanApiStatus.NotConfigured, "The workspace id may only contain letters, digits and '-'. Nothing was sent."));
        }

        var payload = Payload(collectionJson);
        return payload.Failure is { } failure
            ? Task.FromResult(failure)
            : SendAsync(HttpMethod.Post, $"collections?workspace={Uri.EscapeDataString(workspaceId)}", payload.Body, SmallResponseBytes, false, ParseReference, cancellationToken);
    }

    /// <summary>
    /// Replaces the collection's content (a write). Postman keeps an item's id only when the payload carries it; items without an id
    /// are deleted and recreated under new ids, which is why published collections carry stable ids.
    /// </summary>
    public Task<PostmanApiResult<PostmanCollectionRef>> UpdateCollectionAsync(
        string collectionId, string collectionJson, CancellationToken cancellationToken = default)
    {
        if (!IdPattern().IsMatch(collectionId))
        {
            return Task.FromResult(Fail<PostmanCollectionRef>(PostmanApiStatus.NotConfigured, "The collection id may only contain letters, digits and '-'. Nothing was sent."));
        }

        var payload = Payload(collectionJson);
        return payload.Failure is { } failure
            ? Task.FromResult(failure)
            : SendAsync(HttpMethod.Put, $"collections/{Uri.EscapeDataString(collectionId)}", payload.Body, SmallResponseBytes, false, ParseReference, cancellationToken);
    }

    // ---- payload ----

    /// <summary>
    /// Wraps the collection for Postman: <c>{ "collection": ... }</c> with the schema address Postman asks for, and without the ids of
    /// the collection itself (Postman rejects a payload that carries another collection's <c>_postman_id</c>, <c>id</c> or <c>uid</c>).
    /// Folder and request ids are kept: they are what lets Postman keep those items.
    /// </summary>
    private (string? Body, PostmanApiResult<PostmanCollectionRef>? Failure) Payload(string collectionJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(collectionJson);
        }
        catch (JsonException)
        {
            return (null, Fail<PostmanCollectionRef>(PostmanApiStatus.NotConfigured, "The collection is not valid JSON. Nothing was sent."));
        }

        if (root is not JsonObject collection || collection["info"] is not JsonObject info
            || info["name"] is not JsonValue name || !name.TryGetValue<string>(out var collectionName) || string.IsNullOrWhiteSpace(collectionName)
            || collection["item"] is not JsonArray)
        {
            return (null, Fail<PostmanCollectionRef>(PostmanApiStatus.NotConfigured, "The JSON is not a Postman collection (it needs info.name and item). Nothing was sent."));
        }

        info["schema"] = CollectionSchema;
        foreach (var own in new[] { "_postman_id", "id", "uid" })
        {
            info.Remove(own);
        }

        collection.Remove("id");
        collection.Remove("uid");

        var body = new JsonObject { ["collection"] = collection }.ToJsonString();
        return Encoding.UTF8.GetByteCount(body) > options.MaxPayloadBytes
            ? (null, Fail<PostmanCollectionRef>(PostmanApiStatus.NotConfigured, $"The collection is larger than {options.MaxPayloadBytes / (1024 * 1024)} MB, which Postman does not accept. Nothing was sent."))
            : (body, null);
    }

    private PostmanApiResult<PostmanCollectionRef> ParseReference(PostmanReply reply)
    {
        var collection = Section(reply.Body, "collection");
        if (collection is null)
        {
            return NotAPostmanAnswer<PostmanCollectionRef>(reply);
        }

        return Text(collection.Value, "uid") is { } uid
            ? Ok(new PostmanCollectionRef(uid, Text(collection.Value, "id"), Text(collection.Value, "name")))
            : Fail<PostmanCollectionRef>(
                PostmanApiStatus.UnexpectedResponse,
                $"Postman answered HTTP {(int)reply.Status} but the collection's id could not be read. If the collection was created, the next run finds it again by its marker.",
                (int)reply.Status);
    }

    // ---- sending and classifying ----

    private async Task<PostmanApiResult<T>> SendAsync<T>(
        HttpMethod method, string pathAndQuery, string? body, int maxResponseBytes, bool notFoundOnBadRequest,
        Func<PostmanReply, PostmanApiResult<T>> parse, CancellationToken cancellationToken)
    {
        var apiKey = (options.ApiKey ?? string.Empty).Trim();
        if (apiKey.Length == 0)
        {
            return Fail<T>(PostmanApiStatus.NotConfigured, "No Postman API key was provided. Nothing was sent.");
        }

        if (!IsPlausibleApiKey(apiKey))
        {
            return Fail<T>(PostmanApiStatus.InvalidApiKey, "The API key contains whitespace, control or non-ASCII characters, so it looks damaged. Nothing was sent.");
        }

        if (options.BaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            return Fail<T>(PostmanApiStatus.NotConfigured, "The Postman API address must use https; the API key is never sent over plain http. Nothing was sent.");
        }

        var (reply, failure) = await PostmanApiTransport.SendAsync(
            http, method, new Uri(options.BaseAddress, pathAndQuery), apiKey, body, options.Timeout, maxResponseBytes, cancellationToken);

        if (failure is not null)
        {
            return Fail<T>(
                failure.Kind == TransportFailureKind.Timeout ? PostmanApiStatus.Timeout : PostmanApiStatus.NetworkError,
                Clean(failure.Message, apiKey));
        }

        var result = reply!.Status == HttpStatusCode.OK ? parse(reply) : Classify<T>(reply, apiKey, notFoundOnBadRequest);
        return result with { Message = Clean(result.Message, apiKey) };
    }

    private static PostmanApiResult<T> Classify<T>(PostmanReply reply, string apiKey, bool notFoundOnBadRequest)
    {
        var status = (int)reply.Status;
        var remote = RemoteMessage(reply.Body);
        var said = remote is null ? string.Empty : $" Postman says: {Clean(remote, apiKey)}";
        var notFound = notFoundOnBadRequest && status == 400 && remote is not null
            && (remote.Contains("not found", StringComparison.OrdinalIgnoreCase) || remote.Contains("notfound", StringComparison.OrdinalIgnoreCase));

        var (kind, message) = status switch
        {
            401 => (PostmanApiStatus.InvalidApiKey, $"Postman rejected the API key (HTTP 401).{said}"),
            403 => (PostmanApiStatus.AccessDenied, $"Postman denied access (HTTP 403).{said}"),
            404 => (PostmanApiStatus.NotFound, $"Postman could not find it (HTTP 404).{said}"),
            400 when notFound => (PostmanApiStatus.NotFound, $"Postman could not find it (HTTP 400).{said}"),
            400 => (PostmanApiStatus.Rejected, $"Postman rejected the request (HTTP 400).{said}"),
            409 => (PostmanApiStatus.Conflict, $"Postman reported a conflict (HTTP 409).{said}"),
            429 => (PostmanApiStatus.RateLimited, $"Postman is rate limiting requests (HTTP 429).{said}"),
            >= 500 and <= 599 => (PostmanApiStatus.ServerError, $"Postman reported a server error (HTTP {status}).{said}"),
            >= 300 and <= 399 => (PostmanApiStatus.UnexpectedResponse, $"Postman answered with a redirect (HTTP {status}); it was not followed."),
            _ => (PostmanApiStatus.UnexpectedResponse, $"Unexpected answer from Postman (HTTP {status}).{said}")
        };

        return new PostmanApiResult<T> { Status = kind, Message = message, HttpStatus = status, RetryAfter = kind == PostmanApiStatus.RateLimited ? reply.RetryAfter : null };
    }

    private static PostmanApiResult<T> Ok<T>(T value) => new() { Status = PostmanApiStatus.Ok, Message = "OK", Value = value, HttpStatus = 200 };

    private static PostmanApiResult<T> Fail<T>(PostmanApiStatus status, string message, int? httpStatus = null) =>
        new() { Status = status, Message = message, HttpStatus = httpStatus };

    private static PostmanApiResult<T> NotAPostmanAnswer<T>(PostmanReply reply) =>
        Fail<T>(PostmanApiStatus.UnexpectedResponse, $"HTTP {(int)reply.Status} was returned but the body is not a Postman API response (a proxy or captive portal in the way?).", (int)reply.Status);

    private static JsonElement? Section(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(property, out var section)
                && section.ValueKind == JsonValueKind.Object
                ? section.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,128}$")]
    private static partial Regex IdPattern();
}
