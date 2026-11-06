using System.Text.Json;
using System.Text.Json.Nodes;
using ApiCollection.Core.Backup;
using ApiCollection.Core.Diff;
using ApiCollection.Core.Sync;
using ApiCollection.Postman.CloudApi;
using ApiCollection.Postman.Generation;

namespace ApiCollection.Postman.Sync;

/// <summary>
/// Publishes a collection to Postman so that the update can be undone: it checks the collection against the change set and the safety
/// decision, backs up what is in Postman, publishes, reads the collection back and compares it with what was meant, and restores the
/// backup when it does not match. Each step is reported, and so is a rollback that itself fails, with explicit recovery instructions.
/// Writes happen only after the backup was stored and verified. A collection it created cannot be rolled back (this tool never deletes
/// collections); that case ends with instructions instead.
/// </summary>
public sealed class PostmanSyncOrchestrator(PostmanCloudClient client, ICollectionBackupStore backups, TimeProvider? clock = null)
{
    private readonly PostmanCollectionRepository _repository = new(client);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed class Run
    {
        public List<SyncStep> Steps { get; } = [];

        public string? BackupLocation { get; set; }

        public void Add(SyncStepName name, SyncStepStatus status, string message) => Steps.Add(new SyncStep(name, status, message));

        public PostmanSyncReport Done(
            PostmanSyncOutcome outcome, RemoteState state, string message, string? uid = null, IReadOnlyList<string>? mismatches = null,
            IReadOnlyList<string>? rollbackMismatches = null, IReadOnlyList<string>? recovery = null) => new()
        {
            Outcome = outcome,
            RemoteState = state,
            Message = message,
            CollectionUid = uid,
            BackupLocation = BackupLocation,
            Steps = Steps.ToList(),
            Mismatches = mismatches ?? [],
            RollbackMismatches = rollbackMismatches ?? [],
            RecoveryInstructions = recovery ?? []
        };
    }

    public async Task<PostmanSyncReport> SyncAsync(PostmanSyncRequest request, CancellationToken cancellationToken = default)
    {
        var run = new Run();

        // 1. Preflight: nothing is sent until the decision and the collection agree with the change set.
        if (request.Decision.Outcome == SyncOutcome.Blocked)
        {
            var reasons = string.Join(", ", request.Decision.Violations.Select(v => v.Code));
            run.Add(SyncStepName.Preflight, SyncStepStatus.Failed, $"The sync safety gate blocked this ({(reasons.Length == 0 ? "no reason given" : reasons)}).");
            return run.Done(PostmanSyncOutcome.Blocked, RemoteState.Untouched, "The sync safety gate blocked this; nothing was sent.");
        }

        var problem = Preflight(request);
        if (problem is not null)
        {
            run.Add(SyncStepName.Preflight, SyncStepStatus.Failed, problem);
            return run.Done(PostmanSyncOutcome.Failed, RemoteState.Untouched, $"{problem} Nothing was sent.");
        }

        run.Add(SyncStepName.Preflight, SyncStepStatus.Ok, "The collection contains exactly the endpoints the change set says exist, and the decision belongs to that change set.");

        if (request.Decision.Outcome == SyncOutcome.NoChanges && request.KnownCollectionId is not null)
        {
            run.Add(SyncStepName.Plan, SyncStepStatus.Skipped, "Nothing changed and the collection is known.");
            return run.Done(PostmanSyncOutcome.NoChanges, RemoteState.Untouched, "Nothing changed, so nothing was sent.", request.KnownCollectionId);
        }

        // 2. Plan: read-only. Finds out whether the collection exists and which one it is.
        var plan = await _repository.PublishAsync(ToPublish(request, dryRun: true, request.KnownCollectionId), cancellationToken);
        switch (plan.Outcome)
        {
            case PostmanPublishOutcome.Failed:
                run.Add(SyncStepName.Plan, SyncStepStatus.Failed, plan.Message);
                return run.Done(PostmanSyncOutcome.Failed, RemoteState.Untouched, $"{plan.Message} Nothing was written.");
            case PostmanPublishOutcome.NoChanges or PostmanPublishOutcome.Adopted:
                run.Add(SyncStepName.Plan, SyncStepStatus.Ok, plan.Message);
                return run.Done(PostmanSyncOutcome.NoChanges, RemoteState.Untouched, plan.Message, plan.CollectionUid);
        }

        run.Add(SyncStepName.Plan, SyncStepStatus.Ok, plan.Message);
        if (request.DryRun)
        {
            run.Add(SyncStepName.Backup, SyncStepStatus.Skipped, "Dry run.");
            run.Add(SyncStepName.Publish, SyncStepStatus.Skipped, "Dry run.");
            return run.Done(PostmanSyncOutcome.DryRun, RemoteState.Untouched, plan.Message, plan.CollectionUid);
        }

        return plan.Outcome == PostmanPublishOutcome.WouldCreate
            ? await CreateAsync(request, run, cancellationToken)
            : await UpdateAsync(request, plan.CollectionUid!, run, cancellationToken);
    }

