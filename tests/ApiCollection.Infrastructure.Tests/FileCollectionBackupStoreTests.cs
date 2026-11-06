using System.Text.Json.Nodes;
using ApiCollection.Core.Backup;
using ApiCollection.Infrastructure.FileSystem;

namespace ApiCollection.Infrastructure.Tests;

public sealed class FileCollectionBackupStoreTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ApiCollection.Backups." + Guid.NewGuid().ToString("N"));

    public FileCollectionBackupStoreTests() => Directory.CreateDirectory(_work);

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static CollectionBackup Backup(
        string json = """{"info":{"name":"Sample API"},"item":[{"name":"Items","item":[]}]}""", string name = "Sample API",
        string uid = "1234567-abc", IReadOnlyList<string>? redacted = null, DateTimeOffset? at = null) => new()
    {
        CollectionUid = uid,
        CollectionName = name,
        WorkspaceId = "00000000-aaaa-bbbb-cccc-000000000001",
        TakenAtUtc = at ?? new DateTimeOffset(2026, 11, 6, 10, 30, 15, 123, TimeSpan.Zero),
        CollectionJson = json,
        Sha256 = CollectionBackup.ComputeHash(json),
        RedactedPaths = redacted ?? []
    };

    private FileCollectionBackupStore Store(string? directory = null) => new(directory ?? Path.Combine(_work, "backups"));

    [Fact]
    public async Task A_saved_backup_reads_back_identically_and_is_named_after_the_collection_uid_and_time()
    {
        var original = Backup(redacted: ["variable[token]"]);

        var saved = await Store().SaveAsync(original);
        var loaded = await Store().LoadAsync(saved.Location!);

        Assert.True(saved.Saved);
        Assert.Equal("sample-api.1234567-abc.20261106T103015123Z.backup.json", Path.GetFileName(saved.Location));
        Assert.Equal(original.CollectionJson, loaded.CollectionJson);
        Assert.Equal((original.CollectionUid, original.CollectionName, original.WorkspaceId, original.Sha256), (loaded.CollectionUid, loaded.CollectionName, loaded.WorkspaceId, loaded.Sha256));
        Assert.Equal(original.TakenAtUtc, loaded.TakenAtUtc);
        Assert.Equal(["variable[token]"], loaded.RedactedPaths);
        Assert.Equal(CollectionBackup.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public async Task The_file_is_an_envelope_around_the_collection_with_lf_endings()
    {
        var saved = await Store().SaveAsync(Backup());

        var text = await File.ReadAllTextAsync(saved.Location!);
        var envelope = JsonNode.Parse(text)!;

        Assert.DoesNotContain('\r', text);
        Assert.Equal("1234567-abc", (string?)envelope["collectionUid"]);
        Assert.Equal("Sample API", (string?)envelope["collection"]!["info"]!["name"]);
        Assert.Equal(CollectionBackup.ComputeHash(envelope["collection"]!.ToJsonString()), (string?)envelope["sha256"]);
    }

    [Fact]
    public async Task A_backup_is_never_overwritten_even_at_the_same_second()
    {
        var first = await Store().SaveAsync(Backup());
        var second = await Store().SaveAsync(Backup());

        Assert.NotEqual(first.Location, second.Location);
        Assert.EndsWith("-2.backup.json", second.Location);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_work, "backups")).Length);
    }

    [Fact]
    public async Task Names_that_are_not_safe_for_a_file_are_made_safe()
    {
        var saved = await Store().SaveAsync(Backup(name: "../../Evil: API?*", uid: "a/b\\c"));

        Assert.True(saved.Saved);
        Assert.Equal(Path.Combine(_work, "backups"), Path.GetDirectoryName(saved.Location));
        Assert.DoesNotContain("..", Path.GetFileName(saved.Location!));
        Assert.Matches("^[A-Za-z0-9_.-]+$", Path.GetFileName(saved.Location!));
        Assert.StartsWith("evil-api.a-b-c.", Path.GetFileName(saved.Location!));
    }

    [Fact]
    public async Task A_backup_whose_hash_does_not_match_its_content_is_not_stored()
    {
        var bad = Backup() with { CollectionJson = """{"info":{"name":"tampered"},"item":[]}""" };

        var saved = await Store().SaveAsync(bad);

        Assert.False(saved.Saved);
        Assert.Contains("hash does not match", saved.Message);
        Assert.False(Directory.Exists(Path.Combine(_work, "backups")) && Directory.GetFiles(Path.Combine(_work, "backups")).Length > 0);
    }

    [Fact]
    public async Task Storage_problems_are_a_failed_result_not_an_exception()
    {
        var blocker = Path.Combine(_work, "a-file");
        await File.WriteAllTextAsync(blocker, "not a folder");

        var saved = await Store(Path.Combine(blocker, "backups")).SaveAsync(Backup());

        Assert.False(saved.Saved);
        Assert.Null(saved.Location);
        Assert.Contains("could not be stored", saved.Message);
    }

    [Fact]
    public async Task A_stored_copy_that_was_edited_or_damaged_is_refused_when_loaded()
    {
        var saved = await Store().SaveAsync(Backup());
        var text = await File.ReadAllTextAsync(saved.Location!);

        var edited = Path.Combine(_work, "edited.json");
        await File.WriteAllTextAsync(edited, text.Replace("\"Items\"", "\"Hacked\""));
        var corrupt = Path.Combine(_work, "corrupt.json");
        await File.WriteAllTextAsync(corrupt, "{ not json");
        var foreign = Path.Combine(_work, "foreign.json");
        await File.WriteAllTextAsync(foreign, """{ "hello": "world" }""");

        Assert.Contains("corrupt or was edited", (await Assert.ThrowsAsync<InvalidDataException>(() => Store().LoadAsync(edited))).Message);
        Assert.Contains("not valid JSON", (await Assert.ThrowsAsync<InvalidDataException>(() => Store().LoadAsync(corrupt))).Message);
        Assert.Contains("not a collection backup", (await Assert.ThrowsAsync<InvalidDataException>(() => Store().LoadAsync(foreign))).Message);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Store().LoadAsync(Path.Combine(_work, "missing.json")));
    }
}
