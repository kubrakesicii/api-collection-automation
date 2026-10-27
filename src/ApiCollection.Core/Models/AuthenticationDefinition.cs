namespace ApiCollection.Core.Models;

public sealed record AuthenticationDefinition
{
    public bool RequiresAuthentication { get; init; }
    public string? Scheme { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<string> Policies { get; init; } = [];
}
