using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiCollection.Infrastructure.Configuration;

/// <summary>
/// Reads the standard .NET "user secrets" file (<c>secrets.json</c>) that <c>dotnet user-secrets</c> maintains. It lives in the
/// user's profile, <b>outside the repository</b>, so a secret kept there cannot be committed by accident. Read directly, so no
/// configuration package is needed.
/// <list type="bullet">
/// <item>Windows: <c>%APPDATA%\Microsoft\UserSecrets\&lt;id&gt;\secrets.json</c></item>
/// <item>Elsewhere: <c>~/.microsoft/usersecrets/&lt;id&gt;/secrets.json</c></item>
/// </list>
/// Keys use ':' as the separator (<c>Postman:ApiKey</c>), as the tool writes them; nested JSON objects are flattened the same way.
/// </summary>
public static partial class UserSecretsReader
{
    /// <summary>Where the secrets of <paramref name="userSecretsId"/> are kept; the arguments exist so tests need not touch the real profile.</summary>
    public static string PathFor(string userSecretsId, string? appData = null, string? home = null, bool? isWindows = null)
    {
        if (!IdPattern().IsMatch(userSecretsId))
        {
            throw new ArgumentException("The user secrets id may only contain letters, digits, '-', '_' and '.'.", nameof(userSecretsId));
        }

        if (isWindows ?? OperatingSystem.IsWindows())
        {
            var root = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "Microsoft", "UserSecrets", userSecretsId, "secrets.json");
        }

        var profile = home ?? Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".microsoft", "usersecrets", userSecretsId, "secrets.json");
    }

    /// <summary>Reads the secrets; a missing file simply means none are set. Keys are case-insensitive.</summary>
    /// <exception cref="InvalidDataException">The file exists but is not usable JSON (the message never contains the file's content).</exception>
    public static IReadOnlyDictionary<string, string> Read(string userSecretsId, string? path = null)
    {
        path ??= PathFor(userSecretsId);
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return secrets;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The user secrets file must contain a JSON object.");
            }

            Flatten(document.RootElement, string.Empty, secrets);
            return secrets;
        }
        catch (JsonException exception)
        {
            // JsonException reports a position, never the text, so a secret cannot leak through the message.
            throw new InvalidDataException($"The user secrets file is not valid JSON ({exception.Message})", exception);
        }
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> secrets)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, key, secrets);
                    break;
                case JsonValueKind.String:
                    secrets[key] = property.Value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    secrets[key] = property.Value.GetRawText();
                    break;
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex IdPattern();
}
