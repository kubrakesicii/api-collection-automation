namespace ApiCollection.Core.Models;

/// <summary>Where an example value came from. Every example must be traceable to one of these.</summary>
public enum ExampleSource
{
    XmlDocExample,
    DefaultValueAttribute,
    PropertyInitializer,
    ConstructorDefault,
    ResponseLiteral,
    ExternalMetadata
}

/// <summary>A single example value (raw JSON) found for a DTO property, with its origin.</summary>
public sealed record ExampleValue
{
    public required string Json { get; init; }
    public required ExampleSource Source { get; init; }
    public required string SourceDetail { get; init; }
}

/// <summary>Origin of one field (<c>$</c> for the whole document) inside a composed example.</summary>
public sealed record ExampleFieldSource
{
    public required string Path { get; init; }
    public required ExampleSource Source { get; init; }
    public required string SourceDetail { get; init; }
}

/// <summary>An example JSON document plus the provenance of every value in it.</summary>
public sealed record ExampleDefinition
{
    public required string Json { get; init; }
    public bool IsComplete { get; init; }
    public IReadOnlyList<ExampleFieldSource> Fields { get; init; } = [];
    public IReadOnlyList<string> MissingFields { get; init; } = [];
}

/// <summary>
/// Curated examples kept outside the analyzed source, for values that cannot be found in code.
/// Values are raw JSON text. Keys: properties "Type.Property"; requests and responses use the endpoint Id.
/// </summary>
public sealed record ExampleMetadata
{
    public string SourceName { get; init; } = "example metadata";
    public IReadOnlyDictionary<string, string> PropertyExamples { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> RequestExamples { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> ResponseExamples { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<int, string>>();
}
