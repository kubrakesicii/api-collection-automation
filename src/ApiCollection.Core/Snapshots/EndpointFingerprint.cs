using System.Security.Cryptography;
using System.Text;
using ApiCollection.Core.Models;

namespace ApiCollection.Core.Snapshots;

/// <summary>
/// A SHA-256 over an endpoint's <see cref="EndpointContract"/> facts, used to tell "Unchanged" from "Modified" without
/// comparing every fact. Because it is derived from the facts, moving or reformatting code (which never changes a fact)
/// never changes the fingerprint.
/// </summary>
public static class EndpointFingerprint
{
    public static string Compute(EndpointDefinition endpoint) => FromContract(EndpointContract.From(endpoint));

    public static string FromContract(IReadOnlyDictionary<string, string> contract)
    {
        var text = new StringBuilder();
        foreach (var (key, value) in contract.OrderBy(fact => fact.Key, StringComparer.Ordinal))
        {
            text.Append(Escape(key)).Append('=').Append(Escape(value)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("=", "\\=", StringComparison.Ordinal)
             .Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
