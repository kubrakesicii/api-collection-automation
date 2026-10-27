using ApiCollection.Analysis.Discovery;

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
}

Console.WriteLine($"{result.Endpoints.Count} endpoint(s), {result.Warnings.Count} warning(s)");
foreach (var warning in result.Warnings)
{
    Console.WriteLine($"WARN {warning}");
}

return 0;