    // ---- the collection does not exist yet ----

    private async Task<PostmanSyncReport> CreateAsync(PostmanSyncRequest request, Run run, CancellationToken cancellationToken)
    {
        run.Add(SyncStepName.Backup, SyncStepStatus.Skipped, "No collection exists yet, so there is nothing to back up.");

        // Created directly, not through the repository: its lookup would run a second time and, if someone created the collection in the
        // meantime, would turn this into an overwrite that has no backup.
        var created = await client.CreateCollectionAsync(request.WorkspaceId, request.CollectionJson, cancellationToken);
        if (!created.Succeeded)
        {
            run.Add(SyncStepName.Publish, SyncStepStatus.Failed, created.Message);
            var unclear = IsAmbiguous(created.Status);
            return run.Done(
                PostmanSyncOutcome.Failed, unclear ? RemoteState.Unknown : RemoteState.Untouched,
                $"The collection could not be created: {created.Message}",
                recovery: unclear
                    ?
                    [
                        $"Postman may or may not have created the collection '{request.CollectionName}'. Look for it in the workspace.",
                        $"If it exists and carries the '{PostmanCollectionGenerator.ManagedMarkerVariable}' variable, the next run finds and updates it; if its content looks wrong, delete it in Postman (this tool never deletes collections) and run again."
                    ]
                    : null);
        }

        var uid = created.Value!.Uid;
        run.Add(SyncStepName.Publish, SyncStepStatus.Ok, "The collection was created.");

        var expected = CollectionProjection.Of(request.CollectionJson);
        var read = await client.GetCollectionAsync(uid, cancellationToken);
        if (!read.Succeeded)
        {
            run.Add(SyncStepName.Verify, SyncStepStatus.Inconclusive, $"The new collection could not be read back: {read.Message}");
            return run.Done(
                PostmanSyncOutcome.VerificationInconclusive, RemoteState.Unknown, "The collection was created but could not be read back, so it is unknown whether it is right.", uid,
                recovery: [$"Open the collection '{uid}' in Postman and check that it has the endpoints you expect.", "If it is wrong, delete it in Postman (this tool never deletes collections) and run again."]);
        }

        var differences = CollectionProjection.Differences(expected, CollectionProjection.Of(read.Value!));
        if (differences.Count == 0)
        {
            run.Add(SyncStepName.Verify, SyncStepStatus.Ok, "The new collection reads back exactly as intended.");
            run.Add(SyncStepName.Rollback, SyncStepStatus.Skipped, "Not needed.");
            return run.Done(PostmanSyncOutcome.Synced, RemoteState.Published, "The collection was created and verified. Keep its uid so later runs update this same collection.", uid);
        }

        run.Add(SyncStepName.Verify, SyncStepStatus.Failed, $"The new collection differs from what was sent in {differences.Count} place(s).");
        run.Add(SyncStepName.Rollback, SyncStepStatus.Skipped, "A collection that was just created cannot be rolled back: this tool never deletes collections.");
        return run.Done(
            PostmanSyncOutcome.VerificationFailed, RemoteState.Divergent, "The collection was created but does not match what was sent.", uid, differences,
            recovery:
            [
                $"Delete the collection '{uid}' in Postman (this tool never deletes collections). It carries the '{PostmanCollectionGenerator.ManagedMarkerVariable}' variable.",
                "Run the sync again; with no collection of that name left it creates a fresh one.",
                "If this repeats, the differences above (paths only) show where Postman changes what is sent; report it."
            ]);
    }

    // ---- the collection exists ----

