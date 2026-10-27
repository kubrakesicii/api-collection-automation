namespace ApiCollection.Core.Models;

public sealed record ParameterDefinition
{
    public required string Name { get; init; }
    public required ParameterLocation Location { get; init; }
    public required string TypeName { get; init; }
    public bool IsRequired { get; init; }
    public string? DefaultValue { get; init; }
}
