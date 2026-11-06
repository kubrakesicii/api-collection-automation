using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Diff;
using ApiCollection.Core.Models;
using ApiCollection.Core.Snapshots;
using ApiCollection.Core.Sync;
using ApiCollection.Infrastructure.Configuration;
using ApiCollection.Infrastructure.FileSystem;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;

namespace ApiCollection.Cli;

/// <summary>Command-line entry logic, kept separate from Program so it can be tested with in-memory writers.</summary>
public static class CliApplication
{
    public const int Success = 0;
    public const int Failure = 1;

    /// <summary>Returned by <c>generate --strict</c> when discovery reported warnings (outputs are still written).</summary>
    public const int StrictViolation = 2;

    /// <summary>Returned by <c>diff --exit-code</c> when anything was added, modified or removed.</summary>
    public const int ChangesDetected = 2;

    /// <summary>Returned by <c>sync</c> when the safety gate blocked it; nothing was written.</summary>
    public const int Blocked = 3;

    public const string Usage = """
        Usage:
          ApiCollection.Cli discover <api-source-directory> [--examples <example-metadata.json>]
          ApiCollection.Cli generate <api-source-directory> [--examples <example-metadata.json>] [--name <collection-name>]
                                     [--base-url <url>] [--out <collection.json>] [--report <report.json|report.txt>]
                                     [--snapshot <snapshot.json>] [--strict]
          ApiCollection.Cli diff <api-source-directory> --baseline <snapshot.json> [--examples <example-metadata.json>]
                                 [--changes <changes.json>] [--report <diff.txt>] [--exit-code]
          ApiCollection.Cli sync <api-source-directory> --baseline <snapshot.json> --out <collection.json>
                                 [--examples <example-metadata.json>] [--name <collection-name>] [--base-url <url>]
                                 [--dry-run] [--max-removed <n>] [--max-removed-percent <0-100>] [--allow-incomplete-discovery]
                                 [--plan <plan.json>] [--changes <changes.json>] [--report <sync.txt>]

        'discover' prints the discovery report. 'generate' builds a Postman Collection v2.1 file locally; nothing is sent to Postman.
        'diff' compares the current API with a baseline snapshot and prints what was added, modified, removed or unchanged.
        'sync' brings the stored collection and its baseline snapshot up to date, but only if the safety gate allows it: it blocks
          (exit code 3, nothing written) when discovery failed or may be incomplete, when the stored collection no longer matches
          the baseline, or when too many endpoints would be deleted. With nothing changed it writes nothing. Nothing is sent to Postman.
        --dry-run   show the decision and the plan; never write the collection or the baseline
        --max-removed / --max-removed-percent   deletion limits (defaults: 10 endpoints, 30%); raise them only on purpose
        --allow-incomplete-discovery   go ahead although discovery reported failures that may have hidden endpoints (stays listed)
        --plan      write the decision and plan as JSON
        --out       write the collection to a file (otherwise it is printed to stdout)
        --report    generate: write the discovery report (JSON for a .json extension, readable text otherwise); diff: write the diff report
        --snapshot  write the endpoint snapshot (identities, contract facts, collection hash); an existing file is overwritten.
                    Keep it next to the collection; it is the baseline later runs are compared against.
        --baseline  the snapshot to compare with (read only; never overwritten)
        --changes   write the machine-readable change set (JSON)
        --strict    exit with code 2 when discovery reported warnings (example diagnostics do not count); files are still written
        --exit-code exit with code 2 when the diff found changes (like 'git diff --exit-code'); files are still written
        The API source is only read, never built or run, and output files may not be placed inside it.
        """;

