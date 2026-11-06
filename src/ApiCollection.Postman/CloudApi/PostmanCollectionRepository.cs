using System.Text.Json;
using System.Text.RegularExpressions;
using ApiCollection.Core.Sync;
using ApiCollection.Postman.Generation;

namespace ApiCollection.Postman.CloudApi;

public sealed record PostmanPublishRequest
{
    public required string WorkspaceId { get; init; }

    /// <summary>The collection's name; it must equal <c>info.name</c> of <see cref="CollectionJson"/>.</summary>
    public required string CollectionName { get; init; }

    /// <summary>The collection as generated with <see cref="PostmanCollectionOptions.IdNamespace"/> set (stable ids and the marker).</summary>
    public required string CollectionJson { get; init; }

    /// <summary>The uid returned by an earlier publish, when the caller has kept it. Without it the collection is looked up by name and marker.</summary>
    public string? KnownCollectionId { get; init; }

    /// <summary>The verdict of the sync safety gate (ADR 0003). A blocked decision never reaches Postman.</summary>
    public required SyncDecision Decision { get; init; }

    /// <summary>Only read from Postman and report what would happen; never write.</summary>
    public bool DryRun { get; init; }
}

public enum PostmanPublishOutcome
{
    /// <summary>The collection did not exist and was created.</summary>
    Created,

    /// <summary>The existing collection was updated in place (same id).</summary>
    Updated,

    /// <summary>The collection this tool maintains was found; nothing needed writing.</summary>
    Adopted,

    /// <summary>Nothing changed and the collection is known: nothing was sent.</summary>
    NoChanges,

    /// <summary>Dry run: the collection would be created.</summary>
    WouldCreate,

    /// <summary>Dry run: the collection would be updated.</summary>
    WouldUpdate,

    /// <summary>The sync safety gate blocked it; nothing was sent.</summary>
    Blocked,

    /// <summary>See <see cref="PostmanPublishResult.Failure"/>. Nothing was changed unless the message says otherwise.</summary>
    Failed
}

public enum PostmanPublishFailure
{
    None,

    /// <summary>The request itself is unusable. Nothing was sent.</summary>
    InvalidInput,

    /// <summary>A Postman call failed; see <see cref="PostmanPublishResult.ApiStatus"/>.</summary>
    Api,

    /// <summary>A collection with this name exists in the workspace but is not maintained by this tool. It is never overwritten, and no second one is created.</summary>
    NameConflict,

    /// <summary>More than one collection in the workspace is maintained by this tool under this name; which one is meant is not guessed.</summary>
    AmbiguousCollections,

    /// <summary>The known collection no longer exists (deleted in Postman). A new one is not created behind the user's back.</summary>
    CollectionMissing
}

public sealed record PostmanPublishResult
{
    public required PostmanPublishOutcome Outcome { get; init; }

    public PostmanPublishFailure Failure { get; init; }

    public required string Message { get; init; }

    /// <summary>The unique id to keep and pass back as <see cref="PostmanPublishRequest.KnownCollectionId"/>.</summary>
    public string? CollectionUid { get; init; }

    public PostmanApiStatus? ApiStatus { get; init; }

