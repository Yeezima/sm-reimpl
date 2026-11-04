using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SmContent;
using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmTool;

/// <summary>
/// <c>smtool ranged-hit-confirms [--game dir] [--out dir] [--count k]</c> (sound/original-melee-hits, ranged scope): for every multiplayer gun, pistol and heavy weapon of both
/// sides the shooter's hit-confirm sound commands the data names (<c>player_hit_something_action</c>, with its delay, trigger point and upgrade gate, and any other sound
/// command whose trigger name has hit, kill, smack or confirm in it) resolved against the banks; then WAV previews per distinct confirm event for the cases armour, flesh,
/// kill on armour and kill on flesh: PLANNED (<c>--hit-confirm data</c>: the data's event with the victim's material, the kill cases with the death state the data's
/// <c>play_set_state_death_*</c> events set) and NOW (<c>--hit-confirm soft</c>, the build's default: no material, the local-player switch, the offset of
/// <see cref="ImpactSounds.HitConfirmOffset"/>: -10 dB (kill -7), 500 cents down on armour or 700 on flesh, low-pass +45). The low-pass is an offline approximation of the
/// client's filter. Output: table.txt and index.html in the out folder (default %LOCALAPPDATA%\sm-reimpl\cache\previews\ranged-confirms).
/// </summary>
static class RangedConfirmsCommand
{
    static readonly Regex Name = new(@"^(sm|csm)_mp_(ranged|pistol|exotic)_.+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex OtherTrigger = new("hit|kill|smack|confirm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The cases rendered: label, kill, armoured.</summary>
    internal static readonly (string Label, bool Kill, bool Armoured)[] Cases = [("armour hit", false, true), ("flesh hit", false, false), ("kill, armoured victim", true, true), ("kill, flesh victim", true, false)];

    internal sealed record Weapon(string Name, string? Event, IReadOnlyList<SoundBinding> Confirms, IReadOnlyList<SoundBinding> Others);

    /// <summary>The planned (data) post of a confirm event: the victim's material, the local-player switch, the death state a kill sets.</summary>
    internal static AudioRender.PostSpec Planned(string ev, bool kill, bool armoured) =>
        new(ev, ImpactSounds.ConfirmMaterial(armoured), null, kill ? (armoured ? ImpactSounds.DeathArmored : ImpactSounds.DeathFlesh) : null, Local: true);

    /// <summary>What the build plays today (<see cref="HitConfirmMode.Soft"/>): no material, the death state of a kill, the soft offset.</summary>
    internal static AudioRender.PostSpec Now(string ev, bool kill, bool armoured) =>
        new(ev, null, null, ImpactSounds.StateBeforeConfirm(HitConfirmMode.Soft, null, kill, armoured), Local: true, Offset: ImpactSounds.HitConfirmOffset(armoured, kill));

    internal static List<Weapon> Weapons(string gameDir)
    {
        var docs = new SortedDictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && Name.IsMatch(e.FileName), (e, d) => docs.TryAdd(e.FileName, BafDocument.Read(d)));
        var list = new List<Weapon>();
        foreach (var (name, doc) in docs)
        {
            var all = SoundBinding.Collect(doc.Root).Where(b => b.StartEvent.Length > 0).ToList();
            var confirms = all.Where(b => b.Trigger == "player_hit_something_action").ToList();
            var others = all.Where(b => b.Trigger != "player_hit_something_action" && OtherTrigger.IsMatch(b.Trigger)).ToList();
            // The client plays Of("player_hit_something_action").FirstOrDefault(): the first default-entry command.
            list.Add(new Weapon(name, confirms.FirstOrDefault(b => b.IsDefault)?.StartEvent ?? confirms.FirstOrDefault()?.StartEvent, confirms, others));
        }
        return list;
    }

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
        string outDir = Path.GetFullPath(Opt(rest, "--out") ?? Path.Combine(AudioCache.Root, "previews", "ranged-confirms"));
        if (AudioCache.RefuseOutputDir(outDir, gameDir, repoRoot) is { } refusal) { Console.Error.WriteLine($"Refusing: {refusal}."); return 1; }
        int count = int.TryParse(Opt(rest, "--count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c > 0 ? Math.Min(c, 16) : 3;
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: repoRoot, log: Console.Error, seed: 20261105);
        var weapons = Weapons(gameDir);
        var kit = new PreviewKit(catalog, outDir, "events", count, 2);
        var t = new StringBuilder();
        t.AppendLine("ranged hit confirms (shooter's sound when a shot hits a player), smtool ranged-hit-confirms");
        t.AppendLine("The client plays the first default player_hit_something_action command of the weapon in hand (NetworkView.Sound.cs HitConfirmSound).");
        t.AppendLine();
        foreach (var w in weapons)
        {
            t.AppendLine(w.Name);
            if (w.Confirms.Count == 0) t.AppendLine("    no player_hit_something_action sound command");
            foreach (var b in w.Confirms)
            {
                var play = catalog.Post(b.StartEvent, new SoundSwitches().Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue), 1);
                t.AppendLine($"    player_hit_something_action: {b.StartEvent}{(b.IsDefault ? "" : " [needs " + string.Join(",", b.RequiredUpgrades) + "]")}  point {b.TriggerPoint}, delay {b.DelayMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms, path {b.Path}  -> {(play.Found ? play.Voices.Count + " voice(s) in one post (no material)" : "NOT IN THE BANKS")}");
            }
            foreach (var b in w.Others) t.AppendLine($"    other ({b.Trigger}): {b.StartEvent}  delay {b.DelayMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
        }

        var html = new StringBuilder();
        html.AppendLine($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Ranged hit confirms: planned (data) and now (soft)</title><style>{PreviewKit.Css}</style></head><body>");
        html.AppendLine("<h1>Ranged hit confirms (the shooter's sound)</h1>");
        html.AppendLine("<p class=\"note\">Previews have no distance, positioning or reverb; levels are the data's relative levels (the client's global gain is left out). <b>Planned</b> = the data's event as <code>--hit-confirm data</code> plays it: the victim's material (ceramite_armour or flesh), the kill cases with the death state the data sets. <b>Now</b> = the default <code>--hit-confirm soft</code>: no material, the local-player switch, -10 dB (kill -7), 500 cents down on armour or 700 on flesh, low-pass +45. The low-pass is an offline two-pole approximation of the client's filter (the client uses stepped Godot bus filters), so the muffling is approximate.</p>");
        html.AppendLine("<table><tr><th>Weapons</th><th>Event</th></tr>");
        foreach (var g in weapons.GroupBy(w => w.Event ?? "(none)").OrderBy(g => g.Key))
            html.AppendLine($"<tr><td>{WebUtility.HtmlEncode(string.Join(", ", g.Select(w => w.Name)))}</td><td><code>{WebUtility.HtmlEncode(g.Key)}</code></td></tr>");
        html.AppendLine("</table>");
        foreach (var ev in weapons.Select(w => w.Event).OfType<string>().Distinct().Order())
        {
            html.AppendLine($"<h2>{WebUtility.HtmlEncode(ev)}</h2><table><tr><th>case</th><th>now (soft)</th><th>planned (data)</th></tr>");
            foreach (var (label, kill, armoured) in Cases)
            {
                var now = kit.Render(new DataSource([Now(ev, kill, armoured)], "now_" + Slug(label)));
                var planned = kit.Render(new DataSource([Planned(ev, kill, armoured)], "planned_" + Slug(label)));
                html.AppendLine($"<tr><td>{label}</td><td>{AllPlayers(now)}</td><td>{AllPlayers(planned)}</td></tr>");
            }
            html.AppendLine("</table>");
        }
        html.AppendLine("</body></html>");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "index.html"), html.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "table.txt"), t.ToString(), new UTF8Encoding(false));
        Console.Write(t.ToString());
        Console.WriteLine($"rendered {kit.Files} WAV files ({kit.Silent.Count} sources without sound), index {Path.Combine(outDir, "index.html")}");
        return 0;
    }

    static string AllPlayers(IReadOnlyList<string> files) => files.Count == 0 ? "<em class=\"none\">no sound</em>"
        : string.Concat(files.Select(f => $"<audio controls preload=\"none\" src=\"{WebUtility.HtmlEncode(f)}\"></audio>"));

    internal static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

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
