using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;

namespace ApiCollection.Analysis.Tests;

public sealed class ExampleExtractionTests
{
    private static readonly EndpointDiscoveryEngine Engine = new();

    private static EndpointDefinition Single(EndpointDiscoveryResult result, string action) =>
        Assert.Single(result.Endpoints, e => e.ActionName == action);

    private static IEnumerable<string> Codes(EndpointDiscoveryResult result) => result.ExampleDiagnostics.Select(d => d.Code);

    private const string ItemController = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpPost] public IActionResult Create(CreateItem request) => NoContent();
        }
        """;

    // ---- request examples from source ----

    [Fact]
    public void Static_sources_produce_a_request_example_with_traceable_provenance()
    {
        var result = Engine.DiscoverFromSources([ItemController, """
            public class CreateItem
            {
                /// <summary>The item name.</summary>
                /// <example>Laptop</example>
                public string Name { get; set; } = "";
                [DefaultValue(3)] public int Quantity { get; set; }
                public string Currency { get; set; } = "TRY";
                [JsonPropertyName("SKU")] [DefaultValue("A-1")] public string Sku { get; set; } = "";
                public bool Active { get; set; } = true;
                public string Note { get; set; } = "";
            }
            """]);

        var example = Single(result, "Create").RequestBody!.Example!;

        Assert.Equal("""{"name":"Laptop","quantity":3,"currency":"TRY","SKU":"A-1","active":true}""", Compact(example.Json));
        Assert.False(example.IsComplete);
        Assert.Equal(["Note"], example.MissingFields);
        Assert.Equal(
            [
                ("Name", ExampleSource.XmlDocExample), ("Quantity", ExampleSource.DefaultValueAttribute),
                ("Currency", ExampleSource.PropertyInitializer), ("Sku", ExampleSource.DefaultValueAttribute),
                ("Active", ExampleSource.PropertyInitializer)
            ],
            example.Fields.Select(f => (f.Path, f.Source)));
        Assert.All(example.Fields, f => Assert.Contains("CreateItem.", f.SourceDetail));
        Assert.All(example.Fields, f => Assert.StartsWith("line ", f.SourceDetail));
        Assert.Contains(result.ExampleDiagnostics, d => d is { Code: "EX002", Target: "Note" });
    }

    [Fact]
    public void Nested_dtos_collections_and_constructor_defaults_are_composed()
    {
        var result = Engine.DiscoverFromSources([ItemController.Replace("CreateItem", "Order"), """
            public record Order(string Customer, Address Ship, List<Line> Lines, int Priority = 5);
            public class Address
            {
                /// <example>Berlin</example>
                public string City { get; set; } = "";
                public string Zip { get; set; } = "";
            }
            public class Line { [DefaultValue(2)] public int Qty { get; set; } }
            """]);

        var example = Single(result, "Create").RequestBody!.Example!;

        Assert.Equal("""{"ship":{"city":"Berlin"},"lines":[{"qty":2}],"priority":5}""", Compact(example.Json));
        Assert.Equal(["Customer", "Ship.Zip"], example.MissingFields);
        Assert.Equal(
            [("Ship.City", ExampleSource.XmlDocExample), ("Lines.Qty", ExampleSource.DefaultValueAttribute), ("Priority", ExampleSource.ConstructorDefault)],
            example.Fields.Select(f => (f.Path, f.Source)));
    }

    [Fact]
    public void Nothing_is_invented_when_the_source_has_no_examples()
    {
        var result = Engine.DiscoverFromSources([ItemController, """
            public class CreateItem { public string Name { get; set; } = ""; public int Quantity { get; set; } }
            """]);

        Assert.Null(Single(result, "Create").RequestBody!.Example);
        Assert.Equal(["EX002", "EX002", "EX001"], Codes(result));
    }

    // ---- contract validation ----

    [Fact]
    public void Examples_that_violate_the_dto_contract_are_rejected_and_reported()
    {
        var result = Engine.DiscoverFromSources([ItemController, """
            public class CreateItem
            {
                /// <example>abc</example>
                public int Count { get; set; }
                [DefaultValue("x")] public int Other { get; set; }
                [DefaultValue(300)] public byte Small { get; set; }
                [DefaultValue(1)] public Guid Id { get; set; }
            }
            """]);

        Assert.Null(Single(result, "Create").RequestBody!.Example);
        var rejected = result.ExampleDiagnostics.Where(d => d.Code == "EX003").ToList();
        Assert.Contains(rejected, d => d.Target == "CreateItem.Count" && d.Message.Contains("cannot be converted"));
        Assert.Contains(rejected, d => d.Target == "CreateItem.Other" && d.Message.Contains("expected an integer"));
        Assert.Contains(rejected, d => d.Target == "CreateItem.Small" && d.Message.Contains("range of 'byte'"));
        Assert.Contains(rejected, d => d.Target == "CreateItem.Id" && d.Message.Contains("expected a string"));
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX001");
    }

    // ---- external example metadata ----

    private static ExampleMetadata Metadata(
        Dictionary<string, string>? properties = null, Dictionary<string, string>? requests = null,
        Dictionary<string, IReadOnlyDictionary<int, string>>? responses = null) => new()
        {
            SourceName = "meta.json",
            PropertyExamples = properties ?? [],
            RequestExamples = requests ?? [],
            ResponseExamples = responses ?? []
        };

    private const string DocumentedItem = """
        public class CreateItem
        {
            /// <example>Laptop</example>
            public string Name { get; set; } = "";
            [DefaultValue(3)] public int Quantity { get; set; }
        }
        """;

    [Fact]
    public void Property_metadata_overrides_code_examples_and_invalid_entries_fall_back()
    {
        var metadata = Metadata(properties: new()
        {
            ["CreateItem.Name"] = "\"Meta Name\"",
            ["CreateItem.Quantity"] = "\"many\"",
            ["Nope.X"] = "1",
            ["CreateItem.Ghost"] = "1",
            ["Malformed"] = "1"
        });

        var result = Engine.DiscoverFromSources([ItemController, DocumentedItem], metadata);
        var example = Single(result, "Create").RequestBody!.Example!;

        Assert.Equal("""{"name":"Meta Name","quantity":3}""", Compact(example.Json));
        Assert.True(example.IsComplete);
        Assert.Equal(ExampleSource.ExternalMetadata, example.Fields[0].Source);
        Assert.Equal("meta.json: properties[CreateItem.Name]", example.Fields[0].SourceDetail);
        Assert.Equal(ExampleSource.DefaultValueAttribute, example.Fields[1].Source);

        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX003" && d.Message.Contains("meta.json: properties[CreateItem.Quantity] was rejected"));
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX005" && d.Target == "Nope.X" && d.Message.Contains("type 'Nope' was not found"));
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX005" && d.Target == "CreateItem.Ghost" && d.Message.Contains("no property 'Ghost'"));
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX005" && d.Target == "Malformed");
    }

    [Fact]
    public void Endpoint_level_request_metadata_is_validated_against_the_dto()
    {
        var valid = Metadata(requests: new() { ["POST api/Items"] = """{ "name": "Whole", "quantity": 7 }""" });
        var ok = Single(Engine.DiscoverFromSources([ItemController, DocumentedItem], valid), "Create").RequestBody!.Example!;

        Assert.Equal("""{"name":"Whole","quantity":7}""", Compact(ok.Json));
        Assert.True(ok.IsComplete);
        Assert.Equal([("$", ExampleSource.ExternalMetadata)], ok.Fields.Select(f => (f.Path, f.Source)));

        var invalid = Metadata(requests: new() { ["POST api/Items"] = """{ "name": 5, "extra": true }""", ["GET api/Ghost"] = "{}" });
        var result = Engine.DiscoverFromSources([ItemController, DocumentedItem], invalid);
        var fallback = Single(result, "Create").RequestBody!.Example!;

        Assert.Equal("""{"name":"Laptop","quantity":3}""", Compact(fallback.Json));
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX003" && d.Message.Contains("$.name: expected a string"));
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX003" && d.Message.Contains("$.extra: unknown property"));
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX005" && d.Target == "requests" && d.Endpoint == "GET api/Ghost");
    }

    [Fact]
    public void Null_is_rejected_for_non_nullable_properties_but_allowed_for_nullable_ones()
    {
        var metadata = Metadata(properties: new() { ["CreateItem.Name"] = "null", ["CreateItem.Note"] = "null" });
        var result = Engine.DiscoverFromSources([ItemController, """
            public class CreateItem { public string Name { get; set; } = ""; public string? Note { get; set; } }
            """], metadata);

        var example = Single(result, "Create").RequestBody!.Example!;
        Assert.Equal("""{"note":null}""", Compact(example.Json));
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX003" && d.Message.Contains("null is not allowed"));
    }

    // ---- response examples ----

    private const string PingSource = """
        [ApiController]
        [Route("api/[controller]")]
        public class PingController : ControllerBase
        {
            [HttpGet("one")] public IActionResult One() => Ok(new Pong(1, "pong", new Inner("x")));
            [HttpGet("many")] public IActionResult Many() { return Ok(new[] { new Pong(1, "a", new Inner("i")), new Pong(2, "b", new Inner("j")) }); }
            [HttpPost] public IActionResult Create() => CreatedAtAction("One", new { id = 1 }, new Pong(3, "c", new Inner("k")));
            [HttpGet("init")] public ActionResult<Cfg> Init() => new Cfg { Name = "n", Count = 4 };
            [HttpGet("empty")] public IActionResult Empty() => Ok(Array.Empty<Pong>());
            [HttpGet("var")] public IActionResult Var(int x) { var p = Load(x); return Ok(p); }
            [HttpGet("partial")] public ActionResult<Cfg> Partial() => new Cfg { Name = "n" };
            [HttpGet("wrong")] public ActionResult<Cfg> Wrong() => new Cfg { Name = 5, Count = 1 };
            [HttpGet("cond")] public ActionResult<Cfg> Cond(bool b) => b ? new Cfg { Name = "a", Count = 1 } : NotFound();
            [HttpDelete("{id}")] public IActionResult Delete(int id) => NoContent();
        }
        public record Pong(int Id, string Message, Inner Detail);
        public record Inner(string Code);
        public class Cfg { public string Name { get; set; } = ""; public int Count { get; set; } }
        """;

    [Fact]
    public void Literal_responses_become_examples_with_status_type_and_provenance()
    {
        var result = Engine.DiscoverFromSources([PingSource]);

        var one = Assert.Single(Single(result, "One").Responses);
        Assert.Equal((200, "Pong"), (one.StatusCode, one.TypeName));
        Assert.Equal("""{"id":1,"message":"pong","detail":{"code":"x"}}""", Compact(one.Example!.Json));
        var field = Assert.Single(one.Example.Fields);
        Assert.Equal(("$", ExampleSource.ResponseLiteral), (field.Path, field.Source));
        Assert.Contains("line ", field.SourceDetail);
        Assert.Contains("PingController.One", field.SourceDetail);

        Assert.Equal(
            """[{"id":1,"message":"a","detail":{"code":"i"}},{"id":2,"message":"b","detail":{"code":"j"}}]""",
            Compact(Assert.Single(Single(result, "Many").Responses).Example!.Json));

        var created = Assert.Single(Single(result, "Create").Responses);
        Assert.Equal(201, created.StatusCode);
        Assert.Equal("""{"id":3,"message":"c","detail":{"code":"k"}}""", Compact(created.Example!.Json));

        var init = Assert.Single(Single(result, "Init").Responses);
        Assert.Equal((200, "Cfg"), (init.StatusCode, init.TypeName));
        Assert.Equal("""{"name":"n","count":4}""", Compact(init.Example!.Json));

        var empty = Assert.Single(Single(result, "Empty").Responses);
        Assert.Equal("[]", Compact(empty.Example!.Json));
        Assert.Equal("Pong[]", empty.TypeName);
    }

    [Fact]
    public void Unresolvable_responses_are_reported_never_guessed()
    {
        var result = Engine.DiscoverFromSources([PingSource]);

        foreach (var action in new[] { "Var", "Partial", "Wrong", "Cond", "Delete" })
        {
            Assert.Empty(Single(result, action).Responses);
        }

        var messages = result.ExampleDiagnostics.Where(d => d.Code == "EX004").ToDictionary(d => d.Endpoint!, d => d.Message);
        Assert.Contains("'p' is not a static literal", messages["GET api/Ping/var"]);
        Assert.Contains("Count: not assigned and has no literal default", messages["GET api/Ping/partial"]);
        Assert.Contains("Name: expected a string", messages["GET api/Ping/wrong"]);
        Assert.Contains("not a static literal", messages["GET api/Ping/cond"]);
        Assert.DoesNotContain("DELETE api/Ping/{id}", messages.Keys);
    }

    [Fact]
    public void Response_metadata_is_used_when_code_has_no_literal_and_wins_over_literals()
    {
        var metadata = Metadata(responses: new()
        {
            ["GET api/Ping/var"] = new Dictionary<int, string> { [200] = """{ "id": 9 }""", [404] = """{ "title": "Not Found" }""" },
            ["GET api/Ping/one"] = new Dictionary<int, string> { [200] = """{ "id": 100 }""" }
        });

        var result = Engine.DiscoverFromSources([PingSource], metadata);

        var variable = Single(result, "Var").Responses;
        Assert.Equal([200, 404], variable.Select(r => r.StatusCode));
        Assert.All(variable, r => Assert.Equal(ExampleSource.ExternalMetadata, Assert.Single(r.Example!.Fields).Source));
        Assert.DoesNotContain(result.ExampleDiagnostics, d => d.Endpoint == "GET api/Ping/var");

        var one = Assert.Single(Single(result, "One").Responses);
        Assert.Equal("""{"id":100}""", Compact(one.Example!.Json));
    }

    [Fact]
    public void Customized_serializer_options_are_reported()
    {
        var result = Engine.DiscoverFromSources([ItemController, DocumentedItem, "builder.Services.AddControllers().AddJsonOptions(o => { });"]);

        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX006");
    }

    private static string Compact(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!.ToJsonString();
}
