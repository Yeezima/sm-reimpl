using System.Globalization;
using SmContent;
using SmFormats.Audio;
using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim.Extras.Executions;
using SmTool.Dump;

namespace SmTool;

/// <summary>
/// smtool exec-cues (executions sound and gore, 2026-11-27; docs/executions-cues.md): for every catalogue row (<see cref="ExecutionRows.All"/>) the original's own cues, read-only
/// from the install. The attacker sequencer's and the victim sequencer's Action tags (name and seconds into the move), and what each tag name sets off in the files that
/// react to it: the <c>triggered_actions</c> entry of that name in every blueprint (<c>ebps.oppc</c>) and wargear (<c>wargear.oppc</c>) file, with its
/// <c>post_sound_event</c> start and stop events, its <c>play_fx</c> effects and its speech commands. Each sound event is checked against the banks the match client loads
/// (<see cref="AudioCatalog.PackagesFor"/>; <c>--all-banks</c>: every package of the install, for the ones the client lacks). Nothing is written.
/// </summary>
static class ExecutionCuesCommand
{
    public static int Run(string[] args)
    {
        try { return RunInner(args, Console.Out); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    sealed record Reaction(string File, string Kind, string What);

    static int RunInner(string[] args, TextWriter o)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Error.WriteLine("usage: smtool exec-cues [--game <dir>] [--row <id>] [--all-banks] [--files <regex>]");
            return 2;
        }
        var inv = CultureInfo.InvariantCulture;
        string gameDir = GameInstall.Locate(Opt(args, "--game"));
        if (args.Contains("--sheet")) return Sheet(gameDir, o, inv);
        string? onlyRow = Opt(args, "--row");
        var fileFilter = Opt(args, "--files") is { } ff ? new System.Text.RegularExpressions.Regex(ff, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;

        // 1. Every row's attacker and victim sequencer tags.
        var rigs = new Dictionary<string, (HavokCharacterFile C, IReadOnlyList<SyncKillInventory.SyncMove> Moves)>(StringComparer.OrdinalIgnoreCase);
        (HavokCharacterFile C, IReadOnlyList<SyncKillInventory.SyncMove> Moves)? Rig(string archive, string entry)
        {
            string key = archive + ":" + entry;
            if (rigs.TryGetValue(key, out var hit)) return hit;
            string path = Path.Combine(gameDir, archive);
            byte[]? bytes = null;
            if (File.Exists(path)) using (var a = ObpkArchive.Open(path)) a.ReadEntries(e => e.FileName == entry, (_, d) => bytes = d.ToArray());
            if (bytes is null) return null;
            var c = new HavokCharacterFile(bytes);
            var graphs = HavokBehaviorDetailReader.Read(c.Pack);
            return rigs[key] = (c, SyncKillInventory.Moves(c.Pack, graphs));
        }

        var rows = ExecutionRows.All.Where(r => onlyRow is null || r.Id.Equals(onlyRow, StringComparison.OrdinalIgnoreCase)).ToList();
        var perRow = new List<(ExecutionRow Row, List<(float S, string Tag)> Attacker, List<(float S, string Tag)> Victim, string Note)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var notes = new List<string>();
            var att = new List<(float, string)>();
            var vic = new List<(float, string)>();
            if (Rig(ExecutionRows.AttackerRig.Archive, ExecutionRows.AttackerRig.Entry) is { } a)
            {
                var m = a.Moves.FirstOrDefault(x => x.Graph == row.Graph && x.Sequencer == row.Sequencer && x.Clips.Any(c => c.Clip.Equals(row.AttackerClip, StringComparison.OrdinalIgnoreCase)))
                        ?? a.Moves.FirstOrDefault(x => x.Graph == row.Graph && x.Sequencer == row.Sequencer);
                if (m is null) notes.Add("attacker sequencer not found");
                else att.AddRange(Tags(m, row.AttackerSeconds));
            }
            else notes.Add("attacker rig not found");
            if (ExecutionRows.VictimRigs.TryGetValue(row.Target, out var vr) && Rig(vr.Archive, vr.Entry) is { } v)
            {
                var m = v.Moves.FirstOrDefault(x => x.Sequencer == row.VictimSequencer && x.Clips.Any(c => c.Clip.Equals(row.VictimClip, StringComparison.OrdinalIgnoreCase)))
                        ?? v.Moves.FirstOrDefault(x => x.Clips.Any(c => c.Clip.Equals(row.VictimClip, StringComparison.OrdinalIgnoreCase)));
                if (m is null) notes.Add("victim sequencer not found");
                else vic.AddRange(Tags(m, row.VictimSeconds));
            }
            else notes.Add("victim rig not found");
            foreach (var (_, t) in att) names.Add(t);
            foreach (var (_, t) in vic) names.Add(t);
            perRow.Add((row, att, vic, string.Join("; ", notes)));
        }

        // 2. What reacts to each tag name: every blueprint and wargear file's triggered_actions entry of that name.
        var reactions = new Dictionary<string, List<Reaction>>(StringComparer.OrdinalIgnoreCase);
        int files = 0;
        foreach (var package in new[] { GameplayTuning.BlueprintPackage, GameplayTuning.WargearPackage })
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, package)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && (fileFilter is null || fileFilter.IsMatch(e.FileName)), (e, d) =>
                {
                    BafDocument doc;
                    try { doc = BafDocument.Read(d); } catch (InvalidDataException) { return; }
                    files++;
                    var keys = SoundTriggers.TriggeredActionKeys(doc.Root).Where(names.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (keys.Count == 0) return;
                    string file = e.FileName;
                    void Add(string tag, string kind, string what)
                    {
                        if (!reactions.TryGetValue(tag, out var list)) reactions[tag] = list = [];
                        if (!list.Any(x => x.File == file && x.Kind == kind && x.What == what)) list.Add(new Reaction(file, kind, what));
                    }
                    foreach (var b in SoundBinding.Collect(doc.Root))
                        if (b.UnderTriggeredActions && keys.Contains(b.Trigger))
                            Add(b.Trigger, "sound", $"{b.StartEvent}{(b.StopEvent.Length > 0 ? " / stop " + b.StopEvent : "")}{(b.DelayMilliseconds > 0 ? $" +{b.DelayMilliseconds:0} ms" : "")}{(b.IsDefault ? "" : " [upgrade]")}");
                    foreach (var b in SpeechBinding.Collect(doc.Root))
                        if (b.UnderTriggeredActions && keys.Contains(b.Trigger))
                            Add(b.Trigger, "speech", $"{b.Kind} {b.Character} {b.Type}{b.State}");
                    foreach (var b in FxBinding.Collect(doc.Root))
                    {
                        string? tag = TriggerOf(b.Path, keys);
                        if (tag is null) continue;
                        if (b.Play is { } p) Add(tag, "fx", $"{p.Name} at {(p.Marker.Length > 0 ? p.Marker : p.TriggerPoint)}{(p.Attached ? " attached" : "")}{(p.Lifetime > 0 ? $" life {p.Lifetime.ToString("0.##", inv)}" : "")}");
                        else if (b.Material is { } m) Add(tag, "material", m.ContainerName);
                    }
                    foreach (var (k, refs) in CommandKinds(doc.Root, keys))
                        if (!(reactions.GetValueOrDefault(k)?.Any(x => x.File == file) ?? false)) Add(k, "other", refs.Count == 0 ? "(no command)" : string.Join(", ", refs));
                });

