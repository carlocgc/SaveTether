using System.Security.Cryptography;

namespace SaveTether.Core.FileSystem;

/// <summary>Content hashes, used to check backups and to compare saves.</summary>
public static class FileHasher
{
    /// <summary>The SHA-256 of a file's content, as lowercase hex.</summary>
    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
