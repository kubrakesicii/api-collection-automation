using ApiCollection.Core.Snapshots;

namespace ApiCollection.Core.Diff;

public static class ChangeSetBuilder
{
    /// <summary>
    /// Classifies every endpoint as Added, Modified, Removed or Unchanged (matching by identity, see <see cref="SnapshotComparer"/>)
    /// and, for modified ones, lists the contract changes. Rename candidates are attached as suggestions only.
    /// </summary>
    /// <param name="warnings">Reasons to distrust the result, carried into <see cref="ChangeSet.Warnings"/> as given.</param>
    /// <exception cref="InvalidOperationException">The snapshots use different schema or identity versions.</exception>
    public static ChangeSet Build(AnalysisSnapshot previous, AnalysisSnapshot current, IEnumerable<string>? warnings = null)
    {
        var comparison = SnapshotComparer.Compare(previous, current);
        var renamedTo = comparison.RenameCandidates.ToDictionary(c => c.Previous.Id, c => c.Current.Id, StringComparer.Ordinal);
        var renamedFrom = comparison.RenameCandidates.ToDictionary(c => c.Current.Id, c => c.Previous.Id, StringComparer.Ordinal);

        var diffs = new List<EndpointDiff>();
        diffs.AddRange(comparison.Added.Select(e => Diff(ChangeStatus.Added, e, [], renamedFrom.GetValueOrDefault(e.Id))));
        diffs.AddRange(comparison.Removed.Select(e => Diff(ChangeStatus.Removed, e, [], renamedTo.GetValueOrDefault(e.Id))));
        diffs.AddRange(comparison.Modified.Select(c => Diff(ChangeStatus.Modified, c.Current, ContractDiff.Compute(c.Previous.Contract, c.Current.Contract), null)));
        diffs.AddRange(comparison.Unchanged.Select(e => Diff(ChangeStatus.Unchanged, e, [], null)));

        return new ChangeSet
        {
            Summary = new ChangeSetSummary(comparison.Added.Count, comparison.Modified.Count, comparison.Removed.Count, comparison.Unchanged.Count),
            Warnings = (warnings ?? []).ToList(),
            Endpoints = diffs.OrderBy(d => d.Id, StringComparer.Ordinal).ToList()
        };
    }

    private static EndpointDiff Diff(ChangeStatus status, SnapshotEndpoint endpoint, IReadOnlyList<ContractChange> changes, string? possibleRename) => new()
    {
        Id = endpoint.Id,
        Status = status,
        HttpMethod = endpoint.HttpMethod,
        Route = endpoint.Route,
        ControllerName = endpoint.ControllerName,
        ActionName = endpoint.ActionName,
        Changes = changes,
        PossibleRename = possibleRename
    };
}