        // 3. Which banks have the events.
        using var catalog = AudioCatalog.Open(gameDir, null);
        WwiseLibrary? all = null;
        if (args.Contains("--all-banks"))
        {
            string dir = Path.Combine(gameDir, "streamingaudio");
            all = WwiseLibrary.Open(dir, Directory.EnumerateFiles(dir, "*.pck").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal), out _);
        }
        string Bank(string evt)
        {
            var play = catalog.Post(evt, new SoundSwitches());
            if (play.Found) return $"client banks: {play.Voices.Count} voice(s)";
            if (all is null) return "NOT in the client's banks";
            uint id = Fnv.Hash32(evt);
            return all.Find(id) is not null ? $"NOT in the client's banks; in {all.BankOf(id)}" : "in no bank of the install";
        }

        o.WriteLine($"#### exec-cues: {perRow.Count} rows, {names.Count} tag names, {files} blueprint/wargear files read, {reactions.Count} tag names with a triggered action");
        foreach (var (row, att, vic, note) in perRow)
        {
            o.WriteLine();
            o.WriteLine(string.Create(inv, $"## {row.Id} ({row.Target}, {row.Weapon}) attacker {row.AttackerClip} {row.AttackerSeconds:0.###} s, victim {row.VictimClip} {row.VictimSeconds:0.###} s, kill {row.KillSeconds:0.###} s{(note.Length > 0 ? " NOTE " + note : "")}"));
            foreach (var (side, list) in new[] { ("attacker", att), ("victim", vic) })
                foreach (var (s, tag) in list.OrderBy(x => x.S))
                {
                    o.WriteLine(string.Create(inv, $"  {side,-8} {s,6:0.000} s {tag}"));
                    var sideFiles = side == "attacker" ? (Opt(args, "--weapon-file") is { } wf ? ["vanguard", wf] : AttackerFiles(row)) : VictimFiles(row);
                    var every = reactions.GetValueOrDefault(tag) ?? [];
                    var mine = every.Where(r => sideFiles.Contains(r.File, StringComparer.OrdinalIgnoreCase)).ToList();
                    foreach (var r in mine)
                    {
                        string extra = r.Kind == "sound" ? "   [" + Bank(r.What.Split(' ')[0]) + "]" : "";
                        o.WriteLine($"             {r.Kind,-8} {r.File}: {r.What}{extra}");
                    }
                    if (mine.Count == 0)
                    {
                        var others = every.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        o.WriteLine($"             (nothing in {string.Join(", ", sideFiles)}; {others.Count} other file(s){(others.Count > 0 ? ": " + string.Join(", ", others.Take(6)) : "")})");
                        foreach (var r in every.Where(r => r.File == others.FirstOrDefault()))
                            o.WriteLine($"               e.g. {r.Kind} {r.File}: {r.What}{(r.Kind == "sound" ? "   [" + Bank(r.What.Split(' ')[0]) + "]" : "")}");
                    }
                }
        }
        all?.Dispose();
        return 0;
    }

    /// <summary>
    /// <c>--sheet</c>: the run-time cue sheet as the client builds it (<see cref="SmContent.Extras.Executions.ExecutionCueLoader.Load"/> over the install's catalogue), as Markdown for
    /// docs/executions-cues.md: per row and wield every cue (time on the shared clock, side, kind, place, tag, event or effect, file) and whether the client's banks have the event.
    /// </summary>
    static int Sheet(string gameDir, TextWriter o, CultureInfo inv)
    {
        var notes = new List<string>();
        var catalogue = SmContent.Extras.Executions.ExecutionCatalogueLoader.Load(gameDir, notes);
        var sheet = SmContent.Extras.Executions.ExecutionCueLoader.Load(gameDir, catalogue.Rows, notes);
        using var audio = AudioCatalog.Open(gameDir, null);
        int original = 0, ours = 0, missing = 0;
        foreach (var (rowId, wield, cues) in sheet.All)
        {
            var row = catalogue.Find(rowId);
            o.WriteLine();
            o.WriteLine(string.Create(inv, $"#### {rowId}{(wield == SmContent.Extras.Executions.ExecutionWield.Own ? "" : " with the " + (wield == SmContent.Extras.Executions.ExecutionWield.PowerSword ? "power sword" : "combat knife"))} (kill {row?.KillSeconds:0.000} s, end {row?.Seconds:0.000} s)"));
            o.WriteLine();
            o.WriteLine("| s | side | kind | place | tag | event / effect | file | |");
            o.WriteLine("|---|---|---|---|---|---|---|---|");
            foreach (var c in cues)
            {
                string bank = c.Kind is SmContent.Extras.Executions.ExecutionCueKind.Sound or SmContent.Extras.Executions.ExecutionCueKind.Stop
                    ? audio.Post(c.Event, new SoundSwitches()).Found ? "" : "not in the client's banks" : "";
                if (bank.Length > 0) missing++;
                if (c.Ours) ours++; else original++;
                string what = c.Kind switch
                {
                    SmContent.Extras.Executions.ExecutionCueKind.Speech => $"speech {c.Speech!.Character} {c.Event}",
                    SmContent.Extras.Executions.ExecutionCueKind.Effect => $"{c.Event} at {c.Fx!.Marker}{(c.Blood ? " (blood)" : "")}",
                    SmContent.Extras.Executions.ExecutionCueKind.Motor => $"{c.Event} -> the wielder's {c.Motor.ToString().ToLowerInvariant()}",
                    _ => c.Event,
                };
                string span = c.Until > c.At + 0.01f ? $"{c.At:0.000}-{c.Until:0.000}" : $"{c.At:0.000}";
                o.WriteLine(string.Create(inv, $"| {span} | {c.Side.ToString().ToLowerInvariant()} | {c.Kind.ToString().ToLowerInvariant()}{(c.Ours ? " (ours)" : "")} | {c.Place.ToString().ToLowerInvariant()} | `{c.Tag}` | `{what}` | {c.File} | {bank} |"));
            }
        }
        o.WriteLine();
        o.WriteLine($"Counts: {sheet.All.Count()} row-wield lists, {sheet.Count} cues: {original} original bindings, {ours} ours; {missing} sound posts not in the client's banks.");
        foreach (var n in notes.Where(n => n.StartsWith("execution cues", StringComparison.Ordinal))) o.WriteLine("- " + n);
        return 0;
    }

    /// <summary>
    /// The files whose triggered actions the attacker's tags set off in the original (interpretation: the entity's blueprint and its equipped weapon react): the campaign player
    /// blueprint <c>vanguard</c> and the campaign weapon of the row's graph (inventory 1.3: <c>chainsword_new</c>, <c>poweraxe</c>, <c>thunderhammer_new</c>, <c>combat_knife</c>).
    /// </summary>
    public static IReadOnlyList<string> AttackerFiles(ExecutionRow row) => ["vanguard", row.Graph switch
    {
        "SM_MEL_ChainSword" => "chainsword_new", "SM_MEL_PowerAxe" => "poweraxe", "SM_MEL_ThunderHammer" => "thunderhammer_new", _ => "combat_knife",
    }];

    /// <summary>The victim's campaign blueprints (the rig's own entity) and co-op twins.</summary>
    public static IReadOnlyList<string> VictimFiles(ExecutionRow row) => row.Target switch
    {
        ExecutionTargets.OrkBoy => ["boy", "boy_pro", "ork_coop_boy"],
        ExecutionTargets.Gretchin => ["gretchin", "ork_coop_gretchin"],
        ExecutionTargets.ChaosMarine => ["chaos_marine_melee", "chaos_space_marine", "chaos_coop_marine_melee"],
        ExecutionTargets.Bloodletter => ["chaos_bloodletter", "chaos_coop_bloodletter"],
        ExecutionTargets.Renegade => ["chaos_renegade_ig", "chaos_coop_renegade_ig", "ig_coop_guardsman"],
        _ => [],
    };

    /// <summary>For each wanted triggered-action key of a file, the <c>$ref</c> kinds of every command under it (any depth).</summary>
    static List<(string Key, List<string> Refs)> CommandKinds(BafTable root, IReadOnlySet<string> keys)
    {
        var found = new List<(string, List<string>)>();
        void Walk(BafTable t, int depth)
        {
            if (depth > 48) return;
            foreach (var e in t.Entries)
            {
                if (e.Type != BafType.Table) continue;
                if (e.Name == "triggered_actions")
                    foreach (var k in e.AsTable().Entries.Where(x => x.Type == BafType.Table && keys.Contains(x.Name)))
                    {
                        var refs = new List<string>();
                        Refs(k.AsTable(), refs, 0);
                        found.Add((k.Name, refs.Distinct().ToList()));
                    }
                else Walk(e.AsTable(), depth + 1);
            }
        }
        static void Refs(BafTable t, List<string> refs, int depth)
        {
            if (depth > 48) return;
            foreach (var e in t.Entries)
            {
                if (e.Type != BafType.Table) continue;
                var c = e.AsTable();
                if (c.String("$ref") is { Length: > 0 } r && r.Contains("action_commands", StringComparison.OrdinalIgnoreCase)) refs.Add(r[(r.LastIndexOf('\\') + 1)..]);
                Refs(c, refs, depth + 1);
            }
        }
        Walk(root, 0);
        return found;
    }

    /// <summary>The Action tags of a move (names only: the cues; the reward, branch and alignment tags stay), at seconds into the clip (<paramref name="seconds"/> long).</summary>
    static IEnumerable<(float, string)> Tags(SyncKillInventory.SyncMove m, float seconds) =>
        m.Tags.Where(t => t.Kind == "Action" && !string.IsNullOrEmpty(t.Name)).Select(t => (t.Start * seconds, t.Name!));

    static string? TriggerOf(string path, IReadOnlySet<string> keys)
    {
        var parts = path.Split('/');
        int at = Array.IndexOf(parts, "triggered_actions");
        return at >= 0 && at + 1 < parts.Length && keys.Contains(parts[at + 1]) ? parts[at + 1] : null;
    }

    static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
