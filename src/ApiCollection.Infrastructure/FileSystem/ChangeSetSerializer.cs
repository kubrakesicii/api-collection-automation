using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApiCollection.Core.Diff;

namespace ApiCollection.Infrastructure.FileSystem;

/// <summary>Writes a <see cref="ChangeSet"/> as deterministic JSON (camelCase, enums as names, LF, no timestamps) for tools to read.</summary>
public static class ChangeSetSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(ChangeSet changeSet) =>
        JsonSerializer.Serialize(changeSet, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}
