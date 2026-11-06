using System.Net;
using System.Text.Json.Nodes;
using ApiCollection.Postman.CloudApi;

namespace ApiCollection.Postman.Tests;

/// <summary>The Postman API client call by call: addresses, id and payload safety, and how every kind of answer is classified.</summary>
public sealed class PostmanCloudClientTests
{
    private const string Workspace = "00000000-aaaa-bbbb-cccc-000000000001";

    private const string Minimal = """
        {
          "info": { "name": "Minimal", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
          "item": [ { "name": "Folder", "id": "11111111-1111-5111-8111-111111111111", "item": [] } ],
          "variable": [ { "key": "automationManaged", "value": "api-collection-automation" } ]
        }
        """;

    private static FakePostmanCloud Healthy()
    {
        var fake = new FakePostmanCloud();
        fake.Workspaces.Add(Workspace);
        return fake;
    }

    private static PostmanCloudClient Client(FakePostmanCloud fake, PostmanCloudOptions? options = null) =>
        new(PostmanHttpClientFactory.Create(fake), options ?? new PostmanCloudOptions { ApiKey = FakePostmanCloud.ApiKey });

    // ---- reads ----

    [Fact]
    public async Task The_workspace_and_the_collection_list_are_read_with_escaped_addresses()
    {
        var fake = Healthy();
        fake.Add(Workspace, "My API & Co?", managed: true);

        var workspace = await Client(fake).GetWorkspaceAsync(Workspace);
        var found = await Client(fake).FindCollectionsByNameAsync(Workspace, "My API & Co?");

        Assert.Equal(new PostmanWorkspaceInfo(Workspace, "Test Workspace", "team"), workspace.Value);
        Assert.Equal(["My API & Co?"], found.Value!.Select(c => c.Name));
        Assert.Contains(fake.Calls, c => c.PathAndQuery == $"/collections?workspace={Workspace}&name=My%20API%20%26%20Co%3F");
        Assert.All(fake.Calls, c => Assert.Equal(HttpMethod.Get, c.Method));
    }

    [Fact]
    public async Task Reading_a_collection_returns_its_json()
    {
        var fake = Healthy();
        var stored = fake.Add(Workspace, "Readable", managed: true);

        var result = await Client(fake).GetCollectionAsync(stored.Uid);

        Assert.True(result.Succeeded);
        Assert.Equal("Readable", (string?)JsonNode.Parse(result.Value!)!["info"]!["name"]);
    }

    [Fact]
    public async Task A_missing_collection_is_not_found_whether_postman_says_400_or_404_but_other_400s_are_rejections()
    {
        var fake = Healthy();

        var documented = await Client(fake).GetCollectionAsync("1234567-missing");
        Assert.Equal((PostmanApiStatus.NotFound, 400), (documented.Status, documented.HttpStatus));

        fake.Intercept = _ => FakePostmanCloud.Json(HttpStatusCode.NotFound, new JsonObject { ["type"] = "x", ["title"] = "Not Found", ["detail"] = "gone", ["status"] = 404 });
        Assert.Equal(PostmanApiStatus.NotFound, (await Client(fake).GetCollectionAsync("1234567-missing")).Status);

        fake.Intercept = _ => FakePostmanCloud.Error(HttpStatusCode.BadRequest, "malformedRequestError", "The id is malformed");
        var other = await Client(fake).GetCollectionAsync("1234567-missing");
        Assert.Equal(PostmanApiStatus.Rejected, other.Status);
        Assert.Contains("The id is malformed", other.Message);
    }

