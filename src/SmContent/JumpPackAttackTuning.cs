using System.Numerics;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmContent;

/// <summary>
/// Protocol 30: the jump pack's two melee moves beside the ground pound, read from the pack's wargear (<c>sm_mp_abil_jumppack</c>, identical in
/// <c>csm_mp_abil_jumppack</c>) and the marine rig's behaviour graphs:
/// <list type="bullet">
/// <item>the dash attack (<see cref="DashAttackSpec"/>): <see cref="DashAbility"/> and the <see cref="DashGraph"/> sequencers of the "Jump Dash Attack"
/// state (<see cref="DashClip"/>, <see cref="DashHitClip"/>, <see cref="DashCollisionClip"/>, <see cref="DashStopClip"/>);</item>
/// <item>the sword slash (<see cref="SlashSpec"/>): <see cref="SlashAbility"/> and the <see cref="JumpPackTuning.Graph"/> "Melee Dash Impact"
/// sequencer (<see cref="SlashClip"/>), the pound-or-slash height from <c>jump_info/action_land_smash_by_height</c>.</item>
/// </list>
/// Every entry choice follows <see cref="UpgradeScope"/>, so the perk variants (Furious Charge's 100 + 50 and stun, the heal-on-hit perks) come
/// from <see cref="MpClasses"/>' variant reads. Values in spec/behavior.md "Dash attack" and "Sword slash".
/// </summary>
public static class JumpPackAttackTuning
{
    public const string DashAbility = "ability_data/ability_definitions/ability_dash_attack";
    public const string SlashAbility = "ability_data/ability_definitions/ability_melee_dash";
    public const string DashGraph = EvadeTuning.Graph;
    public const string DashClip = "SM_JMP_DashAtk_F", DashHitClip = "SM_MEL_PA_XATK_Combo3FIN", DashCollisionClip = "SM_NAV_Bullrush2Idle_F_Collision_01",
        DashStopClip = "SM_NAV_Sprint_to_Idle_F";
    public const string SlashClip = "SM_MEL_CS_XATK_Hv1_LS_2_7m";

    /// <summary>
    /// The owner's decision (2026-10-28; "make it always stun, but note that as an estimation that needs to be reviewed by me later"): the dash
    /// attack's primary hit stuns. The data's default entry interrupts (<c>reaction_mp_melee</c>); only Furious Charge's entry stuns (kept as read).
    /// </summary>
    public const bool DashAttackAlwaysStuns = true;

    /// <summary>
    /// The highest the feet may be at the dash attack's press (metres): the graph's <c>FLT_Distance_Above_Ground in [0, 3]</c> on the "Jumppack
    /// Evade" state's <c>BTN_Melee_Attack</c> transition (read from <c>smtool graph-dump SM_Base_MP</c>; the reader does not decode transition
    /// conditions, so the value is carried here).
    /// </summary>
    public const float DashMaxHeight = 3f;

    /// <summary>
    /// feel/jump-pack-2, the owner's request (2026-10-29: "when an assault marine or raptor hops and then right clicks into a running charge, he should
    /// collide with walls and play the same 'impact ricochet' animation that plays when a charge ... hits a target"): the dash attack's lunge meets walls
    /// from its first tick (<see cref="RushSpec.WallFrom"/> 0; the data's <c>WallCollisionTest</c> tag starts 0.300 s in, before which a wall only blocked
    /// and the body slid along it). The stop is the data's wall state (<c>SM_NAV_Bullrush2Idle_F_Collision_01</c>, the clip the bull rush plays when it
    /// connects). Owner's decision.
    /// </summary>
    public const bool DashWallsFromStart = true;
    /// <summary>
    /// feel/jump-pack-2 (ours, for the owner's request above): a wall within this angle of head-on (degrees) stops the lunge: the wall took at least
    /// 1 − sin²(60°) = a quarter of the tick's motion. The sprint's <c>max_horz_angle_for_wall_hit</c> (25, the bull rush's) let a lunge slide along any
    /// wall met more than 25 degrees off head-on.
    /// </summary>
    public const float DashWallHitAngle = 60f;

