using System.Security.Cryptography;
using System.Text.Json;
using ApiCollection.Cli;

namespace ApiCollection.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ApiCollection.CliTests." + Guid.NewGuid().ToString("N"));

    public CliApplicationTests() => Directory.CreateDirectory(_work);

    public void Dispose() => Directory.Delete(_work, recursive: true);

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

    private static string SampleApi => Path.Combine(RepositoryRoot(), "sample-api", "SampleApi");

    private static string Examples => Path.Combine(RepositoryRoot(), "config", "examples.sample-api.json");

    private string InWork(string name) => Path.Combine(_work, name);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    // ---- the single command from the task ----

    [Fact]
    public void One_command_writes_the_collection_and_the_discovery_report_without_running_the_api()
    {
        var collection = InWork("collection.json");
        var report = InWork("report.json");

        var (code, output, error) = Run("generate", SampleApi, "--examples", Examples, "--name", "Sample API", "--out", collection, "--report", report);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains($"Wrote collection {collection}", output);
        Assert.Contains($"Wrote report {report}", output);
        Assert.Contains("15 endpoint(s) in 3 folder(s)", error);

        using var collectionJson = JsonDocument.Parse(File.ReadAllText(collection));
        Assert.Equal("Sample API", collectionJson.RootElement.GetProperty("info").GetProperty("name").GetString());
        var requests = collectionJson.RootElement.GetProperty("item").EnumerateArray().SelectMany(f => f.GetProperty("item").EnumerateArray());
        Assert.Equal(15, requests.Count());

        using var reportJson = JsonDocument.Parse(File.ReadAllText(report));
        var summary = reportJson.RootElement.GetProperty("summary");
        Assert.Equal(15, summary.GetProperty("endpoints").GetInt32());
        Assert.Equal(3, summary.GetProperty("controllers").GetInt32());
        Assert.Equal(15, reportJson.RootElement.GetProperty("endpoints").GetArrayLength());
        var create = reportJson.RootElement.GetProperty("endpoints").EnumerateArray()
            .Single(e => e.GetProperty("controllerName").GetString() == "Products" && e.GetProperty("actionName").GetString() == "Create");
        Assert.Equal("Post", create.GetProperty("httpMethod").GetString());
        Assert.Equal("api/Products", create.GetProperty("routeTemplate").GetString());
        Assert.Equal("CreateProductRequest", create.GetProperty("requestBody").GetProperty("typeName").GetString());
        Assert.True(reportJson.RootElement.GetProperty("exampleDiagnostics").GetArrayLength() > 0);
    }

    [Fact]
    public void Report_extension_selects_text_or_json_and_missing_directories_are_created()
    {
        var text = InWork(Path.Combine("nested", "dir", "report.txt"));

        var (code, _, _) = Run("generate", SampleApi, "--out", InWork("c.json"), "--report", text);

        Assert.Equal(CliApplication.Success, code);
        var content = File.ReadAllText(text);
        Assert.Contains("POST    /api/Products", content);
        Assert.Contains("15 endpoint(s), 0 warning(s)", content);
        Assert.DoesNotContain('\r', content);
        Assert.False(content.TrimStart().StartsWith('{'));
    }

    [Fact]
    public void Without_out_the_collection_goes_to_stdout_and_status_to_stderr()
    {
        var (code, output, error) = Run("generate", SampleApi);

        Assert.Equal(CliApplication.Success, code);
        using var document = JsonDocument.Parse(output);
        Assert.Equal("API Collection", document.RootElement.GetProperty("info").GetProperty("name").GetString());
        Assert.Contains("15 endpoint(s)", error);
        Assert.DoesNotContain("Wrote", output);
    }

    [Fact]
    public void Output_files_are_identical_across_runs()
    {
        string[] Generate(string suffix)
        {
            var collection = InWork($"c{suffix}.json");
            var report = InWork($"r{suffix}.json");
            Assert.Equal(0, Run("generate", SampleApi, "--examples", Examples, "--out", collection, "--report", report).Code);
            return [File.ReadAllText(collection), File.ReadAllText(report)];
        }

        Assert.Equal(Generate("1"), Generate("2"));
    }

    [Fact]
    public void The_analyzed_api_directory_is_not_modified()
    {
        var before = Snapshot(SampleApi);

        Run("generate", SampleApi, "--examples", Examples, "--out", InWork("c.json"), "--report", InWork("r.json"));
        Run("discover", SampleApi);

        Assert.Equal(before, Snapshot(SampleApi));
    }

    private static List<string> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .ToList();

    // ---- safety and argument handling ----

    [Fact]
    public void Writing_inside_the_analyzed_api_directory_is_refused()
    {
        var api = InWork("api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "C.cs"), "[Route(\"x\")] public class XController : ControllerBase { [HttpGet] public object G() => 1; }");

        var inside = Path.Combine(api, "collection.json");
        var (code, _, error) = Run("generate", api, "--out", inside);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("Refusing to write --out", error);
        Assert.False(File.Exists(inside));

        var (reportCode, _, reportError) = Run("generate", api, "--out", InWork("ok.json"), "--report", Path.Combine(api, "sub", "r.txt"));
        Assert.Equal(CliApplication.Failure, reportCode);
        Assert.Contains("Refusing to write --report", reportError);
        Assert.False(File.Exists(InWork("ok.json")));
    }

    [Fact]
    public void A_sibling_directory_with_a_shared_name_prefix_is_not_treated_as_inside()
    {
        var api = InWork("api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "C.cs"), "[Route(\"x\")] public class XController : ControllerBase { [HttpGet] public object G() => 1; }");
        var sibling = InWork("api-output.json");

        var (code, _, _) = Run("generate", api, "--out", sibling);

        Assert.Equal(CliApplication.Success, code);
        Assert.True(File.Exists(sibling));
    }

    [Theory]
    [InlineData("generate")]
    [InlineData("frobnicate", "x")]
    [InlineData("generate", "{dir}", "--unknown", "x")]
    [InlineData("generate", "{dir}", "--out")]
    [InlineData("generate", "{dir}", "--out", "a.json", "--out", "b.json")]
    [InlineData("generate", "{dir}", "--strict", "--strict")]
    [InlineData("discover", "{dir}", "--out", "a.json")]
    [InlineData("discover", "{dir}", "--strict")]
    public void Invalid_arguments_print_usage_and_fail(params string[] args)
    {
        var resolved = args.Select(a => a == "{dir}" ? SampleApi : a).ToArray();

        var (code, output, error) = Run(resolved);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("Usage:", error);
        Assert.Equal("", output);
    }

    [Fact]
    public void Missing_directory_bad_metadata_and_identical_output_paths_fail_clearly()
    {
        var missing = Run("generate", InWork("nope"));
        Assert.Equal(CliApplication.Failure, missing.Code);
        Assert.Contains("Directory not found", missing.Err);

        var metadata = InWork("bad.json");
        File.WriteAllText(metadata, "{ not json");
        var bad = Run("generate", SampleApi, "--examples", metadata);
        Assert.Equal(CliApplication.Failure, bad.Code);
        Assert.Contains("Cannot read example metadata", bad.Err);

        var same = InWork("same.json");
        var duplicate = Run("generate", SampleApi, "--out", same, "--report", same);
        Assert.Equal(CliApplication.Failure, duplicate.Code);
        Assert.Contains("must be different files", duplicate.Err);
        Assert.False(File.Exists(same));
    }

    // ---- strict mode ----

    private string ApiWithUnresolvableRoute()
    {
        var api = InWork("strict-api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "C.cs"), """
            [Route(Routes.Base)]
            public class BadController : ControllerBase { [HttpGet] public object G() => 1; }
            [Route("api/[controller]")]
            public class GoodController : ControllerBase { [HttpGet] public object G() => 1; }
            """);
        return api;
    }

    [Fact]
    public void Strict_fails_with_code_2_on_warnings_but_still_writes_the_files()
    {
        var api = ApiWithUnresolvableRoute();
        var collection = InWork("strict-c.json");
        var report = InWork("strict-r.json");

        var (code, _, error) = Run("generate", api, "--out", collection, "--report", report, "--strict");

        Assert.Equal(CliApplication.StrictViolation, code);
        Assert.Contains("Strict mode: discovery reported 1 warning(s)", error);
        Assert.Contains("BadController: route template is not a string literal", error);
        Assert.True(File.Exists(collection));
        Assert.True(File.Exists(report));
    }

    [Fact]
    public void Without_strict_the_same_warnings_do_not_change_the_exit_code()
    {
        var (code, _, error) = Run("generate", ApiWithUnresolvableRoute(), "--out", InWork("lenient.json"));

        Assert.Equal(CliApplication.Success, code);
        Assert.DoesNotContain("Strict mode", error);
    }

    [Fact]
    public void Strict_passes_for_the_sample_api_because_missing_examples_are_not_warnings()
    {
        var (code, _, error) = Run("generate", SampleApi, "--out", InWork("c.json"), "--strict");

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("0 warning(s)", error);
        Assert.Contains("example diagnostic(s)", error);
    }

    [Fact]
    public void Discover_still_prints_the_report_to_stdout()
    {
        var (code, output, _) = Run("discover", SampleApi, "--examples", Examples);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("PUT     /api/Orders/{id:int}", output);
        Assert.Contains("15 endpoint(s), 0 warning(s)", output);
        Assert.Contains("source Name <- ExternalMetadata", output);
    }

    // ---- snapshot ----

    [Fact]
    public void Snapshot_is_written_next_to_the_collection_and_references_its_hash()
    {
        var collection = InWork(Path.Combine("out", "sample.postman_collection.json"));
        var snapshot = InWork(Path.Combine("out", "sample.snapshot.json"));

        var (code, output, _) = Run("generate", SampleApi, "--examples", Examples, "--out", collection, "--snapshot", snapshot);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains($"Wrote snapshot {snapshot}", output);

        using var json = JsonDocument.Parse(File.ReadAllText(snapshot));
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(15, root.GetProperty("endpoints").GetArrayLength());
        Assert.Equal("sample.postman_collection.json", root.GetProperty("collection").GetProperty("fileName").GetString());
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(collection))).ToLowerInvariant();
        Assert.Equal(expectedHash, root.GetProperty("collection").GetProperty("sha256").GetString());
    }

    [Fact]
    public void Snapshot_output_is_identical_across_runs_and_without_out_has_no_collection_reference()
    {
        var first = InWork("s1.json");
        var second = InWork("s2.json");
        Assert.Equal(0, Run("generate", SampleApi, "--snapshot", first).Code);
        Assert.Equal(0, Run("generate", SampleApi, "--snapshot", second).Code);

        Assert.Equal(File.ReadAllText(first), File.ReadAllText(second));
        using var json = JsonDocument.Parse(File.ReadAllText(first));
        Assert.False(json.RootElement.TryGetProperty("collection", out _));
    }

    [Fact]
    public void The_cli_reproduces_the_committed_baseline_snapshot()
    {
        var collection = InWork("sample-api.postman_collection.json");
        var snapshot = InWork("sample-api.snapshot.json");

        var (code, _, _) = Run("generate", SampleApi, "--examples", Examples, "--name", "Sample API", "--out", collection, "--snapshot", snapshot);

        Assert.Equal(CliApplication.Success, code);
        var fixtures = Path.Combine(RepositoryRoot(), "fixtures", "postman");
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "sample-api.snapshot.json")).Replace("\r\n", "\n"), File.ReadAllText(snapshot));
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "sample-api.postman_collection.json")).Replace("\r\n", "\n"), File.ReadAllText(collection));
    }

    [Fact]
    public void Snapshot_must_not_be_written_inside_the_api_or_over_another_output()
    {
        var api = InWork("api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "C.cs"), "[Route(\"x\")] public class XController : ControllerBase { [HttpGet] public object G() => 1; }");

        var inside = Run("generate", api, "--snapshot", Path.Combine(api, "snapshot.json"));
        Assert.Equal(CliApplication.Failure, inside.Code);
        Assert.Contains("Refusing to write --snapshot", inside.Err);

        var same = InWork("same.json");
        var clash = Run("generate", api, "--out", same, "--snapshot", same);
        Assert.Equal(CliApplication.Failure, clash.Code);
        Assert.Contains("--out and --snapshot must be different files", clash.Err);
        Assert.False(File.Exists(same));
    }

    // ---- diff ----

    private const string ItemsApi = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpGet("{id:int}")] public IActionResult Get(int id) => Ok();
            [HttpGet("ping")] public IActionResult Ping() => Ok();
            [HttpPost] public IActionResult Create(CreateItem request) => Ok();
            [HttpDelete("{id:int}")] public IActionResult Delete(int id) => NoContent();
        }
        public class CreateItem { public string Name { get; set; } = ""; }
        """;

    private const string EvolvedItemsApi = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [Authorize] [HttpGet] public IActionResult List() => Ok();
            [HttpGet("{id:int}")] public IActionResult Get(int id, string? expand) => Ok();
            [HttpGet("ping")] public IActionResult Ping() => Ok();
            [HttpGet("count")] public IActionResult Count() => Ok();
            [HttpPost] public IActionResult Create(CreateItem request) => Ok();
        }
        public class CreateItem { public string Name { get; set; } = ""; public decimal Price { get; set; } }
        """;

    private string WriteApi(string name, string source)
    {
        var api = InWork(name);
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Items.cs"), source);
        return api;
    }

    /// <summary>Creates a baseline (collection + snapshot) for the given API in its own folder and returns the snapshot path.</summary>
    private string Baseline(string api)
    {
        var folder = InWork("baseline-" + Path.GetFileName(api));
        var snapshot = Path.Combine(folder, "api.snapshot.json");
        Assert.Equal(0, Run("generate", api, "--out", Path.Combine(folder, "api.postman_collection.json"), "--snapshot", snapshot).Code);
        return snapshot;
    }

    [Fact]
    public void Diff_of_an_unchanged_api_reports_no_changes_and_exit_code_stays_zero()
    {
        var baseline = Path.Combine(RepositoryRoot(), "fixtures", "postman", "sample-api.snapshot.json");

        var (code, output, error) = Run("diff", SampleApi, "--baseline", baseline, "--examples", Examples, "--exit-code");

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Summary: 0 added, 0 modified, 0 removed, 15 unchanged", output);
        Assert.Contains("No changes.", output);
        Assert.DoesNotContain("Warnings", output);
        Assert.DoesNotContain("Added (", output);
        Assert.Equal("", error);
    }

    [Fact]
    public void Diff_reports_added_modified_removed_and_unchanged_endpoints_and_writes_both_outputs()
    {
        var baseline = Baseline(WriteApi("items-v1", ItemsApi));
        var evolved = WriteApi("items-v2", EvolvedItemsApi);
        var changes = InWork("out/changes.json");
        var report = InWork("out/diff.txt");

        var (code, output, error) = Run("diff", evolved, "--baseline", baseline, "--changes", changes, "--report", report, "--exit-code");

        Assert.Equal(CliApplication.ChangesDetected, code);
        Assert.Contains("Summary: 1 added, 3 modified, 1 removed, 1 unchanged", output);
        Assert.Contains("Added (1)", output);
        Assert.Contains("  + GET /api/items/count   Items.Count", output);
        Assert.Contains("Modified (3)", output);
        Assert.Contains("  ~ GET /api/items/{:int}   Items.Get", output);
        Assert.Contains("Parameter      Added    parameter[query:expand]: required=false; type=string?", output);
        Assert.Contains("Authentication Changed  auth.required: false -> true", output);
        Assert.Contains("body.property[Price]", output);
        Assert.Contains("Removed (1)", output);
        Assert.Contains("  - DELETE /api/items/{:int}   Items.Delete", output);
        Assert.Contains("Unchanged (1)", output);
        Assert.Contains("  = GET /api/items/ping   Items.Ping", output);
        Assert.Contains($"Wrote report {report}", error);
        Assert.Contains($"Wrote change set {changes}", error);

        Assert.Equal(output, File.ReadAllText(report));
        using var json = JsonDocument.Parse(File.ReadAllText(changes));
        var root = json.RootElement;
        Assert.True(root.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("added").GetInt32());
        Assert.Equal(3, root.GetProperty("summary").GetProperty("modified").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("removed").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("unchanged").GetInt32());
        var statuses = root.GetProperty("endpoints").EnumerateArray().ToDictionary(
            e => e.GetProperty("actionName").GetString()!, e => e.GetProperty("status").GetString());
        Assert.Equal("Added", statuses["Count"]);
        Assert.Equal("Removed", statuses["Delete"]);
        Assert.Equal("Unchanged", statuses["Ping"]);
        Assert.Equal("Modified", statuses["List"]);
    }

    [Fact]
    public void Diff_without_exit_code_succeeds_even_when_there_are_changes()
    {
        var baseline = Baseline(WriteApi("items-v1", ItemsApi));

        var (code, output, _) = Run("diff", WriteApi("items-v2", EvolvedItemsApi), "--baseline", baseline);

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("1 added, 3 modified, 1 removed", output);
    }

    [Fact]
    public void Diff_output_is_identical_across_runs()
    {
        var baseline = Baseline(WriteApi("items-v1", ItemsApi));
        var evolved = WriteApi("items-v2", EvolvedItemsApi);

        var first = Run("diff", evolved, "--baseline", baseline, "--changes", InWork("c1.json"));
        var second = Run("diff", evolved, "--baseline", baseline, "--changes", InWork("c2.json"));

        Assert.Equal(first.Out, second.Out);
        Assert.Equal(File.ReadAllText(InWork("c1.json")), File.ReadAllText(InWork("c2.json")));
    }

    [Fact]
    public void Diff_never_overwrites_the_baseline_and_keeps_its_outputs_out_of_the_api()
    {
        var api = WriteApi("items-v1", ItemsApi);
        var baseline = Baseline(api);
        var before = File.ReadAllText(baseline);

        var overwrite = Run("diff", api, "--baseline", baseline, "--changes", baseline);
        Assert.Equal(CliApplication.Failure, overwrite.Code);
        Assert.Contains("--changes and --baseline must be different files", overwrite.Err);
        Assert.Equal(before, File.ReadAllText(baseline));

        var inside = Run("diff", api, "--baseline", baseline, "--report", Path.Combine(api, "diff.txt"));
        Assert.Equal(CliApplication.Failure, inside.Code);
        Assert.Contains("Refusing to write --report", inside.Err);
        Assert.False(File.Exists(Path.Combine(api, "diff.txt")));
    }

    [Fact]
    public void Diff_fails_clearly_for_a_missing_corrupt_edited_or_incompatible_baseline()
    {
        var api = WriteApi("items-v1", ItemsApi);

        var missing = Run("diff", api, "--baseline", InWork("nope.json"));
        Assert.Equal(CliApplication.Failure, missing.Code);
        Assert.Contains("Cannot read the baseline snapshot", missing.Err);

        var corrupt = InWork("corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        Assert.Contains("not a valid snapshot", Run("diff", api, "--baseline", corrupt).Err);

        var good = Baseline(api);
        var edited = InWork("edited.json");
        File.WriteAllText(edited, File.ReadAllText(good).Replace("\"endpoint.action\": \"List\"", "\"endpoint.action\": \"Hacked\""));
        var tampered = Run("diff", api, "--baseline", edited);
        Assert.Equal(CliApplication.Failure, tampered.Code);
        Assert.Contains("does not match its contract", tampered.Err);

        var incompatible = InWork("old-rules.json");
        File.WriteAllText(incompatible, File.ReadAllText(good).Replace("\"identityVersion\": 1", "\"identityVersion\": 0"));
        var versions = Run("diff", api, "--baseline", incompatible);
        Assert.Equal(CliApplication.Failure, versions.Code);
        Assert.Contains("new baseline", versions.Err);
    }

    [Fact]
    public void Diff_warns_when_the_baseline_collection_was_changed_or_is_missing()
    {
        var api = WriteApi("items-v1", ItemsApi);
        var baseline = Baseline(api);
        var collection = Path.Combine(Path.GetDirectoryName(baseline)!, "api.postman_collection.json");

        File.AppendAllText(collection, " ");
        var changed = Run("diff", api, "--baseline", baseline);
        Assert.Equal(CliApplication.Success, changed.Code);
        Assert.Contains("differs from the one the snapshot was taken with", changed.Out);
        Assert.Contains("No changes.", changed.Out);

        File.Delete(collection);
        var missing = Run("diff", api, "--baseline", baseline);
        Assert.Contains("was not found next to the snapshot", missing.Out);
    }

    [Fact]
    public void Diff_carries_discovery_warnings_so_a_removal_can_be_distrusted()
    {
        var api = WriteApi("items-v1", ItemsApi);
        var baseline = Baseline(api);
        File.WriteAllText(Path.Combine(api, "Items.cs"), ItemsApi.Replace("""[Route("api/[controller]")]""", "[Route(Routes.Base)]"));

        var (_, output, _) = Run("diff", api, "--baseline", baseline);

        Assert.Contains("Summary: 0 added, 0 modified, 5 removed, 0 unchanged", output);
        Assert.Contains("WARN Discovery: ItemsController: route template is not a string literal", output);
    }

    [Theory]
    [InlineData("diff", "{dir}")]
    [InlineData("diff", "{dir}", "--out", "x.json")]
    [InlineData("diff", "{dir}", "--baseline")]
    [InlineData("diff", "{dir}", "--baseline", "a.json", "--strict")]
    [InlineData("diff", "{dir}", "--baseline", "a.json", "--exit-code", "--exit-code")]
    [InlineData("generate", "{dir}", "--exit-code")]
    [InlineData("generate", "{dir}", "--baseline", "a.json")]
    public void Diff_and_generate_reject_options_that_belong_to_the_other_command(params string[] args)
    {
        var (code, output, error) = Run(args.Select(a => a == "{dir}" ? SampleApi : a).ToArray());

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("Usage:", error);
        Assert.Equal("", output);
    }

    [Fact]
    public void Duplicate_endpoint_identities_fail_the_run_before_any_file_is_written()
    {
        var api = InWork("dup-api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "C.cs"), """
            [Route("api/x")]
            public class XController : ControllerBase
            {
                [HttpGet("{id:int}")] public object ById(int id) => 1;
                [HttpGet("{count:int}")] public object ByCount(int count) => 1;
            }
            """);
        var collection = InWork("dup-collection.json");
        var snapshot = InWork("dup-snapshot.json");

        var (code, _, error) = Run("generate", api, "--out", collection, "--snapshot", snapshot);

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("Cannot create the snapshot", error);
        Assert.Contains("GET /api/x/{:int}", error);
        Assert.False(File.Exists(collection));
        Assert.False(File.Exists(snapshot));
    }
}
