using System.Globalization;
using System.Text.RegularExpressions;
using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>The weapon families whose navigation graphs hold a different fall: the rifle graph (<c>SM_Bolter_NAV</c>, which the bolt pistol and the rest of the multiplayer weapons use here) and the heavy bolter's (<c>SM_TB_NAV</c>).</summary>
public enum FallFamily { Rifle, Heavy }

/// <summary>A clip of a fall state: its name, length in seconds at the graph's playback speed and, for a layered clip, the bone weights it is blended in with (null: the whole body).</summary>
public sealed record FallClip(string Clip, float Length, IReadOnlyDictionary<string, float>? Mask);

/// <summary>When the navigation takes over from a landing clip: <paramref name="At"/> seconds into the clip (its <c>BRANCH_To_*</c> window opens) and the blend into the navigation state (<paramref name="Blend"/>, seconds).</summary>
public readonly record struct FallExit(float At, float Blend);

/// <summary>
/// One family's fall, read from its navigation graph (M-anim P1; <see cref="FallAnimations"/>).
/// <list type="bullet">
/// <item><b>Rifle</b> (<c>SM_Bolter_NAV</c>, top machine <c>Behavior00</c>): the stand states go to "Fall Behaviors" over <see cref="EnterBlend"/> (0.167 s, 'To Fall') when <c>FLT_Fall_Speed</c> is at or under
/// -20 or the character is not supported (<c>!BOOL_IsSupported</c>: any time in the air, no height). Inside, "Dropping" loops <see cref="Legs"/> (<c>SM_ACT_B_button_drop_fall_01</c>) on the legs only
/// (the 14-bone mask: root, interaction, pelvis, thighs, calves, feet, toes, knee pads) while the upper body keeps the standing aim-offset idle. On the ground a fall that was not hard
/// (fall speed over -20: <see cref="HardFallSpeed"/>) returns to the stand over <see cref="SoftExitBlend"/> (0.1 s); a hard one goes to "Land" (<see cref="Land"/>, <c>SM_ACT_B_button_drop_land_01</c>, the whole body) over
/// <see cref="LandBlend"/> (0.1 s) and leaves at <see cref="Exit"/> (<c>BRANCH_To_Run</c> 0.635 s, 0.333 s blend) or, with the upgrade <c>mp_fall_protection</c>, at <see cref="ProtectedExit"/> (<c>BRANCH_To_Run_MP</c> 0.167 s, 0.267 s blend).</item>
/// <item><b>Heavy</b> (<c>SM_TB_NAV</c>, machine <c>Behavior10</c>): "Heavy Drop Down" starts at <see cref="EnterHeight"/> (1 m) above the ground and plays <see cref="Drop"/> (<c>SM_NAV_TB_Dropdown</c>, once, the whole body); at
/// <see cref="LandHeight"/> (0.5 m) it goes to "Land" (<c>SM_NAV_TB_Landing</c>) whatever the speed (<see cref="HardFallSpeed"/> is 0), leaving at <c>BRANCH_To_Nav</c> (0.833 s) or <c>BRANCH_To_NAV_MP</c> (0.233 s).</item>
/// </list>
/// </summary>
public sealed record FallSet(FallFamily Family, string Graph, FallClip? Legs, FallClip? Drop, FallClip Land, float EnterBlend, float LandBlend, float SoftExitBlend,
    float HardFallSpeed, float EnterHeight, float LandHeight, FallExit Exit, FallExit ProtectedExit);