    /// <summary>The pack with its dash attack and sword slash (each null with a note when something is missing; a base read only notes).</summary>
    public static JumpPackSpec With(JumpPackSpec spec, string name, BafDocument doc, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength,
        Func<string, HavokExtractedMotion?>? extractedMotion, IReadOnlyDictionary<string, string>? mapping, List<string> notes) =>
        spec with
        {
            DashAttack = extractedMotion is null ? null : ReadDashAttack(name, doc, graphs, animationLength, extractedMotion, mapping, notes),
            Slash = ReadSlash(name, doc, graphs, animationLength, mapping, notes),
        };

    /// <summary>The dash attack, or null with a note.</summary>
    public static DashAttackSpec? ReadDashAttack(string name, BafDocument doc, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength,
        Func<string, HavokExtractedMotion?> extractedMotion, IReadOnlyDictionary<string, string>? mapping, List<string> notes)
    {
        if (doc.Root.Table(DashAbility) is not { } ability) { notes.Add($"{name}: no ability_dash_attack; no dash attack"); return null; }
        var graph = graphs.FirstOrDefault(g => g.Name.Equals(DashGraph, StringComparison.OrdinalIgnoreCase));
        if (graph is null) { notes.Add($"{name}: graph {DashGraph} not found; no dash attack"); return null; }
        var primaryActions = ability.Table("primary_target_actions/hit_sim/actions");
        var secondaryActions = ability.Table("secondary_target_actions/hit_sim/actions");
        if (RushTuning.Damage(primaryActions, name, notes) is not { } primary) { notes.Add($"{name}: ability_dash_attack has no primary damage; no dash attack"); return null; }
        var secondary = RushTuning.Damage(secondaryActions, name, notes) ?? new DamageSpec(0f, 0f, 1f, 1f, 1f, 1f, false);
        var reaction = ReactionTuning.HitReaction(primaryActions, mapping);
        if (DashAttackAlwaysStuns && reaction.Kind != ReactionKind.Stun)
        {
            // The ability's own stun entry (the Furious Charge entry's reaction_mp_stun, through the reaction mapping: its intensity, knockback distances
            // and duration as written), not the interrupt entry with its kind swapped; a plain stun without one.
            var stun = StunEntry(primaryActions, mapping);
            notes.Add($"{name}: the dash attack's primary hit stuns (owner's estimate, 2026-10-28; the data's reaction is {reaction.Kind}): " +
                      (stun is { } st ? $"the ability's stun entry (from intensity {st.MinIntensity}, knockback {st.Backward} m over {st.Duration} s)" : "a plain stun (no stun entry in the ability)"));
            reaction = stun ?? new ReactionEntry(0f, ReactionKind.Stun);
        }
        float range = ability.Table("ability_metadata/generic_metadata")?.Float("max_range") is float r && float.IsFinite(r) && r > 0f ? r : 4f;
        float interval = ability.Float("min_time_between_activations") is float m && float.IsFinite(m) && m >= 0f ? m : 1f;

        // The lunge: no input ends it before BRANCH_toIdle (the "Dash Attack State" has only the hit, wall and to-idle transitions).
        if (Move(graph, DashClip, ["BRANCH_toIdle"], [], [], [], true, out var attackSeq) is not { } attack) return null;
        // The states after it share the "Jump Dash Attack" state's exits: Run (stick), Evade (Jump), ToRanged (fire), Evade_End.
        string[] end = ["BRANCH_Evade_End"], run = ["BRANCH_Run"], evade = ["BRANCH_Evade"], melee = ["BRANCH_Melee_Combo"];
        if (Move(graph, DashHitClip, end, run, evade, melee, false, out _) is not { } hit
            || Move(graph, DashCollisionClip, end, run, evade, melee, false, out _) is not { } collision
            || Move(graph, DashStopClip, end, run, evade, melee, false, out _) is not { } stop) return null;
        float length = attack.ClipLength;
        float wallFrom = (attackSeq!.Tags.Where(t => t.Kind == "WallCollisionTest").Select(t => (float?)t.Start).Min() ?? 0f) * length;
        var charge = new RushSpec("ability_dash_attack", attack, collision, primary, reaction, secondary, ReactionTuning.HitReaction(secondaryActions, mapping), range, interval)
        {
            // Ours: contacts count over the whole lunge (to BRANCH_toIdle; its swing runs 0.30 to 0.86 s); no hyper armour (no tag).
            // feel/jump-pack-2 (the owner's request, 2026-10-29): walls end it from its first tick (the data's WallCollisionTest starts later) and
            // up to DashWallHitAngle off head-on (the sprint's 25 degrees let a lunge slide along most walls).
            ContactUntil = attack.End, WallFrom = DashWallsFromStart ? 0f : Math.Min(wallFrom, attack.End), HyperArmourUntil = 0f,
            WallHitAngle = DashWallHitAngle,
        };
        var spec = new DashAttackSpec(charge, hit, stop) { MaxHeight = DashMaxHeight };
        notes.Add($"{name}: the dash attack: {attack.At(attack.End).Y:F1} m lunge to {attack.End:F3} s (walls from {charge.WallFrom:F3} s within {charge.WallHitAngle:F0} degrees of head-on; the data's WallCollisionTest {wallFrom:F3} s), hit {primary.Damage} + {primary.ShieldDamage} shield " +
                  $"({reaction.Kind}), others {secondary.Damage}; within {DashMaxHeight} m of the ground; {interval} s spacing");
        return spec;

        RushMove? Move(BehaviorGraph g, string clip, string[] endBranches, string[] moveBranches, string[] evadeBranches, string[] meleeBranches, bool needMotion,
            out BehaviorSequencer? sequencer)
        {
            var seq = g.Sequencers.FirstOrDefault(s => s.Animations.Count > 0 && s.Animations[0].Equals(clip, StringComparison.OrdinalIgnoreCase));
            sequencer = seq;
            if (seq is null) { notes.Add($"{name}: no {g.Name} sequencer plays {clip}; no dash attack"); return null; }
            float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
            float clipLength;
            HavokExtractedMotion? motion;
            try { clipLength = animationLength(clip) / speed; motion = extractedMotion(clip); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or NotSupportedException or ArgumentException
                or IndexOutOfRangeException or OverflowException)
            { notes.Add($"{name}: {clip}: {e.Message}; no dash attack"); return null; }
            if (!float.IsFinite(clipLength) || clipLength <= 0f) { notes.Add($"{name}: {clip} has no usable length; no dash attack"); return null; }
            Vector2[] path;
            if (motion is { Samples.Count: >= 2 } && Vector3.Dot(Vector3.Normalize(motion.Up), Vector3.UnitZ) >= 0.999f && Vector3.Dot(Vector3.Normalize(motion.Forward), Vector3.UnitY) >= 0.999f)
            {
                var first = new Vector2(motion.Samples[0].X, motion.Samples[0].Y);
                path = motion.Samples.Select(v => new Vector2(v.X, v.Y) - first).ToArray();
            }
            else if (needMotion) { notes.Add($"{name}: {clip} has no usable extracted motion; no dash attack"); return null; }
            else { notes.Add($"{name}: {clip} has no usable extracted motion; it stands still"); path = [Vector2.Zero, Vector2.Zero]; }
            float? Start(string[] names) => seq.Tags.Where(t => t.Kind == "Branch" && t.Name is { } n && names.Contains(n)).Select(t => (float?)t.Start).Min();
            float endAt = Math.Clamp(Start(endBranches) ?? 1f, 0f, 1f) * clipLength;
            float At(string[] names) => Start(names) is float f ? Math.Min(f * clipLength, endAt) : endAt;
            return new RushMove(clip, path, clipLength, endAt, At(moveBranches), At(evadeBranches), At(meleeBranches));
        }
    }