    [Fact]
    public async Task A_listing_that_is_not_a_collection_list_is_not_trusted()
    {
        var fake = Healthy();
        fake.Intercept = _ => FakePostmanCloud.Json(HttpStatusCode.OK, new JsonObject { ["hello"] = "world" });
        Assert.Equal(PostmanApiStatus.UnexpectedResponse, (await Client(fake).FindCollectionsByNameAsync(Workspace, "x")).Status);

        fake.Intercept = _ => FakePostmanCloud.Json(HttpStatusCode.OK, new JsonObject
        {
            ["collections"] = new JsonArray(
                new JsonObject { ["name"] = "no uid" },
                new JsonObject { ["uid"] = "1-a", ["name"] = "usable" })
        });
        var partial = await Client(fake).FindCollectionsByNameAsync(Workspace, "x");
        Assert.Equal(["usable"], partial.Value!.Select(c => c.Name));
    }

    // ---- ids are validated before they reach an address ----

    [Fact]
    public async Task Ids_that_could_alter_the_request_path_are_refused_without_a_request()
    {
        var fake = Healthy();
        var client = Client(fake);

        PostmanApiStatus[] statuses =
        [
            (await client.GetWorkspaceAsync("../me")).Status,
            (await client.FindCollectionsByNameAsync("a/b", "x")).Status,
            (await client.GetCollectionAsync("a b")).Status,
            (await client.GetCollectionAsync("..%2f..")).Status,
            (await client.CreateCollectionAsync("../x", "{}")).Status,
            (await client.UpdateCollectionAsync("a/b", "{}")).Status
        ];

        Assert.All(statuses, s => Assert.Equal(PostmanApiStatus.NotConfigured, s));
        Assert.Empty(fake.Calls);
    }

    // ---- writes: payload preparation ----

