using ApiCollection.Core.Diff;
using ApiCollection.Core.Identity;
using ApiCollection.Core.Models;
using ApiCollection.Core.Snapshots;

namespace ApiCollection.Core.Tests;

public sealed class ContractAndDiffTests
{
    private static EndpointDefinition Endpoint(Func<EndpointDefinition, EndpointDefinition>? change = null) =>
        SnapshotTests.Endpoint(change: change);

    private static IReadOnlyList<ContractChange> Diff(EndpointDefinition before, EndpointDefinition after) =>
        ContractDiff.Compute(EndpointContract.From(before), EndpointContract.From(after));

    // ---- contract facts ----

    [Fact]
    public void Contract_facts_describe_the_endpoint_with_plain_sorted_keys()
    {
        var facts = EndpointContract.From(Endpoint());

        Assert.Equal(facts.Keys.OrderBy(k => k, StringComparer.Ordinal), facts.Keys);
        Assert.Equal("Products", facts["endpoint.controller"]);
        Assert.Equal("GetById", facts["endpoint.action"]);
        Assert.Equal("api/Products/{id:int}", facts["endpoint.route"]);
        Assert.Equal("int", facts["parameter[path:id].type"]);
        Assert.Equal("true", facts["parameter[path:id].required"]);
        Assert.Equal("Dto", facts["body.type"]);
        Assert.Equal("string", facts["body.property[Name].type"]);
        Assert.Equal("\"Laptop\"", facts["body.property[Name].example"]);
        Assert.Equal("{\"name\":\"Laptop\"}", facts["body.example"]);
        Assert.Equal("true", facts["auth.required"]);
        Assert.Equal("Admin", facts["auth.roles"]);
        Assert.Equal("100", facts["validation[Name:MaximumLength].arguments"]);
        Assert.Equal("Dto", facts["response[200].type"]);
    }

    [Fact]
    public void Unknown_values_are_omitted_and_never_stored_as_empty_text()
    {
        var facts = EndpointContract.From(Endpoint());

        Assert.DoesNotContain("parameter[path:id].default", facts.Keys);
        Assert.DoesNotContain("auth.scheme", facts.Keys);
        Assert.DoesNotContain("auth.policies", facts.Keys);
    }

    [Fact]
    public void Repeated_entities_get_numbered_instead_of_overwriting_each_other()
    {
        var facts = EndpointContract.From(Endpoint(e => e with
        {
            Validation =
            [
                new ValidationRuleDefinition { PropertyName = "Name", RuleName = "Length", Arguments = ["1", "5"] },
                new ValidationRuleDefinition { PropertyName = "Name", RuleName = "Length", Arguments = ["2", "9"] }
            ]
        }));

        Assert.Equal("1,5", facts["validation[Name:Length].arguments"]);
        Assert.Equal("2,9", facts["validation[Name:Length#2].arguments"]);
    }

    [Theory]
    [InlineData("endpoint.route", "endpoint", "route")]
    [InlineData("auth.required", "auth", "required")]
    [InlineData("parameter[path:id].type", "parameter[path:id]", "type")]
    [InlineData("body.property[Address.City].example", "body.property[Address.City]", "example")]
    [InlineData("body.propertyOrder[].names", "body.propertyOrder[]", "names")]
    [InlineData("validation[Lines[].Qty:GreaterThan].arguments", "validation[Lines[].Qty:GreaterThan]", "arguments")]
    [InlineData("response[200].exampleComplete", "response[200]", "exampleComplete")]
    public void Fact_keys_split_into_entity_and_attribute(string key, string entity, string attribute) =>
        Assert.Equal((entity, attribute), EndpointContract.Split(key));

    [Fact]
    public void Fingerprint_is_derived_from_the_facts()
    {
        var endpoint = Endpoint();

        Assert.Equal(EndpointFingerprint.Compute(endpoint), EndpointFingerprint.FromContract(EndpointContract.From(endpoint)));
        Assert.NotEqual(
            EndpointFingerprint.FromContract(new ContractFacts { ["a"] = "b" }),
            EndpointFingerprint.FromContract(new ContractFacts { ["a"] = "" }));
    }

