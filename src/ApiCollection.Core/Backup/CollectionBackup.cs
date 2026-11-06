namespace ApiCollection.Core.Backup;

/// <summary>
/// A copy of a collection taken from Postman before it is overwritten. The copy never contains secrets: values that look like
/// secrets (tokens, passwords, API keys, authorization headers) are replaced by a placeholder before it is stored, and
/// <see cref="RedactedPaths"/> says where. Restoring from a copy that has redactions would write the placeholders back, so a restore
/// from it must be an explicit decision.
/// </summary>
public sealed record CollectionBackup
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string CollectionUid { get; init; }

    public required string CollectionName { get; init; }

    public required string WorkspaceId { get; init; }

    public required DateTimeOffset TakenAtUtc { get; init; }

    /// <summary>The collection as JSON text, with secrets already redacted.</summary>
    public required string CollectionJson { get; init; }

    /// <summary>SHA-256 (lower-case hex) of <see cref="CollectionJson"/>; a stored copy whose hash no longer matches is corrupt or edited.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Where secret-looking values were replaced (paths and names only, never the values).</summary>
    public IReadOnlyList<string> RedactedPaths { get; init; } = [];

    /// <summary>The hash a backup of this JSON text must carry.</summary>
    public static string ComputeHash(string collectionJson) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(collectionJson))).ToLowerInvariant();
}

public sealed record BackupSaveResult(bool Saved, string? Location, string Message);

/// <summary>Where backups go. An implementation must confirm a copy is readable and intact before it reports <c>Saved</c>.</summary>
public interface ICollectionBackupStore
{
    /// <summary>Stores the backup and reads it back to verify it. Never throws for storage problems; they are a failed result.</summary>
    Task<BackupSaveResult> SaveAsync(CollectionBackup backup, CancellationToken cancellationToken = default);

    /// <exception cref="InvalidDataException">The stored copy is unreadable or its hash does not match.</exception>
    /// <exception cref="IOException">The copy could not be read.</exception>
    Task<CollectionBackup> LoadAsync(string location, CancellationToken cancellationToken = default);
}
