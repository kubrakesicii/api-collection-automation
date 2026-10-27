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
}
