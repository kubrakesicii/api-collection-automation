using System.Text;
using ApiCollection.Postman.CloudApi;

namespace ApiCollection.Cli;

/// <summary>
/// <c>postman-check</c>: verifies the API key and workspace access with read-only requests. The key is taken only from the
/// environment or the user secrets file (never from the command line, where it would end up in shell history and process listings)
/// and is never printed.
/// </summary>
internal static class PostmanCheckCommand
{
    public const string ApiKeyVariable = "POSTMAN_API_KEY";
    public const string WorkspaceVariable = "POSTMAN_WORKSPACE_ID";
    public const string ApiKeySecret = "Postman:ApiKey";
    public const string WorkspaceSecret = "Postman:WorkspaceId";

    /// <summary>A value and where it came from, so the report can say which source was used without revealing the value.</summary>
    internal sealed record Setting(string Value, string Source);

    /// <summary>
    /// Finds the API key and workspace id. Precedence: explicit option, then the environment (CI), then the user secrets file on this
    /// machine. Throws for an unreadable secrets file (the message never contains the file's content).
    /// </summary>
    internal static (Setting? ApiKey, Setting? Workspace) Resolve(CliEnvironment environment, string? workspaceOption)
    {
        var apiKey = First(
            (environment.GetVariable, ApiKeyVariable, "environment variable"),
            (environment.GetUserSecret, ApiKeySecret, "user secrets"));
        var workspace = Normalize(workspaceOption) is { } fromOption
            ? new Setting(fromOption, "--workspace")
            : First((environment.GetVariable, WorkspaceVariable, "environment variable"), (environment.GetUserSecret, WorkspaceSecret, "user secrets"));
        return (apiKey, workspace);
    }

    public static int Run(string[] args, TextWriter output, TextWriter error, CliEnvironment environment)
    {
        string? workspaceOption = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--workspace" && i + 1 < args.Length && workspaceOption is null)
            {
                workspaceOption = args[++i];
            }
            else
            {
                error.WriteLine(CliApplication.Usage);
                return CliApplication.Failure;
            }
        }

        Setting? apiKey;
        Setting? workspace;
        try
        {
            (apiKey, workspace) = Resolve(environment, workspaceOption);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The message never contains the file's content. Nothing was sent.
            error.WriteLine($"Cannot read the user secrets: {exception.Message}");
            return CliApplication.Failure;
        }

        using var http = environment.CreateHttpClient();
        var result = new PostmanConnectionChecker(http)
            .CheckAsync(new PostmanConnectionOptions { ApiKey = apiKey?.Value ?? string.Empty, WorkspaceId = workspace?.Value })
            .GetAwaiter().GetResult();

        output.Write(Report(result, apiKey, workspace));

        if (result.Succeeded)
        {
            return CliApplication.Success;
        }

        error.WriteLine($"Connection check failed: {result.Status}.");
        return result.Step == PostmanConnectionStep.Configuration ? CliApplication.Failure : CliApplication.ConnectionFailed;
    }

    /// <summary>The first source that has a non-blank value; later sources are not even read (a broken secrets file is irrelevant when the environment is set).</summary>
    private static Setting? First(params (Func<string, string?> Get, string Name, string Kind)[] sources)
    {
        foreach (var (get, name, kind) in sources)
        {
            if (Normalize(get(name)) is { } value)
            {
                return new Setting(value, kind == "user secrets" ? "user secrets" : name);
            }
        }

        return null;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Report(PostmanConnectionResult result, Setting? apiKey, Setting? workspace)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append('\n');

        Line("Postman connection check (read-only: only GET requests are sent)");
        Line($"  API key:         {(apiKey is null ? "not set" : $"{Mask(apiKey.Value)} (from {apiKey.Source})")}");
        Line($"  Workspace id:    {(workspace is null ? "not set" : $"{workspace.Value} (from {workspace.Source})")}");

        var authenticated = result.Step != PostmanConnectionStep.Configuration
            && !(result.Step == PostmanConnectionStep.Authentication && !result.Succeeded);
        Line($"  Authentication:  {AuthenticationLine(result, authenticated)}");
        Line($"  Workspace:       {WorkspaceLine(result)}");
        Line($"Result: {(result.Succeeded ? "CONNECTED" : $"FAILED ({result.Status})")}");

        if (!result.Succeeded)
        {
            Line(result.Message);
            Line(Hint(result));
        }

        return text.ToString();
    }

    private static string AuthenticationLine(PostmanConnectionResult result, bool authenticated)
    {
        if (authenticated)
        {
            return result.UserName is null ? "OK" : $"OK, signed in as {result.UserName}";
        }

        return result.Step == PostmanConnectionStep.Authentication ? "FAILED" : "not checked";
    }

    private static string WorkspaceLine(PostmanConnectionResult result)
    {
        if (result.WorkspaceChecked && result.Workspace is { } workspace)
        {
            var name = workspace.Name is null ? workspace.Id : $"\"{workspace.Name}\"";
            return workspace.Type is null ? $"OK, {name}" : $"OK, {name} ({workspace.Type})";
        }

        return result.Step == PostmanConnectionStep.Workspace && !result.Succeeded
            ? "FAILED"
            : result.Succeeded ? "not checked (no workspace id was given)" : "not checked";
    }

    /// <summary>Shows only that a key is present and roughly what it is; never any part of the secret itself.</summary>
    private static string Mask(string apiKey) =>
        $"{(apiKey.StartsWith("PMAK-", StringComparison.Ordinal) ? "PMAK-" : string.Empty)}… ({apiKey.Length} characters)";

    private static string Hint(PostmanConnectionResult result)
    {
        var hint = result.Status switch
        {
            PostmanConnectionStatus.NotConfigured when result.Message.StartsWith("No Postman API key", StringComparison.Ordinal)
                => $"Set the {ApiKeyVariable} environment variable or the {ApiKeySecret} user secret (see docs/postman-connection.md). The key is never accepted on the command line.",
            PostmanConnectionStatus.NotConfigured => "Fix the setting named above and run the check again.",
            PostmanConnectionStatus.InvalidApiKey => $"Check that {ApiKeyVariable} (or the {ApiKeySecret} user secret) holds a current key (Postman: Settings, API keys); generate a new one if unsure.",
            PostmanConnectionStatus.AccessDenied => "The key may be valid but not allowed here; check the key's owner and the workspace's sharing.",
            PostmanConnectionStatus.WorkspaceNotFound => $"Check --workspace / {WorkspaceVariable} / {WorkspaceSecret}: it must be the workspace's id, not its name.",
            _ => "The answer was not what the Postman API normally sends; check proxies and the address."
        };

        if (result.IsTransient)
        {
            hint = result.Status == PostmanConnectionStatus.RateLimited && result.RetryAfter is { } wait
                ? $"This may be temporary. Postman asks to wait {Math.Ceiling(wait.TotalSeconds)} second(s) before trying again."
                : "This may be temporary; try again later.";
        }

        return hint;
    }
}