    public int? HttpStatus { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public bool Succeeded => Outcome is not (PostmanPublishOutcome.Failed or PostmanPublishOutcome.Blocked);

    /// <summary>True when something was written to Postman (created or updated).</summary>
    public bool Wrote => Outcome is PostmanPublishOutcome.Created or PostmanPublishOutcome.Updated;
}

/// <summary>
/// Keeps one collection in a workspace up to date: the first publish creates it, later publishes update the <b>same</b> collection.
/// It is the only way this tool writes to Postman, and it fails closed: a blocked safety decision, an unverifiable workspace, a
/// same-named collection it does not own, an ambiguous match or a vanished collection all stop it without writing and without
/// creating a second collection.
/// </summary>
public sealed partial class PostmanCollectionRepository(PostmanCloudClient client)
{
    public async Task<PostmanPublishResult> PublishAsync(PostmanPublishRequest request, CancellationToken cancellationToken = default)
    {
        var invalid = Validate(request);
        if (invalid is not null)
        {
            return Failed(PostmanPublishFailure.InvalidInput, $"{invalid} Nothing was sent.");
        }

        if (request.Decision.Outcome == SyncOutcome.Blocked)
        {
            var reasons = string.Join(", ", request.Decision.Violations.Select(v => v.Code));
            return new PostmanPublishResult
            {
                Outcome = PostmanPublishOutcome.Blocked,
                Message = $"The sync safety gate blocked this ({(reasons.Length == 0 ? "no reason given" : reasons)}). Nothing was sent."
            };
        }

        if (request.Decision.Outcome == SyncOutcome.NoChanges && request.KnownCollectionId is not null)
        {
            return new PostmanPublishResult
            {
                Outcome = PostmanPublishOutcome.NoChanges,
                Message = "Nothing changed, so nothing was sent.",
                CollectionUid = request.KnownCollectionId
            };
        }

        // Postman answers an unknown workspace with an empty collection list, not an error. Without this check "no collection found"
        // could mean "wrong workspace", and creating one would put a collection where nobody expects it.
        var workspace = await client.GetWorkspaceAsync(request.WorkspaceId, cancellationToken);
        if (!workspace.Succeeded)
        {
            return ApiFailure(workspace, "The workspace cannot be used");
        }

        return request.KnownCollectionId is { } known
            ? await UpdateAsync(request, known, cancellationToken)
            : await FindOrCreateAsync(request, cancellationToken);
    }

    // ---- a collection we already know ----

    private async Task<PostmanPublishResult> UpdateAsync(PostmanPublishRequest request, string uid, CancellationToken cancellationToken)
    {
        if (request.DryRun)
        {
            var read = await client.GetCollectionAsync(uid, cancellationToken);
            if (!read.Succeeded)
            {
                return read.Status == PostmanApiStatus.NotFound ? Missing(uid) : ApiFailure(read, "The collection could not be read");
            }

            return new PostmanPublishResult { Outcome = PostmanPublishOutcome.WouldUpdate, Message = "Dry run: the collection would be updated in place.", CollectionUid = uid };
        }

        var update = await client.UpdateCollectionAsync(uid, request.CollectionJson, cancellationToken);
        if (update.Succeeded)
        {
            return new PostmanPublishResult
            {
                Outcome = PostmanPublishOutcome.Updated,
                Message = "The existing collection was updated in place.",
                CollectionUid = update.Value!.Uid
            };
        }

        return update.Status == PostmanApiStatus.NotFound ? Missing(uid) : ApiFailure(update, "The collection could not be updated");
    }

    private static PostmanPublishResult Missing(string uid) => Failed(
        PostmanPublishFailure.CollectionMissing,
        $"The collection '{uid}' no longer exists in Postman (deleted?). A new one was not created; decide explicitly whether to start over.");

    // ---- a collection we have to find ----

    private async Task<PostmanPublishResult> FindOrCreateAsync(PostmanPublishRequest request, CancellationToken cancellationToken)
    {
        var found = await client.FindCollectionsByNameAsync(request.WorkspaceId, request.CollectionName, cancellationToken);
        if (!found.Succeeded)
        {
            return ApiFailure(found, "The workspace's collections could not be listed");
        }

        var sameName = found.Value!.Where(c => string.Equals(c.Name, request.CollectionName, StringComparison.Ordinal)).ToList();
        if (sameName.Count == 0)
        {
            return await CreateAsync(request, cancellationToken);
        }

        // Only a collection carrying our marker may be adopted. Reading each candidate is cheap (there are usually one or two).
        var managed = new List<PostmanCollectionRef>();
        foreach (var candidate in sameName)
        {
            var read = await client.GetCollectionAsync(candidate.Uid, cancellationToken);
            if (!read.Succeeded)
            {
                return ApiFailure(read, $"A collection named '{request.CollectionName}' exists but could not be inspected");
            }

            if (IsManaged(read.Value!))
            {
                managed.Add(candidate);
            }
        }

        if (managed.Count == 0)
        {
            return Failed(
                PostmanPublishFailure.NameConflict,
                $"The workspace already has a collection named '{request.CollectionName}' that this tool does not maintain (it lacks the '{PostmanCollectionGenerator.ManagedMarkerVariable}' marker). " +
                "It was not overwritten and no second collection was created; rename one of them.");
        }

        if (managed.Count > 1)
        {
            return Failed(
                PostmanPublishFailure.AmbiguousCollections,
                $"{managed.Count} collections named '{request.CollectionName}' are maintained by this tool in the workspace; which one is meant is not guessed. Remove the extra ones in Postman.");
        }

        var uid = managed[0].Uid;
        if (request.Decision.Outcome == SyncOutcome.NoChanges)
        {
            return new PostmanPublishResult
            {
                Outcome = PostmanPublishOutcome.Adopted,
                Message = "The collection this tool maintains already exists and nothing changed; nothing was written. Keep its uid.",
                CollectionUid = uid
            };
        }

        return await UpdateAsync(request, uid, cancellationToken);
    }

