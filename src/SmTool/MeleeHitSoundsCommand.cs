using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SmContent;
using SmFormats;
using SmFormats.Audio;
using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmTool;

/// <summary>
/// <c>smtool melee-hit-sounds [--game dir] [--out dir] [--render] [--count k]</c> (sound/original-melee-hits): for every multiplayer melee wargear of both sides (and the
/// jump packs and the heavy classes' displace ability) every ability's sound-related commands: the hit effect containers (<c>trigger_material_effect</c>) with their
/// <c>sfx</c> event, <c>post_sound_event</c> and speech commands, the delays on the way, whether each event resolves in the banks (voices per post for the armour and the
/// flesh case) and the sim's hit time for the attack. The table goes to the console and to <c>table.txt</c> under the out folder (default
/// %LOCALAPPDATA%\sm-reimpl\cache\previews\melee-hits); <c>--render</c> also renders the distinct hit events to WAVs per weapon (armour and flesh, <c>--count</c> variants,
/// default 3) and writes <c>index.html</c>. Read-only; decoded audio goes to the cache only.
/// </summary>
static class MeleeHitSoundsCommand
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly Regex WargearName = new(@"^(sm|csm)_mp_(melee_.+|abil_jumppack|core_abil_heavy_evade)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>One sound-related command found under an ability.</summary>
    sealed record Cmd(string Ability, string Slot, string Kind, BafTable Table, IReadOnlyList<string> Upgrades, IReadOnlyList<(string Wrapper, float Delay)> Delays, string Path);

    /// <summary>One container file of a name (the archive keeps base names; a Space Marine and a Chaos variant can share one).</summary>
    sealed record ContainerFile(string Group, int Ordinal, BafDocument Doc);

    sealed record EventInfo(string Name, bool Found, int WemCount, int WemsWithMedia, int ArmourVoices, int FleshVoices, bool SameWems, string ArmourDelays, string FleshDelays, int ArmourWems, int FleshWems, int SharedWems);

    /// <summary>A hit event to render: the weapon, the attacks that use it and where in the ability it sits.</summary>
    sealed record RenderItem(string Weapon, string Attack, string Event, string Slot, List<string> Abilities, string Container);

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
        string outDir = Path.GetFullPath(Opt(rest, "--out") ?? Path.Combine(AudioCache.Root, "previews", "melee-hits"));
        if (AudioCache.RefuseOutputDir(outDir, gameDir, repoRoot) is { } refusal) { Console.Error.WriteLine($"Refusing: {refusal}."); return 1; }
        int count = int.TryParse(Opt(rest, "--count"), NumberStyles.Integer, Inv, out int c) && c > 0 ? Math.Min(c, 16) : 3;
        bool render = rest.Contains("--render");
        var o = new StringBuilder();
        void P(string line = "") { o.AppendLine(line); }

        // ---- the wargear files and the material effect containers they name
        var wargear = new SortedDictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && WargearName.IsMatch(e.FileName), (e, d) => wargear.TryAdd(e.FileName, BafDocument.Read(d)));
        if (wargear.Count == 0) throw new InvalidDataException("no multiplayer melee wargear found in wargear.oppc");
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perFile = new Dictionary<string, List<Cmd>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in wargear)
        {
            var cmds = Commands(doc.Root);
            perFile[name] = cmds;
            foreach (var cmd in cmds.Where(x => x.Kind == "trigger_material_effect")) wanted.Add(Leaf(cmd.Table.String("effect_container") ?? ""));
        }
        var containers = new Dictionary<string, List<ContainerFile>>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, EffectCatalogue.MaterialEffectPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) =>
            {
                if (!containers.TryGetValue(e.FileName, out var list)) containers[e.FileName] = list = [];
                list.Add(new ContainerFile(archive.GroupName(e), list.Count + 1, BafDocument.Read(d)));
            });

        // ---- audio
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: repoRoot, log: Console.Error, seed: 1);
        var events = new Dictionary<string, EventInfo>(StringComparer.OrdinalIgnoreCase);
        EventInfo Info(string ev) => events.TryGetValue(ev, out var known) ? known : events[ev] = Resolve(catalog, ev);

        // ---- what the current build plays
        var triggers = SoundTriggers.Load(gameDir, wargear.Keys, [], null);

        P($"melee hit sounds of the multiplayer melee wargear (smtool melee-hit-sounds), {DateTime.UtcNow:yyyy-MM-dd} UTC");
        P($"game install: (auto-detected; not printed)   wargear files: {wargear.Count}   catalogue: {catalog.LoadedPackages.Count} packages, {catalog.Library.ObjectCount:N0} objects");
        P("""
            Reading guide
              A hit command is a table under the ability: <slot>/<command>, slot = primary_target_actions (the first target), secondary_target_actions (the second hit: the kick/foot),
              world_hit_actions / world_hit_trace_actions (the blade strikes a wall), start_actions (the swing). Its parts hit_sim / hit_audio / hit_fx / start_audio / start_fx say what it is.
              trigger_material_effect: effect_container = a material effect container file; its sfx table is ONE post_sound_event (start_event_name) for every material: the sound itself is
              not chosen by the container's surface tables (fx/<surface>/<damage class>), those carry only the visual effects (play_fx). use_terrain_material = True: the Wwise
              switch material_type is set from the hit surface, so the event's switch container picks the variant (the armour and flesh cases below).
              Voices: how many sounds ONE post of the event starts (armour = material_type ceramite_armour + set_state_impact_armor; flesh = material_type flesh + set_state_impact_broken_armor,
              as the client posts a body hit); "wems" = the sound files the event can reach at all.
            """);

        var items = new List<RenderItem>();
        var referenced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in wargear)
        {
            P();
            P(new string('=', 140));
            var map = MpClasses.MeleeMapFor(name);
            MeleeTuning? tuning = null;
            string tuningNote = "";
            if (map is not null)
            {
                try { tuning = MeleeTuning.Load(gameDir, map); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or KeyNotFoundException or NotSupportedException or InvalidOperationException) { tuningNote = $"tuning not loaded: {ex.Message}"; }
            }
            else tuningNote = "no combo map in the sim (not a melee weapon the sim has strikes for)";
            P($"{name}   (current build: light hit {triggers.MeleeHitSound(name) ?? "-"}, charge hit {triggers.ChargeHitSound(name) ?? "-"}; synth replacement: {SynthOf(name)})");
            if (tuningNote.Length > 0) P($"  sim: {tuningNote}");
            if (tuning is not null) P($"  sim: graph {map!.Graph}, reach {tuning.Range:0.##} m, {tuning.Steps.Count} light + {tuning.Heavy.Count} heavy strikes");
            var cmds = perFile[name];
            var abilities = AbilityNames(doc.Root);
            foreach (var ability in abilities)
            {
                var mine = cmds.Where(x => x.Ability == ability).ToList();
                string step = StepOf(map, tuning, ability);
                P();
                P($"  {ability}{(step.Length > 0 ? "   [" + step + "]" : "")}");
                foreach (var d in HitSimDelays(doc.Root, ability)) P($"      hit_sim delay: {d}");
                if (mine.Count == 0) { P("      (no sound or hit-effect commands)"); continue; }
                foreach (var cmd in mine)
                {
                    string up = cmd.Upgrades.Count > 0 ? $" [needs upgrade {string.Join(",", cmd.Upgrades)}]" : "";
                    string dly = cmd.Delays.Count > 0 ? "  delayed " + string.Join(" + ", cmd.Delays.Select(d => $"{d.Wrapper} {d.Delay.ToString("0.###", Inv)} s")) : "";
                    switch (cmd.Kind)
                    {
                        case "trigger_material_effect":
                        {
                            string container = cmd.Table.String("effect_container") ?? "";
                            referenced.Add(Leaf(container));
                            P($"      {cmd.Slot}{up}{dly}: trigger_material_effect {container}   (material_source {cmd.Table.String("material_source")})");
                            if (!containers.TryGetValue(Leaf(container), out var files)) { P("          container file NOT FOUND in material_effect_container.oppc"); break; }
                            foreach (var f in files)
                            {
                                var sfx = f.Doc.Root.Table("sfx");
                                string ev = sfx?.String("start_event_name") ?? "";
                                bool ork = IsOrkEvent(ev);
                                P($"          file {f.Ordinal}/{files.Count} ({f.Group}){(ork ? " [ORK variant of the same base name: the Orks' file, not the Marines'; ignored below]" : "")}: sfx {Fields(sfx)}");
                                if (ev.Length == 0) { P("              no sound event"); continue; }
                                P($"              {Describe(Info(ev))}");
                                if (!ork && cmd.Upgrades.Count == 0 && IsHit(cmd.Slot) && Info(ev).ArmourVoices + Info(ev).FleshVoices > 0) AddItem(items, name, ability, ev, cmd.Slot, container, files.Count > 1 ? f.Ordinal : 0);
                            }
                            break;
                        }
                        case "post_sound_event":
                        {
                            string ev = cmd.Table.String("start_event_name") ?? "";
                            P($"      {cmd.Slot}{up}{dly}: post_sound_event {Fields(cmd.Table)}");
                            if (ev.Length > 0)
                            {
                                P($"              {Describe(Info(ev))}");
                                if (cmd.Upgrades.Count == 0 && IsHit(cmd.Slot) && !IsOrkEvent(ev) && Info(ev).ArmourVoices + Info(ev).FleshVoices > 0) AddItem(items, name, ability, ev, cmd.Slot, "(direct)", 0);
                            }
                            break;
                        }
                        default:
                            P($"      {cmd.Slot}{up}{dly}: {cmd.Kind} {Fields(cmd.Table)}");
                            break;
                    }
                }
            }
            // The wargear's own triggered actions that post sounds (the swing and the antic tags), with their delays.
            var bindings = SoundBinding.Collect(doc.Root).Where(b => b.StartEvent.Length > 0 && b.Ability.Length == 0).ToList();
            if (bindings.Count > 0)
            {
                P();
                P("  triggered_actions / other sound commands outside the abilities:");
                foreach (var b in bindings)
                    P($"      {b.Trigger,-30} {b.StartEvent,-34} delay {b.DelayMilliseconds.ToString("0", Inv)} ms, point {b.TriggerPoint}{(b.IsDefault ? "" : " [" + string.Join(",", b.RequiredUpgrades) + "]")}   {(Info(b.StartEvent).Found ? $"armour {Info(b.StartEvent).ArmourVoices}v / flesh {Info(b.StartEvent).FleshVoices}v" : "NOT IN THE BANKS")}");
            }
        }

        // ---- the containers' visual effects by surface (the only per-material part of a container)
        P();
        P(new string('=', 140));
        P("Containers: effects by surface (fx/<surface>/<damage class>: play_fx names). The sound (sfx) is one event per file.");
        foreach (var name in referenced)
        {
            if (!containers.TryGetValue(name, out var files)) continue;
            foreach (var f in files)
            {
                P($"  {name} (file {f.Ordinal}/{files.Count}, {f.Group}): sfx {f.Doc.Root.Table("sfx")?.String("start_event_name") ?? "-"}");
                var fx = FxBinding.Collect(f.Doc.Root).Where(b => b.Play is not null).ToList();
                foreach (var surface in fx.GroupBy(b => b.Path.Split('/').ElementAtOrDefault(1) ?? ""))
                    P($"      {surface.Key,-12} {string.Join(", ", surface.Select(b => $"{b.Path.Split('/').ElementAtOrDefault(2)}={b.Play!.Name}"))}");
                if (fx.Count == 0) P("      no effects");
            }
        }

        // ---- summary per weapon
        P();
        P(new string('=', 140));
        P("Summary: hit events per weapon (default entries: what the original plays when the blow connects)");
        foreach (var g in items.GroupBy(i => i.Weapon).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            P($"  {g.Key}");
            foreach (var i in g.OrderBy(i => i.Slot).ThenBy(i => i.Event))
            {
                var info = Info(i.Event);
                P($"      {ShortSlot(i.Slot),-10} {i.Event,-34} armour {info.ArmourVoices}v / flesh {info.FleshVoices}v   attacks: {string.Join(", ", i.Abilities)}{(i.Container.Length > 0 && i.Container != "(direct)" ? "   via " + Leaf(i.Container) : "")}");
            }
        }

        string text = o.ToString();
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "table.txt"), text, new UTF8Encoding(false));
        Console.Write(text);
        Console.WriteLine($"\ntable written to {Path.Combine(outDir, "table.txt")}");

        if (render) return RenderAll(gameDir, repoRoot, outDir, items, count);
        return 0;
    }

    // ------------------------------------------------------------------ rendering

    static int RenderAll(string gameDir, string? repoRoot, string outDir, List<RenderItem> items, int count)
    {
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: repoRoot, log: Console.Error, seed: 20261104);
        var index = new StringBuilder();
        index.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Melee hit sounds: original events, offline preview</title>");
        index.AppendLine("<style>body{font:14px/1.4 system-ui,sans-serif;margin:16px auto;max-width:1100px;padding:0 16px;color:#222;background:#fafafa}h2{margin-top:32px;border-bottom:1px solid #ccc}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:4px 8px;vertical-align:top;text-align:left}code{background:#eee;padding:0 3px}audio{width:210px;height:30px}.m{color:#555;font-size:12px}</style></head><body>");
        index.AppendLine("<h1>Melee hit sounds: the original events, offline preview</h1>");
        index.AppendLine("<p>Each row is one distinct hit event of a weapon's data (default entries, no upgrade). One post of the event is rendered per variant, with the volume, pitch and delay the data resolves; no 3D attenuation, no low-pass, no bus effects (so the real game sounds a little different). Armour = <code>material_type ceramite_armour</code> with the armour impact state, flesh = <code>flesh</code> with the broken-armour state. Variants are successive random draws.</p>");
        int wavs = 0, failures = 0;
        foreach (var g in items.GroupBy(i => i.Weapon).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            index.AppendLine($"<h2>{WebUtility.HtmlEncode(g.Key)}</h2><table><tr><th>attack(s)</th><th>slot</th><th>event</th><th>material</th><th>variants</th><th>post</th></tr>");
            foreach (var i in g.OrderBy(i => i.Slot).ThenBy(i => i.Event))
            {
                foreach (var material in new[] { "armour", "flesh" })
                {
                    var cells = new StringBuilder();
                    string post = "";
                    for (int n = 1; n <= count; n++)
                    {
                        var r = AudioRender.Render(catalog, i.Event, material, local: false, channels: 2);
                        if (!r.Found || r.Wems == 0) { failures++; post = r.Found ? "no media" : "event not in the banks"; break; }
                        string rel = $"{AudioRender.SafeName(g.Key)}/{AudioRender.SafeName(g.Key)}__{AudioRender.SafeName(i.Attack)}__{AudioRender.SafeName(i.Event)}__{material}__v{n}.wav";
                        AudioRender.WriteWav(Path.Combine(outDir, rel), r);
                        wavs++;
                        cells.Append($"<audio controls preload=\"none\" src=\"{WebUtility.HtmlEncode(rel)}\"></audio> ");
                        if (n == 1) post = $"{r.Voices} voice(s), peak {r.PeakDb:0.0} dBFS";
                    }
                    index.AppendLine($"<tr><td>{WebUtility.HtmlEncode(string.Join(", ", i.Abilities))}</td><td>{WebUtility.HtmlEncode(ShortSlot(i.Slot))}</td><td><code>{WebUtility.HtmlEncode(i.Event)}</code></td><td>{material}</td><td>{cells}</td><td class=\"m\">{WebUtility.HtmlEncode(post)}</td></tr>");
                }
            }
            index.AppendLine("</table>");
        }
        index.AppendLine("</body></html>");
        File.WriteAllText(Path.Combine(outDir, "index.html"), index.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"rendered {wavs} WAV files ({failures} events without media or missing), index: {Path.Combine(outDir, "index.html")}");
        return 0;
    }

    // ------------------------------------------------------------------ reading the wargear

    static void AddItem(List<RenderItem> items, string weapon, string ability, string ev, string slot, string container, int variant)
    {
        string kind = ShortSlot(slot);
        var hit = items.FirstOrDefault(i => i.Weapon == weapon && i.Event == ev && ShortSlot(i.Slot) == kind && i.Container == container);
        if (hit is null) items.Add(new RenderItem(weapon, kind == "primary" ? ability : $"{ability}_{kind}", ev, slot, [ability], container));
        else if (!hit.Abilities.Contains(ability)) hit.Abilities.Add(ability);
    }

    /// <summary>The Orks' container files carry the same base names as the Marines' (the archive keeps base names); their events start with play_or_.</summary>
    static bool IsOrkEvent(string ev) => ev.StartsWith("play_or_", StringComparison.OrdinalIgnoreCase);

    static bool IsHit(string slot) => slot.Contains("primary_target_actions") || slot.Contains("secondary_target_actions");

    static string ShortSlot(string slot) => slot.StartsWith("primary", StringComparison.Ordinal) ? "primary" : slot.StartsWith("secondary", StringComparison.Ordinal) ? "secondary"
        : slot.StartsWith("world_hit_trace", StringComparison.Ordinal) ? "world-trace" : slot.StartsWith("world", StringComparison.Ordinal) ? "world" : slot.StartsWith("start", StringComparison.Ordinal) ? "start" : slot;

    static string SynthOf(string wargear) => wargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase) || wargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase) ? "yes (Roar)"
        : wargear.Contains("power_axe", StringComparison.OrdinalIgnoreCase) || wargear.Contains("powersword", StringComparison.OrdinalIgnoreCase) ? "yes (Sizzle)" : "no";

    static List<string> AbilityNames(BafTable root) =>
        root.Table("ability_data/ability_definitions")?.Entries.Where(e => e.Type == BafType.Table).Select(e => e.Name).ToList() ?? [];

    static readonly HashSet<string> Interesting = new(StringComparer.OrdinalIgnoreCase) { "trigger_material_effect", "post_sound_event" };

    static bool IsSoundish(string kind) => Interesting.Contains(kind) || kind.Contains("speech", StringComparison.OrdinalIgnoreCase) || kind.Contains("sound", StringComparison.OrdinalIgnoreCase)
        || kind.Contains("voice", StringComparison.OrdinalIgnoreCase);

    /// <summary>The sound and hit-effect commands of every ability, with the upgrades they need and the delayed_action wrappers on the way.</summary>
    static List<Cmd> Commands(BafTable root)
    {
        var found = new List<Cmd>();
        if (root.Table("ability_data/ability_definitions") is not { } defs) return found;
        foreach (var a in defs.Entries.Where(e => e.Type == BafType.Table))
            Walk(a.AsTable(), a.Name, [], [], [], found, 0);
        return found;
    }

    static void Walk(BafTable table, string ability, List<string> path, List<string> upgrades, List<(string, float)> delays, List<Cmd> found, int depth)
    {
        if (depth > 40) return;
        foreach (var e in table.Entries)
        {
            if (e.Type != BafType.Table) continue;
            var child = e.AsTable();
            string kind = Leaf(child.String("$ref") ?? "");
            path.Add(e.Name);
            var required = child.Table("required_upgrade_name/required_list")?.Entries.Where(x => x.Type == BafType.String).Select(x => x.AsString()).ToList() ?? [];
            upgrades.AddRange(required);
            if (IsSoundish(kind))
            {
                var slotParts = path.Where(p => p is not ("actions" or "commands" or "actions_by_upgrade" or "entry" or "action" or "delayed_action" or "in_action") && p != e.Name).ToList();
                found.Add(new Cmd(ability, string.Join("/", slotParts.Take(2)), kind, child, upgrades.Distinct().ToList(), [.. delays], string.Join("/", path)));
            }
            else
            {
                bool delayed = kind.Equals("delayed_action", StringComparison.OrdinalIgnoreCase);
                if (delayed) delays.Add((e.Name, Num(child, "delay") + Num(child, "time_delay")));
                Walk(child, ability, path, upgrades, delays, found, depth + 1);
                if (delayed) delays.RemoveAt(delays.Count - 1);
            }
            upgrades.RemoveRange(upgrades.Count - required.Count, required.Count);
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>delayed_action wrappers in an ability's hit_sim (when the damage is dealt after the hit), with their delay.</summary>
    static IEnumerable<string> HitSimDelays(BafTable root, string ability)
    {
        if (root.Table($"ability_data/ability_definitions/{ability}") is not { } a) yield break;
        var found = new List<string>();
        Delayed(a, [], found, 0);
        foreach (var d in found.Distinct().Take(6)) yield return d;
    }

    static void Delayed(BafTable table, List<string> path, List<string> found, int depth)
    {
        if (depth > 40) return;
        foreach (var e in table.Entries)
        {
            if (e.Type != BafType.Table) continue;
            var child = e.AsTable();
            path.Add(e.Name);
            if (Leaf(child.String("$ref") ?? "").Equals("delayed_action", StringComparison.OrdinalIgnoreCase) && path.Contains("hit_sim"))
            {
                float delay = Num(child, "delay") + Num(child, "time_delay");
                if (delay > 0f) found.Add($"{string.Join("/", path.Take(2))} {e.Name} {delay.ToString("0.###", Inv)} s");
            }
            else Delayed(child, path, found, depth + 1);
            path.RemoveAt(path.Count - 1);
        }
    }

    static float Num(BafTable t, string name) => t[name] is { } e ? e.Type switch { BafType.Float => e.AsFloat(), BafType.Int => e.Raw, _ => 0f } : 0f;

    /// <summary>The scalar fields of a command table, "name=value" (the $ref and nested tables left out).</summary>
    static string Fields(BafTable? t)
    {
        if (t is not { } table) return "(none)";
        var parts = new List<string>();
        foreach (var e in table.Entries)
        {
            if (e.Type == BafType.Table || e.Name == "$ref") continue;
            string v = e.Type switch { BafType.String => $"\"{e.AsString()}\"", BafType.Float => e.AsFloat().ToString("0.###", Inv), BafType.Bool => e.AsBool() ? "True" : "False", _ => e.Raw.ToString(Inv) };
            if (e.Type == BafType.String && e.AsString().Length == 0) continue;
            parts.Add($"{e.Name}={v}");
        }
        return string.Join(" ", parts);
    }

    /// <summary>"light 1: animation, hit at 0.43 s of 0.9 s", the sim's timing for an ability.</summary>
    static string StepOf(MeleeTuning.WeaponMap? map, MeleeTuning? tuning, string ability)
    {
        if (map is null || tuning is null) return "";
        string kind = "";
        MeleeSimStep? s = null;
        int i = Array.FindIndex(map.Abilities, a => a == ability);
        if (i >= 0 && i < tuning.Steps.Count) { s = new MeleeSimStep(tuning.Steps[i], i < tuning.Animations.Count ? tuning.Animations[i] : "?"); kind = $"sim light {i + 1}"; }
        else
        {
            var heavy = map.HeavyAbilities.Concat(map.FinisherAbilities).ToList();
            int h = heavy.FindIndex(a => a == ability);
            if (h >= 0 && h < tuning.Heavy.Count) { s = new MeleeSimStep(tuning.Heavy[h], h < tuning.HeavyAnimations.Count ? tuning.HeavyAnimations[h] : "?"); kind = $"sim heavy {h + 1}"; }
        }
        if (s is null) return "no strike for it in the sim";
        return string.Create(Inv, $"{kind}: clip {s.Anim}, hit event (ACTN_Hit_Targets_Multiplayer) at {s.Step.HitTime:0.###} s of {s.Step.Duration:0.###} s");
    }

    sealed record MeleeSimStep(SmSim.MeleeStep Step, string Anim);

    // ------------------------------------------------------------------ audio resolution

    static EventInfo Resolve(AudioCatalog catalog, string ev)
    {
        uint id = Fnv.Hash32(ev);
        if (!catalog.HasEvent(id)) return new EventInfo(ev, false, 0, 0, 0, 0, true, "", "", 0, 0, 0);
        var wems = catalog.WemsOf(id);
        var armour = Post(catalog, ev, "armour");
        var flesh = Post(catalog, ev, "flesh");
        // Random containers draw differently each post: the sets of sounds a material can play are the union over many posts.
        var armourSet = new HashSet<uint>(armour.Voices.Select(v => v.WemId));
        var fleshSet = new HashSet<uint>(flesh.Voices.Select(v => v.WemId));
        for (int n = 0; n < 40; n++)
        {
            foreach (var v in Post(catalog, ev, "armour").Voices) armourSet.Add(v.WemId);
            foreach (var v in Post(catalog, ev, "flesh").Voices) fleshSet.Add(v.WemId);
        }
        return new EventInfo(ev, true, wems.Count, wems.Count(catalog.HasMedia), armour.Voices.Count, flesh.Voices.Count, armourSet.SetEquals(fleshSet), Delays(armour), Delays(flesh),
            armourSet.Count, fleshSet.Count, armourSet.Intersect(fleshSet).Count());
    }

    static SoundPlay Post(AudioCatalog catalog, string ev, string material)
    {
        AudioRender.TryMaterial(material, out string type, out string state);
        var before = catalog.State(AudioRender.ImpactStateGroup);
        catalog.Post(state, new SoundSwitches());
        try { return catalog.Post(ev, new SoundSwitches().Set("material_type", type), 1); }
        finally { catalog.SetState(AudioRender.ImpactStateGroup, before ?? AudioCatalog.NoneState); }
    }

    static string Delays(SoundPlay p) => p.Voices.Count == 0 ? "" : string.Join(" ", p.Voices.Select(v => v.Delay.ToString("0.###", Inv)).Distinct());

    static string Describe(EventInfo i) => !i.Found ? $"event {i.Name}: NOT IN THE BANKS"
        : $"event {i.Name}: in banks, {i.WemCount} wems reachable ({i.WemsWithMedia} with media); one post: armour {i.ArmourVoices} voice(s) [delay s: {i.ArmourDelays}], flesh {i.FleshVoices} voice(s) [delay s: {i.FleshDelays}]; over 41 posts armour draws from {i.ArmourWems} sounds, flesh from {i.FleshWems}, {i.SharedWems} in common{(i.SameWems ? " (material does not change the sounds)" : "")}";

    static string Leaf(string path) => path.Split('\\', '/')[^1];

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
