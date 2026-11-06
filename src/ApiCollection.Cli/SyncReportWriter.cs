using System.Globalization;
using System.Text;
using ApiCollection.Core.Diff;
using ApiCollection.Core.Sync;

namespace ApiCollection.Cli;

/// <summary>Renders the sync decision (the dry-run report): what would happen, what blocks it and why. Deterministic (LF, no timestamps).</summary>
internal static class SyncReportWriter
{
    public static string ToText(SyncDecision decision, ChangeSet changes, bool dryRun, bool baselineExists)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append('\n');

        var outcome = decision.Outcome switch
        {
            SyncOutcome.Allowed => "ALLOWED",
            SyncOutcome.Blocked => "BLOCKED",
            _ => "NO CHANGES"
        };
        Line($"Sync decision: {outcome}{(dryRun ? " (dry run)" : string.Empty)}");
        if (!baselineExists)
        {
            Line("First sync: there is no baseline yet, so nothing can be deleted.");
        }

        var summary = decision.Summary;
        Line($"Summary: {summary.Added} to create, {summary.Modified} to update, {summary.Removed} to delete, {summary.Unchanged} unchanged");

        if (decision.Plan.Count > 0)
        {
            Line();
            Line($"Plan ({decision.Plan.Count})");
            foreach (var action in decision.Plan)
            {
                var symbol = action.Action switch { SyncAction.Create => "+ create", SyncAction.Update => "~ update", _ => "- delete" };
                Line($"  {symbol}  {action.Id}   {action.ControllerName}.{action.ActionName}");
                var rename = changes.Endpoints.FirstOrDefault(e => e.Id == action.Id)?.PossibleRename;
                if (rename is not null)
                {
                    Line($"      possibly renamed {(action.Action == SyncAction.Create ? "from" : "to")} {rename}");
                }
            }
        }

        if (decision.Violations.Count > 0)
        {
            Line();
            Line($"Violations ({decision.Violations.Count})");
            foreach (var violation in decision.Violations)
            {
                Line($"  [{violation.Code}] {violation.Message}");
                Line($"      {(violation.CanOverride ? "can be overridden explicitly" : "cannot be overridden by an option")}");
            }
        }

        if (decision.Notes.Count > 0)
        {
            Line();
            Line($"Notes ({decision.Notes.Count})");
            foreach (var note in decision.Notes)
            {
                Line($"  - {note}");
            }
        }

        Line();
        Line(string.Create(
            CultureInfo.InvariantCulture,
            $"Limits: at most {decision.Limits.MaxRemovedCount} endpoint(s) and {decision.Limits.MaxRemovedPercent:0.#}% of the baseline may be deleted at once."));

        Line(decision.Outcome switch
        {
            SyncOutcome.Blocked => "Blocked: nothing was written to the collection or the baseline.",
            SyncOutcome.NoChanges => "No changes: nothing was written.",
            _ when dryRun => "Dry run: nothing was written to the collection or the baseline.",
            _ => "Applying the plan."
        });

        return text.ToString();
    }
}
