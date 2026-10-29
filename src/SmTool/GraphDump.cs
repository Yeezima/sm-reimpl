using System.Text;
using SmContent;
using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmTool;

/// <summary>
/// smtool graph-dump: read-only inspection of the behaviour graphs in a character packfile (default: the multiplayer
/// Space Marine rig). Prints class counts, the generator tree (state machines, blends with weights and bone masks,
/// modifier and reference generators), bone masks by skeleton bone name, sequencers with clips, lengths, blend hints and
/// tags, and blending transitions. <c>--raw &lt;class&gt;</c> prints objects of a class as annotated 32-bit words for
/// establishing layouts.
/// </summary>
static class GraphDump
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

    static readonly string[] Modes = ["--list", "--raw-any", "--animations", "--hipfire", "--reload", "--locomotion", "--turns", "--falls", "--upper", "--clip-table", "--jump-flight", "--bones", "--gaits", "--brace", "--melee", "--root-motion", "--sprint-edges", "--pose-diff", "--twists"];

    static int RunInner(string[] args, TextWriter o)
    {
        if (args.Length == 0 || args[0].StartsWith("--") && !Modes.Contains(args[0])) return Usage();
        ShowZeros = args.Contains("--zeros");
        // Ork O2: --rig picks a rig profile (its packfile and the graph names the analyzers look for); default the marine.
        var rig = RigProfiles.Marine;
        if (args.Contains("--rig"))
        {
            string? rigName = Opt(args, "--rig");
            if (rigName is null || rigName.StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine("--rig needs a value (marine, ork)."); return 2; }
            if (RigProfiles.ByName(rigName) is not { } named) { Console.Error.WriteLine($"Unknown rig '{rigName}' (marine, ork)."); return 2; }
            rig = named;
        }
        string gameDir = GameInstall.Locate(Opt(args, "--game"));
        string archive = Opt(args, "--archive") ?? rig.Archive;
        string entry = Opt(args, "--entry") ?? rig.Entry;
        byte[]? bytes = null;
        using (var a = ObpkArchive.Open(Path.Combine(gameDir, archive)))
            a.ReadEntries(e => e.FileName.Equals(entry, StringComparison.OrdinalIgnoreCase) && bytes is null, (_, d) => bytes = d.ToArray());
        if (bytes is null) throw new FileNotFoundException($"'{entry}' not found in {archive}.");
        var character = new HavokCharacterFile(bytes);
        var pack = character.Pack;
        var bones = character.Skeleton.BoneNames;
        string Hint(string clip) { try { return character.LoadBlendHint(clip).ToString(); } catch (KeyNotFoundException) { return "?"; } }
        string Length(string clip) { try { return $"{character.LoadAnimation(clip).Animation.Duration:0.###}s"; } catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return "?"; } }

        if (args[0] == "--raw-any")
        {
            Raw(o, pack, 0, int.MaxValue, null, args.Length > 1 ? args[1] : "", int.TryParse(Opt(args, "--count"), out int c) ? c : 3);
            return 0;
        }
        if (args[0] == "--animations")
        {
            string? pattern = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
            foreach (var a in character.AnimationNames.Where(a => pattern is null || Matches(a, pattern)))
                o.WriteLine($"{a,-48} {Length(a),7} {Hint(a)}");
            return 0;
        }
        if (args[0] == "--root-motion")
        {
            // feel/locks-sprint-stop: a clip's extracted root motion (the sprint stop SM_NAV_Bullrush2Idle_F_01 by default): every sample's time,
            // forward distance from the start and the speed since the previous sample.
            var clips = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (clips.Count == 0) clips = ["SM_NAV_Bullrush2Idle_F_01", "SM_NAV_Bullrush2Idle_F_HalfSpeed_01"];
            foreach (var clip in clips)
            {
                HavokExtractedMotion? motion;
                try { motion = character.LoadExtractedMotion(clip); }
                catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { o.WriteLine($"== {clip}: {ex.Message}"); continue; }
                if (motion is null || motion.Samples.Count == 0) { o.WriteLine($"== {clip}: no root motion"); continue; }
                var first = motion.Samples[0];
                int n = motion.Samples.Count;
                o.WriteLine($"== {clip}: {Length(clip)}, {n} samples over {motion.Duration:0.###} s");
                for (int i = 0; i < n; i++)
                {
                    var p = motion.Samples[i];
                    float t = n > 1 ? motion.Duration * i / (n - 1) : 0f;
                    float d = new System.Numerics.Vector2(p.X - first.X, p.Y - first.Y).Length();
                    float v = i == 0 || n < 2 ? 0f : new System.Numerics.Vector2(p.X - motion.Samples[i - 1].X, p.Y - motion.Samples[i - 1].Y).Length() / (motion.Duration / (n - 1));
                    o.WriteLine($"  {t,6:0.000} s  x {p.X - first.X,7:0.000}  y {p.Y - first.Y,7:0.000}  z {p.Z - first.Z,6:0.000}  dist {d,6:0.000} m  speed {v,6:0.00} m/s");
                }
            }
            return 0;
        }
        if (args[0] == "--bones")
        {
            for (int i = 0; i < bones.Count; i++) o.WriteLine($"{i,3} {bones[i]}  (parent {(character.Skeleton.Parents[i] >= 0 ? bones[character.Skeleton.Parents[i]] : "-")})");
            return 0;
        }
        if (args[0] == "--gaits")
        {
            // Ork O7a: the Ork's locomotion plan and the measured gait of each clip it blends (marine: the library's clips).
            GaitLibrary gaits;
            if (rig.Name == "ork")
            {
                var bundle = OrkLocomotion.Load(gameDir);
                gaits = bundle.Gaits;
                foreach (var (label, set) in new[] { ("gun", bundle.Plan.Gun), ("pistol", bundle.Plan.Pistol) })
                {
                    if (set is null) { o.WriteLine($"== {label}: no move set"); continue; }
                    o.WriteLine($"== {label} ({set.Family}): idle {set.Idle}; idle pitch (additive) down {set.IdleDown ?? "-"}, up {set.IdleUp ?? "-"}");
                    foreach (var d in set.Directions)
                        o.WriteLine($"  {d.AngleDegrees,5:0}° walk {d.Walk.Level} [{d.Walk.Down ?? "-"} {d.Walk.DownDegrees:0}° / {d.Walk.Up ?? "-"} {d.Walk.UpDegrees:0}°]; run {d.Run.Level} [{d.Run.Down ?? "-"} {d.Run.DownDegrees:0}° / {d.Run.Up ?? "-"} {d.Run.UpDegrees:0}°]");
                }
                o.WriteLine($"== sprint {bundle.Plan.Sprint ?? "-"} (blend in {bundle.Plan.SprintBlend:0.###}s); toss {bundle.Plan.Toss ?? "-"}");
                foreach (var n in bundle.Plan.Notes) o.WriteLine($"  note: {n}");
            }
            else gaits = GaitLibrary.Load(gameDir).Gaits;
            o.WriteLine($"== gaits of rig '{gaits.RigKey}': {gaits.Clips.Count} clips");
            foreach (var (clip, g) in gaits.Clips.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                o.WriteLine($"  {clip,-44} {g.Duration,5:0.00}s stride {g.Stride.X,6:0.00},{g.Stride.Y,6:0.00} m = {g.Speed,4:0.00} m/s  planted {g.PlantedFraction:P0}  sync L {g.SyncLeft:0.00} R {g.SyncRight:0.00}");
            foreach (var n in gaits.Notes) o.WriteLine($"  gait note: {n}");
            return 0;
        }
        var graphs = HavokBehaviorDetailReader.Read(pack);
        if (args[0] == "--hipfire")
        {
            var names = new HashSet<string>(character.AnimationNames, StringComparer.OrdinalIgnoreCase);
            var hip = HipFireAnimations.From(graphs, bones, clip => character.LoadAnimation(clip).Animation.Duration,
                clip => names.Contains(clip) ? character.LoadBlendHint(clip) : null, rig.HipFire, rig.BaseGraph);
            foreach (var set in hip.Sets) PrintHipFire(o, set);
            return 0;
        }
        if (args[0] == "--reload")
        {
            var reloads = ReloadAnimations.From(graphs, bones, clip => character.LoadAnimation(clip).Animation.Duration, rig.Reloads);
            foreach (var r in reloads.Clips) PrintReload(o, r);
            return 0;
        }
        if (args[0] == "--clip-table")
        {
            // The rig's clip table (RigProfile.Clips) against the clips the packfile has.
            var have = new HashSet<string>(character.AnimationNames, StringComparer.OrdinalIgnoreCase);
            string Mark(string clip) => $"{clip}{(have.Contains(clip) ? "" : " (MISSING)")}";
            string List(IEnumerable<string> clips) => clips.Any() ? string.Join(", ", clips.Select(Mark)) : "-";
            var t = rig.Clips;
            o.WriteLine($"== clip table of rig '{rig.Name}'");
            o.WriteLine($"  idle: {List(t.IdleCandidates)}");
            o.WriteLine($"  preview idle: {List(t.PreviewIdleCandidates)}");
            o.WriteLine($"  default: {Mark(t.DefaultAnimation)}");
            o.WriteLine($"  toss: {(t.TossClip is { } toss ? Mark(toss) : "-")}; recoil: {(t.RecoilClip is { } rc ? Mark(rc) : "-")}; grenade-launcher recoil: {(t.GrenadeLauncherRecoilClip is { } gl ? Mark(gl) : "-")}");
            foreach (var (key, a) in t.AimStances.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                o.WriteLine($"  aim {key,-16} down [{List(a.Down)}] level [{List(a.Level)}] up [{List(a.Up)}]{(a.AdditivePitch ? " (additive pitch)" : "")}");
            o.WriteLine($"  death: {List(t.DeathClips)}");
            foreach (var u in t.Unfilled) o.WriteLine($"  unfilled: {u}");
            return 0;
        }
        if (args[0] == "--locomotion")
        {
            var loco = LocomotionAnimations.From(graphs, clip => character.LoadAnimation(clip).Animation.Duration, rig.Relaxed, rig.Sprints, rig.BaseGraph);
            foreach (var set in loco.Relaxed)
            {
                o.WriteLine($"== relaxed run, {set.Class}: {set.Graph} / '{set.State}'");
                foreach (var d in set.Directions)
                    o.WriteLine($"  {d.Label,-3} {d.AngleDegrees,5:0}°  on the spot {(d.WalkOnSpot is { } wos ? $"{wos.Clip} ({wos.Length:0.###}s)" : "-")}; walk {(d.Walk is { } w ? $"{w.Clip} ({w.Length:0.###}s at {w.Speed:0.##})" : "-")}; run {(d.Run is { } r ? $"{r.Clip} ({r.Length:0.###}s at {r.Speed:0.##}{(r.SpeedBound ? ", playback speed bound" : "")})" : "-")}");
            }
            foreach (var sprint in loco.Sprints)
                o.WriteLine($"== sprint, {sprint.Family}: {sprint.Graph} / '{sprint.State}': " + string.Join(", ", sprint.Clips.Select(c => $"{c.AngleDegrees:+0;-0;0}° {c.Clip} ({c.Length:0.###}s)")) +
                            $"; playback speed variable {sprint.PlaybackVariable ?? "-"}; blend in {sprint.EnterBlend:0.###}s");
            foreach (var n in loco.Notes) o.WriteLine($"  note: {n}");
            return 0;
        }
        if (args[0] == "--brace")
        {
            // Fidelity gap 7: the heavy bolter brace's timing (BraceTuning) and the vent states' clips.
            var notes = new List<string>();
            if (VentAnimations.Read(graphs, notes) is { } vent)
                o.WriteLine("vent clips: " + string.Join(", ", vent.Clips.Select(kv => $"{kv.Key} {kv.Value.Clip}{(kv.Value.Additive ? " (additive)" : "")}{(kv.Value.Looping ? " (looping)" : "")}")));
            if (BraceTuning.Read(character, notes) is { } b)
            {
                o.WriteLine($"braced idle: up {b.Idle?.Up ?? "-"}, forward {b.Idle?.Forward ?? "-"}, down {b.Idle?.Down ?? "-"}; recoil {b.FireClip ?? "-"}; exit leaves to the legs: {string.Join(" ", b.ExitLegBones)}");
                o.WriteLine($"sound tags: ACTN_SND_HB_Brace_Enter at {b.EnterSoundAt:0.####}, ACTN_SND_HB_Brace_Exit at {b.ExitSoundAt:0.####} of their clips");
                // The transition clips' root motion (extracted motion samples: x, y, z, yaw): whether planting the feet moves the body.
                foreach (var clip in new[] { b.EnterClip, b.ExitClip })
                {
                    var motion = character.LoadExtractedMotion(clip);
                    if (motion is null || motion.Samples.Count == 0) { o.WriteLine($"root motion {clip}: none"); continue; }
                    var first = motion.Samples[0]; var last = motion.Samples[^1];
                    float path = 0f;
                    for (int i = 1; i < motion.Samples.Count; i++)
                        path += new System.Numerics.Vector3(motion.Samples[i].X - motion.Samples[i - 1].X, motion.Samples[i].Y - motion.Samples[i - 1].Y, motion.Samples[i].Z - motion.Samples[i - 1].Z).Length();
                    o.WriteLine($"root motion {clip}: {motion.Samples.Count} samples over {motion.Duration:0.###} s, net ({last.X - first.X:0.###}, {last.Y - first.Y:0.###}, {last.Z - first.Z:0.###}) m, path {path:0.###} m, turn {last.W - first.W:0.###}");
                }
                o.WriteLine($"entry: {b.EnterClip} {b.EnterLength:0.###}s, BRANCH_Idle_to_Brace at {b.EnterBranch:0.####} of the clip");
                o.WriteLine($"exit:  {b.ExitClip} {b.ExitLength:0.###}s, BRANCH_Exit_Brace at {b.ExitBranch:0.####} of the clip");
                o.WriteLine($"{BraceTuning.SpeedVariable}: initial {b.DefaultSpeed:0.###}; entry graph sets " + string.Join(", ", b.EntrySpeeds.Select(kv => $"{kv.Value:0.###} when {(kv.Key.Length > 0 ? kv.Key : "always")}"))
                    + "; brace graph sets " + string.Join(", ", b.ExitSpeeds.Select(kv => $"{kv.Value:0.###} when {(kv.Key.Length > 0 ? kv.Key : "always")}")));
                o.WriteLine($"seconds: entry {b.EnterSeconds(false):0.####} (Expertise {b.EnterSeconds(true):0.####}), exit {b.ExitSeconds(null, false):0.####} (Expertise {b.ExitSeconds(null, true):0.####}; used: the entry's speed)"
                    + $"; with the brace graph's fire-mode speed the exit would be {b.ExitSeconds("mp_fire_mode_heavy_bolter_braced_fire", false):0.####} (not used)");
            }
            foreach (var n in notes) o.WriteLine($"  note: {n}");
            return 0;
        }
        if (args[0] == "--melee")
        {
            // feel/melee-lock: each melee sequencer's alignment, distance and validation windows and its distance variants' root motion.
            string prefix = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : rig.Name == "ork" ? "Base_MELEE" : "MP_SM_MEL";
            var simple = HavokBehavior.Read(pack);
            foreach (var g in graphs.Where(g => g.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                o.WriteLine($"-- {g.Name}: FLT_MeleeTargetDistance initial value {BraceTuning.InitialValue(pack, g, "FLT_MeleeTargetDistance")?.ToString("0.###") ?? "-"}");
            foreach (var r in MeleeMotion.ReadAll(simple, prefix, clip => character.LoadAnimation(clip).Animation.Duration, character.LoadExtractedMotion))
            {
                o.WriteLine($"== {r.Graph} / {r.Sequencer}: {r.Length:0.###}s, hit {r.HitTime:0.###}s; align {r.Alignment.From:0.###}-{r.Alignment.To:0.###}s; distance {r.DistanceWindow.From:0.###}-{r.DistanceWindow.To:0.###}s d={r.Distance:0.##}; validation {r.Validation.From:0.###}-{r.Validation.To:0.###}s");
                foreach (var v in r.Variants)
                    o.WriteLine($"    {v.Clip,-40} name {v.Distance,4:0.#} m  forward at align end {v.ForwardAt(r.Alignment.To),6:0.00}  distance end {v.ForwardAt(r.DistanceWindow.To),6:0.00}  hit {v.ForwardAt(r.HitTime),6:0.00}  move ({r.MoveAt:0.###}s) {v.ForwardAt(r.MoveAt),6:0.00}  end {v.ForwardAt(v.Length),6:0.00} m");
            }
            return 0;
        }
        if (args[0] == "--twists")
        {
            // qc/reference-pass: every hkbTwistModifier (layout: Havok's hkbModifier then axisOfRotation @48 (vector4), twistAngle @64, startBoneIndex @68 and endBoneIndex @70
            // (int16), setAngleMethod @72, rotationAxisCoordinates @73, isAdditive @74 (bytes); the field order is Havok's declared one, the offsets read off the objects), its variable
            // bindings and every node that holds it (a modifier list or a modifier generator), up to the state it runs in.
            string? only = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
            foreach (var g in graphs)
            {
                if (only is not null && !g.Name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                var parents = new Dictionary<int, List<BehaviorNode>>();
                foreach (var n in g.Nodes.Values)
                    foreach (var c in n.Children)
                        if (c.Target is int t) { if (!parents.TryGetValue(t, out var l)) parents[t] = l = new(); l.Add(n); }
                var stateOf = new Dictionary<int, string>();
                foreach (var (m, s) in g.AllStates) if (s.Generator is int gen) stateOf.TryAdd(gen, $"{m.Name}/'{s.Name}'");
                string Path(int at)
                {
                    var chain = new List<string>();
                    var seen = new HashSet<int>();
                    int cur = at;
                    while (seen.Add(cur) && chain.Count < 12)
                    {
                        if (stateOf.TryGetValue(cur, out var st)) { chain.Add("state " + st); break; }
                        if (!parents.TryGetValue(cur, out var ps) || ps.Count == 0) break;
                        var p = ps[0];
                        chain.Add($"{p.Class.Replace("hkb", "")} '{p.Name}'{(ps.Count > 1 ? $" (+{ps.Count - 1} more holders)" : "")}");
                        cur = p.Offset;
                    }
                    return string.Join(" < ", chain);
                }
                foreach (var n in g.Nodes.Values.Where(n => n.Class == "hkbTwistModifier").OrderBy(n => n.Offset))
                {
                    int at = n.Offset;
                    float ax = pack.ReadSingle(at + 48), ay = pack.ReadSingle(at + 52), az = pack.ReadSingle(at + 56);
                    float angle = pack.ReadSingle(at + 64);
                    short sb = pack.ReadInt16(at + 68), eb = pack.ReadInt16(at + 70);
                    byte method = pack.ReadByte(at + 72), coords = pack.ReadByte(at + 73), additive = pack.ReadByte(at + 74), enable = pack.ReadByte(at + 40);
                    string Bone(short b) => b >= 0 && b < bones.Count ? bones[b] : b.ToString();
                    o.WriteLine($"== {g.Name} / '{n.Name}' @{at}: enable {enable}, axis ({ax:0.###}, {ay:0.###}, {az:0.###}), angle {angle:0.###}, bones {Bone(sb)} ({sb}) .. {Bone(eb)} ({eb}), " +
                        $"setAngleMethod {method} ({(method == 0 ? "linear" : method == 1 ? "ramped" : "?")}), axis coordinates {coords} ({(coords == 0 ? "model" : coords == 1 ? "local" : "?")}), additive {additive}");
                    foreach (var (k, v) in n.Bindings) o.WriteLine($"    binding {k} <- {v}");
                    o.WriteLine($"    held by: {Path(at)}");
                    if (parents.TryGetValue(at, out var all) && all.Count > 1)
                        foreach (var p in all.Skip(1)) o.WriteLine($"    also held by: {p.Class.Replace("hkb", "")} '{p.Name}' < {Path(p.Offset)}");
                }
            }
            return 0;
        }
        if (args[0] == "--turns")
        {
            var turns = TurnAnimations.From(character);
            foreach (var set in turns.Sets) PrintTurns(o, set);
            foreach (var n in turns.Notes) o.WriteLine($"  note: {n}");
            return 0;
        }
        if (args[0] == "--upper")
        {
            // Animation pass 2: the weapon switch, the hit flinches, the death clips and the jump pack's get-up and flight noise.
            var upper = UpperBodyBundle.From(graphs, bones, clip => character.LoadAnimation(clip).Animation.Duration);
            foreach (var (name, c) in new[] { ("to a rifle", upper.Switch.ToRifle), ("to a pistol", upper.Switch.ToPistol) })
                o.WriteLine(c is null ? $"== switch {name}: none" : $"== switch {name}: {c.Clip} ({c.Length:0.###}s), weapon swapped at {c.SwapAt:0.###}s, BranchIn {c.BranchIn:0.###}s, upper body (leg bones at 0: {string.Join(", ", c.Mask.Where(kv => kv.Value == 0f).Select(kv => kv.Key))})");
            foreach (var set in upper.Flinch.Sets.Values)
                o.WriteLine($"== flinch {set.Weapon}: " + string.Join(", ", set.Slots.Select((s, i) => s is null ? null : $"{i}={s.Clip}").Where(x => x is not null)));
            o.WriteLine($"== flinch: min_hit_reaction_time {upper.Flinch.MinHitReactionTime:0.###}s");
            foreach (var (kind, d) in upper.Death.Clips) o.WriteLine($"== death {kind}: {d.Clip} ({d.Length:0.###}s), ragdoll at {d.RagdollAt:0.###}s");
            var jp = upper.JumpPack;
            o.WriteLine($"== jump pack: get-up run {jp.RunClip} ({jp.RunLength:0.###}s) from {jp.FromImpact:0.###}s into the impact at speed >= {jp.MinSpeed:0.##}, blend {jp.Blend:0.###}s, ends at {jp.RunEnd:0.###}s; flight noise {jp.NoiseClip} ({jp.NoiseLength:0.###}s)");
            foreach (var n in upper.Switch.Notes.Concat(upper.Flinch.Notes).Concat(upper.Death.Notes).Concat(jp.Notes)) o.WriteLine($"  note: {n}");
            return 0;
        }
        if (args[0] == "--pose-diff" && args.Length > 2)
        {
            // Animation pass 3: how two clips' poses differ, bone by bone (model space, relative to the pelvis), over 11 samples of each (the same fraction of each clip).
            var sk = character.Skeleton;
            var a = character.LoadAnimation(args[1]); var b = character.LoadAnimation(args[2]);
            var la = new HavokTransform[bones.Count]; var lb = new HavokTransform[bones.Count];
            var worst = new float[bones.Count];
            for (int k = 0; k <= 10; k++)
            {
                for (int i = 0; i < bones.Count; i++) { la[i] = sk.ReferencePose[i]; lb[i] = sk.ReferencePose[i]; }
                var sa = a.Animation.Sample(MathF.Min(a.Animation.Duration * k / 10f, a.Animation.Duration - 1e-4f));
                var sb = b.Animation.Sample(MathF.Min(b.Animation.Duration * k / 10f, b.Animation.Duration - 1e-4f));
                for (int t = 0; t < sa.Length; t++) if ((uint)a.TrackToBone[t] < (uint)la.Length) la[a.TrackToBone[t]] = sa[t];
                for (int t = 0; t < sb.Length; t++) if ((uint)b.TrackToBone[t] < (uint)lb.Length) lb[b.TrackToBone[t]] = sb[t];
                var ma = sk.ModelSpace(la); var mb = sk.ModelSpace(lb);
                for (int i = 0; i < bones.Count; i++) worst[i] = MathF.Max(worst[i], (ma[i].Translation - mb[i].Translation).Length());
            }
            o.WriteLine($"== {args[1]} against {args[2]}: largest bone position difference (cm, model space) over 11 samples");
            for (int i = 0; i < bones.Count; i++) o.WriteLine($"  {bones[i],-24} {worst[i] * 100f,7:0.0}");
            return 0;
        }
        if (args[0] == "--sprint-edges")
        {
            // Animation pass 3: the sprint's start and stop clips, their blends, branches and root motion (speed profile), and the pistol's relaxed idle.
            var edges = SprintTransitions.From(graphs, clip => character.LoadAnimation(clip).Animation.Duration,
                clip => { try { return character.LoadExtractedMotion(clip); } catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return null; } });
            foreach (var set in edges.Sets)
                foreach (var (kind, e) in new[] { ("start", set.Start), ("stop", set.Stop) })
                {
                    if (e is null) { o.WriteLine($"== {set.Family}{(set.OneHanded ? " 1HM" : "")} {kind}: none"); continue; }
                    var (peak, peakAt) = e.Peak;
                    o.WriteLine($"== {set.Family}{(set.OneHanded ? " 1HM" : "")} {kind}: {e.Graph}/{e.State} {e.Clip} {e.Length:0.###}s, blend in {e.BlendIn:0.###}s, branch at {e.BranchAt:0.###}s, blend out {e.BlendOut:0.###}s; root {e.DistanceAt(e.Length):0.00} m, peak {peak:0.00} m/s at {peakAt:0.00}s, rest at {e.RestAt:0.00}s");
                    o.WriteLine("    speed every 0.1 s: " + string.Join(" ", Enumerable.Range(0, (int)(e.Length * 10) + 1).Select(k => $"{e.SpeedAt(k * 0.1f):0.0}")));
                }
            foreach (var set in edges.Sets) o.WriteLine($"== {set.Family}{(set.OneHanded ? " 1HM" : "")} ways out of the sprint: run on {set.RunOnBlend:0.###}s, at the stop's branch {set.StopExitBlend:0.###}s");
            o.WriteLine($"== pistol relaxed idle: {edges.PistolRelaxedIdle ?? "none"}");
            foreach (var n in edges.Notes) o.WriteLine($"  note: {n}");
            return 0;
        }
        if (args[0] == "--falls")
        {
            // Animation pass 1: the fall, its legs layer, the hard landing and the exits (and which wargear gives the short exit).
            var protectedNames = FallAnimations.Load(gameDir).ProtectedWargear;
            var falls = FallAnimations.From(graphs, bones, clip => character.LoadAnimation(clip).Animation.Duration, protectedWargear: protectedNames);
            foreach (var set in falls.Sets) PrintFall(o, set);
            o.WriteLine($"== mp_fall_protection (lifetime upgrade string) is applied by: {(falls.ProtectedWargear.Count == 0 ? "-" : string.Join(", ", falls.ProtectedWargear.Order(StringComparer.Ordinal)))}");
            foreach (var n in falls.Notes) o.WriteLine($"  note: {n}");
            return 0;
        }
        if (args[0] == "--jump-flight")
        {
            // M45: the flight clips are posed by the vertical angle of motion (user-controlled time); print the pose along the clip (model space,
            // relative to the pelvis) so which end rises and which falls can be read off.
            string pattern = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "SM_JMP_Zero_F_Aim_F";
            var sk = character.Skeleton;
            int Bone(string n) { for (int i = 0; i < bones.Count; i++) if (bones[i].Equals(n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
            int pelvis = Bone("RIG_Pelvis"), head = Bone("RIG_Head"), lt = Bone("RIG_L_Toe"), rt = Bone("RIG_R_Toe"), lc = Bone("RIG_L_Calf"), rc = Bone("RIG_R_Calf"),
                lh = Bone("RIG_L_Hand"), rh = Bone("RIG_R_Hand"), root = Bone("RIG_Root");
            foreach (var name in character.AnimationNames.Where(a => Matches(a, pattern)))
            {
                var clip = character.LoadAnimation(name);
                float duration = clip.Animation.Duration;
                o.WriteLine($"== {name} ({duration:0.###}s): bone - pelvis in model space (x, y, z), every 0.1 of the clip");
                var local = new HavokTransform[bones.Count];
                for (int k = 0; k <= 10; k++)
                {
                    for (int b = 0; b < local.Length; b++) local[b] = sk.ReferencePose[b];
                    var sampled = clip.Animation.Sample(MathF.Min(duration * k / 10f, duration - 1e-4f));
                    for (int t = 0; t < sampled.Length; t++) if ((uint)clip.TrackToBone[t] < (uint)local.Length) local[clip.TrackToBone[t]] = sampled[t];
                    var m = sk.ModelSpace(local);
                    var p = m[pelvis].Translation;
                    string V(int b) => b < 0 ? "-" : $"({m[b].Translation.X - p.X,5:0.00},{m[b].Translation.Y - p.Y,5:0.00},{m[b].Translation.Z - p.Z,5:0.00})";
                    o.WriteLine($"  {k / 10f:0.0}: pelvis ({p.X:0.00},{p.Y:0.00},{p.Z:0.00}) root {(root < 0 ? "-" : $"({m[root].Translation.X:0.00},{m[root].Translation.Y:0.00},{m[root].Translation.Z:0.00})")} head {V(head)} " +
                                $"toes L {V(lt)} R {V(rt)} calves L {V(lc)} R {V(rc)} hands L {V(lh)} R {V(rh)}");
                }
            }
            return 0;
        }
        if (args[0] == "--list")
        {
            foreach (var g in graphs)
                o.WriteLine($"{g.Name,-36} objects={g.ClassCounts.Values.Sum(),5} sequencers={g.Sequencers.Count,3} clips={g.Clips.Count,3} blends={g.Blenders.Count,3} masks={g.BoneWeights.Count,3} states={g.StateMachines.Sum(s => s.States.Count),3}");
            o.WriteLine($"graphs={graphs.Count}");
            o.WriteLine("packfile classes:");
            foreach (var (cls, n) in pack.Objects.GroupBy(x => x.ClassName).Select(x => (x.Key, x.Count())).OrderBy(x => x.Key, StringComparer.Ordinal))
                o.WriteLine($"  {n,6} {cls}");
            return 0;
        }

        var selected = graphs.Where(g => Matches(g.Name, args[0])).ToList();
        if (selected.Count == 0) { Console.Error.WriteLine($"No graph matches '{args[0]}' (try --list)."); return 1; }
        string? clipPattern = Opt(args, "--clips");
        string? raw = Opt(args, "--raw");
        bool tree = !args.Contains("--no-tree");
        foreach (var g in selected)
        {
            o.WriteLine($"== {g.Name}  (objects @{g.Start}..{g.End})");
            if (raw is not null) { Raw(o, pack, g.Start, g.End, g.Nodes, raw, int.TryParse(Opt(args, "--count"), out int n) ? n : 1000); continue; }
            o.WriteLine("classes: " + string.Join(", ", g.ClassCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key[3..]}={kv.Value}")));
            o.WriteLine("variables: " + string.Join(", ", g.Variables));
            o.WriteLine("events: " + string.Join(", ", g.Events));
            if (tree)
            {
                o.WriteLine("tree:");
                Tree(o, g, g.Root, "root", 1, new HashSet<int>(), clipPattern, Hint, Length);
            }
            var machines = g.StateMachines.Where(sm => sm.States.Any(s => s.Transitions.Count > 0) || sm.WildcardTransitions.Count > 0 || sm.StartChoices.Count > 0).ToList();
            if (machines.Count > 0) o.WriteLine("transitions:");
            foreach (var sm in machines)
            {
                o.WriteLine($"  '{sm.Name}' (start state {sm.StartStateId}{(sm.StartStateChooser is not null ? ", chooser" : "")})");
                string Effect(BehaviorTransition t) => t.Transition is int e && g.BlendEffects.FirstOrDefault(x => x.Offset == e) is { } be ? $" blend '{be.Name}' {be.Duration:0.###}s" : "";
                string State(int id) => sm.States.FirstOrDefault(s => s.Id == id)?.Name ?? $"#{id}";
                foreach (var (id, cond) in sm.StartChoices)
                    o.WriteLine($"    start in {State(id)} if {cond ?? "(always)"}");
                foreach (var s in sm.States)
                    foreach (var t in s.Transitions)
                        o.WriteLine($"    {s.Name} --{Trigger(t)}--> {State(t.ToStateId)}{Effect(t)}");
                foreach (var t in sm.WildcardTransitions)
                    o.WriteLine($"    * --{Trigger(t)}--> {State(t.ToStateId)}{Effect(t)}");
            }
            foreach (var m in g.BoneWeights)
            {
                var users = g.MaskedChildren.Where(x => x.Child.BoneWeights == m.Offset).Select(x => $"'{x.Blend.Name}'").Distinct();
                var nonzero = m.Weights.Select((w, i) => (w, i)).Where(x => x.w != 0f).ToList();
                o.WriteLine($"bone mask @{m.Offset} ({m.Weights.Count} entries, {nonzero.Count} non-zero; used by {string.Join(", ", users)}):");
                o.WriteLine("    " + (nonzero.Count == 0 ? "(all zero)" : string.Join(", ", nonzero.Select(x => $"{(x.i < bones.Count ? bones[x.i] : $"#{x.i}")}={x.w:0.###}"))));
            }
            foreach (var s in g.Sequencers.Values)
            {
                if (clipPattern is not null && !s.Animations.Any(a => Matches(a, clipPattern))) continue;
                o.WriteLine($"sequencer '{s.Name}'");
                for (int i = 0; i < s.Animations.Count; i++)
                    o.WriteLine($"    clip {s.Animations[i]} {Length(s.Animations[i])} speed={(i < s.PlaybackSpeeds.Count ? s.PlaybackSpeeds[i] : 1f):0.###} {Hint(s.Animations[i])}");
                foreach (var t in s.Tags)
                    o.WriteLine($"    tag {t.Kind,-16} {t.Start:0.####}-{t.End:0.####} {t.Name}{(t.Distance is float d ? $" d={d}" : "")}{(t.Mode is int md ? $" mode={md}" : "")}");
            }
        }
        return 0;
    }

    static bool ShowZeros;

    static void PrintHipFire(TextWriter o, HipFireSet s)
    {
        o.WriteLine($"== {s.Class}: {s.NavGraph} / '{s.NavState}', recoil from {s.FireGraph}");
        foreach (var d in s.Directions)
            foreach (var b in d.Bands)
                o.WriteLine($"  {d.Label,-3} {d.AngleDegrees,5:0}°  speed {b.Speed,4:0.##}: " + string.Join(", ", b.Clips.Select(c => $"{c.PitchDegrees:+0;-0;0}° {c.Clip} ({c.Length:0.###}s)")));
        if (s.Recoil is { } r)
            o.WriteLine($"  recoil: {r.Clip} ({r.Length:0.###}s, speed {r.PlaybackSpeed:0.##}, {(r.Additive ? "additive" : "normal")}) in '{r.Sequencer}'; shot finished at {r.ShotFinished:0.###}s, refire from {r.RefireFrom?.ToString("0.###") ?? "-"}s");
        o.WriteLine($"  raise delay {s.RaiseDelay?.ToString("0.###") ?? "-"}s; blend nav->fire {s.NavToFireBlend?.ToString("0.###") ?? "-"}s, fire->nav {s.FireToNavBlend?.ToString("0.###") ?? "-"}s; sprint->fire: {s.SprintToFire ?? "-"}");
        o.WriteLine($"  ready posture: {s.ReadyCondition ?? "(always)"}; relaxed->ready {s.RelaxedToReadyBlend?.ToString("0.###") ?? "-"}s, ready->relaxed {s.ReadyToRelaxedBlend?.ToString("0.###") ?? "-"}s");
        o.WriteLine($"  fire mask: {(s.FireMask is null ? "none" : "yes")}; upper-body (reload) mask: " +
            (s.UpperBodyMask is { } m ? string.Join(", ", m.Where(kv => kv.Value != 1f).Select(kv => $"{kv.Key}={kv.Value:0.##}")) + " (others 1)" : "none"));
        foreach (var n in s.Notes) o.WriteLine($"  note: {n}");
    }

    static void PrintFall(TextWriter o, FallSet set)
    {
        o.WriteLine($"== fall, {set.Family}: {set.Graph}; enters over {set.EnterBlend:0.###}s{(set.EnterHeight > 0f ? $" at {set.EnterHeight:0.##} m above the ground" : " when unsupported")}");
        if (set.Legs is { } legs)
            o.WriteLine($"  legs: {legs.Clip} ({legs.Length:0.###}s, looping) on {(legs.Mask is { } m ? string.Join(", ", m.Where(kv => kv.Value != 0f).Select(kv => $"{kv.Key}={kv.Value:0.##}")) : "the whole body")}");
        if (set.Drop is { } drop) o.WriteLine($"  drop: {drop.Clip} ({drop.Length:0.###}s, once, the whole body)");
        o.WriteLine($"  land: {set.Land.Clip} ({set.Land.Length:0.###}s) over {set.LandBlend:0.###}s at {(set.HardFallSpeed > 0f ? $"fall speed {set.HardFallSpeed:0.##} m/s" : "any speed")}, {(set.LandHeight > 0f ? $"{set.LandHeight:0.##} m above the ground" : "touchdown")}; a softer fall returns over {set.SoftExitBlend:0.###}s");
        o.WriteLine($"  exit: from {set.Exit.At:0.###}s, blend {set.Exit.Blend:0.###}s; with fall protection from {set.ProtectedExit.At:0.###}s, blend {set.ProtectedExit.Blend:0.###}s");
    }

    static void PrintTurns(TextWriter o, TurnSet set)
    {
        o.WriteLine($"== {set.Class}");
        foreach (var p in new[] { set.Relaxed, set.Ready })
        {
            if (p is null) continue;
            o.WriteLine($"  {p.Name} posture: {p.Graph} / '{p.IdleState}', turns when the aim is more than {p.EnterDegrees:0.#}° off the body (blend {p.TurnBlend:0.###}s), stop blend {p.StopBlend:0.###}s; {(p.Idle.ProceduralTwist ? "upper-body twist by a twist modifier" : "no twist modifier")}");
            foreach (var c in p.Idle.Columns)
                o.WriteLine($"    aim {c.HorizontalDegrees,4:0}°: " + string.Join(", ", c.Pitches.Select(a => $"{a.PitchDegrees:+0;-0;0}° {a.Clip}")));
            foreach (var t in new[] { p.Left, p.Right })
                if (t is not null)
                    o.WriteLine($"    turn {t.Clip} ({t.Length:0.###}s), root motion {t.YawDegrees:+0.#;-0.#}°, BRANCH_Turn_End from {t.EndFraction:0.###}; leg mask: " +
                                (t.LegMask is { } m ? string.Join(", ", m.Where(kv => kv.Value != 0f).Select(kv => $"{kv.Key}={kv.Value:0.##}")) + " (others 0)" : "none"));
        }
        foreach (var s in new[] { set.RelaxedStart, set.ReadyStart })
            if (s is not null)
                o.WriteLine($"  start {s.Graph} / '{s.State}': {s.Clip} ({s.Length:0.###}s), from requested speed {s.EnterSpeed:0.#}; blend in {s.BlendIn:0.###}s, out {s.BlendOut:0.###}s, abort {s.BlendAbort:0.###}s; ends at {s.EndFraction:0.###}; root motion {s.RootForward[^1]:0.##} m in {s.RootDuration:0.###}s");
    }

    static void PrintReload(TextWriter o, ReloadClip r)
    {
        o.WriteLine($"== {r.Family}: {r.Graph} plays {r.Clip} ({r.ClipLength:0.###}s), duration enforced by {r.EnforcedDuration ?? "(no: own length)"}");
        o.WriteLine($"  magazine out {r.MagazineOut?.ToString("0.####") ?? "-"}, in {r.MagazineIn?.ToString("0.####") ?? "-"}, ammunition event {r.AmmunitionAt?.ToString("0.####") ?? "-"} (fractions of the clip); blend in {r.BlendIn?.ToString("0.###") ?? "-"}s, out {r.BlendOut?.ToString("0.###") ?? "-"}s");
        o.WriteLine("  tags: " + string.Join(", ", r.Tags.Select(t => $"{t.Kind}:{t.Name}@{t.Start:0.####}")));
        o.WriteLine("  reload layer bone mask: " + (r.BoneMask is { } m ? string.Join(", ", m.Where(kv => kv.Value != 1f).Select(kv => $"{kv.Key}={kv.Value:0.##}")) + " (others 1)" : "none"));
        foreach (var n in r.Notes) o.WriteLine($"  note: {n}");
    }

    static string Trigger(BehaviorTransition t)
    {
        var parts = new[] { t.Event is { } e ? "on " + e : null, t.ConditionText is { } c ? "if " + c : null, t.BranchEvent is { } b ? "branch " + b : null }
            .OfType<string>().ToList();
        return (parts.Count > 0 ? string.Join(" ", parts) : "(no trigger)") + (t.Priority != 0 ? $" [priority {t.Priority}]" : "");
    }

    const int MaxTreeDepth = 40;

    static void Tree(TextWriter o, BehaviorGraphDetail g, int? at, string label, int depth, HashSet<int> path, string? clipPattern,
        Func<string, string> hint, Func<string, string> length)
    {
        string pad = new(' ', 2 * depth);
        if (at is not int node) { o.WriteLine($"{pad}[{label}] null"); return; }
        if (!g.Nodes.TryGetValue(node, out var n)) { o.WriteLine($"{pad}[{label}] @{node} (outside this graph)"); return; }
        if (depth > MaxTreeDepth || !path.Add(node)) { o.WriteLine($"{pad}[{label}] {n.Class[3..]} '{n.Name}' (repeated)"); return; }
        var sb = new StringBuilder($"{pad}[{label}] {n.Class[3..]} '{n.Name}'");
        if (g.Blenders.FirstOrDefault(b => b.Offset == node) is { } blend)
            sb.Append($" param={blend.BlendParameter:0.###} flags=0x{blend.Flags:X}{(blend.SubtractLastChild ? " subtract-last" : "")}");
        var clip = g.Clips.FirstOrDefault(c => c.Offset == node);
        if (clip is not null)
            sb.Append($" {clip.Animation} {length(clip.Animation)} speed={clip.PlaybackSpeed:0.###} mode={clip.Mode}{(clip.CropStart != 0f || clip.CropEnd != 0f || clip.StartTime != 0f ? $" crop={clip.CropStart:0.####}/{clip.CropEnd:0.####} start={clip.StartTime:0.####}" : "")} {hint(clip.Animation)}");
        if (n.Bindings.Count > 0) sb.Append(" bind{" + string.Join(", ", n.Bindings.Select(kv => $"{kv.Key}={kv.Value}")) + "}");
        bool show = clipPattern is null || clip is null || Matches(clip.Animation, clipPattern);
        if (show) o.WriteLine(sb);
        foreach (var c in n.Children) Tree(o, g, c.Target, c.Label, depth + 1, path, clipPattern, hint, length);
        path.Remove(node);
    }

    static bool Matches(string name, string pattern)
    {
        if (pattern is "*" or "all") return true;
        if (!pattern.Contains('*')) return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        var parts = pattern.Split('*');
        int at = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            int found = name.IndexOf(parts[i], at, StringComparison.OrdinalIgnoreCase);
            if (found < 0 || (i == 0 && found != 0)) return false;
            at = found + parts[i].Length;
        }
        return parts[^1].Length == 0 || name.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase);
    }

    static void Raw(TextWriter o, HavokPackfile pack, int start, int end, IReadOnlyDictionary<int, BehaviorNode>? names, string cls, int max)
    {
        var all = pack.Objects.OrderBy(x => x.Offset).ToList();
        var classAt = new Dictionary<int, string>();
        foreach (var x in all) classAt.TryAdd(x.Offset, x.ClassName);
        int shown = 0;
        for (int i = 0; i < all.Count && shown < max; i++)
        {
            var obj = all[i];
            if (obj.Offset < start || obj.Offset >= end || obj.ClassName != cls) continue;
            int size = Math.Min(i + 1 < all.Count ? all[i + 1].Offset - obj.Offset : 64, 256);
            shown++;
            var sb = new StringBuilder($"{cls} @{obj.Offset} (next object +{size})");
            for (int f = 0; f + 4 <= size; f += 4)
            {
                int v = pack.ReadInt32(obj.Offset + f);
                int? p = pack.Pointer(obj.Offset + f);
                if (v == 0 && p is null && !ShowZeros) continue;   // zero words are left out unless --zeros
                sb.Append($"\n  +{f,-3} ");
                if (p is int target)
                {
                    if (classAt.TryGetValue(target, out var tc))
                        sb.Append($"-> {tc} @{target}{(names is not null && names.TryGetValue(target, out var nm) ? $" '{nm.Name}'" : "")}");
                    else
                    {
                        string? s = pack.String(obj.Offset + f);
                        bool printable = s is { Length: > 0 and < 80 } && s.All(ch => ch >= 32 && ch < 127);
                        sb.Append(printable ? $"-> \"{s}\"" : $"-> data @{target}");
                        int count = f + 8 <= size ? pack.ReadInt32(obj.Offset + f + 4) : 0;
                        if (count is > 0 and < 4096) sb.Append($" (if array: {count} items)");
                    }
                }
                else
                {
                    float fl = BitConverter.Int32BitsToSingle(v);
                    sb.Append($"0x{v:X8} {v}");
                    if (float.IsFinite(fl) && Math.Abs(fl) is > 1e-4f and < 1e6f) sb.Append($" f={fl:0.####}");
                }
            }
            o.WriteLine(sb);
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              smtool graph-dump --list [--game <dir>] [--rig marine|ork] [--archive <oppc> --entry <hkxx>]
                                                        graphs in a character packfile, with packfile class counts
                                                        (--rig: the rig profile's packfile and graph names; default marine)
              smtool graph-dump <graph|pattern*> [--clips <pattern*>] [--no-tree] [--raw <class> [--count n]]
                                                        generator tree (state machines, blends with weights and bone
                                                        masks), transitions, masks by bone, sequencers with clips, tags
              smtool graph-dump --animations [<pattern*>]
                                                        every animation with its length and binding blend hint
              smtool graph-dump --hipfire               the hip-fire animation data per weapon family (SmContent.HipFireAnimations)
              smtool graph-dump --reload                the reload clips, tags, masks and blends per family (SmContent.ReloadAnimations)
              smtool graph-dump --locomotion            the relaxed run (eight directions, walk and run clips) and the sprint loops (SmContent.LocomotionAnimations)
              smtool graph-dump --falls                 the fall (legs layer), the hard landing and the exits per family, and the wargear with mp_fall_protection (SmContent.FallAnimations)
              smtool graph-dump --melee [<graph prefix>]  each melee sequencer's alignment / distance windows and its distance variants' root motion (SmContent.MeleeMotion)
              smtool graph-dump --turns                the standing aim-offset idle, the turn-in-place clips and the run-start clip per weapon family (SmContent.TurnAnimations)
              smtool graph-dump --root-motion [<clip> ...]   a clip's root motion samples (default: the sprint stops SM_NAV_Bullrush2Idle_F_01 / _HalfSpeed_01)
              smtool graph-dump --clip-table            the rig profile's clip table (SmContent.RigClipTable), each clip checked against the packfile
              smtool graph-dump --raw-any <class> [--count n]
                                                        annotated words of any packfile object of a class
            """);
        return 2;
    }

    static string? Opt(string[] rest, string name)
    {
        int i = Array.IndexOf(rest, name);
        return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
    }
}
