using System.Security.Cryptography;
using System.Text;
using ApiCollection.Core.Identity;
using ApiCollection.Core.Models;

namespace ApiCollection.Core.Snapshots;

/// <summary>
/// What is remembered about one analysis run so the next run can recognise the same endpoints:
/// identity, content fingerprint and the last-known names. Contains no timestamps, absolute paths or secrets,
/// so analysing an unchanged API always produces an identical snapshot.
/// </summary>
public sealed record AnalysisSnapshot
{
    /// <summary>2: endpoints carry their contract facts so a later run can say what changed, not only that something did.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The <see cref="EndpointIdentity.Version"/> the identities were created with.</summary>
    public int IdentityVersion { get; init; } = EndpointIdentity.Version;

    /// <summary>The collection generated from this analysis; the collection file itself is stored next to the snapshot.</summary>
    public SnapshotCollection? Collection { get; init; }

    /// <summary>Endpoints ordered by <see cref="SnapshotEndpoint.Id"/> (ordinal), one entry per identity.</summary>
    public IReadOnlyList<SnapshotEndpoint> Endpoints { get; init; } = [];

    /// <exception cref="ArgumentException">Two endpoints share an identity; losing one silently would corrupt later comparisons.</exception>
    public static AnalysisSnapshot Create(IEnumerable<EndpointDefinition> endpoints, SnapshotCollection? collection = null)
    {
        var entries = endpoints
            .Select(e =>
            {
                var contract = EndpointContract.From(e);
                return new SnapshotEndpoint
                {
                    Id = e.Id,
                    Fingerprint = EndpointFingerprint.FromContract(contract),
                    HttpMethod = e.HttpMethod.ToString().ToUpperInvariant(),
                    Route = e.RouteTemplate,
                    ControllerName = e.ControllerName,
                    ActionName = e.ActionName,
                    Contract = contract
                };
            })
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ThenBy(e => e.ControllerName, StringComparer.Ordinal)
            .ThenBy(e => e.ActionName, StringComparer.Ordinal)
            .ToList();

        var duplicates = entries.GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new ArgumentException(
                $"Endpoint identities must be unique, but these are produced more than once: {string.Join("; ", duplicates)}.",
                nameof(endpoints));
        }

        return new AnalysisSnapshot { Collection = collection, Endpoints = entries };
    }
}

public sealed record SnapshotEndpoint
{
    /// <summary>Stable identity (see <see cref="EndpointIdentity"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Hash of <see cref="Contract"/> (see <see cref="EndpointFingerprint"/>); a stored file whose hash does not match was edited by hand.</summary>
    public required string Fingerprint { get; init; }

    public required string HttpMethod { get; init; }

    /// <summary>The route as written, kept for display and rename detection.</summary>
    public required string Route { get; init; }

    public required string ControllerName { get; init; }

    public required string ActionName { get; init; }

    /// <summary>Everything about the endpoint's contract as plain facts (see <see cref="EndpointContract"/>); this is what a diff reads.</summary>
    public required ContractFacts Contract { get; init; }
}

public sealed record SnapshotCollection
{
    public required string FileName { get; init; }

    /// <summary>SHA-256 of the collection text with LF line endings, to detect a stored collection that no longer matches.</summary>
    public required string Sha256 { get; init; }

    public static SnapshotCollection FromJson(string fileName, string json) => new()
    {
        FileName = Path.GetFileName(fileName),
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.Replace("\r\n", "\n", StringComparison.Ordinal)))).ToLowerInvariant()
    };
}
