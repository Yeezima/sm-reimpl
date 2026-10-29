using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmContent;

/// <summary>
/// Reads a jump pack (<see cref="JumpPackSpec"/>) from its wargear file (<c>sm_mp_abil_jumppack</c>, <c>csm_mp_abil_jumppack</c>):
/// <c>ability_data/ability_definitions/ability_jumppack/jump_info</c> (the default entry: no required upgrade), the ground
/// pound's <c>ability_groundpound</c>, and the triggered action <c>actn_gp_hit</c> (its default <c>actions_by_upgrade</c> entry).
/// Timing comes from the marine rig's behavior graph <see cref="Graph"/> (<see cref="WithTiming"/>). Since M43 every entry choice
/// follows <see cref="UpgradeScope"/> (perk variants: <c>mp_perk_jump_pack_*</c>, read by <see cref="MpClasses"/>).
/// </summary>
public static class JumpPackTuning
{
    public const string Ability = "ability_data/ability_definitions/ability_jumppack";
    public const string PoundAbility = "ability_data/ability_definitions/ability_groundpound";
    public const string PoundAction = "action_data/triggered_actions/actn_gp_hit/commands/actions_by_upgrade/actions";
    /// <summary>The lift-off's triggered action (M43: blasts only in the Blast Off perk's entry).</summary>
    public const string TakeOffAction = "action_data/triggered_actions/actn_take_off/commands";
    /// <summary>The behavior graph and its tags the timing is read from.</summary>
    public const string Graph = "MP_SM_NAV_JP_Sim", TakeOffEvent = "ACTN_Take_Off", RunBranch = "BRANCH_Run", JumpBranch = "BRANCH_Jump_To_Jump",
        GetupBranch = "BRANCH_to_Getup", RunLanding = "Non-Hammer Run Land Sequence", IdleLanding = "Non-Hammer Idle Land Sequence",
        PoundImpact = "GP Impack Sequencer";

    /// <summary>Whether a wargear file has a jump pack ability.</summary>
    public static bool IsJumpPack(BafDocument doc) => doc.Root.Table(Ability) is not null;

