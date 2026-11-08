using System.Text;
using SmLauncher.Core;

namespace SmTool;

/// <summary>
/// <c>smtool release-parts build|plan</c>: the release side of a playtest build in parts (docs/releasing.md, "Parts"); the launcher reads what this
/// writes with the same code (SmLauncher.Core/Parts.cs).
/// <list type="bullet">
/// <item><c>build &lt;playtest folder&gt; --parts-dir &lt;dir&gt; --manifest &lt;x.parts.json&gt; --tag &lt;t&gt; --version &lt;v&gt; [--commit &lt;sha&gt;] [--built &lt;date&gt;] [--parts-tag &lt;tag&gt;]</c>:
/// the part zips (named by their content fingerprint) and the manifest; prints one line per part.</item>
/// <item><c>plan &lt;x.parts.json&gt; --remote &lt;assets.json&gt; [--digest &lt;asset&gt;=&lt;sha256&gt;]... [--write &lt;out.parts.json&gt;]</c>: compares the parts with the
/// assets of the parts release (GitHub's JSON: the release object or its assets array; a missing file = an empty release) and prints
/// <c>UPLOAD &lt;asset&gt; &lt;bytes&gt;</c>, <c>REUSE &lt;asset&gt; &lt;bytes&gt;</c> and <c>NODIGEST &lt;asset&gt;</c> lines. With --write the manifest is written with the
/// reused zips' SHA-256 and size (only when no NODIGEST is left). Exit 0 = planned, 3 = some existing asset has no digest (hash it, pass --digest).</item>
/// </list>
/// </summary>
public static class ReleasePartsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "build" => Build(args[1..]),
                "plan" => Plan(args[1..]),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"release-parts: {e.Message}");
            return 2;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: smtool release-parts build <playtest folder> --parts-dir <dir> --manifest <x.parts.json> --tag <t> --version <v> [--commit <sha>] [--built <date>] [--parts-tag <tag>]");
        Console.Error.WriteLine("       smtool release-parts plan <x.parts.json> --remote <assets.json> [--digest <asset>=<sha256>]... [--write <out.parts.json>]");
        return 2;
    }

    static Dictionary<string, List<string>> Options(string[] args, out List<string> positional)
    {
        var o = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        positional = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs a value");
                if (!o.TryGetValue(args[i], out var list)) o[args[i]] = list = [];
                list.Add(args[++i]);
            }
            else positional.Add(args[i]);
        }
        return o;
    }

    static string Need(Dictionary<string, List<string>> o, string name) =>
        o.TryGetValue(name, out var v) && v.Count > 0 ? v[^1] : throw new ArgumentException($"{name} is required");
    static string? Opt(Dictionary<string, List<string>> o, string name) => o.TryGetValue(name, out var v) && v.Count > 0 ? v[^1] : null;

    static int Build(string[] args)
    {
        var o = Options(args, out var pos);
        if (pos.Count != 1) return Usage();
        var r = PartsBuilder.Build(pos[0], Need(o, "--parts-dir"), Need(o, "--manifest"), Need(o, "--tag"), Need(o, "--version"), Opt(o, "--commit"), Opt(o, "--built"),
            Opt(o, "--parts-tag") ?? PartsManifest.DefaultPartsTag);
        foreach (var p in r.Manifest.Parts)
            Console.WriteLine($"PART {p.Name} {p.Asset} {p.ZipSize} {p.Entries} {p.Bytes}");
        Console.WriteLine($"manifest: {r.Manifest.Files.Count} files, {r.Manifest.TotalBytes} bytes, {r.Manifest.Parts.Count} parts, {r.Manifest.Parts.Sum(p => p.ZipSize)} bytes zipped");
        return 0;
    }

    static int Plan(string[] args)
    {
        var o = Options(args, out var pos);
        if (pos.Count != 1) return Usage();
        var manifest = PartsManifest.Parse(File.ReadAllText(pos[0]));
        string remoteFile = Need(o, "--remote");
        var remote = File.Exists(remoteFile) ? PartsUpload.ParseAssets(File.ReadAllText(remoteFile)) : [];
        var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in o.GetValueOrDefault("--digest") ?? [])
        {
            int eq = d.IndexOf('=');
            if (eq <= 0 || !Hashes.IsSha256Hex(d[(eq + 1)..])) throw new ArgumentException($"--digest '{d}' is not <asset>=<sha256>");
            digests[d[..eq]] = d[(eq + 1)..].ToLowerInvariant();
        }
        var plan = PartsUpload.Plan(manifest, remote, digests);
        foreach (var p in plan.Upload) Console.WriteLine($"UPLOAD {p.Asset} {p.ZipSize} {p.Name}");
        foreach (var p in plan.Reuse) Console.WriteLine($"REUSE {p.Asset} {p.ZipSize} {p.Name}");
        foreach (var n in plan.MissingDigest) Console.WriteLine($"NODIGEST {n}");
        Console.WriteLine($"upload: {plan.Upload.Count} part(s), {plan.UploadBytes} bytes; reuse: {plan.Reuse.Count} part(s)");
        if (plan.MissingDigest.Count > 0) return 3;
        if (Opt(o, "--write") is { } outPath)
        {
            manifest.Validate();
            File.WriteAllText(outPath, manifest.ToJson() + "\n", new UTF8Encoding(false));
        }
        return 0;
    }
}
