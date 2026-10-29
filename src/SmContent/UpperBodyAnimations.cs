using SmFormats.Havok;

namespace SmContent;

// Animation pass 2: the weapon switch, the hit flinches, the death clips before the ragdoll and the jump pack's get-up and flight noise, read from the marine rig's behaviour graphs
// (SM_Base_MP and MP_SM_NAV_JP_Sim; `smtool graph-dump --upper`). Read-only; engine-free; every reader is tested on synthetic graphs.

/// <summary>Small graph walks shared by the pass-2 readers.</summary>
static class GraphWalk
{
    public static IEnumerable<int> Walk(BehaviorGraphDetail g, int? node, int maxDepth = 16)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<(int At, int Depth)>();
        if (node is int start) stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (at, depth) = stack.Pop();
            if (!g.Nodes.TryGetValue(at, out var n) || !seen.Add(at)) continue;
            yield return at;
            if (depth >= maxDepth) continue;
            for (int i = n.Children.Count - 1; i >= 0; i--)
                if (n.Children[i].Target is int c) stack.Push((c, depth + 1));
        }
    }

    public static BehaviorSequencer? SequencerUnder(BehaviorGraphDetail g, int? node)
    {
        foreach (var at in Walk(g, node))
            if (g.Sequencers.TryGetValue(at, out var s) && s.Animations.Count > 0) return s;
        return null;
    }

    public static float Speed(BehaviorSequencer seq) => seq.PlaybackSpeeds.Count > 0 && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;

    /// <summary>The first tag of a kind (and, when given, a name containing <paramref name="nameContains"/>), or null.</summary>
    public static BehaviorTag? Tag(BehaviorSequencer seq, string kind, string? nameContains = null, float minStart = -1f) =>
        seq.Tags.Where(t => t.Kind == kind && (nameContains is null || t.Name?.Contains(nameContains, StringComparison.OrdinalIgnoreCase) == true) && t.Start > minStart)
                .OrderBy(t => t.Start).Cast<BehaviorTag?>().FirstOrDefault();
}

// ---------------------------------------------------------------------------------------------------------------------------------------------------------
// 1. Weapon switch

/// <summary>
/// One weapon-switch clip of the base graph: it plays on the whole body except the 14 leg bones (<paramref name="Mask"/>: the swap child's bone mask, whose 14 entries are all zero and
/// whose later bones read as 1) while the navigation graph keeps the legs. <paramref name="SwapAt"/>: seconds into the clip at which the new weapon is in the hand (the sequencer's
/// <c>Weapons - Ranged Weapon</c> / <c>Weapons - Melee and Ranged</c> modifier tag starts); <paramref name="BranchIn"/>: the <c>BranchIn</c> tag (seconds; where the next action may blend in).
/// </summary>
public sealed record SwitchClip(string Clip, float Length, IReadOnlyDictionary<string, float> Mask, float SwapAt, float BranchIn);

/// <summary>
/// The weapon switches of <c>SM_Base_MP</c>: <see cref="ToRifle"/> (state "Bolter Weapon Switch" and the other rifle-class behaviours': <c>SM_Swap_Rifle_to_Rifle_01</c>, 0.467 s, the model swaps at
/// 0.5 of it) and <see cref="ToPistol"/> (the bolt pistol behaviour's "BoltPistol Weapon Switch": <c>SM_Swap_Rifle_to_Melee_01</c>, 0.667 s, the swap at 0.6). Which of them a multiplayer switch plays
/// (the pistol's for a switch to a pistol, the rifle's for any other) is an interpretation: the graph runs the new weapon's behaviour state.
/// </summary>
public sealed record SwitchAnimations(SwitchClip? ToRifle, SwitchClip? ToPistol, IReadOnlyList<string> Notes)
{
    public const string Graph = "SM_Base_MP";
    public const string RifleState = "Bolter Weapon Switch", PistolState = "BoltPistol Weapon Switch";

