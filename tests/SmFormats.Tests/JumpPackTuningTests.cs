using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>M14 reader: the jump pack's jump_info, ground pound blast, landing blasts and the jump graph's timing. Synthetic files only.</summary>
public class JumpPackTuningTests
{
    static readonly (float, float)[] Heights = [(0.1f, 4f), (0.1f, 4f), (0.25f, 13f), (0.4f, 18f), (0.41f, 0f), (0f, 0f)];   // as on the install: terminators, unsorted
    static readonly (float, float)[] Costs = [(0.25f, 35f), (0.1f, 35f), (0.4f, 55f)];

    static BafDocument Pack(bool smash = true, bool poundAbility = true, (float, float)? pound = null, (float, float, float)[]? landings = null) =>
        BafDocument.Read(SyntheticBafWriter.JumpPack(Heights, Costs, 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f, smash, poundAbility,
            pound ?? (25f, 50f), 0f, landings ?? [(15f, 4f, 15f), (0f, 4f, 5f)]));

    [Fact]
    public void The_default_jump_info_is_read_with_sorted_points_and_terminators_left_out()
    {
        var notes = new List<string>();
        var j = JumpPackTuning.Read("sm_mp_abil_jumppack", Pack(), notes)!;
        Assert.Equal([new(0.1f, 4f), new(0.1f, 4f), new(0.25f, 13f), new DistancePoint(0.4f, 18f)], j.HeightMap);
        Assert.Equal([new(0.1f, 35f), new(0.25f, 35f), new DistancePoint(0.4f, 55f)], j.PowerCost);
        // feel/jump-pack (owner's decision, 2026-10-29): the capacity baseline is 300 (the data's 100; M44 had 110). Recharge: the data's
        // default variant, not the perk's 15. Air control is on for every pack read (ours, the owner's request).
        Assert.Equal((300f, 10f), (j.MaxPower, j.RechargeRate));
        Assert.Contains(notes, n => n.Contains("becomes 300"));
        Assert.Equal((8f, 15f), (j.AirSpeed, j.AirAcceleration));
        Assert.Equal((20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f), (j.JumpingGravity, j.HangGravity, j.HangTime, j.FallingGravity, j.ForwardSpeed, j.MoveGain, j.DriftGain));
        Assert.Equal((true, 0f, 75f, 50f, 50f), (j.PoundEnabled, j.PoundDelay, j.PoundGravity, j.PoundSpeed, j.PoundLimitAngle));   // perk delay 2 not taken
        Assert.Contains(notes, n => n.Contains("2 entries with height 0"));
        Assert.Equal(0.4f, j.MaxHold);
        Assert.Equal(4f, j.HeightAt(0f));
        Assert.Equal(8.5f, j.HeightAt(0.175f), 4);
        Assert.Equal(18f, j.HeightAt(1f));
        Assert.Equal(45f, j.CostAt(0.325f), 4);
    }

    [Fact]
    public void The_ground_pound_blast_is_the_default_actn_gp_hit_entry_and_its_reactions_are_noted()
    {
        var notes = new List<string>();
        var j = JumpPackTuning.Read("p", Pack(), notes)!;
        var b = j.Pound!;
        Assert.Equal((7f, 25f, 50f, true, true, "target_all"), (b.Radius, b.Damage.Damage, b.Damage.ShieldDamage, b.LineOfSight, b.IgnoreSelf, b.TargetFilter));   // not the perk's 75
        Assert.Equal([new(0f, 1f), new(4.5f, 1f), new DistancePoint(7f, 0.05f)], b.FallOff);
        Assert.Equal([new JumpReaction(0.81f, "reaction_mp_stun"), new JumpReaction(0.8f, "reaction_mp_melee")], j.PoundReactions);
        Assert.Contains(notes, n => n.Contains("simulated") && n.Contains("reaction_mp_stun"));
        // M28 read them as stun from 0.81 and interrupt from 0.8; M44 (owner's request, user's knowledge): the data's stun (no knockback) for
        // the whole radius, i.e. from intensity 0.
        Assert.Equal([new ReactionEntry(0f, ReactionKind.Stun)], b.Reactions);
        Assert.Contains(notes, n => n.Contains("stuns in its whole 7 m radius"));
        // Landing blasts by drop, ascending.
        Assert.Equal([0f, 15f], j.Landings.Select(l => l.MinDrop));
        Assert.Equal((4f, 5f), (j.Landings[0].Blast.Radius, j.Landings[0].Blast.Damage.Damage));
        Assert.Equal(15f, j.Landings[1].Blast.Damage.Damage);
    }

