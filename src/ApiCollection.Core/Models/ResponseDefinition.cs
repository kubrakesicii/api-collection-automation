namespace ApiCollection.Core.Models;

public sealed record ResponseDefinition
{
    public required int StatusCode { get; init; }
    public string? TypeName { get; init; }
    public string? ContentType { get; init; }
}