    public static SwitchAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames, Func<string, float> animationLength)
    {
        var notes = new List<string>();
        if (graphs.FirstOrDefault(x => x.Name.Equals(Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {Graph} not found"); return new(null, null, notes); }
        return new(Read(g, RifleState, boneNames, animationLength, notes), Read(g, PistolState, boneNames, animationLength, notes), notes);
    }

    static SwitchClip? Read(BehaviorGraphDetail g, string stateName, IReadOnlyList<string> boneNames, Func<string, float> length, List<string> notes)
    {
        var state = g.AllStates.FirstOrDefault(x => x.State.Name == stateName).State;
        if (state is null) { notes.Add($"{Graph}: no state '{stateName}'"); return null; }
        foreach (var at in GraphWalk.Walk(g, state.Generator))
        {
            if (g.BlenderAt(at) is not { } blend) continue;
            foreach (var child in blend.Children)
            {
                if (g.BoneWeightsAt(child.BoneWeights) is not { } weights || GraphWalk.SequencerUnder(g, child.Generator) is not { } seq) continue;
                string clip = seq.Animations[0];
                float len = length(clip) / GraphWalk.Speed(seq);
                var mask = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int i = 0; i < boneNames.Count; i++) mask[boneNames[i]] = weights.WeightOf(i);
                // The tag that puts the new weapon in the hand: "Weapons - Ranged Weapon" (0.5 to 1 of the rifle swap) or "Weapons - Melee and Ranged" (0.6 to 1 of the stow clip), the first modifier tag
                // naming the ranged weapon that does not start at 0.
                float swap = GraphWalk.Tag(seq, "Modifier", "Ranged", 0.01f) is { } tag ? tag.Start : 0.5f;
                float branchIn = GraphWalk.Tag(seq, "BranchIn") is { } b ? b.Start : 1f;
                return new SwitchClip(clip, len, mask, Math.Clamp(swap, 0f, 1f) * len, Math.Clamp(branchIn, 0f, 1f) * len);
            }
        }
        notes.Add($"{Graph}/{stateName}: no masked swap clip");
        return null;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------------------
// 2. Hit flinch

/// <summary>A flinch clip (<paramref name="Additive"/>: its binding's blend hint; the flinches are additive offsets laid over the pose).</summary>
public sealed record FlinchClip(string Clip, float Length, bool Additive = true);

/// <summary>
/// The flinch states of one weapon's set in <c>SM_Base_MP</c> ("Upper Body Flinches" > "Ranged Flinch Behavior" > the weapon's machine). Every set has the same twelve slots by state id (the "Reference Flinches" set
/// names them): 0 head front left, 1 head front right, 2 head back left, 3 head back right, 4 high left, 5 high right, 6 low left, 7 low right, 8 back left, 9 back right, 10 back low left, 11 back low right
/// (<see cref="Slots"/> index = id; a slot the weapon's set lacks is null).
/// </summary>
public sealed record FlinchSet(string Weapon, IReadOnlyList<FlinchClip?> Slots)
{
    public const int SlotCount = 12;
    public FlinchClip? Slot(int id) => id >= 0 && id < Slots.Count ? Slots[id] : null;
}

/// <summary>The graph's flinch sets by weapon and the blueprint's <c>min_hit_reaction_time</c> (<see cref="MinHitReactionTime"/>, seconds: the least time between two flinches of one player).</summary>
public sealed record FlinchAnimations(IReadOnlyDictionary<string, FlinchSet> Sets, float MinHitReactionTime, IReadOnlyList<string> Notes)
{
    public const string Machine = "Ranged Flinch Behavior";
    /// <summary>Used when the blueprint has none (the install's value is 1).</summary>
    public const float DefaultMinHitReactionTime = 1f;

    public FlinchSet? For(string weaponState) => Sets.GetValueOrDefault(weaponState);

    public static FlinchAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength, float? minHitReactionTime = null, Func<string, bool>? isAdditive = null)
    {
        var notes = new List<string>();
        var sets = new Dictionary<string, FlinchSet>(StringComparer.Ordinal);
        float min = minHitReactionTime is { } m && float.IsFinite(m) && m >= 0f ? m : DefaultMinHitReactionTime;
        if (graphs.FirstOrDefault(x => x.Name.Equals(SwitchAnimations.Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {SwitchAnimations.Graph} not found"); return new(sets, min, notes); }
        var machine = g.StateMachines.FirstOrDefault(x => x.Name == Machine);
        if (machine is null) { notes.Add($"{SwitchAnimations.Graph}: no machine '{Machine}'"); return new(sets, min, notes); }
        foreach (var weapon in machine.States)
        {
            BehaviorStateMachine? inner = null;
            foreach (var at in GraphWalk.Walk(g, weapon.Generator, 4)) if (g.MachineAt(at) is { } m2) { inner = m2; break; }
            if (inner is null) continue;
            var slots = new FlinchClip?[FlinchSet.SlotCount];
            foreach (var s in inner.States)
            {
                if (s.Id < 0 || s.Id >= slots.Length) continue;
                (string? Name, float Speed) clip = GraphWalk.SequencerUnder(g, s.Generator) is { } seq ? (seq.Animations[0], GraphWalk.Speed(seq))
                    : HipFireAnimations.FirstClip(g, s.Generator) is { } c ? (c.Animation, c.PlaybackSpeed > 0f ? c.PlaybackSpeed : 1f) : (null, 1f);
                if (clip.Name is null) continue;
                slots[s.Id] = new FlinchClip(clip.Name, animationLength(clip.Name) / clip.Speed, isAdditive?.Invoke(clip.Name) ?? true);
            }
            if (slots.Any(x => x is not null)) sets[weapon.Name] = new FlinchSet(weapon.Name, slots);
        }
        if (sets.Count == 0) notes.Add($"{SwitchAnimations.Graph}/{Machine}: no flinch sets");
        return new(sets, min, notes);
    }
}

/// <summary>Which of the twelve flinch slots a hit takes (client presentation; **interpretation**: the graph picks the state in code the readers do not decode) and which weapon's set a weapon uses.</summary>
public static class FlinchRules
{
    /// <summary>The slot for a hit: a head shot takes the head slots (front or back, left or right), a body hit the high slots (back: the back slots).</summary>
    public static int Slot(bool head, bool fromBehind, bool fromLeft) =>
        head ? (fromBehind ? 2 : 0) + (fromLeft ? 0 : 1) : fromBehind ? (fromLeft ? 8 : 9) : (fromLeft ? 4 : 5);

    /// <summary>The slots to try for a hit, in order: the wanted one, then the same side's body slot (a set without head clips), then the other side's body slot, then any slot of the set.</summary>
    public static int Choose(FlinchSet set, bool head, bool fromBehind, bool fromLeft)
    {
        int wanted = Slot(head, fromBehind, fromLeft);
        if (set.Slot(wanted) is not null) return wanted;
        int body = Slot(false, fromBehind, fromLeft);
        if (set.Slot(body) is not null) return body;
        int mirrored = Slot(false, fromBehind, !fromLeft);
        if (set.Slot(mirrored) is not null) return mirrored;
        for (int i = 0; i < FlinchSet.SlotCount; i++) if (set.Slot(i) is not null) return i;
        return -1;
    }

    /// <summary>
    /// Whether a shot at <paramref name="victimYaw"/>'s marine from <paramref name="shooterX"/>, <paramref name="shooterY"/> comes from behind and from its left (game space, yaw 0 faces +Y, positive yaw turns left).
    /// </summary>
    public static (bool Behind, bool Left) Side(float victimX, float victimY, float victimYaw, float shooterX, float shooterY)
    {
        float dx = shooterX - victimX, dy = shooterY - victimY;
        float fx = -MathF.Sin(victimYaw), fy = MathF.Cos(victimYaw);   // the facing
        float along = dx * fx + dy * fy;                                 // positive: the shooter is in front
        float left = dx * -fy + dy * fx;                                 // positive: on the marine's left
        return (along < 0f, left > 0f);
    }

    /// <summary>The set (the graph's weapon state name) a weapon in hand uses; null for a weapon with none (the Bolter's set is the default). **Interpretation** of the names.</summary>
    public static string SetFor(string weaponName, bool charging)
    {
        string n = weaponName.ToLowerInvariant();
        if (n.Contains("heavy_bolter")) return "Heavy Bolter Flinch";
        if (n.Contains("las_cannon") || n.Contains("lascannon")) return "Lascannon Flinch";
        if (n.Contains("plasma_cannon")) return charging ? "Plasma Cannon Strong Flinch" : "Plasma Cannon Weak Flinch";
        if (n.Contains("plasma_pistol")) return charging ? "Plasma Pistol Strong Flinch" : "Plasma Pistol Weak Flinch";
        if (n.Contains("plasma")) return charging ? "Plasma Strong Flinch" : "Plasma Weak Flinch";
        if (n.Contains("melta")) return "Melta Flinch";
        if (n.Contains("stalker")) return "Stalker Pattern Flinch";
        if (n.Contains("bolt_pistol")) return "Bolt Pistol Flinch";
        return "Bolter Flinch";
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------------------
// 3. Death before the ragdoll

public enum MarineDeathKind
{
    DefaultFront, DefaultBack, Sprint, Bullrush, HeadshotFront, HeadshotBack, Plasma,
    LasFront, LasBack, LasRight, LasLeft, MeltaFront, MeltaBack, MeltaRight, MeltaLeft,
}

/// <summary>A death clip and the time the graph hands the body to the ragdoll (<paramref name="RagdollAt"/>: the "Powered Ragdoll Controls Modifier" tag's start x the clip's length).</summary>
public sealed record DeathClip(string Clip, float Length, float RagdollAt);

/// <summary>The death states of <c>SM_Base_MP</c> ("DEATH" > "Behavior Death"), by <see cref="MarineDeathKind"/>.</summary>
public sealed record DeathAnimations(IReadOnlyDictionary<MarineDeathKind, DeathClip> Clips, IReadOnlyList<string> Notes)
{
    public const string Machine = "Behavior Death";

    /// <summary>(outer state, inner state or null for a leaf) of each kind.</summary>
    static readonly (MarineDeathKind Kind, string Outer, string? Inner)[] Map =
    [
        (MarineDeathKind.DefaultFront, "Death - Default", "Default Death Front"), (MarineDeathKind.DefaultBack, "Death - Default", "Default Death Back"),
        (MarineDeathKind.Sprint, "Sprinting Death", null), (MarineDeathKind.Bullrush, "Bullrush Death", null),
        (MarineDeathKind.HeadshotFront, "Headshot Death", "Headshot Front"), (MarineDeathKind.HeadshotBack, "Headshot Death", "Headshot Back"),
        (MarineDeathKind.Plasma, "Plasma Death", null),
        (MarineDeathKind.LasFront, "Lascannon Deaths", "Las Death Front"), (MarineDeathKind.LasBack, "Lascannon Deaths", "Las Death Back"),
        (MarineDeathKind.LasRight, "Lascannon Deaths", "Las Death Right"), (MarineDeathKind.LasLeft, "Lascannon Deaths", "Las Death Left"),
        (MarineDeathKind.MeltaFront, "Melta Death", "Melta Death Front"), (MarineDeathKind.MeltaBack, "Melta Death", "Melta Death Back"),
        (MarineDeathKind.MeltaRight, "Melta Death", "Melta Death Right"), (MarineDeathKind.MeltaLeft, "Melta Death", "Melta Death Left"),
    ];

    public DeathClip? Get(MarineDeathKind kind) => Clips.GetValueOrDefault(kind);

    public static DeathAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength)
    {
        var notes = new List<string>();
        var clips = new Dictionary<MarineDeathKind, DeathClip>();
        if (graphs.FirstOrDefault(x => x.Name.Equals(SwitchAnimations.Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {SwitchAnimations.Graph} not found"); return new(clips, notes); }
        var machine = g.StateMachines.FirstOrDefault(x => x.Name == Machine);
        if (machine is null) { notes.Add($"{SwitchAnimations.Graph}: no machine '{Machine}'"); return new(clips, notes); }
        foreach (var (kind, outerName, innerName) in Map)
        {
            var outer = machine.States.FirstOrDefault(s => s.Name == outerName);
            if (outer is null) { notes.Add($"death: no state '{outerName}'"); continue; }
            int? generator = outer.Generator;
            if (innerName is not null)
            {
                BehaviorStateMachine? inner = null;
                foreach (var at in GraphWalk.Walk(g, outer.Generator, 4)) if (g.MachineAt(at) is { } m) { inner = m; break; }
                var leaf = inner?.States.FirstOrDefault(s => s.Name == innerName);
                if (leaf is null) { notes.Add($"death: no state '{outerName}' / '{innerName}'"); continue; }
                generator = leaf.Generator;
            }
            if (GraphWalk.SequencerUnder(g, generator) is not { } seq) { notes.Add($"death: no clip in '{outerName}'{(innerName is null ? "" : " / '" + innerName + "'")}"); continue; }
            string clip = seq.Animations[0];
            float length = animationLength(clip) / GraphWalk.Speed(seq);
            float ragdoll = GraphWalk.Tag(seq, "Modifier", "Powered Ragdoll") is { } tag ? tag.Start : 0.8f;
            clips[kind] = new DeathClip(clip, length, Math.Clamp(ragdoll, 0f, 1f) * length);
        }
        return new(clips, notes);
    }
}

/// <summary>Which death clip a kill plays (client presentation; **interpretation**: the graph's choice is made in code and by its start wedges, only the wedges are read).</summary>
public static class DeathRules
{
    /// <summary>
    /// The kind for a death: a sprinting victim its stagger; a charge's kill the bull rush clip; a head shot the head-shot pair; a lascannon, plasma cannon or melta kill the "Las" / "Melta" wedge clip (front
    /// within 55 degrees of the facing, back beyond 125, else by side); a plasma weapon the plasma death; everything else the default pair (front / back by the wedge of 90 degrees).
    /// <paramref name="blowAlongFacing"/> / <paramref name="blowRight"/>: the blow's direction in the victim's frame (the part along its facing, positive = travelling the way it faces = from behind; the part to its right).
    /// </summary>
    public static MarineDeathKind Choose(bool sprinting, bool chargeKill, bool head, WeaponDeath weapon, bool hasDirection, float blowAlongFacing, float blowRight)
    {
        bool fromBehind = hasDirection && blowAlongFacing > 0f;
        if (sprinting && !chargeKill) return MarineDeathKind.Sprint;
        if (chargeKill) return MarineDeathKind.Bullrush;
        if (head && weapon == WeaponDeath.Bolter) return fromBehind ? MarineDeathKind.HeadshotBack : MarineDeathKind.HeadshotFront;
        if (weapon == WeaponDeath.Plasma) return MarineDeathKind.Plasma;
        if (weapon is WeaponDeath.Las or WeaponDeath.Melta)
        {
            bool melta = weapon == WeaponDeath.Melta;
            if (!hasDirection) return melta ? MarineDeathKind.MeltaFront : MarineDeathKind.LasFront;
            // The wedge is the angle between the blow and the facing's reverse (0 = shot from the front): front within 55, back beyond 125, else the side.
            float degrees = MathF.Atan2(blowRight, -blowAlongFacing) * 180f / MathF.PI;   // 0 = the blow comes straight at the face
            float a = MathF.Abs(degrees);
            if (a <= 55f) return melta ? MarineDeathKind.MeltaFront : MarineDeathKind.LasFront;
            if (a >= 125f) return melta ? MarineDeathKind.MeltaBack : MarineDeathKind.LasBack;
            return degrees > 0f ? (melta ? MarineDeathKind.MeltaLeft : MarineDeathKind.LasLeft) : (melta ? MarineDeathKind.MeltaRight : MarineDeathKind.LasRight);
        }
        return fromBehind ? MarineDeathKind.DefaultBack : MarineDeathKind.DefaultFront;
    }

    /// <summary>
    /// Whether a death plays its standing clip before the ragdoll: not for a fall out of the level and not for a victim that was in the air at its last living frame (a jump-pack flight, a pound,
    /// a plain fall: the body is not placed while dead, so a standing death clip would hang it in the air; those go straight to the ragdoll).
    /// </summary>
    public static bool Holds(bool fellOutOfLevel, bool airborneAtLastLivingFrame) => !fellOutOfLevel && !airborneAtLastLivingFrame;

    /// <summary>The share of the body's last horizontal velocity that is left <paramref name="age"/> seconds into the hold (an exponential decay with time constant <paramref name="tau"/>; ours): the held body slides on and slows.</summary>
    public static float HeldVelocityFactor(float age, float tau) => tau > 1e-4f && age > 0f ? MathF.Exp(-age / tau) : 1f;

    /// <summary>The weapon class of a kill for <see cref="Choose"/>, by the wargear name (interpretation).</summary>
    public static WeaponDeath WeaponOf(string? weaponName)
    {
        if (weaponName is null) return WeaponDeath.Bolter;
        string n = weaponName.ToLowerInvariant();
        if (n.Contains("las_cannon") || n.Contains("lascannon") || n.Contains("plasma_cannon")) return WeaponDeath.Las;
        if (n.Contains("melta")) return WeaponDeath.Melta;
        if (n.Contains("plasma")) return WeaponDeath.Plasma;
        return WeaponDeath.Bolter;
    }
}

public enum WeaponDeath { Bolter, Plasma, Las, Melta }

// ---------------------------------------------------------------------------------------------------------------------------------------------------------
// 4. The jump pack's get-up and the flight noise

/// <summary>
/// The ground pound's recovery into a run (<c>MP_SM_NAV_JP_Sim</c>: "GroundPoundImpact" goes to "GroundPoundRun" over <see cref="Blend"/> (0.167 s, 'Fast Blend BranchIn') when the requested speed is at least
/// <see cref="MinSpeed"/> (0.5) from the impact clip's <c>BRANCH_to_Getup</c> window (<see cref="FromImpact"/> seconds into it), else to "GroundPound Idle" at <c>BRANCH_To_Idle_Getup</c>):
/// <see cref="RunClip"/> (<c>SM_JMP_Landing_MedRun</c>, <see cref="RunLength"/> s) ends at its <c>BRANCH_Run</c> (<see cref="RunEnd"/> s). <see cref="NoiseClip"/>: the flight's <c>Noise</c> blend
/// (<c>SM_JMP_Blend_90_Pos_Fast</c>, 1 s, looping minus its first pose, added to the flight pose).
/// </summary>
public sealed record JumpPackAnimations(string? RunClip, float RunLength, float FromImpact, float MinSpeed, float Blend, float RunEnd, string? NoiseClip, float NoiseLength, IReadOnlyList<string> Notes, bool NoiseAdditive = false)
{
    public const string Graph = "MP_SM_NAV_JP_Sim";

    public static JumpPackAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength, Func<string, bool>? isAdditive = null)
    {
        var notes = new List<string>();
        if (graphs.FirstOrDefault(x => x.Name.Equals(Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {Graph} not found"); return new(null, 0f, 0f, 0.5f, 0.167f, 0f, null, 0f, notes); }
        string? run = null; float runLength = 0f, fromImpact = 0f, minSpeed = 0.5f, blend = 0.167f, runEnd = 0f;
        var impact = g.AllStates.FirstOrDefault(x => x.State.Name == "GroundPoundImpact");
        var runState = impact.Machine?.States.FirstOrDefault(s => s.Name == "GroundPoundRun");
        if (impact.State is null || runState is null) notes.Add($"{Graph}: no GroundPoundImpact -> GroundPoundRun states");
        else
        {
            var toRun = impact.State.Transitions.FirstOrDefault(t => t.ToStateId == runState.Id);
            blend = toRun is null ? blend : g.EffectAt(toRun.Transition)?.Duration ?? blend;
            if (toRun?.ConditionText is { } c && System.Text.RegularExpressions.Regex.Match(c, @"FLT_Input_Nav_Speed in \[(-?[0-9.]+),") is { Success: true } m
                && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lo)) minSpeed = lo;
            if (GraphWalk.SequencerUnder(g, impact.State.Generator) is { } impactSeq)
            {
                float impactLength = animationLength(impactSeq.Animations[0]) / GraphWalk.Speed(impactSeq);
                fromImpact = (GraphWalk.Tag(impactSeq, "Branch", toRun?.BranchEvent ?? "BRANCH_to_Getup")?.Start ?? 0.3333f) * impactLength;
            }
            if (GraphWalk.SequencerUnder(g, runState.Generator) is { } runSeq)
            {
                run = runSeq.Animations[0];
                runLength = animationLength(run) / GraphWalk.Speed(runSeq);
                runEnd = (GraphWalk.Tag(runSeq, "Branch", "BRANCH_Run")?.Start ?? 1f) * runLength;
            }
            else notes.Add($"{Graph}: no clip in GroundPoundRun");
        }
        var noise = g.Clips.FirstOrDefault(c => c.Animation.Contains("Blend_90", StringComparison.OrdinalIgnoreCase) && c.Mode == 1);
        if (noise is null) notes.Add($"{Graph}: no flight noise clip");
        return new(run, runLength, fromImpact, minSpeed, blend, runEnd, noise?.Animation, noise is null ? 0f : animationLength(noise.Animation), notes, noise is not null && isAdditive?.Invoke(noise.Animation) == true);
    }
}

/// <summary>Everything pass 2 reads, loaded in one background task (as <see cref="FallAnimations.Load"/>).</summary>
public sealed record UpperBodyBundle(SwitchAnimations Switch, FlinchAnimations Flinch, DeathAnimations Death, JumpPackAnimations JumpPack)
{
    public static UpperBodyBundle Load(string gameDir)
    {
        var character = RigProfiles.Marine.LoadCharacter(gameDir);
        float? minHitReactionTime = null;
        try
        {
            using var archive = SmFormats.Obpk.ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage));
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == GameplayTuning.MarineBlueprint,
                (_, d) => minHitReactionTime = SmFormats.Baf.BafDocument.Read(d).Root.Table("extensions/health_ext")?.Float("min_hit_reaction_time"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { /* the default applies */ }
        var graphs = HavokBehaviorDetailReader.Read(character.Pack);
        Func<string, float> length = clip => character.LoadAnimation(clip).Animation.Duration;
        Func<string, bool> additive = clip => { try { return character.LoadBlendHint(clip) == AnimationBlendHint.Additive; } catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return false; } };
        return From(graphs, character.Skeleton.BoneNames, length, minHitReactionTime, additive);
    }

    public static UpperBodyBundle From(IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames, Func<string, float> length, float? minHitReactionTime = null, Func<string, bool>? isAdditive = null) =>
        new(SwitchAnimations.From(graphs, boneNames, length), FlinchAnimations.From(graphs, length, minHitReactionTime, isAdditive), DeathAnimations.From(graphs, length), JumpPackAnimations.From(graphs, length, isAdditive));
}
