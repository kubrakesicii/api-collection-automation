using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using static ApiCollection.Postman.CloudApi.PostmanApiTransport;

namespace ApiCollection.Postman.CloudApi;

/// <summary>
/// Verifies that the API key works and, optionally, that a workspace is reachable. Strictly read-only: it sends at most two GET
/// requests (<c>/me</c>, then <c>/workspaces/{id}</c>) and nothing else. Every outcome is a result, never an exception
/// (except the caller cancelling), and no result ever contains the API key.
/// </summary>
public sealed partial class PostmanConnectionChecker(HttpClient http)
{
    public async Task<PostmanConnectionResult> CheckAsync(PostmanConnectionOptions options, CancellationToken cancellationToken = default)
    {
        var apiKey = (options.ApiKey ?? string.Empty).Trim();
        var workspaceId = string.IsNullOrWhiteSpace(options.WorkspaceId) ? null : options.WorkspaceId.Trim();

        var configurationProblem = ValidateConfiguration(apiKey, workspaceId, options);
        if (configurationProblem is not null)
        {
            return configurationProblem;
        }

        var authentication = await GetAsync("me", PostmanConnectionStep.Authentication, apiKey, options, cancellationToken);
        if (authentication.Failure is { } authenticationFailure)
        {
            return authenticationFailure;
        }

        var user = ReadObject(authentication.Reply!.Body, "user");
        if (!user.IsObject)
        {
            return Unexpected(PostmanConnectionStep.Authentication, authentication.Reply.Status, null);
        }

        var userName = Text(user.Section, "username") ?? Text(user.Section, "fullName") ?? Text(user.Section, "name");
        if (workspaceId is null)
        {
            return new PostmanConnectionResult
            {
                Status = PostmanConnectionStatus.Connected,
                Step = PostmanConnectionStep.Authentication,
                Message = "The API key was accepted. No workspace was given, so workspace access was not checked.",
                HttpStatus = (int)authentication.Reply.Status,
                UserName = userName
            };
        }

        var workspaceReply = await GetAsync($"workspaces/{Uri.EscapeDataString(workspaceId)}", PostmanConnectionStep.Workspace, apiKey, options, cancellationToken, workspaceId);
        if (workspaceReply.Failure is { } workspaceFailure)
        {
            return workspaceFailure with { UserName = userName };
        }

        var workspace = ReadObject(workspaceReply.Reply!.Body, "workspace");
        if (!workspace.IsObject)
        {
            return Unexpected(PostmanConnectionStep.Workspace, workspaceReply.Reply.Status, null) with { UserName = userName };
        }

        return new PostmanConnectionResult
        {
            Status = PostmanConnectionStatus.Connected,
            Step = PostmanConnectionStep.Workspace,
            Message = "The API key was accepted and the workspace is reachable.",
            HttpStatus = (int)workspaceReply.Reply.Status,
            UserName = userName,
            Workspace = new PostmanWorkspaceInfo(Text(workspace.Section, "id") ?? workspaceId, Text(workspace.Section, "name"), Text(workspace.Section, "type")),
            WorkspaceChecked = true
        };
    }

    // ---- configuration ----

    private static PostmanConnectionResult? ValidateConfiguration(string apiKey, string? workspaceId, PostmanConnectionOptions options)
    {
        if (apiKey.Length == 0)
        {
            return Configuration(PostmanConnectionStatus.NotConfigured, "No Postman API key was provided. Nothing was sent.");
        }

        if (!IsPlausibleApiKey(apiKey))
        {
            return Configuration(
                PostmanConnectionStatus.InvalidApiKey,
                "The API key contains whitespace, control or non-ASCII characters, so it looks damaged (for example a broken copy and paste). Nothing was sent.");
        }

        if (options.BaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            return Configuration(PostmanConnectionStatus.NotConfigured, "The Postman API address must use https; the API key is never sent over plain http. Nothing was sent.");
        }

        if (workspaceId is not null && !WorkspaceIdPattern().IsMatch(workspaceId))
        {
            return Configuration(
                PostmanConnectionStatus.NotConfigured,
                "The workspace id may only contain letters, digits and '-' (up to 64 characters). Nothing was sent.");
        }

        return null;
    }

