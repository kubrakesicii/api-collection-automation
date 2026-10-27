using System.Text.Json.Nodes;
using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;
using ApiCollection.Infrastructure.Configuration;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;

const string Usage = """
    Usage:
      ApiCollection.Cli discover <api-source-directory> [--examples <example-metadata.json>]
      ApiCollection.Cli generate <api-source-directory> [--examples <example-metadata.json>] [--name <collection-name>] [--base-url <url>] [--out <file.json>]

    'generate' builds a Postman Collection v2.1 file locally; nothing is sent to Postman.
    """;

if (args.Length < 2 || args[0] is not ("discover" or "generate"))
{
    Console.Error.WriteLine(Usage);
    return 1;
}

var command = args[0];
var directory = args[1];
var allowed = command == "discover" ? new[] { "--examples" } : ["--examples", "--name", "--base-url", "--out"];
var options = new Dictionary<string, string>();
for (var i = 2; i < args.Length; i += 2)
{
    if (!allowed.Contains(args[i]) || i + 1 >= args.Length || options.ContainsKey(args[i]))
    {
        Console.Error.WriteLine(Usage);
        return 1;
    }

    options[args[i]] = args[i + 1];
}

if (!Directory.Exists(directory))
{
    Console.Error.WriteLine($"Directory not found: {directory}");
    return 1;
}

ExampleMetadata? examples = null;
if (options.TryGetValue("--examples", out var examplesPath))
{
    try
    {
        examples = ExampleMetadataLoader.Load(examplesPath);
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Cannot read example metadata: {exception.Message}");
        return 1;
    }
}

var result = new EndpointDiscoveryEngine().DiscoverFromDirectory(directory, examples);

if (command == "generate")
{
    var collection = new PostmanCollectionGenerator().Generate(result.Endpoints, new PostmanCollectionOptions
    {
        Name = options.GetValueOrDefault("--name", "API Collection"),
        BaseUrl = options.GetValueOrDefault("--base-url", "http://localhost:5000")
    });
    var json = PostmanCollectionSerializer.Serialize(collection);

    if (options.TryGetValue("--out", out var outputPath))
    {
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"Wrote {outputPath}");
    }
    else
    {
        Console.WriteLine(json);
    }

    // Summary goes to stderr so stdout stays a pure collection document.
    Console.Error.WriteLine($"{result.Endpoints.Count} endpoint(s) in {collection.Items.Count} folder(s); {result.Warnings.Count} warning(s), {result.ExampleDiagnostics.Count} example diagnostic(s).");
    return 0;
}

foreach (var endpoint in result.Endpoints)
{
    Console.WriteLine($"{endpoint.HttpMethod.ToString().ToUpperInvariant(),-7} /{endpoint.RouteTemplate,-28} {endpoint.ControllerName}.{endpoint.ActionName}");

    foreach (var parameter in endpoint.Parameters.Where(p => p.Location != ParameterLocation.Body))
    {
        Console.WriteLine($"          {parameter.Location.ToString().ToLowerInvariant()} {parameter.Name}: {parameter.TypeName}{(parameter.IsRequired ? " (required)" : "")}");
    }

    if (endpoint.RequestBody is { } body)
    {
        Console.WriteLine($"          body {body.TypeName}{(body.IsRequired ? " (required)" : "")}");
        PrintProperties(body.Properties, "            ");
        PrintExample("request example", body.Example);
    }

    var auth = endpoint.Authentication;
    if (auth.RequiresAuthentication)
    {
        var details = string.Join("; ", new[]
        {
            auth.Scheme is null ? null : $"scheme {auth.Scheme}",
            auth.Roles.Count == 0 ? null : $"roles {string.Join(",", auth.Roles)}",
            auth.Policies.Count == 0 ? null : $"policies {string.Join(",", auth.Policies)}"
        }.Where(d => d is not null));
        Console.WriteLine($"          auth: required{(details.Length == 0 ? "" : " (" + details + ")")}");
    }

    foreach (var rule in endpoint.Validation)
    {
        Console.WriteLine($"          validation: {rule.PropertyName} {rule.RuleName}({string.Join(", ", rule.Arguments)})");
    }

    foreach (var response in endpoint.Responses.Where(r => r.Example is not null))
    {
        PrintExample($"response example {response.StatusCode}", response.Example);
    }
}

Console.WriteLine($"{result.Endpoints.Count} endpoint(s), {result.Warnings.Count} warning(s), {result.ExampleDiagnostics.Count} example diagnostic(s)");
foreach (var warning in result.Warnings)
{
    Console.WriteLine($"WARN {warning}");
}

foreach (var diagnostic in result.ExampleDiagnostics)
{
    Console.WriteLine($"{diagnostic.Code} {diagnostic.Endpoint ?? "-"} [{diagnostic.Target}] {diagnostic.Message}");
}

return 0;

static void PrintProperties(IReadOnlyList<PropertyDefinition> properties, string indent)
{
    foreach (var property in properties)
    {
        Console.WriteLine($"{indent}{property.Name}: {property.TypeName}{(property.IsRequired ? " (required)" : "")}");
        PrintProperties(property.Properties, indent + "  ");
    }
}

static void PrintExample(string label, ExampleDefinition? example)
{
    if (example is null)
    {
        return;
    }

    var state = example.IsComplete ? "complete" : $"incomplete, missing: {string.Join(", ", example.MissingFields)}";
    Console.WriteLine($"          {label} ({state}):");
    Console.WriteLine($"            {JsonNode.Parse(example.Json)?.ToJsonString()}");
    foreach (var field in example.Fields)
    {
        Console.WriteLine($"            source {field.Path} <- {field.Source} [{field.SourceDetail}]");
    }
}