    private async Task<PostmanPublishResult> CreateAsync(PostmanPublishRequest request, CancellationToken cancellationToken)
    {
        if (request.DryRun)
        {
            return new PostmanPublishResult
            {
                Outcome = PostmanPublishOutcome.WouldCreate,
                Message = $"Dry run: no collection named '{request.CollectionName}' exists in the workspace, so one would be created."
            };
        }

        var created = await client.CreateCollectionAsync(request.WorkspaceId, request.CollectionJson, cancellationToken);
        return created.Succeeded
            ? new PostmanPublishResult
            {
                Outcome = PostmanPublishOutcome.Created,
                Message = "The collection did not exist and was created. Keep its uid so later runs update this same collection.",
                CollectionUid = created.Value!.Uid
            }
            : ApiFailure(created, "The collection could not be created");
    }

    // ---- helpers ----

    private static string? Validate(PostmanPublishRequest request)
    {
        if (!IdPattern().IsMatch(request.WorkspaceId))
        {
            return "The workspace id may only contain letters, digits and '-'.";
        }

        if (request.KnownCollectionId is { } known && !IdPattern().IsMatch(known))
        {
            return "The collection id may only contain letters, digits and '-'.";
        }

        if (string.IsNullOrWhiteSpace(request.CollectionName))
        {
            return "The collection name is empty.";
        }

        try
        {
            using var document = JsonDocument.Parse(request.CollectionJson);
            var name = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String
                    ? nameProperty.GetString()
                    : null;
            if (name is null)
            {
                return "The JSON is not a Postman collection (it needs info.name).";
            }

            if (!string.Equals(name, request.CollectionName, StringComparison.Ordinal))
            {
                return $"The collection's info.name ('{name}') differs from the name it is looked up by ('{request.CollectionName}').";
            }

            if (request.Decision.Outcome != SyncOutcome.Blocked && !HasMarker(document.RootElement))
            {
                return $"The collection lacks the '{PostmanCollectionGenerator.ManagedMarkerVariable}' marker; generate it with PostmanCollectionOptions.IdNamespace set.";
            }
        }
        catch (JsonException)
        {
            return "The collection is not valid JSON.";
        }

        return null;
    }

    private static bool IsManaged(string collectionJson)
    {
        try
        {
            using var document = JsonDocument.Parse(collectionJson);
            return HasMarker(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasMarker(JsonElement collection) =>
        collection.ValueKind == JsonValueKind.Object && collection.TryGetProperty("variable", out var variables) && variables.ValueKind == JsonValueKind.Array
        && variables.EnumerateArray().Any(v =>
            v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() == PostmanCollectionGenerator.ManagedMarkerVariable
            && v.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == PostmanCollectionGenerator.ManagedMarkerValue);

    private static PostmanPublishResult Failed(PostmanPublishFailure failure, string message) =>
        new() { Outcome = PostmanPublishOutcome.Failed, Failure = failure, Message = message };

    private static PostmanPublishResult ApiFailure<T>(PostmanApiResult<T> result, string what) => new()
    {
        Outcome = PostmanPublishOutcome.Failed,
        Failure = PostmanPublishFailure.Api,
        Message = $"{what}: {result.Message}",
        ApiStatus = result.Status,
        HttpStatus = result.HttpStatus,
        RetryAfter = result.RetryAfter
    };

    [GeneratedRegex("^[A-Za-z0-9-]{1,128}$")]
    private static partial Regex IdPattern();
}
