using ApiCollection.Core.Diff;
using ApiCollection.Core.Sync;
using ApiCollection.Infrastructure.FileSystem;

namespace ApiCollection.Infrastructure.Tests;

public sealed class SyncInfrastructureTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ApiCollection.SyncTests." + Guid.NewGuid().ToString("N"));

    public SyncInfrastructureTests() => Directory.CreateDirectory(_work);

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string InWork(string name) => Path.Combine(_work, name);

    // ---- AtomicFileWriter ----

    [Fact]
    public void All_files_are_written_with_their_folders_and_no_temporary_file_is_left()
    {
        var first = InWork(Path.Combine("a", "b", "collection.json"));
        var second = InWork("baseline.json");

        AtomicFileWriter.WriteAll([(first, "collection"), (second, "baseline")]);

        Assert.Equal("collection", File.ReadAllText(first));
        Assert.Equal("baseline", File.ReadAllText(second));
        Assert.Empty(Directory.EnumerateFiles(_work, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Existing_files_are_replaced()
    {
        var path = InWork("collection.json");
        File.WriteAllText(path, "old");

        AtomicFileWriter.WriteAll([(path, "new")]);

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void A_failure_while_writing_leaves_every_existing_file_untouched_and_cleans_up()
    {
        var existing = InWork("collection.json");
        File.WriteAllText(existing, "old collection");
        var blocker = InWork("blocker");
        File.WriteAllText(blocker, "a file where a folder is needed");
        var wouldBeCreated = InWork("other.json");

        Assert.ThrowsAny<IOException>(() => AtomicFileWriter.WriteAll(
        [
            (existing, "new collection"),
            (wouldBeCreated, "new other"),
            (Path.Combine(blocker, "baseline.json"), "new baseline")
        ]));

        Assert.Equal("old collection", File.ReadAllText(existing));
        Assert.False(File.Exists(wouldBeCreated));
        Assert.Empty(Directory.EnumerateFiles(_work, "*.tmp", SearchOption.AllDirectories));
    }

    // ---- SyncDecisionSerializer ----

    [Fact]
    public void The_decision_is_written_as_deterministic_camel_case_json_with_enum_names()
    {
        var changes = new ChangeSet
        {
            Summary = new ChangeSetSummary(0, 0, 5, 0),
            Endpoints = Enumerable.Range(0, 5).Select(i => new EndpointDiff
            {
                Id = $"GET /api/x{i}", Status = ChangeStatus.Removed, HttpMethod = "GET", Route = $"api/x{i}", ControllerName = "X", ActionName = $"A{i}"
            }).ToList()
        };
        var decision = SyncSafetyValidator.Validate(new SyncSafetyInput { Changes = changes, DiscoveryFailures = ["a route could not be resolved"] });

        var text = SyncDecisionSerializer.Serialize(decision);

        Assert.Equal(text, SyncDecisionSerializer.Serialize(decision));
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text);
        Assert.Contains("\"outcome\": \"Blocked\"", text);
        Assert.Contains("\"code\": \"DiscoveryFailed\"", text);
        Assert.Contains("\"code\": \"DiscoveryIncomplete\"", text);
        Assert.Contains("\"code\": \"MassDeletion\"", text);
        Assert.Contains("\"canOverride\": false", text);
        Assert.Contains("\"canOverride\": true", text);
        Assert.Contains("\"action\": \"Delete\"", text);
        Assert.Contains("\"maxRemovedPercent\": 30", text);
        Assert.Contains("\"maxRemovedCount\": 10", text);
        Assert.Contains("\"removed\": 5", text);
    }
}
