using System.Globalization;
using System.Numerics;
using SmContent;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmTool.Dump;

namespace SmTool;

/// <summary>
/// smtool sync-kills (executions-inventory, 2026-11-25; docs/executions-coverage.md): the install's sync kills, read-only. For each
/// attacker rig, every sync move (graph, state path, clips with lengths and distance weights, the timing tags in seconds, the
/// interaction events it sends, the alignment tag, the transitions into it), the root motion and the interaction bone of its
/// clips; then, for each victim rig, the state each sent <c>PORT_*</c> event enters and that state's clips. Nothing is written;
/// the archives are opened for reading only (<see cref="ObpkArchive.Open"/>).
/// </summary>
static class SyncKillsCommand
{
    /// <summary>The default attackers: the campaign/player marine rig (player_set), the MP rigs, the MP Dreadnought.</summary>
    static readonly string[] DefaultAttackers =
    [
        "data/chunks/ebp_sets__player_set.oppc:havok_spacemarine__hkxx",
        "data/chunks/ebp_sets__mp_versus_set.oppc:havok_sm_base_mp__hkxx",
        "data/chunks/ebp_sets__mp_coop_default_player.oppc:havok_sm_base_mp__hkxx",
        "data/chunks/ebp_sets__mp_versus_set.oppc:havok_sm_dreadnought_basic__hkxx",
    ];

    /// <summary>The default victims: every character rig of the Ork and Chaos core sets, the co-op (Exterminatus) rigs and both marine rigs.</summary>
    static readonly string[] DefaultVictims =
    [
        "data/chunks/ebp_sets__ork_core_units.oppc:havok_boy__hkxx",
        "data/chunks/ebp_sets__ork_core_units.oppc:havok_nob__hkxx",
        "data/chunks/ebp_sets__ork_core_units.oppc:havok_gretchin__hkxx",
        "data/chunks/ebp_sets__s008-001.oppc:havok_ard_boy__hkxx",
        "data/chunks/ebp_sets__seq08_core_units.oppc:havok_elite_boy__hkxx",
        "data/chunks/ebp_sets__s002-001.oppc:havok_stormboy__hkxx",
        "data/chunks/ebp_sets__sequence2_core_units.oppc:havok_warboss_regular__hkxx",
        "data/chunks/ebp_sets__chaos_core_units.oppc:havok_chaosspacemarine__hkxx",
        "data/chunks/ebp_sets__chaos_core_units.oppc:havok_bloodletter__hkxx",
        "data/chunks/ebp_sets__chaos_core_units.oppc:havok_tainted_psyker__hkxx",
        "data/chunks/ebp_sets__chaos_core_units.oppc:havok_guardsman_a__hkxx",
        "data/chunks/ebp_sets__mp_coop_set.oppc:havok_boy_coop__hkxx",
        "data/chunks/ebp_sets__mp_coop_set.oppc:havok_nob_coop__hkxx",
        "data/chunks/ebp_sets__mp_coop_set.oppc:havok_ard_boy_coop__hkxx",
        "data/chunks/ebp_sets__mp_coop_set.oppc:havok_gretchin_coop__hkxx",
        "data/chunks/ebp_sets__mp_coop_set_sm.oppc:havok_chaosspacemarine_coop__hkxx",
        "data/chunks/ebp_sets__mp_coop_set_sm.oppc:havok_psyker__hkxx",
        "data/chunks/ebp_sets__mp_coop_set_habs.oppc:havok_bloodletter_coop__hkxx",
        "data/chunks/ebp_sets__mp_coop_set_grid_test.oppc:havok_elite_boy__hkxx",
        "data/chunks/ebp_sets__mp_versus_set.oppc:havok_sm_base_mp__hkxx",
        "data/chunks/ebp_sets__player_set.oppc:havok_spacemarine__hkxx",
    ];

