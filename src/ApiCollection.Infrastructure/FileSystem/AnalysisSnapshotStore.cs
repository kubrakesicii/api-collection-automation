using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApiCollection.Core.Snapshots;

namespace ApiCollection.Infrastructure.FileSystem;

/// <summary>
/// Stores an <see cref="AnalysisSnapshot"/> as a small JSON file (camelCase, LF, no timestamps), meant to be kept
/// next to the collection it describes (for example <c>sample-api.snapshot.json</c> beside <c>sample-api.postman_collection.json</c>).
/// </summary>
public static class AnalysisSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(AnalysisSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    /// <exception cref="InvalidDataException">The text is not a snapshot this version understands.</exception>
    public static AnalysisSnapshot Deserialize(string json, string sourceName)
    {
        AnalysisSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<AnalysisSnapshot>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{sourceName}: not a valid snapshot ({exception.Message})", exception);
        }

        if (snapshot is null)
        {
            throw new InvalidDataException($"{sourceName}: the snapshot is empty.");
        }

        if (snapshot.SchemaVersion != AnalysisSnapshot.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"{sourceName}: snapshot schema version {snapshot.SchemaVersion} is not supported (expected {AnalysisSnapshot.CurrentSchemaVersion}).");
        }

        var duplicate = snapshot.Endpoints.GroupBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"{sourceName}: endpoint identity '{duplicate.Key}' appears more than once.");
        }

        // The fingerprint is derived from the contract; a mismatch means the file was edited by hand, and a diff against it would lie.
        var tampered = snapshot.Endpoints.FirstOrDefault(e => EndpointFingerprint.FromContract(e.Contract) != e.Fingerprint);
        if (tampered is not null)
        {
            throw new InvalidDataException(
                $"{sourceName}: the fingerprint of endpoint '{tampered.Id}' does not match its contract; the snapshot was edited by hand or is corrupt.");
        }

        return snapshot;
    }

    public static void Save(string path, AnalysisSnapshot snapshot)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(path, Serialize(snapshot));
    }

    public static AnalysisSnapshot Load(string path) => Deserialize(File.ReadAllText(path), Path.GetFileName(path));
}