/// <summary>
/// The camera shake of a <c>camera_effect</c> command (the player blueprint's landing): <see cref="Duration"/> seconds at full strength then <see cref="EaseOut"/> seconds fading, along the
/// camera's horizontal and vertical axes (and pitch, yaw, roll) as sine waves of the given amplitudes and frequencies (Hz). Units of the amplitudes are not stated in the file (interpretation: metres for the
/// two position axes, degrees for the three angles).
/// </summary>
public sealed record CameraShakeSpec(float Duration, float EaseIn, float EaseOut, (float Amplitude, float Frequency) Horizontal, (float Amplitude, float Frequency) Vertical,
    (float Amplitude, float Frequency) Pitch, (float Amplitude, float Frequency) Yaw, (float Amplitude, float Frequency) Roll)
{
    /// <summary>The envelope (0 to 1) at <paramref name="time"/> seconds after the effect began.</summary>
    public float Envelope(float time)
    {
        if (!(time >= 0f)) return 0f;
        float rise = EaseIn > 1e-4f ? Math.Min(1f, time / EaseIn) : 1f;
        float fall = time <= Duration ? 1f : EaseOut > 1e-4f ? Math.Max(0f, 1f - (time - Duration) / EaseOut) : 0f;
        return rise * fall;
    }

    /// <summary>The whole effect's length, seconds.</summary>
    public float Total => Duration + EaseOut;

    /// <summary>The camera offset (horizontal, vertical) at <paramref name="time"/> seconds: amplitude x envelope x sine.</summary>
    public (float Horizontal, float Vertical) OffsetAt(float time)
    {
        float e = Envelope(time);
        return (Horizontal.Amplitude * e * MathF.Sin(2f * MathF.PI * Horizontal.Frequency * time), Vertical.Amplitude * e * MathF.Sin(2f * MathF.PI * Vertical.Frequency * time));
    }
}

/// <summary>What the player blueprint's <c>finish_falling_actions</c> does when a fall ends: the effects it plays (<c>play_fx</c>: <c>jump_land</c> at scale 0.5), its camera shake, and its sound event (not used here: sounds are the sound system's).</summary>
public sealed record FallLanding(IReadOnlyList<FxPlay> Plays, CameraShakeSpec? Shake, string? SoundEvent)
{
    public static readonly FallLanding None = new([], null, null);

    /// <summary>Reads the landing of a player blueprint (<c>finish_falling_actions</c> under any of its extensions); <see cref="None"/> when it has none.</summary>
    public static FallLanding Read(BafDocument blueprint)
    {
        BafTable? actions = null;
        foreach (var ext in blueprint.Root.Table("extensions")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
            if (ext.AsTable().Table("finish_falling_actions") is { } found) { actions = found; break; }
        if (actions?.Table("commands") is not { } commands) return None;
        var plays = new List<FxPlay>();
        CameraShakeSpec? shake = null;
        string? sound = null;
        foreach (var e in commands.Entries.Where(e => e.Type == BafType.Table))
        {
            var t = e.AsTable();
            switch (e.Name)
            {
                case "play_fx": plays.Add(FxPlay.Read(t)); break;
                case "camera_effect" when shake is null: shake = ReadShake(t); break;
                case "post_sound_event" when sound is null: sound = t.String("start_event_name"); break;
            }
        }
        return new FallLanding(plays, shake, sound);
    }

    static CameraShakeSpec? ReadShake(BafTable camera)
    {
        if (camera.Table("shake_info") is not { } info) return null;
        (float, float) Axis(string name) => info.Table(name) is { } a ? (a.Float("amplitude") ?? 0f, a.Float("frequency") ?? 0f) : (0f, 0f);
        float duration = camera.Float("duration") ?? 0f, easeIn = camera.Float("ease_in") ?? 0f, easeOut = camera.Float("ease_out") ?? 0f;
        var spec = new CameraShakeSpec(duration, easeIn, easeOut, Axis("horizontal"), Axis("vertical"), Axis("pitch"), Axis("yaw"), Axis("roll"));
        bool any = new[] { spec.Horizontal, spec.Vertical, spec.Pitch, spec.Yaw, spec.Roll }.Any(a => a.Item1 != 0f);
        return any && float.IsFinite(spec.Total) && spec.Total > 0f ? spec : null;
    }
}

/// <summary>
/// The fall animations of the marine rig, from the navigation graphs (M-anim P1, <c>smtool graph-dump SM_Bolter_NAV</c> / <c>SM_TB_NAV</c>; read-only): see <see cref="FallSet"/>. Also the wargear files whose
/// <c>lifetime_actions</c> apply the upgrade string <c>mp_fall_protection</c> (<see cref="ProtectedWargear"/>: the three core evade abilities, so every multiplayer class has it; the graphs' short landing exit needs it).
/// Which fall belongs to the pistol and melee weapons is not in the data (the pistol graph's drop states exist only for the chainsword and the thunder hammer), so the client shows them the rifle fall (interpretation).
/// </summary>
public sealed record FallAnimations(IReadOnlyList<FallSet> Sets, IReadOnlySet<string> ProtectedWargear, IReadOnlyList<string> Notes)
{
    /// <summary>Where a family's fall states sit in its navigation graph: the state of the stand machine that holds the fall, its inner "dropping" and "land" states.</summary>
    public sealed record FallMap(FallFamily Family, string Graph, string FallState, string DropState, string LandState);