    private static readonly string[] DiscoverOptions = ["--examples"];
    private static readonly string[] GenerateOptions = ["--examples", "--name", "--base-url", "--out", "--report", "--snapshot"];
    private static readonly string[] DiffOptions = ["--examples", "--baseline", "--changes", "--report"];
    private static readonly string[] SyncOptions =
        ["--examples", "--name", "--base-url", "--baseline", "--out", "--plan", "--changes", "--report", "--max-removed", "--max-removed-percent"];

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParse(args, out var command, out var directory, out var values, out var flags)
            || (command == "diff" && !values.ContainsKey("--baseline"))
            || (command == "sync" && (!values.ContainsKey("--baseline") || !values.ContainsKey("--out"))))
        {
            error.WriteLine(Usage);
            return Failure;
        }

        if (!Directory.Exists(directory))
        {
            error.WriteLine($"Directory not found: {directory}");
            return Failure;
        }

        var written = command switch
        {
            "generate" => new[] { "--out", "--report", "--snapshot" },
            "diff" => ["--changes", "--report"],
            "sync" => ["--out", "--baseline", "--plan", "--changes", "--report"],
            _ => []
        };
        if (!ValidateOutputs(directory, values, written, error))
        {
            return Failure;
        }

        var limits = new SyncSafetyOptions { AllowIncompleteDiscovery = flags.Contains("--allow-incomplete-discovery") };
        if (command == "sync" && !TryReadLimits(values, limits, error, out limits))
        {
            return Failure;
        }

        // A sync may start without a baseline (the first sync); every other command needs one that can be read.
        AnalysisSnapshot? baseline = null;
        var baselineRequired = command == "diff" || (command == "sync" && File.Exists(values["--baseline"]));
        if (baselineRequired && !TryLoadBaseline(values["--baseline"], error, out baseline))
        {
            return Failure;
        }

        ExampleMetadata? examples = null;
        if (values.TryGetValue("--examples", out var examplesPath))
        {
            try
            {
                examples = ExampleMetadataLoader.Load(examplesPath);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                error.WriteLine($"Cannot read example metadata: {exception.Message}");
                return Failure;
            }
        }

        EndpointDiscoveryResult result;
        try
        {
            result = new EndpointDiscoveryEngine().DiscoverFromDirectory(directory, examples);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fail closed: a source that cannot be read is a failed analysis, never an empty API.
            error.WriteLine($"Discovery failed: {exception.Message}");
            return Failure;
        }

        return command switch
        {
            "discover" => Discover(result, output),
            "generate" => Generate(result, values, flags.Contains("--strict"), output, error),
            "diff" => Diff(result, baseline!, values, flags.Contains("--exit-code"), output, error),
            _ => Sync(result, baseline, values, flags.Contains("--dry-run"), limits, output, error)
        };
    }

    private static bool TryReadLimits(Dictionary<string, string> values, SyncSafetyOptions defaults, TextWriter error, out SyncSafetyOptions limits)
    {
        limits = defaults;
        if (values.TryGetValue("--max-removed-percent", out var percentText))
        {
            if (!double.TryParse(percentText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent)
                || percent is < 0 or > 100)
            {
                error.WriteLine("--max-removed-percent must be a number between 0 and 100.");
                return false;
            }

            limits = limits with { MaxRemovedPercent = percent };
        }

        if (values.TryGetValue("--max-removed", out var countText))
        {
            if (!int.TryParse(countText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count))
            {
                error.WriteLine("--max-removed must be a whole number of endpoints (0 or more).");
                return false;
            }

            limits = limits with { MaxRemovedCount = count };
        }

        return true;
    }

    // ---- sync ----

    private static int Sync(
        EndpointDiscoveryResult result, AnalysisSnapshot? baseline, Dictionary<string, string> values, bool dryRun,
        SyncSafetyOptions limits, TextWriter output, TextWriter error)
    {
        var collectionPath = values["--out"];
        var baselinePath = values["--baseline"];

        AnalysisSnapshot current;
        try
        {
            current = AnalysisSnapshot.Create(result.Endpoints);
        }
        catch (ArgumentException exception)
        {
            error.WriteLine($"Cannot sync: {exception.Message}");
            return Failure;
        }

        ChangeSet changes;
        try
        {
            changes = ChangeSetBuilder.Build(
                baseline ?? AnalysisSnapshot.Create([]), current, result.Warnings.Select(w => $"Discovery: {w}"));
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine(exception.Message);
            return Failure;
        }

        var decision = SyncSafetyValidator.Validate(
            new SyncSafetyInput
            {
                Changes = changes,
                BaselineCollection = ClassifyBaselineCollection(baseline, collectionPath),
                DiscoveryFailures = result.Failures,
                DiscoveryWarnings = result.Warnings.Except(result.Failures).ToList()
            },
            limits);

        var report = SyncReportWriter.ToText(decision, changes, dryRun, baseline is not null);
        output.Write(report);

        // Reports describe the decision; they are written in every case, including dry runs and blocked runs.
        foreach (var (option, content) in new[]
        {
            ("--report", report),
            ("--plan", SyncDecisionSerializer.Serialize(decision)),
            ("--changes", ChangeSetSerializer.Serialize(changes))
        })
        {
            if (values.TryGetValue(option, out var path) && !TryWrite(path, content, error))
            {
                return Failure;
            }
        }

        if (decision.Outcome == SyncOutcome.Blocked)
        {
            return Blocked;
        }

        if (decision.Outcome == SyncOutcome.NoChanges || dryRun)
        {
            return Success;
        }

        var collection = new PostmanCollectionGenerator().Generate(result.Endpoints, new PostmanCollectionOptions
        {
            Name = values.GetValueOrDefault("--name", "API Collection"),
            BaseUrl = values.GetValueOrDefault("--base-url", "http://localhost:5000")
        });
        var json = PostmanCollectionSerializer.Serialize(collection);
        var newBaseline = AnalysisSnapshot.Create(result.Endpoints, SnapshotCollection.FromJson(collectionPath, json));

        try
        {
            // The baseline goes last: it refers to the collection by hash (see AtomicFileWriter).
            AtomicFileWriter.WriteAll([(collectionPath, json), (baselinePath, AnalysisSnapshotStore.Serialize(newBaseline))]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Cannot write the collection and baseline: {exception.Message}");
            return Failure;
        }

        output.WriteLine($"Wrote collection {collectionPath}");
        output.WriteLine($"Wrote baseline {baselinePath}");
        return Success;
    }

    private static BaselineCollectionState ClassifyBaselineCollection(AnalysisSnapshot? baseline, string collectionPath)
    {
        if (baseline is null)
        {
            return BaselineCollectionState.NotApplicable;
        }

        if (baseline.Collection is null)
        {
            return BaselineCollectionState.NoReference;
        }

        if (!string.Equals(baseline.Collection.FileName, Path.GetFileName(collectionPath), StringComparison.Ordinal))
        {
            return BaselineCollectionState.NameMismatch;
        }

        try
        {
            return !File.Exists(collectionPath)
                ? BaselineCollectionState.Missing
                : SnapshotCollection.FromJson(collectionPath, File.ReadAllText(collectionPath)).Sha256 == baseline.Collection.Sha256
                    ? BaselineCollectionState.Verified
                    : BaselineCollectionState.Changed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BaselineCollectionState.Missing;
        }
    }

    private static int Discover(EndpointDiscoveryResult result, TextWriter output)
    {
        output.Write(DiscoveryReportWriter.ToText(result));
        return Success;
    }

    // ---- generate ----

    private static int Generate(
        EndpointDiscoveryResult result, Dictionary<string, string> values, bool strict, TextWriter output, TextWriter error)
    {
        var collection = new PostmanCollectionGenerator().Generate(result.Endpoints, new PostmanCollectionOptions
        {
            Name = values.GetValueOrDefault("--name", "API Collection"),
            BaseUrl = values.GetValueOrDefault("--base-url", "http://localhost:5000")
        });
        var json = PostmanCollectionSerializer.Serialize(collection);

        // Built before anything is written, so an unusable snapshot (duplicate identities) fails the run without partial output.
        AnalysisSnapshot? snapshot = null;
        if (values.ContainsKey("--snapshot"))
        {
            try
            {
                snapshot = AnalysisSnapshot.Create(
                    result.Endpoints,
                    values.TryGetValue("--out", out var collectionPath) ? SnapshotCollection.FromJson(collectionPath, json) : null);
            }
            catch (ArgumentException exception)
            {
                error.WriteLine($"Cannot create the snapshot: {exception.Message}");
                return Failure;
            }
        }

        // When the collection itself goes to stdout, status messages go to stderr to keep stdout a pure document.
        var status = values.ContainsKey("--out") ? output : error;

        if (values.TryGetValue("--out", out var outputPath))
        {
            if (!TryWrite(outputPath, json, error))
            {
                return Failure;
            }

            status.WriteLine($"Wrote collection {outputPath}");
        }
        else
        {
            output.WriteLine(json);
        }

        if (values.TryGetValue("--report", out var reportPath))
        {
            var report = reportPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? DiscoveryReportWriter.ToJson(result)
                : DiscoveryReportWriter.ToText(result);
            if (!TryWrite(reportPath, report, error))
            {
                return Failure;
            }

            status.WriteLine($"Wrote report {reportPath}");
        }

        if (snapshot is not null)
        {
            try
            {
                AnalysisSnapshotStore.Save(values["--snapshot"], snapshot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Cannot write '{values["--snapshot"]}': {exception.Message}");
                return Failure;
            }

            status.WriteLine($"Wrote snapshot {values["--snapshot"]}");
        }

        error.WriteLine($"{result.Endpoints.Count} endpoint(s) in {collection.Items.Count} folder(s); {result.Warnings.Count} warning(s), {result.ExampleDiagnostics.Count} example diagnostic(s).");

        if (strict && result.Warnings.Count > 0)
        {
            error.WriteLine($"Strict mode: discovery reported {result.Warnings.Count} warning(s):");
            foreach (var warning in result.Warnings)
            {
                error.WriteLine($"  WARN {warning}");
            }

            return StrictViolation;
        }

        return Success;
    }

    // ---- diff ----

    private static bool TryLoadBaseline(string path, TextWriter error, out AnalysisSnapshot? baseline)
    {
        try
        {
            baseline = AnalysisSnapshotStore.Load(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"Cannot read the baseline snapshot: {exception.Message}");
            baseline = null;
            return false;
        }
    }

    private static int Diff(
        EndpointDiscoveryResult result, AnalysisSnapshot baseline, Dictionary<string, string> values, bool exitCode,
        TextWriter output, TextWriter error)
    {
        AnalysisSnapshot current;
        try
        {
            current = AnalysisSnapshot.Create(result.Endpoints);
        }
        catch (ArgumentException exception)
        {
            error.WriteLine($"Cannot compare: {exception.Message}");
            return Failure;
        }

        // A removal may only be a failed discovery, and the baseline collection may have been edited after the snapshot:
        // both are reported next to the result so a later policy (or a person) can decide how far to trust it.
        var warnings = BaselineCollectionWarnings(baseline, values["--baseline"]).Concat(result.Warnings.Select(w => $"Discovery: {w}")).ToList();

        ChangeSet changes;
        try
        {
            changes = ChangeSetBuilder.Build(baseline, current, warnings);
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine(exception.Message);
            return Failure;
        }

        var report = DiffReportWriter.ToText(changes);
        output.Write(report);

        if (values.TryGetValue("--report", out var reportPath))
        {
            if (!TryWrite(reportPath, report, error))
            {
                return Failure;
            }

            error.WriteLine($"Wrote report {reportPath}");
        }

        if (values.TryGetValue("--changes", out var changesPath))
        {
            if (!TryWrite(changesPath, ChangeSetSerializer.Serialize(changes), error))
            {
                return Failure;
            }

            error.WriteLine($"Wrote change set {changesPath}");
        }

        return exitCode && changes.HasChanges ? ChangesDetected : Success;
    }

    private static List<string> BaselineCollectionWarnings(AnalysisSnapshot baseline, string baselinePath)
    {
        if (baseline.Collection is null)
        {
            return ["The baseline snapshot does not reference a collection, so the baseline cannot be verified."];
        }

        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(baselinePath)) ?? string.Empty, baseline.Collection.FileName);
        try
        {
            if (!File.Exists(path))
            {
                return [$"Baseline collection '{baseline.Collection.FileName}' was not found next to the snapshot, so the baseline cannot be verified."];
            }

            return SnapshotCollection.FromJson(path, File.ReadAllText(path)).Sha256 == baseline.Collection.Sha256
                ? []
                : [$"Baseline collection '{baseline.Collection.FileName}' differs from the one the snapshot was taken with; this diff describes the snapshot, not that file."];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [$"Baseline collection '{baseline.Collection.FileName}' could not be read ({exception.Message}), so the baseline cannot be verified."];
        }
    }

    // ---- arguments ----

    private static bool TryParse(
        string[] args, out string command, out string directory, out Dictionary<string, string> values, out HashSet<string> flags)
    {
        command = directory = string.Empty;
        values = [];
        flags = [];

        if (args.Length < 2 || args[0] is not ("discover" or "generate" or "diff" or "sync"))
        {
            return false;
        }

        command = args[0];
        directory = args[1];
        var allowed = command switch { "discover" => DiscoverOptions, "generate" => GenerateOptions, "diff" => DiffOptions, _ => SyncOptions };
        string[] allowedFlags = command switch
        {
            "generate" => ["--strict"],
            "diff" => ["--exit-code"],
            "sync" => ["--dry-run", "--allow-incomplete-discovery"],
            _ => []
        };

        for (var i = 2; i < args.Length; i++)
        {
            if (allowedFlags.Contains(args[i]) && !flags.Contains(args[i]))
            {
                flags.Add(args[i]);
            }
            else if (allowed.Contains(args[i]) && i + 1 < args.Length && !values.ContainsKey(args[i]))
            {
                values[args[i]] = args[++i];
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Output files must differ from each other and from the baseline, and may not be placed inside the analyzed API.</summary>
    private static bool ValidateOutputs(string directory, Dictionary<string, string> values, string[] outputOptions, TextWriter error)
    {
        var outputs = outputOptions.Where(values.ContainsKey).Select(option => (Option: option, Path: values[option])).ToList();
        // The baseline is compared with the outputs too; for sync it is itself one of the outputs and must not be compared with itself.
        var compared = values.ContainsKey("--baseline") && outputs.TrueForAll(o => o.Option != "--baseline")
            ? [.. outputs, ("--baseline", values["--baseline"])]
            : outputs;

        for (var i = 0; i < compared.Count; i++)
        {
            for (var j = i + 1; j < compared.Count; j++)
            {
                if (SamePath(compared[i].Path, compared[j].Path))
                {
                    error.WriteLine($"{compared[i].Item1} and {compared[j].Item1} must be different files.");
                    return false;
                }
            }
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        foreach (var (option, path) in outputs)
        {
            if (Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine($"Refusing to write {option} '{path}' inside the analyzed API directory; choose a location outside '{directory}'.");
                return false;
            }
        }

        return true;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool TryWrite(string path, string content, TextWriter error)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(path, content);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Cannot write '{path}': {exception.Message}");
            return false;
        }
    }
}
