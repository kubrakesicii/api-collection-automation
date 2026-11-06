using System.Net;
using System.Text;
using ApiCollection.Cli;
using ApiCollection.Postman.CloudApi;

namespace ApiCollection.Cli.Tests;

/// <summary>The postman-check command with a stubbed environment and Postman: no real variables, key or network are touched.</summary>
public sealed class PostmanCheckCommandTests
{
    private const string Key = "PMAK-fake-test-key-not-real";
    private const string WorkspaceId = "00000000-aaaa-bbbb-cccc-000000000001";
    private const string OtherWorkspaceId = "00000000-aaaa-bbbb-cccc-000000000002";

    private sealed class FakePostman : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? ApiKey)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = request => request.RequestUri!.AbsolutePath == "/me"
            ? Json(HttpStatusCode.OK, """{ "user": { "username": "ada" } }""")
            : Json(HttpStatusCode.OK, $$"""{ "workspace": { "id": "{{WorkspaceId}}", "name": "Team Workspace", "type": "team" } }""");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("X-API-Key", out var values) ? values.Single() : null));
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Always hermetic: the environment, the user secrets and the HTTP client are all stubs, so a real key on the machine running the
    /// tests (in the environment or in secrets.json) can never leak into, or change the outcome of, a test.
    /// </summary>
    private static (int Code, string Out, string Err) Run(
        Dictionary<string, string> environment, FakePostman postman, params string[] args) =>
        RunWithSecrets(environment, secrets: null, postman, args);

    private static (int Code, string Out, string Err) RunWithSecrets(
        Dictionary<string, string> environment, Func<string, string?>? secrets, FakePostman postman, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliApplication.Run(
            ["postman-check", .. args], output, error,
            new CliEnvironment
            {
                GetVariable = name => environment.GetValueOrDefault(name),
                GetUserSecret = secrets ?? (_ => null),
                CreateHttpClient = () => PostmanHttpClientFactory.Create(postman)
            });
        return (code, output.ToString(), error.ToString());
    }

    private static Dictionary<string, string> Env(string? key = Key, string? workspace = WorkspaceId)
    {
        var environment = new Dictionary<string, string>();
        if (key is not null)
        {
            environment["POSTMAN_API_KEY"] = key;
        }

        if (workspace is not null)
        {
            environment["POSTMAN_WORKSPACE_ID"] = workspace;
        }

        return environment;
    }

    private static void AssertNoSecret(params string[] texts) =>
        Assert.All(texts, text => Assert.DoesNotContain(Key, text));

    // ---- a valid key ----

    [Fact]
    public void A_valid_key_and_workspace_connect_and_the_report_shows_who_and_what()
    {
        var postman = new FakePostman();

        var (code, output, error) = Run(Env(), postman);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Postman connection check (read-only: only GET requests are sent)", output);
        Assert.Contains("  API key:         PMAK-… (27 characters) (from POSTMAN_API_KEY)", output);
        Assert.Contains($"  Workspace id:    {WorkspaceId} (from POSTMAN_WORKSPACE_ID)", output);
        Assert.Contains("  Authentication:  OK, signed in as ada", output);
        Assert.Contains("  Workspace:       OK, \"Team Workspace\" (team)", output);
        Assert.Contains("Result: CONNECTED", output);
        Assert.Equal("", error);
        AssertNoSecret(output, error);
    }

    [Fact]
    public void The_command_only_sends_gets_with_the_key_from_the_environment()
    {
        var postman = new FakePostman();

        Run(Env(key: $"{Key}\r\n"), postman);

        Assert.Equal(["/me", $"/workspaces/{WorkspaceId}"], postman.Requests.Select(r => r.Path));
        Assert.All(postman.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal(Key, r.ApiKey);
        });
    }

    [Fact]
    public void The_workspace_option_overrides_the_environment()
    {
        var postman = new FakePostman();

        var (code, output, _) = Run(Env(), postman, "--workspace", OtherWorkspaceId);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains($"{OtherWorkspaceId} (from --workspace)", output);
        Assert.Equal($"/workspaces/{OtherWorkspaceId}", postman.Requests[1].Path);
    }

    [Fact]
    public void Without_a_workspace_only_the_key_is_checked_and_the_report_says_so()
    {
        var postman = new FakePostman();

        var (code, output, _) = Run(Env(workspace: null), postman);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("  Workspace id:    not set", output);
        Assert.Contains("  Workspace:       not checked (no workspace id was given)", output);
        Assert.Contains("Result: CONNECTED", output);
        Assert.Single(postman.Requests);
    }

    // ---- an invalid key ----

    [Fact]
    public void An_invalid_key_fails_in_a_controlled_way_with_exit_code_4_and_a_useful_hint()
    {
        var postman = new FakePostman
        {
            Respond = _ => Json(HttpStatusCode.Unauthorized, """{ "error": { "name": "AuthenticationError", "message": "Invalid API Key. Every request requires a valid API Key to be sent." } }""")
        };

        var (code, output, error) = Run(Env(), postman);

        Assert.Equal(CliApplication.ConnectionFailed, code);
        Assert.Equal(4, code);
        Assert.Contains("  Authentication:  FAILED", output);
        Assert.Contains("  Workspace:       not checked", output);
        Assert.Contains("Result: FAILED (InvalidApiKey)", output);
        Assert.Contains("Postman rejected the API key (HTTP 401).", output);
        Assert.Contains("Invalid API Key. Every request requires a valid API Key to be sent.", output);
        Assert.Contains("Check that POSTMAN_API_KEY (or the Postman:ApiKey user secret) holds a current key", output);
        Assert.Equal("Connection check failed: InvalidApiKey.\n".Replace("\n", Environment.NewLine), error);
        Assert.Single(postman.Requests);
        AssertNoSecret(output, error);
    }

    [Fact]
    public void An_unknown_workspace_keeps_the_authentication_result_and_points_at_the_id()
    {
        var postman = new FakePostman();
        var healthy = postman.Respond;
        postman.Respond = request => request.RequestUri!.AbsolutePath == "/me"
            ? healthy(request)
            : Json(HttpStatusCode.NotFound, """{ "error": { "message": "Workspace not found" } }""");

        var (code, output, _) = Run(Env(), postman);

        Assert.Equal(CliApplication.ConnectionFailed, code);
        Assert.Contains("  Authentication:  OK, signed in as ada", output);
        Assert.Contains("  Workspace:       FAILED", output);
        Assert.Contains("Result: FAILED (WorkspaceNotFound)", output);
        Assert.Contains("it must be the workspace's id, not its name", output);
    }

    [Fact]
    public void Access_denied_is_reported_as_such()
    {
        var postman = new FakePostman { Respond = _ => Json(HttpStatusCode.Forbidden, "{}") };

        var (code, output, _) = Run(Env(), postman);

        Assert.Equal(CliApplication.ConnectionFailed, code);
        Assert.Contains("Result: FAILED (AccessDenied)", output);
        Assert.Contains("check the key's owner and the workspace's sharing", output);
    }

    // ---- transient failures ----

    [Fact]
    public void Rate_limiting_says_to_wait_and_is_a_connection_failure()
    {
        var postman = new FakePostman
        {
            Respond = _ =>
            {
                var response = Json((HttpStatusCode)429, "{}");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
                return response;
            }
        };

        var (code, output, _) = Run(Env(), postman);

        Assert.Equal(CliApplication.ConnectionFailed, code);
        Assert.Contains("Result: FAILED (RateLimited)", output);
        Assert.Contains("This may be temporary. Postman asks to wait 12 second(s) before trying again.", output);
    }

    [Fact]
    public void A_server_error_and_a_network_failure_are_described_as_possibly_temporary()
    {
        var serverError = Run(Env(), new FakePostman { Respond = _ => Json(HttpStatusCode.BadGateway, "bad gateway") });
        Assert.Equal(CliApplication.ConnectionFailed, serverError.Code);
        Assert.Contains("FAILED (ServerError)", serverError.Out);
        Assert.Contains("This may be temporary; try again later.", serverError.Out);

        var network = Run(Env(), new FakePostman { Respond = _ => throw new HttpRequestException("no route", new IOException("Network is unreachable.")) });
        Assert.Equal(CliApplication.ConnectionFailed, network.Code);
        Assert.Contains("FAILED (NetworkError)", network.Out);
        Assert.Contains("Network is unreachable.", network.Out);
        Assert.Contains("This may be temporary; try again later.", network.Out);
    }

    // ---- configuration problems ----

    [Fact]
    public void A_missing_key_is_a_configuration_error_with_exit_code_1_and_no_request()
    {
        var postman = new FakePostman();

        var (code, output, error) = Run(Env(key: null), postman);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("  API key:         not set", output);
        Assert.Contains("Result: FAILED (NotConfigured)", output);
        Assert.Contains("Set the POSTMAN_API_KEY environment variable or the Postman:ApiKey user secret", output);
        Assert.Contains("never accepted on the command line", output);
        Assert.Contains("Connection check failed: NotConfigured.", error);
        Assert.Empty(postman.Requests);
    }

    [Fact]
    public void A_damaged_key_is_rejected_before_anything_is_sent_and_is_not_echoed()
    {
        var postman = new FakePostman();
        const string damaged = "PMAK-broken copy";

        var (code, output, error) = Run(Env(key: damaged), postman);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("looks damaged", output);
        Assert.DoesNotContain(damaged, output);
        Assert.DoesNotContain(damaged, error);
        Assert.Empty(postman.Requests);
    }

    [Fact]
    public void A_workspace_id_that_could_alter_the_request_path_is_refused()
    {
        var postman = new FakePostman();

        var (code, output, _) = Run(Env(workspace: null), postman, "--workspace", "../me");

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("workspace id may only contain", output);
        Assert.Empty(postman.Requests);
    }

    // ---- the key is never accepted on the command line ----

    [Theory]
    [InlineData("--api-key", "x")]
    [InlineData("--key", "x")]
    [InlineData("--workspace")]
    [InlineData("--workspace", "a", "--workspace", "b")]
    [InlineData("extra")]
    [InlineData("--apikey=x")]
    public void Unknown_options_and_a_key_on_the_command_line_are_rejected_with_usage_and_no_request(params string[] args)
    {
        var postman = new FakePostman();

        var (code, output, error) = Run(Env(), postman, args);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("Usage:", error);
        Assert.Contains("never from the command line", error);
        Assert.Equal("", output);
        Assert.Empty(postman.Requests);
    }

    // ---- output safety ----

    [Fact]
    public void No_scenario_prints_the_key_not_even_when_postman_echoes_it()
    {
        var echoes = new FakePostman { Respond = _ => Json(HttpStatusCode.Unauthorized, $$"""{ "error": { "message": "Bad key {{Key}}" } }""") };

        var (_, output, error) = Run(Env(), echoes);

        AssertNoSecret(output, error);
        Assert.Contains("*****", output);
    }

    [Fact]
    public void The_report_is_deterministic()
    {
        var first = Run(Env(), new FakePostman());
        var second = Run(Env(), new FakePostman());

        Assert.Equal(first.Out, second.Out);
    }

    [Fact]
    public void The_real_environment_is_not_consulted_when_a_stub_is_given()
    {
        // Even if the machine running the tests has a real key set, the stubbed environment is what the command reads.
        var postman = new FakePostman();

        var (code, output, _) = Run(Env(key: null, workspace: null), postman);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("API key:         not set", output);
        Assert.Empty(postman.Requests);
    }

    // ---- user secrets (secrets.json outside the repository) ----

    private static Func<string, string?> Secrets(string? key = Key, string? workspace = WorkspaceId) => name => name switch
    {
        "Postman:ApiKey" => key,
        "Postman:WorkspaceId" => workspace,
        _ => null
    };

    [Fact]
    public void Without_environment_variables_the_key_and_workspace_come_from_the_user_secrets()
    {
        var postman = new FakePostman();

        var (code, output, error) = RunWithSecrets(Env(key: null, workspace: null), Secrets(), postman);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("PMAK-… (27 characters) (from user secrets)", output);
        Assert.Contains($"{WorkspaceId} (from user secrets)", output);
        Assert.Contains("Result: CONNECTED", output);
        Assert.All(postman.Requests, r => Assert.Equal(Key, r.ApiKey));
        AssertNoSecret(output, error);
    }

    [Fact]
    public void The_environment_wins_over_the_user_secrets_and_the_secrets_are_not_even_read()
    {
        var postman = new FakePostman();
        Func<string, string?> mustNotBeRead = _ => throw new InvalidDataException("secrets.json must not be read when the environment is complete");

        var (code, output, _) = RunWithSecrets(Env(), mustNotBeRead, postman);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("(from POSTMAN_API_KEY)", output);
        Assert.Contains("(from POSTMAN_WORKSPACE_ID)", output);
    }

    [Fact]
    public void Each_value_falls_through_independently_option_then_environment_then_secrets()
    {
        var postman = new FakePostman();

        // Key from the environment, workspace only in the secrets.
        var mixed = RunWithSecrets(Env(workspace: null), Secrets(key: "PMAK-other-secret-key"), postman);
        Assert.Contains("(from POSTMAN_API_KEY)", mixed.Out);
        Assert.Contains("(from user secrets)", mixed.Out);
        Assert.Equal(Key, postman.Requests[0].ApiKey);

        // The option beats both.
        var option = RunWithSecrets(Env(), Secrets(workspace: "00000000-aaaa-bbbb-cccc-0000000000ff"), new FakePostman(), "--workspace", OtherWorkspaceId);
        Assert.Contains($"{OtherWorkspaceId} (from --workspace)", option.Out);
    }

    [Fact]
    public void A_blank_environment_variable_does_not_hide_the_user_secret()
    {
        var postman = new FakePostman();

        var (code, output, _) = RunWithSecrets(Env(key: "   ", workspace: ""), Secrets(), postman);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("(from user secrets)", output);
    }

    [Fact]
    public void An_unreadable_secrets_file_is_a_configuration_error_that_names_no_secret()
    {
        var postman = new FakePostman();
        Func<string, string?> broken = _ => throw new InvalidDataException("The user secrets file is not valid JSON (line 1, position 9)");

        var (code, output, error) = RunWithSecrets(Env(key: null, workspace: null), broken, postman);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("Cannot read the user secrets: The user secrets file is not valid JSON", error);
        Assert.Equal("", output);
        Assert.Empty(postman.Requests);
    }

    [Fact]
    public void A_secret_that_is_wrong_is_reported_like_any_other_invalid_key_without_echoing_it()
    {
        var postman = new FakePostman { Respond = _ => Json(HttpStatusCode.Unauthorized, """{ "error": { "message": "Invalid API Key." } }""") };

        var (code, output, error) = RunWithSecrets(Env(key: null, workspace: null), Secrets(), postman);

        Assert.Equal(CliApplication.ConnectionFailed, code);
        Assert.Contains("Result: FAILED (InvalidApiKey)", output);
        AssertNoSecret(output, error);
    }

    [Fact]
    public void The_user_secrets_id_in_the_code_matches_the_project_so_dotnet_user_secrets_writes_where_the_cli_reads()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        var project = File.ReadAllText(Path.Combine(directory!.FullName, "src", "ApiCollection.Cli", "ApiCollection.Cli.csproj"));

        Assert.Contains($"<UserSecretsId>{CliEnvironment.UserSecretsId}</UserSecretsId>", project);
    }
}