    [Fact]
    public void The_http_method_is_identity_not_contract_so_it_is_not_a_fact()
    {
        var get = Endpoint();
        var put = SnapshotTests.Endpoint(method: HttpMethodKind.Put);

        Assert.Equal(EndpointContract.From(get), EndpointContract.From(put));
        Assert.NotEqual(get.Id, put.Id);
    }

    // ---- what changed, per category ----

    [Fact]
    public void Identical_contracts_have_no_changes() => Assert.Empty(Diff(Endpoint(), Endpoint()));

    [Fact]
    public void A_changed_parameter_attribute_is_reported_with_before_and_after()
    {
        var changes = Diff(Endpoint(), Endpoint(e => e with { Parameters = [e.Parameters[0] with { TypeName = "long" }] }));

        var change = Assert.Single(changes);
        Assert.Equal(
            (ChangeCategory.Parameter, ChangeKind.Changed, "parameter[path:id].type", "int", "long"),
            (change.Category, change.Kind, change.Path, change.Before, change.After));
    }

    [Fact]
    public void An_added_parameter_is_one_added_change_that_summarises_it_without_a_reorder_change()
    {
        var added = new ParameterDefinition { Name = "page", Location = ParameterLocation.Query, TypeName = "int", IsRequired = false, DefaultValue = "1" };

        var changes = Diff(Endpoint(), Endpoint(e => e with { Parameters = [.. e.Parameters, added] }));

        var change = Assert.Single(changes);
        Assert.Equal((ChangeCategory.Parameter, ChangeKind.Added, "parameter[query:page]"), (change.Category, change.Kind, change.Path));
        Assert.Equal("default=1; required=false; type=int", change.After);
        Assert.Null(change.Before);
    }

    [Fact]
    public void A_removed_parameter_is_one_removed_change()
    {
        var withExtra = Endpoint(e => e with
        {
            Parameters = [.. e.Parameters, new ParameterDefinition { Name = "page", Location = ParameterLocation.Query, TypeName = "int" }]
        });

        var change = Assert.Single(Diff(withExtra, Endpoint()));

        Assert.Equal((ChangeCategory.Parameter, ChangeKind.Removed, "parameter[query:page]"), (change.Category, change.Kind, change.Path));
        Assert.Equal("required=false; type=int", change.Before);
    }

    [Fact]
    public void Reordering_parameters_is_reported_as_a_parameter_change()
    {
        var two = Endpoint(e => e with
        {
            Parameters =
            [
                e.Parameters[0],
                new ParameterDefinition { Name = "page", Location = ParameterLocation.Query, TypeName = "int" }
            ]
        });
        var swapped = two with { Parameters = [two.Parameters[1], two.Parameters[0]] };

        var change = Assert.Single(Diff(two, swapped));

        Assert.Equal((ChangeCategory.Parameter, "parameters.order", "path:id,query:page", "query:page,path:id"), (change.Category, change.Path, change.Before, change.After));
    }

    [Fact]
    public void Body_changes_are_reported_per_property_in_the_request_body_category()
    {
        var before = Endpoint();
        var after = Endpoint(e => e with
        {
            RequestBody = e.RequestBody! with
            {
                TypeName = "NewDto",
                Properties =
                [
                    e.RequestBody.Properties[0] with { TypeName = "int", IsRequired = true },
                    new PropertyDefinition { Name = "Price", TypeName = "decimal" }
                ]
            }
        });

        var changes = Diff(before, after);

        Assert.Contains(changes, c => c is { Category: ChangeCategory.RequestBody, Kind: ChangeKind.Changed, Path: "body.type", Before: "Dto", After: "NewDto" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.RequestBody, Kind: ChangeKind.Changed, Path: "body.property[Name].type", Before: "string", After: "int" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.RequestBody, Kind: ChangeKind.Changed, Path: "body.property[Name].required", Before: "false", After: "true" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.RequestBody, Kind: ChangeKind.Added, Path: "body.property[Price]" });
    }

