using ApiCollection.Core.Models;
using ApiCollection.Core.Snapshots;

namespace ApiCollection.Core.Tests;

public sealed class SnapshotComparerTests
{
    private static AnalysisSnapshot Snapshot(params EndpointDefinition[] endpoints) => AnalysisSnapshot.Create(endpoints);

    private static EndpointDefinition Endpoint(string controller, string action, HttpMethodKind method, string route) =>
        SnapshotTests.Endpoint(controller, action, method, route);

    private static readonly EndpointDefinition GetAll = Endpoint("Products", "GetAll", HttpMethodKind.Get, "api/Products");
    private static readonly EndpointDefinition GetById = Endpoint("Products", "GetById", HttpMethodKind.Get, "api/Products/{id:int}");
    private static readonly EndpointDefinition Create = Endpoint("Products", "Create", HttpMethodKind.Post, "api/Products");

    [Fact]
    public void The_same_endpoints_are_all_unchanged_and_nothing_looks_new()
    {
        var result = SnapshotComparer.Compare(Snapshot(GetAll, GetById, Create), Snapshot(Create, GetById, GetAll));

        Assert.Equal(3, result.Unchanged.Count);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
        Assert.Empty(result.Modified);
        Assert.Empty(result.RenameCandidates);
    }

    [Fact]
    public void A_content_change_is_modified_not_added_or_removed()
    {
        var changed = GetById with { Parameters = [GetById.Parameters[0] with { TypeName = "long" }] };

        var result = SnapshotComparer.Compare(Snapshot(GetAll, GetById), Snapshot(GetAll, changed));

        var modification = Assert.Single(result.Modified);
        Assert.Equal("GET /api/products/{:int}", modification.Current.Id);
        Assert.NotEqual(modification.Previous.Fingerprint, modification.Current.Fingerprint);
        Assert.Equal(["GET /api/products"], result.Unchanged.Select(e => e.Id));
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Renaming_the_controller_or_action_with_the_same_route_keeps_the_identity()
    {
        var renamedAction = Endpoint("Products", "Fetch", HttpMethodKind.Get, "api/Products/{id:int}");
        var renamedController = Endpoint("Items", "GetById", HttpMethodKind.Get, "api/Products/{id:int}");

        foreach (var renamed in new[] { renamedAction, renamedController })
        {
            var result = SnapshotComparer.Compare(Snapshot(GetById), Snapshot(renamed));

            Assert.Single(result.Modified);
            Assert.Empty(result.Added);
            Assert.Empty(result.Removed);
        }
    }

    [Fact]
    public void New_and_missing_endpoints_are_reported_as_added_and_removed()
    {
        var result = SnapshotComparer.Compare(Snapshot(GetAll, GetById), Snapshot(GetAll, Create));

        Assert.Equal(["POST /api/products"], result.Added.Select(e => e.Id));
        Assert.Equal(["GET /api/products/{:int}"], result.Removed.Select(e => e.Id));
        Assert.Equal(["GET /api/products"], result.Unchanged.Select(e => e.Id));
        Assert.Empty(result.RenameCandidates);
    }

    [Fact]
    public void A_route_change_is_removed_plus_added_with_a_rename_candidate_but_nothing_is_merged()
    {
        var moved = Endpoint("Products", "GetById", HttpMethodKind.Get, "api/v2/Products/{id:int}");

        var result = SnapshotComparer.Compare(Snapshot(GetAll, GetById), Snapshot(GetAll, moved));

        Assert.Equal(["GET /api/v2/products/{:int}"], result.Added.Select(e => e.Id));
        Assert.Equal(["GET /api/products/{:int}"], result.Removed.Select(e => e.Id));
        var candidate = Assert.Single(result.RenameCandidates);
        Assert.Equal(("GET /api/products/{:int}", "GET /api/v2/products/{:int}"), (candidate.Previous.Id, candidate.Current.Id));
        Assert.Empty(result.Unchanged.Where(e => e.ActionName == "GetById"));
    }

    [Fact]
    public void A_method_change_is_also_a_rename_candidate()
    {
        var changed = Endpoint("Products", "Create", HttpMethodKind.Put, "api/Products");

        var result = SnapshotComparer.Compare(Snapshot(Create), Snapshot(changed));

        Assert.Equal(["PUT /api/products"], result.Added.Select(e => e.Id));
        Assert.Equal(["POST /api/products"], result.Removed.Select(e => e.Id));
        Assert.Single(result.RenameCandidates);
    }

    [Fact]
    public void No_candidate_when_the_controller_changed_too_or_when_the_pairing_is_ambiguous()
    {
        var otherController = Endpoint("Items", "GetById", HttpMethodKind.Get, "api/Items/{id:int}");
        var controllerAndRoute = SnapshotComparer.Compare(Snapshot(GetById), Snapshot(otherController));
        Assert.Empty(controllerAndRoute.RenameCandidates);
        Assert.Single(controllerAndRoute.Added);
        Assert.Single(controllerAndRoute.Removed);

        // Two overloads of the same action both lose their routes: which new route belongs to which old one is a guess.
        var overloadA = Endpoint("Search", "Find", HttpMethodKind.Get, "api/search/a");
        var overloadB = Endpoint("Search", "Find", HttpMethodKind.Get, "api/search/b");
        var movedA = Endpoint("Search", "Find", HttpMethodKind.Get, "api/find/a");
        var movedB = Endpoint("Search", "Find", HttpMethodKind.Get, "api/find/b");

        var ambiguous = SnapshotComparer.Compare(Snapshot(overloadA, overloadB), Snapshot(movedA, movedB));
        Assert.Empty(ambiguous.RenameCandidates);
        Assert.Equal(2, ambiguous.Added.Count);
        Assert.Equal(2, ambiguous.Removed.Count);
    }

    [Fact]
    public void Results_are_ordered_by_identity()
    {
        var result = SnapshotComparer.Compare(
            Snapshot(),
            Snapshot(Create, GetById, GetAll));

        Assert.Equal(["GET /api/products", "GET /api/products/{:int}", "POST /api/products"], result.Added.Select(e => e.Id));
    }

    [Fact]
    public void Snapshots_made_with_different_identity_rules_or_schema_are_refused()
    {
        var current = Snapshot(GetAll);

        var identity = Assert.Throws<InvalidOperationException>(() => SnapshotComparer.Compare(current with { IdentityVersion = 99 }, current));
        Assert.Contains("identity rules 99/1", identity.Message);
        Assert.Contains("new baseline", identity.Message);

        var schema = Assert.Throws<InvalidOperationException>(() => SnapshotComparer.Compare(current, current with { SchemaVersion = 2 }));
        Assert.Contains("schema 1/2", schema.Message);
    }
}
