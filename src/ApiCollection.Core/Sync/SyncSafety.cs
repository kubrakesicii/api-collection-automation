using ApiCollection.Core.Diff;

namespace ApiCollection.Core.Sync;

/// <summary>Limits and explicit overrides for <see cref="SyncSafetyValidator"/>. The defaults are the safe ones.</summary>
public sealed record SyncSafetyOptions
{
    /// <summary>A sync that would delete more than this share (0-100) of the baseline's endpoints is blocked.</summary>
    public double MaxRemovedPercent { get; init; } = 30;

    /// <summary>A sync that would delete more than this many endpoints is blocked.</summary>
    public int MaxRemovedCount { get; init; } = 10;

    /// <summary>Proceed although discovery reported failures that may have hidden endpoints. They stay listed in the decision.</summary>
    public bool AllowIncompleteDiscovery { get; init; }
}

/// <summary>What is known about the stored collection the baseline snapshot was taken with.</summary>
public enum BaselineCollectionState
{
    /// <summary>There is no baseline yet (the first sync); nothing can be overwritten or deleted.</summary>
    NotApplicable,

    /// <summary>The stored collection is exactly the one the baseline describes.</summary>
    Verified,

    /// <summary>The stored collection differs from the baseline: it was edited after it was generated.</summary>
    Changed,

    /// <summary>The stored collection file does not exist or cannot be read.</summary>
    Missing,

    /// <summary>The baseline has no collection reference, so it cannot be verified.</summary>
    NoReference,

    /// <summary>The baseline refers to a different collection file than the one being synced.</summary>
    NameMismatch
}

public sealed record SyncSafetyInput
{
    public required ChangeSet Changes { get; init; }

    public BaselineCollectionState BaselineCollection { get; init; } = BaselineCollectionState.NotApplicable;

    /// <summary>Discovery failures that may have hidden endpoints (see <c>EndpointDiscoveryResult.Failures</c>).</summary>
    public IReadOnlyList<string> DiscoveryFailures { get; init; } = [];

    /// <summary>Discovery warnings that only make endpoint details less complete; reported, never blocking.</summary>
    public IReadOnlyList<string> DiscoveryWarnings { get; init; } = [];
}

public enum SyncViolationCode
{
    /// <summary>Nothing was discovered although the baseline has endpoints: almost certainly a failed analysis. Never overridable.</summary>
    DiscoveryFailed,

    /// <summary>Discovery reported failures that may have hidden endpoints. Overridable with <see cref="SyncSafetyOptions.AllowIncompleteDiscovery"/>.</summary>
    DiscoveryIncomplete,

    /// <summary>The stored collection was edited after the baseline was taken; syncing would overwrite those edits. Not overridable.</summary>
    BaselineCollectionChanged,

    /// <summary>The stored collection is missing or unreadable, so what is being replaced is unknown. Not overridable.</summary>
    BaselineCollectionMissing,

    /// <summary>The baseline cannot be tied to the collection being synced. Not overridable.</summary>
    BaselineUnverifiable,

    /// <summary>Too many endpoints would be deleted at once. Overridable by raising the limits explicitly.</summary>
    MassDeletion
}

public sealed record SyncViolation(SyncViolationCode Code, string Message, bool CanOverride);

public enum SyncOutcome
{
    /// <summary>Nothing differs from the baseline: nothing to write or publish.</summary>
    NoChanges,

    /// <summary>Safe to apply the planned actions.</summary>
    Allowed,

    /// <summary>At least one violation: nothing may be written or published.</summary>
    Blocked
}

public enum SyncAction
{
    Create,
    Update,
    Delete
}

public sealed record PlannedAction(SyncAction Action, string Id, string ControllerName, string ActionName);

public sealed record SyncDecision
{
    public required SyncOutcome Outcome { get; init; }

    public IReadOnlyList<SyncViolation> Violations { get; init; } = [];

    /// <summary>Non-blocking remarks: accepted overrides and warnings that only affect details.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public required ChangeSetSummary Summary { get; init; }

    /// <summary>What applying the sync would do, ordered by identity. Empty when nothing changed.</summary>
    public IReadOnlyList<PlannedAction> Plan { get; init; } = [];

    public required SyncSafetyOptions Limits { get; init; }
}
