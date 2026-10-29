using System.Globalization;
using System.Text;
using SmContent;
using SmFormats;
using SmFormats.Audio;
using SmFormats.Baf;
using SmFormats.Obpk;

/// <summary>
/// smtool audio commands: read-only inspection of the install's Wwise packages and decoding into the private cache.
/// Decoded audio is original content: it is written only under %LOCALAPPDATA%\sm-reimpl\cache\audio (or another
/// folder outside both the install and the repository).
/// </summary>
static class AudioCommands
{
    /// <summary>
    /// Packages the multiplayer alpha needs (docs/discovery.md, "Audio (Wwise)"): global banks, player, abilities,
    /// shared SFX, UI, Manufactorum's region bank, plus every weapon_*.pck.
    /// </summary>
    static readonly string[] MultiplayerPackages =
    [
        "init", "in_game_default", "player1_vanguard", "player1_chaos", "abilities", "jump_pack", "explosions", "foley",
        "destruction", "world_objects", "ambiences", "ambiences_ram", "ui_in_game", "fe_ui", "mp_tdm_09",
    ];

    /// <summary>Archives whose BAF strings name sound events (post_sound_event and friends).</summary>
    static readonly string[] NameSources =
    [
        "data/packages/wargear.oppc", "data/packages/ebps.oppc", "data/packages/material_effect_container.oppc",
        "data/packages/global.oppc",
    ];


