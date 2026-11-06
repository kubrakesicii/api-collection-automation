using System.Security.Cryptography;
using System.Text;

namespace ApiCollection.Postman.Generation;

/// <summary>
/// Name-based UUIDs (RFC 4122 version 5): the same name always gives the same id, so a folder or request keeps its Postman id from one
/// update to the next, and Postman does not recreate it under a new one.
/// </summary>
public static class DeterministicId
{
    /// <summary>This project's namespace for ids; changing it would change every id ever published.</summary>
    public static readonly Guid ProjectNamespace = new("8d4b6c2e-5f1a-4b7e-9c3d-2a6e0f91b7d4");

    public static string For(string name) => UuidV5(ProjectNamespace, name);

    public static string UuidV5(Guid namespaceId, string name)
    {
        var data = ToNetworkOrder(namespaceId.ToByteArray()).Concat(Encoding.UTF8.GetBytes(name)).ToArray();
        var hash = SHA1.HashData(data)[..16];
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(ToNetworkOrder(hash)).ToString();
    }

    /// <summary>Guid stores its first three fields little-endian; RFC 4122 hashes and prints them big-endian. The swap is its own inverse.</summary>
    private static byte[] ToNetworkOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
        return guid;
    }
}
