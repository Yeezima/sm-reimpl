using System.Diagnostics;
using System.Globalization;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Maps;
using SmContent.Extras.Musou;

namespace SmContent.Extras.Warfront.Maps;

/// <summary>
/// Reads a lane slot's layout from ANY process kind's cache file (slice W5d; ours). <see cref="WarfrontLayoutCache"/> names its files <c>...-&lt;key&gt;-&lt;process kind&gt;.bin</c> so two processes never write one file, but the bytes do not depend on the kind
/// (same writer, same key: the generator, the reader and the format version are in the key), so a file the server or <c>smtool warfront-maps --prebuild</c> wrote warms a client too, and the reverse. Read only: it never writes and never generates;
/// a missing, cut or damaged file gives null (the caller generates). Nothing from the install is in these files.
/// </summary>
public static class WarfrontSharedCache
{
    /// <summary>The slot's cache files for the current key in <paramref name="folder"/>: <paramref name="preferredKind"/>'s first, then the newest.</summary>
    public static IReadOnlyList<string> FilesOf(string folder, WarfrontSlot slot, string preferredKind)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            string prefix = string.Create(CultureInfo.InvariantCulture, $"warfront-{slot.Name}-s{slot.Seed}-{AshfallLayoutCache.Key}-");
            string preferred = Path.GetFullPath(WarfrontLayoutCache.PathIn(folder, slot, preferredKind));
            return [.. new DirectoryInfo(folder).GetFiles(prefix + "*.bin")
                .OrderByDescending(f => string.Equals(f.FullName, preferred, StringComparison.OrdinalIgnoreCase)).ThenByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
    }

    /// <summary>The first readable file of <see cref="FilesOf"/>, or null (no file, or none readable).</summary>
    public static (AshfallLevel Level, AshfallDressing Dressing)? TryRead(string folder, WarfrontSlot slot, string preferredKind, out string note)
    {
        var watch = Stopwatch.StartNew();
        foreach (string path in FilesOf(folder, slot, preferredKind))
        {
            try
            {
                (AshfallLevel Level, AshfallDressing Dressing)? read;
                using (var stream = File.OpenRead(path)) read = AshfallLayoutCache.Read(stream, slot.Layout);
                if (read is { } found)
                {
                    note = string.Create(CultureInfo.InvariantCulture, $"{slot.Name} read from {Path.GetFileName(path)} in {watch.ElapsedMilliseconds} ms (hash {found.Level.Hash:X16})");
                    return found;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException) { }
        }
        note = $"{slot.Name}: no readable cache file";
        return null;
    }
}
