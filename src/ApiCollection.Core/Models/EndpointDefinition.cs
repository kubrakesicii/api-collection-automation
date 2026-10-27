namespace ApiCollection.Core.Models;

public sealed record EndpointDefinition
{
    public required string Id { get; init; }
    public required string ControllerName { get; init; }
    public required string ActionName { get; init; }
    public required HttpMethodKind HttpMethod { get; init; }
    public required string RouteTemplate { get; init; }
    public IReadOnlyList<ParameterDefinition> Parameters { get; init; } = [];
    public RequestBodyDefinition? RequestBody { get; init; }
    public IReadOnlyList<ResponseDefinition> Responses { get; init; } = [];
    public AuthenticationDefinition Authentication { get; init; } = new();
    public IReadOnlyList<ValidationRuleDefinition> Validation { get; init; } = [];
}