    public static int Run(string[] args)
    {
        try { return RunInner(args, Console.Out); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int RunInner(string[] args, TextWriter o)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Error.WriteLine("usage: smtool sync-kills [--game <dir>] [--attacker <archive>:<entry>]... [--victim <archive>:<entry>]... [--graph <pattern>] [--no-victims] [--no-geometry]");
            return 2;
        }
        string gameDir = GameInstall.Locate(Opt(args, "--game"));
        var attackers = Many(args, "--attacker") is { Count: > 0 } a ? a : [.. DefaultAttackers];
        var victims = Many(args, "--victim") is { Count: > 0 } v ? v : [.. DefaultVictims];
        string? graphPattern = Opt(args, "--graph");
        bool geometry = !args.Contains("--no-geometry");
        var inv = CultureInfo.InvariantCulture;

        var sent = new HashSet<string>(StringComparer.Ordinal);
        var attackerClips = new List<(string Rig, HavokCharacterFile Character, string Move, string Clip)>();
        var victimClips = new List<(string Rig, HavokCharacterFile Character, string Clip)>();
        foreach (var spec in attackers)
        {
            if (Load(gameDir, spec) is not { } character) { o.WriteLine($"#### attacker {spec}: not found"); continue; }
            var graphs = HavokBehaviorDetailReader.Read(character.Pack).Where(g => graphPattern is null || g.Name.Contains(graphPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            var moves = SyncKillInventory.Moves(character.Pack, graphs);
            o.WriteLine($"#### attacker {spec}: {graphs.Count} graphs, {moves.Count} sync sequencers, skeleton {character.Skeleton.BoneNames.Count} bones");
            foreach (var m in moves)
            {
                var g = graphs.First(x => x.Name == m.Graph);
                PrintMove(o, character, m, geometry, inv);
                foreach (var e in SyncKillInventory.Entries(g, m, character.Pack))
                    o.WriteLine($"    entry  [{e.Machine}] {e.From} -> {e.To}{(e.Event is { } ev ? " on " + ev : "")}{(e.Condition is { } c ? " if " + c : "")}{(e.Branch is { } b ? " branch " + b : "")}{(e.Priority != 0 ? $" [priority {e.Priority}]" : "")}{(e.Flags != 0 ? $" flags=0x{e.Flags:X}" : "")}");
            }
            foreach (var s in SyncKillInventory.SentEvents(moves)) sent.Add(s);
            foreach (var m in moves)
                foreach (var (clip, _) in m.Clips)
                    if (SyncKillInventory.IsSyncClip(clip) || m.StatePath.Contains("Sync", StringComparison.OrdinalIgnoreCase))
                        attackerClips.Add((spec, character, $"[{m.Graph}] {m.StatePath.Split(" / ")[^1]}", clip));
            o.WriteLine();
        }
        o.WriteLine($"#### interaction events sent ({sent.Count}): {string.Join(", ", sent.OrderBy(x => x, StringComparer.Ordinal))}");
        o.WriteLine();
        if (args.Contains("--no-victims")) return 0;
        foreach (var spec in victims)
        {
            if (Load(gameDir, spec) is not { } character) { o.WriteLine($"#### victim {spec}: not found"); continue; }
            var graphs = HavokBehaviorDetailReader.Read(character.Pack);
            var reactions = SyncKillInventory.Reactions(character.Pack, graphs, sent);
            var ownMoves = SyncKillInventory.Moves(character.Pack, graphs);
            o.WriteLine($"#### victim {spec}: {graphs.Count} graphs, {reactions.Count} reactions to the sent events, {ownMoves.Count} own sync sequencers, skeleton {character.Skeleton.BoneNames.Count} bones, {character.AnimationNames.Count(SyncKillInventory.IsSyncClip)} sync clips");
            foreach (var r in reactions)
            {
                o.WriteLine($"  on {r.Event}: [{r.Graph}] {r.From} -> {r.StatePath}");
                foreach (var s in r.Sequencers) PrintMove(o, character, s, geometry, inv, indent: "    ");
            }
            // The rig's own sync sequencers that no sent event reaches (victim halves driven otherwise, or its own attacks on a marine).
            var reached = reactions.SelectMany(r => r.Sequencers).Select(s => s.SequencerOffset).ToHashSet();
            foreach (var m in ownMoves.Where(m => !reached.Contains(m.SequencerOffset)))
            {
                o.WriteLine($"  own (not reached by a sent event):");
                PrintMove(o, character, m, geometry, inv, indent: "    ");
                foreach (var e in SyncKillInventory.Entries(graphs.First(x => x.Name == m.Graph), m, character.Pack))
                    o.WriteLine($"      entry  [{e.Machine}] {e.From} -> {e.To}{(e.Event is { } ev ? " on " + ev : "")}{(e.Condition is { } c ? " if " + c : "")}{(e.Priority != 0 ? $" [priority {e.Priority}]" : "")}{(e.Flags != 0 ? $" flags=0x{e.Flags:X}" : "")}");
            }
            o.WriteLine($"  sync clips: {string.Join(", ", character.AnimationNames.Where(SyncKillInventory.IsSyncClip).Select(c => $"{c} {Length(character, c)}"))}");
            o.WriteLine();
            // Victim halves: the rig's own sync clips plus any clip its sync states play (the knife's stab victim is a melee clip).
            foreach (var c in character.AnimationNames.Where(SyncKillInventory.IsSyncClip).Concat(ownMoves.SelectMany(m => m.Clips.Select(x => x.Clip))).Distinct())
                victimClips.Add((spec, character, c));
        }
        if (geometry) PrintPairs(o, attackerClips, victimClips, inv);
        return 0;
    }

    /// <summary>
    /// Each attacker clip with every victim clip of the same <see cref="SyncKillInventory.Stem"/> (a victim rig other than the attacker's): where the
    /// victim starts and faces (the same clip on another marine rig is not a partner) when both interaction bones meet at the clip start (<see cref="SyncKillInventory.Align"/>), and where each root ends,
    /// in the attacker's starting frame (metres, X right, Y forward). The footprint is what alignment needs free of walls and drops.
    /// </summary>
    static void PrintPairs(TextWriter o, List<(string Rig, HavokCharacterFile Character, string Move, string Clip)> attackers,
        List<(string Rig, HavokCharacterFile Character, string Clip)> victims, CultureInfo inv)
    {
        var byStem = victims.GroupBy(v => SyncKillInventory.Stem(v.Clip)).ToDictionary(g => g.Key, g => g.ToList());
        o.WriteLine("#### pairs (attacker clip -> victim clip by name; positions in the attacker's start frame, metres)");
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in attackers)
        {
            if (!done.Add($"{a.Rig}|{a.Move}|{a.Clip}")) continue;
            var partners = byStem.GetValueOrDefault(SyncKillInventory.Stem(a.Clip))?.Where(v => !ReferenceEquals(v.Character, a.Character) && !v.Clip.Equals(a.Clip, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
            if (partners.Count == 0) { o.WriteLine($"  {a.Move} {a.Clip}: no victim clip of the same name in the victim rigs"); continue; }
            if (Mark(a.Character, a.Clip, 0f) is not { } am || Root(a.Character, a.Clip) is not { } ar) { o.WriteLine($"  {a.Move} {a.Clip}: no interaction bone or root motion"); continue; }
            foreach (var v in partners.DistinctBy(p => (p.Rig, p.Clip)))
            {
                if (Mark(v.Character, v.Clip, 0f) is not { } vm || Root(v.Character, v.Clip) is not { } vr) { o.WriteLine($"  {a.Move} {a.Clip} -> {v.Clip}: victim has no interaction bone or root motion"); continue; }
                var (start, yaw) = SyncKillInventory.Align(am, vm);
                var vEnd = start + SyncKillInventory.Rotate(new Vector2(vr.X, vr.Y), yaw);
                string rig = v.Rig[(v.Rig.LastIndexOf(':') + 1)..].Replace("__hkxx", "");
                o.WriteLine(string.Create(inv, $"  {a.Move} {a.Clip} ({LengthOf(a.Character, a.Clip):0.###}s) -> {rig} {v.Clip} ({LengthOf(v.Character, v.Clip):0.###}s): victim starts ({start.X:0.00}, {start.Y:0.00}) {start.Length():0.00} m away, yaw {yaw:0}deg; attacker ends ({ar.X:0.00}, {ar.Y:0.00}) turn {ar.Z:0}deg; victim ends ({vEnd.X:0.00}, {vEnd.Y:0.00}) turn {vr.Z:0}deg"));
            }
        }
    }

    /// <summary>The clip's RIG_Interaction bone on the ground plane at <paramref name="time"/>, or null when the rig has no such bone.</summary>
    static SyncKillInventory.BoneMark? Mark(HavokCharacterFile character, string clip, float time)
    {
        int bone = IndexOf(character.Skeleton, "RIG_Interaction");
        if (bone < 0) return null;
        try
        {
            var anim = character.LoadAnimation(clip);
            var m = Pose(character.Skeleton, anim, time)[bone];
            var fwd = Vector3.TransformNormal(Vector3.UnitY, m);
            return new SyncKillInventory.BoneMark(new Vector2(m.Translation.X, m.Translation.Y), MathF.Atan2(-fwd.X, fwd.Y) * 180f / MathF.PI);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return null; }
    }

    /// <summary>The clip's root motion from start to end: X, Y (metres) and the turn (degrees) as Z; null without root motion.</summary>
    static Vector3? Root(HavokCharacterFile character, string clip)
    {
        try
        {
            if (character.LoadExtractedMotion(clip) is not { Samples.Count: > 0 } motion) return null;
            var a = motion.Samples[0]; var b = motion.Samples[^1];
            return new Vector3(b.X - a.X, b.Y - a.Y, (b.W - a.W) * 180f / MathF.PI);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return null; }
    }

    /// <summary>Tags worth a line: everything except footsteps, sounds and attachments (counted instead).</summary>
    static bool Noise(SyncKillInventory.SyncTag t) =>
        t.Name is { } n && (n.StartsWith("EVT_Left_Foot", StringComparison.Ordinal) || n.StartsWith("EVT_Right_Foot", StringComparison.Ordinal)
            || n.StartsWith("SFX_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Start_Action_FootStep", StringComparison.Ordinal));

    static void PrintMove(TextWriter o, HavokCharacterFile character, SyncKillInventory.SyncMove m, bool geometry, CultureInfo inv, string indent = "  ")
    {
        float len = m.Clips.Count > 0 ? LengthOf(character, m.Clips[0].Clip) : float.NaN;
        o.WriteLine($"{indent}[{m.Graph}] {m.StatePath}  (sequencer '{m.Sequencer}')");
        o.WriteLine($"{indent}  clips: {string.Join(", ", m.Clips.Select(c => $"{c.Clip} {Length(character, c.Clip)}{(c.Weight is float w ? $" @{w.ToString("0.##", inv)}" : "")}"))}");
        string S(float f) => float.IsFinite(len) ? (f * len).ToString("0.000", inv) + "s" : f.ToString("0.####", inv);
        int quiet = 0;
        foreach (var t in m.Tags.OrderBy(t => t.Start))
        {
            if (Noise(t)) { quiet++; continue; }
            string extra = t.Fields is { } f ? $" (@28={f[0].ToString("0.###", inv)} @80={f[1]} @84={f[2].ToString("0.###", inv)} @88={f[3].ToString("0.###", inv)} @92={f[4]})" : "";
            o.WriteLine($"{indent}  tag {t.Kind,-15} {S(t.Start)}-{S(t.End)} {t.Name}{extra}");
        }
        if (quiet > 0) o.WriteLine($"{indent}  ({quiet} footstep/sound tags not listed)");
        if (!geometry) return;
        foreach (var (clip, _) in m.Clips)
            if (Geometry(character, clip, inv) is { } line) o.WriteLine($"{indent}  geo {clip}: {line}");
    }

    /// <summary>
    /// A clip's root motion (extracted motion: start to end displacement and turn) and, when the rig has the <c>RIG_Interaction</c>
    /// bone, that bone's model-space position and heading at the clip's start and end (the partner's mark, interpretation).
    /// </summary>
    static string? Geometry(HavokCharacterFile character, string clip, CultureInfo inv)
    {
        var parts = new List<string>();
        try
        {
            if (character.LoadExtractedMotion(clip) is { Samples.Count: > 0 } motion)
            {
                var a = motion.Samples[0]; var b = motion.Samples[^1];
                parts.Add(string.Create(inv, $"root dx {b.X - a.X:0.000} dy {b.Y - a.Y:0.000} dz {b.Z - a.Z:0.000} turn {(b.W - a.W) * 180f / MathF.PI:0.0}deg"));
            }
            else parts.Add("no root motion");
            int bone = IndexOf(character.Skeleton, "RIG_Interaction");
            if (bone >= 0)
            {
                var anim = character.LoadAnimation(clip);
                bool tracked = anim.TrackToBone.Contains((short)bone);
                foreach (float t in new[] { 0f, anim.Animation.Duration })
                {
                    var m = Pose(character.Skeleton, anim, t)[bone];
                    var fwd = Vector3.TransformNormal(Vector3.UnitY, m);
                    float heading = MathF.Atan2(-fwd.X, fwd.Y) * 180f / MathF.PI;
                    parts.Add(string.Create(inv, $"interaction@{t:0.##}s ({m.Translation.X:0.000}, {m.Translation.Y:0.000}, {m.Translation.Z:0.000}) heading {heading:0.0}deg"));
                }
                if (!tracked) parts.Add("(interaction bone not animated: reference pose)");
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { parts.Add(ex.Message); }
        return string.Join("; ", parts);
    }

    static Matrix4x4[] Pose(HavokSkeleton skeleton, HavokBoundAnimation anim, float time)
    {
        var local = new HavokTransform[skeleton.BoneNames.Count];
        for (int b = 0; b < local.Length; b++) local[b] = skeleton.ReferencePose[b];
        var sampled = anim.Animation.Sample(Math.Clamp(time, 0f, anim.Animation.Duration));
        for (int t = 0; t < sampled.Length; t++)
            if ((uint)anim.TrackToBone[t] < (uint)local.Length) local[anim.TrackToBone[t]] = sampled[t];
        return skeleton.ModelSpace(local);
    }

    static int IndexOf(HavokSkeleton skeleton, string bone)
    {
        for (int i = 0; i < skeleton.BoneNames.Count; i++)
            if (skeleton.BoneNames[i].Equals(bone, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    static float LengthOf(HavokCharacterFile c, string clip)
    {
        try { return c.LoadAnimation(clip).Animation.Duration; }
        catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return float.NaN; }
    }

    static string Length(HavokCharacterFile c, string clip) => LengthOf(c, clip) is var l && float.IsFinite(l) ? l.ToString("0.###", CultureInfo.InvariantCulture) + "s" : "?";

    static readonly Dictionary<string, HavokCharacterFile?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rig "archive:entry" (the archive relative to the game folder), read with <see cref="ObpkArchive.Open"/> (read-only); null when absent.</summary>
    static HavokCharacterFile? Load(string gameDir, string spec)
    {
        if (Cache.TryGetValue(spec, out var hit)) return hit;
        int colon = spec.LastIndexOf(':');
        if (colon <= 0) throw new InvalidDataException($"'{spec}': expected <archive>:<entry>.");
        string archive = Path.Combine(gameDir, spec[..colon]), entry = spec[(colon + 1)..];
        byte[]? bytes = null;
        if (File.Exists(archive))
            using (var a = ObpkArchive.Open(archive))
                a.ReadEntries(e => bytes is null && e.FileName.Equals(entry, StringComparison.OrdinalIgnoreCase), (_, d) => bytes = d.ToArray());
        var c = bytes is null ? null : new HavokCharacterFile(bytes);
        Cache[spec] = c;
        return c;
    }

    static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static List<string> Many(string[] args, string name)
    {
        var list = new List<string>();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) list.Add(args[i + 1]);
        return list;
    }
}
