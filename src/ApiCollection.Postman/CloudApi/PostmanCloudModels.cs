namespace ApiCollection.Postman.CloudApi;

/// <summary>What the Postman API client needs. The API key is a secret: this type never prints it.</summary>
public sealed class PostmanCloudOptions
{
    public required string ApiKey { get; init; }

    /// <summary>Must be https: the API key is never sent over plain http.</summary>
    public Uri BaseAddress { get; init; } = new(PostmanConnectionOptions.DefaultBaseAddress);

    /// <summary>Upper bound for each request (writes of a whole collection can take a while).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Postman accepts at most 100 MB per collection; a bigger payload is refused locally without being sent.</summary>
    public long MaxPayloadBytes { get; init; } = 100L * 1024 * 1024;

    public override string ToString() => $"PostmanCloudOptions {{ BaseAddress = {BaseAddress}, ApiKey = ***** }}";
}

public enum PostmanApiStatus
{
    Ok,

    /// <summary>Missing or unusable settings or input (no key, a malformed id, not a collection). Nothing was sent.</summary>
    NotConfigured,

    /// <summary>The API key is malformed or Postman rejected it (HTTP 401).</summary>
    InvalidApiKey,

    /// <summary>HTTP 403.</summary>
    AccessDenied,

    /// <summary>The workspace or collection does not exist (HTTP 404, or HTTP 400 "not found" where Postman documents that).</summary>
    NotFound,

    /// <summary>Postman refused the request or the payload (HTTP 400).</summary>
    Rejected,

    /// <summary>HTTP 409.</summary>
    Conflict,

    /// <summary>HTTP 429.</summary>
    RateLimited,

    /// <summary>HTTP 5xx.</summary>
    ServerError,

    /// <summary>The request never got an answer (DNS, TLS, proxy, connection refused).</summary>
    NetworkError,

    Timeout,

    /// <summary>An answer that is not what the Postman API sends for this call.</summary>
    UnexpectedResponse
}

public sealed record PostmanApiResult<T>
{
    public required PostmanApiStatus Status { get; init; }

    /// <summary>A safe-to-print explanation: never contains the API key.</summary>
    public required string Message { get; init; }

    public T? Value { get; init; }

    public int? HttpStatus { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public bool Succeeded => Status == PostmanApiStatus.Ok;

    /// <summary>A failure that may go away by itself; trying again later can help.</summary>
    public bool IsTransient => Status is PostmanApiStatus.RateLimited or PostmanApiStatus.ServerError
        or PostmanApiStatus.NetworkError or PostmanApiStatus.Timeout;
}

/// <summary>A collection as Postman names it: <c>Uid</c> (owner id plus id) is the unique form that is stored and used in requests.</summary>
public sealed record PostmanCollectionRef(string Uid, string? Id, string? Name);