    private async Task<PostmanSyncReport> UpdateAsync(PostmanSyncRequest request, string uid, Run run, CancellationToken cancellationToken)
    {
        // 3. Back up what is in Postman. Nothing is written until the copy is stored and has been read back intact.
        var current = await client.GetCollectionAsync(uid, cancellationToken);
        if (!current.Succeeded)
        {
            run.Add(SyncStepName.Backup, SyncStepStatus.Failed, $"The current collection could not be read, so it cannot be backed up: {current.Message}");
            return run.Done(PostmanSyncOutcome.Failed, RemoteState.Untouched, "Nothing was written: no backup could be made.", uid);
        }

        var original = current.Value!;
        var (redacted, redactedPaths) = SecretRedactor.Redact(original);
        var saved = await backups.SaveAsync(
            new CollectionBackup
            {
                CollectionUid = uid,
                CollectionName = request.CollectionName,
                WorkspaceId = request.WorkspaceId,
                TakenAtUtc = _clock.GetUtcNow(),
                CollectionJson = redacted,
                Sha256 = CollectionBackup.ComputeHash(redacted),
                RedactedPaths = redactedPaths
            },
            cancellationToken);

        if (!saved.Saved)
        {
            run.Add(SyncStepName.Backup, SyncStepStatus.Failed, saved.Message);
            return run.Done(PostmanSyncOutcome.Failed, RemoteState.Untouched, "Nothing was written: the backup could not be stored.", uid);
        }

        run.BackupLocation = saved.Location;
        run.Add(
            SyncStepName.Backup, SyncStepStatus.Ok,
            $"{saved.Message}{(redactedPaths.Count == 0 ? string.Empty : $" {redactedPaths.Count} secret-looking value(s) were redacted in the stored copy; this run's rollback uses the unredacted original it holds in memory.")}");

        var expected = CollectionProjection.Of(request.CollectionJson);
        var before = CollectionProjection.Of(original);

        // 4. Publish.
        var published = await _repository.PublishAsync(ToPublish(request, dryRun: false, uid), cancellationToken);
        IReadOnlyList<string> mismatches;
        var read = await client.GetCollectionAsync(uid, cancellationToken);

        if (published.Succeeded)
        {
            run.Add(SyncStepName.Publish, SyncStepStatus.Ok, "The collection was updated in place.");
            if (!read.Succeeded)
            {
                run.Add(SyncStepName.Verify, SyncStepStatus.Inconclusive, $"The collection could not be read back: {read.Message}");
                return run.Done(
                    PostmanSyncOutcome.VerificationInconclusive, RemoteState.Unknown,
                    "The collection was updated but could not be read back, so it is unknown whether it is right. It was not rolled back because nothing shows it is wrong.", uid,
                    recovery: ManualRecovery(uid, run.BackupLocation, request.CollectionName, "Check the collection in Postman. If it is wrong, restore it from the backup."));
            }

            mismatches = CollectionProjection.Differences(expected, CollectionProjection.Of(read.Value!));
            if (mismatches.Count == 0)
            {
                run.Add(SyncStepName.Verify, SyncStepStatus.Ok, "The collection reads back exactly as intended.");
                run.Add(SyncStepName.Rollback, SyncStepStatus.Skipped, "Not needed.");
                return run.Done(PostmanSyncOutcome.Synced, RemoteState.Published, "The collection was updated and verified.", uid);
            }

            run.Add(SyncStepName.Verify, SyncStepStatus.Failed, $"The collection differs from what was published in {mismatches.Count} place(s).");
        }
        else
        {
            run.Add(SyncStepName.Publish, SyncStepStatus.Failed, published.Message);

            // The call failed, but a failed call does not prove nothing happened (an answer can be lost after the change was applied): look.
            if (!read.Succeeded)
            {
                run.Add(SyncStepName.Verify, SyncStepStatus.Inconclusive, $"After the failed publish the collection could not be read: {read.Message}");
                return run.Done(
                    PostmanSyncOutcome.Failed, RemoteState.Unknown, $"The update failed and the state of the collection could not be checked: {published.Message}", uid,
                    recovery: ManualRecovery(uid, run.BackupLocation, request.CollectionName, "Check the collection in Postman. If it is wrong, restore it from the backup."));
            }

            var now = CollectionProjection.Of(read.Value!);
            if (CollectionProjection.Differences(expected, now).Count == 0)
            {
                run.Add(SyncStepName.Verify, SyncStepStatus.Ok, "The publish reported a failure, but the collection reads back exactly as intended.");
                return run.Done(PostmanSyncOutcome.Synced, RemoteState.Published, "The collection reads back as intended although the publish call reported a failure.", uid);
            }

            if (CollectionProjection.Differences(before, now).Count == 0)
            {
                run.Add(SyncStepName.Verify, SyncStepStatus.Ok, "The collection reads back unchanged.");
                run.Add(SyncStepName.Rollback, SyncStepStatus.Skipped, "Nothing to roll back.");
                return run.Done(PostmanSyncOutcome.Failed, RemoteState.Unchanged, $"The update failed and the collection is unchanged: {published.Message}", uid);
            }

            mismatches = CollectionProjection.Differences(expected, now);
            run.Add(SyncStepName.Verify, SyncStepStatus.Failed, "The collection is in a state that is neither the old nor the intended one.");
        }

        // 5. What is in Postman is wrong: restore the backup if allowed.
        if (!request.AutoRollback)
        {
            run.Add(SyncStepName.Rollback, SyncStepStatus.Skipped, "Automatic rollback is turned off.");
            return run.Done(
                PostmanSyncOutcome.VerificationFailed, RemoteState.Divergent, "The collection in Postman is not what was meant and was left as it is (automatic rollback is off).", uid, mismatches,
                recovery: ManualRecovery(uid, run.BackupLocation, request.CollectionName, "Inspect the collection, then restore it from the backup or run the sync again."));
        }

        return await RollbackAsync(uid, original, before, run, mismatches, request.CollectionName, cancellationToken);
    }

    private async Task<PostmanSyncReport> RollbackAsync(
        string uid, string original, SortedDictionary<string, string> before, Run run, IReadOnlyList<string> mismatches, string name,
        CancellationToken cancellationToken)
    {
        var restore = await client.UpdateCollectionAsync(uid, RestorePayload(original), cancellationToken);
        run.Add(
            SyncStepName.Rollback, restore.Succeeded ? SyncStepStatus.Ok : SyncStepStatus.Failed,
            restore.Succeeded ? "The previous collection was written back." : $"The rollback request failed: {restore.Message}");

        // Whether the rollback call succeeded or not, what counts is what Postman holds now.
        var read = await client.GetCollectionAsync(uid, cancellationToken);
        if (!read.Succeeded)
        {
            run.Add(SyncStepName.VerifyRollback, SyncStepStatus.Inconclusive, $"After the rollback the collection could not be read: {read.Message}");
            return run.Done(
                PostmanSyncOutcome.RollbackFailed, RemoteState.Unknown, "The rollback could not be verified: the collection in Postman may be in a wrong state.", uid, mismatches,
                recovery: ManualRecovery(uid, run.BackupLocation, name, "The rollback itself may have failed. Restore the collection by hand from the backup."));
        }

        var remaining = CollectionProjection.Differences(before, CollectionProjection.Of(read.Value!));
        if (remaining.Count == 0)
        {
            run.Add(SyncStepName.VerifyRollback, SyncStepStatus.Ok, "The collection reads back exactly as it was before the sync.");
            return run.Done(
                PostmanSyncOutcome.RolledBack, RemoteState.Restored,
                restore.Succeeded
                    ? "What was published did not verify, so the previous collection was restored and the restore was verified."
                    : "What was published did not verify. The rollback request reported a failure, but the collection reads back as it was.",
                uid, mismatches);
        }

        run.Add(SyncStepName.VerifyRollback, SyncStepStatus.Failed, $"After the rollback the collection still differs from the backup in {remaining.Count} place(s).");
        return run.Done(
            PostmanSyncOutcome.RollbackFailed, RemoteState.Divergent,
            "The rollback failed: the collection in Postman is in neither its old nor its intended state.", uid, mismatches, remaining,
            ManualRecovery(uid, run.BackupLocation, name, "The rollback did not restore the collection. Restore it by hand from the backup."));
    }

    // ---- restoring from a stored backup (the explicit recovery path) ----

    /// <summary>
    /// Writes a stored backup back to Postman and verifies the result. A backup that had secrets redacted is refused unless the caller
    /// accepts that the placeholders will be written back where the secrets were.
    /// </summary>
    public async Task<PostmanSyncReport> RestoreAsync(CollectionBackup backup, bool acceptRedactedValues = false, CancellationToken cancellationToken = default)
    {
        var run = new Run { BackupLocation = null };

        if (CollectionBackup.ComputeHash(backup.CollectionJson) != backup.Sha256)
        {
            run.Add(SyncStepName.Preflight, SyncStepStatus.Failed, "The backup's hash does not match its content; it is corrupt or was edited.");
            return run.Done(PostmanSyncOutcome.Failed, RemoteState.Untouched, "Nothing was written: the backup cannot be trusted.", backup.CollectionUid);
        }

        if (backup.RedactedPaths.Count > 0 && !acceptRedactedValues)
        {
            run.Add(SyncStepName.Preflight, SyncStepStatus.Failed, $"The backup has {backup.RedactedPaths.Count} redacted secret value(s); restoring it would write the placeholder '{SecretRedactor.Placeholder}' where they were.");
            return run.Done(PostmanSyncOutcome.Failed, RemoteState.Untouched, "Nothing was written: restoring would replace secrets with placeholders. Accept that explicitly, or re-enter them afterwards.", backup.CollectionUid);
        }

        run.Add(SyncStepName.Preflight, SyncStepStatus.Ok, "The backup is intact.");

        var exists = await client.GetCollectionAsync(backup.CollectionUid, cancellationToken);
        if (!exists.Succeeded)
        {
            run.Add(SyncStepName.Plan, SyncStepStatus.Failed, $"The collection could not be read: {exists.Message}");
            return run.Done(PostmanSyncOutcome.Failed, RemoteState.Untouched, "Nothing was written: the collection to restore into could not be read.", backup.CollectionUid);
        }

        run.Add(SyncStepName.Plan, SyncStepStatus.Ok, "The collection exists.");
        var restore = await client.UpdateCollectionAsync(backup.CollectionUid, RestorePayload(backup.CollectionJson), cancellationToken);
        run.Add(SyncStepName.Rollback, restore.Succeeded ? SyncStepStatus.Ok : SyncStepStatus.Failed, restore.Succeeded ? "The backup was written back." : $"The restore request failed: {restore.Message}");

        var read = await client.GetCollectionAsync(backup.CollectionUid, cancellationToken);
        if (!read.Succeeded)
        {
            run.Add(SyncStepName.VerifyRollback, SyncStepStatus.Inconclusive, $"After the restore the collection could not be read: {read.Message}");
            return run.Done(PostmanSyncOutcome.RollbackFailed, RemoteState.Unknown, "The restore could not be verified.", backup.CollectionUid,
                recovery: ManualRecovery(backup.CollectionUid, null, backup.CollectionName, "Check the collection in Postman."));
        }

        var differences = CollectionProjection.Differences(CollectionProjection.Of(backup.CollectionJson), CollectionProjection.Of(read.Value!));
        if (differences.Count == 0)
        {
            run.Add(SyncStepName.VerifyRollback, SyncStepStatus.Ok, "The collection reads back exactly as the backup.");
            return run.Done(PostmanSyncOutcome.RolledBack, RemoteState.Restored, "The backup was restored and verified.", backup.CollectionUid);
        }

        run.Add(SyncStepName.VerifyRollback, SyncStepStatus.Failed, $"After the restore the collection still differs from the backup in {differences.Count} place(s).");
        return run.Done(PostmanSyncOutcome.RollbackFailed, RemoteState.Divergent, "The restore did not reproduce the backup.", backup.CollectionUid, rollbackMismatches: differences,
            recovery: ManualRecovery(backup.CollectionUid, null, backup.CollectionName, "Import the backup file into Postman (Import, File) and compare it with the collection."));
    }

    // ---- helpers ----

    private static PostmanPublishRequest ToPublish(PostmanSyncRequest request, bool dryRun, string? known) => new()
    {
        WorkspaceId = request.WorkspaceId,
        CollectionName = request.CollectionName,
        CollectionJson = request.CollectionJson,
        KnownCollectionId = known,
        Decision = request.Decision,
        DryRun = dryRun
    };

    /// <summary>
    /// Everything that must hold before anything is sent: the decision belongs to the change set, and the collection holds exactly the
    /// folders and requests the change set says exist (their ids are derived from the endpoint identities and the id namespace).
    /// </summary>
    private static string? Preflight(PostmanSyncRequest request)
    {
        if (request.Decision.Summary != request.Changes.Summary)
        {
            return "The safety decision and the change set do not belong together (their summaries differ).";
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(request.CollectionJson);
        }
        catch (JsonException)
        {
            return "The collection is not valid JSON.";
        }

        if (root is not JsonObject collection || collection["item"] is not JsonArray folders)
        {
            return "The JSON is not a Postman collection.";
        }

        var live = request.Changes.Endpoints.Where(e => e.Status != ChangeStatus.Removed).ToList();
        var expectedRequests = live.ToDictionary(e => DeterministicId.For($"{request.IdNamespace}|request|{e.Id}"), e => e.Id);
        var expectedFolders = live.Select(e => e.ControllerName).Distinct().ToDictionary(c => DeterministicId.For($"{request.IdNamespace}|folder|{c}"), c => c);

        var actualRequests = new HashSet<string>();
        var actualFolders = new HashSet<string>();
        foreach (var folder in folders.OfType<JsonObject>())
        {
            if ((string?)folder["id"] is not { Length: > 0 } folderId)
            {
                return "A folder has no id; generate the collection with an id namespace.";
            }

            actualFolders.Add(folderId);
            foreach (var item in (folder["item"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((string?)item["id"] is not { Length: > 0 } requestId)
                {
                    return "A request has no id; generate the collection with an id namespace.";
                }

                actualRequests.Add(requestId);
            }
        }

        var missing = expectedRequests.Where(e => !actualRequests.Contains(e.Key)).Select(e => e.Value).OrderBy(i => i, StringComparer.Ordinal).ToList();
        var unexpected = actualRequests.Count(id => !expectedRequests.ContainsKey(id));
        if (missing.Count > 0 || unexpected > 0 || actualFolders.Count != expectedFolders.Count || !actualFolders.All(expectedFolders.ContainsKey))
        {
            return $"The collection does not match the change set: {missing.Count} expected endpoint(s) are missing" +
                   $"{(missing.Count > 0 ? $" (first: {missing[0]})" : string.Empty)}, {unexpected} request(s) are not expected, " +
                   $"{expectedFolders.Count} folder(s) expected and {actualFolders.Count} found. It may be stale or generated for another namespace.";
        }

        return null;
    }

    /// <summary>The previous collection in the form Postman accepts for a replace: its read-only <c>info</c> fields (timestamps, uid, owner) are left out.</summary>
    internal static string RestorePayload(string collectionJson)
    {
        var collection = (JsonObject)JsonNode.Parse(collectionJson)!;
        if (collection["info"] is JsonObject info)
        {
            var clean = new JsonObject();
            foreach (var key in new[] { "name", "description" })
            {
                if (info[key] is { } value)
                {
                    clean[key] = value.DeepClone();
                }
            }

            clean["schema"] = PostmanCloudClient.CollectionSchema;
            collection["info"] = clean;
        }

        return collection.ToJsonString();
    }

    /// <summary>A failed create is unclear when Postman may have created the collection anyway (no answer, a server error, an unreadable answer).</summary>
    private static bool IsAmbiguous(PostmanApiStatus status) =>
        status is PostmanApiStatus.Timeout or PostmanApiStatus.NetworkError or PostmanApiStatus.ServerError or PostmanApiStatus.UnexpectedResponse;

    private static List<string> ManualRecovery(string uid, string? backupLocation, string name, string first)
    {
        var steps = new List<string> { first };
        if (backupLocation is not null)
        {
            steps.Add($"The backup of '{name}' (uid {uid}) is at: {backupLocation}. Secret-looking values in it were replaced by '{SecretRedactor.Placeholder}'.");
            steps.Add("To put it back with this tool, restore that backup (RestoreAsync): it refuses a backup with redacted secrets unless you accept the placeholders, in which case re-enter those values in Postman afterwards.");
            steps.Add("Or import the backup file into Postman (Import, File) to get a copy to compare with and copy from.");
        }

        steps.Add("Do not run the sync again until the collection is as you want it: another publish replaces the whole collection.");
        return steps;
    }
}
