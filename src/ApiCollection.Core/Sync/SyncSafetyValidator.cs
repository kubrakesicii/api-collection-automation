using System.Globalization;
using ApiCollection.Core.Diff;

namespace ApiCollection.Core.Sync;

/// <summary>
/// The gate every publish passes through: decides, from the change set and how much the analysis can be trusted, whether
/// applying it is safe. It fails closed: anything uncertain blocks, and a person must say so explicitly to go ahead.
/// </summary>
public static class SyncSafetyValidator
{
    public static SyncDecision Validate(SyncSafetyInput input, SyncSafetyOptions? options = null)
    {
        options ??= new SyncSafetyOptions();
        var summary = input.Changes.Summary;
        var baselineTotal = summary.Modified + summary.Removed + summary.Unchanged;
        var discovered = summary.Added + summary.Modified + summary.Unchanged;

        var violations = new List<SyncViolation>();
        var notes = new List<string>();

        CheckBaselineCollection(input.BaselineCollection, violations);

        if (baselineTotal > 0 && discovered == 0)
        {
            violations.Add(new SyncViolation(
                SyncViolationCode.DiscoveryFailed,
                $"No endpoint was discovered although the baseline has {baselineTotal}; this is treated as a failed analysis, not as a request to delete everything.",
                CanOverride: false));
        }

        if (input.DiscoveryFailures.Count > 0)
        {
            var detail = $"{input.DiscoveryFailures.Count} discovery failure(s) may have hidden endpoints, so a missing endpoint cannot be trusted to be deleted: {First(input.DiscoveryFailures)}";
            if (options.AllowIncompleteDiscovery)
            {
                notes.Add($"Incomplete discovery was accepted explicitly. {detail}");
            }
            else
            {
                violations.Add(new SyncViolation(SyncViolationCode.DiscoveryIncomplete, detail, CanOverride: true));
            }
        }

        if (input.DiscoveryWarnings.Count > 0)
        {
            notes.Add($"{input.DiscoveryWarnings.Count} discovery warning(s) only make endpoint details less complete; they do not block the sync.");
        }

        CheckMassDeletion(summary.Removed, baselineTotal, options, violations);

        var outcome = violations.Count > 0 ? SyncOutcome.Blocked : input.Changes.HasChanges ? SyncOutcome.Allowed : SyncOutcome.NoChanges;

        return new SyncDecision
        {
            Outcome = outcome,
            Violations = violations,
            Notes = notes,
            Summary = summary,
            Plan = outcome == SyncOutcome.NoChanges ? [] : PlanOf(input.Changes),
            Limits = options
        };
    }

    private static void CheckBaselineCollection(BaselineCollectionState state, List<SyncViolation> violations)
    {
        switch (state)
        {
            case BaselineCollectionState.Changed:
                violations.Add(new SyncViolation(
                    SyncViolationCode.BaselineCollectionChanged,
                    "The stored collection differs from the one the baseline was taken with (edited after it was generated); syncing would overwrite those edits. Re-baseline deliberately first.",
                    CanOverride: false));
                break;
            case BaselineCollectionState.Missing:
                violations.Add(new SyncViolation(
                    SyncViolationCode.BaselineCollectionMissing,
                    "The stored collection the baseline refers to is missing or unreadable, so what would be replaced is unknown.",
                    CanOverride: false));
                break;
            case BaselineCollectionState.NoReference:
                violations.Add(new SyncViolation(
                    SyncViolationCode.BaselineUnverifiable,
                    "The baseline does not reference a collection, so it cannot be checked against the stored one. Create it with a collection (generate --out --snapshot).",
                    CanOverride: false));
                break;
            case BaselineCollectionState.NameMismatch:
                violations.Add(new SyncViolation(
                    SyncViolationCode.BaselineUnverifiable,
                    "The baseline refers to a different collection file than the one being synced.",
                    CanOverride: false));
                break;
        }
    }

    private static void CheckMassDeletion(int removed, int baselineTotal, SyncSafetyOptions options, List<SyncViolation> violations)
    {
        if (removed == 0 || baselineTotal == 0)
        {
            return;
        }

        var percent = 100.0 * removed / baselineTotal;
        if (removed > options.MaxRemovedCount || percent > options.MaxRemovedPercent)
        {
            violations.Add(new SyncViolation(
                SyncViolationCode.MassDeletion,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{removed} of {baselineTotal} endpoint(s) ({percent:0.#}%) would be deleted; the limits are {options.MaxRemovedCount} endpoint(s) and {options.MaxRemovedPercent:0.#}%. Raise --max-removed / --max-removed-percent if this is intended."),
                CanOverride: true));
        }
    }

    private static List<PlannedAction> PlanOf(ChangeSet changes) => changes.Endpoints
        .Where(e => e.Status != ChangeStatus.Unchanged)
        .Select(e => new PlannedAction(
            e.Status switch { ChangeStatus.Added => SyncAction.Create, ChangeStatus.Modified => SyncAction.Update, _ => SyncAction.Delete },
            e.Id, e.ControllerName, e.ActionName))
        .ToList();

    private static string First(IReadOnlyList<string> failures) =>
        failures.Count == 1 ? failures[0] : $"{failures[0]} (and {failures.Count - 1} more)";
}