    [Fact]
    public async Task The_payload_uses_postmans_schema_and_drops_the_collections_own_ids_but_keeps_item_ids()
    {
        var fake = Healthy();
        const string withOwnIds = """
            {
              "info": { "name": "Own ids", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
                        "_postman_id": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "id": "bbbb", "uid": "1-bbbb" },
              "id": "cccc", "uid": "1-cccc",
              "item": [ { "name": "Folder", "id": "11111111-1111-5111-8111-111111111111", "item": [ { "name": "R", "id": "22222222-2222-5222-8222-222222222222" } ] } ],
              "variable": []
            }
            """;

        var created = await Client(fake).CreateCollectionAsync(Workspace, withOwnIds);

        // The fake rejects a payload carrying the collection's own ids or another schema address, as Postman does.
        Assert.True(created.Succeeded, created.Message);
        var sent = JsonNode.Parse(fake.Calls.Single(c => c.Method == HttpMethod.Post).Body!)!["collection"]!;
        Assert.Equal(PostmanCloudClient.CollectionSchema, (string?)sent["info"]!["schema"]);
        Assert.Null(sent["info"]!["_postman_id"]);
        Assert.Null(sent["info"]!["id"]);
        Assert.Null(sent["info"]!["uid"]);
        Assert.Null(sent["id"]);
        Assert.Null(sent["uid"]);
        Assert.Equal("11111111-1111-5111-8111-111111111111", (string?)sent["item"]![0]!["id"]);
        Assert.Equal("22222222-2222-5222-8222-222222222222", (string?)sent["item"]![0]!["item"]![0]!["id"]);
        Assert.NotNull(created.Value!.Uid);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "info": { "name": "x" } }""")]
    [InlineData("""{ "item": [] }""")]
    [InlineData("""{ "info": { "name": 5 }, "item": [] }""")]
    public async Task Something_that_is_not_a_collection_is_refused_without_a_request(string json)
    {
        var fake = Healthy();

        var create = await Client(fake).CreateCollectionAsync(Workspace, json);
        var update = await Client(fake).UpdateCollectionAsync("1234567-abc", json);

        Assert.Equal(PostmanApiStatus.NotConfigured, create.Status);
        Assert.Equal(PostmanApiStatus.NotConfigured, update.Status);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_collection_over_the_size_limit_is_refused_locally()
    {
        var fake = Healthy();

        var result = await Client(fake, new PostmanCloudOptions { ApiKey = FakePostmanCloud.ApiKey, MaxPayloadBytes = 100 }).CreateCollectionAsync(Workspace, Minimal);

        Assert.Equal(PostmanApiStatus.NotConfigured, result.Status);
        Assert.Contains("is larger than", result.Message);
        Assert.Empty(fake.Calls);
    }

    // ---- classifying answers ----

    [Fact]
    public async Task An_update_that_postman_answers_asynchronously_is_not_assumed_to_have_finished()
    {
        var fake = Healthy();
        var stored = fake.Add(Workspace, "Minimal", managed: true);
        fake.Intercept = call => call.Method == HttpMethod.Put
            ? FakePostmanCloud.Json(HttpStatusCode.Accepted, new JsonObject { ["task"] = new JsonObject { ["id"] = "t1", ["status"] = "pending" } })
            : null;

        var result = await Client(fake).UpdateCollectionAsync(stored.Uid, Minimal);

        Assert.Equal((PostmanApiStatus.UnexpectedResponse, 202), (result.Status, result.HttpStatus));
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(401, PostmanApiStatus.InvalidApiKey)]
    [InlineData(403, PostmanApiStatus.AccessDenied)]
    [InlineData(404, PostmanApiStatus.NotFound)]
    [InlineData(409, PostmanApiStatus.Conflict)]
    [InlineData(429, PostmanApiStatus.RateLimited)]
    [InlineData(500, PostmanApiStatus.ServerError)]
    [InlineData(503, PostmanApiStatus.ServerError)]
    [InlineData(418, PostmanApiStatus.UnexpectedResponse)]
    [InlineData(302, PostmanApiStatus.UnexpectedResponse)]
    public async Task Every_status_is_classified_in_both_documented_error_shapes(int status, PostmanApiStatus expected)
    {
        foreach (var body in new[]
        {
            new JsonObject { ["error"] = new JsonObject { ["name"] = "someError", ["message"] = "first shape" } },
            new JsonObject { ["type"] = "t", ["title"] = "Title", ["detail"] = "second shape", ["status"] = status }
        })
        {
            var fake = Healthy();
            fake.Intercept = _ => FakePostmanCloud.Json((HttpStatusCode)status, body);

            var result = await Client(fake).UpdateCollectionAsync("1234567-abc", Minimal);

            Assert.Equal((expected, status), (result.Status, result.HttpStatus));
            Assert.False(result.Succeeded);
            if (status != 302)
            {
                Assert.Matches("first shape|second shape", result.Message);
            }
        }
    }

    [Fact]
    public async Task Rate_limiting_reports_how_long_to_wait_and_counts_as_transient()
    {
        var fake = Healthy();
        fake.Intercept = _ =>
        {
            var response = FakePostmanCloud.Json((HttpStatusCode)429, new JsonObject { ["detail"] = "slow down" });
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };

        var result = await Client(fake).GetWorkspaceAsync(Workspace);

        Assert.Equal((PostmanApiStatus.RateLimited, TimeSpan.FromSeconds(30)), (result.Status, result.RetryAfter));
        Assert.True(result.IsTransient);
    }

    [Fact]
    public async Task A_create_answer_without_a_collection_uid_or_not_json_is_not_trusted()
    {
        var fake = Healthy();

        fake.Intercept = _ => FakePostmanCloud.Json(HttpStatusCode.OK, new JsonObject { ["collection"] = new JsonObject { ["name"] = "x" } });
        var noUid = await Client(fake).CreateCollectionAsync(Workspace, Minimal);
        Assert.Equal(PostmanApiStatus.UnexpectedResponse, noUid.Status);
        Assert.Contains("finds it again by its marker", noUid.Message);

        fake.Intercept = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>") };
        var html = await Client(fake).CreateCollectionAsync(Workspace, Minimal);
        Assert.Equal(PostmanApiStatus.UnexpectedResponse, html.Status);
        Assert.Contains("not a Postman API response", html.Message);
    }

    [Fact]
    public async Task Network_failures_and_timeouts_are_transient_results_not_exceptions()
    {
        var fake = Healthy();
        fake.Intercept = _ => throw new HttpRequestException("no route", new IOException("Network is unreachable."));
        var network = await Client(fake).GetWorkspaceAsync(Workspace);
        Assert.Equal(PostmanApiStatus.NetworkError, network.Status);
        Assert.True(network.IsTransient);
        Assert.Contains("Network is unreachable.", network.Message);

        var neverAnswers = new NeverAnswers();
        var slow = new PostmanCloudClient(
            PostmanHttpClientFactory.Create(neverAnswers),
            new PostmanCloudOptions { ApiKey = FakePostmanCloud.ApiKey, Timeout = TimeSpan.FromMilliseconds(50) });
        var timeout = await slow.GetWorkspaceAsync(Workspace);
        Assert.Equal(PostmanApiStatus.Timeout, timeout.Status);
        Assert.True(timeout.IsTransient);
    }

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    // ---- the key ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PMAK-has space")]
    [InlineData("PMAK-line\nbreak")]
    [InlineData("PMAK-ünïcode")]
    public async Task A_missing_or_damaged_key_is_refused_without_a_request_and_without_an_echo(string key)
    {
        var fake = Healthy();

        var result = await Client(fake, new PostmanCloudOptions { ApiKey = key }).GetWorkspaceAsync(Workspace);

        Assert.Contains(result.Status, new[] { PostmanApiStatus.NotConfigured, PostmanApiStatus.InvalidApiKey });
        if (key.Trim().Length > 0)
        {
            Assert.DoesNotContain(key.Trim(), result.Message, StringComparison.Ordinal);
        }

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task The_key_is_never_sent_over_plain_http()
    {
        var fake = Healthy();

        var result = await Client(fake, new PostmanCloudOptions { ApiKey = FakePostmanCloud.ApiKey, BaseAddress = new Uri("http://api.getpostman.com") }).GetWorkspaceAsync(Workspace);

        Assert.Equal(PostmanApiStatus.NotConfigured, result.Status);
        Assert.Contains("must use https", result.Message);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task The_key_is_scrubbed_from_whatever_postman_or_the_network_says()
    {
        var fake = Healthy();
        fake.Intercept = _ => FakePostmanCloud.Error(HttpStatusCode.Forbidden, "forbiddenError", $"Key {FakePostmanCloud.ApiKey} may not do this");
        var echoed = await Client(fake).UpdateCollectionAsync("1234567-abc", Minimal);

        fake.Intercept = _ => throw new HttpRequestException($"failed for {FakePostmanCloud.ApiKey}");
        var network = await Client(fake).UpdateCollectionAsync("1234567-abc", Minimal);

        Assert.DoesNotContain(FakePostmanCloud.ApiKey, echoed.Message);
        Assert.DoesNotContain(FakePostmanCloud.ApiKey, network.Message);
        Assert.DoesNotContain(FakePostmanCloud.ApiKey, echoed.ToString());
        Assert.Contains("*****", echoed.Message);
    }

    [Fact]
    public void The_options_never_print_the_key()
    {
        var text = new PostmanCloudOptions { ApiKey = FakePostmanCloud.ApiKey }.ToString();

        Assert.DoesNotContain(FakePostmanCloud.ApiKey, text);
        Assert.Contains("ApiKey = *****", text);
    }

    [Fact]
    public async Task A_huge_answer_is_not_read_without_limit()
    {
        var fake = Healthy();
        fake.Intercept = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"workspace\":{\"name\":\"" + new string('a', 5 * 1024 * 1024) + "\"}}") };

        var result = await Client(fake).GetWorkspaceAsync(Workspace);

        Assert.Equal(PostmanApiStatus.UnexpectedResponse, result.Status);
    }
}
