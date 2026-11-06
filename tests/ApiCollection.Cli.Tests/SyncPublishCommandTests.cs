using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ApiCollection.Cli;
using ApiCollection.Postman.CloudApi;
using ApiCollection.Postman.Tests;

namespace ApiCollection.Cli.Tests;

/// <summary>
/// <c>sync --publish</c> end to end against an in-memory Postman: the four scenarios of the local E2E test (added, modified, removed,
/// unchanged) and what happens when the publish goes wrong. No test touches the network, the real environment or the user secrets.
/// </summary>
public sealed class SyncPublishCommandTests : IDisposable
{
    private const string Workspace = "00000000-aaaa-bbbb-cccc-000000000001";
    private const string CollectionName = "Sample API";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "ApiCollection.PublishCli." + Guid.NewGuid().ToString("N"));

    public SyncPublishCommandTests() => Directory.CreateDirectory(_work);

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string InWork(string name) => Path.Combine(_work, name);

    private string Collection => InWork("store/api.postman_collection.json");

    private string Baseline => InWork("store/api.snapshot.json");

    private string Backups => InWork("store/backups");

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

    private const string Orders = """
        [ApiController]
        [Route("api/orders")]
        public class OrdersController : ControllerBase { [HttpGet] public IActionResult All() => Ok(); }
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

    private string V1() => WriteApi("api-v1", ("Items.cs", Items), ("Extra.cs", Extra), ("Info.cs", Info));

    private string Added() => WriteApi("api-added", ("Items.cs", Items), ("Extra.cs", Extra), ("Info.cs", Info), ("Orders.cs", Orders));

    private string Modified() => WriteApi(
        "api-modified",
        ("Items.cs", Items.Replace("public string Name { get; set; } = \"\"; }", "public string Name { get; set; } = \"\"; public decimal Price { get; set; } }")),
        ("Extra.cs", Extra), ("Info.cs", Info));

    private string Removed() => WriteApi("api-removed", ("Items.cs", Items), ("Extra.cs", Extra));

    private static FakePostmanCloud Healthy()
    {
        var fake = new FakePostmanCloud();
        fake.Workspaces.Add(Workspace);
        return fake;
    }

    private static CliEnvironment Env(FakePostmanCloud fake, string? key = FakePostmanCloud.ApiKey, string? workspace = Workspace) => new()
    {
        GetVariable = name => name switch { "POSTMAN_API_KEY" => key, "POSTMAN_WORKSPACE_ID" => workspace, _ => null },
        GetUserSecret = _ => null,
        CreateHttpClient = () => PostmanHttpClientFactory.Create(fake)
    };

    private (int Code, string Out, string Err) Publish(FakePostmanCloud fake, string api, params string[] extra)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliApplication.Run(
            [.. new[] { "sync", api, "--baseline", Baseline, "--out", Collection, "--name", CollectionName, "--publish" }, .. extra],
            output, error, Env(fake));
        return (code, output.ToString(), error.ToString());
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static int Requests(FakePostmanCloud fake) =>
        fake.Collections[0].Collection["item"]!.AsArray().Sum(folder => folder!["item"]!.AsArray().Count);

    private int BackupCount() => Directory.Exists(Backups) ? Directory.GetFiles(Backups).Length : 0;

    private (string Collection, string Baseline) Stored() => (Hash(Collection), Hash(Baseline));

    private void CorruptNextPut(FakePostmanCloud fake)
    {
        var done = false;
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method == HttpMethod.Put && !done)
            {
                done = true;
                fake.Collections[0].Collection["item"]![0]!["item"]!.AsArray().RemoveAt(0);
            }
        };
    }

    /// <summary>Runs the first publish and forgets its calls, so a test sees only what the next run does.</summary>
    private FakePostmanCloud Published()
    {
        var fake = Healthy();
        var (code, _, err) = Publish(fake, V1());
        Assert.True(code == CliApplication.Success, err);
        fake.Calls.Clear();
        return fake;
    }

    // ---- the four scenarios ----

    [Fact]
    public void The_first_publish_creates_the_collection_in_postman_and_then_the_local_files()
    {
        var fake = Healthy();

        var (code, output, error) = Publish(fake, V1());

        Assert.Equal(CliApplication.Success, code);
        Assert.Equal(string.Empty, error);
        Assert.Contains("Postman sync: SYNCED", output);
        Assert.Contains("Wrote collection ", output);
        Assert.Single(fake.Collections);
        Assert.Equal(6, Requests(fake));
        Assert.Single(fake.Writes);
        Assert.Equal(HttpMethod.Post, fake.Writes.Single().Method);
        Assert.Contains($"workspace={Workspace}", fake.Writes.Single().PathAndQuery);
        Assert.True(File.Exists(Collection) && File.Exists(Baseline));
        Assert.Equal(0, BackupCount());
        Assert.Contains("automationManaged", File.ReadAllText(Collection));
    }

    [Fact]
    public void Unchanged_reads_postman_but_writes_nothing_anywhere()
    {
        var fake = Published();
        var before = Stored();
        var collectionTime = File.GetLastWriteTimeUtc(Collection);

        var (code, output, _) = Publish(fake, V1());

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Sync decision: NO CHANGES", output);
        Assert.Contains("Postman sync: NO CHANGES", output);
        Assert.Empty(fake.Writes);
        Assert.All(fake.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
        Assert.Equal(before, Stored());
        Assert.Equal(collectionTime, File.GetLastWriteTimeUtc(Collection));
        Assert.Equal(0, BackupCount());
    }

    [Fact]
    public void An_added_endpoint_is_backed_up_published_verified_and_then_recorded_locally()
    {
        var fake = Published();
        var before = Stored();

        var (code, output, _) = Publish(fake, Added());

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Summary: 1 to create, 0 to update, 0 to delete, 6 unchanged", output);
        Assert.Contains("Postman sync: SYNCED", output);
        Assert.Equal(7, Requests(fake));
        Assert.Equal(1, BackupCount());
        Assert.Equal(HttpMethod.Put, Assert.Single(fake.Writes).Method);
        Assert.Single(fake.Collections);
        Assert.NotEqual(before, Stored());
        Assert.Contains("/api/orders", File.ReadAllText(Collection));
    }

    [Fact]
    public void A_modified_endpoint_updates_the_same_collection_in_place()
    {
        var fake = Published();
        var uid = fake.Collections[0].Uid;

        var (code, output, _) = Publish(fake, Modified());

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("0 to create, 1 to update, 0 to delete", output);
        Assert.Contains("Postman sync: SYNCED", output);
        Assert.Contains(uid, output);
        Assert.Contains("price", fake.Collections[0].Collection.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, Requests(fake));
        Assert.Single(fake.Collections);
        Assert.Equal(1, BackupCount());
    }

    [Fact]
    public void A_removed_endpoint_disappears_from_the_collection_and_the_old_state_stays_in_the_backup()
    {
        var fake = Published();

        var (code, output, _) = Publish(fake, Removed());

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("0 to create, 0 to update, 1 to delete", output);
        Assert.Equal(5, Requests(fake));
        Assert.DoesNotContain("/api/info/version", fake.Collections[0].Collection.ToJsonString());
        var backup = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(Backups).Single()))!;
        Assert.Contains("version", backup["collection"]!.ToJsonString());
        Assert.DoesNotContain(fake.Calls, call => call.Method == HttpMethod.Delete);
    }

    [Fact]
    public void A_whole_cycle_added_modified_removed_unchanged_ends_consistent_with_the_source()
    {
        var withQty = Items.Replace("public string Name { get; set; } = \"\"; }", "public string Name { get; set; } = \"\"; public int Qty { get; set; } }");
        var fake = Healthy();

        Assert.Equal(CliApplication.Success, Publish(fake, V1()).Code);
        Assert.Equal(CliApplication.Success, Publish(fake, Added()).Code);
        Assert.Equal(7, Requests(fake));
        Assert.Equal(CliApplication.Success, Publish(fake, WriteApi("api-m", ("Items.cs", withQty), ("Extra.cs", Extra), ("Info.cs", Info), ("Orders.cs", Orders))).Code);
        var final = WriteApi("api-r", ("Items.cs", withQty), ("Extra.cs", Extra), ("Orders.cs", Orders));
        Assert.Equal(CliApplication.Success, Publish(fake, final).Code);
        Assert.Equal(6, Requests(fake));

        fake.Calls.Clear();
        Assert.Equal(CliApplication.Success, Publish(fake, final).Code);
        Assert.Empty(fake.Writes);
        Assert.Single(fake.Collections);
    }

    // ---- when the publish goes wrong, the local state does not move ----

    [Fact]
    public void A_publish_that_does_not_verify_is_rolled_back_and_the_local_baseline_is_left_alone()
    {
        var fake = Published();
        var before = Stored();
        var postman = fake.Collections[0].Collection.ToJsonString();
        CorruptNextPut(fake);

        var (code, output, _) = Publish(fake, Added());

        Assert.Equal(CliApplication.NotSynced, code);
        Assert.Contains("Postman sync: ROLLED BACK", output);
        Assert.Contains("Remote state: Restored", output);
        Assert.Equal(before, Stored());
        Assert.Equal(6, Requests(fake));
        Assert.DoesNotContain("Wrote baseline", output);
        Assert.Equal(1, BackupCount());
        Assert.NotEqual(string.Empty, postman);
    }

    [Fact]
    public void A_rollback_that_fails_too_exits_with_the_attention_code_and_prints_how_to_recover()
    {
        var fake = Published();
        var before = Stored();
        var puts = 0;
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method == HttpMethod.Put && ++puts >= 1)
            {
                fake.Collections[0].Collection["item"]![0]!["item"]!.AsArray().RemoveAt(0);
            }
        };

        var (code, output, _) = Publish(fake, Added());

        Assert.Equal(CliApplication.NeedsAttention, code);
        Assert.Contains("Postman sync: ROLLBACK FAILED", output);
        Assert.Contains("Recovery", output);
        Assert.Equal(before, Stored());
    }

    [Fact]
    public void A_publish_postman_refuses_leaves_everything_as_it_was_and_is_not_an_attention_case()
    {
        var fake = Published();
        var before = Stored();
        fake.Intercept = call => call.Method == HttpMethod.Put ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "try later") : null;

        var (code, output, _) = Publish(fake, Added());

        Assert.Equal(CliApplication.NotSynced, code);
        Assert.Contains("Postman sync: FAILED", output);
        Assert.Equal(before, Stored());
    }

    [Fact]
    public void When_the_backup_cannot_be_stored_nothing_is_written_to_postman()
    {
        var fake = Published();
        var before = Stored();
        var blocker = InWork("a-file");
        File.WriteAllText(blocker, "not a folder");

        var (code, output, _) = Publish(fake, Added(), "--backup-dir", Path.Combine(blocker, "backups"));

        Assert.Equal(CliApplication.NotSynced, code);
        Assert.Contains("backup could not be stored", output);
        Assert.Empty(fake.Writes);
        Assert.Equal(before, Stored());
    }

    [Fact]
    public void A_no_rollback_run_leaves_a_failed_publish_in_place_and_says_so()
    {
        var fake = Published();
        CorruptNextPut(fake);

        var (code, output, _) = Publish(fake, Added(), "--no-rollback");

        Assert.Equal(CliApplication.NeedsAttention, code);
        Assert.Contains("Postman sync: VERIFICATION FAILED", output);
        Assert.Single(fake.Writes);
    }

    [Fact]
    public void A_safety_gate_block_stops_before_postman_is_contacted()
    {
        var fake = Published();

        var (code, output, _) = Publish(fake, WriteApi("api-gone", ("Extra.cs", Extra)), "--max-removed", "1");

        Assert.Equal(CliApplication.Blocked, code);
        Assert.Contains("BLOCKED", output);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void A_dry_run_only_reads_from_postman_and_writes_no_file()
    {
        var fake = Published();
        var before = Stored();

        var (code, output, _) = Publish(fake, Added(), "--dry-run");

        Assert.Equal(CliApplication.Success, code);
        Assert.Contains("Postman sync: DRY RUN", output);
        Assert.Empty(fake.Writes);
        Assert.Equal(before, Stored());
        Assert.Equal(0, BackupCount());
    }

    // ---- configuration and secrets ----

    [Fact]
    public void Without_a_key_or_workspace_nothing_is_sent_and_the_user_is_told_what_to_set()
    {
        var fake = Healthy();
        var api = V1();

        var output = new StringWriter();
        var error = new StringWriter();
        var noKey = CliApplication.Run(["sync", api, "--baseline", Baseline, "--out", Collection, "--publish"], output, error, Env(fake, key: null));
        Assert.Equal(CliApplication.Failure, noKey);
        Assert.Contains("No Postman API key", error.ToString());
        Assert.Contains("POSTMAN_API_KEY", error.ToString());

        error = new StringWriter();
        var noWorkspace = CliApplication.Run(["sync", api, "--baseline", Baseline, "--out", Collection, "--publish"], output, error, Env(fake, workspace: null));
        Assert.Equal(CliApplication.Failure, noWorkspace);
        Assert.Contains("No Postman workspace id", error.ToString());

        Assert.Empty(fake.Calls);
        Assert.False(File.Exists(Collection));
    }

    [Fact]
    public void The_workspace_option_overrides_the_environment_and_the_key_is_never_printed_or_sent_outside_its_header()
    {
        var fake = Healthy();
        fake.Workspaces.Add("11111111-aaaa-bbbb-cccc-000000000002");

        var (code, output, error) = Publish(fake, V1(), "--workspace", "11111111-aaaa-bbbb-cccc-000000000002");

        Assert.Equal(CliApplication.Success, code);
        Assert.Equal("11111111-aaaa-bbbb-cccc-000000000002", fake.Collections[0].WorkspaceId);
        Assert.DoesNotContain(FakePostmanCloud.ApiKey, output + error + File.ReadAllText(Collection) + File.ReadAllText(Baseline));
        Assert.All(fake.Calls, call =>
        {
            Assert.Equal(FakePostmanCloud.ApiKey, call.Key);
            Assert.DoesNotContain(FakePostmanCloud.ApiKey, call.PathAndQuery);
            Assert.DoesNotContain(FakePostmanCloud.ApiKey, call.Body ?? string.Empty);
        });
    }

    [Fact]
    public void The_postman_options_are_refused_without_publish_and_a_plain_sync_never_contacts_postman()
    {
        var fake = Healthy();
        var api = V1();

        var error = new StringWriter();
        var refused = CliApplication.Run(["sync", api, "--baseline", Baseline, "--out", Collection, "--workspace", Workspace], new StringWriter(), error, Env(fake));
        Assert.Equal(CliApplication.Failure, refused);
        Assert.Contains("only apply together with --publish", error.ToString());

        var plain = CliApplication.Run(["sync", api, "--baseline", Baseline, "--out", Collection], new StringWriter(), new StringWriter(), Env(fake));
        Assert.Equal(CliApplication.Success, plain);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Backups_may_not_be_placed_inside_the_analyzed_api()
    {
        var fake = Healthy();
        var api = V1();
        var error = new StringWriter();

        var code = CliApplication.Run(
            ["sync", api, "--baseline", Baseline, "--out", Collection, "--publish", "--backup-dir", Path.Combine(api, "backups")],
            new StringWriter(), error, Env(fake));

        Assert.Equal(CliApplication.Failure, code);
        Assert.Contains("inside the analyzed API", error.ToString());
        Assert.Empty(fake.Calls);
    }
}
