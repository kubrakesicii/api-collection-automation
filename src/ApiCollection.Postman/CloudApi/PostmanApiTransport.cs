using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiCollection.Postman.CloudApi;

internal sealed record PostmanReply(HttpStatusCode Status, string Body, TimeSpan? RetryAfter);

internal enum TransportFailureKind
{
    Timeout,
    Network
}

internal sealed record TransportFailure(TransportFailureKind Kind, string Message);

/// <summary>
/// The one place that actually talks to Postman, shared by the connection check and the collection client: it sends the API key
/// in a header, enforces a per-request timeout and a response size limit, never follows redirects (the caller supplies a client
/// that does not), and turns network problems into values. Helpers make text from Postman safe to print.
/// </summary>
internal static partial class PostmanApiTransport
{
    public const int MaxMessageLength = 200;

    /// <summary>Responses that are only small status answers; large ones (a whole collection) pass an explicit, bigger limit.</summary>
    public const int SmallResponseBytes = 64 * 1024;

    public static async Task<(PostmanReply? Reply, TransportFailure? Failure)> SendAsync(
        HttpClient http, HttpMethod method, Uri uri, string apiKey, string? jsonBody, TimeSpan timeout, int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            var body = await ReadBodyAsync(response, maxResponseBytes, timeoutSource.Token);
            return (new PostmanReply(response.StatusCode, body, RetryAfterOf(response)), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, new TransportFailure(TransportFailureKind.Timeout, $"Postman did not answer within {timeout.TotalSeconds:0.#} seconds."));
        }
        catch (HttpRequestException exception)
        {
            return (null, new TransportFailure(
                TransportFailureKind.Network,
                $"The request could not be completed ({exception.InnerException?.Message ?? exception.Message}). Check the network, proxy and TLS settings."));
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var collected = new MemoryStream();
        var buffer = new byte[Math.Min(maxBytes, 81920)];
        while (collected.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - collected.Length)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            collected.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta;
        }

        return retry?.Date is { } date && date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : null;
    }

    public static string? Text(JsonElement section, string name) =>
        section.ValueKind == JsonValueKind.Object && section.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    /// <summary>
    /// The message Postman put in an error body. Postman uses two shapes: <c>{ "error": { "name", "message" } }</c> and the flat
    /// <c>{ "type", "title", "detail", "status" }</c>.
    /// </summary>
    public static string? RemoteMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                return Text(error, "message") ?? Text(error, "name");
            }

            return Text(root, "detail") ?? Text(root, "title") ?? Text(root, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Makes text safe to print: single line, no control characters, bounded, and never containing the API key.</summary>
    public static string Clean(string text, string apiKey)
    {
        var cleaned = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (apiKey.Length > 0)
        {
            cleaned = cleaned.Replace(apiKey, "*****", StringComparison.Ordinal);
        }

        cleaned = Whitespace().Replace(cleaned, " ").Trim();
        return cleaned.Length <= MaxMessageLength ? cleaned : cleaned[..(MaxMessageLength - 3)] + "...";
    }

    /// <summary>The key is a header value: it must be printable ASCII without whitespace, or something pasted it wrongly.</summary>
    public static bool IsPlausibleApiKey(string apiKey) => apiKey.Length > 0 && !apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c > 126);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
