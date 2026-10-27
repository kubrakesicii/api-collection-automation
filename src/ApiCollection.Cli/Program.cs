using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;

if (args.Length != 2 || args[0] != "discover")
{
    Console.Error.WriteLine("Usage: ApiCollection.Cli discover <api-source-directory>");
    return 1;
}

var directory = args[1];
if (!Directory.Exists(directory))
{
    Console.Error.WriteLine($"Directory not found: {directory}");
    return 1;
}

var result = new EndpointDiscoveryEngine().DiscoverFromDirectory(directory);

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
}

Console.WriteLine($"{result.Endpoints.Count} endpoint(s), {result.Warnings.Count} warning(s)");
foreach (var warning in result.Warnings)
{
    Console.WriteLine($"WARN {warning}");
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
