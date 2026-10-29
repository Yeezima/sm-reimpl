using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Ork O2: rig profiles. Ork-named synthetic graphs (shaped like the boy's on the install: a four-direction aim run, a speed-blend
/// sprint, a reload whose mask sits in its own graph, side-only evade sequencers, reaction and choppa sequencers) read through the Ork
/// profile; marine-named graphs read through the marine profile give exactly the readers' default output. Synthetic data only.
/// </summary>
public class RigProfileTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true, ReferenceHandler = ReferenceHandler.IgnoreCycles, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    // ---------------------------------------------------------------- the profiles themselves

    [Fact]
    public void The_marine_profile_is_the_readers_defaults()
    {
        var m = RigProfiles.Marine;
        Assert.Equal((CharacterModel.MarineRigArchive, CharacterModel.MarineRigEntry, WeaponModels.BodyVisual, HipFireAnimations.BaseGraph),
            (m.Archive, m.Entry, m.BodyVisual, m.BaseGraph));
        Assert.Same(LocomotionAnimations.DefaultRelaxed, m.Relaxed);
        Assert.Same(LocomotionAnimations.DefaultSprints, m.Sprints);
        Assert.Same(HipFireAnimations.DefaultMaps, m.HipFire);
        Assert.Same(ReloadAnimations.DefaultMaps, m.Reloads);
        Assert.Same(EvadeTuning.MarineRig, m.Evade);
        Assert.Equal((EvadeTuning.Graph, EvadeTuning.ClipSets, EvadeTuning.HopSets), (m.Evade.Graph, m.Evade.ClipSets, m.Evade.HopSets));
        Assert.Equal([EvadeTuning.Ability], m.Evade.Abilities);
        Assert.Null(m.Evade.DefaultSet);
        Assert.Empty(m.Evade.SideOnlySets);
        Assert.Same(ReactionTuning.MarineRig, m.Reactions);
        Assert.Equal((ReactionTuning.Graph, "CSM"), (m.Reactions.Graph, m.Reactions.PreferredSequencer));
        Assert.Same(ReactionTuning.Sets, m.Reactions.Sets);
        Assert.Same(MeleeTuning.CombatKnife, Assert.Single(m.Melee));
        Assert.Empty(m.DefaultWargear);
        Assert.Same(RigClipTable.Marine, m.Clips);
        // The new map fields default to the marine's behaviour.
        Assert.All(m.Relaxed, r => Assert.False(r.PreferLevelPitch));
        Assert.All(m.Sprints, s => Assert.True(!s.ForwardOnly && s.TransitionGraph is null));
        Assert.All(m.HipFire, h => Assert.True(h.RecoilState == "Recoil" && h.ReloadBlendGraph is null));
        Assert.All(m.Reloads, r => Assert.True(r.FinishedBranch == "BRANCH_Reload_Finished" && !r.MaskInReloadGraph && r.MagazineOutWords.SequenceEqual(["remove"])));
        Assert.Equal(["BRANCH_Melee_Draw_Run", "BRANCH_Melee_Run"], MeleeTuning.CombatKnife.MoveBranches);
    }

    [Fact]
    public void Profiles_are_found_by_name_and_the_ork_profile_names_the_boy()
    {
        Assert.Same(RigProfiles.Marine, RigProfiles.ByName("marine"));
        Assert.Same(RigProfiles.OrkBoy, RigProfiles.ByName("Ork"));
        Assert.Null(RigProfiles.ByName("tyranid"));
        Assert.Null(RigProfiles.ByName(null));
        var o = RigProfiles.OrkBoy;
        Assert.Equal(("data/chunks/ebp_sets__mp_coop_set.oppc", "havok_boy_coop__hkxx", "boy_melee", "Base_RANGED_Coop"), (o.Archive, o.Entry, o.BodyVisual, o.BaseGraph));
        Assert.Equal(Path.Combine("game", "data/chunks/ebp_sets__mp_coop_set.oppc"), o.ArchivePath("game"));
        Assert.Same(EvadeTuning.OrkRig, o.Evade);
        Assert.Same(ReactionTuning.OrkRig, o.Reactions);
        Assert.Equal([MeleeTuning.OrkChoppa, MeleeTuning.OrkChoppaSet2], o.Melee);
        Assert.Contains("ork_coop_boy_choppa", o.DefaultWargear);
        // The clip table: no heavy or diagonal stances on the boy, recorded as unfilled.
        Assert.DoesNotContain(o.Clips.AimStances.Keys, k => k.StartsWith("heavy:", StringComparison.Ordinal) || k.EndsWith(":FR", StringComparison.Ordinal));
        Assert.True(o.Clips.AimStances["rifle:wos"].AdditivePitch);
        Assert.Equal(["BOY_2HR_NAV_Walk_L_AimDown_02"], o.Clips.AimStances["rifle:walk:L"].Down);
        Assert.Null(o.Clips.GrenadeLauncherRecoilClip);
        Assert.NotEmpty(o.Clips.Unfilled);
        Assert.Equal(9 + 9 + 5, RigClipTable.Marine.AimStances.Count);   // the client's rifle 9, pistol 9, heavy 5 stances
    }

    [Fact]
    public void Hit_volumes_load_from_the_profiles_rig_and_the_marine_load_reads_the_marine_entry()
    {
        // A synthetic game folder with only the Ork rig archive: a two-body ragdoll (head and pelvis) under the boy's entry name.
        string dir = Path.Combine(Path.GetTempPath(), "sm-reimpl-tests", "o2-hit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var o = RigProfiles.OrkBoy;
            Directory.CreateDirectory(Path.GetDirectoryName(o.ArchivePath(dir))!);
            byte[] ragdoll = BuildRagdoll(
            [
                new RagdollBodyFixture("Ragdoll_RIG_Pelvis", new Vector3(0, 0, 0.9f), Quaternion.Identity, new CapsuleFixture(new(0, 0, -0.1f), new(0, 0, 0.1f), 0.2f)),
                new RagdollBodyFixture("Ragdoll_RIG_Head", new Vector3(0, 0, 1.5f), Quaternion.Identity, new SphereFixture(0.15f), Parent: 0),
            ]);
            File.WriteAllBytes(o.ArchivePath(dir), SyntheticObpkWriter.Build([new("hkxx", o.Entry, ragdoll)]));

            var notes = new List<string>();
            var volumes = HitVolumeTuning.Load(dir, o, notes)!;
            Assert.Equal([("Head", HitZone.Head), ("Pelvis", HitZone.Body)], volumes.Select(v => (v.Name, v.Zone)));   // head first
            // The same ragdoll through FromRagdoll gives the same volumes (the overload only picks the file).
            var direct = HitVolumeTuning.FromRagdoll(HavokRagdoll.Read(new HavokPackfile(ragdoll), []), [])!;
            Assert.Equal(Json(direct), Json(volumes));

            // The marine overload reads the marine archive, which this folder lacks.
            Assert.ThrowsAny<IOException>(() => HitVolumeTuning.Load(dir, notes));
            // A profile whose entry is missing from the archive: placeholders (null) with a note.
            Assert.Null(HitVolumeTuning.Load(dir, o with { Entry = "havok_missing__hkxx" }, notes));
            Assert.Contains(notes, n => n.Contains("'havok_missing__hkxx' not found"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- Ork-named graphs

    static readonly string[] RangedNavVariables = ["FLT_Nav_Motion_Angle_DAMPED", "FLT_Input_Nav_Speed_DAMPED", "FLT_Aim_Vertical_Deg_DAMPED"];
    static readonly string[] MeleeNavVariables = ["FLT_Nav_Motion_Angle_CLAMPED", "FLT_Input_Nav_Speed_CLAMPED"];

    static BlendNode Pitch(string gait, string dir) => new($"{gait} {dir}",
    [
        new BlendChildFixture(-80, new ClipNode($"{gait} {dir} down", $"BOY_2HR_NAV_{gait}_{dir}_AimDown_02")),
        new BlendChildFixture(0, new ClipNode($"{gait} {dir} level", $"BOY_2HR_NAV_{gait}_{dir}_AimForward_02")),
        new BlendChildFixture(80, new ClipNode($"{gait} {dir} up", $"BOY_2HR_NAV_{gait}_{dir}_AimUp_02")),
    ], ParameterVariable: 2);

    static BlendNode RangedSpeed(string dir) => new($"Strafe {dir}",
        [new BlendChildFixture(0, new ClipNode("wos", "BOY_2HR_NAV_Walkonspot")), new BlendChildFixture(2, Pitch("Walk", dir)), new BlendChildFixture(6, Pitch("Run", dir))],
        ParameterVariable: 1);

    /// <summary>BOY_NAV_RangedLOD: the "High LOD Strafe" state, four directions (back listed at -180 and +180), speeds 0 / 2 / 6, aim pitch ±80.</summary>
    static StructuredGraph OrkRangedNav()
    {
        var back = RangedSpeed("B");
        var strafe = new BlendNode("Strafe Blend",
            [new BlendChildFixture(-180, back), new BlendChildFixture(-90, RangedSpeed("L")), new BlendChildFixture(0, RangedSpeed("F")),
             new BlendChildFixture(90, RangedSpeed("R")), new BlendChildFixture(180, back)], ParameterVariable: 0, Flags: 0x31);
        var high = new ModifierGeneratorNode("Nav Facing Modifier Generator", new SequencerNode("Strafe Sequencer", strafe, []));
        var lod = new StateMachineNode("Behavior00", [new StateFixture(0, "High LOD Strafe", high)]);
        var root = new StateMachineNode("Ranged Nav Behavior", [new StateFixture(0, "Strafe", lod), new StateFixture(1, "Idle", new ClipNode("idle", "BOY_2HR_RNG_Idle_S_01"))]);
        return new StructuredGraph("BOY_NAV_RangedLOD", [], RangedNavVariables, root);
    }

    static BlendNode MeleeSpeed(string dir, string walk, string run) => new($"1HM {dir}",
        [new BlendChildFixture(0, new ClipNode("wos", "BOY_1HM_NAV_WalkonSpot")), new BlendChildFixture(2, new ClipNode($"w{dir}", walk)), new BlendChildFixture(6, new ClipNode($"r{dir}", run))],
        ParameterVariable: 1);

    /// <summary>BOY_NAV_Melee: the 1HM "Move" (four directions) and the "Sprint" speed blend (run 6, sprint 9.5) with its Move → Sprint transition.</summary>
    static StructuredGraph OrkMeleeNav(bool sprintTransition = true)
    {
        var back = MeleeSpeed("B", "BOY_1HM_NAV_Walk_Strafe_B_01", "BOY_1HM_Nav_Run_B_03");
        var move = new BlendNode("Movegroup",
            [new BlendChildFixture(-180, back), new BlendChildFixture(-90, MeleeSpeed("L", "BOY_1HM_NAV_Walk_Strafe_L_01", "BOY_1HM_NAV_Run_Strafe_L_03")),
             new BlendChildFixture(0, MeleeSpeed("F", "BOY_1HM_NAV_walk_F_01", "BOY_1HM_NAV_Run_F_03")),
             new BlendChildFixture(90, MeleeSpeed("R", "BOY_1HM_NAV_Walk_Strafe_R_01", "BOY_1HM_NAV_Run_Strafe_R_03")), new BlendChildFixture(180, back)],
            ParameterVariable: 0, Flags: 0x31);
        var sprint = new BlendNode("Blend00", [new BlendChildFixture(6, new ClipNode("run", "BOY_1HM_NAV_Run_F_01")), new BlendChildFixture(9.5f, new ClipNode("sprint", "BOY_1HM_NAV_Sprint_F_01"))],
            ParameterVariable: 1);
        var machine = new StateMachineNode("Behavior00",
        [
            new StateFixture(0, "Idle", new ClipNode("idle", "BOY_1HM_MEL_Idle_Combat_03")),
            new StateFixture(1, "Move", new ModifierGeneratorNode("m", new SequencerNode("SequencerGenerator08", move, [])),
                sprintTransition ? [new TransitionFixture(2, BlendDuration: 0.333f, EffectName: "Move To Sprint")] : null),
            new StateFixture(2, "Sprint", new ModifierGeneratorNode("s", new SequencerNode("Sprint Sequence", sprint, []))),
        ]);
        return new StructuredGraph("BOY_NAV_Melee", [], MeleeNavVariables, machine);
    }

    static float Length(string clip) => clip.Contains("Run") || clip.Contains("Sprint") ? 2.2f : 3.1f;

    [Fact]
    public void The_ork_profile_reads_a_four_direction_aim_run_a_1hm_run_and_a_single_clip_sprint()
    {
        var o = RigProfiles.OrkBoy;
        var loco = LocomotionAnimations.From(Read(OrkRangedNav(), OrkMeleeNav()), Length, o.Relaxed, o.Sprints, o.BaseGraph);
        Assert.Empty(loco.Notes);

        var rifle = loco.RelaxedFor(HipFireWeaponClass.Rifle)!;
        Assert.Equal(("BOY_NAV_RangedLOD", "High LOD Strafe"), (rifle.Graph, rifle.State));
        Assert.Equal([-180f, -90f, 0f, 90f], rifle.Directions.Select(d => d.AngleDegrees));   // four directions; back kept once
        Assert.Equal(["B", "L", "F", "R"], rifle.Directions.Select(d => d.Label));
        // Each speed band is a pitch blend: the level-aim clip is taken (no relaxed stance on the boy).
        Assert.Equal(new LocoBand(6f, "BOY_2HR_NAV_Run_L_AimForward_02", 2.2f, false), rifle.Directions[1].Run);
        Assert.Equal(new LocoBand(2f, "BOY_2HR_NAV_Walk_L_AimForward_02", 3.1f, false), rifle.Directions[1].Walk);

        var pistol = loco.RelaxedFor(HipFireWeaponClass.Pistol)!;
        Assert.Equal(["BOY_1HM_Nav_Run_B_03", "BOY_1HM_NAV_Run_Strafe_L_03", "BOY_1HM_NAV_Run_F_03", "BOY_1HM_NAV_Run_Strafe_R_03"], pistol.Directions.Select(d => d.Run!.Clip));
        Assert.Equal("BOY_1HM_NAV_walk_F_01", pistol.Directions[2].Walk!.Clip);

        // The sprint: the speed blend's fastest clip, forward only; the blend in from the navigation graph's own Move → Sprint transition.
        var sprint = Assert.Single(loco.Sprints);
        Assert.Equal(SprintFamily.Rifle, sprint.Family);
        Assert.Equal([new SprintClip(0f, "BOY_1HM_NAV_Sprint_F_01", 2.2f)], sprint.Clips);
        Assert.Equal(0.333f, sprint.EnterBlend, 4);

        // Negative control: without the level preference the first clip under each pitch blend (aim down) is taken.
        var plain = LocomotionAnimations.From(Read(OrkRangedNav()), Length, [o.Relaxed[0] with { PreferLevelPitch = false }], []);
        Assert.Equal("BOY_2HR_NAV_Run_F_AimDown_02", plain.Relaxed[0].Directions[2].Run!.Clip);
        // ... and the marine maps find nothing on the boy's graphs.
        var marineMaps = LocomotionAnimations.From(Read(OrkRangedNav(), OrkMeleeNav()), Length);
        Assert.Empty(marineMaps.Relaxed);
        Assert.Empty(marineMaps.Sprints);
    }

    static readonly string[] OrkBones = ["RIG_Root", "RIG_Pelvis", "RIG_Spine1", "RIG_LArm_Hand"];

    /// <summary>BOY_2HR_Reload (standing reload, and a moving reload blend: navigation on the legs, the same sequencer on the rest) and Base_RANGED_Coop's reload state.</summary>
    static IReadOnlyList<BehaviorGraphDetail> OrkReloadGraphs(string unload = "ACTN_SND_Wep_Unload")
    {
        string[] events = ["BRANCH_Reload_End", "Reload_Wargear_Ammunition", "BRANCH_Run"];
        var sequencer = new SequencerNode("SequencerGenerator00", new ClipNode("BOY_2HR_RNG_Reload_S_01a", "BOY_2HR_RNG_Reload_S_01", Mode: 0),
        [
            new Tag("Branch", 0.9588f, 1f, 0),
            new Tag("Branch", 0.8235f, 1f, 2),
            new Tag("Event", 0.4353f, 0.4353f, 1),
            new Tag("Action", 0.4353f, 0.4353f, "ACTN_SND_Wep_Clip_Load"),
            new Tag("Action", 0.5294f, 0.5294f, "ACTN_SND_Wep_Cock"),
            new Tag("Action", 0.1529f, 0.1529f, unload),
        ]);
        var moving = new BlendNode("Moving Reload",
        [
            new BlendChildFixture(1, new ReferenceNode("BehaviorFileReference00 -> Behaviors\\BOY_NAV_RangedLOD.hkxg", "Behaviors\\BOY_NAV_RangedLOD.hkxg"), [1f, 1f, 0f, 0f]),
            new BlendChildFixture(1, sequencer, [0f, 0f]),
        ], Flags: 0);
        var reload = new StructuredGraph("BOY_2HR_Reload", events, ["FLT_Input_Nav_Speed"],
            new StateMachineNode("Behavior00", [new StateFixture(0, "Standing Reload", sequencer), new StateFixture(1, "Moving Reload", moving)]));

        string[] baseEvents = ["BTN_Reload", "BRANCH_Reload_End"];
        var machine = new StateMachineNode("RANGED BEHAVIOR",
        [
            new StateFixture(0, "Ranged Navigation", new ReferenceNode("Locomotion -> Behaviors\\BOY_NAV_RangedLOD.hkxg", "Behaviors\\BOY_NAV_RangedLOD.hkxg"),
                [new TransitionFixture(2, EventId: 0, Priority: 5, BlendDuration: 0.333f, EffectName: "Standard Blend")]),
            new StateFixture(2, "Ranged Reload", new ModifierGeneratorNode("Reload Generator",
                new ReferenceNode("Reload -> Behaviors\\BOY_2HR_Reload.hkxg", "Behaviors\\BOY_2HR_Reload.hkxg")),
                [new TransitionFixture(0, BranchEventId: 1, Priority: 1, BlendDuration: 0.5f, EffectName: "Long Blend")]),
        ]);
        var baseGraph = new StructuredGraph("Base_RANGED_Coop", baseEvents, [], new StateMachineNode("Base_Ranged", [new StateFixture(3, "RANGED BEHAVIOR", machine)]));
        return Read(reload, baseGraph);
    }

    [Fact]
    public void The_ork_reload_clip_its_tags_end_branch_and_mask_from_its_own_graph_are_read()
    {
        var data = ReloadAnimations.From(OrkReloadGraphs(), OrkBones, clip => clip == "BOY_2HR_RNG_Reload_S_01" ? 4.1f : throw new KeyNotFoundException(clip), RigProfiles.OrkBoy.Reloads);
        Assert.Equal([ReloadFamily.Bolter, ReloadFamily.BoltPistol], data.Clips.Select(c => c.Family));   // the gun and the pistol share the clip
        var r = data.For(ReloadFamily.Bolter)!;
        Assert.Empty(r.Notes);
        Assert.Equal(("BOY_2HR_Reload", "BOY_2HR_RNG_Reload_S_01", 4.1f, (string?)null), (r.Graph, r.Clip, r.ClipLength, r.EnforcedDuration));   // plays at its own length
        Assert.Equal((0.1529f, 0.4353f, 0.4353f), (r.MagazineOut!.Value, r.MagazineIn!.Value, r.AmmunitionAt!.Value));   // the unload sound marks the old magazine
        Assert.Equal((0.333f, 0.5f), (r.BlendIn!.Value, r.BlendOut!.Value));   // into "Ranged Reload", back on BRANCH_Reload_End
        Assert.Equal(new Dictionary<string, float> { ["RIG_Root"] = 0f, ["RIG_Pelvis"] = 0f, ["RIG_Spine1"] = 1f, ["RIG_LArm_Hand"] = 1f }, r.BoneMask);
        Assert.Equal("Branch:BRANCH_Reload_End", $"{r.Tags[^1].Kind}:{r.Tags[^1].Name}");

        // The marine's rules on the same graphs: no BRANCH_Reload_Finished way back, no "remove" tag, no mask under the owner's state.
        var marineRules = ReloadAnimations.From(OrkReloadGraphs(), OrkBones, _ => 4.1f,
            [new ReloadAnimations.Map(ReloadFamily.Bolter, "BOY_2HR_Reload", "Base_RANGED_Coop", "Ranged Reload", "Ranged Navigation")]).Clips[0];
        Assert.Null(marineRules.MagazineOut);
        Assert.Null(marineRules.BlendOut);
        Assert.Null(marineRules.BoneMask);
        Assert.Contains(marineRules.Notes, n => n.Contains("no BRANCH_Reload_Finished transition"));
    }

    /// <summary>BOY_2HR_Shoot: the additive standing recoil in "Standing Shoot"; Base_RANGED_Coop's navigation ⇄ shoot transitions.</summary>
    static IReadOnlyList<BehaviorGraphDetail> OrkShootGraphs()
    {
        var shoot = new SequencerNode("Shoot Sequence", new ClipNode("BOY_2HR_RNG_Fire_S_01a", "BOY_2HR_RNG_Fire_S_01", Mode: 0),
            [new Tag("Branch", 0.2167f, 1f, 1), new Tag("Branch", 0.05f, 1f, 0)]);
        var fire = new StructuredGraph("BOY_2HR_Shoot", ["BRANCH_Refire", "BRANCH_Shot_Finished"], [],
            new StateMachineNode("2HR Shoot", [new StateFixture(0, "Standing Shoot", shoot)]));
        var nav = new ReferenceNode("Locomotion -> Behaviors\\BOY_NAV_RangedLOD.hkxg", "Behaviors\\BOY_NAV_RangedLOD.hkxg");
        var machine = new StateMachineNode("RANGED BEHAVIOR",
        [
            new StateFixture(0, "Ranged Navigation", nav, [new TransitionFixture(3, EventId: 0, BlendDuration: 0.167f, EffectName: "Fast Blend")]),
            new StateFixture(3, "Ranged Shoot", new BlendNode("Shoot Blend", [new BlendChildFixture(1, nav),
                new BlendChildFixture(1, new ReferenceNode("BehaviorRef_Shoot -> Behaviors\\BOY_2HR_Shoot.hkxg", "Behaviors\\BOY_2HR_Shoot.hkxg"))], Flags: 0),
                [new TransitionFixture(0, BranchEventId: 1, BlendDuration: 0.333f, EffectName: "Standard Blend")]),
        ]);
        var baseGraph = new StructuredGraph("Base_RANGED_Coop", ["BTN_Ranged", "BRANCH_Shot_Finished"], [], new StateMachineNode("Base_Ranged", [new StateFixture(3, "RANGED BEHAVIOR", machine)]));
        return Read(OrkRangedNav(), OrkMeleeNav(), fire, baseGraph);
    }

    [Fact]
    public void The_ork_hip_fire_has_the_aim_run_the_standing_recoil_and_no_relaxed_posture()
    {
        var hip = HipFireAnimations.From(OrkShootGraphs(), OrkBones, Length, clip => clip == "BOY_2HR_RNG_Fire_S_01" ? AnimationBlendHint.Additive : AnimationBlendHint.Normal,
            RigProfiles.OrkBoy.HipFire, RigProfiles.OrkBoy.BaseGraph);
        var rifle = hip.For(HipFireWeaponClass.Rifle)!;
        Assert.Equal([-180f, -90f, 0f, 90f], rifle.Directions.Select(d => d.AngleDegrees));
        Assert.Equal(["BOY_2HR_NAV_Run_F_AimDown_02", "BOY_2HR_NAV_Run_F_AimForward_02", "BOY_2HR_NAV_Run_F_AimUp_02"], rifle.Directions[2].Run!.Clips.Select(c => c.Clip));
        Assert.Equal(new RecoilClip("Shoot Sequence", "BOY_2HR_RNG_Fire_S_01", 3.1f, 1f, true, 0.2167f * 3.1f, 0.05f * 3.1f), rifle.Recoil);
        Assert.Equal((0.167f, 0.333f), (rifle.NavToFireBlend!.Value, rifle.FireToNavBlend!.Value));
        Assert.Null(rifle.RaiseDelay);       // no "Set Weapon Ready" state on the boy
        Assert.Null(rifle.ReadyCondition);   // no relaxed posture: always ready
        Assert.Null(rifle.SprintToFire);
        Assert.Null(hip.For(HipFireWeaponClass.Heavy));
        var pistol = hip.For(HipFireWeaponClass.Pistol)!;   // the 1HM run, one clip per band (no aim pitch)
        Assert.Equal([new AimClip(0f, "BOY_1HM_NAV_Run_F_03", 2.2f)], pistol.Directions[2].Run!.Clips);
        Assert.Equal(rifle.Recoil, pistol.Recoil);
    }

    [Fact]
    public void A_clip_unload_action_is_the_magazine_leaving_not_the_new_one_going_in()
    {
        // "ACTN_SND_Wep_Clip_Unload" has "load" and "clip" but is no load: the new magazine stays at the Clip_Load tag.
        var r = ReloadAnimations.From(OrkReloadGraphs("ACTN_SND_Wep_Clip_Unload"), OrkBones, _ => 4.1f, RigProfiles.OrkBoy.Reloads).Clips[0];
        Assert.Equal((0.1529f, 0.4353f), (r.MagazineOut!.Value, r.MagazineIn!.Value));
        // The marine rules on the same clip: no "remove" word, so no magazine out; the load is still the Clip_Load tag.
        var marineRules = ReloadAnimations.From(OrkReloadGraphs("ACTN_SND_Wep_Clip_Unload"), OrkBones, _ => 4.1f,
            [new ReloadAnimations.Map(ReloadFamily.Bolter, "BOY_2HR_Reload", "Base_RANGED_Coop", "Ranged Reload", "Ranged Navigation")]).Clips[0];
        Assert.Null(marineRules.MagazineOut);
        Assert.Equal(0.4353f, marineRules.MagazineIn!.Value);
    }

    [Fact]
    public void A_sprint_without_its_named_transition_falls_back_to_the_default_blend_with_a_note()
    {
        var o = RigProfiles.OrkBoy;
        var loco = LocomotionAnimations.From(Read(OrkMeleeNav(sprintTransition: false)), Length, [], o.Sprints, o.BaseGraph);
        Assert.Equal(LocomotionAnimations.DefaultEnterBlend, Assert.Single(loco.Sprints).EnterBlend);
        Assert.Contains(loco.Notes, n => n.Contains("no blending transition 'Move' → 'Sprint' in BOY_NAV_Melee/Behavior00"));
    }

    [Theory]
    [InlineData("sm_mp_ranged_bolter")]
    [InlineData("sm_mp_pistol_bolt_pistol")]
    [InlineData("sm_mp_pistol_bolter")]
    [InlineData("sm_mp_exotic_heavy_bolter")]
    [InlineData("sm_mp_exotic_las_cannon")]
    [InlineData("csm_mp_ranged_sniper_rifle")]
    [InlineData("sm_mp_ranged_melta")]
    [InlineData("slugga")]
    public void The_marine_profile_resolves_families_by_the_readers_name_rules(string wargear)
    {
        var m = RigProfiles.Marine;
        var expected = new WeaponFamilies(HipFireAnimations.FamilyOf(wargear), ReloadAnimations.FamilyOf(wargear), LocomotionAnimations.SprintFamilyOf(wargear));
        Assert.Equal(expected, m.FamiliesOf(wargear));
        Assert.Equal(expected, m.FamiliesOf(wargear, WeaponSlot.Pistol));   // the marine has no slot table: the slot changes nothing
        Assert.Null(m.FamilyForSlot(WeaponSlot.Ranged));
    }

    [Fact]
    public void The_ork_profile_resolves_its_guns_by_name_and_other_weapons_by_slot()
    {
        var o = RigProfiles.OrkBoy;
        // The name rules alone would get these wrong: the slugga a rifle, neither gun a reload.
        Assert.Equal(HipFireWeaponClass.Rifle, HipFireAnimations.FamilyOf("slugga"));
        Assert.Null(ReloadAnimations.FamilyOf("slugga"));
        Assert.Null(ReloadAnimations.FamilyOf("ork_coop_boy_2hand_shoota_rapid"));

        Assert.Equal(new WeaponFamilies(HipFireWeaponClass.Pistol, ReloadFamily.BoltPistol, SprintFamily.Rifle), o.FamiliesOf("Slugga"));
        Assert.Equal(new WeaponFamilies(HipFireWeaponClass.Rifle, ReloadFamily.Bolter, SprintFamily.Rifle), o.FamiliesOf("ork_coop_boy_2hand_shoota_rapid"));
        Assert.Equal(HipFireWeaponClass.Pistol, o.HipFireFamilyOf("slugga", WeaponSlot.Ranged));   // the name entry wins over the slot
        // An Ork weapon not in the table: by its loadout slot; without a slot, the name rules.
        Assert.Equal(ReloadFamily.BoltPistol, o.ReloadFamilyOf("stormboy_slugga", WeaponSlot.Pistol));
        Assert.Equal(HipFireWeaponClass.Rifle, o.HipFireFamilyOf("big_shoota_playable", WeaponSlot.Ranged));
        Assert.Null(o.ReloadFamilyOf("big_shoota_playable"));
        Assert.Equal(SprintFamily.Rifle, o.SprintFamilyOf("slugga"));
        // Every family the table names has its reload and hip-fire map on the rig.
        foreach (var f in o.WargearFamilies.Values.Concat(o.SlotFamilies.Values))
        {
            Assert.Contains(o.Reloads, r => r.Family == f.Reload);
            Assert.Contains(o.HipFire, h => h.Class == f.HipFire);
            Assert.Contains(o.Sprints, s => s.Family == f.Sprint);
        }
    }

    // ---------------------------------------------------------------- evade, reactions, melee (simple graphs)

    static BehaviorTag B(float start, string name) => new("Branch", start, 1f, name, null);

    static readonly BehaviorGraph OrkRangedBase = new("Base_RANGED_Coop", [],
    [
        new("Evade Left Sequence", ["BOY_2HR_NAV_Evade4m_L_01", "BOY_2HR_NAV_Evade6m_L_01"], [B(0.9286f, "BRANCH_Evade_End")], [1f, 1f]),
        new("Sidestep Left Sequence", ["BOY_2HR_NAV_SideStep_L_2M", "BOY_2HR_NAV_SideStep_L_4M"], [B(0.925f, "BRANCH_Evade_End")], [1f, 1f]),
        new("Evade Right Sequence", ["BOY_2HR_NAV_Evade4m_R_01", "BOY_2HR_NAV_Evade6m_R_01"], [B(0.9143f, "BRANCH_Evade_End")], [1.5f, 1f]),
    ]);

    /// <summary>The boy's evade wargear: an ability named "evade", no special_nav_ability, an empty animation_set.</summary>
    static BafDocument OrkEvadeWargear() => BafDocument.Read(SyntheticBafWriter.Build(
        ("ability_data", new (string, object)[]
        {
            ("ability_definitions", new (string, object)[] { ("evade", new (string, object)[] { ("$ref", @"wargear\ability_types\evade") }) }),
        }),
        ("common_data", new (string, object)[]
        {
            ("fire_mode_primary", new (string, object)[] { ("entry", new (string, object)[] { ("animation_set", ""), ("upgrade_granted", "") }) }),
        })));

    static HavokExtractedMotion? SideMotion(string clip)
    {
        float x = clip.Contains("_L_") ? -4f : 3.7f;
        return new HavokExtractedMotion(Vector3.UnitZ, Vector3.UnitY, 2f, [Vector4.Zero, new(x / 2, 0, 0, 0), new(x, 0, 0, 0)]);
    }

    [Fact]
    public void The_ork_evade_is_a_side_only_hop_from_the_4m_clips()
    {
        var notes = new List<string>();
        IReadOnlyList<float>? Feet(string clip, int n) => Enumerable.Range(0, n).Select(k => k == 1 ? 0.13f : 0f).ToArray();
        var doc = OrkEvadeWargear();
        Assert.False(EvadeTuning.IsEvade(doc));                      // not the marine's ability_evade
        Assert.True(EvadeTuning.IsEvade(doc, EvadeTuning.OrkRig));
        var e = EvadeTuning.Read(EvadeTuning.OrkRig, "ork_coop_boy_evade_ranged", doc, [OrkRangedBase], _ => 2f, SideMotion, notes, Feet)!;
        Assert.Equal((EvadeTuning.OrkSideHop, false), (e.AnimationSet, e.Diagonals));
        Assert.Equal(("BOY_2HR_NAV_Evade4m_L_01", "BOY_2HR_NAV_Evade4m_R_01"), (e.Left.Animation, e.Right.Animation));
        Assert.Equal(new Vector2(-4f, 0f), e.Left.Distance);
        Assert.Equal(new Vector2(3.7f, 0f), e.Right.Distance);
        Assert.Equal(2f / 1.5f, e.Right.Duration, 5);                // the right clip plays at 1.5
        Assert.True(e.Left.Hops && e.Right.Hops);
        Assert.Equal(0.13f, e.Left.PeakHeight, 5);
        // Stand-ins until the simulation has a side-only evade: forward is the right hop, back the left.
        Assert.Same(e.Right, e.Forward);
        Assert.Same(e.Left, e.Back);
        Assert.Equal((0.8f, 25f, 40f), (e.MinInterval, e.WallHitAngle, e.MaxTurnRate));   // no special_nav_ability: the fallbacks
        Assert.Contains(notes, n => n.Contains("side-only evade"));
        Assert.Contains(notes, n => n.Contains("no animation_set; the rig's 'ork_side_hop' clips are used"));
        Assert.DoesNotContain(notes, n => n.Contains("evade not simulated"));
        // The marine's rules refuse it: no ability_evade.
        Assert.Null(EvadeTuning.Read("ork_coop_boy_evade_ranged", doc, [OrkRangedBase], _ => 2f, SideMotion, notes, Feet));
        // A missing right clip: no evade.
        Assert.Null(EvadeTuning.Read(EvadeTuning.OrkRig, "x", doc, [OrkRangedBase with { Sequencers = OrkRangedBase.Sequencers.Where(s => !s.Name.Contains("Right")).ToArray() }],
            _ => 2f, SideMotion, notes, Feet));
    }

    [Fact]
    public void The_ork_reactions_come_from_its_reaction_graph_two_way()
    {
        BehaviorSequencer Seq(string name, string clip, float end) => new(name, [clip], [B(end, "BRANCH_Reaction_End"), B(0.5f, "BRANCH_HitReact")], [1f]);
        var graph = new BehaviorGraph("BOY_REACTIONS_Coop", [],
        [
            Seq("SequencerGenerator LFHL", "BOY_1HM_REC_Lt_Front_Hi_L_01", 0.9333f), Seq("SequencerGenerator LBHR", "BOY_1HM_REC_Lt_Back_Hi_R_01", 0.8857f),
            Seq("SequencerGenerator HFHL", "BOY_1HM_REC_Hvy_Front_Hi_L_01", 0.8857f), Seq("SequencerGenerator HBHL", "BOY_1HM_REC_Hvy_Back_Hi_L_01", 0.8857f),
            Seq("SequencerGenerator HFLR", "BOY_1HM_REC_Hvy_Front_Low_R_01", 0.8857f), Seq("SequencerGenerator HBLR", "BOY_1HM_REC_Hvy_Back_Low_R_01", 0.8857f),
        ]);
        var notes = new List<string>();
        var specs = ReactionTuning.Specs(ReactionTuning.OrkRig, [graph], c => c.Contains("Lt_") ? 1f : 2f, notes)!;
        Assert.Empty(notes);
        Assert.Equal([ReactionKind.Interrupt, ReactionKind.Stun, ReactionKind.Stagger], specs.Select(s => s.Kind));
        Assert.All(specs, s => Assert.False(s.FourWay));
        Assert.Equal(("BOY_1HM_REC_Lt_Front_Hi_L_01", 0.9333f), (specs[0].Front.Animation, specs[0].Front.EndAt));
        Assert.Equal("BOY_1HM_REC_Hvy_Back_Hi_L_01", specs[1].Back.Animation);
        Assert.Equal(2f * 0.8857f, specs[2].Front.EndAt, 4);
        // The marine's graph name is not there: placeholders (null) with a note.
        Assert.Null(ReactionTuning.Specs([graph], _ => 1f, notes));
        Assert.Contains(notes, n => n.Contains("graph MP_SM_Reactions not found"));
    }

    [Fact]
    public void The_choppa_combo_reads_with_its_run_branch()
    {
        BehaviorTag Ev(string name, float at) => new("Event", at, at, name, null);
        BehaviorSequencer Attack(string name, string clip, float hit, float? combo, float run, string hitEvent = "ACTN_Hit_Primary_Target") => new(name,
            [$"{clip}_1m", $"{clip}_3.5m", $"{clip}_7m"],
            [Ev(hitEvent, hit), B(run, "BRANCH_Run"), .. combo is float c ? [B(c, "BRANCH_Melee_Combo")] : Array.Empty<BehaviorTag>(), new("AttackDistance", 0f, 0.5f, null, 7f)],
            [1f, 1f, 1f]);
        var graph = new BehaviorGraph("Base_MELEE_Coop", [],
        [
            Attack("Combo Start Sequencer", "BOY_1HM_MEL_Cmbo_01_Atk01", 0.3922f, 0.549f, 0.9706f),
            Attack("Combo 2 Sequencer", "BOY_1HM_MEL_Cmbo_01_Atk02", 0.3947f, 0.7895f, 0.9474f),
            Attack("Combo Heavy Attack Sequencer", "BOY_1HM_MEL_Cmbo_01_Hvy01", 0.3818f, null, 0.5636f, "ACTN_Hit_Targets_Multiplayer"),
        ]);
        static (string, object) Ability(string name, float damage, float range) => (name, new (string, object)[]
        {
            ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
                { ("do_damage", new (string, object)[] { ("damage_general", damage) }) }) }) }),
            ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", range) }) }),
        });
        var wargear = BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[]
            { ("ability_definitions", new (string, object)[] { Ability("ability_melee_light", 10f, 3f), Ability("ability_melee_heavy", 24f, 5f) }) })));
        var t = MeleeTuning.From([graph], _ => 2f, wargear, MeleeTuning.OrkChoppa);
        Assert.Equal(["BOY_1HM_MEL_Cmbo_01_Atk01_3.5m", "BOY_1HM_MEL_Cmbo_01_Atk02_3.5m"], t.Animations);
        Assert.Equal(["BOY_1HM_MEL_Cmbo_01_Hvy01_3.5m"], t.HeavyAnimations);
        Assert.Equal((0.3922f * 2f, 0.549f * 2f, 0.9706f * 2f, 10f, 7f), (t.Steps[0].HitTime, t.Steps[0].ComboOpen, t.Steps[0].MoveAt, t.Steps[0].Damage, t.Steps[0].Lunge));
        Assert.Equal((24f, 0.5636f * 2f), (t.Heavy[0].Damage, t.Heavy[0].MoveAt));
        Assert.Equal(5f, t.Range);
        Assert.DoesNotContain(t.Notes, n => n.Contains("no run branch"));
        // The knife's run branches are not on the boy's sequencers: movement only at the end.
        var knifeRules = MeleeTuning.From([graph], _ => 2f, wargear, MeleeTuning.OrkChoppa with { MoveBranches = MeleeTuning.CombatKnife.MoveBranches });
        Assert.Equal(2f, knifeRules.Steps[0].MoveAt);
        Assert.Contains(knifeRules.Notes, n => n.Contains("no run branch"));
    }

    // ---------------------------------------------------------------- the marine profile gives today's output

    static readonly string[] MarineNavVariables = ["FLT_Nav_Motion_Angle_DAMPED", "FLT_Input_Nav_Speed_DAMPED", "FLT_Aim_Vertical_Deg_DAMPED", "BOOL_IsReady"];

    /// <summary>Marine-named graphs: SM_Bolter_NAV (relaxed and ready runs), SM_UB_RangedRecoil, MP_SM_Bolter_NuSprint, SM_Bolter_Reload and SM_Base_MP.</summary>
    static IReadOnlyList<BehaviorGraphDetail> MarineGraphs()
    {
        BlendNode Relaxed(string dir, float run) => new($"Relaxed {dir}",
            [new BlendChildFixture(0, new ClipNode("w0", "SM_WOS")), new BlendChildFixture(2, new ClipNode($"w{dir}", $"SM_NAV_Walk_{dir}_01")), new BlendChildFixture(run, new ClipNode($"r{dir}", $"SM_NAV_Run_{dir}_01"))],
            ParameterVariable: 1);
        BlendNode ReadyPitch(string dir) => new($"Ready {dir}",
            [new BlendChildFixture(-80, new ClipNode($"d{dir}", $"SM_NAV_Run_{dir}_AimDown_01")), new BlendChildFixture(0, new ClipNode($"l{dir}", $"SM_NAV_Run_{dir}_Aim_F_01")),
             new BlendChildFixture(80, new ClipNode($"u{dir}", $"SM_NAV_Run_{dir}_AimUp_01"))], ParameterVariable: 2);
        BlendNode Ready(string dir, float run) => new($"Ready Speed {dir}", [new BlendChildFixture(2, ReadyPitch(dir)), new BlendChildFixture(run, ReadyPitch(dir))], ParameterVariable: 1);
        BlendNode Directions(Func<string, float, BlendNode> band) => new("Directions",
            [new BlendChildFixture(-180, band("B", 4.5f)), new BlendChildFixture(-90, band("L", 4.5f)), new BlendChildFixture(0, band("F", 6.5f)), new BlendChildFixture(90, band("R", 4.5f))],
            ParameterVariable: 0, Flags: 0x31);
        var stand = new StateMachineNode("Stand",
        [
            new StateFixture(0, "Run Relaxed", new ModifierGeneratorNode("rel", new SequencerNode("Seq", Directions(Relaxed), [])),
                [new TransitionFixture(1, BlendDuration: 0.267f, Condition: new BoolFixture([3], []))]),
            new StateFixture(1, "Run Ready", new ModifierGeneratorNode("rdy", new SequencerNode("Seq2", Directions(Ready), [])),
                [new TransitionFixture(0, BlendDuration: 0.5f, Condition: new BoolFixture([], [3]))]),
        ]);
        var nav = new StructuredGraph("SM_Bolter_NAV", [], MarineNavVariables, stand);

        var recoil = new SequencerNode("Shoot Sequence", new ClipNode("c", "SM_RNG_FireIdle_S_01", Mode: 0), [new Tag("Branch", 0.1f, 1f, 0), new Tag("Branch", 0.8f, 1f, 1)]);
        var raise = new SequencerNode("Raise", new ClipNode("c2", "SM_RNG_Raise", Mode: 0), [new Tag("Branch", 0.4f, 1f, 2)]);
        var fire = new StructuredGraph("SM_UB_RangedRecoil", ["BRANCH_Refire", "BRANCH_Shot_Finished", "BRANCH_to_Fire"], [],
            new StateMachineNode("Shoot", [new StateFixture(0, "Recoil", recoil), new StateFixture(2, "Set Weapon Ready00", raise)]));

        var sprintBlend = new BlendNode("Sprint Blend",
            [new BlendChildFixture(45, new ClipNode("sr", "SM_MP_1HR_NAV_Sprint_R")), new BlendChildFixture(-45, new ClipNode("sl", "SM_MP_1HR_NAV_Sprint_L")),
             new BlendChildFixture(0, new ClipNode("sf", "SM_MP_1HR_NAV_Sprint_F"))], ParameterVariable: 0);
        var sprint = new StructuredGraph("MP_SM_Bolter_NuSprint", [], ["FLT_Nav_Motion_Angle_SPRINT_DAMP"],
            new StateMachineNode("Sprint", [new StateFixture(2, "Sprint Loop", new ModifierGeneratorNode("m", sprintBlend))]));

        var reloadSeq = new SequencerNode("Seq", new ClipNode("gen", "SM_RNG_Bolter_Reload_UB_S_01", Mode: 0, EnforcedDurationVariable: 0),
            [new Tag("Action", 0.05f, 0.05f, "SFX_Bolter_Remove_Clip"), new Tag("Action", 0.3f, 0.3f, "SFX_Bolter_Load_Clip"), new Tag("Event", 0.8f, 0.8f, 0), new Tag("Branch", 1f, 1f, 1)]);
        var reload = new StructuredGraph("SM_Bolter_Reload", ["Reload_Wargear_Ammunition", "BRANCH_Reload_Finished"], ["FLT_WeaponReloadTime"], reloadSeq);

        var navRef = new ReferenceNode("Nav Ref", "Behaviors\\SM_Bolter_NAV.hkxg");
        var reloadBlend = new BlendNode("Bolter_ReloadBlend",
            [new BlendChildFixture(1, navRef, [1f, 1f, 0f, 0f]), new BlendChildFixture(1, new ReferenceNode("REF_BolterReload", "Behaviors\\SM_Bolter_Reload.hkxg"), [0f, 0f])], Flags: 0);
        var machine = new StateMachineNode("BolterBehavior",
        [
            new StateFixture(1, "Bolter Navigation", navRef, [new TransitionFixture(2, EventId: 0, BlendDuration: 0.1f), new TransitionFixture(3, EventId: 1, BlendDuration: 0.2f),
                new TransitionFixture(4, BlendDuration: 0.1f)]),
            new StateFixture(2, "Bolter Fire", new BlendNode("Fire Blend", [new BlendChildFixture(1, navRef), new BlendChildFixture(1, new ReferenceNode("Fire Ref", "Behaviors\\SM_UB_RangedRecoil.hkxg"))], Flags: 0),
                [new TransitionFixture(1, BranchEventId: 2, BlendDuration: 0.15f)]),
            new StateFixture(3, "Bolter Reload", reloadBlend, [new TransitionFixture(1, BranchEventId: 3, BlendDuration: 0.133f)]),
            new StateFixture(4, "Bolter Sprint", new ClipNode("sp", "SM_Sprint"), [new TransitionFixture(2, EventId: 0, BranchEventId: 4, BlendDuration: 0.1f)]),
        ]);
        var baseGraph = new StructuredGraph("SM_Base_MP", ["BTN_Ranged", "BTN_Reload", "BRANCH_Shot_Finished", "BRANCH_Reload_Finished", "BRANCH_Sprint_Evade"], [],
            new StateMachineNode("Base", [new StateFixture(0, "Ranged - Bolter", machine)]));
        return Read(nav, fire, sprint, reload, baseGraph);
    }

    static readonly string[] MarineBones = ["RIG_Root", "RIG_Pelvis", "RIG_SpineA", "RIG_Head"];
    static float MarineLength(string clip) => clip.Contains("Run") ? 0.867f : clip.Contains("Reload") ? 2f : 1.333f;

    [Fact]
    public void The_marine_profile_gives_exactly_the_default_output()
    {
        var graphs = MarineGraphs();
        var m = RigProfiles.Marine;

        var loco = LocomotionAnimations.From(graphs, MarineLength);
        Assert.Equal(Json(loco), Json(LocomotionAnimations.From(graphs, MarineLength, m.Relaxed, m.Sprints, m.BaseGraph)));
        Assert.Equal(4, loco.RelaxedFor(HipFireWeaponClass.Rifle)!.Directions.Count);            // the comparison covers real data
        Assert.Equal(3, loco.SprintFor(SprintFamily.Rifle)!.Clips.Count);
        Assert.Equal(0.1f, loco.SprintFor(SprintFamily.Rifle)!.EnterBlend);

        AnimationBlendHint? Hint(string clip) => clip == "SM_RNG_FireIdle_S_01" ? AnimationBlendHint.Additive : AnimationBlendHint.Normal;
        var hip = HipFireAnimations.From(graphs, MarineBones, MarineLength, Hint);
        Assert.Equal(Json(hip), Json(HipFireAnimations.From(graphs, MarineBones, MarineLength, Hint, m.HipFire, m.BaseGraph)));
        var rifle = hip.For(HipFireWeaponClass.Rifle)!;
        Assert.NotNull(rifle.Recoil);
        Assert.NotNull(rifle.UpperBodyMask);
        Assert.Equal("BOOL_IsReady", rifle.ReadyCondition);

        var reloads = ReloadAnimations.From(graphs, MarineBones, MarineLength);
        Assert.Equal(Json(reloads), Json(ReloadAnimations.From(graphs, MarineBones, MarineLength, m.Reloads)));
        var bolter = reloads.For(ReloadFamily.Bolter)!;
        Assert.Equal((0.05f, 0.3f), (bolter.MagazineOut!.Value, bolter.MagazineIn!.Value));
        Assert.NotNull(bolter.BoneMask);
        Assert.Equal(0.133f, bolter.BlendOut!.Value);
    }

    [Fact]
    public void The_marine_evade_and_reactions_through_their_rig_maps_equal_the_old_calls()
    {
        BehaviorSequencer Roll(char letter) => new($"{letter} seq", [$"SM_EVD_R2R_Roll_{letter}_01_8m"],
            [new("EntityProperty", 0f, 0.5f, "TargetableMode", null, 1), B(0.57f, "BRANCH_Run"), B(0.57f, "BRANCH_ToRanged"), B(0.68f, "BRANCH_Melee_Combo"),
             B(1f, "BRANCH_Evade"), B(0.66f, "BRANCH_To_Weapon_Switch")], [1f]);
        var baseGraph = new BehaviorGraph(EvadeTuning.Graph, [], [Roll('F'), Roll('B'), Roll('L'), Roll('R')]);
        HavokExtractedMotion? Motion(string clip) => new(Vector3.UnitZ, Vector3.UnitY, 1f, [Vector4.Zero, new(0, 3, 0, 0), new(0, 6, 0, 0)]);
        var doc = BafDocument.Read(SyntheticBafWriter.EvadeAbility("evade", "evade_default_mp", 0.8f, 25, 40, "ability_displace_kick"));
        List<string> before = [], after = [];
        var old = EvadeTuning.Read("sm_mp_core_abil_evade", doc, [baseGraph], _ => 1f, Motion, before);
        var viaMap = EvadeTuning.Read(EvadeTuning.MarineRig, "sm_mp_core_abil_evade", doc, [baseGraph], _ => 1f, Motion, after);
        Assert.NotNull(old);
        Assert.Equal(Json(old), Json(viaMap));
        Assert.Equal(before, after);
        Assert.True(old!.Diagonals);
        Assert.Contains(before, n => n.Contains("ability_displace_kick not simulated"));
        Assert.Equal(EvadeTuning.IsEvade(doc), EvadeTuning.IsEvade(doc, EvadeTuning.MarineRig));

        var reactions = new BehaviorGraph(ReactionTuning.Graph, [], ReactionTuning.Sets.SelectMany(s => s.Clips).Distinct()
            .Select(c => new BehaviorSequencer($"CSM {c}", [c], [B(0.9f, "BRANCH_Reaction_End")], [1f])).ToArray());
        List<string> r1 = [], r2 = [];
        var specs = ReactionTuning.Specs([reactions], _ => 1.2f, r1);
        Assert.NotNull(specs);
        Assert.Equal(Json(specs), Json(ReactionTuning.Specs(ReactionTuning.MarineRig, [reactions], _ => 1.2f, r2)));
        Assert.Equal(r1, r2);
    }
}