    public static readonly FallMap[] DefaultMaps =
    [
        new(FallFamily.Rifle, "SM_Bolter_NAV", "Fall Behaviors", "Dropping", "Land"),
        new(FallFamily.Heavy, "SM_TB_NAV", "Heavy Drop Down", "Drop", "Land"),
    ];

    /// <summary>The upgrade string whose <c>lifetime_actions</c> shortens the landing recovery.</summary>
    public const string FallProtection = "mp_fall_protection";

    public FallSet? For(FallFamily family) => Sets.FirstOrDefault(s => s.Family == family);

    /// <summary>The wargear whose lifetime actions apply <see cref="FallProtection"/> in a set of wargear files by name.</summary>
    public static HashSet<string> ProtectedIn(IEnumerable<KeyValuePair<string, BafDocument>> wargear)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in wargear)
            if (doc.Root.Table("action_data/lifetime_actions/commands/apply_upgrade_string/strings")?.Entries.Any(e => e.Type == BafType.String && e.AsString() == FallProtection) == true)
                names.Add(name);
        return names;
    }

    /// <summary>Reads the rig's graphs and the core ability wargear from the install.</summary>
    public static FallAnimations Load(string gameDir)
    {
        var character = RigProfiles.Marine.LoadCharacter(gameDir);
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        string package = Path.Combine(gameDir, GameplayTuning.WargearPackage);
        if (File.Exists(package))
            using (var archive = ObpkArchive.Open(package))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName.Contains("core_abil", StringComparison.OrdinalIgnoreCase), (e, d) => wargear[e.FileName] = BafDocument.Read(d));
        return From(HavokBehaviorDetailReader.Read(character.Pack), character.Skeleton.BoneNames, clip => character.LoadAnimation(clip).Animation.Duration, protectedWargear: ProtectedIn(wargear));
    }

    /// <summary>The data from already-read graphs (testable without an install).</summary>
    public static FallAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames, Func<string, float> animationLength,
        IEnumerable<FallMap>? maps = null, IReadOnlySet<string>? protectedWargear = null)
    {
        var notes = new List<string>();
        var sets = new List<FallSet>();
        foreach (var map in maps ?? DefaultMaps)
            if (ReadSet(map, graphs, boneNames, animationLength, notes) is { } set) sets.Add(set);
        return new FallAnimations(sets, protectedWargear ?? new HashSet<string>(), notes);
    }

    static readonly Regex FallSpeedRegex = new(@"FLT_Fall_Speed in \[(-?[0-9.]+), (-?[0-9.]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex HeightRegex = new(@"FLT_Dist\w*_Above_Ground in \[(-?[0-9.]+), (-?[0-9.]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static (float Lo, float Hi)? Range(Regex regex, string? text)
    {
        if (text is null) return null;
        var m = regex.Match(text);
        return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) && float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi) ? (lo, hi) : null;
    }

    static FallSet? ReadSet(FallMap map, IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames, Func<string, float> length, List<string> notes)
    {
        string where = $"{map.Graph}/{map.FallState}";
        if (graphs.FirstOrDefault(x => x.Name.Equals(map.Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {map.Graph} not found"); return null; }
        var fall = g.AllStates.FirstOrDefault(x => x.State.Name == map.FallState);
        if (fall.State is null) { notes.Add($"{where}: no such state"); return null; }
        // The inner machine (the state itself for the rifle, under a modifier for the heavy bolter).
        BehaviorStateMachine? inner = null;
        foreach (var at in Walk(g, fall.State.Generator))
            if (g.MachineAt(at) is { } m) { inner = m; break; }
        var drop = inner?.States.FirstOrDefault(s => s.Name == map.DropState);
        var land = inner?.States.FirstOrDefault(s => s.Name == map.LandState);
        if (inner is null || drop is null || land is null) { notes.Add($"{where}: no '{map.DropState}' / '{map.LandState}' states"); return null; }

        // The drop state's clip: on the legs only (the rifle: a blend child with a bone mask and a sequencer) or the whole body (the heavy bolter: a plain clip).
        FallClip? legs = null, dropClip = null;
        foreach (var at in Walk(g, drop.Generator))
        {
            if (g.BlenderAt(at) is not { } blend) continue;
            foreach (var child in blend.Children)
            {
                if (g.BoneWeightsAt(child.BoneWeights) is not { } weights) continue;
                if (Walk(g, child.Generator).Select(n => g.Sequencers.TryGetValue(n, out var s) ? s : null).FirstOrDefault(s => s is { Animations.Count: > 0 }) is not { } seq) continue;
                var mask = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int i = 0; i < boneNames.Count; i++) mask[boneNames[i]] = weights.WeightOf(i);
                legs = new FallClip(seq.Animations[0], length(seq.Animations[0]) / Speed(seq), mask);
                break;
            }
            if (legs is not null) break;
        }
        if (legs is null)
        {
            if (HipFireAnimations.FirstClip(g, drop.Generator) is { } plain) dropClip = new FallClip(plain.Animation, length(plain.Animation) / (plain.PlaybackSpeed > 0f ? plain.PlaybackSpeed : 1f), null);
            else { notes.Add($"{where}: no clip in '{map.DropState}'"); return null; }
        }

        var landSeq = Walk(g, land.Generator).Select(n => g.Sequencers.TryGetValue(n, out var s) ? s : null).FirstOrDefault(s => s is { Animations.Count: > 0 });
        if (landSeq is null) { notes.Add($"{where}: no clip in '{map.LandState}'"); return null; }
        string landName = landSeq.Animations[0];
        float landLength = length(landName) / Speed(landSeq);

        // Blends and conditions: the stand state going to the fall, the drop to the land, the fall state's exits.
        float enter = fall.Machine.States.SelectMany(s => s.Transitions).Where(t => t.ToStateId == fall.State.Id).Select(t => g.EffectAt(t.Transition)?.Duration).FirstOrDefault(d => d is not null) ?? 0.1f;
        var intoLand = drop.Transitions.FirstOrDefault(t => t.ToStateId == land.Id);
        float landBlend = intoLand is null ? 0.1f : g.EffectAt(intoLand.Transition)?.Duration ?? 0.1f;
        float hard = Range(FallSpeedRegex, intoLand?.ConditionText) is { } speed && speed.Hi < 0f ? -speed.Hi : 0f;
        float landHeight = Range(HeightRegex, intoLand?.ConditionText) is { } landRange ? landRange.Hi : 0.1f;
        var enterTransition = fall.Machine.States.SelectMany(s => s.Transitions).FirstOrDefault(t => t.ToStateId == fall.State.Id);
        float enterHeight = Range(HeightRegex, enterTransition?.ConditionText) is { } enterRange ? enterRange.Lo : 0f;
        float soft = 0.1f;
        foreach (var t in fall.State.Transitions)
            if (t.BranchEvent is null && t.ConditionText is { } c && HeightRegex.IsMatch(c) && g.EffectAt(t.Transition) is { } e) { soft = e.Duration; break; }

        FallExit ExitOf(bool upgraded)
        {
            // The exit transitions are branch-event transitions on the fall state: BRANCH_To_Run / BRANCH_To_Nav (always), the same with an _MP suffix (needs mp_fall_protection).
            var t = fall.State.Transitions.FirstOrDefault(x => x.BranchEvent is { } b && b.StartsWith("BRANCH_To_", StringComparison.OrdinalIgnoreCase) && b.EndsWith("_MP", StringComparison.OrdinalIgnoreCase) == upgraded);
            if (t?.BranchEvent is not { } branch)
            {
                notes.Add($"{where}: no {(upgraded ? "protected " : "")}exit branch; the clip's end");
                return new FallExit(landLength, 0.1f);
            }
            float start = landSeq.Tags.Where(x => x.Kind == "Branch" && x.Name is { } n && n.Equals(branch, StringComparison.OrdinalIgnoreCase)).Select(x => (float?)x.Start).Min() ?? 1f;
            return new FallExit(Math.Clamp(start, 0f, 1f) * landLength, g.EffectAt(t.Transition)?.Duration ?? 0.1f);
        }

        return new FallSet(map.Family, map.Graph, legs, dropClip, new FallClip(landName, landLength, null), enter, landBlend, soft, hard, enterHeight, landHeight, ExitOf(false), ExitOf(true));
    }

    static float Speed(BehaviorSequencer seq) => seq.PlaybackSpeeds.Count > 0 && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;

    static IEnumerable<int> Walk(BehaviorGraphDetail g, int? node)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<(int At, int Depth)>();
        if (node is int start) stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (at, depth) = stack.Pop();
            if (!g.Nodes.TryGetValue(at, out var n) || !seen.Add(at)) continue;
            yield return at;
            if (depth >= 16) continue;
            for (int i = n.Children.Count - 1; i >= 0; i--)
                if (n.Children[i].Target is int c) stack.Push((c, depth + 1));
        }
    }
}