    [Fact]
    public void Negative_controls_no_ground_pound_without_the_ability_or_smash_and_no_pack_without_jump_info()
    {
        var notes = new List<string>();
        Assert.False(JumpPackTuning.Read("a", Pack(poundAbility: false), notes)!.PoundEnabled);
        Assert.Contains(notes, n => n.Contains("a:") && n.Contains("no ground pound"));
        Assert.False(JumpPackTuning.Read("b", Pack(smash: false), notes)!.PoundEnabled);
        Assert.True(JumpPackTuning.Read("c", Pack(), notes)!.PoundEnabled);   // control
        var bolter = BafDocument.Read(SyntheticBafWriter.RangedWeapon(22f, 0.133f, 30, 270, 1.3f, 250f, [], 1f, 1f, 1f, 1f));
        Assert.False(JumpPackTuning.IsJumpPack(bolter));
        Assert.Null(JumpPackTuning.Read("bolter", bolter, notes));
        Assert.True(JumpPackTuning.IsJumpPack(Pack()));
        // Only zero heights: no pack.
        var flat = BafDocument.Read(SyntheticBafWriter.JumpPack([(0.4f, 0f)], Costs, 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f));
        Assert.Null(JumpPackTuning.Read("flat", flat, notes));
        Assert.Contains(notes, n => n.Contains("flat") && n.Contains("no height"));
    }

    static BehaviorTag T(string kind, float start, string name) => new(kind, start, 1f, name, null);

    [Fact]
    public void Timing_comes_from_the_jump_graph_tags_scaled_by_the_animation_length()
    {
        var graph = new BehaviorGraph(JumpPackTuning.Graph, [],
        [
            new BehaviorSequencer("Sequencer00", ["takeoff"], [T("Branch", 0.0769f, "BRANCH_To_Jump"), T("Action", 0.05f, JumpPackTuning.TakeOffEvent)], [1f]),
            new BehaviorSequencer(JumpPackTuning.RunLanding, ["runland"], [T("Branch", 0.4f, "BRANCH_Run"), T("Branch", 0.1f, "BRANCH_Jump_To_Jump")], [1f]),
            new BehaviorSequencer(JumpPackTuning.IdleLanding, ["idleland"], [T("Branch", 0.5f, "BRANCH_Run"), T("Branch", 0.25f, "BRANCH_Jump_To_Jump")], [2f]),
            new BehaviorSequencer(JumpPackTuning.PoundImpact, ["impact"], [T("Branch", 0.25f, "BRANCH_to_Getup"), T("Branch", 0.8f, "BRANCH_To_Idle_Getup")], [1f]),
        ]);
        float Length(string a) => a switch { "takeoff" => 4f, "runland" => 1f, "idleland" => 2f, "impact" => 1.2f, _ => throw new KeyNotFoundException(a) };
        var notes = new List<string>();
        var j = JumpPackTuning.WithTiming(JumpPackTuning.Read("p", Pack(), notes)!, [graph], Length, notes);
        Assert.Equal(0.2f, j.TakeOffDelay, 5);
        // The data's branch times (feel/jump-pack-2: the simulation scales them by SimConfig.LockTime, JumpPack.MoveLock / JumpLock).
        Assert.Equal((0.4f, 0.1f), (j.LandMoveLock, j.LandJumpLock));
        Assert.Equal((0.5f, 0.25f), (j.LandIdleMoveLock, j.LandIdleJumpLock));   // 2 s clip at speed 2
        Assert.Equal(0.3f, j.PoundRecovery, 5);
        Assert.DoesNotContain(notes, n => n.Contains("placeholder"));
        // Controls: another graph name keeps every placeholder; a missing tag keeps that one.
        var none = JumpPackTuning.WithTiming(JumpPackTuning.Read("p", Pack(), notes)!, [graph with { Name = "Other" }], Length, notes);
        Assert.Equal(new JumpPackSpec("x", [], [], 0, 0, 0, 0, 0, 0, 0, 0, 0).TakeOffDelay, none.TakeOffDelay);
        Assert.Contains(notes, n => n.Contains("not found; timing placeholders stay"));
        var partial = graph with { Sequencers = graph.Sequencers.Where(s => s.Name != JumpPackTuning.PoundImpact).ToList() };
        notes.Clear();
        var p2 = JumpPackTuning.WithTiming(JumpPackTuning.Read("p", Pack(), notes)!, [partial], Length, notes);
        Assert.Equal(0.356f, p2.PoundRecovery);
        Assert.Contains(notes, n => n.Contains("pound impact"));
    }

