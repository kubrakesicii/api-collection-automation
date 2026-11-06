using System.Text;
using ApiCollection.Core.Diff;
using ApiCollection.Core.Sync;

namespace ApiCollection.Postman.Sync;

public sealed record PostmanSyncRequest
{
    public required string WorkspaceId { get; init; }

    public required string CollectionName { get; init; }

    /// <summary>The collection as generated with <c>IdNamespace</c> set (stable ids and the marker).</summary>
    public required string CollectionJson { get; init; }

    /// <summary>The namespace the collection's ids were generated with; the change set is checked against ids derived from it.</summary>
    public required string IdNamespace { get; init; }

    /// <summary>The uid returned by an earlier sync, when the caller has kept it.</summary>
    public string? KnownCollectionId { get; init; }

    /// <summary>The verdict of the sync safety gate (ADR 0003).</summary>
    public required SyncDecision Decision { get; init; }

    /// <summary>The change set the decision was made on. The collection to publish must contain exactly the endpoints it says exist.</summary>
    public required ChangeSet Changes { get; init; }

    /// <summary>Plan only: read from Postman, write nothing (no backup, no publish).</summary>
    public bool DryRun { get; init; }

    /// <summary>Restore the previous collection when what was published does not verify. Turn off only to inspect a failed publish by hand.</summary>
    public bool AutoRollback { get; init; } = true;
}

public enum PostmanSyncOutcome
{
    /// <summary>Published, read back and found as intended.</summary>
    Synced,

    /// <summary>Nothing to do; nothing was written.</summary>
    NoChanges,

    /// <summary>Plan only; nothing was written.</summary>
    DryRun,

    /// <summary>The safety gate blocked it; nothing was sent.</summary>
    Blocked,

    /// <summary>Stopped without leaving Postman changed (see <see cref="PostmanSyncReport.RemoteState"/> for what is known).</summary>
    Failed,

    /// <summary>Published, but what Postman holds is not what was meant and it was not rolled back (rollback is off, or there was nothing to restore).</summary>
    VerificationFailed,

    /// <summary>Published, but the collection could not be read back, so it is unknown whether it is right.</summary>
    VerificationInconclusive,

    /// <summary>What was published did not verify; the previous collection was restored and the restore was verified.</summary>
    RolledBack,

    /// <summary>The rollback itself failed or did not verify: the collection in Postman may be in a wrong state. See the recovery instructions.</summary>
    RollbackFailed
}

/// <summary>What is known about the collection in Postman after the run, from reading it back where that was possible.</summary>
public enum RemoteState
{
    /// <summary>Never written to.</summary>
    Untouched,

    /// <summary>Read back and equal to what it was before.</summary>
    Unchanged,

    /// <summary>Read back and equal to what was published.</summary>
    Published,

    /// <summary>Read back after a rollback and equal to what it was before.</summary>
    Restored,

    /// <summary>Read back and equal to neither the intended nor the previous state.</summary>
    Divergent,

    /// <summary>Could not be read back, so nothing can be said.</summary>
    Unknown
}

public enum SyncStepName
{
    Preflight,
    Plan,
    Backup,
    Publish,
    Verify,
    Rollback,
    VerifyRollback
}

public enum SyncStepStatus
{
    Ok,
    Skipped,
    Failed,
    Inconclusive
}

public sealed record SyncStep(SyncStepName Name, SyncStepStatus Status, string Message);

/// <summary>
/// The account of one sync run: every step with its outcome, what is known about the collection in Postman afterwards, the backup, the
/// verification differences, and, when something went wrong, explicit instructions for recovering. It never contains the API key or a
/// value from the collection (differences are listed by path only).
/// </summary>
public sealed record PostmanSyncReport
{
    public required PostmanSyncOutcome Outcome { get; init; }

    public required RemoteState RemoteState { get; init; }

    public required string Message { get; init; }

    public string? CollectionUid { get; init; }

    public string? BackupLocation { get; init; }

    public IReadOnlyList<SyncStep> Steps { get; init; } = [];

    /// <summary>How the collection in Postman differed from what was published (fact paths only).</summary>
    public IReadOnlyList<string> Mismatches { get; init; } = [];

    /// <summary>How the collection in Postman differed from the backup after a rollback (fact paths only).</summary>
    public IReadOnlyList<string> RollbackMismatches { get; init; } = [];

    public IReadOnlyList<string> RecoveryInstructions { get; init; } = [];

    public bool Succeeded => Outcome is PostmanSyncOutcome.Synced or PostmanSyncOutcome.NoChanges or PostmanSyncOutcome.DryRun;

    /// <summary>True when the collection in Postman may be left in a state nobody checked or wanted: the run needs a person.</summary>
    public bool NeedsAttention => Outcome is PostmanSyncOutcome.RollbackFailed or PostmanSyncOutcome.VerificationFailed or PostmanSyncOutcome.VerificationInconclusive
        || (Outcome == PostmanSyncOutcome.Failed && RemoteState is RemoteState.Unknown or RemoteState.Divergent);

    public string ToText()
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append('\n');

        Line($"Postman sync: {Outcome switch
        {
            PostmanSyncOutcome.Synced => "SYNCED",
            PostmanSyncOutcome.NoChanges => "NO CHANGES",
            PostmanSyncOutcome.DryRun => "DRY RUN",
            PostmanSyncOutcome.Blocked => "BLOCKED",
            PostmanSyncOutcome.Failed => "FAILED",
            PostmanSyncOutcome.VerificationFailed => "VERIFICATION FAILED",
            PostmanSyncOutcome.VerificationInconclusive => "VERIFICATION INCONCLUSIVE",
            PostmanSyncOutcome.RolledBack => "ROLLED BACK",
            _ => "ROLLBACK FAILED"
        }}");
        Line($"Collection:   {CollectionUid ?? "(none)"}");
        Line($"Remote state: {RemoteState}");
        Line($"Backup:       {BackupLocation ?? "(none taken)"}");
        Line(Message);

        Line();
        Line("Steps");
        foreach (var step in Steps)
        {
            var status = step.Status switch { SyncStepStatus.Ok => "ok", SyncStepStatus.Skipped => "skipped", SyncStepStatus.Failed => "FAILED", _ => "INCONCLUSIVE" };
            Line($"  [{status}] {step.Name}: {step.Message}");
        }

        if (Mismatches.Count > 0)
        {
            Line();
            Line($"Differences between what was published and what Postman holds ({Mismatches.Count}, paths only)");
            foreach (var mismatch in Mismatches)
            {
                Line($"  {mismatch}");
            }
        }

        if (RollbackMismatches.Count > 0)
        {
            Line();
            Line($"Differences between the backup and what Postman holds after the rollback ({RollbackMismatches.Count}, paths only)");
            foreach (var mismatch in RollbackMismatches)
            {
                Line($"  {mismatch}");
            }
        }

        if (RecoveryInstructions.Count > 0)
        {
            Line();
            Line("Recovery");
            for (var i = 0; i < RecoveryInstructions.Count; i++)
            {
                Line($"  {i + 1}. {RecoveryInstructions[i]}");
            }
        }

        return text.ToString();
    }
}