    /// <summary>
    /// The first stun reaction anywhere in a hit's actions (directly, or in any <c>actions_by_upgrade</c> entry whatever upgrades it needs), mapped
    /// through the multiplayer reaction mapping, or null.
    /// </summary>
    static ReactionEntry? StunEntry(BafTable? actions, IReadOnlyDictionary<string, string>? mapping)
    {
        if (actions is not { } a) return null;
        foreach (var e in ReactionTuning.Entries(a.Table("trigger_reaction"), mapping)) if (e.Kind == ReactionKind.Stun) return e;
        foreach (var block in a.Entries.Where(x => x.Type == BafType.Table && x.Name == "actions_by_upgrade").Select(x => x.AsTable()))
            foreach (var entry in block.Table("actions")?.Entries.Where(x => x.Type == BafType.Table && x.Name == "entry").Select(x => x.AsTable()) ?? [])
                foreach (var e in ReactionTuning.Entries(entry.Table("action/commands/trigger_reaction"), mapping))
                    if (e.Kind == ReactionKind.Stun) return e;
        return null;
    }

    /// <summary>The sword slash, or null with a note.</summary>
    public static SlashSpec? ReadSlash(string name, BafDocument doc, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength,
        IReadOnlyDictionary<string, string>? mapping, List<string> notes)
    {
        if (doc.Root.Table(SlashAbility) is not { } ability) { notes.Add($"{name}: no ability_melee_dash; no sword slash (every press in flight pounds)"); return null; }
        var graph = graphs.FirstOrDefault(g => g.Name.Equals(JumpPackTuning.Graph, StringComparison.OrdinalIgnoreCase));
        if (graph is null) { notes.Add($"{name}: graph {JumpPackTuning.Graph} not found; no sword slash"); return null; }
        // The ability's primary actions are empty: every target in the cone takes the secondary profile (ours, the report's recommendation).
        var actions = ability.Table("secondary_target_actions/hit_sim/actions");
        if (RushTuning.Damage(actions, name, notes) is not { } damage) { notes.Add($"{name}: ability_melee_dash has no damage; no sword slash"); return null; }
        if (RushTuning.Damage(ability.Table("primary_target_actions/hit_sim/actions"), name, []) is { } primary)
            notes.Add($"{name}: ability_melee_dash has a primary hit ({primary.Damage}) too; the slash deals its secondary profile to every target");
        var reaction = ReactionTuning.HitReaction(actions, mapping);
        float range = ability.Table("ability_metadata/generic_metadata")?.Float("max_range") is float r && float.IsFinite(r) && r > 0f ? r : 4f;
        // The impact sequencer restarts the clip on landing: the one playing it with the hit event (the flight's has none).
        var seq = graph.Sequencers.FirstOrDefault(s => s.Animations.Count > 0 && s.Animations[0].Equals(SlashClip, StringComparison.OrdinalIgnoreCase)
            && s.Tags.Any(t => t.Kind == "Event" && t.Name == "ACTN_Hit_Targets_Multiplayer"));
        if (seq is null) { notes.Add($"{name}: no {graph.Name} sequencer plays {SlashClip} with a hit event; no sword slash"); return null; }
        float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
        float length;
        try { length = animationLength(SlashClip) / speed; }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException) { notes.Add($"{name}: {SlashClip}: {e.Message}; no sword slash"); return null; }
        if (!float.IsFinite(length) || length <= 0f) { notes.Add($"{name}: {SlashClip} has no usable length; no sword slash"); return null; }
        float hit = (seq.Tags.Where(t => t.Kind == "Event" && t.Name == "ACTN_Hit_Targets_Multiplayer").Select(t => (float?)t.Start).Min() ?? 0f) * length;
        float recovery = (seq.Tags.Where(t => t.Kind == "Branch" && t.Name == "BRANCH_Run").Select(t => (float?)t.Start).Min() ?? 1f) * length;
        // The pound from the smash-landing table's highest entry up (install: entries 0 and 8; the graph's own split is the same 8 m).
        var smash = WeaponTuning.DefaultEntry(doc.Root.Table(JumpPackTuning.Ability)?.Table("jump_info"), null)?.Table("action_land_smash_by_height");
        float split = smash?.Entries.Where(e => e.Type == BafType.Table).Select(e => e.AsTable().Float("distance_from_apex_to_land") ?? 0f)
            .Where(d => float.IsFinite(d) && d > 0f).DefaultIfEmpty(8f).Max() ?? 8f;
        // feel/jump-pack (the owner's request, 2026-10-29): the data's BRANCH_Run (0.638 s) is the clip's; the slash frees the player at SlashRecovery.
        float data = Math.Max(recovery, hit);
        // feel/jump-pack-2: where the two sequencers play the clip (their generators' crops, local clip time): the impact from its crop start
        // (the overhead strike), the flight up to its crop end (the wind-up). The hit and recovery times above stay as before (clip length based).
        var flight = graph.Sequencers.FirstOrDefault(s => !ReferenceEquals(s, seq) && s.Animations.Count > 0 && s.Animations[0].Equals(SlashClip, StringComparison.OrdinalIgnoreCase));
        float raw = length * speed;   // the clip's own length (crops are local clip time)
        float impactStart = seq.Crops.Count > 0 && float.IsFinite(seq.Crops[0].Start) ? Math.Clamp(seq.Crops[0].Start, 0f, raw) : 0f;
        float flightEnd = flight is { Crops.Count: > 0 } && float.IsFinite(flight.Crops[0].End) ? Math.Clamp(raw - flight.Crops[0].End, 0f, raw) : 0f;
        var spec = new SlashSpec("ability_melee_dash", SlashClip, length, hit, SnappySlash ? Math.Max(SlashRecovery, hit) : data, damage, reaction, range)
        {
            Heal = MeleeTuning.HealOf(actions), MinPoundHeight = split, ImpactClipStart = impactStart, FlightClipEnd = flightEnd,
        };
        notes.Add($"{name}: the sword slash's clip: the flight plays {SlashClip} from 0 to {flightEnd:F3} s (the wind-up), the impact from {impactStart:F3} s (the strike)");
        if (SnappySlash)
            spec = spec with { DiveSpeed = SlashDiveSpeed, DiveMinDown = SlashDiveMinDown, DiveReach = SlashDiveReach, LungeSpeed = SlashLungeSpeed, LungeTime = SlashLungeTime };
        notes.Add($"{name}: the sword slash below {split} m: {damage.Damage} melee ({reaction.Kind}) to every target in the melee cone {hit:F3} s after landing, acts again at {spec.Recovery:F3} s" +
                  (SnappySlash ? $" (the data's run branch {data:F3} s; feel/jump-pack: a {SlashDiveSpeed} m/s dive toward the aim, a {SlashLungeSpeed} m/s lunge for {SlashLungeTime} s on landing)" : ""));
        return spec;
    }

    /// <summary>
    /// feel/jump-pack: the owner's request of 2026-10-29 ("the air slash ... feels terrible to use"; "when air slash landing you should also move forward
    /// a bit"): the slash's own dive, landing lunge and short recovery (<see cref="SlashSpec.DiveSpeed"/>). The values below are ours.
    /// </summary>
    public const bool SnappySlash = true;
    /// <summary>Seconds from the slash's landing until the player acts again (the data's <c>BRANCH_Run</c> 0.638 s; the pound's get-up 0.356 s). Ours.</summary>
    public const float SlashRecovery = 0.3f;
    /// <summary>The slash's straight dive toward the aim (m/s; the pound's dive starts at 50 and accelerates at 75 m/s²). Ours.</summary>
    public const float SlashDiveSpeed = 40f;
    /// <summary>Least angle below level of the slash's dive (degrees; 40 = the data's 50° smash limit from straight down). Ours.</summary>
    public const float SlashDiveMinDown = 40f;
    /// <summary>How far the aim ray looks for the slash's target point (m). Ours.</summary>
    public const float SlashDiveReach = 10f;
    /// <summary>The landing lunge's speed (m/s) and time (s): about 1.2 m, then the ground motor's stop (about 0.6 m more). Ours.</summary>
    public const float SlashLungeSpeed = 6f, SlashLungeTime = 0.2f;
}
