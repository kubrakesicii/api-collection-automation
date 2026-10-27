using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApiCollection.Postman.Models;

namespace ApiCollection.Postman.Serialization;

public static class PostmanCollectionSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The output is a file for Postman, not HTML: keep {{variables}} and <placeholders> readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Serializes with LF line endings so the output is identical on every OS (stable for later diffing).</summary>
    public static string Serialize(PostmanCollection collection) =>
        JsonSerializer.Serialize(collection, Options).Replace("\r\n", "\n", StringComparison.Ordinal);
}
