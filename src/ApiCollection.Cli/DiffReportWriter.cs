using System.Text;
using ApiCollection.Core.Diff;

namespace ApiCollection.Cli;

/// <summary>Renders a <see cref="ChangeSet"/> as a human-readable report. Deterministic (LF, no timestamps).</summary>
internal static class DiffReportWriter
{
    public static string ToText(ChangeSet changeSet)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append('\n');

        var summary = changeSet.Summary;
        Line($"Summary: {summary.Added} added, {summary.Modified} modified, {summary.Removed} removed, {summary.Unchanged} unchanged");
        if (!changeSet.HasChanges)
        {
            Line("No changes.");
        }

        Section("Added", ChangeStatus.Added, "+");
        Section("Modified", ChangeStatus.Modified, "~");
        Section("Removed", ChangeStatus.Removed, "-");
        Section("Unchanged", ChangeStatus.Unchanged, "=");

        if (changeSet.Warnings.Count > 0)
        {
            Line();
            Line($"Warnings ({changeSet.Warnings.Count})");
            foreach (var warning in changeSet.Warnings)
            {
                Line($"  WARN {warning}");
            }
        }

        return text.ToString();

        void Section(string title, ChangeStatus status, string symbol)
        {
            var diffs = changeSet.Endpoints.Where(d => d.Status == status).ToList();
            if (diffs.Count == 0)
            {
                return;
            }

            Line();
            Line($"{title} ({diffs.Count})");
            foreach (var diff in diffs)
            {
                Line($"  {symbol} {diff.Id}   {diff.ControllerName}.{diff.ActionName}");
                if (diff.PossibleRename is not null)
                {
                    Line(status == ChangeStatus.Added
                        ? $"      possibly renamed from {diff.PossibleRename}"
                        : $"      possibly renamed to {diff.PossibleRename}");
                }

                foreach (var change in diff.Changes)
                {
                    Line($"      {change.Category,-14} {change.Kind,-8} {change.Path}{Detail(change)}");
                }
            }
        }
    }

    private static string Detail(ContractChange change) => change.Kind switch
    {
        ChangeKind.Changed => $": {change.Before} -> {change.After}",
        ChangeKind.Added => $": {change.After}",
        _ => $": {change.Before}"
    };
}
