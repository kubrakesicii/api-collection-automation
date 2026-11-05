using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;
using ApiCollection.Core.Snapshots;

namespace ApiCollection.Analysis.Tests;

/// <summary>
/// Acceptance: analysing the same API again must not make endpoints look new, whatever happens to formatting,
/// file layout or ordering; and each real change must be classified the way docs/adr/0001-endpoint-identity.md says.
/// </summary>
public sealed class EndpointIdentityStabilityTests
{
    private static readonly EndpointDiscoveryEngine Engine = new();

    private const string Controller = """
        [ApiController]
        [Route("api/[controller]")]
        public class ProductsController : ControllerBase
        {
            [HttpGet] public IActionResult GetAll() => Ok();
            [HttpGet("{id:int}")] public IActionResult GetById(int id) => Ok();
            [HttpPost] public IActionResult Create(CreateProduct request) => Ok();
            [HttpPut("{id:int}")] public IActionResult Update(int id, CreateProduct request) => Ok();
            [HttpDelete("{id:int}")] public IActionResult Delete(int id) => NoContent();
        }
        """;

    private const string Dto = """
        public class CreateProduct
        {
            /// <example>Laptop</example>
            public string Name { get; set; } = "";
            public decimal Price { get; set; }
        }
        """;

    private static AnalysisSnapshot Snap(params string[] sources) => AnalysisSnapshot.Create(Engine.DiscoverFromSources(sources).Endpoints);

    private static SnapshotComparison Compare(string[] before, string[] after) => SnapshotComparer.Compare(Snap(before), Snap(after));

    private static void AssertNothingNew(SnapshotComparison comparison, int unchanged)
    {
        Assert.Equal(unchanged, comparison.Unchanged.Count);
        Assert.Empty(comparison.Added);
        Assert.Empty(comparison.Removed);
        Assert.Empty(comparison.Modified);
        Assert.Empty(comparison.RenameCandidates);
    }

    // ---- the same API analysed again ----

    [Fact]
    public void Analysing_the_same_source_again_produces_an_identical_snapshot()
    {
        var first = Snap(Controller, Dto);
        var second = Snap(Controller, Dto);

        Assert.Equal(first.Endpoints, second.Endpoints);
        AssertNothingNew(SnapshotComparer.Compare(first, second), unchanged: 5);
    }

    [Fact]
    public void Formatting_comments_line_endings_file_order_and_method_order_do_not_matter()
    {
        var reordered = """
            [ApiController]
            [Route("api/[controller]")]
            public class ProductsController : ControllerBase
            {
                // Removing items.
                [HttpDelete("{id:int}")] public IActionResult Delete(int id) => NoContent();

                [HttpPut("{id:int}")]
                public IActionResult Update(int id, CreateProduct request) => Ok();

                [HttpPost] public IActionResult Create(CreateProduct request) => Ok();
                [HttpGet("{id:int}")] public IActionResult GetById(int id) => Ok();
                [HttpGet] public IActionResult GetAll() => Ok();
            }
            """;
        var shiftedDto = "// header comment\r\n\r\n\r\n" + Dto.Replace("\n", "\r\n");

        AssertNothingNew(Compare([Controller, Dto], [shiftedDto, reordered.Replace("\n", "\r\n")]), unchanged: 5);
    }

    [Fact]
    public void Moving_code_to_other_files_and_namespaces_does_not_matter()
    {
        var moved = $"namespace Shop.Catalog.Web;\n\n{Controller}";
        var dtoElsewhere = $"namespace Shop.Contracts\n{{\n{Dto}\n}}";

        AssertNothingNew(Compare([Controller, Dto], [dtoElsewhere, moved]), unchanged: 5);
    }

