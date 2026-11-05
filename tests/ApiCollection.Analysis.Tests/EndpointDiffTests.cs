using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Diff;
using ApiCollection.Core.Snapshots;

namespace ApiCollection.Analysis.Tests;

/// <summary>Acceptance for the diff engine: the real discovery engine on a baseline and a changed API, scenario by scenario.</summary>
public sealed class EndpointDiffTests
{
    private static readonly EndpointDiscoveryEngine Engine = new();

    private const string Controller = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpGet("{id:int}")] public IActionResult Get(int id) => Ok();
            [HttpPost] public IActionResult Create(CreateItem request) => Ok();
            [HttpPut("{id:int}")] public IActionResult Update(int id, CreateItem request) => Ok();
            [HttpDelete("{id:int}")] public IActionResult Delete(int id) => NoContent();
        }
        """;

    private const string Dto = """
        public class CreateItem
        {
            /// <example>Laptop</example>
            public string Name { get; set; } = "";
            public decimal Price { get; set; }
        }
        """;

    private static ChangeSet Diff(string[] before, string[] after)
    {
        var baseline = AnalysisSnapshot.Create(Engine.DiscoverFromSources(before).Endpoints);
        var current = AnalysisSnapshot.Create(Engine.DiscoverFromSources(after).Endpoints);
        return ChangeSetBuilder.Build(baseline, current);
    }

    private static EndpointDiff Single(ChangeSet changes, string action) => Assert.Single(changes.Endpoints, e => e.ActionName == action);

    // ---- no change ----

    [Fact]
    public void An_unchanged_api_is_reported_as_no_changes()
    {
        var changes = Diff([Controller, Dto], [Controller, Dto]);

        Assert.Equal(new ChangeSetSummary(0, 0, 0, 5), changes.Summary);
        Assert.False(changes.HasChanges);
        Assert.All(changes.Endpoints, e => Assert.Equal(ChangeStatus.Unchanged, e.Status));
    }

    // ---- addition ----

    [Fact]
    public void A_new_action_is_added_and_nothing_else_changes()
    {
        var extended = Controller.Replace("[HttpGet] public IActionResult List() => Ok();", "[HttpGet] public IActionResult List() => Ok();\n    [HttpGet(\"count\")] public IActionResult Count() => Ok();");

        var changes = Diff([Controller, Dto], [extended, Dto]);

        Assert.Equal(new ChangeSetSummary(1, 0, 0, 5), changes.Summary);
        var added = Single(changes, "Count");
        Assert.Equal(("GET /api/items/count", ChangeStatus.Added), (added.Id, added.Status));
        Assert.True(changes.HasChanges);
    }

    // ---- removal ----

    [Fact]
    public void A_deleted_action_is_removed_and_keeps_its_names()
    {
        var shrunk = Controller.Replace("    [HttpDelete(\"{id:int}\")] public IActionResult Delete(int id) => NoContent();\n", string.Empty);

        var changes = Diff([Controller, Dto], [shrunk, Dto]);

        Assert.Equal(new ChangeSetSummary(0, 0, 1, 4), changes.Summary);
        var removed = Single(changes, "Delete");
        Assert.Equal((ChangeStatus.Removed, "DELETE /api/items/{:int}", "api/Items/{id:int}"), (removed.Status, removed.Id, removed.Route));
    }

    // ---- modification, per kind of contract change ----

    [Fact]
    public void A_new_query_parameter_modifies_only_that_endpoint()
    {
        var changed = Controller.Replace("Get(int id)", "Get(int id, string? expand)");

        var changes = Diff([Controller, Dto], [changed, Dto]);

        Assert.Equal(new ChangeSetSummary(0, 1, 0, 4), changes.Summary);
        var change = Assert.Single(Single(changes, "Get").Changes);
        Assert.Equal((ChangeCategory.Parameter, ChangeKind.Added, "parameter[query:expand]"), (change.Category, change.Kind, change.Path));
        Assert.Equal("required=false; type=string?", change.After);
    }

    [Fact]
    public void Changing_a_dto_property_modifies_every_endpoint_that_uses_that_body()
    {
        var changed = Dto.Replace("public decimal Price", "public int Price");

        var changes = Diff([Controller, Dto], [Controller, changed]);

        Assert.Equal(new ChangeSetSummary(0, 2, 0, 3), changes.Summary);
        foreach (var action in new[] { "Create", "Update" })
        {
            var change = Assert.Single(Single(changes, action).Changes);
            Assert.Equal(
                (ChangeCategory.RequestBody, ChangeKind.Changed, "body.property[Price].type", "decimal", "int"),
                (change.Category, change.Kind, change.Path, change.Before, change.After));
        }
    }

    [Fact]
    public void A_new_dto_property_is_reported_as_an_added_body_property()
    {
        var changed = Dto.Replace("public decimal Price { get; set; }", "public decimal Price { get; set; }\n    public string? Note { get; set; }");

        var changes = Diff([Controller, Dto], [Controller, changed]);

        var create = Single(changes, "Create").Changes;
        Assert.Contains(create, c => c is { Category: ChangeCategory.RequestBody, Kind: ChangeKind.Added, Path: "body.property[Note]" });
        // The new property has no example, so the list of fields without one (shown in the collection description) grows too.
        Assert.Contains(create, c => c is { Category: ChangeCategory.Example, Path: "body.exampleMissing", Before: "Price", After: "Price,Note" });
        Assert.Equal(2, create.Count);
    }

    [Fact]
    public void Adding_authorization_is_an_authentication_change()
    {
        var secured = Controller.Replace("[HttpDelete(\"{id:int}\")]", "[Authorize(Roles = \"Admin\")] [HttpDelete(\"{id:int}\")]");

        var changes = Diff([Controller, Dto], [secured, Dto]);

        var delete = Single(changes, "Delete");
        Assert.Equal(ChangeStatus.Modified, delete.Status);
        Assert.Contains(delete.Changes, c => c is { Category: ChangeCategory.Authentication, Kind: ChangeKind.Changed, Path: "auth.required", Before: "false", After: "true" });
        Assert.Contains(delete.Changes, c => c is { Category: ChangeCategory.Authentication, Kind: ChangeKind.Added, Path: "auth.roles", After: "Admin" });
        Assert.Equal(1, changes.Summary.Modified);
    }

    [Fact]
    public void Changing_an_example_value_is_an_example_change()
    {
        var changed = Dto.Replace("<example>Laptop</example>", "<example>Phone</example>");

        var changes = Diff([Controller, Dto], [Controller, changed]);

        var create = Single(changes, "Create");
        Assert.All(create.Changes, c => Assert.Equal(ChangeCategory.Example, c.Category));
        Assert.Contains(create.Changes, c => c is { Path: "body.property[Name].example", Before: "\"Laptop\"", After: "\"Phone\"" });
        Assert.Contains(create.Changes, c => c.Path == "body.example");
    }

    [Fact]
    public void Renaming_a_controller_or_action_is_a_naming_change_not_an_added_or_removed_endpoint()
    {
        var explicitRoute = Controller.Replace("""[Route("api/[controller]")]""", """[Route("api/items")]""");
        var renamed = explicitRoute.Replace("ItemsController", "CatalogController").Replace("Get(int id)", "Fetch(int id)");

        var changes = Diff([explicitRoute, Dto], [renamed, Dto]);

        Assert.Equal(new ChangeSetSummary(0, 5, 0, 0), changes.Summary);
        Assert.All(changes.Endpoints, e => Assert.Contains(e.Changes, c => c is { Category: ChangeCategory.Naming, Path: "endpoint.controller", Before: "Items", After: "Catalog" }));
        Assert.Contains(Single(changes, "Fetch").Changes, c => c is { Path: "endpoint.action", Before: "Get", After: "Fetch" });
    }

    // ---- everything at once ----

    [Fact]
    public void Addition_modification_removal_and_no_change_in_one_run()
    {
        var evolved = Controller
            .Replace("[HttpGet] public IActionResult List() => Ok();", "[HttpGet] public IActionResult List() => Ok();\n    [HttpGet(\"count\")] public IActionResult Count() => Ok();")
            .Replace("Get(int id)", "Get(int id, string? expand)")
            .Replace("    [HttpDelete(\"{id:int}\")] public IActionResult Delete(int id) => NoContent();\n", string.Empty);

        var changes = Diff([Controller, Dto], [evolved, Dto]);

        Assert.Equal(
            new Dictionary<string, ChangeStatus>
            {
                ["Count"] = ChangeStatus.Added, ["Get"] = ChangeStatus.Modified, ["Delete"] = ChangeStatus.Removed,
                ["List"] = ChangeStatus.Unchanged, ["Create"] = ChangeStatus.Unchanged, ["Update"] = ChangeStatus.Unchanged
            },
            changes.Endpoints.ToDictionary(e => e.ActionName, e => e.Status));
        Assert.Equal(new ChangeSetSummary(1, 1, 1, 3), changes.Summary);
        Assert.Equal(changes.Endpoints.Select(e => e.Id).Order(StringComparer.Ordinal), changes.Endpoints.Select(e => e.Id));
    }

    [Fact]
    public void Reformatting_and_moving_code_is_no_change_even_with_examples_whose_source_lines_shifted()
    {
        var shifted = "// a new comment\r\n\r\n\r\n" + Dto.Replace("\n", "\r\n");

        var changes = Diff([Controller, Dto], [shifted, "namespace Elsewhere;\n" + Controller]);

        Assert.False(changes.HasChanges);
        Assert.Equal(5, changes.Summary.Unchanged);
    }
}