/// <summary>
/// The rules around the fall animations that need no engine (M-anim P1; client presentation, the numbers are the graphs' unless a member says ours; tested).
/// </summary>
public static class FallRules
{
    /// <summary>A marine counts as falling after it has been unsupported this long (ours: two simulation ticks; the graph reacts at once, but the ground snap and the stairs keep a marine unsupported for a tick or two at a crest).</summary>
    public const float DebounceSeconds = 2f / 30f;

    /// <summary>
    /// sim/combat-pass (item 21; the owner's playtest of 2026-10-30: "charging off a ledge: the falling animation should start about 1 s into the ledge charge"): a charge held in the air shows its
    /// own clip (its pose as it left the ground) until the charge has been under way this long (seconds on the ground and in the air together; ours, an estimate of "about 1 s"), then the fall pose.
    /// </summary>
    public const float ChargeFallDelay = 1.0f;

    /// <summary>Whether a charge held in the air that has run <paramref name="chargeSeconds"/> on the ground and has been in the air <paramref name="airSeconds"/> shows the fall pose (<see cref="ChargeFallDelay"/>).</summary>
    public static bool ChargeShowsFall(float chargeSeconds, double airSeconds) =>
        (float.IsFinite(chargeSeconds) ? Math.Max(0f, chargeSeconds) : 0f) + (double.IsFinite(airSeconds) ? Math.Max(0.0, airSeconds) : 0.0) >= ChargeFallDelay - 1e-6;