    public static int Run(string command, string[] rest)
    {
        try
        {
            return command switch
            {
                "audio-list" when rest.Length >= 1 => List(rest[0], rest[1..]),
                "audio-event" when rest.Length >= 1 => Event(rest[0], rest[1..]),
                "audio-decode" when rest.Length >= 1 => Decode(rest[0], rest[1..]),
                "audio-verify" when rest.Length >= 1 => VerifyAll(rest[0]),
                "audio-coverage" => Coverage(rest),
                "audio-node" when rest.Length >= 1 => Node(rest[0], rest[1..]),
                "audio-mix" when rest.Length >= 1 => Mix(rest[0], rest[1..]),
                "audio-surfaces" => SurfaceReport(rest),
                "audio-rtpcs" => RtpcReport(rest),
                "audio-tags" => TagReport(rest),
                "audio-levels" => LevelReport(rest),
                "audio-objects" when rest.Length >= 1 => ObjectList(rest[0], rest[1..]),
                _ => -1,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException
            or OverflowException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // ---------------------------------------------------------------- audio-list

    static int List(string pckPath, string[] rest)
    {
        bool all = rest.Contains("--all");
        string? gameDir = TryGameDir(Opt(rest, "--game") ?? InstallPaths.FindInstallRoot(pckPath));
        var names = LoadNames(gameDir, Opt(rest, "--names"));
        using var pck = AkpkPackage.Open(pckPath);
        Console.WriteLine($"package {Path.GetFileName(pckPath)}: AKPK version {pck.Version}, languages: " +
            string.Join(", ", pck.Languages.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} ({kv.Key})")));
        Console.WriteLine($"banks: {pck.Banks.Count}");
        var events = new List<(HircEvent Ev, SoundBank Bank)>();
        foreach (var e in pck.Banks)
        {
            var bank = pck.ReadBank(e, loadMedia: false);
            names.Add(bank.Name ?? "");
            var kinds = bank.Objects.GroupBy(o => o.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}");
            int plays = 0, playsHere = 0;
            foreach (var a in bank.Objects.OfType<HircAction>().Where(a => a.Kind == HircActionKind.Play))
            {
                plays++;
                if (a.PlayBankId == bank.BankId) playsHere++;
            }
            Console.WriteLine($"  bank 0x{bank.BankId:x8} \"{bank.Name}\" v{bank.Version} {pck.LanguageName(e.LanguageId)} " +
                $"{e.Size:N0} B, {bank.Media.Count} embedded wems, {bank.Objects.Count} objects ({string.Join(" ", kinds)})" +
                (bank.ParseFailures > 0 ? $", {bank.ParseFailures} PARSE FAILURES" : "") +
                (plays > 0 ? $"; Play actions naming this bank: {playsHere}/{plays}" : ""));
            if (Fnv.Hash32(Path.GetFileNameWithoutExtension(pckPath)) == bank.BankId)
                Console.WriteLine("    bank ID = FNV-1 of the package name");
            events.AddRange(bank.Events.Select(ev => (ev, bank)));
        }
        long streamBytes = pck.Streams.Sum(s => s.Size);
        Console.WriteLine($"streamed wems: {pck.Streams.Count} ({streamBytes / 1048576.0:0.0} MB)");
        int named = events.Count(x => names.TryGet(x.Ev.Id, out _));
        Console.WriteLine($"events: {events.Count} ({named} named; names from {names.Count} known strings)");
        foreach (var (ev, bank) in events.OrderBy(x => names.TryGet(x.Ev.Id, out var n) ? n : "~" + x.Ev.Id.ToString("x8"), StringComparer.Ordinal))
        {
            bool known = names.TryGet(ev.Id, out var name);
            if (!known && !all) continue;
            var kinds = ev.ActionIds.Select(id => bank.Find(id) as HircAction).Where(a => a is not null)
                .GroupBy(a => a!.Kind).Select(g => g.Count() > 1 ? $"{g.Key}x{g.Count()}" : g.Key.ToString());
            Console.WriteLine($"  {(known ? name : "?"),-48} 0x{ev.Id:x8}  {string.Join(" ", kinds)}");
        }
        if (!all && named < events.Count) Console.WriteLine($"  ({events.Count - named} unnamed events; --all lists them)");
        return 0;
    }

    // ---------------------------------------------------------------- audio-event

    static int Event(string eventName, string[] rest)
    {
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        var names = LoadNames(gameDir, Opt(rest, "--names"));
        using var lib = OpenLibrary(gameDir, Opt(rest, "--packages"), names);
        uint id = ParseEventId(eventName, names);
        var chain = lib.ResolveEvent(id);
        if (chain.Event is null)
        {
            Console.Error.WriteLine($"Event {eventName} (0x{id:x8}) is not in the loaded banks.");
            return 1;
        }
        PrintChain(chain, lib, names, Console.Out);
        return 0;
    }

    static void PrintChain(EventChain chain, WwiseLibrary lib, WwiseNames names, TextWriter w)
    {
        w.WriteLine($"event {names.Name(chain.EventId)} (0x{chain.EventId:x8}) in bank {chain.BankName}, {chain.Actions.Count} actions");
        int i = 0;
        var printed = new HashSet<ChainNode>(ReferenceEqualityComparer.Instance);   // shared subtrees print once
        foreach (var a in chain.Actions)
        {
            i++;
            var act = a.Action;
            string delay = act.DelayMin != 0 || act.DelayMax != 0 ? $"{act.Delay} [{act.DelayMin}..{act.DelayMax}]" : act.Delay.ToString(CultureInfo.InvariantCulture);
            w.WriteLine($"  action {i}: {act.Kind} (0x{act.ActionType:x}) target {names.Name(act.TargetId)} delay {delay} ms");
            if (a.Target is { } t) PrintNode(t, lib, names, w, "    ", printed);
        }
        var wems = chain.WemIds();
        w.WriteLine($"wems: {wems.Count} unique");
        foreach (var wem in wems)
        {
            var loc = lib.FindMedia(wem);
            w.WriteLine(loc is null ? $"  {wem}: NOT FOUND in the loaded packages"
                : $"  {wem}: {(loc.Streamed ? "streamed" : "embedded")} in {loc.PackageName}{(loc.BankName is null ? "" : $" ({loc.BankName})")}, {loc.Size:N0} B");
        }
    }

    static void PrintNode(ChainNode n, WwiseLibrary lib, WwiseNames names, TextWriter w, string pad, HashSet<ChainNode> printed)
    {
        string where = n.BankName is null ? "" : $" [{n.BankName}]";
        if (n.Branches.Count > 0 && !printed.Add(n))
        {
            w.WriteLine($"{pad}{n.Object?.Type} {n.Id:x8}{where} (same subtree as above)");
            return;
        }
        switch (n.Object)
        {
            case null:
                w.WriteLine($"{pad}{names.Name(n.Id)}: not in the loaded banks");
                return;
            case HircSound s:
                var loc = lib.FindMedia(s.SourceId);
                w.WriteLine($"{pad}Sound {n.Id:x8}{where} wem {s.SourceId} ({s.StreamType}, {(loc is null ? "media not found" : $"{loc.Size:N0} B")})" +
                    NodeParams(s.Base) + (s.LoopCount != 1 ? $" loop {s.LoopCount}" : ""));
                return;
            case HircRandomSequence r:
                w.WriteLine($"{pad}{(r.IsRandom ? "Random" : "Sequence")} {n.Id:x8}{where} avoid-repeat {r.AvoidRepeatCount}, {r.Children.Count} children" + NodeParams(r.Base));
                break;
            case HircSwitch sw:
                w.WriteLine($"{pad}Switch {n.Id:x8}{where} {(sw.GroupType == 0 ? "switch" : "state")} group {names.Name(sw.GroupId)}, default {names.Name(sw.DefaultSwitch)}" + NodeParams(sw.Base));
                foreach (var b in n.Branches)
                {
                    w.WriteLine($"{pad}  case {names.Name(b.SwitchValue ?? 0)}{(b.IsDefault ? " (default)" : "")}:{(b.Nodes.Count == 0 ? " nothing" : "")}");
                    foreach (var c in b.Nodes) PrintNode(c, lib, names, w, pad + "    ", printed);
                }
                return;
            case HircLayer l:
                w.WriteLine($"{pad}Layer {n.Id:x8}{where} {l.Children.Count} children" + NodeParams(l.Base) + LayerInfo(l));
                break;
            case HircNode other:
                w.WriteLine($"{pad}{other.Type} {n.Id:x8}{where}" + NodeParams(other.Base));
                break;
            default:
                w.WriteLine($"{pad}{n.Object.Type} {n.Id:x8}{where} (not followed)");
                return;
        }
        if (n.Truncated) w.WriteLine($"{pad}  (cut: cycle, depth limit or node budget)");
        foreach (var b in n.Branches) foreach (var c in b.Nodes) PrintNode(c, lib, names, w, pad + "  ", printed);
    }

    static string NodeParams(HircNodeBase b)
    {
        var parts = new List<string>();
        if (b.Volume.Base != 0 || b.Volume.HasRange) parts.Add($"vol {b.Volume} dB");
        if (b.Pitch.Base != 0 || b.Pitch.HasRange) parts.Add($"pitch {b.Pitch} cents");
        if (b.Lpf.Base != 0 || b.Lpf.HasRange) parts.Add($"lpf {b.Lpf}");
        if (b.AttenuationId != 0) parts.Add($"attenuation {b.AttenuationId:x8}");
        if (b.MaxInstances != 0) parts.Add($"max {b.MaxInstances}{(b.KillNewest ? " kill-newest" : "")}{(b.GlobalLimit ? " global" : "")}{(b.IgnoreParentMaxInstances ? " ignore-parent" : "")}");
        if (b.BelowThreshold != 0 || b.VirtualBehavior != 0) parts.Add($"below-threshold {b.BelowThreshold} virtual {b.VirtualBehavior}");
        if (b.TrailingA != 0 || b.TrailingB != 0) parts.Add($"trailing {b.TrailingA},{b.TrailingB}");
        foreach (var r in b.Rtpcs)
            parts.Add($"rtpc {(r.RtpcId == VoiceMixer.Speed ? "speed" : r.RtpcId == VoiceMixer.Distance ? "distance" : r.RtpcId.ToString("x8"))}->p{r.ParamId}" +
                      $"[s{r.Scaling} {string.Join(" ", r.Points.Select(p => $"{p.X:0.##}:{p.Y:0.##}/i{p.Interpolation}"))}]");
        return parts.Count == 0 ? "" : " | " + string.Join(", ", parts);
    }

    static string LayerInfo(HircLayer l)
    {
        var parts = new List<string>();
        foreach (var info in l.Layers)
        {
            string rtpc = info.RtpcId == 0 ? "no rtpc" : info.RtpcId == VoiceMixer.Speed ? "rtpc speed" : info.RtpcId == VoiceMixer.Distance ? "rtpc distance" : $"rtpc {info.RtpcId:x8}";
            var kids = info.AssociatedChildIds.Select((id, i) => $"{id:x8}" + (i < info.ChildCurves.Count && info.ChildCurves[i].Count > 0
                ? $"[{string.Join(" ", info.ChildCurves[i].Select(p => $"{p.X:0.##}:{p.Y:0.##}"))}]" : "")).ToList();
            parts.Add($"{rtpc} xfade {info.CrossfadeDefault:0.##} " + string.Join(" ", kids) + (info.Rtpcs.Count > 0 ? $" own-rtpcs {info.Rtpcs.Count}" : ""));
        }
        return parts.Count == 0 ? "" : " {" + string.Join("; ", parts) + "}";
    }

    // ---------------------------------------------------------------- audio-node

    /// <summary>Prints one Hierarchy object by ID (hex): its kind, parent chain and what it contains; <c>--events</c> lists the events that reach it (slow).</summary>
    static int Node(string idText, string[] rest)
    {
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        var names = LoadNames(gameDir, Opt(rest, "--names"));
        using var lib = OpenLibrary(gameDir, Opt(rest, "--packages"), names);
        uint id = ParseEventId(idText, names);
        var obj = lib.Find(id);
        if (obj is null) { Console.Error.WriteLine($"0x{id:x8} is not in the loaded banks."); return 1; }
        Console.WriteLine($"{obj.Type} 0x{id:x8} in {lib.BankOf(id)}");
        if (rest.Contains("--hex")) Console.WriteLine("  payload " + Convert.ToHexString(obj.Payload.Span));
        if (obj is HircNode node)
        {
            Console.WriteLine("  own: " + NodeParams(node.Base).TrimStart(' ', '|'));
            uint parent = node.Base.ParentId;
            for (int i = 0; parent != 0 && i < 16; i++)
            {
                var p = lib.Find(parent);
                Console.WriteLine($"  parent {i}: {p?.Type.ToString() ?? "(not loaded)"} 0x{parent:x8}" + (p is HircNode pn ? " |" + NodeParams(pn.Base).TrimStart(' ', '|') : ""));
                parent = (p as HircNode)?.Base.ParentId ?? 0;
            }
            Console.WriteLine($"  children: {node.Children.Count}");
        }
        if (rest.Contains("--events"))
        {
            foreach (var ev in lib.Events)
            {
                var chain = lib.ResolveEvent(ev.Id);
                bool hit = chain.Actions.Any(a => a.Action.TargetId == id || (a.Target is { } t && Contains(t, id)));
                if (hit) Console.WriteLine($"  event {names.Name(ev.Id)}");
            }
        }
        return 0;

        static bool Contains(ChainNode n, uint target)
        {
            if (n.Id == target) return true;
            foreach (var b in n.Branches) foreach (var c in b.Nodes) if (Contains(c, target)) return true;
            return false;
        }
    }

    // ---------------------------------------------------------------- audio-surfaces

    /// <summary>
    /// Which footstep surface switch a player would get across a map: samples walkable floor (a grid of <c>--step</c> metres; floor =
    /// a character-mesh triangle facing up with room for a standing capsule above it) and reads the surface the client's
    /// <c>SurfaceUnder</c> reads (the weapon layer's material under the feet, a ray 0.5 m up, 2.5 m down) and, for comparison,
    /// the weapon layer's material straight under the character-mesh hit point.
    /// </summary>
    static int SurfaceReport(string[] rest)
    {
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        string map = Opt(rest, "--map") ?? "mp_tdm_09";
        int every = int.Parse(Opt(rest, "--every") ?? "8", CultureInfo.InvariantCulture);
        var tuning = GameplayTuning.Load(gameDir);
        var content = MapContent.Load(gameDir, map);
        var world = content.ToCollisionWorld();
        var wp = world.Weapons;
        var graph = BotNavigation.LoadOrBuild(content, world, tuning.Config, SmSim.Team.SpaceMarines, -1, BotNavigation.CacheDirectory, out _);
        Console.WriteLine($"{map}: {graph.NodeCount:N0} reachable cells (every {every}th sampled); weapon mesh {wp.TriangleCount} triangles, materials {wp.HasMaterials}");
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var ids = new SortedDictionary<int, int>();
        var down = new System.Numerics.Vector3(0, 0, -1);
        int floors = 0;
        for (int n = 0; n < graph.NodeCount; n += every)
        {
            var feet = graph.Position(n);
            floors++;
            // What SurfaceUnder reads: a ray from 0.5 m above the feet, 2.5 m down, on the weapon layer.
            string name; int id = -2;
            if (wp.Raycast(feet + new System.Numerics.Vector3(0, 0, 0.5f), down, 2.5f) is { } w) { id = wp.MaterialOf(w.Triangle); name = SmContent.Surfaces.NameOf(id); }
            else name = "(no weapon-layer hit: reads default)";
            counts[name] = counts.GetValueOrDefault(name) + 1;
            ids[id] = ids.GetValueOrDefault(id) + 1;
        }
        Console.WriteLine($"samples: {floors}; weapon-layer surface under the feet (the client's SurfaceUnder):");
        foreach (var (name, c) in counts.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {name,-40} {c,7} {100.0 * c / Math.Max(1, floors),5:0.0} %");
        Console.WriteLine("material ids: " + string.Join(", ", ids.Select(kv => $"{kv.Key}:{kv.Value}")));
        return 0;
    }

    // ---------------------------------------------------------------- audio-objects

    /// <summary>Lists the Hierarchy objects of one type (name or number, e.g. Bus, FxShareSet, FxCustom) of the loaded banks with their payload bytes in hex (a test aid for layouts that are kept raw).</summary>
    static int ObjectList(string typeText, string[] rest)
    {
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        var names = LoadNames(gameDir, Opt(rest, "--names"));
        using var lib = OpenLibrary(gameDir, Opt(rest, "--packages"), names);
        var type = Enum.TryParse<HircType>(typeText, ignoreCase: true, out var t) ? t : (HircType)byte.Parse(typeText, CultureInfo.InvariantCulture);
        int limit = int.Parse(Opt(rest, "--limit") ?? "40", CultureInfo.InvariantCulture), n = 0;
        var seen = new HashSet<uint>();
        foreach (var b in lib.Banks)
            foreach (var o in b.Bank.Objects.Where(o => o.Type == type))
            {
                if (!seen.Add(o.Id)) continue;
                if (++n <= limit) Console.WriteLine($"  {o.Id:x8} [{b.PackageName}] {o.Payload.Length} B  {Convert.ToHexString(o.Payload.Span)[..Math.Min(o.Payload.Length * 2, 240)]}");
            }
        Console.WriteLine($"{n} objects of type {type}");
        return 0;
    }

    // ---------------------------------------------------------------- audio-levels

    /// <summary>
    /// The loudest voice of every sound event the multiplayer triggers name, as the local player would hear it right next to it (the local variant,
    /// <c>--speed</c> m/s, surface stone, <c>--posts</c> posts each, the mean of the per-post maxima): a ranking of how loud the footsteps are
    /// against everything else (volume dB incl. the master bus, before the sample's own level, which the banks normalise to about 0 dBFS).
    /// </summary>
    static int LevelReport(string[] rest)
    {
        if (!TryCap(rest, out var cap)) return 2;
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        float speed = float.Parse(Opt(rest, "--speed") ?? "6.5", CultureInfo.InvariantCulture);
        int posts = int.Parse(Opt(rest, "--posts") ?? "8", CultureInfo.InvariantCulture);
        var tuning = GameplayTuning.Load(gameDir);
        var model = CharacterModel.LoadMarine(gameDir);
        var triggers = LoadTriggers(gameDir, tuning, model);
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: RepoRoot(), log: Console.Error);
        var parameters = new Dictionary<uint, float> { [VoiceMixer.Speed] = speed };
        Console.WriteLine($"speed {speed:0.##} m/s, surface stone, local variant, rtpc volume cap {cap}; footsteps without the owner's {FootstepLevel.OwnerGainDb:0.#} dB");
        var rows = new List<(string Event, double Max, int Voices, double Sum)>();
        foreach (var name in triggers.EventNames())
        {
            if (!catalog.HasEvent(name)) continue;
            double maxSum = 0, powerSum = 0; int voices = 0;
            for (int i = 0; i < posts; i++)
            {
                var sw = new SoundSwitches().Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue).Set("material_type", "stone");
                var play = catalog.Post(name, sw, 1);
                double best = -200, power = 0; voices = play.Voices.Count;
                foreach (var v in play.Voices)
                {
                    var mix = VoiceMixer.Evaluate(v, null, 1, 0, v.Is3D ? 3f : null, parameters, cap);
                    best = Math.Max(best, mix.VolumeDb);
                    power += Math.Pow(10, mix.VolumeDb / 10.0);
                }
                maxSum += best; powerSum += power;
            }
            if (maxSum / posts > -150) rows.Add((name, maxSum / posts, voices, 10 * Math.Log10(Math.Max(powerSum / posts, 1e-20))));
        }
        foreach (var r in rows.OrderByDescending(r => r.Max))
            Console.WriteLine($"  {r.Max,6:0.0} dB loudest voice, {r.Sum,6:0.0} dB summed power, {r.Voices} voices  {r.Event}");
        return 0;
    }

    // ---------------------------------------------------------------- audio-tags

    /// <summary>
    /// The rig's behaviour Action tags per clip whose tag name matches <c>--tag</c> (a regex, default foot/land/step): which clips carry footstep tags
    /// and at which fraction of the clip, and the clip names (<c>--clip</c> regex filter).
    /// </summary>
    static int TagReport(string[] rest)
    {
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        var tagFilter = new System.Text.RegularExpressions.Regex(Opt(rest, "--tag") ?? "foot|step|land", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var clipFilter = Opt(rest, "--clip") is { } cf ? new System.Text.RegularExpressions.Regex(cf, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
        var tuning = GameplayTuning.Load(gameDir);
        var model = CharacterModel.LoadMarine(gameDir);
        var triggers = LoadTriggers(gameDir, tuning, model);
        int shown = 0;
        foreach (var (clip, tags) in triggers.ClipTags.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (clipFilter is not null && !clipFilter.IsMatch(clip)) continue;
            var hit = tags.Where(t => tagFilter.IsMatch(t.Tag)).ToList();
            if (hit.Count == 0) continue;
            shown++;
            Console.WriteLine($"  {clip,-52} {string.Join("  ", hit.Select(t => $"{t.Tag}@{t.At:0.###}"))}");
        }
        Console.WriteLine($"{shown} clips of {triggers.ClipTags.Count} with a matching tag");
        return 0;
    }

    // ---------------------------------------------------------------- audio-rtpcs

    /// <summary>Every RTPC binding in the multiplayer banks, grouped by (game parameter, property): how many nodes, the range of the curves' y values.</summary>
    static int RtpcReport(string[] rest)
    {
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        var names = LoadNames(gameDir, Opt(rest, "--names"));
        using var lib = OpenLibrary(gameDir, Opt(rest, "--packages"), names);
        var groups = new SortedDictionary<(uint Rtpc, uint Param), (int Nodes, float MinY, float MaxY, float MaxX)>();
        foreach (var o in lib.Banks.SelectMany(b => b.Bank.Objects).OfType<HircNode>().DistinctBy(n => n.Id))
            foreach (var r in o.Base.Rtpcs.Where(r => r.Points.Count > 0))
            {
                var g = groups.TryGetValue((r.RtpcId, r.ParamId), out var e) ? e : (Nodes: 0, MinY: float.MaxValue, MaxY: float.MinValue, MaxX: float.MinValue);
                groups[(r.RtpcId, r.ParamId)] = (g.Nodes + 1, Math.Min(g.MinY, r.Points.Min(p => p.Y)), Math.Max(g.MaxY, r.Points.Max(p => p.Y)), Math.Max(g.MaxX, r.Points.Max(p => p.X)));
            }
        if (rest.Contains("--limits"))
        {
            // The advanced settings of every node: how many set a limit, which kind, and the below-threshold byte.
            var nodes = lib.Banks.SelectMany(b => b.Bank.Objects).OfType<HircNode>().DistinctBy(n => n.Id).ToList();
            Console.WriteLine($"nodes {nodes.Count}: max instances set {nodes.Count(n => n.Base.MaxInstances > 0)} (kill-newest {nodes.Count(n => n.Base.MaxInstances > 0 && n.Base.KillNewest)}, " +
                              $"global {nodes.Count(n => n.Base.MaxInstances > 0 && n.Base.GlobalLimit)}, ignore-parent {nodes.Count(n => n.Base.MaxInstances > 0 && n.Base.IgnoreParentMaxInstances)}); " +
                              $"max values {string.Join(" ", nodes.Where(n => n.Base.MaxInstances > 0).GroupBy(n => (int)n.Base.MaxInstances).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}; " +
                              $"below-threshold byte {string.Join(" ", nodes.GroupBy(n => (int)n.Base.BelowThreshold).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}; " +
                              $"virtual-behaviour byte {string.Join(" ", nodes.GroupBy(n => (int)n.Base.VirtualBehavior).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
        }
        if (rest.Contains("--limits"))
        {
            // The two bytes that close the advanced settings against "has a limit", the below-threshold byte, the ignore-parent byte and whether the node is a root.
            var nodes = lib.Banks.SelectMany(b => b.Bank.Objects).OfType<HircNode>().DistinctBy(n => n.Id).ToList();
            string Cross(Func<HircNode, string> key) => string.Join("  ", nodes.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"[{g.Key}] {g.Count()}"));
            Console.WriteLine("trailing bytes (A,B) x has-limit:        " + Cross(n => $"{n.Base.TrailingA},{n.Base.TrailingB} limit={(n.Base.MaxInstances > 0 ? 1 : 0)}"));
            Console.WriteLine("trailing bytes (A,B) x below-threshold:  " + Cross(n => $"{n.Base.TrailingA},{n.Base.TrailingB} below={n.Base.BelowThreshold}"));
            Console.WriteLine("trailing bytes (A,B) x ignore-parent:    " + Cross(n => $"{n.Base.TrailingA},{n.Base.TrailingB} ignore={(n.Base.IgnoreParentMaxInstances ? 1 : 0)}"));
            Console.WriteLine("trailing bytes (A,B) x root node:        " + Cross(n => $"{n.Base.TrailingA},{n.Base.TrailingB} root={(n.Base.ParentId == 0 ? 1 : 0)}"));
            Console.WriteLine("ignore-parent x has-limit:               " + Cross(n => $"ignore={(n.Base.IgnoreParentMaxInstances ? 1 : 0)} limit={(n.Base.MaxInstances > 0 ? 1 : 0)}"));
            Console.WriteLine("below-threshold x root node:             " + Cross(n => $"below={n.Base.BelowThreshold} root={(n.Base.ParentId == 0 ? 1 : 0)}"));
            // Inheritance test: does a child's below-threshold byte equal its parent's, split by each trailing byte (an "override" flag would make the unset ones inherit)?
            var byId = nodes.ToDictionary(n => n.Id);
            string Inherit(string name, Func<HircNode, int> flag)
            {
                var rows = nodes.Where(n => byId.ContainsKey(n.Base.ParentId)).Select(n => (Flag: flag(n), Same: n.Base.BelowThreshold == byId[n.Base.ParentId].Base.BelowThreshold,
                    SameLimit: (n.Base.MaxInstances == byId[n.Base.ParentId].Base.MaxInstances))).ToList();
                return $"{name}: " + string.Join("  ", rows.GroupBy(r => r.Flag).OrderBy(g => g.Key).Select(g => $"={g.Key}: n {g.Count()}, below equals parent's {100.0 * g.Count(r => r.Same) / g.Count():0}%, max equals parent's {100.0 * g.Count(r => r.SameLimit) / g.Count():0}%"));
            }
            // Value copying: among nodes that set a limit (or a below-threshold byte other than the default 1), how often the parent holds the same value, by trailing byte.
            foreach (var (label, flag) in new (string, Func<HircNode, int>)[] { ("A", n => n.Base.TrailingA), ("B", n => n.Base.TrailingB) })
            {
                var withLimit = nodes.Where(n => n.Base.MaxInstances > 0 && byId.ContainsKey(n.Base.ParentId)).ToList();
                Console.WriteLine($"limit set, parent has the same value, by trailing {label}: " + string.Join("  ", withLimit.GroupBy(flag).OrderBy(g => g.Key)
                    .Select(g => $"={g.Key}: n {g.Count()}, same {100.0 * g.Count(n => byId[n.Base.ParentId].Base.MaxInstances == n.Base.MaxInstances) / g.Count():0}%, parent has none {100.0 * g.Count(n => byId[n.Base.ParentId].Base.MaxInstances == 0) / g.Count():0}%")));
                var notDefault = nodes.Where(n => n.Base.BelowThreshold != 1 && byId.ContainsKey(n.Base.ParentId)).ToList();
                Console.WriteLine($"below-threshold byte not 1, parent has the same, by trailing {label}: " + string.Join("  ", notDefault.GroupBy(flag).OrderBy(g => g.Key)
                    .Select(g => $"={g.Key}: n {g.Count()}, same {100.0 * g.Count(n => byId[n.Base.ParentId].Base.BelowThreshold == n.Base.BelowThreshold) / g.Count():0}%")));
            }
            Console.WriteLine(Inherit("trailing A", n => n.Base.TrailingA));
            Console.WriteLine(Inherit("trailing B", n => n.Base.TrailingB));
            Console.WriteLine(Inherit("ignore-parent", n => n.Base.IgnoreParentMaxInstances ? 1 : 0));
            Console.WriteLine("below-threshold x node type:             " + Cross(n => $"{n.Type} below={n.Base.BelowThreshold}"));
        }
        if (Opt(rest, "--hot") is { } hotText)
        {
            // Nodes whose volume (property 0) curves reach above this many dB: which sounds are driven hot, and the events that reach them.
            float hot = float.Parse(hotText, CultureInfo.InvariantCulture);
            foreach (var o in lib.Banks.SelectMany(b => b.Bank.Objects).OfType<HircNode>().DistinctBy(n => n.Id))
                foreach (var r in o.Base.Rtpcs.Where(r => r.ParamId == 0 && r.Points.Count > 0 && r.Points.Max(p => p.Y) > hot))
                    Console.WriteLine($"  {o.Type} {o.Id:x8} in {lib.BankOf(o.Id)}: rtpc {(r.RtpcId == VoiceMixer.Speed ? "speed" : r.RtpcId == VoiceMixer.Distance ? "distance" : r.RtpcId.ToString("x8"))} y max {r.Points.Max(p => p.Y):0.##} " +
                                      $"[{string.Join(" ", r.Points.Select(p => $"{p.X:0.##}:{p.Y:0.##}"))}] own vol {o.Base.Volume} bus {o.Base.OutputBusId:x8}");
        }
        foreach (var ((rtpc, param), g) in groups)
        {
            string name = rtpc == VoiceMixer.Speed ? "speed" : rtpc == VoiceMixer.Distance ? "distance" : rtpc.ToString("x8");
            Console.WriteLine($"  rtpc {name,-9} property {param}: {g.Item1,4} nodes, y {g.Item2,8:0.##} .. {g.Item3,7:0.##}, x up to {g.Item4:0.##}");
        }
        return 0;
    }

    // ---------------------------------------------------------------- audio-mix

    /// <summary>
    /// Posts an event the way the client would for one emitter and prints each voice's resolved mix: wem, duration, volume (dB, with the
    /// master bus and the RTPC curves at <c>--speed</c>), pitch, LPF and whether it is positioned. <c>--local</c> sets the local-player
    /// switch, <c>--surface</c> the material_type, <c>--distance</c> the listener distance (only used by positioned voices),
    /// <c>--posts</c> repeats the post (random containers), <c>--cap peak|node|voice|off</c> how positive RTPC volume offsets are bounded
    /// (<see cref="RtpcVolumeCap"/>, default peak), <c>--step</c> adds the owner's footstep level (<see cref="FootstepLevel.OwnerGainDb"/>) as the client does for a footstep post.
    /// </summary>
    static int Mix(string eventName, string[] rest)
    {
        if (!TryCap(rest, out var cap)) return 2;
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: RepoRoot(), log: Console.Error);
        var sw = new SoundSwitches();
        if (rest.Contains("--local")) sw.Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue);
        if (Opt(rest, "--surface") is { } surface) sw.Set("material_type", surface);
        float speed = float.Parse(Opt(rest, "--speed") ?? "0", CultureInfo.InvariantCulture);
        float distance = float.Parse(Opt(rest, "--distance") ?? "3", CultureInfo.InvariantCulture);
        int posts = int.Parse(Opt(rest, "--posts") ?? "1", CultureInfo.InvariantCulture);
        var offset = new VoiceOffset(rest.Contains("--step") ? FootstepLevel.OwnerGainDb : 0f);
        // sound/impacts: --treatment impact | confirm-armour | confirm-flesh | confirm-kill adds the owner's impact treatment or the soft hit confirm's offset.
        switch (Opt(rest, "--treatment"))
        {
            case null: break;
            case "impact": offset = offset.Plus(ImpactSounds.ImpactTreatment); break;
            case "confirm-armour": offset = offset.Plus(ImpactSounds.HitConfirmOffset(true, false)); break;
            case "confirm-flesh": offset = offset.Plus(ImpactSounds.HitConfirmOffset(false, false)); break;
            case "confirm-kill": offset = offset.Plus(ImpactSounds.HitConfirmOffset(true, true)); break;
            case var other: Console.Error.WriteLine($"--treatment {other}: not one of impact, confirm-armour, confirm-flesh, confirm-kill"); return 2;
        }
        if (Opt(rest, "--state") is { } stateEvent) catalog.Post(stateEvent, new SoundSwitches());   // e.g. play_set_state_death_armored before a confirm
        var parameters = new Dictionary<uint, float> { [VoiceMixer.Speed] = speed };
        Console.WriteLine($"event {eventName}: {(rest.Contains("--local") ? "local player" : "remote player")}, surface {Opt(rest, "--surface") ?? "(default)"}, speed {speed:0.##} m/s, distance {distance:0.#} m, master bus {catalog.MasterBusVolume:0.#} dB, rtpc volume cap {cap}" +
                          (offset.IsNone ? "" : $", offset {offset} included"));
        for (int n = 0; n < posts; n++)
        {
            var play = catalog.Post(eventName, sw, 1);
            if (!play.Found) { Console.Error.WriteLine("event not found"); return 1; }
            Console.WriteLine($"post {n + 1}: {play.Voices.Count} voices, {play.Stops.Count} stops, {play.Properties.Count} property actions");
            foreach (var v in play.Voices)
            {
                var mix = VoiceMixer.Evaluate(v, null, 1, 0, v.Is3D ? distance : null, parameters, cap).With(offset);
                float dur = catalog.Duration(v.WemId);
                var rtpcs = v.Chain.SelectMany(c => c.Rtpcs).Where(r => r.RtpcId == VoiceMixer.Speed).Select(r => $"p{r.ParamId}={r.Evaluate(speed):0.#}");
                Console.WriteLine($"  wem {v.WemId,10} {dur,5:0.00} s  {(v.Is3D ? "3D" : "2D")}  vol {mix.VolumeDb,6:0.0} dB (at post {v.VolumeDb:0.0}, buses {v.BusVolumeDb:0.0})  pitch {mix.PitchCents,6:0} c  lpf {mix.Lpf,4:0.#}  speed-rtpc [{string.Join(" ", rtpcs)}]  bus chain [{string.Join(" > ", v.Buses.Select(b => $"{b.Id:x8}:{b.Volume:0.#}"))}]");
            }
        }
        return 0;
    }

    /// <summary><c>--cap peak|node|voice|off</c> (default peak): see <see cref="RtpcVolumeCap"/>. False (with a usage line on stderr) for a bad or missing value.</summary>
    static bool TryCap(string[] rest, out RtpcVolumeCap cap)
    {
        cap = RtpcVolumeCaps.Default;
        int i = Array.IndexOf(rest, "--cap");
        if (i < 0) return true;
        if (RtpcVolumeCaps.TryParse(i + 1 < rest.Length ? rest[i + 1] : null, out cap, out string? error)) return true;
        Console.Error.WriteLine($"--cap: {error}");
        Console.Error.WriteLine("usage: smtool audio-mix <event> [--local] [--surface s] [--speed v] [--distance d] [--posts n] [--cap peak|node|voice|off] [--step] [--treatment impact|confirm-armour|confirm-flesh|confirm-kill] [--state <set-state event>]; smtool audio-levels [--speed v] [--posts n] [--cap ...]");
        return false;
    }

    // ---------------------------------------------------------------- audio-decode

    static int Decode(string what, string[] rest)
    {
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        string outDir = Path.GetFullPath(Opt(rest, "--out") ?? AudioCache.DecodedDir);
        if (AudioCache.RefuseOutputDir(outDir, gameDir, RepoRoot()) is { } refusal) { Console.Error.WriteLine($"Refusing: {refusal}."); return 1; }
        bool wav = !rest.Contains("--no-wav");
        var codebooks = AudioCache.EnsureCodebooks(log: Console.Error);

        var names = LoadNames(gameDir, Opt(rest, "--names"));
        using var lib = OpenLibrary(gameDir, Opt(rest, "--packages"), names);
        List<uint> wems;
        string target = outDir;
        if (uint.TryParse(what, NumberStyles.None, CultureInfo.InvariantCulture, out uint wemId)) wems = [wemId];
        else
        {
            uint id = ParseEventId(what, names);
            var chain = lib.ResolveEvent(id);
            if (chain.Event is null) { Console.Error.WriteLine($"Event {what} (0x{id:x8}) is not in the loaded banks."); return 1; }
            wems = chain.WemIds().ToList();
            target = Path.Combine(outDir, SafeName(names.Name(id)));
            Console.WriteLine($"event {names.Name(id)}: {wems.Count} wems");
        }
        Directory.CreateDirectory(target);
        int ok = 0, failed = 0;
        foreach (var wem in wems)
        {
            var bytes = lib.ReadMedia(wem);
            if (bytes is null) { Console.WriteLine($"  {wem}: not found in the loaded packages"); failed++; continue; }
            try
            {
                var stream = WwiseVorbis.Convert(bytes, codebooks);
                using var ms = new MemoryStream();
                OggWriter.Write(stream, ms);
                byte[] ogg = ms.ToArray();
                var check = DecodeCheck(ogg, stream.Info);                  // only a stream that decodes is written
                string oggPath = Path.Combine(target, $"{wem}.ogg");
                File.WriteAllBytes(oggPath, ogg);
                string wavNote = "";
                if (wav)
                {
                    string wavPath = Path.Combine(target, $"{wem}.wav");
                    WriteWav(wavPath, check.Pcm, check.Channels, check.SampleRate);
                    wavNote = $", {Path.GetFileName(wavPath)}";
                }
                var info = stream.Info;
                Console.WriteLine($"  {wem}: {info.Channels} ch {info.SampleRate} Hz, {check.Frames} frames = {check.Frames / (double)check.SampleRate:0.000} s " +
                    $"(header {info.SampleCount} = {info.Duration:0.000} s), {(double.IsFinite(check.PeakDb) ? $"peak {check.PeakDb:0.0} dBFS" : "silent")}, {(info.ModPackets ? "mod" : "std")} packets " +
                    $"-> {oggPath}{wavNote}");
                if (check.Frames != info.SampleCount) Console.WriteLine($"    note: decoded length differs from the header by {check.Frames - info.SampleCount} frames");
                ok++;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
            {
                Console.WriteLine($"  {wem}: FAILED: {ex.Message}");
                failed++;
            }
        }
        Console.WriteLine($"decoded {ok}, failed {failed}, into {target}");
        return failed == 0 && ok > 0 ? 0 : 1;
    }

    sealed record PcmCheck(int Channels, int SampleRate, long Frames, float[] Pcm, double PeakDb);

    /// <summary>
    /// Decodes the Ogg with NVorbis (MIT) to prove the stream is valid; returns the PCM.
    /// NVorbis 0.10.5 takes its start position from the first page's granule but counts that packet's samples up to
    /// the end of its valid region, which lies (blocksize1 - blocksize0) / 4 past the window centre when a long block is
    /// followed by a short one; it then keeps up to that many extra samples at the end. Our granules follow libvorbis
    /// (window centres). Also, when the header's length would cut back further than the last packet, the converter
    /// leaves the last granule alone (see WwiseVorbis.ComputeGranules). In both cases the PCM is cut to the header's
    /// sample count, as long as the excess is under half a long block.
    /// </summary>
    static PcmCheck DecodeCheck(byte[] ogg, WwiseVorbisInfo info)
    {
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), closeOnDispose: true);
        int ch = reader.Channels, rate = reader.SampleRate;
        if (ch != info.Channels || rate != info.SampleRate) throw new InvalidDataException($"decoder reports {ch} ch {rate} Hz");
        var all = new List<float>();
        var buffer = new float[4096 * Math.Max(1, ch)];
        int n;
        while ((n = reader.ReadSamples(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.AsSpan(0, n));
        long frames = all.Count / Math.Max(1, ch);
        long tolerance = (1L << info.Blocksize1Exponent) / 2;
        if (frames > info.SampleCount && frames - info.SampleCount <= tolerance)
        {
            frames = info.SampleCount;
            all.RemoveRange((int)frames * ch, all.Count - (int)frames * ch);
        }
        float peak = 0;
        foreach (float s in all) peak = Math.Max(peak, Math.Abs(s));
        double db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        return new PcmCheck(ch, rate, frames, all.ToArray(), db);
    }

    static void WriteWav(string path, float[] pcm, int channels, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (float s in pcm) w.Write((short)Math.Clamp(MathF.Round(s * 32767f), -32768f, 32767f));
    }

    // ---------------------------------------------------------------- audio-verify

    public static int VerifyAll(string dir)
    {
        int packages = 0, banks = 0, objects = 0, failures = 0, streams = 0;
        var byType = new SortedDictionary<HircType, (int Total, int Raw)>();
        var actionTypes = new SortedDictionary<uint, int>();
        var errors = new Dictionary<string, int>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.pck", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            using var pck = AkpkPackage.Open(path);
            packages++;
            streams += pck.Streams.Count;
            foreach (var e in pck.Banks)
            {
                var bank = pck.ReadBank(e, loadMedia: false);
                banks++;
                objects += bank.Objects.Count;
                failures += bank.ParseFailures;
                foreach (var o in bank.Objects)
                {
                    byType.TryGetValue(o.Type, out var t);
                    byType[o.Type] = (t.Total + 1, t.Raw + (o is HircRaw ? 1 : 0));
                    if (o is HircRaw { ParseError: { } err })
                    {
                        string key = $"{o.Type}: {System.Text.RegularExpressions.Regex.Replace(err, "[0-9]+", "#")}";
                        errors[key] = errors.GetValueOrDefault(key) + 1;
                    }
                    if (o is HircAction a) actionTypes[a.ActionType] = actionTypes.GetValueOrDefault(a.ActionType) + 1;
                }
            }
        }
        Console.WriteLine($"packages={packages} banks={banks} streams={streams} objects={objects} parse-failures={failures}");
        foreach (var (t, (total, raw)) in byType) Console.WriteLine($"  {t,-20} {total,7} {(raw == total ? "kept raw" : raw > 0 ? $"raw={raw}" : "decoded")}");
        Console.WriteLine("action types: " + string.Join(" ", actionTypes.Select(kv => $"0x{kv.Key:x}({HircAction.KindOf(kv.Key)})={kv.Value}")));
        foreach (var (k, n) in errors.OrderByDescending(kv => kv.Value).Take(15)) Console.WriteLine($"  error {n,6} {k}");
        return failures == 0 && packages > 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- audio-coverage

    /// <summary>
    /// M26b: which sound events the multiplayer triggers name (blueprints, wargear, projectiles, hit containers, the map's
    /// emitters) and whether the catalogue resolves each to playable media; plus the evidence on switch group 0x0d500bea.
    /// </summary>
    static int Coverage(string[] rest)
    {
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), RepoRoot());
        string map = Opt(rest, "--map") ?? "mp_tdm_09";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tuning = GameplayTuning.Load(gameDir);
        using var catalog = AudioCatalog.Open(gameDir, map, repoRoot: RepoRoot(), log: Console.Error);
        Console.WriteLine($"catalogue: {catalog.LoadedPackages.Count} packages, {catalog.Library.ObjectCount:N0} objects, {sw.ElapsedMilliseconds} ms" +
            (catalog.MissingPackages.Count > 0 ? $"; missing: {string.Join(", ", catalog.MissingPackages)}" : ""));
        sw.Restart();
        var model = CharacterModel.LoadMarine(gameDir);
        var triggers = LoadTriggers(gameDir, tuning, model);
        var mapAudio = MapAudio.Load(gameDir, map);
        Console.WriteLine($"triggers: {triggers.Files.Count} files, {triggers.Files.Values.Sum(f => f.Bindings.Count)} commands, {triggers.ClipTags.Count} clips with action tags, " +
            $"{mapAudio.Emitters.Count} map emitters, {mapAudio.Regions.Count} regions, {sw.ElapsedMilliseconds} ms");
        foreach (var n in triggers.Notes) Console.WriteLine($"  note: {n}");
        // Fidelity gap 9: the match and menu events the client posts itself (SmNet.SeizeSounds, SmContent.UiSoundRules; the literals are pinned by SeizeSoundsTests).
        string[] matchEvents = ["play_mp_point_being_captured", "stop_mp_point_being_captured", "Play_Objective_Complete", "Play_MP_Points_Total", UiSoundRules.Move, UiSoundRules.Select, UiSoundRules.Back];
        var names = triggers.EventNames().Concat(matchEvents).Concat(mapAudio.Emitters.Select(e => e.Event)).Concat(mapAudio.Regions.SelectMany(r => new[] { r.EnterEvent, r.ExitEvent }))
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        int ok = 0, silent = 0, missing = 0;
        foreach (var name in names)
        {
            uint id = Fnv.Hash32(name);
            if (!catalog.HasEvent(id)) { missing++; Console.WriteLine($"  MISSING  {name}"); continue; }
            var wems = catalog.WemsOf(id);
            int media = wems.Count(catalog.HasMedia);
            var probe = catalog.Post(id, SoundSwitches.None);
            string kinds = $"{probe.Voices.Count} voices, {probe.Stops.Count} stops, {probe.UnsupportedActions} unsupported actions";
            if (media == 0) { silent++; Console.WriteLine($"  NO MEDIA {name} ({wems.Count} wems reachable; {kinds})"); }
            else { ok++; Console.WriteLine($"  ok       {name}: {media}/{wems.Count} wems with media; one post: {kinds}"); }
        }
        Console.WriteLine($"events: {names.Count} named, {ok} with media, {silent} without media, {missing} not in the loaded banks");

        // 0x0d500bea: per case, how the sounds under it are positioned (3D with attenuation or 2D) and where their media lives.
        var stats = new Dictionary<string, (int Sounds, int Positioned, int Attenuated, int TwoD, long Bytes)>();
        int switches = 0;
        foreach (var obj in catalog.Library.Banks.SelectMany(b => b.Bank.Objects).OfType<HircSwitch>().Where(s => s.GroupId == AudioCatalog.PlayerGroup).DistinctBy(s => s.Id))
        {
            switches++;
            foreach (var c in obj.Cases)
            {
                string key = c.SwitchId == AudioCatalog.LocalPlayerValue ? "0x0d500bea" : c.SwitchId == Fnv.Hash32("default") ? "default" : $"0x{c.SwitchId:x8}";
                var switchesFor = new SoundSwitches().Set(AudioCatalog.PlayerGroup, c.SwitchId);
                foreach (var node in c.NodeIds)
                {
                    var chain = catalog.Library.ResolveNode(node);
                    foreach (var s in Sounds(chain))
                    {
                        var (is3D, att) = Positioning(catalog, s);
                        var loc = catalog.Library.FindMedia(s.SourceId);
                        var t = stats.GetValueOrDefault(key);
                        stats[key] = (t.Sounds + 1, t.Positioned + (is3D ? 1 : 0), t.Attenuated + (att ? 1 : 0), t.TwoD + (is3D ? 0 : 1), t.Bytes + (loc?.Size ?? 0));
                    }
                }
            }
        }
        // Buses (interpretation, M33: u32 parent, f32 a, f32 b; a + b is the bus's gain): how many parents are buses, the gains, the master.
        var buses = catalog.Library.Banks.SelectMany(b => b.Bank.Objects).Where(o => o.Type == HircType.Bus && o.Payload.Length >= 12).DistinctBy(o => o.Id).ToList();
        var busIds = buses.Select(b => b.Id).ToHashSet();
        var parents = buses.Select(b => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.Payload.Span)).ToList();
        var gains = buses.Select(b => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(b.Payload.Span[4..]) + System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(b.Payload.Span[8..])).ToList();
        Console.WriteLine($"buses: {buses.Count}; parents that are buses {parents.Count(busIds.Contains)}, masters (parent 0) {parents.Count(p => p == 0)}, other {parents.Count(p => p != 0 && !busIds.Contains(p))}; " +
            $"gains {gains.Min():0.##} .. {gains.Max():0.##} dB, {gains.Count(v => v != 0)} non-zero; master bus {catalog.MasterBusId():x8} {catalog.MasterBusVolume:0.##} dB");
        Console.WriteLine($"switch group 0x0d500bea: {switches} switch containers");
        foreach (var (k, v) in stats.OrderBy(kv => kv.Key))
            Console.WriteLine($"  case {k}: {v.Sounds} sound objects, 3D {v.Positioned} (with attenuation {v.Attenuated}), 2D {v.TwoD}, media {v.Bytes / 1024.0 / Math.Max(1, v.Sounds):F1} KB per sound");
        return 0;

        static IEnumerable<HircSound> Sounds(ChainNode n)
        {
            var seen = new HashSet<ChainNode>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<ChainNode>();
            stack.Push(n);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (!seen.Add(x)) continue;
                if (x.Object is HircSound s) yield return s;
                foreach (var b in x.Branches) foreach (var c in b.Nodes) stack.Push(c);
            }
        }

        static (bool Is3D, bool Attenuated) Positioning(AudioCatalog catalog, HircSound s)
        {
            HircNode? node = s;
            for (int i = 0; node is not null && i < 32; i++)
            {
                if (node.Base.PositioningOverrideParent) return (node.Base.Is3D, node.Base.Is3D && node.Base.AttenuationId != 0);
                node = node.Base.ParentId != 0 ? catalog.Library.Find(node.Base.ParentId) as HircNode : null;
            }
            return (false, false);
        }
    }

    // ---------------------------------------------------------------- helpers

    static WwiseLibrary OpenLibrary(string gameDir, string? packages, WwiseNames names)
    {
        string audioDir = Path.Combine(gameDir, "streamingaudio");
        var list = packages is null
            ? MultiplayerPackages.Concat(Directory.EnumerateFiles(audioDir, "weapon_*.pck").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal))
            : packages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lib = WwiseLibrary.Open(audioDir, list, out var missing);
        foreach (var m in missing) Console.Error.WriteLine($"warning: package {m}.pck not found");
        foreach (var b in lib.Banks) names.Add(b.Bank.Name ?? "");
        return lib;
    }

    static uint ParseEventId(string s, WwiseNames names)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
            return hex;
        names.Add(s);
        return Fnv.Hash32(s);
    }

    /// <summary>Names for IDs: every string in the sound-related BAF archives, switch names, plus an optional list file.</summary>
    static WwiseNames LoadNames(string? gameDir, string? extraFile)
    {
        var names = new WwiseNames();
        // Switch-group values used by the MP sounds (docs/discovery.md); "default" covers container defaults.
        foreach (var n in new[] { "material_type", "air", "armour", "ceramite_armour", "default", "dirt", "energy_shield", "flesh",
                     "gravel", "metal", "sand", "stone", "weapon_mode", "none", "alive" })
            names.Add(n);
        if (gameDir is not null)
            foreach (var rel in NameSources)
            {
                string path = Path.Combine(gameDir, rel);
                if (!File.Exists(path)) continue;
                using var a = ObpkArchive.Open(path);
                a.ReadEntries(e => a.GroupName(e) == "attr_pc", (_, data) =>
                {
                    if (!BafDocument.HasMagic(data)) return;
                    try { AddBafStrings(BafDocument.Read(data).Root, names, 0); }
                    catch (InvalidDataException) { }
                });
            }
        if (extraFile is not null)
            foreach (var line in File.ReadLines(extraFile, Encoding.UTF8)) names.Add(line.Trim());
        return names;
    }

    static void AddBafStrings(BafTable t, WwiseNames names, int depth)
    {
        if (depth > 64) return;
        foreach (var e in t.Entries)
        {
            if (e.Type == BafType.String) names.Add(e.AsString());
            else if (e.Type == BafType.Table) AddBafStrings(e.AsTable(), names, depth + 1);
            names.Add(e.Name);
        }
    }

    static string? TryGameDir(string? dir) => dir is not null && Directory.Exists(Path.Combine(dir, "data")) ? dir : null;

    /// <summary>The repository root (the folder holding SmReimpl.slnx) above the current directory or the tool, if any.</summary>
    static string? RepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        return null;
    }

    /// <summary>The multiplayer triggers as the client loads them: both sides' wargear and the Ork's (Ork O8), with the marine and Ork rigs' clip tags.</summary>
    static SoundTriggers LoadTriggers(string gameDir, GameplayTuning tuning, CharacterModel model) =>
        SoundTriggers.Load(gameDir, SoundTriggers.WargearNames(tuning.Config).Concat(OrkClass.Wargear), SoundTriggers.PlayerBlueprints,
            SmFormats.Havok.HavokBehavior.Read(model.Rig.Pack), OrkSounds.ReadGraphs(gameDir));

    static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    static string? Opt(string[] rest, string name)
    {
        int i = Array.IndexOf(rest, name);
        return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
    }
}
