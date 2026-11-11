using System.Net;
using System.Text.Json.Nodes;
using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Backup;
using ApiCollection.Core.Diff;
using ApiCollection.Core.Snapshots;
using ApiCollection.Core.Sync;
using ApiCollection.Postman.CloudApi;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;
using ApiCollection.Postman.Sync;

namespace ApiCollection.Postman.Tests;

/// <summary>
/// The safe sync: back up, publish, read back, compare, roll back. Every scenario runs against an in-memory Postman that can be made to
/// lose answers, apply writes wrongly or refuse the rollback, so the recovery path is exercised, not assumed.
/// </summary>
public sealed class PostmanSyncOrchestratorTests
{
    private const string Workspace = "00000000-aaaa-bbbb-cccc-000000000001";
    private const string Name = "Sample API";
    private const string IdNamespace = $"{Workspace}|{Name}";
    private const string Secret = "eyJhbGciOiJIUzI1NiJ9.a-real-looking-token";
    private static readonly EndpointDiscoveryEngine Engine = new();

    private const string ItemsV1 = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpGet("{id:int}")] public IActionResult Get(int id) => Ok();
            [HttpPost] public IActionResult Create(CreateItem request) => Ok();
        }
        public class CreateItem { public string Name { get; set; } = ""; }
        """;

    private const string Orders = """
        [ApiController]
        [Route("api/[controller]")]
        public class OrdersController : ControllerBase { [HttpGet] public IActionResult All() => Ok(); }
        """;

    private static readonly string[] V1 = [ItemsV1, Orders];

    // Orders disappears and Items gains an action.
    private static readonly string[] V2 = [ItemsV1.Replace("[HttpPost]", "[HttpGet(\"count\")] public IActionResult Count() => Ok();\n    [HttpPost]")];

    // ---- helpers ----

    private sealed class InMemoryBackupStore(FakePostmanCloud fake) : ICollectionBackupStore
    {
        public List<CollectionBackup> Saved { get; } = [];

        /// <summary>How many writes had reached Postman each time a backup was saved: a backup must come before any write.</summary>
        public List<int> WritesAtSave { get; } = [];

        public Func<CollectionBackup, BackupSaveResult>? Override { get; set; }

        public Task<BackupSaveResult> SaveAsync(CollectionBackup backup, CancellationToken cancellationToken = default)
        {
            WritesAtSave.Add(fake.Writes.Count());
            if (Override is not null)
            {
                return Task.FromResult(Override(backup));
            }

            Saved.Add(backup);
            return Task.FromResult(new BackupSaveResult(true, $"memory://backup-{Saved.Count}", "The backup was stored and verified."));
        }

        public Task<CollectionBackup> LoadAsync(string location, CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved[int.Parse(location["memory://backup-".Length..]) - 1]);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Prepared(string Json, ChangeSet Changes, SyncDecision Decision);

    private static Prepared Prepare(string[] before, string[] after, string? idNamespace = IdNamespace)
    {
        var baseline = AnalysisSnapshot.Create(before.Length == 0 ? [] : Engine.DiscoverFromSources(before).Endpoints);
        var endpoints = Engine.DiscoverFromSources(after).Endpoints;
        var changes = ChangeSetBuilder.Build(baseline, AnalysisSnapshot.Create(endpoints));
        var decision = SyncSafetyValidator.Validate(
            new SyncSafetyInput { Changes = changes, BaselineCollection = before.Length == 0 ? BaselineCollectionState.NotApplicable : BaselineCollectionState.Verified },
            new SyncSafetyOptions { MaxRemovedPercent = 100, MaxRemovedCount = 100 });
        var json = PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(endpoints, new PostmanCollectionOptions { Name = Name, IdNamespace = idNamespace }));
        return new Prepared(json, changes, decision);
    }

    private static FakePostmanCloud Healthy()
    {
        var fake = new FakePostmanCloud();
        fake.Workspaces.Add(Workspace);
        return fake;
    }

    private static PostmanSyncOrchestrator Orchestrator(FakePostmanCloud fake, InMemoryBackupStore store, TimeProvider? clock = null) =>
        new(new PostmanCloudClient(PostmanHttpClientFactory.Create(fake), new PostmanCloudOptions { ApiKey = FakePostmanCloud.ApiKey }), store, clock);

    private static PostmanSyncRequest Request(Prepared prepared, string? known = null, bool dryRun = false, bool autoRollback = true) => new()
    {
        WorkspaceId = Workspace,
        CollectionName = Name,
        CollectionJson = prepared.Json,
        IdNamespace = IdNamespace,
        KnownCollectionId = known,
        Decision = prepared.Decision,
        Changes = prepared.Changes,
        DryRun = dryRun,
        AutoRollback = autoRollback
    };

    /// <summary>Publishes V1 so that an existing collection (and its uid) is there to update.</summary>
    private static async Task<string> Seed(FakePostmanCloud fake, InMemoryBackupStore store)
    {
        var created = await Orchestrator(fake, store).SyncAsync(Request(Prepare([], V1)));
        Assert.Equal(PostmanSyncOutcome.Synced, created.Outcome);
        fake.Calls.Clear();
        return created.CollectionUid!;
    }

    private static SortedDictionary<string, string> Project(FakePostmanCloud fake) =>
        CollectionProjection.Of(fake.Collections[0].Collection.ToJsonString());

    /// <summary>Damages the stored collection the way a faulty write would: the first request of the first folder disappears.</summary>
    private static void Corrupt(FakePostmanCloud fake) =>
        fake.Collections[0].Collection["item"]![0]!["item"]!.AsArray().RemoveAt(0);

    private static void CorruptNextPut(FakePostmanCloud fake)
    {
        var done = false;
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method == HttpMethod.Put && !done)
            {
                done = true;
                Corrupt(fake);
            }
        };
    }

    private static int Puts(FakePostmanCloud fake) => fake.Calls.Count(c => c.Method == HttpMethod.Put);

    private static bool IsCollectionRead(FakePostmanCloud.Call call) =>
        call.Method == HttpMethod.Get && call.PathAndQuery.StartsWith("/collections/", StringComparison.Ordinal);

    private static SyncStepStatus Status(PostmanSyncReport report, SyncStepName name) => report.Steps.Single(s => s.Name == name).Status;

    // ---- postman assigns its own item ids (observed on the real service) ----

    [Fact]
    public async Task A_new_collection_verifies_although_postman_replaced_every_folder_and_request_id()
    {
        var fake = Healthy();
        fake.ReplaceItemIds = true;

        var report = await Orchestrator(fake, new InMemoryBackupStore(fake)).SyncAsync(Request(Prepare([], V1)));

        Assert.Equal((PostmanSyncOutcome.Synced, RemoteState.Published), (report.Outcome, report.RemoteState));
        Assert.Empty(report.Mismatches);
        var ids = FakePostmanCloud.ItemIds(fake.Collections[0]).Values;
        Assert.All(ids, id => Assert.StartsWith("fresh-", id));
    }

    [Fact]
    public async Task An_update_verifies_although_postman_replaced_the_ids_and_a_real_loss_is_still_caught_and_rolled_back()
    {
        var fake = Healthy();
        fake.ReplaceItemIds = true;
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);

        var updated = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));
        Assert.Equal((PostmanSyncOutcome.Synced, RemoteState.Published), (updated.Outcome, updated.RemoteState));

        var before = Project(fake);
        CorruptNextPut(fake);
        var broken = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V2, V1), known: uid));

        Assert.Equal((PostmanSyncOutcome.RolledBack, RemoteState.Restored), (broken.Outcome, broken.RemoteState));
        Assert.Empty(CollectionProjection.Differences(before, Project(fake)));
    }

    // ---- the collection does not exist yet ----

    [Fact]
    public async Task A_first_sync_creates_the_collection_verifies_it_and_has_nothing_to_back_up()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare([], V1)));

        Assert.Equal((PostmanSyncOutcome.Synced, RemoteState.Published), (report.Outcome, report.RemoteState));
        Assert.True(report.Succeeded);
        Assert.False(report.NeedsAttention);
        Assert.Equal(fake.Collections[0].Uid, report.CollectionUid);
        Assert.Empty(store.Saved);
        Assert.Single(fake.Writes);
        Assert.Equal(
            [
                (SyncStepName.Preflight, SyncStepStatus.Ok), (SyncStepName.Plan, SyncStepStatus.Ok), (SyncStepName.Backup, SyncStepStatus.Skipped),
                (SyncStepName.Publish, SyncStepStatus.Ok), (SyncStepName.Verify, SyncStepStatus.Ok), (SyncStepName.Rollback, SyncStepStatus.Skipped)
            ],
            report.Steps.Select(s => (s.Name, s.Status)));
    }

    [Fact]
    public async Task A_create_that_does_not_read_back_as_sent_cannot_be_rolled_back_so_it_ends_with_instructions_and_never_deletes()
    {
        var fake = Healthy();
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method == HttpMethod.Post)
            {
                Corrupt(fake);
            }
        };

        var report = await Orchestrator(fake, new InMemoryBackupStore(fake)).SyncAsync(Request(Prepare([], V1)));

        Assert.Equal((PostmanSyncOutcome.VerificationFailed, RemoteState.Divergent), (report.Outcome, report.RemoteState));
        Assert.True(report.NeedsAttention);
        Assert.Equal(SyncStepStatus.Skipped, Status(report, SyncStepName.Rollback));
        Assert.Contains(report.Steps, s => s.Name == SyncStepName.Rollback && s.Message.Contains("never deletes"));
        Assert.Contains(report.RecoveryInstructions, i => i.Contains("never deletes collections") && i.Contains(report.CollectionUid!));
        Assert.NotEmpty(report.Mismatches);
        Assert.DoesNotContain(fake.Calls, c => c.Method == HttpMethod.Delete);
        Assert.Single(fake.Writes);
    }

    [Fact]
    public async Task A_refused_create_leaves_postman_untouched_but_an_unclear_one_says_the_state_is_unknown()
    {
        var clear = Healthy();
        clear.Intercept = call => call.Method == HttpMethod.Post ? FakePostmanCloud.Error(HttpStatusCode.BadRequest, "malformedRequestError", "Invalid collection") : null;
        var refused = await Orchestrator(clear, new InMemoryBackupStore(clear)).SyncAsync(Request(Prepare([], V1)));
        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Untouched), (refused.Outcome, refused.RemoteState));
        Assert.Contains("Invalid collection", refused.Message);
        Assert.False(refused.NeedsAttention);

        var unclear = Healthy();
        unclear.Intercept = call => call.Method == HttpMethod.Post ? throw new HttpRequestException("connection reset") : null;
        var lost = await Orchestrator(unclear, new InMemoryBackupStore(unclear)).SyncAsync(Request(Prepare([], V1)));
        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Unknown), (lost.Outcome, lost.RemoteState));
        Assert.True(lost.NeedsAttention);
        Assert.Contains(lost.RecoveryInstructions, i => i.Contains("may or may not have created"));
    }

    [Fact]
    public async Task A_new_collection_that_cannot_be_read_back_is_inconclusive_not_a_failure()
    {
        var fake = Healthy();
        fake.Intercept = call => IsCollectionRead(call) ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "boom") : null;

        var report = await Orchestrator(fake, new InMemoryBackupStore(fake)).SyncAsync(Request(Prepare([], V1)));

        Assert.Equal((PostmanSyncOutcome.VerificationInconclusive, RemoteState.Unknown), (report.Outcome, report.RemoteState));
        Assert.Equal(SyncStepStatus.Inconclusive, Status(report, SyncStepName.Verify));
        Assert.Single(fake.Writes);
    }

    // ---- an existing collection: backup, publish, verify ----

    [Fact]
    public async Task An_update_backs_up_what_is_in_postman_before_any_write_then_publishes_and_verifies()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        var before = Project(fake);
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));

        var report = await Orchestrator(fake, store, clock).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.Synced, RemoteState.Published), (report.Outcome, report.RemoteState));
        Assert.Equal("memory://backup-1", report.BackupLocation);

        var backup = Assert.Single(store.Saved);
        Assert.Equal([0], store.WritesAtSave);
        Assert.Equal((uid, Name, Workspace, new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)), (backup.CollectionUid, backup.CollectionName, backup.WorkspaceId, backup.TakenAtUtc));
        Assert.Equal(CollectionBackup.ComputeHash(backup.CollectionJson), backup.Sha256);
        Assert.Empty(CollectionProjection.Differences(before, CollectionProjection.Of(backup.CollectionJson)));

        var calls = fake.Calls.Select(c => (c.Method, c.PathAndQuery)).ToList();
        Assert.True(calls.IndexOf((HttpMethod.Get, $"/collections/{uid}")) < calls.IndexOf((HttpMethod.Put, $"/collections/{uid}")));

        var now = Project(fake);
        Assert.Contains(now.Keys, k => k.EndsWith(".name", StringComparison.Ordinal) && now[k] == "Count");
        Assert.DoesNotContain(now.Keys, k => now[k] == "Orders");
        Assert.Equal(
            new[] { SyncStepStatus.Ok, SyncStepStatus.Ok, SyncStepStatus.Ok, SyncStepStatus.Ok, SyncStepStatus.Ok, SyncStepStatus.Skipped },
            report.Steps.Select(s => s.Status));
    }

    [Fact]
    public async Task Without_a_kept_uid_the_existing_collection_is_found_again_backed_up_and_updated()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2)));

        Assert.Equal(PostmanSyncOutcome.Synced, report.Outcome);
        Assert.Equal(uid, report.CollectionUid);
        Assert.Single(fake.Collections);
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task When_the_backup_cannot_be_stored_nothing_is_written()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        var before = Project(fake);
        store.Override = _ => new BackupSaveResult(false, null, "The disk is full.");

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Untouched), (report.Outcome, report.RemoteState));
        Assert.Equal(SyncStepStatus.Failed, Status(report, SyncStepName.Backup));
        Assert.Contains("The disk is full.", report.Steps.Single(s => s.Name == SyncStepName.Backup).Message);
        Assert.Empty(fake.Writes);
        Assert.Empty(CollectionProjection.Differences(before, Project(fake)));
        Assert.DoesNotContain(report.Steps, s => s.Name == SyncStepName.Publish);
    }

    [Fact]
    public async Task When_the_current_collection_cannot_be_read_there_is_nothing_to_back_up_and_nothing_is_written()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        var reads = 0;
        fake.Intercept = call => IsCollectionRead(call) && ++reads == 2 ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "boom") : null;

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Untouched), (report.Outcome, report.RemoteState));
        Assert.Contains("no backup could be made", report.Message);
        Assert.Empty(fake.Writes);
        Assert.Empty(store.Saved.Skip(1));
    }

    // ---- the change set is checked before anything is sent ----

    [Fact]
    public async Task A_collection_that_does_not_match_the_change_set_is_refused_before_anything_is_sent()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var current = Prepare([], V1);
        var other = Prepare([], V2);

        var staleCollection = Request(current) with { CollectionJson = other.Json };
        var report = await Orchestrator(fake, store).SyncAsync(staleCollection);

        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Untouched), (report.Outcome, report.RemoteState));
        Assert.Contains("does not match the change set", report.Message);
        Assert.Contains("expected endpoint(s) are missing", report.Message);
        Assert.Empty(fake.Calls);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task A_decision_that_belongs_to_another_change_set_is_refused()
    {
        var fake = Healthy();
        var current = Prepare([], V1);
        var other = Prepare(V1, V2); // one added, one removed: a different summary than four added

        var report = await Orchestrator(fake, new InMemoryBackupStore(fake)).SyncAsync(Request(current) with { Decision = other.Decision });

        Assert.Equal(PostmanSyncOutcome.Failed, report.Outcome);
        Assert.Contains("do not belong together", report.Message);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_collection_generated_without_ids_or_for_another_namespace_is_refused()
    {
        var fake = Healthy();
        var prepared = Prepare([], V1);

        var withoutIds = await Orchestrator(fake, new InMemoryBackupStore(fake)).SyncAsync(Request(prepared) with { CollectionJson = Prepare([], V1, idNamespace: null).Json });
        var wrongNamespace = await Orchestrator(fake, new InMemoryBackupStore(fake)).SyncAsync(Request(prepared) with { IdNamespace = "another|namespace" });

        Assert.Contains("has no id", withoutIds.Message);
        Assert.Contains("does not match the change set", wrongNamespace.Message);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_blocked_decision_a_known_collection_with_no_changes_and_a_dry_run_write_nothing()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);

        var blocked = Prepare(V1, V2) with { Decision = new SyncDecision { Outcome = SyncOutcome.Blocked, Summary = new ChangeSetSummary(0, 0, 0, 0), Limits = new SyncSafetyOptions(), Violations = [new SyncViolation(SyncViolationCode.MassDeletion, "x", true)] } };
        var blockedReport = await Orchestrator(fake, store).SyncAsync(Request(blocked, known: uid));
        Assert.Equal(PostmanSyncOutcome.Blocked, blockedReport.Outcome);
        Assert.Contains("MassDeletion", blockedReport.Steps.Single().Message);
        Assert.Empty(fake.Calls);

        var unchanged = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V1), known: uid));
        Assert.Equal(PostmanSyncOutcome.NoChanges, unchanged.Outcome);
        Assert.Empty(fake.Calls);

        var dry = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid, dryRun: true));
        Assert.Equal((PostmanSyncOutcome.DryRun, RemoteState.Untouched), (dry.Outcome, dry.RemoteState));
        Assert.Contains("would be updated", dry.Message);
        Assert.Empty(fake.Writes);
        Assert.Empty(store.Saved); // the seed's create takes no backup and the dry run takes none either
    }

    [Fact]
    public async Task An_unknown_workspace_stops_the_sync_without_any_write()
    {
        var fake = new FakePostmanCloud();

        var report = await Orchestrator(fake, new InMemoryBackupStore(fake)).SyncAsync(Request(Prepare([], V1)));

        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Untouched), (report.Outcome, report.RemoteState));
        Assert.Contains("workspace cannot be used", report.Message);
        Assert.Empty(fake.Writes);
    }

    // ---- rollback: the recovery path ----

    [Fact]
    public async Task A_publish_that_does_not_verify_is_rolled_back_and_the_restore_is_verified()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        var before = Project(fake);
        CorruptNextPut(fake);

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.RolledBack, RemoteState.Restored), (report.Outcome, report.RemoteState));
        Assert.NotEmpty(report.Mismatches);
        Assert.Empty(report.RollbackMismatches);
        Assert.Equal(2, Puts(fake));
        Assert.Empty(CollectionProjection.Differences(before, Project(fake)));
        Assert.Equal(
            [
                (SyncStepName.Publish, SyncStepStatus.Ok), (SyncStepName.Verify, SyncStepStatus.Failed),
                (SyncStepName.Rollback, SyncStepStatus.Ok), (SyncStepName.VerifyRollback, SyncStepStatus.Ok)
            ],
            report.Steps.Where(s => s.Name is SyncStepName.Publish or SyncStepName.Verify or SyncStepName.Rollback or SyncStepName.VerifyRollback).Select(s => (s.Name, s.Status)));

        var text = report.ToText();
        Assert.Contains("Postman sync: ROLLED BACK", text);
        Assert.Contains("Remote state: Restored", text);
        Assert.Contains("paths only)", text);
        Assert.Contains("missing request[", text);
    }

    [Fact]
    public async Task The_rollback_restores_real_secrets_from_memory_while_the_stored_backup_has_them_redacted()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        fake.Collections[0].Collection["variable"]!.AsArray().Add(new JsonObject { ["key"] = "apiToken", ["value"] = Secret });
        var before = Project(fake);
        CorruptNextPut(fake);

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal(PostmanSyncOutcome.RolledBack, report.Outcome);
        var restored = fake.Collections[0].Collection["variable"]!.AsArray().Single(v => (string?)v!["key"] == "apiToken");
        Assert.Equal(Secret, (string?)restored!["value"]);
        Assert.Empty(CollectionProjection.Differences(before, Project(fake)));

        var backup = Assert.Single(store.Saved);
        Assert.DoesNotContain(Secret, backup.CollectionJson);
        Assert.Contains(SecretRedactor.Placeholder, backup.CollectionJson);
        Assert.Contains("variable[apiToken]", backup.RedactedPaths);
        Assert.DoesNotContain(Secret, report.ToText());
        Assert.Contains("1 secret-looking value(s) were redacted", report.Steps.Single(s => s.Name == SyncStepName.Backup).Message);
    }

    [Fact]
    public async Task A_rollback_the_service_refuses_is_reported_as_failed_with_the_state_and_how_to_recover()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        CorruptNextPut(fake);
        fake.Intercept = call => call.Method == HttpMethod.Put && Puts(fake) == 2
            ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "cannot write")
            : null;

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.RollbackFailed, RemoteState.Divergent), (report.Outcome, report.RemoteState));
        Assert.True(report.NeedsAttention);
        Assert.False(report.Succeeded);
        Assert.Equal(SyncStepStatus.Failed, Status(report, SyncStepName.Rollback));
        Assert.Equal(SyncStepStatus.Failed, Status(report, SyncStepName.VerifyRollback));
        Assert.NotEmpty(report.RollbackMismatches);
        Assert.Equal("memory://backup-1", report.BackupLocation);
        Assert.Contains(report.RecoveryInstructions, i => i.Contains("memory://backup-1"));
        Assert.Contains(report.RecoveryInstructions, i => i.Contains("Do not run the sync again"));
        Assert.Contains("ROLLBACK FAILED", report.ToText());
        Assert.Contains("The rollback request failed", report.Steps.Single(s => s.Name == SyncStepName.Rollback).Message);
    }

    [Fact]
    public async Task A_rollback_that_is_accepted_but_does_not_restore_the_collection_is_also_a_failed_rollback()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method == HttpMethod.Put)
            {
                Corrupt(fake);
            }
        };

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.RollbackFailed, RemoteState.Divergent), (report.Outcome, report.RemoteState));
        Assert.Equal(SyncStepStatus.Ok, Status(report, SyncStepName.Rollback));
        Assert.Equal(SyncStepStatus.Failed, Status(report, SyncStepName.VerifyRollback));
        Assert.NotEmpty(report.RollbackMismatches);
        Assert.Contains("neither its old nor its intended state", report.Message);
        Assert.Contains("Differences between the backup and what Postman holds after the rollback", report.ToText());
    }

    [Fact]
    public async Task A_rollback_that_cannot_be_checked_leaves_the_state_unknown()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        CorruptNextPut(fake);
        fake.Intercept = call => IsCollectionRead(call) && Puts(fake) >= 2 ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "boom") : null;

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.RollbackFailed, RemoteState.Unknown), (report.Outcome, report.RemoteState));
        Assert.Equal(SyncStepStatus.Inconclusive, Status(report, SyncStepName.VerifyRollback));
        Assert.NotEmpty(report.RecoveryInstructions);
    }

    [Fact]
    public async Task A_rollback_request_that_reports_a_failure_but_did_restore_the_collection_counts_as_rolled_back()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        var before = Project(fake);
        var puts = 0;
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method != HttpMethod.Put)
            {
                return;
            }

            if (++puts == 1)
            {
                Corrupt(fake);
            }
            else
            {
                throw new HttpRequestException("the answer was lost");
            }
        };

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.RolledBack, RemoteState.Restored), (report.Outcome, report.RemoteState));
        Assert.Contains("reported a failure, but the collection reads back as it was", report.Message);
        Assert.Equal(SyncStepStatus.Failed, Status(report, SyncStepName.Rollback));
        Assert.Empty(CollectionProjection.Differences(before, Project(fake)));
    }

    [Fact]
    public async Task With_automatic_rollback_off_a_bad_publish_is_left_alone_and_the_instructions_name_the_backup()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        CorruptNextPut(fake);

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid, autoRollback: false));

        Assert.Equal((PostmanSyncOutcome.VerificationFailed, RemoteState.Divergent), (report.Outcome, report.RemoteState));
        Assert.Equal(1, Puts(fake));
        Assert.Equal(SyncStepStatus.Skipped, Status(report, SyncStepName.Rollback));
        Assert.Contains(report.RecoveryInstructions, i => i.Contains("memory://backup-1"));
        Assert.Contains(report.RecoveryInstructions, i => i.Contains("RestoreAsync"));
    }

    // ---- publish failures: look before assuming ----

    [Fact]
    public async Task A_publish_that_failed_cleanly_leaves_the_collection_verified_unchanged_and_needs_no_rollback()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        fake.Intercept = call => call.Method == HttpMethod.Put ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "try later") : null;

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Unchanged), (report.Outcome, report.RemoteState));
        Assert.False(report.NeedsAttention);
        Assert.Equal(1, Puts(fake));
        Assert.Equal(SyncStepStatus.Failed, Status(report, SyncStepName.Publish));
        Assert.Equal(SyncStepStatus.Skipped, Status(report, SyncStepName.Rollback));
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task A_publish_whose_answer_was_lost_after_it_was_applied_is_recognised_by_reading_the_collection()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method == HttpMethod.Put)
            {
                throw new HttpRequestException("connection reset after the write");
            }
        };

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.Synced, RemoteState.Published), (report.Outcome, report.RemoteState));
        Assert.Contains("reads back as intended although the publish call reported a failure", report.Message);
        Assert.Equal(1, Puts(fake));
    }

    [Fact]
    public async Task A_failed_publish_that_left_the_collection_half_changed_is_rolled_back()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        var before = Project(fake);
        var puts = 0;
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method == HttpMethod.Put && ++puts == 1)
            {
                Corrupt(fake);
                throw new HttpRequestException("failed halfway");
            }
        };

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.RolledBack, RemoteState.Restored), (report.Outcome, report.RemoteState));
        Assert.Equal(SyncStepStatus.Failed, Status(report, SyncStepName.Publish));
        Assert.Empty(CollectionProjection.Differences(before, Project(fake)));
    }

    [Fact]
    public async Task A_failed_publish_whose_result_cannot_be_checked_needs_attention_with_instructions()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        fake.Intercept = call =>
            call.Method == HttpMethod.Put ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "boom")
            : IsCollectionRead(call) && Puts(fake) >= 1 ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "boom")
            : null;

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Unknown), (report.Outcome, report.RemoteState));
        Assert.True(report.NeedsAttention);
        Assert.Contains(report.RecoveryInstructions, i => i.Contains("memory://backup-1"));
        Assert.Equal(1, Puts(fake));
    }

    // ---- verification that cannot be done ----

    [Fact]
    public async Task A_publish_that_cannot_be_read_back_is_inconclusive_and_is_not_rolled_back_on_no_evidence()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        fake.Intercept = call => IsCollectionRead(call) && Puts(fake) >= 1 ? FakePostmanCloud.Error(HttpStatusCode.InternalServerError, "serverError", "boom") : null;

        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));

        Assert.Equal((PostmanSyncOutcome.VerificationInconclusive, RemoteState.Unknown), (report.Outcome, report.RemoteState));
        Assert.True(report.NeedsAttention);
        Assert.Equal(1, Puts(fake));
        Assert.Contains(report.RecoveryInstructions, i => i.Contains("memory://backup-1"));
    }

    // ---- restoring from a stored backup (the explicit recovery path) ----

    [Fact]
    public async Task A_stored_backup_can_be_restored_and_the_restore_is_verified()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        var v1 = Project(fake);
        await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));
        Assert.NotEmpty(CollectionProjection.Differences(v1, Project(fake)));

        var report = await Orchestrator(fake, store).RestoreAsync(store.Saved[0]);

        Assert.Equal((PostmanSyncOutcome.RolledBack, RemoteState.Restored), (report.Outcome, report.RemoteState));
        Assert.Empty(CollectionProjection.Differences(v1, Project(fake)));
    }

    [Fact]
    public async Task A_backup_with_redacted_secrets_is_not_restored_unless_that_is_accepted_explicitly()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        fake.Collections[0].Collection["variable"]!.AsArray().Add(new JsonObject { ["key"] = "apiToken", ["value"] = Secret });
        await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));
        var backup = store.Saved[0];
        Assert.NotEmpty(backup.RedactedPaths);
        fake.Calls.Clear();

        var refused = await Orchestrator(fake, store).RestoreAsync(backup);
        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Untouched), (refused.Outcome, refused.RemoteState));
        Assert.Contains("would replace secrets with placeholders", refused.Message);
        Assert.Empty(fake.Calls);

        var accepted = await Orchestrator(fake, store).RestoreAsync(backup, acceptRedactedValues: true);
        Assert.Equal(PostmanSyncOutcome.RolledBack, accepted.Outcome);
        var written = fake.Collections[0].Collection["variable"]!.AsArray().Single(v => (string?)v!["key"] == "apiToken");
        Assert.Equal(SecretRedactor.Placeholder, (string?)written!["value"]);
    }

    [Fact]
    public async Task A_tampered_backup_or_a_vanished_collection_is_refused_without_a_write()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));
        var backup = store.Saved[0];
        fake.Calls.Clear();

        var tampered = await Orchestrator(fake, store).RestoreAsync(backup with { CollectionJson = backup.CollectionJson.Replace("Items", "Hacked") });
        Assert.Equal(PostmanSyncOutcome.Failed, tampered.Outcome);
        Assert.Contains("cannot be trusted", tampered.Message);
        Assert.Empty(fake.Calls);

        var gone = await Orchestrator(fake, store).RestoreAsync(backup with { CollectionUid = "1234567-does-not-exist" });
        Assert.Equal((PostmanSyncOutcome.Failed, RemoteState.Untouched), (gone.Outcome, gone.RemoteState));
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task A_restore_that_does_not_reproduce_the_backup_is_reported_as_failed()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));
        fake.AfterRoute = (call, _) =>
        {
            if (call.Method == HttpMethod.Put)
            {
                Corrupt(fake);
            }
        };

        var report = await Orchestrator(fake, store).RestoreAsync(store.Saved[0]);

        Assert.Equal((PostmanSyncOutcome.RollbackFailed, RemoteState.Divergent), (report.Outcome, report.RemoteState));
        Assert.NotEmpty(report.RollbackMismatches);
    }

    // ---- idempotency, reports and safety across the whole lifecycle ----

    [Fact]
    public async Task Syncing_the_same_state_again_updates_in_place_and_a_no_change_decision_sends_nothing()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);

        var again = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V1) with { Decision = Prepare([], V1).Decision, Changes = Prepare([], V1).Changes }, known: uid));
        Assert.Equal(PostmanSyncOutcome.Synced, again.Outcome);
        Assert.Single(fake.Collections);

        fake.Calls.Clear();
        var none = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V1), known: uid));
        Assert.Equal(PostmanSyncOutcome.NoChanges, none.Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Across_every_scenario_only_get_post_and_put_are_used_the_key_stays_in_the_header_and_nothing_is_ever_deleted()
    {
        var fake = Healthy();
        var store = new InMemoryBackupStore(fake);
        var uid = await Seed(fake, store);
        CorruptNextPut(fake);
        var report = await Orchestrator(fake, store).SyncAsync(Request(Prepare(V1, V2), known: uid));
        await Orchestrator(fake, store).RestoreAsync(store.Saved[0]);

        Assert.Equal(PostmanSyncOutcome.RolledBack, report.Outcome);
        Assert.All(fake.Calls, c =>
        {
            Assert.Contains(c.Method, new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put });
            Assert.Equal(FakePostmanCloud.ApiKey, c.Key);
            Assert.DoesNotContain(FakePostmanCloud.ApiKey, c.PathAndQuery);
            Assert.DoesNotContain(FakePostmanCloud.ApiKey, c.Body ?? string.Empty);
        });
        Assert.DoesNotContain(FakePostmanCloud.ApiKey, report.ToText());
    }

    [Fact]
    public void The_report_text_is_deterministic_and_the_attention_flag_follows_the_outcome()
    {
        var report = new PostmanSyncReport
        {
            Outcome = PostmanSyncOutcome.RollbackFailed,
            RemoteState = RemoteState.Divergent,
            Message = "The rollback failed.",
            CollectionUid = "1234567-abc",
            BackupLocation = "backups/x.json",
            Steps = [new SyncStep(SyncStepName.Rollback, SyncStepStatus.Failed, "refused")],
            Mismatches = ["missing request[r1].name"],
            RecoveryInstructions = ["Restore it by hand.", "Do not run the sync again."]
        };

        Assert.Equal(report.ToText(), report.ToText());
        Assert.Contains("  1. Restore it by hand.", report.ToText());
        Assert.Contains("  [FAILED] Rollback: refused", report.ToText());

        PostmanSyncOutcome[] calm = [PostmanSyncOutcome.Synced, PostmanSyncOutcome.NoChanges, PostmanSyncOutcome.DryRun, PostmanSyncOutcome.Blocked, PostmanSyncOutcome.RolledBack];
        Assert.All(calm, outcome => Assert.False((report with { Outcome = outcome, RemoteState = RemoteState.Untouched }).NeedsAttention));
        PostmanSyncOutcome[] loud = [PostmanSyncOutcome.RollbackFailed, PostmanSyncOutcome.VerificationFailed, PostmanSyncOutcome.VerificationInconclusive];
        Assert.All(loud, outcome => Assert.True((report with { Outcome = outcome }).NeedsAttention));
        Assert.True((report with { Outcome = PostmanSyncOutcome.Failed, RemoteState = RemoteState.Unknown }).NeedsAttention);
        Assert.False((report with { Outcome = PostmanSyncOutcome.Failed, RemoteState = RemoteState.Untouched }).NeedsAttention);
    }
}
