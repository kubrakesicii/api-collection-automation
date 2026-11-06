using ApiCollection.Core.Models;

namespace ApiCollection.Analysis.Discovery;

/// <param name="Warnings">Everything that went wrong, including the failures.</param>
/// <param name="ExampleDiagnostics">Missing, rejected or unresolvable examples.</param>
/// <param name="Failures">
/// The subset of <paramref name="Warnings"/> that may hide endpoints (unresolved routes, source files that do not parse, ambiguous
/// identities). A sync must not trust a result that has failures to be complete.
/// </param>
public sealed record EndpointDiscoveryResult(
    IReadOnlyList<EndpointDefinition> Endpoints,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ExampleDiagnostic> ExampleDiagnostics,
    IReadOnlyList<string> Failures);
