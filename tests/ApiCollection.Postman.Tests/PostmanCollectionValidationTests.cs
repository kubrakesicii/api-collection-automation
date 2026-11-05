using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;
using ApiCollection.Infrastructure.Configuration;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;
using NJsonSchema;

namespace ApiCollection.Postman.Tests;

/// <summary>
/// Validates generated collections against the official Postman Collection v2.1.0 JSON schema
/// (fixtures/postman/collection-v2.1.0.schema.json) and against the rules Postman needs for a clean import.
/// </summary>
public sealed partial class PostmanCollectionValidationTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> Schema = new(() =>
        JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(Root, "fixtures", "postman", "collection-v2.1.0.schema.json"))).GetAwaiter().GetResult());

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("ApiCollection.sln was not found above the test directory.");
    }

    private static string SampleApi => Path.Combine(Root, "sample-api", "SampleApi");

    private static string SecureApi => Path.Combine(Root, "fixtures", "SecureApi");

    private static string Golden(string name) => Path.Combine(Root, "fixtures", "postman", name);

    private static string SampleCollection(bool withExamples = true)
    {
        var metadata = withExamples ? ExampleMetadataLoader.Load(Path.Combine(Root, "config", "examples.sample-api.json")) : null;
        var discovery = new EndpointDiscoveryEngine().DiscoverFromDirectory(SampleApi, metadata);
        return PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(discovery.Endpoints, new PostmanCollectionOptions { Name = "Sample API" }));
    }

    private static string SecureCollection()
    {
        var discovery = new EndpointDiscoveryEngine().DiscoverFromDirectory(SecureApi);
        return PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(discovery.Endpoints, new PostmanCollectionOptions { Name = "Secure API Fixture" }));
    }

    private const string EdgeSource = """
        [ApiController]
        [Route("api/[controller]")]
        public class EdgeController : ControllerBase
        {
            [HttpGet] public IActionResult Get() => Ok();
            [HttpGet("all")] public IActionResult Get(int page) => Ok();
            [HttpGet("files/{*rest}")] public IActionResult Files(string rest) => Ok();
            [HttpPost("bulk")] public IActionResult Bulk(List<Item> items) => Ok();
            [HttpPut("{id:int}")] public IActionResult Put(int id, [FromBody] Item item, [FromQuery] string? note) => Ok();
        }
        public class Item { public string Name { get; set; } = ""; public int Qty { get; set; } public Tag? Tag { get; set; } }
        public class Tag { public string Label { get; set; } = ""; }
        """;

    private static string EdgeCollection() => PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(
        new EndpointDiscoveryEngine().DiscoverFromSources([EdgeSource]).Endpoints));

    private static string EmptyCollection() => PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate([]));

    public static TheoryData<string> AllCollections => new() { "sample", "sample-no-examples", "secure", "edge", "empty" };

    private static string Build(string name) => name switch
    {
        "sample" => SampleCollection(),
        "sample-no-examples" => SampleCollection(withExamples: false),
        "secure" => SecureCollection(),
        "edge" => EdgeCollection(),
        "empty" => EmptyCollection(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    // ---- the schema validator itself must be able to fail ----

    [Fact]
    public void The_official_schema_is_loaded_and_rejects_invalid_collections()
    {
        Assert.Equal("http://json-schema.org/draft-04/schema#", Schema.Value.SchemaVersion);

        Assert.NotEmpty(Schema.Value.Validate("""{ "item": [] }"""));
        Assert.NotEmpty(Schema.Value.Validate("""{ "info": { "name": "x" }, "item": [] }"""));
        Assert.NotEmpty(Schema.Value.Validate("""{ "info": { "name": "x", "schema": "s" }, "item": {} }"""));
        Assert.NotEmpty(Schema.Value.Validate("""
            { "info": { "name": "x", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [ { "name": "f", "item": [ { "name": "r", "request": { "method": 5 } } ] } ] }
            """));
        Assert.Empty(Schema.Value.Validate("""
            { "info": { "name": "x", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" }, "item": [] }
            """));
    }

    [Fact]
    public void Corrupting_a_generated_collection_is_detected()
    {
        var node = JsonNode.Parse(SampleCollection())!;
        node["item"]![0]!["item"]![0]!["request"]!["url"] = 42;

        Assert.NotEmpty(Schema.Value.Validate(node.ToJsonString()));
    }

    // ---- schema validation of generated output ----

    [Theory]
    [MemberData(nameof(AllCollections))]
    public void Generated_collection_conforms_to_the_official_postman_v21_schema(string name)
    {
        var errors = Schema.Value.Validate(Build(name));

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Path}: {e.Kind}")));
    }

    [Theory]
    [MemberData(nameof(AllCollections))]
    public void Generated_collection_satisfies_the_import_readiness_rules(string name) => AssertImportReady(Build(name));

    // ---- committed local collections ----

    [Theory]
    [InlineData("sample-api.postman_collection.json", "sample")]
    [InlineData("secure-api.postman_collection.json", "secure")]
    public void Committed_collection_matches_what_the_generator_produces(string file, string name)
    {
        var committed = File.ReadAllText(Golden(file)).Replace("\r\n", "\n");

        Assert.True(
            committed == Build(name),
            $"fixtures/postman/{file} is stale. Regenerate it with the commands in docs/tasks/T16-postman-import-report.md.");
        Assert.Empty(Schema.Value.Validate(committed));
    }

    // ---- controller folder structure and field content ----

    [Fact]
    public void Sample_api_has_one_folder_per_controller_with_every_endpoint_and_correct_http_info()
    {
        var root = JsonNode.Parse(SampleCollection())!;
        var folders = root["item"]!.AsArray();

        Assert.Equal(["Customers", "Orders", "Products"], folders.Select(f => f!["name"]!.GetValue<string>()));
        foreach (var folder in folders)
        {
            var name = folder!["name"]!.GetValue<string>();
            var requests = folder["item"]!.AsArray().ToDictionary(i => i!["name"]!.GetValue<string>(), i => i!["request"]!);

            Assert.Equal(["Create", "Delete", "GetAll", "GetById", "Update"], requests.Keys.Order());
            Assert.Equal(("GET", $"{{{{baseUrl}}}}/api/{name}"), Http(requests["GetAll"]));
            Assert.Equal(("POST", $"{{{{baseUrl}}}}/api/{name}"), Http(requests["Create"]));
            Assert.Equal(("GET", $"{{{{baseUrl}}}}/api/{name}/:id"), Http(requests["GetById"]));
            Assert.Equal(("PUT", $"{{{{baseUrl}}}}/api/{name}/:id"), Http(requests["Update"]));
            Assert.Equal(("DELETE", $"{{{{baseUrl}}}}/api/{name}/:id"), Http(requests["Delete"]));
        }

        var create = folders[2]!["item"]!.AsArray().Single(i => i!["name"]!.GetValue<string>() == "Create")!["request"]!;
        Assert.Equal("""{"name":"Laptop","price":1299.9}""", JsonNode.Parse(create["body"]!["raw"]!.GetValue<string>())!.ToJsonString());
        Assert.All(folders.SelectMany(f => f!["item"]!.AsArray()), item => Assert.Null(item!["request"]!["auth"]));
    }

    [Fact]
    public void Secure_fixture_shows_bearer_auth_only_on_protected_endpoints_and_complete_url_header_and_body_fields()
    {
        var root = JsonNode.Parse(SecureCollection())!;
        var requests = root["item"]![0]!["item"]!.AsArray().ToDictionary(i => i!["name"]!.GetValue<string>(), i => i!["request"]!);

        Assert.Equal("Accounts", root["item"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(["Create", "Delete", "Get", "List", "Ping"], requests.Keys.Order());

        foreach (var protectedName in new[] { "List", "Get", "Create", "Delete" })
        {
            var auth = requests[protectedName]["auth"]!;
            Assert.Equal("bearer", auth["type"]!.GetValue<string>());
            var token = Assert.Single(auth["bearer"]!.AsArray())!;
            Assert.Equal(("token", "{{token}}", "string"), (token["key"]!.GetValue<string>(), token["value"]!.GetValue<string>(), token["type"]!.GetValue<string>()));
        }

        Assert.Null(requests["Ping"]["auth"]);
        Assert.Contains("roles: Admin", requests["Delete"]["description"]!.GetValue<string>());

        var get = requests["Get"];
        Assert.Equal(("GET", "{{baseUrl}}/api/Accounts/:id"), Http(get));
        Assert.Equal("id", Assert.Single(get["url"]!["variable"]!.AsArray())!["key"]!.GetValue<string>());
        var header = Assert.Single(get["header"]!.AsArray())!;
        Assert.Equal(("X-Correlation-Id", true), (header["key"]!.GetValue<string>(), header["disabled"]!.GetValue<bool>()));

        var list = requests["List"]["url"]!["query"]!.AsArray();
        Assert.Equal(["page", "search"], list.Select(q => q!["key"]!.GetValue<string>()));
        Assert.All(list, q => Assert.True(q!["disabled"]!.GetValue<bool>()));
        Assert.Equal("{{baseUrl}}/api/Accounts", requests["List"]["url"]!["raw"]!.GetValue<string>());

        var create = requests["Create"];
        Assert.Equal("""{"displayName":"Ada Example","email":"ada@example.com","address":{"city":"Berlin"}}""",
            JsonNode.Parse(create["body"]!["raw"]!.GetValue<string>())!.ToJsonString());
        Assert.Equal("json", create["body"]!["options"]!["raw"]!["language"]!.GetValue<string>());
        Assert.Contains(create["header"]!.AsArray(), h => h!["key"]!.GetValue<string>() == "Content-Type" && h["value"]!.GetValue<string>() == "application/json");
        Assert.Contains("Fields without an example (omitted): `Address.Street`.", create["description"]!.GetValue<string>());
    }

    [Fact]
    public void Edge_cases_overloads_catch_all_and_collection_bodies_produce_a_valid_structure()
    {
        var root = JsonNode.Parse(EdgeCollection())!;
        var requests = root["item"]![0]!["item"]!.AsArray().ToDictionary(i => i!["name"]!.GetValue<string>(), i => i!["request"]!);

        Assert.Contains("Get [GET /api/Edge]", requests.Keys);
        Assert.Contains("Get [GET /api/Edge/all]", requests.Keys);
        Assert.Equal("{{baseUrl}}/api/Edge/files/:rest", requests["Files"]["url"]!["raw"]!.GetValue<string>());

        var bulk = JsonNode.Parse(requests["Bulk"]["body"]!["raw"]!.GetValue<string>())!.AsArray();
        Assert.Equal("<string>", bulk[0]!["name"]!.GetValue<string>());
        Assert.Equal("<Tag>".Length > 0 ? "<string>" : "", bulk[0]!["tag"]!["label"]!.GetValue<string>());
    }

    // ---- helpers ----

    private static (string Method, string Raw) Http(JsonNode request) =>
        (request["method"]!.GetValue<string>(), request["url"]!["raw"]!.GetValue<string>());

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private static void AssertImportReady(string json)
    {
        var root = JsonNode.Parse(json)!;
        var variables = root["variable"]!.AsArray().ToDictionary(v => v!["key"]!.GetValue<string>(), v => v!["value"]!.GetValue<string>());

        Assert.Equal(PostmanInfoSchema, root["info"]!["schema"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(root["info"]!["name"]!.GetValue<string>()));
        Assert.True(variables.ContainsKey("baseUrl"));
        Assert.Equal("", variables["token"]);

        foreach (var folder in root["item"]!.AsArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(folder!["name"]!.GetValue<string>()));
            var items = folder["item"]!.AsArray();
            Assert.NotEmpty(items);

            var names = items.Select(i => i!["name"]!.GetValue<string>()).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());

            foreach (var item in items)
            {
                Assert.Null(item!["item"]);
                Assert.Empty(item["response"]!.AsArray());
                AssertRequest(item["request"]!);
            }
        }

        foreach (Match reference in VariableReference().Matches(json))
        {
            Assert.True(variables.ContainsKey(reference.Groups[1].Value), $"undefined variable {{{{{reference.Groups[1].Value}}}}}");
        }
    }

    private const string PostmanInfoSchema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    private static void AssertRequest(JsonNode request)
    {
        Assert.Contains(request["method"]!.GetValue<string>(), Methods);

        var url = request["url"]!;
        var host = url["host"]!.AsArray().Single()!.GetValue<string>();
        var path = url["path"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        var enabledQuery = (url["query"]?.AsArray() ?? []).Where(q => q!["disabled"] is null).Select(q => q!["key"]!.GetValue<string>() + "=").ToList();
        var expectedRaw = host + (path.Count == 0 ? "" : "/" + string.Join('/', path)) + (enabledQuery.Count == 0 ? "" : "?" + string.Join('&', enabledQuery));
        Assert.Equal(expectedRaw, url["raw"]!.GetValue<string>());

        var pathVariables = path.Where(p => p.StartsWith(':')).Select(p => p[1..]).Order().ToList();
        var declared = (url["variable"]?.AsArray() ?? []).Select(v => v!["key"]!.GetValue<string>()).Order().ToList();
        Assert.Equal(pathVariables, declared);

        if (request["body"] is { } body)
        {
            Assert.Equal("raw", body["mode"]!.GetValue<string>());
            Assert.Equal("json", body["options"]!["raw"]!["language"]!.GetValue<string>());
            using var parsed = System.Text.Json.JsonDocument.Parse(body["raw"]!.GetValue<string>());
            Assert.Contains(request["header"]!.AsArray(), h => h!["key"]!.GetValue<string>() == "Content-Type");
        }

        if (request["auth"] is { } auth)
        {
            Assert.Equal("bearer", auth["type"]!.GetValue<string>());
            var token = Assert.Single(auth["bearer"]!.AsArray())!;
            Assert.Equal("{{token}}", token["value"]!.GetValue<string>());
        }
    }

    [GeneratedRegex(@"\{\{([^}]+)\}\}")]
    private static partial Regex VariableReference();
}
