using ApiCollection.Core.Diff;
using ApiCollection.Core.Sync;
using ApiCollection.Infrastructure.FileSystem;
using ApiCollection.Postman.CloudApi;
using ApiCollection.Postman.Sync;

namespace ApiCollection.Cli;

/// <summary>
/// The Postman side of <c>sync --publish</c>: reads the API key and workspace the same way <c>postman-check</c> does (never from the
/// command line) and runs the safe sync of ADR 0005 (backup, publish, verify, roll back).
/// </summary>
internal static class PostmanPublish
{
    internal sealed record Settings(string ApiKey, string WorkspaceId);

    /// <summary>Ids in the collection are derived from this, so a different workspace or name is a different collection.</summary>
    public static string IdNamespace(string workspaceId, string collectionName) => $"{workspaceId}|{collectionName}";

    /// <summary>Returns false, after telling the user what is missing, when the sync cannot be configured. Nothing has been sent at that point.</summary>
    public static bool TryResolve(CliEnvironment environment, string? workspaceOption, TextWriter error, out Settings? settings)
    {
        settings = null;
        PostmanCheckCommand.Setting? apiKey;
        PostmanCheckCommand.Setting? workspace;
        try
        {
            (apiKey, workspace) = PostmanCheckCommand.Resolve(environment, workspaceOption);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"Cannot read the user secrets: {exception.Message}");
            return false;
        }

        if (apiKey is null)
        {
            error.WriteLine(
                $"No Postman API key. Set the {PostmanCheckCommand.ApiKeyVariable} environment variable or the {PostmanCheckCommand.ApiKeySecret} user secret " +
                "(see docs/postman-connection.md). The key is never accepted on the command line. Nothing was sent.");
            return false;
        }

        if (workspace is null)
        {
            error.WriteLine(
                $"No Postman workspace id. Use --workspace, the {PostmanCheckCommand.WorkspaceVariable} environment variable or the {PostmanCheckCommand.WorkspaceSecret} user secret. Nothing was sent.");
            return false;
        }

        settings = new Settings(apiKey.Value, workspace.Value);
        return true;
    }

    public static PostmanSyncReport Run(
        Settings settings, CliEnvironment environment, string collectionName, string collectionJson, ChangeSet changes, SyncDecision decision,
        string backupDirectory, bool dryRun, bool autoRollback)
    {
        using var http = environment.CreateHttpClient();
        var client = new PostmanCloudClient(http, new PostmanCloudOptions { ApiKey = settings.ApiKey });
        var orchestrator = new PostmanSyncOrchestrator(client, new FileCollectionBackupStore(backupDirectory));

        return orchestrator.SyncAsync(new PostmanSyncRequest
        {
            WorkspaceId = settings.WorkspaceId,
            CollectionName = collectionName,
            CollectionJson = collectionJson,
            IdNamespace = IdNamespace(settings.WorkspaceId, collectionName),
            Decision = decision,
            Changes = changes,
            DryRun = dryRun,
            AutoRollback = autoRollback
        }).GetAwaiter().GetResult();
    }
}
