namespace ApiCollection.Core.Models;

public sealed record RequestBodyDefinition
{
    public required string TypeName { get; init; }
    public string ContentType { get; init; } = "application/json";
    public bool IsRequired { get; init; } = true;
    public IReadOnlyList<PropertyDefinition> Properties { get; init; } = [];
    public ExampleDefinition? Example { get; init; }
}

public sealed record PropertyDefinition
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public bool IsRequired { get; init; }
    public bool IsNullable { get; init; }
    public IReadOnlyList<PropertyDefinition> Properties { get; init; } = [];

    /// <summary>Explicit JSON name from [JsonPropertyName]; null when the serializer's naming policy applies.</summary>
    public string? JsonName { get; init; }

    /// <summary>A documented example (XML &lt;example&gt; or [DefaultValue]); not necessarily an actual runtime value.</summary>
    public ExampleValue? Example { get; init; }

    /// <summary>A literal default declared in source (property initializer or constructor default).</summary>
    public ExampleValue? DefaultValue { get; init; }
}