    [Fact]
    public void The_sample_api_analysed_from_a_reformatted_copy_elsewhere_has_no_new_endpoints()
    {
        var original = Engine.DiscoverFromDirectory(SampleApiDirectory());

        var copy = Path.Combine(Path.GetTempPath(), "ApiCollection.Identity." + Guid.NewGuid().ToString("N"), "RenamedFolder");
        try
        {
            foreach (var file in Directory.EnumerateFiles(SampleApiDirectory(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(SampleApiDirectory(), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var text = File.ReadAllText(file).Replace("\r\n", "\n");
                File.WriteAllText(target, "// copy\r\n\r\n" + text.Replace("\n", "\r\n") + "\r\n\r\n");
            }

            var again = Engine.DiscoverFromDirectory(copy);
            var comparison = SnapshotComparer.Compare(
                AnalysisSnapshot.Create(original.Endpoints), AnalysisSnapshot.Create(again.Endpoints));

            AssertNothingNew(comparison, unchanged: 15);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(copy)!, recursive: true);
        }
    }

    private static string SampleApiDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "sample-api", "SampleApi");
    }

    // ---- refactors that keep the identity ----

    [Fact]
    public void Renaming_the_controller_class_with_an_explicit_route_keeps_every_endpoint()
    {
        var before = Controller.Replace("""[Route("api/[controller]")]""", """[Route("api/products")]""");
        var after = before.Replace("ProductsController", "CatalogController");

        var comparison = Compare([before, Dto], [after, Dto]);

        Assert.Equal(5, comparison.Modified.Count);
        Assert.All(comparison.Modified, change => Assert.Equal("Catalog", change.Current.ControllerName));
        Assert.Empty(comparison.Added);
        Assert.Empty(comparison.Removed);
    }

    [Fact]
    public void Renaming_an_action_keeps_the_endpoint_and_only_that_one_is_modified()
    {
        var comparison = Compare([Controller, Dto], [Controller.Replace("GetById", "Fetch"), Dto]);

        var change = Assert.Single(comparison.Modified);
        Assert.Equal(("GetById", "Fetch"), (change.Previous.ActionName, change.Current.ActionName));
        Assert.Equal(4, comparison.Unchanged.Count);
        Assert.Empty(comparison.Added);
        Assert.Empty(comparison.Removed);
    }

    [Fact]
    public void Renaming_a_route_parameter_or_changing_route_case_keeps_the_identity()
    {
        var parameterRenamed = Controller.Replace("{id:int}", "{productId:int}").Replace("int id", "int productId");
        var parameters = Compare([Controller, Dto], [parameterRenamed, Dto]);
        Assert.Equal(3, parameters.Modified.Count);
        Assert.Equal(2, parameters.Unchanged.Count);
        Assert.Empty(parameters.Added);
        Assert.Empty(parameters.Removed);

        var before = Controller.Replace("""[Route("api/[controller]")]""", """[Route("api/products")]""");
        var recased = Controller.Replace("""[Route("api/[controller]")]""", """[Route("API/Products/")]""");
        var casing = Compare([before, Dto], [recased, Dto]);
        Assert.Equal(5, casing.Modified.Count);
        Assert.Empty(casing.Added);
        Assert.Empty(casing.Removed);
    }

    [Fact]
    public void Adding_an_action_adds_exactly_one_endpoint()
    {
        var extended = Controller.Replace(
            "[HttpGet] public IActionResult GetAll() => Ok();",
            "[HttpGet] public IActionResult GetAll() => Ok();\n    [HttpGet(\"count\")] public IActionResult Count() => Ok();");

        var comparison = Compare([Controller, Dto], [extended, Dto]);

        Assert.Equal(["GET /api/products/count"], comparison.Added.Select(e => e.Id));
        Assert.Equal(5, comparison.Unchanged.Count);
        Assert.Empty(comparison.Removed);
        Assert.Empty(comparison.Modified);
    }

    // ---- changes that do change the identity ----

    [Fact]
    public void Changing_a_route_is_a_different_endpoint_with_a_rename_candidate()
    {
        var moved = Controller.Replace("""[HttpGet("{id:int}")]""", """[HttpGet("v2/{id:int}")]""");

        var comparison = Compare([Controller, Dto], [moved, Dto]);

        Assert.Equal(["GET /api/products/v2/{:int}"], comparison.Added.Select(e => e.Id));
        Assert.Equal(["GET /api/products/{:int}"], comparison.Removed.Select(e => e.Id));
        var candidate = Assert.Single(comparison.RenameCandidates);
        Assert.Equal("GetById", candidate.Current.ActionName);
        Assert.Equal(4, comparison.Unchanged.Count);
    }