    [Fact]
    public void A_body_that_appears_or_disappears_is_reported_as_added_or_removed()
    {
        var withoutBody = Endpoint(e => e with { RequestBody = null });

        var removed = Diff(Endpoint(), withoutBody);
        var added = Diff(withoutBody, Endpoint());

        Assert.Contains(removed, c => c is { Kind: ChangeKind.Removed, Path: "body" });
        Assert.Contains(removed, c => c is { Kind: ChangeKind.Removed, Path: "body.property[Name]" });
        Assert.Contains(added, c => c is { Kind: ChangeKind.Added, Path: "body" });
        Assert.All(removed.Concat(added), c => Assert.Equal(ChangeCategory.RequestBody, c.Category));
    }

    [Fact]
    public void Authentication_changes_are_reported_per_attribute()
    {
        var anonymous = Endpoint(e => e with { Authentication = new AuthenticationDefinition() });

        var changes = Diff(anonymous, Endpoint());

        Assert.Contains(changes, c => c is { Category: ChangeCategory.Authentication, Kind: ChangeKind.Changed, Path: "auth.required", Before: "false", After: "true" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.Authentication, Kind: ChangeKind.Added, Path: "auth.roles", After: "Admin" });
        Assert.All(changes, c => Assert.Equal(ChangeCategory.Authentication, c.Category));
    }

    [Fact]
    public void Validation_response_and_naming_changes_get_their_own_categories()
    {
        var changed = Endpoint(e => e with
        {
            ControllerName = "Items",
            ActionName = "Fetch",
            RouteTemplate = "API/Products/{productId:int}",
            Validation = [e.Validation[0] with { Arguments = ["50"] }],
            Responses = [new ResponseDefinition { StatusCode = 201, TypeName = "Dto" }]
        });

        var changes = Diff(Endpoint(), changed);

        Assert.Contains(changes, c => c is { Category: ChangeCategory.Naming, Path: "endpoint.controller", Before: "Products", After: "Items" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.Naming, Path: "endpoint.action", Before: "GetById", After: "Fetch" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.Route, Path: "endpoint.route", Before: "api/Products/{id:int}", After: "API/Products/{productId:int}" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.Validation, Kind: ChangeKind.Changed, Before: "100", After: "50" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.Response, Kind: ChangeKind.Removed, Path: "response[200]" });
        Assert.Contains(changes, c => c is { Category: ChangeCategory.Response, Kind: ChangeKind.Added, Path: "response[201]" });
    }

    [Fact]
    public void Example_changes_are_their_own_category_even_when_they_sit_on_a_property_or_the_body()
    {
        var changed = Endpoint(e => e with
        {
            RequestBody = e.RequestBody! with
            {
                Properties = [e.RequestBody.Properties[0] with { Example = e.RequestBody.Properties[0].Example! with { Json = "\"Phone\"" } }],
                Example = e.RequestBody.Example! with { Json = "{ \"name\": \"Phone\" }" }
            }
        });

        var changes = Diff(Endpoint(), changed);

        Assert.Equal(["body.example", "body.property[Name].example"], changes.Select(c => c.Path));
        Assert.All(changes, c => Assert.Equal(ChangeCategory.Example, c.Category));
    }

