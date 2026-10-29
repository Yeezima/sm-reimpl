using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// The heavy bolter brace's timing from the marine rig's behaviour graphs (fidelity gap 7; <c>smtool graph-dump --brace</c>):
/// <list type="bullet">
/// <item>Entry: <c>SM_Base_MP</c>'s "Heavy Bolter Switch Mode" plays the sequencer with <c>BRANCH_Idle_to_Brace</c>
/// (<c>SM_RNG_TB_Idle_to_BraceIdle</c>, 1.5 s, branch at 0.9556 of the clip), its clip's playback speed bound to
/// <c>FLT_Brace_Entry_Time</c>.</item>
/// <item>Exit: <c>MP_SM_HB_Brace</c>'s "Exit Brace Fire" plays the sequencer with <c>BRANCH_Exit_Brace</c> (<c>SM_RNG_TB_BraceIdle_to_idle</c>,
/// 1.367 s, branch at 0.9512), bound to the same variable.</item>
/// <item>Speeds: the variable's initial value (<c>MP_SM_HB_Brace</c>'s <c>hkbVariableValueSet</c>: 1); the <c>hkbSetFloatModifier</c>s that set it
/// (variable index @44, value @48; their <c>enable</c> binding names the condition; a Relic class, layout read on the install): in
/// <c>SM_Base_MP</c>'s Switch Mode list, enabled by <c>BOOL_UPGRADE_mp_perk_weap_heavy_bolter_braced_fire_time</c> (the Heavy Bolter Expertise
/// perk), 2; in the brace graph's exit list, enabled by <c>BOOL_UPGRADE_mp_fire_mode_heavy_bolter_braced_fire</c> (the braced fire mode), 3.</item>
/// </list>
/// The exit runs at the entry's speed (1, Expertise 2): the owner's recollection (2026-10-28) is that Heavy Bolter Expertise sped up getting into **and**
/// out of the brace, and the perk text says "to and from", so the brace graph's speed 3 (enabled by the braced fire mode) is read as never applying during
/// the exit, i.e. the mode leaves at the exit's press (**interpretation**, an estimate to check). Timings: entry 1.433 s (Expertise 0.717 s), exit 1.300 s (0.650 s).
/// </summary>
public static class BraceTuning
{
    /// <summary>The graphs' timing: clip lengths (s), branch fractions, the speed variable's initial value and its modifiers by enabling variable.</summary>
    public sealed record Timings(string EnterClip, float EnterLength, float EnterBranch, string ExitClip, float ExitLength, float ExitBranch,
        float DefaultSpeed, IReadOnlyDictionary<string, float> EntrySpeeds, IReadOnlyDictionary<string, float> ExitSpeeds)
    {
        /// <summary>The entry clip's <c>ACTN_SND_HB_Brace_Enter</c> tag (fraction of the clip; NaN without; 0.5556 on the install).</summary>
        public float EnterSoundAt { get; init; } = float.NaN;
        /// <summary>The exit clip's <c>ACTN_SND_HB_Brace_Exit</c> tag (fraction; 0.2927).</summary>
        public float ExitSoundAt { get; init; } = float.NaN;
        /// <summary>The braced idle by the aim's pitch (the brace graph's "Aim Frwd Blend": +80°, 0°, −80°), null when not found.</summary>
        public (string Up, string Forward, string Down)? Idle { get; init; }
        /// <summary>The braced shot's additive recoil (the "Recoil Sequence" clip, 0.167 s), null when not found.</summary>
        public string? FireClip { get; init; }
        /// <summary>The bones the exit leaves to the locomotion (the "Exit Blend"'s locomotion child's mask: the legs and the root), empty when not found.</summary>
        public IReadOnlyList<string> ExitLegBones { get; init; } = [];

        /// <summary>The entry clip's playback speed for a brace whose <see cref="BraceSpec.EnterTime"/> is <paramref name="enterTime"/> (1 without one).</summary>
        public float EnterRate(float enterTime) => enterTime > 1e-4f ? EnterLength * EnterBranch / enterTime : 1f;
        /// <summary>The exit clip's playback speed for <paramref name="exitTime"/>.</summary>
        public float ExitRate(float exitTime) => exitTime > 1e-4f ? ExitLength * ExitBranch / exitTime : 1f;

        /// <summary>Seconds from the press to braced fire; <paramref name="expertise"/>: the perk's speed when the entry graph has one.</summary>
        public float EnterSeconds(bool expertise) =>
            EnterLength * EnterBranch / Speed(EntrySpeeds, expertise ? Upgrade(BraceSpec.ExpertiseUpgrade) : null, DefaultSpeed);

        /// <summary>Seconds from the press to leaving the stance: with <paramref name="fireMode"/> the brace graph's speed for that fire mode (not used since the owner's recollection), null: the entry's speed (used).</summary>
        public float ExitSeconds(string? fireMode, bool expertise)
        {
            float entry = Speed(EntrySpeeds, expertise ? Upgrade(BraceSpec.ExpertiseUpgrade) : null, DefaultSpeed);
            return ExitLength * ExitBranch / Speed(ExitSpeeds, string.IsNullOrEmpty(fireMode) ? null : Upgrade(fireMode), entry);
        }

        static float Speed(IReadOnlyDictionary<string, float> speeds, string? variable, float fallback) =>
            variable is not null && speeds.TryGetValue(variable, out float v) && float.IsFinite(v) && v > 0f ? v : fallback;
    }

