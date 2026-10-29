using System.IO.Compression;
using System.Text;

namespace SmFormats.Obpk;

/// <summary>
/// Builds synthetic OBPK archives following our documented interpretation of the layout.
/// For parser tests and tooling dry-runs only. It encodes our hypothesis, so a test that
/// passes against it proves internal consistency, not compatibility with original files.
/// </summary>
public static class SyntheticObpkWriter
{
    public sealed record File(string Group, string Name, byte[] Data, string Folder = "\\");

    public static byte[] Build(IReadOnlyList<File> files, int signatureBlobLength = 288) =>
        Build(files, files.Select(f => f.Group).Distinct().ToList(), signatureBlobLength);

    /// <param name="groupOrder">Type names in table order; a name with no files becomes an empty group.</param>
    public static byte[] Build(IReadOnlyList<File> files, IReadOnlyList<string> groupOrder, int signatureBlobLength = 288)
    {
        var groups = groupOrder.Select(t => (Key: t, Files: files.Where(f => f.Group == t).ToList())).ToList();
        var table = new MemoryStream();
        var w = new BinaryWriter(table, Encoding.ASCII);

        w.Write(1u); w.Write(1u); w.Write(0x12345678u);            // one ID list with one value
        // Name table: string pool without duplicates, each group's type name then its file names.
        var pool = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups)
            foreach (var s in g.Files.Select(f => f.Name).Prepend(g.Key))
                if (seen.Add(s)) pool.Add(s);
        w.Write((uint)pool.Count);
        foreach (var s in pool) WriteString(w, s);
        w.Write((uint)groups.Count);
        foreach (var g in groups)
        {
            w.Write(Fnv1a64(g.Key));                               // deterministic stand-in; the real hash function is unknown
            w.Write((uint)g.Files.Count);
        }
        foreach (var g in groups)
            foreach (var f in g.Files)
            {
                WriteString(w, f.Name);
                WriteString(w, f.Folder);
                w.Write((uint)f.Data.Length); w.Write(0xFFFFFFFFu); w.Write((uint)f.Data.Length);
                w.Write(0UL);
                w.Write((byte)4); w.Write((uint)f.Data.Length);   // real 4-byte extras repeat the size
            }
        w.Flush();

        var payload = new MemoryStream();
        foreach (var g in groups) foreach (var f in g.Files) payload.Write(f.Data);
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(payload.ToArray());

        uint tableOffset = (uint)(ObpkArchive.HeaderSize + signatureBlobLength);
        long tableEnd = tableOffset + table.Length;
        long dataOffset = (tableEnd + 0xFFF) & ~0xFFFL;

        var outStream = new MemoryStream();
        var o = new BinaryWriter(outStream, Encoding.ASCII);
        o.Write("OBPK\0"u8);
        o.Write(10u); o.Write(0u); o.Write((uint)groups.Sum(g => g.Files.Count)); o.Write(40u);
        o.Write(tableOffset); o.Write((uint)(dataOffset - tableOffset)); o.Write((uint)payload.Length);
        o.Write(0u); o.Write(0u); o.Write(10000u); o.Write(50000u);
        o.Write(new byte[signatureBlobLength]);
        o.Write(table.ToArray());
        o.Write(new byte[dataOffset - tableEnd]);
        o.Write((uint)payload.Length);
        o.Write(compressed.ToArray());
        o.Flush();
        return outStream.ToArray();
    }

    static ulong Fnv1a64(string s)
    {
        ulong h = 14695981039346656037;
        foreach (byte b in Encoding.ASCII.GetBytes(s)) h = (h ^ b) * 1099511628211;
        return h;
    }

    static void WriteString(BinaryWriter w, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        w.Write((uint)b.Length);
        w.Write(b);
    }
}
