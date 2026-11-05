namespace ApiCollection.Core.Diff;

public enum ChangeStatus
{
    Added,
    Modified,
    Removed,
    Unchanged
}

/// <summary>What part of the contract a change belongs to.</summary>
public enum ChangeCategory
{
    /// <summary>Controller or action name (the identity, and so the URL, did not change).</summary>
    Naming,

    /// <summary>The route as written (case, parameter names) changed while the identity stayed the same.</summary>
    Route,
    Parameter,
    RequestBody,
    Response,
    Authentication,
    Validation,

    /// <summary>Example values, documented examples and literal defaults.</summary>
    Example,
    Other
}

public enum ChangeKind
{
    Added,
    Removed,
    Changed
}

/// <summary>
/// One detected difference. <see cref="Path"/> is the contract fact (<c>auth.required</c>) or, for a whole added/removed
/// entity, the entity (<c>parameter[query:page]</c>) with its attributes summarised in <see cref="After"/>/<see cref="Before"/>.
/// </summary>
public sealed record ContractChange
{
    public required ChangeCategory Category { get; init; }
    public required ChangeKind Kind { get; init; }
    public required string Path { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
}

public sealed record EndpointDiff
{
    public required string Id { get; init; }
    public required ChangeStatus Status { get; init; }
    public required string HttpMethod { get; init; }

    /// <summary>The route as written (the current one, or the previous one for a removed endpoint).</summary>
    public required string Route { get; init; }

    public required string ControllerName { get; init; }
    public required string ActionName { get; init; }

    /// <summary>Only for <see cref="ChangeStatus.Modified"/>: what differs, ordered by path.</summary>
    public IReadOnlyList<ContractChange> Changes { get; init; } = [];

    /// <summary>
    /// A suggestion only. For an added endpoint the identity it may have been renamed from; for a removed endpoint the identity
    /// it may have become. Both endpoints stay Added/Removed; nothing is merged on a guess.
    /// </summary>
    public string? PossibleRename { get; init; }
}

public sealed record ChangeSetSummary(int Added, int Modified, int Removed, int Unchanged)
{
    public int Total => Added + Modified + Removed + Unchanged;
}

/// <summary>
/// The difference between a baseline snapshot and the current analysis: machine-readable, ordered by endpoint identity,
/// free of timestamps and paths. Every endpoint of either side appears exactly once.
/// </summary>
public sealed record ChangeSet
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required ChangeSetSummary Summary { get; init; }

    /// <summary>True when anything was added, modified or removed.</summary>
    public bool HasChanges => Summary.Added + Summary.Modified + Summary.Removed > 0;

    /// <summary>
    /// Reasons to distrust the result, such as an incomplete discovery (a removal may only be a failed discovery) or a
    /// baseline collection that was changed after the snapshot was taken. A later policy decides what to do about them.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<EndpointDiff> Endpoints { get; init; } = [];
}
