using System.Formats.Tar;
using System.IO.Compression;

namespace SmTool;

/// <summary>
/// <c>smtool make-tar &lt;folder&gt; &lt;out.tar.gz&gt; --prefix &lt;top folder&gt; [--exec a,b,...]</c>: the Linux server package. A tar keeps the unix mode bits
/// (0755 for the listed files and 0644 for the rest) that a zip written on Windows may lose when it is unpacked with <c>unzip</c> or a file manager.
/// Entries are sorted, names use forward slashes under one top folder, owners are not recorded.
/// </summary>
public static class TarPackage
{
    public static int Run(string[] args)
    {
        if (args.Length < 4 || args[2] != "--prefix") { Console.Error.WriteLine("usage: smtool make-tar <folder> <out.tar.gz> --prefix <top folder> [--exec a,b,...]"); return 2; }
        var executables = args.Length >= 6 && args[4] == "--exec" ? args[5].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        Create(args[0], args[1], args[3], executables);
        Console.WriteLine($"wrote {Path.GetFileName(args[1])}");
        return 0;
    }

    public static void Create(string folder, string output, string prefix, IEnumerable<string> executables)
    {
        var exec = new HashSet<string>(executables, StringComparer.Ordinal);
        string root = Path.GetFullPath(folder);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Rel: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .OrderBy(f => f.Rel, StringComparer.Ordinal).ToList();
        foreach (var name in exec)
            if (!files.Any(f => f.Rel == name)) throw new FileNotFoundException($"the executable '{name}' is not in the folder");
        if (File.Exists(output)) File.Delete(output);
        using var file = File.Create(output);
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var (path, rel) in files)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{prefix}/{rel}")
            {
                Mode = exec.Contains(rel)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                ModificationTime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                DataStream = File.OpenRead(path),
            };
            using (entry.DataStream) tar.WriteEntry(entry);
        }
    }
}
