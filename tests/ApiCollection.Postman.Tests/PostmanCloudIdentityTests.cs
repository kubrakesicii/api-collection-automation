using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiCollection.Analysis.Discovery;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;
using NJsonSchema;

namespace ApiCollection.Postman.Tests;

/// <summary>Stable item ids and the "maintained by this tool" marker: present only for a collection that goes to Postman Cloud.</summary>
public sealed partial class PostmanCloudIdentityTests
{
    private static readonly EndpointDiscoveryEngine Engine = new();

    private const string Controllers = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpGet("{id:int}")] public IActionResult Get(int id) => Ok();
            [HttpPost] public IActionResult Create(CreateItem request) => Ok();
        }
        [ApiController]
        [Route("api/[controller]")]
        public class OrdersController : ControllerBase { [HttpGet] public IActionResult All() => Ok(); }
        public class CreateItem { public string Name { get; set; } = ""; }
        """;

    private static JsonNode Generate(string source = Controllers, string? idNamespace = null) => JsonNode.Parse(
        PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(
            Engine.DiscoverFromSources([source]).Endpoints, new PostmanCollectionOptions { Name = "Sample API", IdNamespace = idNamespace })))!;

    private static IEnumerable<JsonObject> AllItems(JsonNode collection) =>
        collection["item"]!.AsArray().OfType<JsonObject>().SelectMany(folder => new[] { folder }.Concat(folder["item"]!.AsArray().OfType<JsonObject>()));

    // ---- UUID version 5 ----

    [Fact]
    public void The_uuid_v5_implementation_matches_the_published_rfc_test_vector()
    {
        var dnsNamespace = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Equal("2ed6657d-e927-568b-95e1-2665a8aea6a2", DeterministicId.UuidV5(dnsNamespace, "www.example.com"));
    }

    [Fact]
    public void The_project_ids_match_an_independent_implementation_of_the_same_algorithm()
    {
        // Values computed with a separate implementation (Node's crypto), so a mistake in the byte order cannot hide in both.
        Assert.Equal("c7b4909b-ecc0-50f8-a18e-3ae73f2b964d", DeterministicId.For("ws1|Sample API|request|GET /api/items"));
        Assert.Equal("4e77a564-a9d4-54ac-ac2c-8eb768753f78", DeterministicId.For("ws1|Sample API|folder|Items"));
    }

    [Fact]
    public void Ids_are_deterministic_case_sensitive_and_well_formed()
    {
        Assert.Equal(DeterministicId.For("a|b"), DeterministicId.For("a|b"));
        Assert.NotEqual(DeterministicId.For("a|b"), DeterministicId.For("a|B"));
        Assert.Matches(UuidV5(), DeterministicId.For("anything"));
        Assert.Matches(UuidV5(), DeterministicId.For(string.Empty));
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex UuidV5();

    // ---- what is generated ----

    [Fact]
    public void Without_a_namespace_nothing_cloud_specific_is_added_so_local_files_stay_as_they_were()
    {
        var collection = Generate();

        Assert.All(AllItems(collection), item => Assert.Null(item["id"]));
        Assert.DoesNotContain(collection["variable"]!.AsArray(), v => (string?)v!["key"] == "automationManaged");
    }

    [Fact]
    public void With_a_namespace_every_folder_and_request_has_a_unique_stable_id_and_the_marker_appears()
    {
        var collection = Generate(idNamespace: "ws1|Sample API");

        var ids = AllItems(collection).Select(item => (string)item["id"]!).ToList();
        Assert.Equal(6, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches(UuidV5(), id));
        Assert.Equal(ids, AllItems(Generate(idNamespace: "ws1|Sample API")).Select(item => (string)item["id"]!));

        var marker = collection["variable"]!.AsArray().Single(v => (string?)v!["key"] == PostmanCollectionGenerator.ManagedMarkerVariable)!;
        Assert.Equal(PostmanCollectionGenerator.ManagedMarkerValue, (string?)marker["value"]);
        Assert.Equal("", (string?)collection["variable"]!.AsArray().Single(v => (string?)v!["key"] == "token")!["value"]);
    }

    [Fact]
    public void Ids_follow_the_endpoint_identity_the_folder_and_the_namespace()
    {
        var items = Generate(idNamespace: "ws1|Sample API")["item"]!.AsArray();
        var itemsFolder = items.Single(f => (string?)f!["name"] == "Items")!;

        Assert.Equal("4e77a564-a9d4-54ac-ac2c-8eb768753f78", (string?)itemsFolder["id"]);
        Assert.Equal("c7b4909b-ecc0-50f8-a18e-3ae73f2b964d", (string?)itemsFolder["item"]!.AsArray().Single(r => (string?)r!["name"] == "List")!["id"]);

        var otherNamespace = Generate(idNamespace: "ws2|Sample API")["item"]!.AsArray().Single(f => (string?)f!["name"] == "Items")!;
        Assert.NotEqual((string?)itemsFolder["id"], (string?)otherNamespace["id"]);
    }

    [Fact]
    public void Adding_removing_or_renaming_endpoints_never_changes_the_ids_of_the_others()
    {
        const string idNamespace = "ws1|Sample API";
        var before = AllItems(Generate(idNamespace: idNamespace)).ToDictionary(i => (string)i["name"]!, i => (string)i["id"]!);

        var evolved = Controllers
            .Replace("List()", "Index()")
            .Replace("[HttpPost]", "[HttpGet(\"count\")] public IActionResult Count() => Ok();\n    [HttpPost]");
        var after = AllItems(Generate(evolved, idNamespace)).ToDictionary(i => (string)i["name"]!, i => (string)i["id"]!);

        Assert.Equal(before["List"], after["Index"]);
        Assert.Equal(before["Get"], after["Get"]);
        Assert.Equal(before["Create"], after["Create"]);
        Assert.Equal(before["Orders"], after["Orders"]);
        Assert.DoesNotContain(after["Count"], before.Values);
    }

    [Fact]
    public async Task A_cloud_collection_with_ids_and_the_marker_still_satisfies_the_official_postman_schema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        var schema = await JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(directory!.FullName, "fixtures", "postman", "collection-v2.1.0.schema.json")));

        var errors = schema.Validate(Generate(idNamespace: "ws1|Sample API").ToJsonString());

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Path}: {e.Kind}")));
    }
}
