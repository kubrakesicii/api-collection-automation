using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Diff;
using ApiCollection.Core.Sync;
using ApiCollection.Postman.CloudApi;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;

namespace ApiCollection.Postman.Tests;

/// <summary>
/// Acceptance: the first publish creates the collection, later publishes update the same one (never a second), items keep their ids,
/// and every doubtful situation stops without writing. Runs against an in-memory Postman; nothing touches the network.
/// </summary>
public sealed partial class PostmanCollectionRepositoryTests
{
    private const string Workspace = "00000000-aaaa-bbbb-cccc-000000000001";
    private const string Name = "Sample API";
    private static readonly EndpointDiscoveryEngine Engine = new();

    private const string Items = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpGet("{id:int}")] public IActionResult Get(int id) => Ok();
            [HttpPost] public IActionResult Create(CreateItem request) => Ok();
        }
        public class CreateItem { public string Name { get; set; } = ""; }
        """;

    private const string Orders = """
        [ApiController]
        [Route("api/[controller]")]
        public class OrdersController : ControllerBase { [HttpGet] public IActionResult All() => Ok(); }
        """;

    // ---- helpers ----

    private static string Collection(string[] sources, string name = Name, string? idNamespace = null) =>
        PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(
            Engine.DiscoverFromSources(sources).Endpoints,
            new PostmanCollectionOptions { Name = name, IdNamespace = idNamespace ?? $"{Workspace}|{name}" }));

    private static SyncDecision Allowed() => new()
    {
        Outcome = SyncOutcome.Allowed, Summary = new ChangeSetSummary(1, 0, 0, 0), Limits = new SyncSafetyOptions()
    };

    private static SyncDecision NoChanges() => new()
    {
        Outcome = SyncOutcome.NoChanges, Summary = new ChangeSetSummary(0, 0, 0, 3), Limits = new SyncSafetyOptions()
    };

    private static SyncDecision Blocked() => new()
    {
        Outcome = SyncOutcome.Blocked,
        Summary = new ChangeSetSummary(0, 0, 5, 0),
        Limits = new SyncSafetyOptions(),
        Violations = [new SyncViolation(SyncViolationCode.MassDeletion, "too many", true), new SyncViolation(SyncViolationCode.DiscoveryIncomplete, "failed", true)]
    };

    private static FakePostmanCloud Healthy()
    {
        var fake = new FakePostmanCloud();
        fake.Workspaces.Add(Workspace);
        return fake;
    }

    private static Task<PostmanPublishResult> Publish(
        FakePostmanCloud fake, string collectionJson, SyncDecision? decision = null, string? known = null, bool dryRun = false,
        string name = Name, string workspace = Workspace, PostmanCloudOptions? options = null)
    {
        var client = new PostmanCloudClient(
            PostmanHttpClientFactory.Create(fake), options ?? new PostmanCloudOptions { ApiKey = FakePostmanCloud.ApiKey });
        return new PostmanCollectionRepository(client).PublishAsync(new PostmanPublishRequest
        {
            WorkspaceId = workspace,
            CollectionName = name,
            CollectionJson = collectionJson,
            KnownCollectionId = known,
            Decision = decision ?? Allowed(),
            DryRun = dryRun
        });
    }

    private static string[] Paths(FakePostmanCloud fake, HttpMethod method) =>
        fake.Calls.Where(c => c.Method == method).Select(c => Uri.UnescapeDataString(c.PathAndQuery)).ToArray();

    // ---- the acceptance criterion ----

    [Fact]
    public async Task The_first_run_creates_the_collection_and_later_runs_update_that_same_collection()
    {
        var fake = Healthy();
        var json = Collection([Items]);

        var first = await Publish(fake, json);

        Assert.Equal(PostmanPublishOutcome.Created, first.Outcome);
        Assert.True(first.Succeeded);
        Assert.True(first.Wrote);
        var uid = Assert.IsType<string>(first.CollectionUid);
        Assert.Single(fake.Collections);
        Assert.Equal(uid, fake.Collections[0].Uid);

        // A later run that has kept nothing finds the collection again (by name and marker) instead of creating another one.
        var second = await Publish(fake, json);
        Assert.Equal(PostmanPublishOutcome.Updated, second.Outcome);
        Assert.Equal(uid, second.CollectionUid);
        Assert.Single(fake.Collections);

        // And a run that kept the uid updates it directly.
        var third = await Publish(fake, json, known: uid);
        Assert.Equal(PostmanPublishOutcome.Updated, third.Outcome);
        Assert.Equal(uid, third.CollectionUid);
        Assert.Single(fake.Collections);

        Assert.Single(fake.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal(2, fake.Calls.Count(c => c.Method == HttpMethod.Put));
    }

    [Fact]
    public async Task The_first_run_asks_for_the_workspace_then_looks_for_the_collection_then_creates_it()
    {
        var fake = Healthy();

        await Publish(fake, Collection([Items]));

        Assert.Equal(
            [
                (HttpMethod.Get, $"/workspaces/{Workspace}"),
                (HttpMethod.Get, $"/collections?workspace={Workspace}&name=Sample API"),
                (HttpMethod.Post, $"/collections?workspace={Workspace}")
            ],
            fake.Calls.Select(c => (c.Method, Uri.UnescapeDataString(c.PathAndQuery))));
    }

    [Fact]
    public async Task What_is_sent_is_a_wrapped_collection_with_postmans_schema_stable_ids_and_the_marker()
    {
        var fake = Healthy();

        await Publish(fake, Collection([Items, Orders]));

        var body = JsonNode.Parse(fake.Calls.Single(c => c.Method == HttpMethod.Post).Body!)!;
        var collection = body["collection"]!;
        Assert.Equal(FakePostmanCloud.SchemaUrl, (string?)collection["info"]!["schema"]);
        Assert.Equal(Name, (string?)collection["info"]!["name"]);
        Assert.Null(collection["info"]!["_postman_id"]);
        Assert.Contains(collection["variable"]!.AsArray(), v => (string?)v!["key"] == "automationManaged" && (string?)v["value"] == "api-collection-automation");
        Assert.Equal("", (string?)collection["variable"]!.AsArray().Single(v => (string?)v!["key"] == "token")!["value"]);

        foreach (var folder in collection["item"]!.AsArray())
        {
            Assert.Matches(UuidV5(), (string)folder!["id"]!);
            Assert.All(folder["item"]!.AsArray(), request => Assert.Matches(UuidV5(), (string)request!["id"]!));
        }
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex UuidV5();

    // ---- request and folder identities ----

    [Fact]
    public async Task Folders_and_requests_keep_their_ids_across_updates_and_only_new_endpoints_get_new_ones()
    {
        var fake = Healthy();
        var result = await Publish(fake, Collection([Items, Orders]));
        var before = FakePostmanCloud.ItemIds(fake.Collections[0]);
        var idNamespace = $"{Workspace}|{Name}";

        // Orders disappears from the API and a new Items action appears.
        var evolved = Items.Replace("[HttpPost]", "[HttpGet(\"count\")] public IActionResult Count() => Ok();\n    [HttpPost]");
        var update = await Publish(fake, Collection([evolved]), known: result.CollectionUid);
        var after = FakePostmanCloud.ItemIds(fake.Collections[0]);

        Assert.Equal(PostmanPublishOutcome.Updated, update.Outcome);
        foreach (var unchanged in new[] { "Items", "Items/List", "Items/Get", "Items/Create" })
        {
            Assert.Equal(before[unchanged], after[unchanged]);
        }

        Assert.DoesNotContain("Items/Count", before.Keys);
        Assert.Equal(DeterministicId.For($"{idNamespace}|request|GET /api/items/count"), after["Items/Count"]);
        Assert.DoesNotContain("Orders", after.Keys);
        Assert.Equal(DeterministicId.For($"{idNamespace}|request|GET /api/items"), after["Items/List"]);
        Assert.Equal(DeterministicId.For($"{idNamespace}|folder|Items"), after["Items"]);
    }

    [Fact]
    public async Task Renaming_an_action_does_not_change_its_request_id_because_it_comes_from_the_endpoint_identity()
    {
        var fake = Healthy();
        var first = await Publish(fake, Collection([Items]));
        var before = FakePostmanCloud.ItemIds(fake.Collections[0]);

        await Publish(fake, Collection([Items.Replace("List()", "Index()")]), known: first.CollectionUid);
        var after = FakePostmanCloud.ItemIds(fake.Collections[0]);

        Assert.Equal(before["Items/List"], after["Items/Index"]);
        Assert.DoesNotContain("Items/List", after.Keys);
    }

    [Fact]
    public async Task The_same_api_published_to_two_workspaces_gets_different_ids_so_the_ids_cannot_collide()
    {
        var fake = Healthy();
        const string other = "00000000-aaaa-bbbb-cccc-000000000002";
        fake.Workspaces.Add(other);

        await Publish(fake, Collection([Items]));
        await Publish(fake, Collection([Items], idNamespace: $"{other}|{Name}"), workspace: other);

        Assert.Equal(2, fake.Collections.Count);
        var ids = fake.Collections.Select(FakePostmanCloud.ItemIds).ToList();
        Assert.Empty(ids[0].Values.Intersect(ids[1].Values));
    }

    // ---- no unnecessary collection: finding what already exists ----

    [Fact]
    public async Task A_collection_with_the_same_name_that_this_tool_does_not_maintain_is_never_overwritten_or_duplicated()
    {
        var fake = Healthy();
        var foreign = fake.Add(Workspace, Name, managed: false);
        var before = foreign.Collection.ToJsonString();

        var result = await Publish(fake, Collection([Items]));

        Assert.Equal(PostmanPublishOutcome.Failed, result.Outcome);
        Assert.Equal(PostmanPublishFailure.NameConflict, result.Failure);
        Assert.Contains("does not maintain", result.Message);
        Assert.Empty(fake.Writes);
        Assert.Single(fake.Collections);
        Assert.Equal(before, foreign.Collection.ToJsonString());
    }

    [Fact]
    public async Task Two_collections_maintained_by_this_tool_under_the_same_name_are_not_guessed_between()
    {
        var fake = Healthy();
        fake.Add(Workspace, Name, managed: true);
        fake.Add(Workspace, Name, managed: true);

        var result = await Publish(fake, Collection([Items]));

        Assert.Equal((PostmanPublishOutcome.Failed, PostmanPublishFailure.AmbiguousCollections), (result.Outcome, result.Failure));
        Assert.Contains("2 collections named", result.Message);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task One_maintained_collection_among_unrelated_ones_of_the_same_name_is_the_one_that_is_updated()
    {
        var fake = Healthy();
        var foreign = fake.Add(Workspace, Name, managed: false);
        var ours = fake.Add(Workspace, Name, managed: true);

        var result = await Publish(fake, Collection([Items]));

        Assert.Equal(PostmanPublishOutcome.Updated, result.Outcome);
        Assert.Equal(ours.Uid, result.CollectionUid);
        Assert.Equal(2, fake.Collections.Count);
        Assert.Empty(foreign.Collection["item"]!.AsArray());
    }

    [Fact]
    public async Task Collections_with_other_names_or_in_other_workspaces_are_ignored()
    {
        var fake = Healthy();
        fake.Workspaces.Add("00000000-aaaa-bbbb-cccc-000000000002");
        fake.Add(Workspace, "Another API", managed: true);
        fake.Add("00000000-aaaa-bbbb-cccc-000000000002", Name, managed: true);

        var result = await Publish(fake, Collection([Items]));

        Assert.Equal(PostmanPublishOutcome.Created, result.Outcome);
        Assert.Equal(3, fake.Collections.Count);
    }

    [Fact]
    public async Task A_collection_that_vanished_is_not_silently_replaced_by_a_new_one()
    {
        var fake = Healthy();

        var result = await Publish(fake, Collection([Items]), known: "1234567-00000000-0000-4000-8000-0000000000ff");

        Assert.Equal((PostmanPublishOutcome.Failed, PostmanPublishFailure.CollectionMissing), (result.Outcome, result.Failure));
        Assert.Contains("no longer exists", result.Message);
        Assert.Empty(fake.Collections);
        Assert.DoesNotContain(fake.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task An_unknown_workspace_stops_before_listing_because_postman_answers_it_with_an_empty_list()
    {
        var fake = Healthy();

        var result = await Publish(fake, Collection([Items], idNamespace: "x"), workspace: "00000000-aaaa-bbbb-cccc-0000000000ee");

        Assert.Equal((PostmanPublishOutcome.Failed, PostmanPublishFailure.Api, PostmanApiStatus.NotFound), (result.Outcome, result.Failure, result.ApiStatus));
        Assert.Contains("The workspace cannot be used", result.Message);
        Assert.Equal(["/workspaces/00000000-aaaa-bbbb-cccc-0000000000ee"], fake.Calls.Select(c => c.PathAndQuery));
        Assert.Empty(fake.Collections);
    }

    // ---- the safety gate ----

    [Fact]
    public async Task A_blocked_decision_never_reaches_postman_not_even_for_reading()
    {
        var fake = Healthy();

        var result = await Publish(fake, Collection([Items]), Blocked());

        Assert.Equal(PostmanPublishOutcome.Blocked, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Contains("MassDeletion, DiscoveryIncomplete", result.Message);
        Assert.Contains("Nothing was sent", result.Message);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Nothing_changed_and_the_collection_is_known_means_no_request_at_all()
    {
        var fake = Healthy();
        var created = await Publish(fake, Collection([Items]));
        fake.Calls.Clear();

        var result = await Publish(fake, Collection([Items]), NoChanges(), known: created.CollectionUid);

        Assert.Equal(PostmanPublishOutcome.NoChanges, result.Outcome);
        Assert.Equal(created.CollectionUid, result.CollectionUid);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Nothing_changed_locally_still_creates_the_collection_when_it_was_never_published()
    {
        var fake = Healthy();

        var result = await Publish(fake, Collection([Items]), NoChanges());

        Assert.Equal(PostmanPublishOutcome.Created, result.Outcome);
        Assert.Single(fake.Collections);
    }

    [Fact]
    public async Task Nothing_changed_and_the_collection_exists_is_adopted_without_writing()
    {
        var fake = Healthy();
        var ours = fake.Add(Workspace, Name, managed: true);

        var result = await Publish(fake, Collection([Items]), NoChanges());

        Assert.Equal(PostmanPublishOutcome.Adopted, result.Outcome);
        Assert.Equal(ours.Uid, result.CollectionUid);
        Assert.Empty(fake.Writes);
        Assert.False(result.Wrote);
    }

    // ---- dry run ----

    [Fact]
    public async Task A_dry_run_reports_what_would_happen_and_only_reads()
    {
        var fake = Healthy();
        var json = Collection([Items]);

        var wouldCreate = await Publish(fake, json, dryRun: true);
        Assert.Equal(PostmanPublishOutcome.WouldCreate, wouldCreate.Outcome);
        Assert.Contains("would be created", wouldCreate.Message);
        Assert.Empty(fake.Collections);

        await Publish(fake, json);
        fake.Calls.Clear();
        var wouldUpdate = await Publish(fake, json, dryRun: true);
        Assert.Equal(PostmanPublishOutcome.WouldUpdate, wouldUpdate.Outcome);

        var wouldUpdateKnown = await Publish(fake, json, known: fake.Collections[0].Uid, dryRun: true);
        Assert.Equal(PostmanPublishOutcome.WouldUpdate, wouldUpdateKnown.Outcome);

        Assert.All(fake.Calls, c => Assert.Equal(HttpMethod.Get, c.Method));
        Assert.False(wouldCreate.Wrote);
    }

    [Fact]
    public async Task A_dry_run_for_a_vanished_collection_says_so_instead_of_pretending_to_update()
    {
        var fake = Healthy();

        var result = await Publish(fake, Collection([Items]), known: "1234567-gone", dryRun: true);

        Assert.Equal(PostmanPublishFailure.CollectionMissing, result.Failure);
        Assert.Empty(fake.Writes);
    }

    // ---- failures talking to Postman ----

    [Fact]
    public async Task A_rejected_key_stops_the_publish_without_any_write()
    {
        var fake = Healthy();

        var result = await Publish(fake, Collection([Items]), options: new PostmanCloudOptions { ApiKey = "PMAK-wrong-key" });

        Assert.Equal((PostmanPublishOutcome.Failed, PostmanPublishFailure.Api, PostmanApiStatus.InvalidApiKey, 401), (result.Outcome, result.Failure, result.ApiStatus, result.HttpStatus));
        Assert.Contains("Invalid API Key", result.Message);
        Assert.Empty(fake.Writes);
        Assert.DoesNotContain("PMAK-wrong-key", result.Message);
    }

    [Fact]
    public async Task Rate_limiting_while_listing_is_reported_with_how_long_to_wait_and_nothing_is_written()
    {
        var fake = Healthy();
        fake.Intercept = call =>
        {
            if (!call.PathAndQuery.StartsWith("/collections?", StringComparison.Ordinal))
            {
                return null;
            }

            var response = FakePostmanCloud.Json(HttpStatusCode.TooManyRequests, new JsonObject { ["type"] = "rate-limit", ["title"] = "Too Many Requests", ["detail"] = "Slow down", ["status"] = 429 });
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
            return response;
        };

        var result = await Publish(fake, Collection([Items]));

        Assert.Equal((PostmanApiStatus.RateLimited, TimeSpan.FromSeconds(9)), (result.ApiStatus, result.RetryAfter));
        Assert.Contains("Slow down", result.Message);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task A_create_that_postman_refuses_is_reported_with_its_reason_and_leaves_nothing_behind()
    {
        var fake = Healthy();
        fake.Intercept = call => call.Method == HttpMethod.Post
            ? FakePostmanCloud.Error(HttpStatusCode.BadRequest, "malformedRequestError", "item[0].request.method is invalid")
            : null;

        var result = await Publish(fake, Collection([Items]));

        Assert.Equal((PostmanPublishOutcome.Failed, PostmanApiStatus.Rejected), (result.Outcome, result.ApiStatus));
        Assert.Contains("The collection could not be created", result.Message);
        Assert.Contains("item[0].request.method is invalid", result.Message);
        Assert.Empty(fake.Collections);
    }

    [Theory]
    [InlineData(500, PostmanApiStatus.ServerError)]
    [InlineData(503, PostmanApiStatus.ServerError)]
    [InlineData(409, PostmanApiStatus.Conflict)]
    public async Task Other_failures_on_update_are_reported_and_the_collection_is_left_as_it_was(int status, PostmanApiStatus expected)
    {
        var fake = Healthy();
        var created = await Publish(fake, Collection([Items]));
        var before = fake.Collections[0].Collection.ToJsonString();
        fake.Intercept = call => call.Method == HttpMethod.Put
            ? FakePostmanCloud.Json((HttpStatusCode)status, new JsonObject { ["type"] = "x", ["title"] = "Trouble", ["detail"] = "try later", ["status"] = status })
            : null;

        var result = await Publish(fake, Collection([Items, Orders]), known: created.CollectionUid);

        Assert.Equal((PostmanPublishOutcome.Failed, expected), (result.Outcome, result.ApiStatus));
        Assert.Contains("The collection could not be updated", result.Message);
        Assert.Equal(before, fake.Collections[0].Collection.ToJsonString());
    }

    [Fact]
    public async Task A_create_that_succeeded_but_whose_answer_has_no_id_is_found_again_next_time_by_its_marker()
    {
        var fake = Healthy();
        fake.Intercept = call => call.Method == HttpMethod.Post && fake.Collections.Count == 0
            ? CreateButAnswerWithoutId(fake, call)
            : null;

        var first = await Publish(fake, Collection([Items]));

        Assert.Equal((PostmanPublishOutcome.Failed, PostmanApiStatus.UnexpectedResponse), (first.Outcome, first.ApiStatus));
        Assert.Contains("finds it again by its marker", first.Message);
        Assert.Single(fake.Collections);

        fake.Intercept = null;
        var second = await Publish(fake, Collection([Items]));
        Assert.Equal(PostmanPublishOutcome.Updated, second.Outcome);
        Assert.Single(fake.Collections);
    }

    private static HttpResponseMessage CreateButAnswerWithoutId(FakePostmanCloud fake, FakePostmanCloud.Call call)
    {
        var id = "00000000-0000-4000-8000-0000000000aa";
        var collection = (JsonObject)JsonNode.Parse(call.Body!)!["collection"]!.DeepClone();
        fake.Collections.Add(new FakePostmanCloud.Stored { Id = id, Uid = $"1234567-{id}", WorkspaceId = Workspace, Collection = collection });
        return FakePostmanCloud.Json(HttpStatusCode.OK, new JsonObject { ["collection"] = new JsonObject { ["name"] = Name } });
    }

    // ---- input problems: nothing is sent ----

    [Fact]
    public async Task Unusable_requests_are_refused_before_anything_is_sent()
    {
        var fake = Healthy();
        var good = Collection([Items]);

        var results = new[]
        {
            await Publish(fake, good, workspace: "../me"),
            await Publish(fake, good, known: "a/b"),
            await Publish(fake, good, name: "Another name"),
            await Publish(fake, good, name: " "),
            await Publish(fake, "{ not json"),
            await Publish(fake, """{ "item": [] }"""),
            await Publish(fake, PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(Engine.DiscoverFromSources([Items]).Endpoints, new PostmanCollectionOptions { Name = Name })))
        };

        Assert.All(results, r => Assert.Equal((PostmanPublishOutcome.Failed, PostmanPublishFailure.InvalidInput), (r.Outcome, r.Failure)));
        Assert.All(results, r => Assert.Contains("Nothing was sent", r.Message));
        Assert.Contains("lacks the 'automationManaged' marker", results[^1].Message);
        Assert.Contains("differs from the name", results[2].Message);
        Assert.Empty(fake.Calls);
    }

    // ---- what the client may and may not do ----

    [Fact]
    public async Task Across_a_whole_lifecycle_only_get_post_and_put_are_ever_used_never_delete()
    {
        var fake = Healthy();
        var json = Collection([Items]);

        var created = await Publish(fake, json);
        await Publish(fake, json);
        await Publish(fake, Collection([Orders]), known: created.CollectionUid);
        await Publish(fake, json, NoChanges(), known: created.CollectionUid);
        await Publish(fake, json, Blocked());
        await Publish(fake, json, dryRun: true);

        Assert.All(fake.Calls, c => Assert.Contains(c.Method, new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put }));
        Assert.DoesNotContain(fake.Calls, c => c.Method == HttpMethod.Delete);
        Assert.All(fake.Calls, c => Assert.Equal(FakePostmanCloud.ApiKey, c.Key));
        Assert.DoesNotContain(typeof(PostmanCloudClient).GetMethods(), m => m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_key_is_only_ever_sent_in_the_header_never_in_the_address_or_the_body_and_never_printed()
    {
        var fake = Healthy();

        var result = await Publish(fake, Collection([Items]));

        Assert.All(fake.Calls, c =>
        {
            Assert.DoesNotContain(FakePostmanCloud.ApiKey, c.PathAndQuery);
            Assert.DoesNotContain(FakePostmanCloud.ApiKey, c.Body ?? string.Empty);
        });
        Assert.DoesNotContain(FakePostmanCloud.ApiKey, result.Message);
        Assert.DoesNotContain(FakePostmanCloud.ApiKey, result.ToString());
    }
}
