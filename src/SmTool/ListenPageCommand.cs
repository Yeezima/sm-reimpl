using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SmClient.ImpactRules;
using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool listen-page [--game dir] [--out dir] [--count k]</c> (sound/original-melee-hits): the curated listening page <c>listen.html</c> (default folder
/// %LOCALAPPDATA%\sm-reimpl\cache\previews; WAVs under <c>listen\</c>): one card per weapon (Marine and Chaos versions merged when their events are identical), in the owner's
/// order, rows light hit / heavy strike / extra targets in the same swing / charge as they apply, each with NOW (what the build plays today) and PLANNED (the data's own
/// events, armour and flesh); then the ranged weapons' hit confirms. Everything is rendered offline (see <see cref="AudioRender.Help"/> for the limits).
/// </summary>
static class ListenPageCommand
{
    static readonly Regex Light = new(@"^ability_melee_(x\d+|ck\d+|180)$", RegexOptions.IgnoreCase);
    static readonly Regex Heavy = new(@"^(ability_melee_y\d*|ability_displace_(kick|slam))$", RegexOptions.IgnoreCase);
    static readonly Regex Bullrush = new(@"^ability_bullrush_01$", RegexOptions.IgnoreCase);
    static readonly Regex Dash = new(@"^ability_dash_attack$", RegexOptions.IgnoreCase);

    sealed record Row(string Label, string Note, List<(string Caption, PreviewSource? Source)> Now, List<(string Caption, PreviewSource Source)> Planned, string NowNote = "");
    sealed record Card(string Title, string Subtitle, List<Row> Rows);

    static readonly string[][] Families =
    [
        ["Chainsword", "sm_mp_melee_chainsword", "sm_mp_melee_chainsword_gold", "csm_mp_melee_chainsword", "csm_mp_melee_chainsword_gold"],
        ["Combat knife", "sm_mp_melee_combat_knife", "csm_mp_melee_combat_knife"],
        ["Power sword", "sm_mp_melee_powersword", "csm_mp_melee_powersword"],
        ["Power axe", "sm_mp_melee_power_axe"],
        ["Chain axe", "csm_mp_melee_chain_axe"],
        ["Thunder hammer / daemon maul", "sm_mp_melee_2hand_thunder_hammer", "csm_mp_melee_2hand_power_mace"],
        ["Heavy kick (displace)", "sm_mp_core_abil_heavy_evade"],
    ];

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
        string outDir = Path.GetFullPath(Opt(rest, "--out") ?? Path.Combine(AudioCache.Root, "previews"));
        if (AudioCache.RefuseOutputDir(outDir, gameDir, repoRoot) is { } refusal) { Console.Error.WriteLine($"Refusing: {refusal}."); return 1; }
        int count = int.TryParse(Opt(rest, "--count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c > 0 ? Math.Min(c, 8) : 3;
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: repoRoot, log: Console.Error, seed: 20261106);
        var refs = MeleeHitSoundsCommand.HitRefs(gameDir, catalog);
        var triggers = SoundTriggers.Load(gameDir, refs.Select(r => r.Weapon).Distinct(), [], null);
        var kit = new PreviewKit(catalog, outDir, "listen", count, 2);
        var warnings = new List<string>();
        var cards = new List<Card>();

        // ---- the melee weapons
        foreach (var family in Families)
        {
            string title = family[0];
            var groups = family.Skip(1).Where(w => refs.Any(r => r.Weapon == w)).GroupBy(w => Signature(refs, w, Light, Heavy)).ToList();
            foreach (var g in groups)
            {
                var weapons = g.ToList();
                string sub = string.Join(", ", weapons) + (groups.Count > 1 ? "  (not identical to the other versions)" : "");
                cards.Add(new Card(groups.Count > 1 ? $"{title} ({(weapons[0].StartsWith("csm") ? "Chaos" : "Space Marine")})" : title, sub, MeleeRows(refs, triggers, weapons[0], weapons[0].Contains("heavy_evade") ? "sm_mp_melee_2hand_thunder_hammer" : weapons[0])));
            }
        }

        // ---- charges
        var melee = refs.Select(r => r.Weapon).Distinct().Where(w => w.Contains("_melee_")).ToList();
        var bullrushSigs = melee.Select(w => Signature(refs, w, Bullrush)).Where(s => s.Length > 0).Distinct().ToList();
        if (bullrushSigs.Count > 1) warnings.Add("The bull rush's hit events differ between the melee wargear files; the card shows the chainsword's.");
        cards.Add(new Card("Bull rush charge", "ability_bullrush_01 of every melee wargear", ChargeRows(refs, triggers, "sm_mp_melee_chainsword", Bullrush, dash: false)));
        cards.Add(new Card("Jump-pack dash attack", "ability_dash_attack of the jump packs (sm_mp_abil_jumppack, csm_mp_abil_jumppack)", ChargeRows(refs, triggers, "sm_mp_abil_jumppack", Dash, dash: true)));
        if (Signature(refs, "sm_mp_abil_jumppack", Dash) != Signature(refs, "csm_mp_abil_jumppack", Dash)) warnings.Add("The Space Marine and Chaos jump packs' dash attacks differ; the card shows the Space Marine's.");

        // ---- the ranged weapons
        var ranged = RangedConfirmsCommand.Weapons(gameDir);
        var rangedCards = new List<Card>();
        foreach (var g in ranged.GroupBy(w => w.Event ?? "").OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key))
        {
            var rows = new List<Row>();
            string names = string.Join(", ", g.Select(w => w.Name));
            if (g.Key.Length > 0)
                foreach (var (label, kill, armoured) in RangedConfirmsCommand.Cases)
                    rows.Add(new Row($"Hit confirm: {label}", g.Key, [("soft confirm (default today)", new DataSource([RangedConfirmsCommand.Now(g.Key, kill, armoured)], "now_" + RangedConfirmsCommand.Slug(label)))],
                        [("the data's event", new DataSource([RangedConfirmsCommand.Planned(g.Key, kill, armoured)], "planned_" + RangedConfirmsCommand.Slug(label)))]));
            foreach (var w in g.Where(w => w.Confirms.Select(b => b.StartEvent).Distinct().Count() > 1)) warnings.Add($"{w.Name} names different confirm events per fire mode ({string.Join(", ", w.Confirms.Select(b => FireMode(b)).Distinct())}); the client plays the first in file order, {w.Event}, which is what is shown.");
            string title = g.Key.Length == 0 ? "Ranged weapons with no hit confirm in the data" : $"Ranged: {string.Join(", ", g.Select(w => Base(w.Name)).Distinct())}";
            rangedCards.Add(new Card(title, names + (g.Key.Length == 0 ? "  (nothing plays for the shooter today, nothing planned)" : ""), rows));
        }
        cards.AddRange(rangedCards);

        // ---- the page
        var html = new StringBuilder();
        html.AppendLine($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Hit sounds: now and planned</title><style>{PreviewKit.Css}</style></head><body>");
        html.AppendLine("<h1>Hit sounds: what we play now, and the original's</h1>");
        html.AppendLine("<p class=\"note\">Previews have no distance, no positioning and no reverb; levels are the data's relative levels (the client's global gain is left out), so quiet or loud here means quiet or loud against the other sounds on this page. Each row has <b>NOW</b> (what our current build plays) and <b>PLANNED</b> (the original's own event, chosen by the attack and the victim's material). Variant 1 is shown; <i>more</i> opens variants 2 and 3 (random draws of the same event). Click the play button of a player. Where our build mixes the armour and flesh layers together, NOW is one player. Low-pass is approximated offline. The full list of every event is <a href=\"melee-hits/index.html\">melee-hits/index.html</a>; the ranged list is <a href=\"ranged-confirms/index.html\">ranged-confirms/index.html</a>.</p>");
        html.AppendLine("<p>" + string.Join(" | ", cards.Select((cd, i) => $"<a href=\"#c{i}\">{WebUtility.HtmlEncode(cd.Title)}</a>")) + "</p>");
        for (int i = 0; i < cards.Count; i++)
        {
            var cd = cards[i];
            html.AppendLine($"<section class=\"card\" id=\"c{i}\"><h2>{WebUtility.HtmlEncode(cd.Title)}</h2><div class=\"sub\">{WebUtility.HtmlEncode(cd.Subtitle)}</div>");
            foreach (var row in cd.Rows)
            {
                html.Append($"<div class=\"row\"><div class=\"lab\">{WebUtility.HtmlEncode(row.Label)}<span class=\"ev\">{WebUtility.HtmlEncode(row.Note)}</span></div>");
                html.Append("<div class=\"col now\"><h4>NOW</h4>");
                if (row.Now.Count == 0) html.Append("<em class=\"none\">nothing is played today</em>");
                foreach (var (cap, src) in row.Now) html.Append(src is null ? "" : PreviewKit.Player(kit.Render(src), cap));
                if (row.NowNote.Length > 0) html.Append($"<span class=\"ev\">{WebUtility.HtmlEncode(row.NowNote)}</span>");
                html.Append("</div><div class=\"col pl\"><h4>PLANNED (original)</h4>");
                foreach (var (cap, src) in row.Planned) html.Append(PreviewKit.Player(kit.Render(src), cap));
                html.AppendLine("</div></div>");
            }
            html.AppendLine("</section>");
        }
        if (warnings.Count > 0 || kit.Silent.Count > 0)
        {
            html.AppendLine("<h2>Notes on the data</h2><ul>");
            foreach (var w in warnings.Distinct()) html.AppendLine($"<li>{WebUtility.HtmlEncode(w)}</li>");
            foreach (var s in kit.Silent) html.AppendLine($"<li>No sound for {WebUtility.HtmlEncode(s)} (not in the banks or no media).</li>");
            html.AppendLine("</ul>");
        }
        html.AppendLine("</body></html>");
        Directory.CreateDirectory(outDir);
        string page = Path.Combine(outDir, "listen.html");
        File.WriteAllText(page, html.ToString(), new UTF8Encoding(false));
        foreach (var w in warnings.Distinct()) Console.WriteLine("note: " + w);
        Console.WriteLine($"{cards.Count} cards, {kit.Files} WAV files ({kit.Silent.Count} sources without sound), page {page}");
        return 0;
    }

    // ------------------------------------------------------------------ melee cards

    static string FireMode(SoundBinding b) => b.StartEvent + " (" + string.Join("/", b.Path.Split('/').Skip(2).Take(1).Concat(b.Path.Split('/').Where(p => p.Contains("charge_"))).Distinct()) + ")";

    static string Base(string wargear) => Regex.Replace(wargear, "^(sm|csm)_mp_(ranged_|pistol_|exotic_|melee_)?", "");

    /// <summary>The wargear's hit events by category, as one comparable string (empty: none).</summary>
    static string Signature(List<MeleeHitSoundsCommand.HitRef> refs, string weapon, params Regex[] categories) =>
        string.Join(";", refs.Where(r => r.Weapon == weapon && categories.Any(c => c.IsMatch(r.Ability))).Select(r => $"{(Light.IsMatch(r.Ability) ? "L" : Heavy.IsMatch(r.Ability) ? "H" : "C")}/{r.Slot}/{r.Event}/{r.Ability}").Order(StringComparer.Ordinal));

    /// <summary>The planned sound of an attack's hit: every command it fires together (a hit can run several: a container in hit_sim and a post in hit_audio), each with the victim's material.</summary>
    static PreviewSource Planned(IReadOnlyList<string> events, string material) =>
        new DataSource(events.Select(ev => new AudioRender.PostSpec(ev, material == "flesh" ? "flesh" : "ceramite_armour", material == "flesh" ? "set_state_impact_broken_armor" : "set_state_impact_armor")).ToList(), "planned_" + material);

    static List<(string, PreviewSource)> PlannedBoth(IReadOnlyList<string> events) => [("armour", Planned(events, "armour")), ("flesh", Planned(events, "flesh"))];

    /// <summary>Rows for a category: the abilities grouped by the set of events they fire together.</summary>
    static IEnumerable<Row> RowsOf(string label, IEnumerable<MeleeHitSoundsCommand.HitRef> hits, List<(string, PreviewSource?)> now, string nowNote)
    {
        var groups = hits.GroupBy(r => r.Ability).Select(g => (Ability: g.Key, Events: g.Select(x => x.Event).Order(StringComparer.Ordinal).ToList()))
            .GroupBy(x => string.Join(" + ", x.Events)).ToList();
        foreach (var g in groups)
            yield return new Row(label, g.Key + (groups.Count > 1 ? $"  ({string.Join(", ", g.Select(x => ShortAbility(x.Ability)))})" : ""), now, PlannedBoth(g.First().Events), nowNote);
    }

    /// <summary>NOW for a melee hit of a weapon: the generated sound (classic and Musou) where it replaces the data's, else the data's weapon hit with the armour and flesh layers together.</summary>
    static List<(string, PreviewSource?)> NowMelee(SoundTriggers triggers, string weapon, out string note)
    {
        note = "";
        if (PreviewKit.SynthOf(weapon) is { } snd)
        {
            string letter = snd == HitSound.Sizzle ? "A" : "B";
            note = $"our build plays its generated sound {letter} for every melee hit of this weapon (not the data's)";
            return [($"generated sound {letter}, classic (with bass)", new SynthSource(snd, false)), ($"generated sound {letter}, Musou Mode (no bass)", new SynthSource(snd, true))];
        }
        if (triggers.MeleeHitSound(weapon) is not { } ev) { note = "this wargear has no melee hit sound in our build"; return []; }
        note = $"our build plays {ev} for every hit of this weapon, whatever the attack";
        return [("armour + flesh layers together (as played)", new DataSource([new AudioRender.PostSpec(ev, "ceramite_armour", "set_state_impact_broken_armor")], "now_both"))];
    }

    static List<Row> MeleeRows(List<MeleeHitSoundsCommand.HitRef> refs, SoundTriggers triggers, string weapon, string nowWeapon)
    {
        var now = NowMelee(triggers, nowWeapon, out string nowNote);
        if (nowWeapon != weapon) nowNote += " (the heavy class swings its equipped weapon; shown with the thunder hammer)";
        var mine = refs.Where(r => r.Weapon == weapon).ToList();
        var rows = new List<Row>();
        rows.AddRange(RowsOf("Light hit", mine.Where(r => r.Slot == "primary" && Light.IsMatch(r.Ability)), now, nowNote));
        rows.AddRange(RowsOf("Heavy strike", mine.Where(r => r.Slot == "primary" && Heavy.IsMatch(r.Ability)), now, nowNote));
        rows.AddRange(RowsOf("Extra targets in the same swing", mine.Where(r => r.Slot == "secondary" && (Light.IsMatch(r.Ability) || Heavy.IsMatch(r.Ability))), now, nowNote));
        return rows;
    }

    static string ShortAbility(string ability) => ability.Replace("ability_melee_", "").Replace("ability_displace_", "");

    static List<Row> ChargeRows(List<MeleeHitSoundsCommand.HitRef> refs, SoundTriggers triggers, string weapon, Regex cat, bool dash)
    {
        var mine = refs.Where(r => r.Weapon == weapon && cat.IsMatch(r.Ability)).ToList();
        // NOW: the charge's own impact (body smash), and for a bull rush the equipped weapon's hit layered under it (ours; the build's extra layer).
        var specs = new List<AudioRender.PostSpec>();
        string note = "";
        if (triggers.ChargeHitSound(weapon) is { } ev)
        {
            specs.Add(new AudioRender.PostSpec(ev, "ceramite_armour", "set_state_impact_broken_armor"));
            note = $"our build plays {ev}";
            if (!dash && triggers.MeleeHitSound(weapon) is { } layer && layer != ev)
            {
                specs.Add(new AudioRender.PostSpec(layer, "ceramite_armour", "set_state_impact_broken_armor"));
                note += $" plus the equipped weapon's own hit layered under it ({layer}, shown with the chainsword; the build's extra layer, to go)";
            }
        }
        List<(string, PreviewSource?)> now = specs.Count == 0 ? [] : [("armour + flesh layers together (as played)", new DataSource(specs, "now_charge"))];
        var rows = new List<Row>();
        rows.AddRange(RowsOf("Charge", mine.Where(r => r.Slot == "primary"), now, note));
        rows.AddRange(RowsOf("Extra targets in the same swing", mine.Where(r => r.Slot == "secondary"), now, note));
        return rows;
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
