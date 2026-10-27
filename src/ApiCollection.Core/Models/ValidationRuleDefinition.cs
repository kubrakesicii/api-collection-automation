namespace ApiCollection.Core.Models;

public sealed record ValidationRuleDefinition
{
    public required string PropertyName { get; init; }
    public required string RuleName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
