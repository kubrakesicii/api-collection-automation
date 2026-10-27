using ApiCollection.Core.Models;

namespace ApiCollection.Analysis.Discovery;

public sealed record EndpointDiscoveryResult(
    IReadOnlyList<EndpointDefinition> Endpoints,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ExampleDiagnostic> ExampleDiagnostics);