    [Fact]
    public void Classes_with_a_jump_pack_in_their_base_wargear_get_it_and_the_config_accepts_it()
    {
        var notes = new List<string>();
        var classes = new List<MpClass>
        {
            new("sm - assault marine", "assault", "space_marine", ["sm_mp_abil_jumppack", "pistol"], [], [], null),
            new("sm - tactical", "tactical", "space_marine", ["bolter"], [], [], null),
        };
        var wargear = new Dictionary<string, BafDocument>
        {
            ["sm_mp_abil_jumppack"] = Pack(),
            ["pistol"] = BafDocument.Read(SyntheticBafWriter.RangedWeapon(20f, 0.2f, 10, 50, 1.3f, 100f, [], 1f, 1f, 1f, 1f, slot: "pistol")),
            ["bolter"] = BafDocument.Read(SyntheticBafWriter.RangedWeapon(22f, 0.133f, 30, 270, 1.3f, 250f, [], 1f, 1f, 1f, 1f)),
        };
        var built = MpClasses.Build(classes, wargear, [], _ => 1f, notes);
        Assert.Single(built.JumpPacks);
        Assert.Equal((0, -1), (built.Loadouts[0].JumpPack, built.Loadouts[1].JumpPack));
        var config = GameplayTuning.WithClasses(new SimConfig(), built.Weapons, built.Loadouts, built.Thrown, built.JumpPacks)!;
        Assert.Equal("sm_mp_abil_jumppack", Loadouts.JumpPack(config, 0)!.Name);
        Assert.Null(Loadouts.JumpPack(config, 1));
        // Refused: a class naming a pack that does not exist, a gain above 1, decreasing height points, a negative value.
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), built.Weapons, built.Loadouts, built.Thrown, []));
        var j = built.JumpPacks[0];
        foreach (var bad in new[] { j with { MoveGain = 1.5f }, j with { HeightMap = [new(0.4f, 18f), new(0.1f, 4f)] }, j with { HangTime = -1f }, j with { HeightMap = [] }, j with { TakeOffDelay = float.NaN },
                     j with { ReleaseCutDelay = -0.1f }, j with { ReleaseCutDecay = float.NaN } })
            Assert.Null(GameplayTuning.WithClasses(new SimConfig(), built.Weapons, built.Loadouts, built.Thrown, [bad]));
    }

    [Fact]
    public void A_pack_read_through_the_classes_has_the_three_jumps_not_the_M45_cut_which_still_works_when_switched_on()
    {
        var classes = new List<MpClass> { new("sm - assault marine", "assault", "space_marine", ["sm_mp_abil_jumppack", "pistol"], [], [], null) };
        var wargear = new Dictionary<string, BafDocument>
        {
            ["sm_mp_abil_jumppack"] = Pack(),
            ["pistol"] = BafDocument.Read(SyntheticBafWriter.RangedWeapon(20f, 0.2f, 10, 50, 1.3f, 100f, [], 1f, 1f, 1f, 1f, slot: "pistol")),
        };
        var built = MpClasses.Build(classes, wargear, [], _ => 1f, []);
        var config = GameplayTuning.WithClasses(new SimConfig(), built.Weapons, built.Loadouts, built.Thrown, built.JumpPacks)!;
        var pack = Loadouts.JumpPack(config, 0)!;
        // feel/jump-pack-2 (the owner, 2026-10-29: "revert to the old jump behavior"): the three jumps, the M45 cut off (its values kept).
        Assert.True(pack.HoldTiers);
        Assert.False(pack.ReleaseCut);
        Assert.Equal((JumpPackTuning.ReleaseRiseDelay, JumpPackTuning.ReleaseRiseDecay), (pack.ReleaseCutDelay, pack.ReleaseCutDecay));
        // The spec's own defaults are the reader's constants (a synthetic spec with the cut switched on behaves as a read one).
        var bare = new JumpPackSpec("x", [new(0.4f, 18f)], [], 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0f, 0f);
        Assert.False(bare.ReleaseCut);
        Assert.Equal((JumpPackTuning.ReleaseRiseDelay, JumpPackTuning.ReleaseRiseDecay), (bare.ReleaseCutDelay, bare.ReleaseCutDecay));

        float Apex(SimConfig c, int held)
        {
            var s = Combat.Respawned(new PlayerState(1, default, System.Numerics.Vector3.Zero, 0f, true) { Class = 0 }, c);
            float top = 0f;
            for (int k = 0; k < 400 && s.Jump != JumpPhase.Landed; k++)
            {
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, k < held ? InputButtons.Jump : InputButtons.None, 0f), c, out _);
                top = MathF.Max(top, s.Position.Z);
            }
            return top;
        }
        var off = config with { JumpPacks = [pack with { HoldTiers = false }] };
        var cut = config with { JumpPacks = [pack with { HoldTiers = false, ReleaseCut = true }] };
        // No dash here (the tiers need one): the data's hold map, as with the tiers off.
        Assert.Equal(Apex(off, 20), Apex(config, 20));
        Assert.Equal(Apex(off, 400), Apex(config, 400));
        Assert.Equal(Apex(off, 400), Apex(cut, 400));                    // M45 switched on: held to the apex, the same jump
        Assert.True(Apex(cut, 20) < Apex(off, 20) - 4f);                 // let go while rising: cut
        Assert.InRange(Apex(config, 400), 17f, 18.05f);
    }

    [Fact]
    public void The_owners_300_capacity_keeps_a_perk_entrys_ratio_to_the_base_entry()
    {
        // feel/jump-pack (owner's decision, 2026-10-29): base max_power 100 -> 300; a perk entry with its own 150 keeps the data's ratio (450).
        var doc = BafDocument.Read(SyntheticBafWriter.JumpPack(Heights, Costs, 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f, perkMaxPower: 150f));
        Assert.Equal(300f, JumpPackTuning.Read("p", doc, [])!.MaxPower);
        using (UpgradeScope.Grant(["mp_perk_jump_pack_reduced_cooldown"]))
        {
            var perk = JumpPackTuning.Read("p", doc, [])!;
            Assert.Equal((450f, 15f), (perk.MaxPower, perk.RechargeRate));
        }
        // The same perk entry with the base's 100 (as on the install): 300 too.
        using (UpgradeScope.Grant(["mp_perk_jump_pack_reduced_cooldown"]))
            Assert.Equal(300f, JumpPackTuning.Read("p", Pack(), [])!.MaxPower);
    }
}