    /// <summary>The air time from which landing effects, shake and sound happen (ours; the sound code's 0.4 s: a short hop is not a fall).</summary>
    public const float LandingFeelSeconds = 0.4f;

    /// <summary>Whether a fall that reached <paramref name="peakDown"/> m/s downward ends in the hard landing (<paramref name="threshold"/> from the graph; 0: every fall, the heavy bolter's).</summary>
    public static bool IsHard(float peakDown, float threshold) => float.IsFinite(peakDown) && (threshold <= 0f || peakDown >= threshold);

    /// <summary>
    /// The weight of the landing clip <paramref name="elapsed"/> seconds after touchdown: it blends in over <paramref name="landBlend"/>, holds until <paramref name="exit"/>.At and blends out over <paramref name="exit"/>.Blend
    /// (the navigation state taking over). 0 outside the clip. The weight never leaves [0, 1].
    /// </summary>
    public static float LandWeight(float elapsed, float landBlend, FallExit exit)
    {
        if (!(elapsed >= 0f)) return 0f;
        float rise = landBlend > 1e-4f ? Math.Min(1f, elapsed / landBlend) : 1f;
        float fall = elapsed <= exit.At ? 1f : exit.Blend > 1e-4f ? 1f - (elapsed - exit.At) / exit.Blend : 0f;
        return Math.Clamp(Math.Min(rise, fall), 0f, 1f);
    }

    /// <summary>The landing is over (nothing left to show) <paramref name="elapsed"/> seconds after touchdown.</summary>
    public static bool LandOver(float elapsed, FallExit exit) => elapsed >= exit.At + exit.Blend;

    /// <summary>
    /// The share of the landing clip kept while the marine moves at <paramref name="groundSpeed"/> m/s: all of it standing, none from <see cref="LandMoveSpeed"/> up (ours: the clip is a crouch in place and the
    /// simulation lets a marine run at once, so a running marine would slide its feet). feel/buffer-speed: both speeds × <paramref name="speedScale"/>
    /// (<c>SimConfig.MoveSpeedScale</c>), so the aimed run (3 → 2.7 m/s with the owner's slower movement) still keeps none of it.
    /// </summary>
    public static float MoveKeep(float groundSpeed, float speedScale = 1f)
    {
        float k = float.IsFinite(speedScale) && speedScale > 0f ? speedScale : 1f;
        return float.IsFinite(groundSpeed) ? 1f - Math.Clamp((groundSpeed - k) / ((LandMoveSpeed - 1f) * k), 0f, 1f) : 1f;
    }

    public const float LandMoveSpeed = 3f;
}