    [Fact]
    public void Changing_the_http_method_is_a_different_endpoint_with_a_rename_candidate()
    {
        var changed = Controller.Replace("""[HttpDelete("{id:int}")]""", """[HttpPatch("{id:int}")]""");

        var comparison = Compare([Controller, Dto], [changed, Dto]);

        Assert.Equal(["PATCH /api/products/{:int}"], comparison.Added.Select(e => e.Id));
        Assert.Equal(["DELETE /api/products/{:int}"], comparison.Removed.Select(e => e.Id));
        Assert.Equal("Delete", Assert.Single(comparison.RenameCandidates).Current.ActionName);
    }

    [Fact]
    public void Renaming_a_controller_whose_route_comes_from_the_token_changes_the_urls_so_it_is_not_matched()
    {
        var comparison = Compare([Controller, Dto], [Controller.Replace("ProductsController", "ItemsController"), Dto]);

        Assert.Equal(5, comparison.Added.Count);
        Assert.Equal(5, comparison.Removed.Count);
        Assert.Empty(comparison.RenameCandidates);
        Assert.Empty(comparison.Unchanged);
    }

    // ---- uniqueness and metadata keys ----

    [Fact]
    public void Two_actions_with_the_same_identity_are_reported_and_cannot_be_snapshotted_silently()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/x")]
            public class XController : ControllerBase
            {
                [HttpGet("{id:int}")] public object ById(int id) => 1;
                [HttpGet("{count:int}")] public object ByCount(int count) => 1;
            }
            """]);

        Assert.Contains(result.Warnings, w => w.Contains("'GET /api/x/{:int}' is produced by more than one action") && w.Contains("X.ById") && w.Contains("X.ByCount"));
        Assert.Throws<ArgumentException>(() => AnalysisSnapshot.Create(result.Endpoints));
    }

    [Theory]
    [InlineData("POST /api/products")]
    [InlineData("post api/Products")]
    [InlineData("  POST   /API/PRODUCTS/ ")]
    public void Example_metadata_endpoint_keys_match_in_any_readable_spelling(string key)
    {
        var metadata = new ExampleMetadata
        {
            SourceName = "examples.json",
            RequestExamples = new Dictionary<string, string> { [key] = """{ "name": "Phone", "price": 5 }""" }
        };

        var result = Engine.DiscoverFromSources([Controller, Dto], metadata);

        var create = Assert.Single(result.Endpoints, e => e.ActionName == "Create");
        Assert.Equal(ExampleSource.ExternalMetadata, Assert.Single(create.RequestBody!.Example!.Fields).Source);
        Assert.Contains(key, Assert.Single(create.RequestBody.Example.Fields).SourceDetail);
        Assert.DoesNotContain(result.ExampleDiagnostics, d => d.Code == "EX005");
    }

    [Fact]
    public void Canonical_ids_copied_from_a_report_work_as_metadata_keys_and_unknown_keys_are_still_reported()
    {
        var discovery = Engine.DiscoverFromSources([Controller, Dto]);
        var getById = discovery.Endpoints.Single(e => e.ActionName == "GetById");
        var metadata = new ExampleMetadata
        {
            ResponseExamples = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                [getById.Id] = new Dictionary<int, string> { [200] = """{ "id": 1 }""" },
                ["GET /api/nothing"] = new Dictionary<int, string> { [200] = "{}" }
            }
        };

        var result = Engine.DiscoverFromSources([Controller, Dto], metadata);

        Assert.Single(result.Endpoints.Single(e => e.ActionName == "GetById").Responses);
        Assert.Contains(result.ExampleDiagnostics, d => d.Code == "EX005" && d.Endpoint == "GET /api/nothing");
        Assert.DoesNotContain(result.ExampleDiagnostics, d => d.Code == "EX005" && d.Endpoint == getById.Id);
    }
}
