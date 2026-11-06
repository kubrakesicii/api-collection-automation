namespace ApiCollection.Postman.CloudApi;

/// <summary>What a connection check needs. The API key is a secret: this type never prints it.</summary>
public sealed class PostmanConnectionOptions
{
    public const string DefaultBaseAddress = "https://api.getpostman.com";

    /// <summary>The Postman API key. Leading/trailing whitespace (a pasted newline) is trimmed; an empty key is "not configured".</summary>
    public required string ApiKey { get; init; }

    /// <summary>The workspace to check access to; when null only the API key is checked.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Must be https: the API key is never sent over plain http.</summary>
    public Uri BaseAddress { get; init; } = new(DefaultBaseAddress);

    /// <summary>Upper bound for each request.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public override string ToString() =>
        $"PostmanConnectionOptions {{ BaseAddress = {BaseAddress}, WorkspaceId = {WorkspaceId ?? "(none)"}, ApiKey = ***** }}";
}

public enum PostmanConnectionStatus
{
    Connected,

    /// <summary>Missing or unusable settings (no key, a malformed workspace id, a non-https address). Nothing was sent.</summary>
    NotConfigured,

    /// <summary>The API key is malformed or Postman rejected it (HTTP 401).</summary>
    InvalidApiKey,

    /// <summary>Postman refused access (HTTP 403).</summary>
    AccessDenied,

    /// <summary>The workspace does not exist or is not visible to this key (HTTP 404).</summary>
    WorkspaceNotFound,

    /// <summary>HTTP 429.</summary>
    RateLimited,

    /// <summary>HTTP 5xx.</summary>
    ServerError,

    /// <summary>The request never got an answer (DNS, TLS, proxy, connection refused).</summary>
    NetworkError,

    Timeout,

    /// <summary>An answer that is not what the Postman API sends (another status, a redirect, a body that is not JSON).</summary>
    UnexpectedResponse
}

public enum PostmanConnectionStep
{
    Configuration,
    Authentication,
    Workspace
}

public sealed record PostmanWorkspaceInfo(string Id, string? Name, string? Type);

public sealed record PostmanConnectionResult
{
    public required PostmanConnectionStatus Status { get; init; }

    /// <summary>The step that produced this result: where it failed, or the last step that was completed.</summary>
    public required PostmanConnectionStep Step { get; init; }

    /// <summary>A safe-to-print explanation: never contains the API key.</summary>
    public required string Message { get; init; }

    public int? HttpStatus { get; init; }

    /// <summary>Who the key belongs to, when authentication succeeded.</summary>
    public string? UserName { get; init; }

    public PostmanWorkspaceInfo? Workspace { get; init; }

    public bool WorkspaceChecked { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public bool Succeeded => Status == PostmanConnectionStatus.Connected;

    /// <summary>A failure that may go away by itself; trying again later can help.</summary>
    public bool IsTransient => Status is PostmanConnectionStatus.RateLimited or PostmanConnectionStatus.ServerError
        or PostmanConnectionStatus.NetworkError or PostmanConnectionStatus.Timeout;
}