    private static PostmanConnectionResult Configuration(PostmanConnectionStatus status, string message) =>
        new() { Status = status, Step = PostmanConnectionStep.Configuration, Message = message };

    // ---- requests ----

    private async Task<(PostmanReply? Reply, PostmanConnectionResult? Failure)> GetAsync(
        string path, PostmanConnectionStep step, string apiKey, PostmanConnectionOptions options, CancellationToken cancellationToken,
        string? workspaceId = null)
    {
        var (reply, failure) = await SendAsync(
            http, HttpMethod.Get, new Uri(options.BaseAddress, path), apiKey, null, options.Timeout, SmallResponseBytes, cancellationToken);

        if (failure is not null)
        {
            var status = failure.Kind == TransportFailureKind.Timeout ? PostmanConnectionStatus.Timeout : PostmanConnectionStatus.NetworkError;
            return (null, Failure(status, step, null, failure.Message, apiKey));
        }

        return reply!.Status == HttpStatusCode.OK ? (reply, null) : (null, Classify(reply, step, apiKey, workspaceId));
    }

    private static PostmanConnectionResult Classify(PostmanReply reply, PostmanConnectionStep step, string apiKey, string? workspaceId)
    {
        var status = (int)reply.Status;
        var remote = RemoteMessage(reply.Body);
        var said = remote is null ? string.Empty : $" Postman says: {Clean(remote, apiKey)}";

        return status switch
        {
            401 => Failure(PostmanConnectionStatus.InvalidApiKey, step, status, $"Postman rejected the API key (HTTP 401).{said}", apiKey),
            403 => Failure(PostmanConnectionStatus.AccessDenied, step, status, $"Postman denied access (HTTP 403).{said}", apiKey),
            404 when step == PostmanConnectionStep.Workspace => Failure(
                PostmanConnectionStatus.WorkspaceNotFound, step, status,
                $"Workspace '{workspaceId}' was not found, or it is not visible to this API key (HTTP 404).{said}", apiKey),
            429 => Failure(PostmanConnectionStatus.RateLimited, step, status, $"Postman is rate limiting requests (HTTP 429).{said}", apiKey) with { RetryAfter = reply.RetryAfter },
            >= 500 and <= 599 => Failure(PostmanConnectionStatus.ServerError, step, status, $"Postman reported a server error (HTTP {status}).{said}", apiKey),
            >= 300 and <= 399 => Failure(PostmanConnectionStatus.UnexpectedResponse, step, status, $"Postman answered with a redirect (HTTP {status}); it was not followed.", apiKey),
            _ => Failure(PostmanConnectionStatus.UnexpectedResponse, step, status, $"Unexpected answer from Postman (HTTP {status}).{said}", apiKey)
        };
    }

    private static PostmanConnectionResult Failure(PostmanConnectionStatus status, PostmanConnectionStep step, int? httpStatus, string message, string apiKey) => new()
    {
        Status = status,
        Step = step,
        HttpStatus = httpStatus,
        Message = Clean(message, apiKey)
    };

    private static PostmanConnectionResult Unexpected(PostmanConnectionStep step, HttpStatusCode status, string? detail) => new()
    {
        Status = PostmanConnectionStatus.UnexpectedResponse,
        Step = step,
        HttpStatus = (int)status,
        Message = detail ?? $"HTTP {(int)status} was returned but the body is not a Postman API response (a proxy or captive portal in the way?)."
    };

    // ---- reading answers ----

    /// <summary>
    /// A 200 answer must at least be a JSON object (anything else is a proxy or captive portal answering). The named section is
    /// optional: only the status code is relied upon, because the exact shape of the answer is not part of the contract we test.
    /// </summary>
    private static (bool IsObject, JsonElement Section) ReadObject(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (false, default);
            }

            var section = document.RootElement.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.Object
                ? found.Clone()
                : default;
            return (true, section);
        }
        catch (JsonException)
        {
            return (false, default);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex WorkspaceIdPattern();
}
