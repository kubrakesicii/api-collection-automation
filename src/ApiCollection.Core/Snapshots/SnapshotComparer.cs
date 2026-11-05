namespace ApiCollection.Core.Snapshots;

public sealed record EndpointChange(SnapshotEndpoint Previous, SnapshotEndpoint Current);

/// <summary>
/// Result of matching two snapshots. Rename candidates are only suggestions: the endpoints involved also stay in
/// <see cref="Added"/> and <see cref="Removed"/>, so nothing is merged or deleted on a guess.
/// </summary>
public sealed record SnapshotComparison(
    IReadOnlyList<SnapshotEndpoint> Unchanged,
    IReadOnlyList<EndpointChange> Modified,
    IReadOnlyList<SnapshotEndpoint> Added,
    IReadOnlyList<SnapshotEndpoint> Removed,
    IReadOnlyList<EndpointChange> RenameCandidates);

public static class SnapshotComparer
{
    /// <summary>
    /// Matches endpoints by identity; a matched pair is Unchanged when the fingerprints are equal, otherwise Modified.
    /// A removed and an added endpoint become a rename candidate when they have the same controller and action and are the
    /// only such pair (the HTTP method and/or route changed). Output is ordered by identity.
    /// </summary>
    /// <exception cref="InvalidOperationException">The snapshots use different schema or identity versions.</exception>
    public static SnapshotComparison Compare(AnalysisSnapshot previous, AnalysisSnapshot current)
    {
        if (previous.SchemaVersion != current.SchemaVersion || previous.IdentityVersion != current.IdentityVersion)
        {
            throw new InvalidOperationException(
                $"Snapshots are not comparable: schema {previous.SchemaVersion}/{current.SchemaVersion}, " +
                $"identity rules {previous.IdentityVersion}/{current.IdentityVersion}. Create a new baseline snapshot.");
        }

        var before = previous.Endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var after = current.Endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);

        var unchanged = new List<SnapshotEndpoint>();
        var modified = new List<EndpointChange>();
        foreach (var endpoint in current.Endpoints.Where(e => before.ContainsKey(e.Id)))
        {
            var old = before[endpoint.Id];
            if (old.Fingerprint == endpoint.Fingerprint)
            {
                unchanged.Add(endpoint);
            }
            else
            {
                modified.Add(new EndpointChange(old, endpoint));
            }
        }

        var added = current.Endpoints.Where(e => !before.ContainsKey(e.Id)).ToList();
        var removed = previous.Endpoints.Where(e => !after.ContainsKey(e.Id)).ToList();

        var candidates = new List<EndpointChange>();
        foreach (var gone in removed)
        {
            var sameAction = (ControllerName: gone.ControllerName, ActionName: gone.ActionName);
            var removedWithAction = removed.Count(r => (r.ControllerName, r.ActionName) == sameAction);
            var addedWithAction = added.Where(a => (a.ControllerName, a.ActionName) == sameAction).ToList();
            if (removedWithAction == 1 && addedWithAction.Count == 1)
            {
                candidates.Add(new EndpointChange(gone, addedWithAction[0]));
            }
        }

        return new SnapshotComparison(unchanged, modified, added, removed, candidates);
    }
}
