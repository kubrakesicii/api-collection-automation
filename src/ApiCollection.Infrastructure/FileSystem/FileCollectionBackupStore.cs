using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiCollection.Core.Backup;

namespace ApiCollection.Infrastructure.FileSystem;

/// <summary>
/// Keeps collection backups as JSON files in a folder: one file per backup, never overwritten, named after the collection, its uid and the
/// time. Each file holds an envelope (uid, name, workspace, time, hash, redacted paths) around the collection. A backup counts as saved only
/// after it was read back and its hash matched, so a damaged copy is never trusted. There is no cleanup: old backups stay until someone removes them.
/// </summary>
public sealed partial class FileCollectionBackupStore(string directory) : ICollectionBackupStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<BackupSaveResult> SaveAsync(CollectionBackup backup, CancellationToken cancellationToken = default)
    {
        try
        {
            if (CollectionBackup.ComputeHash(backup.CollectionJson) != backup.Sha256)
            {
                return new BackupSaveResult(false, null, "The backup's hash does not match its content, so it was not stored.");
            }

            var envelope = new JsonObject
            {
                ["schemaVersion"] = backup.SchemaVersion,
                ["collectionUid"] = backup.CollectionUid,
                ["collectionName"] = backup.CollectionName,
                ["workspaceId"] = backup.WorkspaceId,
                ["takenAtUtc"] = backup.TakenAtUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["sha256"] = backup.Sha256,
                ["redactedPaths"] = new JsonArray(backup.RedactedPaths.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
                ["collection"] = JsonNode.Parse(backup.CollectionJson)
            };

            Directory.CreateDirectory(directory);
            var path = UniquePath(backup);
            await File.WriteAllTextAsync(path, envelope.ToJsonString(Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", cancellationToken);

            // Read it back: a copy that cannot be read, or whose hash differs, is not a backup.
            var stored = await LoadAsync(path, cancellationToken);
            if (stored.Sha256 != backup.Sha256 || stored.CollectionUid != backup.CollectionUid)
            {
                File.Delete(path);
                return new BackupSaveResult(false, null, "The backup was written but did not read back identically, so it was discarded.");
            }

            return new BackupSaveResult(true, path, "The backup was stored and verified by reading it back.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new BackupSaveResult(false, null, $"The backup could not be stored ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    public async Task<CollectionBackup> LoadAsync(string location, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(location, cancellationToken);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The backup is not valid JSON ({exception.Message}).", exception);
        }

        if (root is not JsonObject envelope || envelope["collection"] is not JsonObject collection)
        {
            throw new InvalidDataException("The file is not a collection backup.");
        }

        var json = collection.ToJsonString();
        var recorded = (string?)envelope["sha256"];
        if (recorded is null || recorded != CollectionBackup.ComputeHash(json))
        {
            throw new InvalidDataException("The backup's hash does not match its content; it is corrupt or was edited.");
        }

        if (!DateTimeOffset.TryParse((string?)envelope["takenAtUtc"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var taken)
            || (string?)envelope["collectionUid"] is not { } uid || (string?)envelope["collectionName"] is not { } name || (string?)envelope["workspaceId"] is not { } workspace)
        {
            throw new InvalidDataException("The backup envelope is incomplete.");
        }

        return new CollectionBackup
        {
            SchemaVersion = (int?)envelope["schemaVersion"] ?? 0,
            CollectionUid = uid,
            CollectionName = name,
            WorkspaceId = workspace,
            TakenAtUtc = taken,
            CollectionJson = json,
            Sha256 = recorded,
            RedactedPaths = (envelope["redactedPaths"] as JsonArray)?.Select(p => (string?)p ?? string.Empty).ToList() ?? []
        };
    }


    private string UniquePath(CollectionBackup backup)
    {
        var safeName = SafeName().Replace(backup.CollectionName.ToLowerInvariant(), "-").Trim('-');
        var safeUid = SafeName().Replace(backup.CollectionUid.ToLowerInvariant(), "-").Trim('-');
        var stamp = backup.TakenAtUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var baseName = $"{(safeName.Length == 0 ? "collection" : safeName)}.{safeUid}.{stamp}";

        var path = Path.Combine(directory, baseName + ".backup.json");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(directory, $"{baseName}-{n}.backup.json");
        }

        return path;
    }

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex SafeName();
}
