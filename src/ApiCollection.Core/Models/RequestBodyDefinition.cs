namespace ApiCollection.Core.Models;

public sealed record RequestBodyDefinition
{
    public required string TypeName { get; init; }
    public string ContentType { get; init; } = "application/json";
    public bool IsRequired { get; init; } = true;
    public IReadOnlyList<PropertyDefinition> Properties { get; init; } = [];
}

public sealed record PropertyDefinition
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public bool IsRequired { get; init; }
    public bool IsNullable { get; init; }
}
