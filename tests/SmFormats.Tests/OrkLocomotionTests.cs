using SmContent;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// Ork O7a: the engine-free rules of the networked Ork. Which look an Ork wears (the wire's own, else one chosen by player id), the Ork locomotion plan built from
/// the rig's hip-fire and locomotion data (four directions with walk and run bands, pitch variants, the sprint, the idle), the pitch weights, and the foot bones the
/// gait is measured at. Synthetic data only: the plan is fed hand-made records, no original bytes.
/// </summary>
public class OrkLocomotionTests
{
    // ---------------------------------------------------------------- the look

    [Fact]
    public void The_look_names_match_the_wire_and_the_client_order()
    {
        Assert.Equal(Races.OrkLooks, OrkLookRules.Names.Count);
        Assert.Equal("boy_ranged", OrkLookRules.Names[0]);
        Assert.Equal("boy_ardboy", OrkLookRules.Names[^1]);
        Assert.Equal(OrkLookRules.Names.Count, OrkLookRules.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Each_look_has_a_character_key_made_once()
    {
        Assert.Equal(OrkLookRules.Names.Select(n => "ork:" + n), OrkLookRules.Keys);
        Assert.Same(OrkLookRules.Keys[2], OrkLookRules.Keys[2]);
    }

    [Fact]
    public void A_look_the_wire_names_is_worn_whoever_the_player_is()
    {
        for (byte look = 0; look < Races.OrkLooks; look++)
            foreach (int id in new[] { 0, 1, 7, 12, 1000 })
            {
                Assert.Equal(look, OrkLookRules.Resolve(look, id));
                Assert.Equal(OrkLookRules.Names[look], OrkLookRules.NameOf(look, id));
            }
    }

    [Fact]
    public void Automatic_is_chosen_by_player_id_the_same_everywhere_and_covers_the_first_four_looks()
    {
        var seen = new HashSet<int>();
        for (int id = 1; id <= 8; id++)
        {
            int look = OrkLookRules.Resolve(Races.OrkLookAutomatic, id);
            Assert.InRange(look, 0, OrkLookRules.AutomaticLooks - 1);
            Assert.Equal(look, OrkLookRules.Resolve(Races.OrkLookAutomatic, id));   // deterministic: every client draws the same look
            Assert.Equal(look, OrkLookRules.Resolve(200, id));                      // any byte past the looks is automatic too
            if (id <= OrkLookRules.AutomaticLooks) seen.Add(look);
        }
        Assert.Equal(OrkLookRules.AutomaticLooks, seen.Count);   // four consecutive ids wear four different looks
        Assert.DoesNotContain(((List<string>)[.. OrkLookRules.Names]).IndexOf("boy_ardboy"), seen);   // the armoured boy is a choice, not a draw
    }

    [Fact]
    public void Automatic_survives_an_unset_or_negative_player_id()
    {
        Assert.Equal(0, OrkLookRules.Resolve(Races.OrkLookAutomatic, 0));
        Assert.InRange(OrkLookRules.Resolve(Races.OrkLookAutomatic, -5), 0, OrkLookRules.AutomaticLooks - 1);
        Assert.InRange(OrkLookRules.Resolve(Races.OrkLookAutomatic, int.MaxValue), 0, OrkLookRules.AutomaticLooks - 1);
    }

    // ---------------------------------------------------------------- pitch weights

    static readonly OrkPitchClip Triple = new("level", "down", -80f, "up", 80f);

    [Theory]
    [InlineData(0f, 0f, 1f, 0f)]
    [InlineData(40f, 0f, 0.5f, 0.5f)]
    [InlineData(80f, 0f, 0f, 1f)]
    [InlineData(120f, 0f, 0f, 1f)]
    [InlineData(-40f, 0.5f, 0.5f, 0f)]
    [InlineData(-80f, 1f, 0f, 0f)]
    [InlineData(-200f, 1f, 0f, 0f)]
    public void The_aim_pitch_blends_the_level_clip_into_the_down_or_up_clip(float degrees, float down, float level, float up)
    {
        var (d, l, u) = Triple.WeightsAt(degrees);
        Assert.Equal(down, d, 4); Assert.Equal(level, l, 4); Assert.Equal(up, u, 4);
        Assert.Equal(1f, d + l + u, 4);
    }

    [Fact]
    public void A_clip_without_pitch_variants_stays_level_and_a_bad_pitch_is_level()
    {
        var flat = new OrkPitchClip("level", null, 0f, null, 0f);
        foreach (float degrees in new[] { -60f, 0f, 60f }) Assert.Equal((0f, 1f, 0f), flat.WeightsAt(degrees));
        Assert.Equal((0f, 1f, 0f), Triple.WeightsAt(float.NaN));
        Assert.Equal((0f, 1f, 0f), Triple.WeightsAt(float.PositiveInfinity));
        // Only an upward variant: a downward pitch has nothing to blend toward.
        var upOnly = new OrkPitchClip("level", null, 0f, "up", 80f);
        Assert.Equal((0f, 1f, 0f), upOnly.WeightsAt(-30f));
        Assert.Equal((0f, 0.625f, 0.375f), upOnly.WeightsAt(30f));
    }

    // ---------------------------------------------------------------- the plan

    static AimSpeedBand Band(float speed, string level, bool pitch = true) =>
        new(speed, pitch ? [new AimClip(-80f, level + "_dn", 2f), new AimClip(0f, level, 2f), new AimClip(80f, level + "_up", 2f)] : [new AimClip(0f, level, 2f)]);

    static AimDirection Direction(float angle, string label, bool pitch = true, bool withRun = true) =>
        new(angle, withRun ? [Band(0f, "spot", pitch), Band(2f, "walk_" + label, pitch), Band(6f, "run_" + label, pitch)] : [Band(0f, "spot", pitch), Band(2f, "walk_" + label, pitch)]);

    static HipFireSet Set(HipFireWeaponClass family, params AimDirection[] directions) =>
        new(family, "nav", "move", directions, "fire", null, null, null, null, null, null, null, null, null, null, []);

    static readonly AimDirection[] Four = [Direction(-180f, "B"), Direction(-90f, "L"), Direction(0f, "F"), Direction(90f, "R")];

    static LocomotionAnimations Loco(params SprintClip[] sprint) =>
        new([], [new SprintSet(SprintFamily.Rifle, "nav", "Sprint", sprint, "FLT_RunSpeedMultiplier", 0.333f)], []);

    static RigClipTable Table(bool toss = true) => new()
    {
        IdleCandidates = ["idle_a", "idle_b"],
        TossClip = toss ? "toss" : null,
        AimStances = new Dictionary<string, AimClipTriple>
        {
            ["rifle:wos"] = new(["idle_dn"], ["idle_gun"], ["idle_up"], AdditivePitch: true),
            ["pistol:wos"] = new(["idle_dn"], ["idle_pistol"], ["idle_up"], AdditivePitch: true),
        },
    };

    static bool Everything(string _) => true;

    [Fact]
    public void The_plan_has_four_directions_of_walk_and_run_clips_with_pitch_variants_in_angle_order()
    {
        var hip = new HipFireAnimations([Set(HipFireWeaponClass.Rifle, Four[3], Four[0], Four[2], Four[1])]);
        var plan = OrkLocomotionPlan.Build(hip, Loco(new SprintClip(0f, "sprint", 2f)), Table(), Everything);
        var gun = Assert.IsType<OrkMoveSet>(plan.Gun);
        Assert.Equal([-180f, -90f, 0f, 90f], gun.Directions.Select(d => d.AngleDegrees));
        var forward = gun.Directions.Single(d => d.AngleDegrees == 0f);
        Assert.Equal("walk_F", forward.Walk.Level);
        Assert.Equal("run_F", forward.Run.Level);
        Assert.Equal(("run_F_dn", -80f, "run_F_up", 80f), (forward.Run.Down, forward.Run.DownDegrees, forward.Run.Up, forward.Run.UpDegrees));
        Assert.Equal(("idle_gun", "idle_dn", "idle_up"), (gun.Idle, gun.IdleDown, gun.IdleUp));
        Assert.Equal("sprint", plan.Sprint);
        Assert.Equal(0.333f, plan.SprintBlend);
        Assert.Equal("toss", plan.Toss);
        Assert.Null(plan.Pistol);                                  // no pistol data: no pistol set ...
        Assert.Same(gun, plan.For(pistol: true));                  // ... and the gun's stands in
        Assert.Contains(plan.Notes, n => n.Contains("Pistol", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pistol_set_is_level_only_and_has_its_own_idle()
    {
        var hip = new HipFireAnimations([Set(HipFireWeaponClass.Rifle, Four), Set(HipFireWeaponClass.Pistol, Direction(-180f, "pB", false), Direction(-90f, "pL", false), Direction(0f, "pF", false), Direction(90f, "pR", false))]);
        var plan = OrkLocomotionPlan.Build(hip, Loco(new SprintClip(0f, "sprint", 2f)), Table(), Everything);
        var pistol = Assert.IsType<OrkMoveSet>(plan.Pistol);
        Assert.All(pistol.Directions, d =>
        {
            Assert.Null(d.Run.Down); Assert.Null(d.Run.Up); Assert.Null(d.Walk.Down); Assert.Null(d.Walk.Up);
            Assert.Equal((0f, 1f, 0f), d.Run.WeightsAt(45f));
        });
        Assert.Equal("idle_pistol", pistol.Idle);
        Assert.NotSame(plan.Gun, plan.For(pistol: true));
        Assert.Same(plan.Gun, plan.For(pistol: false));
    }

    [Fact]
    public void The_gait_clips_are_the_walk_and_run_levels_of_both_sets_and_the_sprint_once_each()
    {
        var hip = new HipFireAnimations([Set(HipFireWeaponClass.Rifle, Four), Set(HipFireWeaponClass.Pistol, Four)]);   // the same clip names in both sets
        var plan = OrkLocomotionPlan.Build(hip, Loco(new SprintClip(0f, "sprint", 2f)), Table(), Everything);
        var names = plan.GaitClips().ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(4 * 2 + 1, names.Count);
        Assert.Contains("sprint", names);
        Assert.Contains("walk_B", names);
        Assert.DoesNotContain("run_F_dn", names);   // the pitch variants borrow the level clip's gait
    }

    [Fact]
    public void A_clip_the_rig_lacks_is_left_out_and_a_set_with_fewer_than_three_directions_is_dropped()
    {
        var hip = new HipFireAnimations([Set(HipFireWeaponClass.Rifle, Four)]);
        // The right strafe's run clip is not in the rig: that direction goes, three are left.
        var plan = OrkLocomotionPlan.Build(hip, Loco(new SprintClip(0f, "sprint", 2f)), Table(), c => c != "run_R");
        Assert.Equal(3, plan.Gun!.Directions.Count);
        Assert.DoesNotContain(plan.Gun.Directions, d => d.AngleDegrees == 90f);
        Assert.Contains(plan.Notes, n => n.StartsWith("Rifle R:", StringComparison.Ordinal));
        // Two of four are missing: no set at all, with a note.
        plan = OrkLocomotionPlan.Build(hip, Loco(), Table(), c => c is not ("run_R" or "run_L"));
        Assert.Null(plan.Gun);
        Assert.Contains(plan.Notes, n => n.Contains("no move set", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Contains("sprint", StringComparison.Ordinal));   // and no sprint data: said
        Assert.Null(plan.Sprint);
    }

    [Fact]
    public void A_direction_without_a_walk_band_uses_its_run_clip_for_both()
    {
        // The Ork's pistol graph has bands 0, 2 and 6; a direction with a single moving band has no "walk".
        var single = new AimDirection(0f, [Band(0f, "spot"), Band(6f, "only_run")]);
        var hip = new HipFireAnimations([Set(HipFireWeaponClass.Rifle, Direction(-90f, "L"), single, Direction(90f, "R"))]);
        var plan = OrkLocomotionPlan.Build(hip, Loco(), Table(), Everything);
        var d = plan.Gun!.Directions.Single(x => x.AngleDegrees == 0f);
        Assert.Equal("only_run", d.Walk.Level);
        Assert.Equal("only_run", d.Run.Level);
    }

    [Fact]
    public void The_idle_falls_back_to_the_clip_tables_first_idle_and_without_one_the_set_is_dropped()
    {
        var hip = new HipFireAnimations([Set(HipFireWeaponClass.Rifle, Four)]);
        var noStance = Table() with { AimStances = new Dictionary<string, AimClipTriple>() };
        var plan = OrkLocomotionPlan.Build(hip, Loco(), noStance, Everything);
        Assert.Equal("idle_a", plan.Gun!.Idle);
        Assert.Null(plan.Gun.IdleDown);   // no additive offsets without the stance
        plan = OrkLocomotionPlan.Build(hip, Loco(), noStance, c => !c.StartsWith("idle", StringComparison.Ordinal));
        Assert.Null(plan.Gun);
    }

    [Fact]
    public void The_sprint_is_the_clip_nearest_forward_and_the_toss_needs_the_rig_to_have_it()
    {
        var hip = new HipFireAnimations([Set(HipFireWeaponClass.Rifle, Four)]);
        var plan = OrkLocomotionPlan.Build(hip, Loco(new SprintClip(-45f, "left", 2f), new SprintClip(2f, "ahead", 2f), new SprintClip(45f, "right", 2f)), Table(), Everything);
        Assert.Equal("ahead", plan.Sprint);
        plan = OrkLocomotionPlan.Build(hip, Loco(new SprintClip(0f, "sprint", 2f)), Table(), c => c is not ("toss" or "sprint"));
        Assert.Null(plan.Toss);
        Assert.Null(plan.Sprint);
        Assert.Contains(plan.Notes, n => n.Contains("toss", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the rig's feet

    [Fact]
    public void The_ork_rig_measures_its_gaits_at_its_own_foot_bones()
    {
        Assert.Same(FootBones.Marine, RigProfiles.Marine.Feet);
        Assert.Equal((ClipGait.LeftToe, ClipGait.RightToe, ClipGait.LeftAnkle, ClipGait.RightAnkle), (FootBones.Marine.LeftToe, FootBones.Marine.RightToe, FootBones.Marine.LeftAnkle, FootBones.Marine.RightAnkle));
        Assert.Same(FootBones.OrkBoy, RigProfiles.OrkBoy.Feet);
        Assert.Equal(("RIG_LLeg_Toe_1", "RIG_RLeg_Toe_1", "RIG_LLeg_Foot", "RIG_RLeg_Foot"), (FootBones.OrkBoy.LeftToe, FootBones.OrkBoy.RightToe, FootBones.OrkBoy.LeftAnkle, FootBones.OrkBoy.RightAnkle));
        Assert.NotEqual(FootBones.Marine, FootBones.OrkBoy);
    }

    [Fact]
    public void A_gait_library_keeps_the_rig_it_was_measured_on()
    {
        Assert.Equal(CharacterModel.MarineRigEntry, GaitLibrary.Build([], _ => true, _ => null).RigKey);
        var ork = GaitLibrary.Build(["a"], _ => true, _ => new ClipGait(1f, 0.1f, 0.5f, new System.Numerics.Vector2(0, 3f), 0.2f), RigProfiles.OrkBoy.Entry);
        Assert.Equal(RigProfiles.OrkBoy.Entry, ork.RigKey);
        Assert.NotNull(ork.Get("a"));
        Assert.NotEqual(CharacterModel.MarineRigEntry, ork.RigKey);
    }
}
