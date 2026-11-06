using ApiCollection.Infrastructure.Configuration;
using ApiCollection.Postman.CloudApi;

namespace ApiCollection.Cli;

/// <summary>What the CLI reads from outside the process, replaceable so commands can be tested without touching the real environment, profile or network.</summary>
public sealed class CliEnvironment
{
    /// <summary>
    /// Identifies this project's user secrets (<c>dotnet user-secrets --project src/ApiCollection.Cli</c>); it must equal the
    /// <c>UserSecretsId</c> in ApiCollection.Cli.csproj, which a test checks.
    /// </summary>
    public const string UserSecretsId = "30c05448-c2e4-4e2a-8769-e0c3708d0c07";

    public static CliEnvironment Default { get; } = new();

    /// <summary>Reads an environment variable.</summary>
    public Func<string, string?> GetVariable { get; init; } = name => Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Reads a user secret (such as <c>Postman:ApiKey</c>) from the profile's <c>secrets.json</c>, outside the repository. The file is read
    /// only when a value is actually needed; an unreadable file throws <see cref="InvalidDataException"/> or <see cref="IOException"/>.
    /// </summary>
    public Func<string, string?> GetUserSecret { get; init; } = CreateUserSecretReader();

    /// <summary>Creates the HTTP client used for Postman (see <see cref="PostmanHttpClientFactory"/>: no redirects).</summary>
    public Func<HttpClient> CreateHttpClient { get; init; } = () => PostmanHttpClientFactory.Create();

    private static Func<string, string?> CreateUserSecretReader()
    {
        var secrets = new Lazy<IReadOnlyDictionary<string, string>>(() => UserSecretsReader.Read(UserSecretsId));
        return key => secrets.Value.GetValueOrDefault(key);
    }
}
