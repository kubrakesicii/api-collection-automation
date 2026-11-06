using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApiCollection.Core.Sync;

namespace ApiCollection.Infrastructure.FileSystem;

/// <summary>Writes a <see cref="SyncDecision"/> (the dry-run plan) as deterministic JSON for tools to read.</summary>
public static class SyncDecisionSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(SyncDecision decision) =>
        JsonSerializer.Serialize(decision, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}
