using System.Security.Cryptography;
using System.Text.Json;
using ApiCollection.Cli;

namespace ApiCollection.Cli.Tests;

/// <summary>
/// The sync gate end to end: the second run on the same source changes nothing, and a failed or suspicious analysis never
/// touches the stored collection or its baseline.
/// </summary>
public sealed class SyncCommandTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ApiCollection.SyncCli." + Guid.NewGuid().ToString("N"));

    public SyncCommandTests() => Directory.CreateDirectory(_work);

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string InWork(string name) => Path.Combine(_work, name);

    private string Collection => InWork("store/api.postman_collection.json");

    private string Baseline => InWork("store/api.snapshot.json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static string SampleApi => Path.Combine(RepositoryRoot(), "fixtures", "SampleApi");

    private static string Examples => Path.Combine(RepositoryRoot(), "fixtures", "examples", "examples.sample-api.json");

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private (int Code, string Out, string Err) Sync(string api, params string[] extra) =>
        Run([.. new[] { "sync", api, "--baseline", Baseline, "--out", Collection }, .. extra]);

    // ---- a small API with three controllers (six endpoints) ----

    private const string Items = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpGet("{id:int}")] public IActionResult Get(int id) => Ok();
            [HttpPost] public IActionResult Create(CreateItem request) => Ok();
            [HttpDelete("{id:int}")] public IActionResult Delete(int id) => NoContent();
        }
        public class CreateItem { public string Name { get; set; } = ""; }
        """;

    private const string Extra = """
        [ApiController]
        [Route("api/extra")]
        public class ExtraController : ControllerBase { [HttpGet("ping")] public IActionResult Ping() => Ok(); }
        """;

    private const string Info = """
        [ApiController]
        [Route("api/info")]
        public class InfoController : ControllerBase { [HttpGet("version")] public IActionResult Version() => Ok(); }
        """;

    private string WriteApi(string name, params (string File, string Source)[] files)
    {
        var api = InWork(name);
        Directory.CreateDirectory(api);
        foreach (var (file, source) in files)
        {
            File.WriteAllText(Path.Combine(api, file), source);
        }

        return api;
    }

    private string BaseApi(string name = "api-v1") => WriteApi(name, ("Items.cs", Items), ("Extra.cs", Extra), ("Info.cs", Info));

    private sealed record FileState(string Hash, DateTime Modified);

    private static FileState State(string path) => new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path));

    private (FileState Collection, FileState Baseline) Stored() => (State(Collection), State(Baseline));

    private void AssertNothingWritten((FileState Collection, FileState Baseline) before)
    {
        Assert.Equal(before, Stored());
        Assert.Empty(Directory.EnumerateFiles(_work, "*.tmp", SearchOption.AllDirectories));
    }

    // ---- first sync and dry run ----

    [Fact]
    public void The_first_sync_creates_the_collection_and_a_baseline_that_matches_it()
    {
        var (code, output, _) = Sync(BaseApi());

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Sync decision: ALLOWED", output);
        Assert.Contains("First sync: there is no baseline yet, so nothing can be deleted.", output);
        Assert.Contains("Summary: 6 to create, 0 to update, 0 to delete, 0 unchanged", output);
        Assert.Contains("Applying the plan.", output);
        Assert.Contains("Wrote collection ", output);
        Assert.Contains("Wrote baseline ", output);

        using var collection = JsonDocument.Parse(File.ReadAllText(Collection));
        Assert.Equal(6, collection.RootElement.GetProperty("item").EnumerateArray().Sum(f => f.GetProperty("item").GetArrayLength()));
        using var baseline = JsonDocument.Parse(File.ReadAllText(Baseline));
        Assert.Equal(6, baseline.RootElement.GetProperty("endpoints").GetArrayLength());
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Collection))).ToLowerInvariant();
        Assert.Equal(expectedHash, baseline.RootElement.GetProperty("collection").GetProperty("sha256").GetString());
        Assert.Equal("api.postman_collection.json", baseline.RootElement.GetProperty("collection").GetProperty("fileName").GetString());
        Assert.Empty(Directory.EnumerateFiles(_work, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_dry_run_of_the_first_sync_shows_the_plan_and_creates_no_file()
    {
        var (code, output, _) = Sync(BaseApi(), "--dry-run");

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Sync decision: ALLOWED (dry run)", output);
        Assert.Contains("  + create  GET /api/items   Items.List", output);
        Assert.Contains("Dry run: nothing was written to the collection or the baseline.", output);
        Assert.False(File.Exists(Collection));
        Assert.False(File.Exists(Baseline));
    }

    // ---- idempotency ----

    [Fact]
    public void Running_twice_on_the_same_source_changes_nothing_the_second_time()
    {
        var api = BaseApi();
        Assert.Equal(CliApplication.Success, Sync(api).Code);
        var afterFirst = Stored();
        Thread.Sleep(50);

        var (code, output, _) = Sync(api);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Sync decision: NO CHANGES", output);
        Assert.Contains("Summary: 0 to create, 0 to update, 0 to delete, 6 unchanged", output);
        Assert.Contains("No changes: nothing was written.", output);
        Assert.DoesNotContain("Plan (", output);
        Assert.DoesNotContain("Wrote ", output);
        AssertNothingWritten(afterFirst);

        Thread.Sleep(50);
        Assert.Contains("NO CHANGES", Sync(api, "--dry-run").Out);
        AssertNothingWritten(afterFirst);
    }

    [Fact]
    public void The_real_sample_api_is_stable_across_runs_and_the_second_run_writes_nothing()
    {
        // Named like the committed fixtures, so what is written can be compared with them.
        var collection = InWork("store/sample-api.postman_collection.json");
        var baseline = InWork("store/sample-api.snapshot.json");
        string[] args = ["sync", SampleApi, "--baseline", baseline, "--out", collection, "--examples", Examples, "--name", "Sample API"];

        var first = Run(args);
        Assert.Equal(CliApplication.Success, first.Code);
        Assert.Contains("Summary: 15 to create", first.Out);
        var afterFirst = (State(collection), State(baseline));
        Thread.Sleep(50);

        var second = Run(args);

        Assert.Equal(CliApplication.Success, second.Code);
        Assert.Contains("Summary: 0 to create, 0 to update, 0 to delete, 15 unchanged", second.Out);
        Assert.Contains("No changes: nothing was written.", second.Out);
        Assert.Equal(afterFirst, (State(collection), State(baseline)));
        Assert.Empty(Directory.EnumerateFiles(_work, "*.tmp", SearchOption.AllDirectories));

        // What it wrote is exactly the committed baseline and collection: the same source is recognised as already synced.
        var fixtures = Path.Combine(RepositoryRoot(), "fixtures", "postman");
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "sample-api.snapshot.json")).Replace("\r\n", "\n"), File.ReadAllText(baseline));
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "sample-api.postman_collection.json")).Replace("\r\n", "\n"), File.ReadAllText(collection));
    }

    [Fact]
    public void Reformatting_the_source_does_not_cause_a_write()
    {
        var api = BaseApi();
        Sync(api);
        var afterFirst = Stored();
        Thread.Sleep(50);

        WriteApi("api-v1", ("Items.cs", "// reformatted\r\n\r\n" + Items.Replace("\n", "\r\n")), ("Extra.cs", "namespace Moved;\n" + Extra), ("Info.cs", Info));
        var (code, output, _) = Sync(api);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("NO CHANGES", output);
        AssertNothingWritten(afterFirst);
    }

    [Fact]
    public void Applied_changes_become_the_new_baseline_so_the_next_run_and_a_diff_see_nothing_new()
    {
        var api = BaseApi();
        Sync(api);
        var evolved = Items
            .Replace("[HttpGet] public IActionResult List() => Ok();", "[HttpGet] public IActionResult List() => Ok();\n    [HttpGet(\"count\")] public IActionResult Count() => Ok();")
            .Replace("Get(int id)", "Get(int id, string? expand)")
            .Replace("    [HttpDelete(\"{id:int}\")] public IActionResult Delete(int id) => NoContent();\n", string.Empty);
        WriteApi("api-v1", ("Items.cs", evolved), ("Extra.cs", Extra), ("Info.cs", Info));

        var (code, output, _) = Sync(api);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Summary: 1 to create, 1 to update, 1 to delete, 4 unchanged", output);
        Assert.Contains("  + create  GET /api/items/count   Items.Count", output);
        Assert.Contains("  ~ update  GET /api/items/{:int}   Items.Get", output);
        Assert.Contains("  - delete  DELETE /api/items/{:int}   Items.Delete", output);
        Assert.Contains("Wrote collection ", output);
        Assert.Contains("\"count\"", File.ReadAllText(Collection).Replace("Count", "count"));
        Assert.DoesNotContain("\"Delete\"", File.ReadAllText(Collection));

        var afterApply = Stored();
        Thread.Sleep(50);
        var again = Sync(api);
        Assert.Contains("NO CHANGES", again.Out);
        Assert.Contains("Summary: 0 to create, 0 to update, 0 to delete, 6 unchanged", again.Out);
        AssertNothingWritten(afterApply);

        var diff = Run("diff", api, "--baseline", Baseline, "--exit-code");
        Assert.Equal(CliApplication.Success, diff.Code);
        Assert.Contains("No changes.", diff.Out);
    }

    [Fact]
    public void A_dry_run_with_pending_changes_writes_no_collection_or_baseline_but_still_writes_the_requested_reports()
    {
        var api = BaseApi();
        Sync(api);
        var before = Stored();
        Thread.Sleep(50);
        WriteApi("api-v1", ("Items.cs", Items.Replace("Get(int id)", "Get(int id, string? expand)")), ("Extra.cs", Extra), ("Info.cs", Info));
        var plan = InWork("out/plan.json");
        var report = InWork("out/sync.txt");
        var changes = InWork("out/changes.json");

        var (code, output, _) = Sync(api, "--dry-run", "--plan", plan, "--report", report, "--changes", changes);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Sync decision: ALLOWED (dry run)", output);
        Assert.Contains("  ~ update  GET /api/items/{:int}   Items.Get", output);
        AssertNothingWritten(before);
        Assert.Equal(output, File.ReadAllText(report));
        using var planJson = JsonDocument.Parse(File.ReadAllText(plan));
        Assert.Equal("Allowed", planJson.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Update", planJson.RootElement.GetProperty("plan")[0].GetProperty("action").GetString());
        using var changesJson = JsonDocument.Parse(File.ReadAllText(changes));
        Assert.True(changesJson.RootElement.GetProperty("hasChanges").GetBoolean());
    }

    // ---- fail closed ----

    private (int Code, string Out, string Err) AssertBlocked(string api, string code, params string[] extra)
    {
        var before = Stored();
        Thread.Sleep(50);

        var result = Sync(api, extra);

        Assert.Equal(CliApplication.Blocked, result.Code);
        Assert.Contains("Sync decision: BLOCKED", result.Out);
        Assert.Contains($"[{code}]", result.Out);
        Assert.Contains("Blocked: nothing was written to the collection or the baseline.", result.Out);
        AssertNothingWritten(before);
        return result;
    }

    [Fact]
    public void A_source_file_that_no_longer_parses_blocks_the_sync_and_nothing_is_written()
    {
        Sync(BaseApi());
        var broken = WriteApi("api-broken", ("Items.cs", Items.Replace("=> Ok();", "=> Ok(;")), ("Extra.cs", Extra), ("Info.cs", Info));
        var plan = InWork("out/plan.json");
        var report = InWork("out/sync.txt");

        var (_, output, _) = AssertBlocked(broken, "DiscoveryIncomplete", "--plan", plan, "--report", report);

        Assert.Contains("Items.cs: the source has", output);
        Assert.Contains("can be overridden explicitly", output);
        // The decision is reported even though nothing is written to the collection or the baseline.
        using var planJson = JsonDocument.Parse(File.ReadAllText(plan));
        Assert.Equal("Blocked", planJson.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(output, File.ReadAllText(report));
    }

    [Fact]
    public void An_unresolvable_route_blocks_even_though_only_one_endpoint_looks_removed()
    {
        Sync(BaseApi());
        var broken = WriteApi("api-broken", ("Items.cs", Items), ("Extra.cs", Extra.Replace("[Route(\"api/extra\")]", "[Route(Routes.Extra)]")), ("Info.cs", Info));

        var (_, output, _) = AssertBlocked(broken, "DiscoveryIncomplete");

        Assert.Contains("Summary: 0 to create, 0 to update, 1 to delete, 5 unchanged", output);
        Assert.Contains("ExtraController: route template is not a string literal", output);
        Assert.DoesNotContain("[MassDeletion]", output);
    }

    [Fact]
    public void Accepting_incomplete_discovery_explicitly_lets_a_bounded_sync_through_and_keeps_it_on_record()
    {
        Sync(BaseApi());
        var broken = WriteApi("api-broken", ("Items.cs", Items), ("Extra.cs", Extra.Replace("[Route(\"api/extra\")]", "[Route(Routes.Extra)]")), ("Info.cs", Info));

        var (code, output, _) = Sync(broken, "--allow-incomplete-discovery");

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Sync decision: ALLOWED", output);
        Assert.Contains("Incomplete discovery was accepted explicitly.", output);
        Assert.Contains("  - delete  GET /api/extra/ping   Extra.Ping", output);
        Assert.Contains("Wrote collection ", output);
    }

    [Fact]
    public void Discovering_nothing_is_a_failed_analysis_that_no_flag_overrides()
    {
        Sync(BaseApi());
        var empty = WriteApi("api-empty");

        var (_, output, _) = AssertBlocked(empty, "DiscoveryFailed", "--allow-incomplete-discovery", "--max-removed", "100", "--max-removed-percent", "100");

        Assert.Contains("cannot be overridden by an option", output);
        Assert.Contains("No endpoint was discovered although the baseline has 6", output);
    }

    [Fact]
    public void Deleting_too_many_endpoints_at_once_is_blocked_until_the_limits_are_raised_on_purpose()
    {
        var api = BaseApi();
        Sync(api);
        var shrunk = WriteApi("api-shrunk", ("Items.cs", Items.Replace("[HttpPost] public IActionResult Create(CreateItem request) => Ok();", string.Empty)
            .Replace("    [HttpGet(\"{id:int}\")] public IActionResult Get(int id) => Ok();\n", string.Empty)
            .Replace("    [HttpDelete(\"{id:int}\")] public IActionResult Delete(int id) => NoContent();\n", string.Empty)), ("Extra.cs", Extra), ("Info.cs", Info));

        var (_, output, _) = AssertBlocked(shrunk, "MassDeletion");

        Assert.Contains("3 of 6 endpoint(s) (50%) would be deleted", output);
        Assert.Contains("10 endpoint(s) and 30%", output);

        var raised = Sync(shrunk, "--max-removed-percent", "50", "--max-removed", "3");
        Assert.Equal(CliApplication.Success, raised.Code);
        Assert.Contains("Summary: 0 to create, 0 to update, 3 to delete, 3 unchanged", raised.Out);
        Assert.Contains("Limits: at most 3 endpoint(s) and 50% of the baseline", raised.Out);
        using var baseline = JsonDocument.Parse(File.ReadAllText(Baseline));
        Assert.Equal(3, baseline.RootElement.GetProperty("endpoints").GetArrayLength());
    }

    [Fact]
    public void A_collection_edited_after_the_baseline_was_taken_blocks_even_when_the_source_did_not_change()
    {
        var api = BaseApi();
        Sync(api);
        File.AppendAllText(Collection, " ");

        var (_, output, _) = AssertBlocked(api, "BaselineCollectionChanged");

        Assert.Contains("would overwrite those edits", output);
    }

    [Fact]
    public void A_missing_stored_collection_blocks()
    {
        var api = BaseApi();
        Sync(api);
        var baseline = State(Baseline);
        File.Delete(Collection);

        var result = Sync(api);

        Assert.Equal(CliApplication.Blocked, result.Code);
        Assert.Contains("[BaselineCollectionMissing]", result.Out);
        Assert.False(File.Exists(Collection));
        Assert.Equal(baseline, State(Baseline));
    }

    [Fact]
    public void A_baseline_that_does_not_reference_a_collection_or_a_different_file_blocks()
    {
        var api = BaseApi();
        Directory.CreateDirectory(InWork("store"));
        Assert.Equal(0, Run("generate", api, "--snapshot", Baseline).Code);
        var baseline = State(Baseline);

        var noReference = Sync(api);
        Assert.Equal(CliApplication.Blocked, noReference.Code);
        Assert.Contains("[BaselineUnverifiable]", noReference.Out);
        Assert.Contains("does not reference a collection", noReference.Out);
        Assert.Equal(baseline, State(Baseline));
        Assert.False(File.Exists(Collection));
    }

    [Fact]
    public void Syncing_into_a_differently_named_collection_than_the_baseline_describes_blocks()
    {
        var api = BaseApi();
        Sync(api);
        var before = Stored();
        var other = InWork("store/other.postman_collection.json");

        var result = Run("sync", api, "--baseline", Baseline, "--out", other);

        Assert.Equal(CliApplication.Blocked, result.Code);
        Assert.Contains("[BaselineUnverifiable]", result.Out);
        Assert.Contains("different collection file", result.Out);
        Assert.False(File.Exists(other));
        AssertNothingWritten(before);
    }

    [Fact]
    public void Warnings_that_only_reduce_detail_do_not_block_but_are_reported()
    {
        var api = WriteApi("api-warn", ("Items.cs", Items.Replace("Create(CreateItem request)", "Create(CreateItem request, Mystery thing)")));

        var (code, output, _) = Sync(api);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Sync decision: ALLOWED", output);
        Assert.Contains("discovery warning(s) only make endpoint details less complete", output);
    }

    [Fact]
    public void An_unwritable_destination_fails_without_creating_a_baseline_that_points_at_a_missing_collection()
    {
        var api = BaseApi();
        var blocker = InWork("blocker");
        File.WriteAllText(blocker, "a file where a folder is needed");

        var result = Run("sync", api, "--baseline", Baseline, "--out", Path.Combine(blocker, "api.postman_collection.json"));

        Assert.Equal(CliApplication.Failure, result.Code);
        Assert.Contains("Cannot write the collection and baseline", result.Err);
        Assert.False(File.Exists(Baseline));
        Assert.Empty(Directory.EnumerateFiles(_work, "*.tmp", SearchOption.AllDirectories));
    }

    // ---- baseline and argument problems ----

    [Fact]
    public void An_unreadable_edited_or_incompatible_baseline_fails_and_nothing_is_written()
    {
        var api = BaseApi();
        Sync(api);
        var good = File.ReadAllText(Baseline);
        var before = Stored();

        File.WriteAllText(Baseline, "{ not json");
        var corrupt = Sync(api);
        Assert.Equal(CliApplication.Failure, corrupt.Code);
        Assert.Contains("not a valid snapshot", corrupt.Err);

        File.WriteAllText(Baseline, good.Replace("\"endpoint.action\": \"List\"", "\"endpoint.action\": \"Hacked\""));
        var edited = Sync(api);
        Assert.Equal(CliApplication.Failure, edited.Code);
        Assert.Contains("does not match its contract", edited.Err);

        File.WriteAllText(Baseline, good.Replace("\"identityVersion\": 1", "\"identityVersion\": 0"));
        var incompatible = Sync(api);
        Assert.Equal(CliApplication.Failure, incompatible.Code);
        Assert.Contains("new baseline", incompatible.Err);

        Assert.Equal(before.Collection, State(Collection));
    }

    [Theory]
    [InlineData("--max-removed-percent", "abc")]
    [InlineData("--max-removed-percent", "-1")]
    [InlineData("--max-removed-percent", "101")]
    [InlineData("--max-removed", "-3")]
    [InlineData("--max-removed", "many")]
    public void Invalid_limits_are_rejected_before_anything_runs(string option, string value)
    {
        var (code, output, error) = Sync(BaseApi(), option, value);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains($"{option} must be", error);
        Assert.Equal("", output);
        Assert.False(File.Exists(Collection));
    }

    [Theory]
    [InlineData("sync", "{dir}", "--out", "c.json")]
    [InlineData("sync", "{dir}", "--baseline", "b.json")]
    [InlineData("sync", "{dir}", "--baseline", "b.json", "--out", "c.json", "--strict")]
    [InlineData("sync", "{dir}", "--baseline", "b.json", "--out", "c.json", "--exit-code")]
    [InlineData("sync", "{dir}", "--baseline", "b.json", "--out", "c.json", "--snapshot", "s.json")]
    [InlineData("sync", "{dir}", "--baseline", "b.json", "--out", "c.json", "--dry-run", "--dry-run")]
    [InlineData("sync", "{dir}", "--baseline", "b.json", "--out", "c.json", "--max-removed")]
    [InlineData("generate", "{dir}", "--dry-run")]
    [InlineData("diff", "{dir}", "--baseline", "b.json", "--dry-run")]
    public void Sync_and_the_other_commands_reject_options_that_belong_elsewhere(params string[] args)
    {
        var (code, output, error) = Run(args.Select(a => a == "{dir}" ? SampleApi : a).ToArray());

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("Usage:", error);
        Assert.Equal("", output);
    }

    [Fact]
    public void Sync_never_writes_inside_the_analyzed_api_or_over_one_file_twice()
    {
        var api = BaseApi();

        var baselineInside = Run("sync", api, "--baseline", Path.Combine(api, "baseline.json"), "--out", Collection);
        Assert.Equal(CliApplication.Failure, baselineInside.Code);
        Assert.Contains("Refusing to write --baseline", baselineInside.Err);

        var outInside = Run("sync", api, "--baseline", Baseline, "--out", Path.Combine(api, "collection.json"));
        Assert.Equal(CliApplication.Failure, outInside.Code);
        Assert.Contains("Refusing to write --out", outInside.Err);

        var planInside = Sync(api, "--plan", Path.Combine(api, "plan.json"));
        Assert.Equal(CliApplication.Failure, planInside.Code);
        Assert.Contains("Refusing to write --plan", planInside.Err);

        var same = Run("sync", api, "--baseline", Baseline, "--out", Baseline);
        Assert.Equal(CliApplication.Failure, same.Code);
        Assert.Contains("--out and --baseline must be different files", same.Err);

        Assert.False(File.Exists(Baseline));
        Assert.False(File.Exists(Collection));
        Assert.Equal(["Extra.cs", "Info.cs", "Items.cs"], Directory.EnumerateFiles(api).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Sync_output_is_identical_across_equivalent_runs()
    {
        var first = Sync(BaseApi("api-a"), "--dry-run");
        var second = Sync(BaseApi("api-b"), "--dry-run");

        Assert.Equal(first.Out, second.Out);
    }
}
