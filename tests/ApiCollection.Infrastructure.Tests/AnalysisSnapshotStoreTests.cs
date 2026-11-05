using ApiCollection.Core.Diff;
using ApiCollection.Core.Identity;
using ApiCollection.Core.Models;
using ApiCollection.Core.Snapshots;
using ApiCollection.Infrastructure.FileSystem;

namespace ApiCollection.Infrastructure.Tests;

public sealed class AnalysisSnapshotStoreTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ApiCollection.SnapshotTests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    private static EndpointDefinition Endpoint(string action, HttpMethodKind method, string route) => new()
    {
        Id = EndpointIdentity.Create(method, route),
        ControllerName = "Products",
        ActionName = action,
        HttpMethod = method,
        RouteTemplate = route
    };

    private static AnalysisSnapshot Sample() => AnalysisSnapshot.Create(
        [
            Endpoint("GetAll", HttpMethodKind.Get, "api/Products"),
            Endpoint("GetById", HttpMethodKind.Get, "api/Products/{id:int}"),
            Endpoint("Create", HttpMethodKind.Post, "api/Products")
        ],
        SnapshotCollection.FromJson("sample.postman_collection.json", "{ }"));

    [Fact]
    public void Serialized_snapshot_is_deterministic_text_without_timestamps_or_paths()
    {
        var text = AnalysisSnapshotStore.Serialize(Sample());

        Assert.Equal(text, AnalysisSnapshotStore.Serialize(Sample()));
        Assert.StartsWith("{\n", text);
        Assert.EndsWith("}\n", text);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("\"schemaVersion\": 2", text);
        Assert.Contains("\"identityVersion\": 1", text);
        Assert.Contains("\"id\": \"GET /api/products/{:int}\"", text);
        Assert.Contains("\"fileName\": \"sample.postman_collection.json\"", text);
        Assert.DoesNotMatch(@"[A-Za-z]:\\|/Users/|20\d\d-\d\d-\d\d", text);
    }

    [Fact]
    public void Snapshot_survives_a_round_trip_and_still_compares_as_unchanged()
    {
        var original = Sample();

        var loaded = AnalysisSnapshotStore.Deserialize(AnalysisSnapshotStore.Serialize(original), "snapshot.json");

        Assert.Equal(original.Endpoints, loaded.Endpoints);
        Assert.Equal(original.Collection, loaded.Collection);
        Assert.Equal((original.SchemaVersion, original.IdentityVersion), (loaded.SchemaVersion, loaded.IdentityVersion));
        Assert.Equal(AnalysisSnapshotStore.Serialize(original), AnalysisSnapshotStore.Serialize(loaded));

        var comparison = SnapshotComparer.Compare(loaded, original);
        Assert.Equal(3, comparison.Unchanged.Count);
        Assert.Empty(comparison.Added);
        Assert.Empty(comparison.Removed);
        Assert.Empty(comparison.Modified);
    }

    [Fact]
    public void Save_creates_missing_folders_and_load_reads_the_file_back()
    {
        var path = Path.Combine(_work, "nested", "baseline", "api.snapshot.json");

        AnalysisSnapshotStore.Save(path, Sample());
        var loaded = AnalysisSnapshotStore.Load(path);

        Assert.Equal(Sample().Endpoints, loaded.Endpoints);
        Assert.Equal("{\n", File.ReadAllText(path)[..2]);
    }

    [Fact]
    public void A_snapshot_without_a_collection_round_trips()
    {
        var snapshot = AnalysisSnapshot.Create([Endpoint("GetAll", HttpMethodKind.Get, "api/Products")]);

        var text = AnalysisSnapshotStore.Serialize(snapshot);
        var loaded = AnalysisSnapshotStore.Deserialize(text, "s.json");

        Assert.Null(loaded.Collection);
        Assert.Single(loaded.Endpoints);
    }

    [Theory]
    [InlineData("{ not json", "not a valid snapshot")]
    [InlineData("null", "snapshot is empty")]
    [InlineData("""{ "schemaVersion": 3, "identityVersion": 1, "endpoints": [] }""", "schema version 3 is not supported")]
    [InlineData("""{ "schemaVersion": 2, "identityVersion": 1, "endpoints": [ { "id": "GET /a" } ] }""", "not a valid snapshot")]
    public void Unreadable_or_unsupported_snapshots_are_rejected_with_a_clear_message(string json, string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(() => AnalysisSnapshotStore.Deserialize(json, "baseline.json"));

        Assert.Contains("baseline.json", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Duplicate_identities_in_a_file_are_rejected()
    {
        const string entry = """{ "id": "GET /a", "fingerprint": "f", "httpMethod": "GET", "route": "a", "controllerName": "A", "actionName": "A", "contract": {} }""";
        var json = $$"""{ "schemaVersion": 2, "identityVersion": 1, "endpoints": [ {{entry}}, {{entry}} ] }""";

        var exception = Assert.Throws<InvalidDataException>(() => AnalysisSnapshotStore.Deserialize(json, "dup.json"));

        Assert.Contains("'GET /a' appears more than once", exception.Message);
    }

    [Fact]
    public void A_snapshot_whose_fingerprint_no_longer_matches_its_contract_is_rejected_as_edited()
    {
        var text = AnalysisSnapshotStore.Serialize(Sample());

        var tampered = text.Replace("\"endpoint.action\": \"GetAll\"", "\"endpoint.action\": \"Hacked\"", StringComparison.Ordinal);
        Assert.NotEqual(text, tampered);

        var exception = Assert.Throws<InvalidDataException>(() => AnalysisSnapshotStore.Deserialize(tampered, "baseline.json"));

        Assert.Contains("baseline.json", exception.Message);
        Assert.Contains("does not match its contract", exception.Message);
        Assert.Contains("GET /api/products", exception.Message);
    }

    [Fact]
    public void Contract_facts_are_stored_sorted_and_survive_a_round_trip_by_value()
    {
        var snapshot = Sample();

        var loaded = AnalysisSnapshotStore.Deserialize(AnalysisSnapshotStore.Serialize(snapshot), "s.json");

        var getById = loaded.Endpoints.Single(e => e.ActionName == "GetById");
        Assert.Equal(snapshot.Endpoints.Single(e => e.ActionName == "GetById").Contract, getById.Contract);
        Assert.Equal(getById.Contract.Keys.OrderBy(k => k, StringComparer.Ordinal), getById.Contract.Keys);
    }

    [Fact]
    public void Change_set_is_written_as_deterministic_camel_case_json_with_enum_names()
    {
        var before = AnalysisSnapshot.Create([Endpoint("GetAll", HttpMethodKind.Get, "api/Products")]);
        var after = AnalysisSnapshot.Create(
        [
            Endpoint("GetAll", HttpMethodKind.Get, "api/Products") with { Authentication = new AuthenticationDefinition { RequiresAuthentication = true } },
            Endpoint("Create", HttpMethodKind.Post, "api/Products")
        ]);
        var changes = ChangeSetBuilder.Build(before, after, ["a warning"]);

        var text = ChangeSetSerializer.Serialize(changes);

        Assert.Equal(text, ChangeSetSerializer.Serialize(changes));
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text);
        Assert.Contains("\"hasChanges\": true", text);
        Assert.Contains("\"status\": \"Modified\"", text);
        Assert.Contains("\"status\": \"Added\"", text);
        Assert.Contains("\"category\": \"Authentication\"", text);
        Assert.Contains("\"kind\": \"Changed\"", text);
        Assert.Contains("\"path\": \"auth.required\"", text);
        Assert.Contains("\"before\": \"false\"", text);
        Assert.Contains("\"after\": \"true\"", text);
        Assert.Contains("\"warnings\": [", text);
        Assert.DoesNotContain("possibleRename", text);
    }

    [Fact]
    public void A_snapshot_from_other_identity_rules_loads_but_is_refused_by_the_comparer()
    {
        var old = AnalysisSnapshotStore.Deserialize(
            """{ "schemaVersion": 2, "identityVersion": 0, "endpoints": [] }""", "old.json");

        Assert.Equal(0, old.IdentityVersion);
        Assert.Throws<InvalidOperationException>(() => SnapshotComparer.Compare(old, Sample()));
    }
}
