using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;
using ApiCollection.Infrastructure.Configuration;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;

namespace ApiCollection.Cli;

/// <summary>Command-line entry logic, kept separate from Program so it can be tested with in-memory writers.</summary>
public static class CliApplication
{
    public const int Success = 0;
    public const int Failure = 1;

    /// <summary>Returned by <c>--strict</c> when discovery reported warnings (outputs are still written).</summary>
    public const int StrictViolation = 2;

    public const string Usage = """
        Usage:
          ApiCollection.Cli discover <api-source-directory> [--examples <example-metadata.json>]
          ApiCollection.Cli generate <api-source-directory> [--examples <example-metadata.json>] [--name <collection-name>]
                                     [--base-url <url>] [--out <collection.json>] [--report <report.json|report.txt>] [--strict]

        'discover' prints the discovery report. 'generate' builds a Postman Collection v2.1 file locally; nothing is sent to Postman.
        --out      write the collection to a file (otherwise it is printed to stdout)
        --report   write the discovery report to a file: JSON for a .json extension, readable text otherwise
        --strict   exit with code 2 when discovery reported warnings (example diagnostics do not count); files are still written
        The API source is only read, never built or run, and output files may not be placed inside it.
        """;

    private static readonly string[] DiscoverOptions = ["--examples"];
    private static readonly string[] GenerateOptions = ["--examples", "--name", "--base-url", "--out", "--report"];

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParse(args, out var command, out var directory, out var values, out var strict))
        {
            error.WriteLine(Usage);
            return Failure;
        }

        if (!Directory.Exists(directory))
        {
            error.WriteLine($"Directory not found: {directory}");
            return Failure;
        }

        if (command == "generate" && !ValidateOutputs(directory, values, error))
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

        var result = new EndpointDiscoveryEngine().DiscoverFromDirectory(directory, examples);

        if (command == "discover")
        {
            output.Write(DiscoveryReportWriter.ToText(result));
            return Success;
        }

        return Generate(result, values, strict, output, error);
    }

    private static int Generate(
        EndpointDiscoveryResult result, Dictionary<string, string> values, bool strict, TextWriter output, TextWriter error)
    {
        var collection = new PostmanCollectionGenerator().Generate(result.Endpoints, new PostmanCollectionOptions
        {
            Name = values.GetValueOrDefault("--name", "API Collection"),
            BaseUrl = values.GetValueOrDefault("--base-url", "http://localhost:5000")
        });
        var json = PostmanCollectionSerializer.Serialize(collection);

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

    private static bool TryParse(
        string[] args, out string command, out string directory, out Dictionary<string, string> values, out bool strict)
    {
        command = directory = string.Empty;
        values = [];
        strict = false;

        if (args.Length < 2 || args[0] is not ("discover" or "generate"))
        {
            return false;
        }

        command = args[0];
        directory = args[1];
        var allowed = command == "discover" ? DiscoverOptions : GenerateOptions;

        for (var i = 2; i < args.Length; i++)
        {
            if (command == "generate" && args[i] == "--strict" && !strict)
            {
                strict = true;
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

    private static bool ValidateOutputs(string directory, Dictionary<string, string> values, TextWriter error)
    {
        var outputs = new[] { "--out", "--report" }.Where(values.ContainsKey).Select(option => (Option: option, Path: values[option])).ToList();

        if (outputs.Count == 2 && SamePath(outputs[0].Path, outputs[1].Path))
        {
            error.WriteLine("--out and --report must be different files.");
            return false;
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
