using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ApiCollection.Postman.CloudApi;

namespace ApiCollection.Postman.Tests;

/// <summary>
/// The connection check against a stubbed Postman: no real network and no real key. The API shapes follow what the Postman API is
/// documented to send (X-API-Key header, HTTP 401 with "Invalid API Key"); the check relies on status codes first and reads bodies leniently.
/// </summary>
public sealed class PostmanConnectionCheckerTests
{
    private const string Key = "PMAK-fake-test-key-not-real";
    private const string WorkspaceId = "00000000-aaaa-bbbb-cccc-000000000001";

    private const string MeOk = """{ "user": { "id": 1, "username": "ada", "fullName": "Ada Example" } }""";
    private const string WorkspaceOk = $$"""{ "workspace": { "id": "{{WorkspaceId}}", "name": "Team Workspace", "type": "team", "visibility": "team" } }""";
    private const string InvalidKeyBody = """{ "error": { "name": "AuthenticationError", "message": "Invalid API Key. Every request requires a valid API Key to be sent." } }""";

    private sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, bool HadBody);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Recorded(
                request.Method, request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is not null));
            return await respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static StubHandler Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new((request, _) => Task.FromResult(respond(request)));

    /// <summary>Postman as it should answer: the identity at /me and the workspace at /workspaces/{id}.</summary>
    private static StubHandler Healthy() => Stub(request => request.RequestUri!.AbsolutePath == "/me"
        ? Json(HttpStatusCode.OK, MeOk)
        : Json(HttpStatusCode.OK, WorkspaceOk));

    private static Task<PostmanConnectionResult> Check(
        StubHandler handler, string? apiKey = Key, string? workspaceId = WorkspaceId, TimeSpan? timeout = null, Uri? baseAddress = null,
        CancellationToken cancellationToken = default)
    {
        using var client = PostmanHttpClientFactory.Create(handler);
        return new PostmanConnectionChecker(client).CheckAsync(
            new PostmanConnectionOptions
            {
                ApiKey = apiKey!,
                WorkspaceId = workspaceId,
                Timeout = timeout ?? TimeSpan.FromSeconds(5),
                BaseAddress = baseAddress ?? new Uri(PostmanConnectionOptions.DefaultBaseAddress)
            },
            cancellationToken);
    }

    // ---- a valid key ----

    [Fact]
    public async Task A_valid_key_and_workspace_connect_and_report_who_and_what()
    {
        var handler = Healthy();

        var result = await Check(handler);

        Assert.True(result.Succeeded);
        Assert.Equal((PostmanConnectionStatus.Connected, PostmanConnectionStep.Workspace, 200), (result.Status, result.Step, result.HttpStatus));
        Assert.Equal("ada", result.UserName);
        Assert.Equal(new PostmanWorkspaceInfo(WorkspaceId, "Team Workspace", "team"), result.Workspace);
        Assert.True(result.WorkspaceChecked);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public async Task The_check_is_strictly_read_only_two_gets_with_the_key_in_the_header_and_no_body()
    {
        var handler = Healthy();

        await Check(handler);

        Assert.Equal(
            ["https://api.getpostman.com/me", $"https://api.getpostman.com/workspaces/{WorkspaceId}"],
            handler.Requests.Select(r => r.Uri.AbsoluteUri));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.False(request.HadBody);
            Assert.Equal(Key, request.Headers["X-API-Key"]);
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.Contains("json", request.Headers["Accept"]);
        });
        Assert.Contains("ApiCollectionAutomation", string.Join(" ", handler.Requests[0].Headers["User-Agent"]));
    }

    [Fact]
    public async Task Without_a_workspace_only_the_key_is_checked_and_the_result_says_so()
    {
        var handler = Healthy();

        var result = await Check(handler, workspaceId: null);

        Assert.True(result.Succeeded);
        Assert.Equal(PostmanConnectionStep.Authentication, result.Step);
        Assert.False(result.WorkspaceChecked);
        Assert.Null(result.Workspace);
        Assert.Contains("workspace access was not checked", result.Message);
        Assert.Equal(["https://api.getpostman.com/me"], handler.Requests.Select(r => r.Uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_workspace_id_means_no_workspace(string blank)
    {
        var handler = Healthy();

        var result = await Check(handler, workspaceId: blank);

        Assert.True(result.Succeeded);
        Assert.False(result.WorkspaceChecked);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_key_with_a_pasted_trailing_newline_is_trimmed_before_it_is_sent()
    {
        var handler = Healthy();

        var result = await Check(handler, apiKey: $"  {Key}\r\n", workspaceId: $" {WorkspaceId} ");

        Assert.True(result.Succeeded);
        Assert.All(handler.Requests, r => Assert.Equal(Key, r.Headers["X-API-Key"]));
        Assert.EndsWith(WorkspaceId, handler.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task The_answer_is_read_leniently_because_only_the_status_code_is_relied_upon()
    {
        var handler = Stub(_ => Json(HttpStatusCode.OK, "{}"));

        var result = await Check(handler);

        Assert.True(result.Succeeded);
        Assert.Null(result.UserName);
        Assert.Equal(new PostmanWorkspaceInfo(WorkspaceId, null, null), result.Workspace);
    }

    // ---- an invalid key ----

    [Fact]
    public async Task An_invalid_key_is_a_controlled_error_and_no_workspace_request_follows()
    {
        var handler = Stub(_ => Json(HttpStatusCode.Unauthorized, InvalidKeyBody));

        var result = await Check(handler);

        Assert.False(result.Succeeded);
        Assert.Equal((PostmanConnectionStatus.InvalidApiKey, PostmanConnectionStep.Authentication, 401), (result.Status, result.Step, result.HttpStatus));
        Assert.Contains("rejected the API key (HTTP 401)", result.Message);
        Assert.Contains("Invalid API Key. Every request requires a valid API Key to be sent.", result.Message);
        Assert.False(result.IsTransient);
        Assert.Null(result.UserName);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_401_without_a_readable_body_is_still_an_invalid_key()
    {
        var handler = Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await Check(handler);

        Assert.Equal(PostmanConnectionStatus.InvalidApiKey, result.Status);
        Assert.DoesNotContain("Postman says", result.Message);
    }

    [Fact]
    public async Task A_401_on_the_workspace_request_is_also_an_invalid_key_and_keeps_who_the_key_belongs_to()
    {
        var handler = Stub(request => request.RequestUri!.AbsolutePath == "/me" ? Json(HttpStatusCode.OK, MeOk) : Json(HttpStatusCode.Unauthorized, InvalidKeyBody));

        var result = await Check(handler);

        Assert.Equal((PostmanConnectionStatus.InvalidApiKey, PostmanConnectionStep.Workspace), (result.Status, result.Step));
        Assert.Equal("ada", result.UserName);
    }

    // ---- workspace access ----

    [Fact]
    public async Task An_unknown_workspace_is_reported_as_not_found_for_the_workspace_step()
    {
        var handler = Stub(request => request.RequestUri!.AbsolutePath == "/me"
            ? Json(HttpStatusCode.OK, MeOk)
            : Json(HttpStatusCode.NotFound, """{ "error": { "name": "instanceNotFoundError", "message": "Workspace not found" } }"""));

        var result = await Check(handler);

        Assert.Equal((PostmanConnectionStatus.WorkspaceNotFound, PostmanConnectionStep.Workspace, 404), (result.Status, result.Step, result.HttpStatus));
        Assert.Contains($"Workspace '{WorkspaceId}' was not found", result.Message);
        Assert.Contains("Workspace not found", result.Message);
        Assert.Equal("ada", result.UserName);
        Assert.False(result.WorkspaceChecked);
    }

    [Fact]
    public async Task A_404_on_the_key_check_is_unexpected_rather_than_a_missing_workspace()
    {
        var result = await Check(Stub(_ => Json(HttpStatusCode.NotFound, "{}")));

        Assert.Equal((PostmanConnectionStatus.UnexpectedResponse, PostmanConnectionStep.Authentication), (result.Status, result.Step));
    }

    [Theory]
    [InlineData(PostmanConnectionStep.Authentication)]
    [InlineData(PostmanConnectionStep.Workspace)]
    public async Task A_403_means_access_denied_at_the_step_where_it_happened(PostmanConnectionStep step)
    {
        var handler = Stub(request => step == PostmanConnectionStep.Workspace && request.RequestUri!.AbsolutePath == "/me"
            ? Json(HttpStatusCode.OK, MeOk)
            : Json(HttpStatusCode.Forbidden, """{ "error": { "name": "forbiddenError", "message": "You are not permitted to perform this action" } }"""));

        var result = await Check(handler);

        Assert.Equal((PostmanConnectionStatus.AccessDenied, step, 403), (result.Status, result.Step, result.HttpStatus));
        Assert.Contains("You are not permitted", result.Message);
    }

    // ---- transient and unexpected failures ----

    [Fact]
    public async Task Rate_limiting_is_transient_and_reports_how_long_to_wait()
    {
        var handler = Stub(_ =>
        {
            var response = Json((HttpStatusCode)429, """{ "error": { "name": "rateLimitError", "message": "Too many requests" } }""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        var result = await Check(handler);

        Assert.Equal((PostmanConnectionStatus.RateLimited, 429), (result.Status, result.HttpStatus));
        Assert.True(result.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(7), result.RetryAfter);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Server_errors_are_transient(int status)
    {
        var result = await Check(Stub(_ => Json((HttpStatusCode)status, "<html>oops</html>")));

        Assert.Equal((PostmanConnectionStatus.ServerError, status), (result.Status, result.HttpStatus));
        Assert.True(result.IsTransient);
    }

    [Fact]
    public async Task A_redirect_is_reported_and_never_followed_because_the_key_would_travel_with_it()
    {
        var handler = Stub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example/me");
            return response;
        });

        var result = await Check(handler);

        Assert.Equal((PostmanConnectionStatus.UnexpectedResponse, 302), (result.Status, result.HttpStatus));
        Assert.Contains("redirect", result.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_real_client_does_not_follow_a_redirect_so_the_key_never_reaches_the_address_it_names()
    {
        // A real (loopback) server that redirects /first to /second: the client from the factory must stop at the 302.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new List<string>();
        var server = Task.Run(async () =>
        {
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync();
                var stream = connection.GetStream();
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                lock (seen)
                {
                    seen.Add(request);
                }

                var response = request.StartsWith("GET /first", StringComparison.Ordinal)
                    ? $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/second\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });

        using var client = PostmanHttpClientFactory.Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/first");
        request.Headers.TryAddWithoutValidation("X-API-Key", Key);

        using var response = await client.SendAsync(request);
        await Task.Delay(100);
        listener.Stop();
        await Task.WhenAny(server, Task.Delay(1000));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        lock (seen)
        {
            Assert.Single(seen);
            Assert.StartsWith("GET /first", seen[0]);
            Assert.DoesNotContain("/second", string.Concat(seen));
        }

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(409)]
    [InlineData(418)]
    public async Task Other_statuses_are_unexpected_responses(int status)
    {
        var result = await Check(Stub(_ => Json((HttpStatusCode)status, "{}")));

        Assert.Equal((PostmanConnectionStatus.UnexpectedResponse, status), (result.Status, result.HttpStatus));
        Assert.False(result.IsTransient);
    }

    [Theory]
    [InlineData("<html>Please sign in to the hotel wifi</html>")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public async Task A_200_that_is_not_a_json_object_is_not_a_postman_answer(string body)
    {
        var result = await Check(Stub(_ => Json(HttpStatusCode.OK, body)));

        Assert.Equal(PostmanConnectionStatus.UnexpectedResponse, result.Status);
        Assert.Contains("not a Postman API response", result.Message);
    }

    [Fact]
    public async Task A_network_failure_is_a_transient_result_not_an_exception()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("send failed", new IOException("No such host is known.")));

        var result = await Check(handler);

        Assert.Equal(PostmanConnectionStatus.NetworkError, result.Status);
        Assert.True(result.IsTransient);
        Assert.Contains("No such host is known.", result.Message);
        Assert.Null(result.HttpStatus);
    }

    [Fact]
    public async Task A_request_that_takes_too_long_times_out_as_a_transient_result()
    {
        // Never answers; the request ends only because the check's own timeout cancels the token it passes down.
        var neverAnswers = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Json(HttpStatusCode.OK, MeOk);
        });

        var result = await Check(neverAnswers, timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(PostmanConnectionStatus.Timeout, result.Status);
        Assert.True(result.IsTransient);
        Assert.Contains("did not answer within", result.Message);
        Assert.Single(neverAnswers.Requests);
    }

    [Fact]
    public async Task Cancelling_on_purpose_is_not_reported_as_a_timeout()
    {
        using var cancelled = new CancellationTokenSource();
        var neverAnswers = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Json(HttpStatusCode.OK, MeOk);
        });

        var pending = Check(neverAnswers, cancellationToken: cancelled.Token);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // ---- configuration problems: nothing is sent ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_missing_key_is_not_configured_and_sends_nothing(string? blank)
    {
        var handler = Healthy();

        var result = await Check(handler, apiKey: blank);

        Assert.Equal((PostmanConnectionStatus.NotConfigured, PostmanConnectionStep.Configuration), (result.Status, result.Step));
        Assert.StartsWith("No Postman API key", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("PMAK-has space")]
    [InlineData("PMAK-has\ttab")]
    [InlineData("PMAK-line\nbreak")]
    [InlineData("PMAK-ünïcode")]
    [InlineData("PMAK-\u0007bell")]
    public async Task A_damaged_key_is_rejected_locally_without_a_request_or_an_echo(string damaged)
    {
        var handler = Healthy();

        var result = await Check(handler, apiKey: damaged);

        Assert.Equal((PostmanConnectionStatus.InvalidApiKey, PostmanConnectionStep.Configuration), (result.Status, result.Step));
        Assert.Contains("looks damaged", result.Message);
        Assert.DoesNotContain(damaged, result.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("../me")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("id?x=1")]
    [InlineData("%2e%2e")]
    [InlineData("ünïcode")]
    [InlineData("a\nb")]
    public async Task A_workspace_id_that_could_change_the_request_path_is_refused_before_anything_is_sent(string id)
    {
        var handler = Healthy();

        var result = await Check(handler, workspaceId: id);

        Assert.Equal((PostmanConnectionStatus.NotConfigured, PostmanConnectionStep.Configuration), (result.Status, result.Step));
        Assert.Contains("workspace id may only contain", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_workspace_id_longer_than_64_characters_is_refused()
    {
        var handler = Healthy();

        var result = await Check(handler, workspaceId: new string('a', 65));

        Assert.Equal(PostmanConnectionStatus.NotConfigured, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_key_is_never_sent_over_plain_http()
    {
        var handler = Healthy();

        var result = await Check(handler, baseAddress: new Uri("http://api.getpostman.com"));

        Assert.Equal((PostmanConnectionStatus.NotConfigured, PostmanConnectionStep.Configuration), (result.Status, result.Step));
        Assert.Contains("must use https", result.Message);
        Assert.Empty(handler.Requests);
    }

    // ---- secrets and output safety ----

    [Fact]
    public async Task The_key_never_appears_in_a_result_even_when_postman_or_the_network_echoes_it()
    {
        var scenarios = new Func<StubHandler>[]
        {
            () => Stub(_ => Json(HttpStatusCode.Unauthorized, $$"""{ "error": { "message": "Key {{Key}} is not valid" } }""")),
            () => Stub(_ => Json(HttpStatusCode.Forbidden, $$"""{ "error": { "name": "{{Key}}" } }""")),
            () => Stub(_ => Json(HttpStatusCode.InternalServerError, $$"""{ "detail": "boom {{Key}}" }""")),
            () => new StubHandler((_, _) => throw new HttpRequestException($"failed for key {Key}")),
        };

        foreach (var scenario in scenarios)
        {
            var result = await Check(scenario());

            Assert.DoesNotContain(Key, result.Message);
            Assert.DoesNotContain(Key, result.ToString());
            Assert.Contains("*****", result.Message);
        }
    }

    [Fact]
    public async Task Messages_from_postman_are_single_line_bounded_and_free_of_control_characters()
    {
        var long_ = new string('x', 500);
        var handler = Stub(_ => Json(HttpStatusCode.Unauthorized, $$"""{ "error": { "message": "line1\nline2\u0007\ttabbed {{long_}}" } }"""));

        var result = await Check(handler);

        Assert.DoesNotContain('\n', result.Message);
        Assert.DoesNotContain('\u0007', result.Message);
        Assert.DoesNotContain('\t', result.Message);
        Assert.Contains("line1 line2", result.Message);
        Assert.True(result.Message.Length < 400, $"message was {result.Message.Length} characters");
        Assert.EndsWith("...", result.Message);
    }

    [Fact]
    public void The_options_never_print_the_key()
    {
        var options = new PostmanConnectionOptions { ApiKey = Key, WorkspaceId = WorkspaceId };

        var text = options.ToString();

        Assert.DoesNotContain(Key, text);
        Assert.Contains("ApiKey = *****", text);
        Assert.Contains(WorkspaceId, text);
    }

    [Fact]
    public async Task A_huge_answer_is_not_read_without_limit()
    {
        var huge = new string('a', 5 * 1024 * 1024);
        var handler = Stub(_ => Json(HttpStatusCode.OK, "{\"user\":{\"username\":\"" + huge + "\"}}"));

        var result = await Check(handler, workspaceId: null);

        // Truncated at the read limit, so it is not valid JSON and is treated as not being a Postman answer.
        Assert.Equal(PostmanConnectionStatus.UnexpectedResponse, result.Status);
    }
}