    /// <summary>The pack's values (timing placeholders: <see cref="WithTiming"/>), or null with a note when unusable.</summary>
    public static JumpPackSpec? Read(string name, BafDocument doc, List<string> notes)
    {
        if (doc.Root.Table(Ability) is not { } ability) { notes.Add($"{name}: no {Ability}"); return null; }
        var info = WeaponTuning.DefaultEntry(ability.Table("jump_info"), null);
        if (info is not { } j) { notes.Add($"{name}: no default jump_info entry"); return null; }
        float F(BafTable? t, string field, float fallback)
        {
            if (t?.Float(field) is float v && float.IsFinite(v) && v >= 0f) return v;
            notes.Add($"{name}: jump_info {field} missing; using {fallback}");
            return fallback;
        }
        var heights = Pairs(j.Table("jump_height_map"), "entry", "time", "height");
        int zero = heights.Count(p => p.Value <= 0f);
        if (zero > 0) notes.Add($"{name}: jump_height_map: {zero} entries with height 0 left out (read as list terminators)");
        heights = heights.Where(p => p.Value > 0f).OrderBy(p => p.Distance).ToList();
        if (heights.Count == 0) { notes.Add($"{name}: jump_height_map has no height"); return null; }
        var power = j.Table("power_settings");
        var costs = Pairs(power?.Table("power_used"), "entry", "time", "power_cost").OrderBy(p => p.Distance).ToList();
        if (costs.Count == 0) notes.Add($"{name}: no power_used entries; jumps cost nothing");

        bool smash = j["smash_can_be_used"] is { Type: BafType.Bool } b && b.AsBool();
        bool poundAbility = doc.Root.Table(PoundAbility) is not null;
        if (smash && !poundAbility) notes.Add($"{name}: smash_can_be_used without an {PoundAbility}; no ground pound");
        SplashSpec? pound = null;
        var reactions = new List<JumpReaction>();
        var gp = WeaponTuning.DefaultEntry(doc.Root.Table(PoundAction), "entry")?.Table("action/commands/area_of_effect");
        if (gp is { } aoe)
        {
            pound = Blast(aoe, name, "actn_gp_hit", notes);
            if (pound is not null) pound = pound with { Reactions = ReactionTuning.Entries(aoe.Table("hit_actions/commands/trigger_reaction")) };
            foreach (var e in aoe.Table("hit_actions/commands/trigger_reaction/reaction_by_intensity")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
                if (e.AsTable().Float("intensity") is float i && float.IsFinite(i) && e.AsTable().String("reaction_type") is { } r) reactions.Add(new JumpReaction(i, r));
            if (reactions.Count > 0) notes.Add($"{name}: ground pound reactions {string.Join(", ", reactions.Select(r => $"{r.Reaction} from {r.Intensity}"))} (M28: simulated)");
        }
        else if (smash) notes.Add($"{name}: no actn_gp_hit area_of_effect; the ground pound deals nothing");

        var landings = new List<JumpLanding>();
        foreach (var e in j.Table("action_land_regular_by_height")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
        {
            var t = e.AsTable();
            float drop = t.Float("distance_from_apex_to_land") ?? float.NaN;
            if (!float.IsFinite(drop) || drop < 0f) { notes.Add($"{name}: a landing entry without distance_from_apex_to_land"); continue; }
            if (t.Table("action/commands/area_of_effect") is { } la && la.Table("hit_actions/commands/do_damage") is not null && Blast(la, name, "landing", notes) is { } blast)
                landings.Add(new JumpLanding(drop, blast));
        }
        landings = landings.OrderBy(l => l.MinDrop).ToList();
        if (j.Table("action_land_smash_by_height")?.Entries.Any(e => e.Type == BafType.Table && e.AsTable().Table("action/commands/area_of_effect") is not null) == true)
            notes.Add($"{name}: action_land_smash_by_height has a blast; not simulated (actn_gp_hit is the pound's)");
        // M43: the take-off's blasts (actn_take_off; the file has them only in the Blast Off perk's entry, so a base read has none).
        var takeOffCommands = doc.Root.Table(TakeOffAction);
        var takeOff = UpgradeScope.Command(takeOffCommands, "area_of_effect") is null ? []
            : WeaponTuning.Blasts(UpgradeScope.Pick(takeOffCommands?.Table("actions_by_upgrade/actions"), "entry")?.Table("action/commands") ?? takeOffCommands, $"{name}: actn_take_off", notes);

        // M44 (the owner's request, 2026-10-28; user's knowledge of the original): the ground pound stuns every enemy it damages.
        if (PoundStunsWholeRadius && pound is not null) pound = StunAll(pound, name, notes);

        var spec = new JumpPackSpec(name, heights, costs, F(power, "max_power", 100f), F(power, "recharge_rate", 10f),
            F(j, "jumping_gravity", 20f), F(j, "jump_hang_gravity", 12f), F(j, "jump_hang_time", 0.5f), F(j, "falling_gravity", 30f),
            F(j, "jump_forward_speed", 16f), Math.Min(1f, F(j, "in_air_move_gain", 0f)), Math.Min(1f, F(j, "in_air_drift_gain", 0f)))
        {
            PoundEnabled = smash && poundAbility,
            PoundDelay = F(j, "smash_available_delay", 0f), PoundGravity = F(j, "smash_gravity", 75f), PoundSpeed = F(j, "smash_initial_speed_boost", 50f),
            PoundLimitAngle = Math.Min(180f, F(j, "smash_limit_angle", 50f)),
            Pound = pound, PoundReactions = reactions, Landings = landings, TakeOffBlasts = takeOff,
            // M45 (user's knowledge): letting go of Jump while rising cuts the rise; not in the data (no release field or branch).
            ReleaseCut = ReleaseStopsRise, ReleaseCutDelay = ReleaseRiseDelay, ReleaseCutDecay = ReleaseRiseDecay,
            // feel/jump-pack-2 (the owner's decision, 2026-10-29): tap = the dash, a medium hold = the small jump, a long hold = the full jump.
            HoldTiers = ThreeJumps,
            // feel/jump-pack (the owner's request, 2026-10-29): steering in the air; not in the data (its gains stay the released-stick rule).
            AirSpeed = AirControlSpeed, AirAcceleration = AirControlAcceleration,
        };
        // The base entry's max_power (no upgrade granted): the owner's capacity replaces it and a perk entry keeps the data's ratio to it.
        float baseMax = WeaponTuning.Entry(ability.Table("jump_info"), null, [])?.Table("power_settings")?.Float("max_power") is float bm && float.IsFinite(bm) && bm > 0f ? bm : spec.MaxPower;
        return WithOwnerCapacity(spec, baseMax, notes);
    }

    /// <summary>
    /// feel/jump-pack, the owner's decision (2026-10-29: "add more asm fuel baseline 300"): the Assault Marine / Raptor pack holds
    /// <see cref="OwnerCapacity"/> power (the file's units; the data's <c>max_power</c> 100; M44 had raised it to 110 for two full jumps). Costs
    /// (35 to 55 a jump) and the recharge (10/s; 15 with Air-Cooled Thrusters) stay the data's, so a full pack holds 5 full jumps and refills
    /// from empty in 30 s. A perk entry with its own <c>max_power</c> keeps the data's ratio to the base entry's (none differs on the install).
    /// </summary>
    public const float OwnerCapacity = 300f;
    /// <summary>feel/jump-pack, air control (ours, the owner's request): the top speed (m/s) the stick steers toward in the air (<see cref="JumpPackSpec.AirSpeed"/>).</summary>
    public const float AirControlSpeed = 8f;
    /// <summary>feel/jump-pack, air control (ours): the steering acceleration (m/s²; 8 m/s from a standstill in about 0.53 s).</summary>
    public const float AirControlAcceleration = 15f;

    /// <summary>The pack at the owner's capacity: <see cref="OwnerCapacity"/> × the entry's max_power / the base entry's (<paramref name="baseMax"/>).</summary>
    public static JumpPackSpec WithOwnerCapacity(JumpPackSpec spec, float baseMax, List<string> notes)
    {
        float capacity = OwnerCapacity * spec.MaxPower / MathF.Max(baseMax, 1e-3f);
        notes.Add($"{spec.Name}: max_power {spec.MaxPower} becomes {capacity} (the owner's baseline {OwnerCapacity} for the base entry's {baseMax}; owner's decision 2026-10-29)");
        return spec with { MaxPower = capacity };
    }

    /// <summary>
    /// M44, the owner's decision (2026-10-28, from the original's feel): a full pack holds exactly <see cref="FullJumpsFromFull"/> full jumps
    /// back to back. The data's <c>max_power</c> (100) holds one full jump (55: the cost of the longest hold) and a short one; the capacity is
    /// raised to <see cref="FullJumpsFromFull"/> × that cost (110) when it holds fewer. Costs and the recharge stay the data's.
    /// Superseded on 2026-10-29 by <see cref="OwnerCapacity"/> (300; <see cref="Read"/> no longer applies this); kept for the record and its test.
    /// </summary>
    public const int FullJumpsFromFull = 2;

    /// <summary>
    /// M45, the owner's knowledge of the original (2026-10-28: "let go of Space mid-jump to stop rising"): the middle jump. Letting go
    /// of Jump while a jump rises cuts the rise (<see cref="JumpPackSpec.ReleaseCut"/>). <c>jump_info</c> and <c>MP_SM_NAV_JP_Sim</c> have
    /// no release field, tag or branch, so the delay and the decay are ours. Superseded on 2026-10-29 by <see cref="ThreeJumps"/> (the owner:
    /// "revert to the old jump behavior"): off; the rule stays in the simulation (<see cref="JumpPackSpec.ReleaseCut"/>) for the record and its tests.
    /// </summary>
    public const bool ReleaseStopsRise = false;
    /// <summary>
    /// feel/jump-pack-2, the owner's decision (2026-10-29: "revert to the old jump behavior. space bar tap makes a hop dash, space bar medium hold
    /// makes a small jump, and space bar long hold is a full jump"): <see cref="JumpPackSpec.HoldTiers"/>. The tiers' boundaries are the data's
    /// (the dash's <c>BRANCH_Evade_to_Jump</c> 0.167 s, the height map's last hold 0.4 s); the small jump's height (the height map at 0.167 s) and
    /// its cut are ours.
    /// </summary>
    public const bool ThreeJumps = true;
    /// <summary>M45 (ours): the rise goes on unchanged this long after the release (the owner's "brief delay").</summary>
    public const float ReleaseRiseDelay = 0.1f;
    /// <summary>M45 (ours): the rise then decays with this time constant (s) to the apex.</summary>
    public const float ReleaseRiseDecay = 0.05f;

    /// <summary>M44: the owner's request (user's knowledge of the original): every enemy the pound damages is stunned (<see cref="StunAll"/>).</summary>
    public const bool PoundStunsWholeRadius = true;

    /// <summary>The pack with a capacity of at least <see cref="FullJumpsFromFull"/> full jumps (see there).</summary>
    public static JumpPackSpec WithFullJumps(JumpPackSpec spec, List<string> notes)
    {
        float full = spec.CostAt(spec.MaxHold);
        float wanted = FullJumpsFromFull * full;
        if (!(full > 0f) || spec.MaxPower + 1e-3f >= wanted) return spec;
        notes.Add($"{spec.Name}: max_power {spec.MaxPower} holds {MathF.Floor(spec.MaxPower / full + 1e-4f)} full jump(s) of {full}; raised to {wanted} for {FullJumpsFromFull} (owner's decision, M44)");
        return spec with { MaxPower = wanted };
    }

    /// <summary>
    /// The pound's blast with one reaction for its whole radius: the data's stun (the <c>reaction_by_intensity</c> entry of kind
    /// <see cref="ReactionKind.Stun"/>, with its knockback distances) from intensity 0, i.e. wherever the blast deals damage (inside the radius,
    /// in line of sight; the world applies reactions only above intensity 0). The data's own entries are kept on the spec's
    /// <see cref="JumpPackSpec.PoundReactions"/> for the record. Without a stun entry, a plain <see cref="ReactionKind.Stun"/>.
    /// </summary>
    public static SplashSpec StunAll(SplashSpec pound, string name, List<string> notes)
    {
        var stun = pound.Reactions.Where(r => r.Kind == ReactionKind.Stun).OrderBy(r => r.MinIntensity).Cast<ReactionEntry?>().FirstOrDefault()
                   ?? new ReactionEntry(0f, ReactionKind.Stun);
        notes.Add($"{name}: the ground pound stuns in its whole {pound.Radius} m radius (owner's request, user's knowledge; the data stun from intensity {stun.MinIntensity}, interrupt below)");
        return pound with { Reactions = [stun with { MinIntensity = 0f }] };
    }

    /// <summary>(key, value) points of a list's tables, file order; entries missing either or negative are left out.</summary>
    static List<DistancePoint> Pairs(BafTable? table, string entry, string key, string value) =>
        table?.Entries.Where(e => e.Type == BafType.Table && e.Name == entry)
            .Select(e => new DistancePoint(e.AsTable().Float(key) ?? float.NaN, e.AsTable().Float(value) ?? float.NaN))
            .Where(p => float.IsFinite(p.Distance) && float.IsFinite(p.Value) && p.Distance >= 0f && p.Value >= 0f).ToList() ?? [];

    static SplashSpec? Blast(BafTable aoe, string name, string what, List<string> notes)
    {
        static bool Bool(BafTable t, string f) => t[f] is { Type: BafType.Bool } e && e.AsBool();
        if (aoe.Table("shape")?.String("$ref") is { } shape && !shape.EndsWith("sphere", StringComparison.OrdinalIgnoreCase))
            notes.Add($"{name}: {what} blast shape '{shape}' read as a sphere");
        float radius = aoe.Table("shape")?.Float("radius") ?? 0f;
        if (WeaponTuning.DamageOf(aoe.Table("hit_actions/commands/do_damage"), name, notes) is not { } damage || !float.IsFinite(radius) || radius <= 0f)
        { notes.Add($"{name}: {what} blast without radius or damage"); return null; }
        var fallOff = WeaponTuning.Points(aoe.Table("fall_off"), "fall_off_entry", "intensity_value");
        if (fallOff.Zip(fallOff.Skip(1)).Any(p => p.Second.Distance < p.First.Distance)) fallOff = fallOff.OrderBy(p => p.Distance).ToList();
        return new SplashSpec(radius, damage, fallOff, Bool(aoe, "line_of_sight"), Bool(aoe, "ignore_self"), aoe.String("target_filter") ?? "");
    }

    /// <summary>
    /// The pack with its timing from <see cref="Graph"/> (tag start × animation length / playback speed): the take-off
    /// sequencer's <see cref="TakeOffEvent"/>, the run and idle landing sequencers' <see cref="RunBranch"/> and
    /// <see cref="JumpBranch"/>, and the pound impact sequencer's <see cref="GetupBranch"/>. A value that cannot be read
    /// keeps the placeholder, with a note.
    /// </summary>
    public static JumpPackSpec WithTiming(JumpPackSpec spec, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, List<string> notes)
    {
        var graph = graphs.FirstOrDefault(g => g.Name == Graph);
        if (graph is null) { notes.Add($"jump pack: graph {Graph} not found; timing placeholders stay"); return spec; }
        float? At(Func<BehaviorSequencer, bool> pick, string tag, string what)
        {
            var seq = graph.Sequencers.FirstOrDefault(s => s.Animations.Count > 0 && pick(s));
            var t = seq?.Tags.Where(x => x.Name == tag).OrderBy(x => x.Start).FirstOrDefault();
            if (seq is null || t is null) { notes.Add($"jump pack: {what} ({tag}) not found; placeholder stays"); return null; }
            float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
            try
            {
                float v = t.Start * animationLength(seq.Animations[0]) / speed;
                if (float.IsFinite(v) && v >= 0f) return v;
                notes.Add($"jump pack: {what} {v} s unusable; placeholder stays");
            }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException) { notes.Add($"jump pack: {what}: {e.Message}; placeholder stays"); }
            return null;
        }
        return spec with
        {
            TakeOffDelay = At(s => s.Tags.Any(x => x.Name == TakeOffEvent), TakeOffEvent, "take-off") ?? spec.TakeOffDelay,
            LandMoveLock = At(s => s.Name == RunLanding, RunBranch, "run landing") ?? spec.LandMoveLock,
            LandJumpLock = At(s => s.Name == RunLanding, JumpBranch, "run landing") ?? spec.LandJumpLock,
            LandIdleMoveLock = At(s => s.Name == IdleLanding, RunBranch, "idle landing") ?? spec.LandIdleMoveLock,
            LandIdleJumpLock = At(s => s.Name == IdleLanding, JumpBranch, "idle landing") ?? spec.LandIdleJumpLock,
            PoundRecovery = At(s => s.Name == PoundImpact, GetupBranch, "pound impact") ?? spec.PoundRecovery,
        };
    }

}
