using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Snapshots;
using ApiCollection.Infrastructure.Configuration;
using ApiCollection.Infrastructure.FileSystem;

namespace ApiCollection.Postman.Tests;

/// <summary>
/// fixtures/postman/sample-api.snapshot.json is the first stored baseline: it ties the analysed endpoints to the committed
/// collection. If these tests fail after an intended API or generator change, regenerate both files together.
/// </summary>
public sealed class CommittedSnapshotTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("ApiCollection.sln was not found above the test directory.");
    }

    private static string Fixture(string name) => Path.Combine(Root, "fixtures", "postman", name);

    private static EndpointDiscoveryResult Analyse()
    {
        var metadata = ExampleMetadataLoader.Load(Path.Combine(Root, "config", "examples.sample-api.json"));
        return new EndpointDiscoveryEngine().DiscoverFromDirectory(Path.Combine(Root, "sample-api", "SampleApi"), metadata);
    }

    [Fact]
    public void Committed_snapshot_matches_the_current_analysis_and_the_committed_collection()
    {
        var collectionText = File.ReadAllText(Fixture("sample-api.postman_collection.json"));
        var expected = AnalysisSnapshotStore.Serialize(AnalysisSnapshot.Create(
            Analyse().Endpoints, SnapshotCollection.FromJson("sample-api.postman_collection.json", collectionText)));

        var committed = File.ReadAllText(Fixture("sample-api.snapshot.json")).Replace("\r\n", "\n");

        Assert.True(committed == expected, "fixtures/postman/sample-api.snapshot.json is stale; regenerate it together with the collection.");
    }

    [Fact]
    public void Analysing_the_sample_api_again_shows_no_new_endpoints_against_the_stored_snapshot()
    {
        var stored = AnalysisSnapshotStore.Load(Fixture("sample-api.snapshot.json"));
        var current = AnalysisSnapshot.Create(Analyse().Endpoints);

        var comparison = SnapshotComparer.Compare(stored, current);

        Assert.Equal(15, comparison.Unchanged.Count);
        Assert.Empty(comparison.Added);
        Assert.Empty(comparison.Removed);
        Assert.Empty(comparison.Modified);
        Assert.Empty(comparison.RenameCandidates);
    }
}