    [Fact]
    public void Changes_are_ordered_by_path()
    {
        var changed = Endpoint(e => e with
        {
            ActionName = "Fetch",
            Authentication = new AuthenticationDefinition(),
            Parameters = [e.Parameters[0] with { TypeName = "long" }]
        });

        var paths = Diff(Endpoint(), changed).Select(c => c.Path).ToList();

        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal), paths);
    }

    // ---- change set ----

    private static AnalysisSnapshot Snapshot(params EndpointDefinition[] endpoints) => AnalysisSnapshot.Create(endpoints);

    private static EndpointDefinition At(string action, HttpMethodKind method, string route) =>
        SnapshotTests.Endpoint("Products", action, method, route);

    [Fact]
    public void Addition_modification_removal_and_no_change_are_each_classified()
    {
        var unchanged = At("GetAll", HttpMethodKind.Get, "api/Products");
        var modified = At("GetById", HttpMethodKind.Get, "api/Products/{id:int}");
        var removed = At("Delete", HttpMethodKind.Delete, "api/Products/{id:int}");
        var added = At("Create", HttpMethodKind.Post, "api/Products");

        var changes = ChangeSetBuilder.Build(
            Snapshot(unchanged, modified, removed),
            Snapshot(unchanged, modified with { Authentication = new AuthenticationDefinition() }, added));

        Assert.Equal(new ChangeSetSummary(1, 1, 1, 1), changes.Summary);
        Assert.True(changes.HasChanges);
        Assert.Equal(4, changes.Summary.Total);
        var byStatus = changes.Endpoints.ToDictionary(e => e.ActionName, e => e.Status);
        Assert.Equal(
            new Dictionary<string, ChangeStatus>
            {
                ["GetAll"] = ChangeStatus.Unchanged, ["GetById"] = ChangeStatus.Modified,
                ["Delete"] = ChangeStatus.Removed, ["Create"] = ChangeStatus.Added
            },
            byStatus);
        Assert.NotEmpty(changes.Endpoints.Single(e => e.Status == ChangeStatus.Modified).Changes);
        Assert.All(changes.Endpoints.Where(e => e.Status != ChangeStatus.Modified), e => Assert.Empty(e.Changes));
    }

    [Fact]
    public void Without_any_difference_the_change_set_says_so()
    {
        var all = new[] { At("GetAll", HttpMethodKind.Get, "api/Products"), At("Create", HttpMethodKind.Post, "api/Products") };

        var changes = ChangeSetBuilder.Build(Snapshot(all), Snapshot(all.Reverse().ToArray()));

        Assert.Equal(new ChangeSetSummary(0, 0, 0, 2), changes.Summary);
        Assert.False(changes.HasChanges);
        Assert.All(changes.Endpoints, e => Assert.Equal(ChangeStatus.Unchanged, e.Status));
    }

    [Fact]
    public void Every_endpoint_appears_once_ordered_by_identity_and_warnings_are_carried_through()
    {
        var changes = ChangeSetBuilder.Build(
            Snapshot(At("Delete", HttpMethodKind.Delete, "api/Products/{id:int}")),
            Snapshot(At("Create", HttpMethodKind.Post, "api/Products"), At("GetAll", HttpMethodKind.Get, "api/Products")),
            ["Discovery: something failed"]);

        Assert.Equal(["DELETE /api/products/{:int}", "GET /api/products", "POST /api/products"], changes.Endpoints.Select(e => e.Id));
        Assert.Equal(["Discovery: something failed"], changes.Warnings);
        Assert.Equal(ChangeSet.CurrentSchemaVersion, changes.SchemaVersion);
    }

    [Fact]
    public void A_removed_endpoint_keeps_its_previous_names_and_a_moved_one_is_only_a_rename_suggestion()
    {
        var before = At("GetById", HttpMethodKind.Get, "api/Products/{id:int}");
        var after = At("GetById", HttpMethodKind.Get, "api/v2/Products/{id:int}");

        var changes = ChangeSetBuilder.Build(Snapshot(before), Snapshot(after));

        var added = changes.Endpoints.Single(e => e.Status == ChangeStatus.Added);
        var removed = changes.Endpoints.Single(e => e.Status == ChangeStatus.Removed);
        Assert.Equal("api/v2/Products/{id:int}", added.Route);
        Assert.Equal("api/Products/{id:int}", removed.Route);
        Assert.Equal(removed.Id, added.PossibleRename);
        Assert.Equal(added.Id, removed.PossibleRename);
        Assert.Equal(new ChangeSetSummary(1, 0, 1, 0), changes.Summary);
    }

    [Fact]
    public void Snapshots_from_other_versions_cannot_be_diffed()
    {
        var current = Snapshot(At("GetAll", HttpMethodKind.Get, "api/Products"));

        Assert.Throws<InvalidOperationException>(() => ChangeSetBuilder.Build(current with { IdentityVersion = EndpointIdentity.Version + 1 }, current));
    }
}