    /// <summary>The graph variable an upgrade string sets (<c>BOOL_UPGRADE_</c> + the string).</summary>
    public static string Upgrade(string upgrade) => "BOOL_UPGRADE_" + upgrade;

    /// <summary>The playback-speed variable of the brace clips.</summary>
    public const string SpeedVariable = "FLT_Brace_Entry_Time";
    /// <summary>The graph holding the entry (the heavy bolter's Switch Mode state).</summary>
    public const string EntryGraph = "SM_Base_MP";
    /// <summary>The braced stance's graph (the exit).</summary>
    public const string BraceGraph = "MP_SM_HB_Brace";

    /// <summary>The timing from a character's graphs, or null with a note when a part is missing.</summary>
    public static Timings? Read(HavokCharacterFile character, List<string> notes) => Read(character, HavokBehaviorDetailReader.Read(character.Pack), notes);

    /// <summary>As <see cref="Read(HavokCharacterFile, List{string})"/> with the graphs already read.</summary>
    public static Timings? Read(HavokCharacterFile character, IReadOnlyList<BehaviorGraphDetail> graphs, List<string> notes)
    {
        var entryGraph = graphs.FirstOrDefault(g => g.Name == EntryGraph);
        var braceGraph = graphs.FirstOrDefault(g => g.Name == BraceGraph);
        if (entryGraph is null || braceGraph is null) { notes.Add($"brace: graph {(entryGraph is null ? EntryGraph : BraceGraph)} not found; no brace timing"); return null; }
        float Length(string clip)
        {
            try { return character.LoadAnimation(clip).Animation.Duration; }
            catch (Exception e) when (e is KeyNotFoundException or NotSupportedException or InvalidDataException) { return float.NaN; }
        }
        (string Clip, float Length, float Branch, float Sound)? Part(BehaviorGraphDetail g, string branch, string sound)
        {
            foreach (var seq in g.Sequencers.Values)
                if (seq.Tags.FirstOrDefault(t => t.Kind == "Branch" && t.Name == branch) is { } tag && seq.Animations.Count > 0)
                    return (seq.Animations[0], Length(seq.Animations[0]), tag.Start,
                        seq.Tags.FirstOrDefault(t => t.Kind == "Action" && string.Equals(t.Name, sound, StringComparison.OrdinalIgnoreCase))?.Start ?? float.NaN);
            return null;
        }
        var enter = Part(entryGraph, "BRANCH_Idle_to_Brace", "ACTN_SND_HB_Brace_Enter");
        var exit = Part(braceGraph, "BRANCH_Exit_Brace", "ACTN_SND_HB_Brace_Exit");
        if (enter is not { } e || exit is not { } x || !(e.Length > 0f) || !(x.Length > 0f) || e.Branch is < 0f or > 1f || x.Branch is < 0f or > 1f)
        { notes.Add("brace: the entry or exit sequencer (BRANCH_Idle_to_Brace, BRANCH_Exit_Brace) or its clip is missing; no brace timing"); return null; }
        float initial = InitialValue(character.Pack, braceGraph, SpeedVariable) ?? InitialValue(character.Pack, entryGraph, SpeedVariable) ?? float.NaN;
        if (!(float.IsFinite(initial) && initial > 0f)) { notes.Add($"brace: {SpeedVariable} has no usable initial value; 1 assumed"); initial = 1f; }
        // Presentation (the client): the braced idle by pitch, the recoil, the exit's leg mask.
        (string, string, string)? idle = null;
        if (braceGraph.Blenders.FirstOrDefault(b => b.Name == "Aim Frwd Blend") is { } aim)
        {
            string? ClipAt(float weight) => aim.Children.FirstOrDefault(c => MathF.Abs(c.Weight - weight) < 1f) is { Generator: int at } ? braceGraph.ClipAt(at)?.Animation : null;
            float up = aim.Children.Max(c => c.Weight), down = aim.Children.Min(c => c.Weight);
            if (ClipAt(up) is { } u && ClipAt(0f) is { } f && ClipAt(down) is { } d && up > 0f && down < 0f) idle = (u, f, d);
        }
        string? fire = braceGraph.Sequencers.Values.FirstOrDefault(s => s.Name == "Recoil Sequence" && s.Animations.Count > 0)?.Animations[0];
        var legs = new List<string>();
        var bones = character.Skeleton.BoneNames;
        if (braceGraph.Blenders.FirstOrDefault(b => b.Name == "Exit Blend") is { } exitBlend)
            foreach (var child in exitBlend.Children)
                if (braceGraph.BoneWeightsAt(child.BoneWeights) is { } mask && mask.Weights.Count == bones.Count && mask.Weights.Any(w => w > 0f))
                    legs.AddRange(Enumerable.Range(0, bones.Count).Where(i => mask.Weights[i] > 0f).Select(i => bones[i]));
        return new Timings(e.Clip, e.Length, e.Branch, x.Clip, x.Length, x.Branch, initial,
            Modifiers(character.Pack, entryGraph, SpeedVariable), Modifiers(character.Pack, braceGraph, SpeedVariable))
        { EnterSoundAt = e.Sound, ExitSoundAt = x.Sound, Idle = idle, FireClip = fire, ExitLegBones = legs.Distinct().ToList() };
    }

