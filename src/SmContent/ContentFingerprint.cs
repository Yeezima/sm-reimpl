using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SmContent;

/// <summary>
/// Cheap fingerprint of the installed content for the session handshake: SHA-256 over the relative path and size of
/// every archive (".oppc") under the install's data folder, in ordinal path order. Two installs of the same build
/// match; a patched or modified archive changes the size of at least one entry in practice, so it is refused.
/// Reads directory metadata only; never opens or writes game files.
/// </summary>
public static class ContentFingerprint
{
    public static ulong Compute(string gameDir)
    {
        string data = Path.Combine(gameDir, "data");
        var files = Directory.EnumerateFiles(data, "*.oppc", SearchOption.AllDirectories)
            .Select(f => (Path: Path.GetRelativePath(gameDir, f).Replace('\\', '/').ToLowerInvariant(), Size: new FileInfo(f).Length))
            .OrderBy(f => f.Path, StringComparer.Ordinal);
        return Compute(files);
    }

    public static ulong Compute(IEnumerable<(string Path, long Size)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        foreach (var (path, length) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(size, length);
            hash.AppendData(size);
        }
        return BinaryPrimitives.ReadUInt64LittleEndian(hash.GetHashAndReset());
    }
}
