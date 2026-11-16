using System.Net;
using System.Text;
using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool stalker-confirm-preview [--game dir] [--out dir] [--count k]</c> (sound/stalker-hit-confirm): WAV previews of the Stalker / Daemon Eye Bolter's hit confirm as the shooter hears it,
/// before and after the change: before Classic = <c>play_stalker_smack</c> at -14 dB (the distance falloff's -18 dB from 40 m plus the mix pass's +4 dB; the mix pass's
/// 250 Hz high-pass is NOT rendered, so the real one was thinner still); after = the same event at the data's level (<see cref="StalkerHitConfirm.Offset"/>), which is also what Musou now
/// plays on the shooter for an Ork hit (flesh). Musou's former bolter smack is an added-content sound and not rendered here (the core tool does not reach Extras).
/// The decoded original audio goes only to the out folder (default %LOCALAPPDATA%\sm-reimpl\cache\previews\stalker-confirm), never the repository.
/// </summary>
static class StalkerConfirmPreviewCommand
{
    public static int Run(string[] rest)
    {
        try { return RunCore(rest); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int RunCore(string[] rest)
    {
        string? repoRoot = RepoRoot();
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), repoRoot);
        string outDir = Path.GetFullPath(Opt(rest, "--out") ?? Path.Combine(AudioCache.Root, "previews", "stalker-confirm"));
        if (AudioCache.RefuseOutputDir(outDir, gameDir, repoRoot) is { } refusal) { Console.Error.WriteLine($"Refusing: {refusal}."); return 1; }
        int count = int.TryParse(Opt(rest, "--count"), out int c) && c > 0 ? Math.Min(c, 8) : 3;
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: repoRoot, log: Console.Error, seed: 20261109);
        var kit = new PreviewKit(catalog, outDir, "wav", count, 2);
        var before = new VoiceOffset(ImpactSounds.HitConfirmFloorDb + SoundMixPass.HitGainDb);
        var cases = new List<(string Label, AudioRender.PostSpec Spec)>
        {
            ("Classic, before: armour hit at 40 m+ (-14 dB; high-pass not rendered)", new(StalkerHitConfirm.Event, "ceramite_armour", Local: true, Offset: before)),
            ("Classic, after: armour hit (the data's level)", new(StalkerHitConfirm.Event, "ceramite_armour", Local: true, Offset: StalkerHitConfirm.Offset)),
            ("Classic, before: flesh hit at 40 m+", new(StalkerHitConfirm.Event, "flesh", Local: true, Offset: before)),
            ("Classic, after: flesh hit", new(StalkerHitConfirm.Event, "flesh", Local: true, Offset: StalkerHitConfirm.Offset)),
        };
        var html = new StringBuilder();
        html.AppendLine($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Stalker hit confirm: before and after</title><style>{PreviewKit.Css}</style></head><body>");
        html.AppendLine("<h1>Stalker Bolter / Daemon Eye Bolter hit confirm (the shooter's sound)</h1>");
        html.AppendLine($"<p class=\"note\">The data binds <code>{StalkerHitConfirm.Event}</code> to both rifles' <code>player_hit_something_action</code>. Previews have no reverb; the Musou explosion that follows a hit is not included. Each row: {count} random draws.</p><table>");
        var t = new StringBuilder();
        foreach (var (label, spec) in cases)
        {
            var files = kit.Render(new DataSource([spec], RangedConfirmsCommand.Slug(label)[..Math.Min(40, RangedConfirmsCommand.Slug(label).Length)]));
            html.AppendLine($"<tr><td>{WebUtility.HtmlEncode(label)}</td><td>{string.Concat(files.Select(f => $"<audio controls preload=\"none\" src=\"{WebUtility.HtmlEncode(f)}\"></audio>"))}</td></tr>");
            t.AppendLine($"{label}: {string.Join(", ", files)}");
        }
        html.AppendLine("</table></body></html>");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "index.html"), html.ToString(), new UTF8Encoding(false));
        Console.Write(t.ToString());
        Console.WriteLine($"rendered {kit.Files} WAV files, index {Path.Combine(outDir, "index.html")}");
        return 0;
    }

    static string? RepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        return null;
    }

    static string? Opt(string[] rest, string name)
    {
        int i = Array.IndexOf(rest, name);
        return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
    }
}