    /// <summary>The timing from the install's marine rig (read-only; <see cref="Read(HavokCharacterFile, List{string})"/>), or null with a note.</summary>
    public static Timings? Load(string gameDir, List<string> notes)
    {
        byte[]? rig = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, CharacterModel.MarineRigArchive)))
            archive.ReadEntries(e => e.FileName == CharacterModel.MarineRigEntry && rig is null, (_, d) => rig = d.ToArray());
        if (rig is null) { notes.Add($"brace: '{CharacterModel.MarineRigEntry}' not found"); return null; }
        return Read(new HavokCharacterFile(rig), notes);
    }

    /// <summary>
    /// The <c>hkbSetFloatModifier</c>s of a graph that set <paramref name="variable"/>: their value by the variable their <c>enable</c> binding names
    /// ("" when not bound). Layout (Relic class; read on the install): variable index @44, value @48.
    /// </summary>
    public static IReadOnlyDictionary<string, float> Modifiers(HavokPackfile pack, BehaviorGraphDetail g, string variable)
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        int index = g.Variables.ToList().IndexOf(variable);
        if (index < 0) return result;
        foreach (var node in g.Nodes.Values.Where(n => n.Class == "hkbSetFloatModifier"))
        {
            if (pack.ReadInt32(node.Offset + 44) != index) continue;
            result[node.Bindings.GetValueOrDefault("enable") ?? ""] = pack.ReadSingle(node.Offset + 48);
        }
        return result;
    }

    /// <summary>A variable's initial value from the graph's <c>hkbVariableValueSet</c> (word values @8, 4 bytes each, read as a float), or null.</summary>
    public static float? InitialValue(HavokPackfile pack, BehaviorGraphDetail g, string variable)
    {
        int index = g.Variables.ToList().IndexOf(variable);
        if (index < 0) return null;
        foreach (var o in pack.Objects.Where(o => o.ClassName == "hkbVariableValueSet" && o.Offset >= g.Start && o.Offset < g.End))
        {
            var (data, count) = pack.Array(o.Offset + 8, 4);
            if (data is int at && index < count) return pack.ReadSingle(at + 4 * index);
        }
        return null;
    }

    /// <summary>The weapons with the brace timing set (<see cref="BraceSpec.EnterTime"/>, <see cref="BraceSpec.ExitTime"/>; perk variants too).</summary>
    public static IReadOnlyList<WeaponSpec> WithTiming(IReadOnlyList<WeaponSpec> weapons, Timings timings)
    {
        static WeaponSpec Set(WeaponSpec w, Timings t) => w.Brace is not { } b ? w : w with
        {
            Brace = b with { EnterTime = t.EnterSeconds(b.Expertise), ExitTime = t.ExitSeconds(null, b.Expertise) },   // the entry's speed (owner's recollection: the perk speeds the exit too)
        };
        return weapons.Select(w => w.Brace is null && w.Variants.All(v => v.Spec.Brace is null) ? w
            : Set(w, timings) with { Variants = w.Variants.Select(v => v with { Spec = Set(v.Spec, timings) }).ToList() }).ToList();
    }

    /// <summary>
    /// <see cref="WithTiming(IReadOnlyList{WeaponSpec}, Timings)"/> from the install's marine rig (read-only); the weapons unchanged (their
    /// transitions instant) with a note when the rig or the graphs cannot be read. Nothing is read when no weapon braces.
    /// </summary>
    public static IReadOnlyList<WeaponSpec> WithTiming(IReadOnlyList<WeaponSpec> weapons, string gameDir, List<string> notes)
    {
        if (weapons.All(w => w.Brace is null)) return weapons;
        try
        {
            if (Load(gameDir, notes) is not { } timings) { notes.Add("brace: transitions instant"); return weapons; }
            notes.Add($"brace: entry {timings.EnterSeconds(false):0.###} s (Expertise {timings.EnterSeconds(true):0.###} s), exit {timings.ExitSeconds(null, false):0.###} s (Expertise {timings.ExitSeconds(null, true):0.###} s; the brace graph's fire-mode speed not applied)");
            return WithTiming(weapons, timings);
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or IOException)
        {
            notes.Add($"brace: {e.GetType().Name}: {e.Message}; brace transitions instant");
            return weapons;
        }
    }

}
