using System.Numerics;
using SmContent;
using SmFormats.Dcm;
using SmFormats.Havok;
using SmSim;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>M35: the reload animation data read from behaviour graphs, and the weapon magazines found in models. Synthetic data only.</summary>
public class ReloadTests
{
    static IReadOnlyList<BehaviorGraphDetail> Read(params StructuredGraph[] graphs) =>
        HavokBehaviorDetailReader.Read(new HavokPackfile(BuildBehaviorStructure(graphs)));

    static readonly string[] Bones = ["RIG_Root", "RIG_Pelvis", "RIG_SpineD", "RIG_L_Hand"];

    /// <summary>A reload graph (one sequencer, clip duration bound to a variable) and a base graph with the weapon's navigation and reload states.</summary>
    static IReadOnlyList<BehaviorGraphDetail> Graphs(bool enforced = true)
    {
        var reload = new StructuredGraph("SM_X_Reload", ["Reload_Wargear_Ammunition", "BRANCH_Reload_Finished"], ["FLT_WeaponReloadTime"],
            new SequencerNode("Seq", new ClipNode("gen", "SM_X_Reload_01", Mode: 0, EnforcedDurationVariable: enforced ? 0 : null),
            [
                new Tag("Action", 0.05f, 0.05f, "SFX_X_Remove_Clip"),
                new Tag("Action", 0.20f, 0.20f, "SFX_X_Retrieve_Clip"),
                new Tag("Action", 0.30f, 0.30f, "SFX_X_Load_Clip"),
                new Tag("Action", 0.40f, 0.40f, "SFX_X_Reload_Click"),   // "reload" is not a magazine load
                new Tag("Event", 0.80f, 0.80f, 0),
                new Tag("Branch", 1f, 1f, 1),
            ]));
        var nav = new ClipNode("nav", "SM_X_Nav");
        var legs = new BlendChildFixture(1, new ReferenceNode("X Nav Ref -> Behaviors\\SM_X_NAV.hkxg", "Behaviors\\SM_X_NAV.hkxg"), [1f, 1f, 0f, 0f]);
        var upper = new BlendChildFixture(1, new ReferenceNode("REF_XReload -> Behaviors\\SM_X_Reload.hkxg", "Behaviors\\SM_X_Reload.hkxg"), [0f, 0f]);
        var machine = new StateMachineNode("X Behavior",
        [
            new StateFixture(1, "X Navigation", nav, [new TransitionFixture(3, EventId: 0, Priority: 1, BlendDuration: 0.1f, EffectName: "Nav to Reload")]),
            new StateFixture(3, "X Reload", new BlendNode("X_ReloadBlend", [legs, upper]),
                [new TransitionFixture(1, BranchEventId: 1, Priority: 1, BlendDuration: 0.133f, EffectName: "Back")]),
        ], StartStateId: 1);
        var baseGraph = new StructuredGraph("SM_Base_MP", ["BTN_Reload", "BRANCH_Reload_Finished"], [], machine);
        return Read(reload, baseGraph);
    }

    static readonly ReloadAnimations.Map XMap = new(ReloadFamily.Bolter, "SM_X_Reload", "SM_Base_MP", "X Reload", "X Navigation");

    [Fact]
    public void A_reload_clip_its_tags_blend_times_and_bone_mask_are_read()
    {
        var data = ReloadAnimations.From(Graphs(), Bones, clip => clip == "SM_X_Reload_01" ? 2f : throw new KeyNotFoundException(clip), [XMap]);
        var r = Assert.Single(data.Clips);
        Assert.Equal((ReloadFamily.Bolter, "SM_X_Reload", "SM_X_Reload_01", 2f, "FLT_WeaponReloadTime"), (r.Family, r.Graph, r.Clip, r.ClipLength, r.EnforcedDuration));
        Assert.Empty(r.Notes);
        // The tags in time; the event and branch names come from the graph's event list.
        Assert.Equal(["Action:SFX_X_Remove_Clip", "Action:SFX_X_Retrieve_Clip", "Action:SFX_X_Load_Clip", "Action:SFX_X_Reload_Click", "Event:Reload_Wargear_Ammunition", "Branch:BRANCH_Reload_Finished"],
            r.Tags.Select(t => $"{t.Kind}:{t.Name}"));
        Assert.Equal([0.05f, 0.2f, 0.3f, 0.4f, 0.8f, 1f], r.Tags.Select(t => t.Start));
        Assert.Equal((0.05f, 0.3f, 0.8f), (r.MagazineOut!.Value, r.MagazineIn!.Value, r.AmmunitionAt!.Value));
        Assert.Equal((0.1f, 0.133f), (r.BlendIn!.Value, r.BlendOut!.Value));
        // The reload child's mask: zero on the legs it lists, one on every bone past its end (the upper body).
        Assert.Equal(new Dictionary<string, float> { ["RIG_Root"] = 0f, ["RIG_Pelvis"] = 0f, ["RIG_SpineD"] = 1f, ["RIG_L_Hand"] = 1f }, r.BoneMask);
    }

    [Fact]
    public void The_clip_is_stretched_to_the_reload_time_only_when_its_duration_is_enforced()
    {
        var stretched = ReloadAnimations.From(Graphs(enforced: true), Bones, _ => 2f, [XMap]).Clips[0];
        Assert.Equal(2f / 1.3f, stretched.PlaybackRate(1.3f), 5);
        Assert.Equal(1f, stretched.PlaybackRate(0f));   // no usable time: the clip's own speed
        var own = ReloadAnimations.From(Graphs(enforced: false), Bones, _ => 2f, [XMap]).Clips[0];
        Assert.Null(own.EnforcedDuration);
        Assert.Equal(1f, own.PlaybackRate(1.3f));
    }

    [Fact]
    public void Missing_graphs_states_and_clips_are_noted_not_thrown()
    {
        // No reload graph: the family is left out. No base graph: the clip is read, the mask and blends are null with a note.
        var reload = Graphs().Where(g => g.Name == "SM_X_Reload").ToList();
        var noBase = Assert.Single(ReloadAnimations.From(reload, Bones, _ => 2f, [XMap]).Clips);
        Assert.Null(noBase.BoneMask);
        Assert.Null(noBase.BlendIn);
        Assert.Contains(noBase.Notes, n => n.Contains("SM_Base_MP not found"));
        Assert.Empty(ReloadAnimations.From(Graphs().Where(g => g.Name == "SM_Base_MP").ToList(), Bones, _ => 2f, [XMap]).Clips);
        var wrongState = ReloadAnimations.From(Graphs(), Bones, _ => 2f, [XMap with { ReloadState = "Y Reload" }]).Clips[0];
        Assert.Null(wrongState.BoneMask);
        Assert.Contains(wrongState.Notes, n => n.Contains("no state 'Y Reload'"));
        // An unreadable clip keeps the data but notes it (length 0: the pose layer then has nothing to play).
        var unreadable = ReloadAnimations.From(Graphs(), Bones, _ => throw new KeyNotFoundException("SM_X_Reload_01"), [XMap]).Clips[0];
        Assert.Equal(0f, unreadable.ClipLength);
        Assert.Contains(unreadable.Notes, n => n.Contains("unreadable"));
    }

    [Theory]
    [InlineData("sm_mp_ranged_bolter", ReloadFamily.Bolter)]
    [InlineData("csm_mp_ranged_bolter_gold", ReloadFamily.Bolter)]
    [InlineData("sm_mp_pistol_bolter", ReloadFamily.Bolter)]   // the bolter in the pistol slot: the bolter's reload (interpretation, as hip fire)
    [InlineData("sm_mp_pistol_bolt_pistol_devastator", ReloadFamily.BoltPistol)]
    [InlineData("csm_mp_pistol_bolt_pistol", ReloadFamily.BoltPistol)]
    [InlineData("sm_mp_pistol_plasma_pistol", ReloadFamily.PlasmaPistol)]
    [InlineData("sm_mp_ranged_storm_bolter", ReloadFamily.StormBolter)]
    [InlineData("csm_mp_ranged_sniper_rifle", ReloadFamily.StalkerPattern)]
    [InlineData("sm_mp_ranged_grenade_launcher", ReloadFamily.GrenadeLauncher)]
    [InlineData("sm_mp_exotic_las_cannon", ReloadFamily.Lascannon)]
    public void The_reload_family_follows_the_wargear_name(string wargear, ReloadFamily family) => Assert.Equal(family, ReloadAnimations.FamilyOf(wargear));

    [Theory]
    [InlineData("sm_mp_exotic_heavy_bolter")]
    [InlineData("csm_mp_exotic_heavy_bolter")]
    [InlineData("sm_mp_ranged_plasma_gun")]
    [InlineData("sm_mp_exotic_plasma_cannon")]
    [InlineData("sm_mp_ranged_melta")]
    [InlineData("sm_mp_melee_chainsword")]
    public void Weapons_without_a_reload_clip_have_no_family(string wargear) =>
        Assert.Null(ReloadAnimations.FamilyOf(wargear));   // the heavy bolter's name contains "bolter": it must not play the bolter's reload

    [Theory]
    [InlineData("GEO_Clip_Plasma", true)]
    [InlineData("GEO_Bolt_Pistol_AmmoClip", true)]
    [InlineData("GEO_clip", true)]
    [InlineData("GEO_Ammo_clip", true)]
    [InlineData("GEO_drum", true)]
    [InlineData("GEO_Magazine", true)]
    [InlineData("GEO_Upg_clip", false)]   // upgrade geometry
    [InlineData("GEO_Bolter", false)]
    [InlineData("GEO_belt", false)]
    public void Magazine_nodes_are_found_by_name(string node, bool magazine) => Assert.Equal(magazine, WeaponModel.IsMagazineNode(node));

    [Fact]
    public void A_models_magazine_is_its_first_visible_rigid_part_with_a_magazine_name()
    {
        WeaponPart Part(int block, string node, bool visible = true, bool skinned = false, bool simulated = false) =>
            new(block, node, Matrix4x4.Identity, skinned, simulated, visible, visible ? null : "hidden");
        var model = new WeaponModel("pkg", null!, null!,
            [Part(0, "GEO_Upg_clip", visible: false), Part(1, "GEO_Gun"), Part(2, "GEO_Ammo_clip", skinned: true), Part(3, "GEO_clip"), Part(4, "GEO_drum")], []);
        Assert.Equal("GEO_clip", model.Magazine!.Node);
        Assert.Null(new WeaponModel("pkg", null!, null!, [Part(0, "GEO_Chaos_pistol")], []).Magazine);
        Assert.Null(new WeaponModel("pkg", null!, null!, [Part(0, "GEO_clip", simulated: true)], []).Magazine);
    }

    // ---- magazines inside a body block (the Chaos bolter and bolt pistol) ----

    /// <summary>Two separate quads: one near the origin, one at x = 10; the second has a vertex split in two (a texture seam) that must not split the shell.</summary>
    static (Vector3[] Positions, ushort[] Indices) TwoShells()
    {
        Vector3[] p =
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),           // shell 0
            new(10, 0, 0), new(11, 0, 0), new(11, 1, 0), new(10, 1, 0),       // shell 1
            new(11, 0, 0),                                                    // the same position as vertex 5, split for a seam
            new(0, 0, 0),                                                     // unused vertex
        ];
        return (p, [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 6, 7]);
    }

    [Fact]
    public void Shells_are_the_triangles_that_share_a_position_even_across_split_vertices()
    {
        var (positions, indices) = TwoShells();
        var (shell, count) = MeshShells.Label(positions, indices);
        Assert.Equal(2, count);
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1, 1, -1], shell);   // the seam vertex 8 joins shell 1; the unused vertex 9 (at shell 0's origin) belongs to none
    }

    [Fact]
    public void Shells_are_picked_by_the_centre_of_their_bounds_after_the_transform()
    {
        var (positions, indices) = TwoShells();
        var (near, one) = MeshShells.PickInBox(positions, indices, Matrix4x4.Identity, new Vector3(-1, -1, -1), new Vector3(2, 2, 1));
        Assert.Equal(1, one);
        Assert.Equal([true, true, true, true, false, false, false, false, false, false], near);
        // Moved by the transform, the other shell is the one in the box; a box that contains neither picks nothing.
        var (far, two) = MeshShells.PickInBox(positions, indices, Matrix4x4.CreateTranslation(-10, 0, 0), new Vector3(-1, -1, -1), new Vector3(2, 2, 1));
        Assert.Equal(1, two);
        Assert.Equal([false, false, false, false, true, true, true, true, true, false], far);
        Assert.Equal(0, MeshShells.PickInBox(positions, indices, Matrix4x4.Identity, new Vector3(50, 50, 50), new Vector3(60, 60, 60)).Shells);
        // A box around both shells picks both; limited to one it keeps the bigger (shell 0 has two triangles, shell 1 three).
        var (_, two2) = MeshShells.PickInBox(positions, indices, Matrix4x4.Identity, new Vector3(-1, -1, -1), new Vector3(20, 2, 1));
        Assert.Equal(2, two2);
        var (biggest, one2) = MeshShells.PickInBox(positions, indices, Matrix4x4.Identity, new Vector3(-1, -1, -1), new Vector3(20, 2, 1), maxShells: 1);
        Assert.Equal(1, one2);
        Assert.Equal([false, false, false, false, true, true, true, true, true, false], biggest);
    }

    [Fact]
    public void An_embedded_magazine_names_the_biggest_visible_rigid_part_and_only_without_a_part_of_its_own()
    {
        // A model whose package has a magazine box (the Chaos bolter), made of two blocks: a small one and the gun body.
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build(
        [
            new SyntheticDcmWriter.Block([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new("a", 0, 3, 0, 3)]),
            new SyntheticDcmWriter.Block([new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)], [0, 1, 2, 0, 2, 3], [new("b", 0, 6, 0, 4)]),
        ]));
        var staticModel = new StaticModel("x.oppc", "x", mesh, []);
        WeaponPart Part(int block, string node, bool visible = true) => new(block, node, Matrix4x4.Identity, false, false, visible, null);
        var body = new WeaponModel("chaos_bolter_mp", staticModel, null!, [Part(0, "GEO_small"), Part(1, "GEO_chaos_bolter")], []);
        var embedded = body.EmbeddedMagazine!.Value;
        Assert.Equal("GEO_chaos_bolter", embedded.Part.Node);
        Assert.Equal(WeaponModel.EmbeddedMagazineBoxes["chaos_bolter_mp"], (embedded.Min, embedded.Max));
        // An unknown package, or a model with a magazine part of its own, has none.
        Assert.Null(new WeaponModel("bolter_mp_basic", staticModel, null!, [Part(1, "GEO_Bolter")], []).EmbeddedMagazine);
        Assert.Null(new WeaponModel("chaos_bolter_mp", staticModel, null!, [Part(1, "GEO_chaos_bolter"), Part(0, "GEO_clip")], []).EmbeddedMagazine);
        // A hidden body part is not the body.
        Assert.Equal("GEO_small", new WeaponModel("chaos_bolter_mp", staticModel, null!, [Part(0, "GEO_small"), Part(1, "GEO_chaos_bolter", visible: false)], []).EmbeddedMagazine!.Value.Part.Node);
    }

    // ---- ReloadTimeline: clock, weight, clip fraction and the magazine moments ----

    static ReloadClip Clip(float? magOut = 0.05f, float? magIn = 0.3f, float? blendIn = 0.1f, float? blendOut = 0.133f, string? enforced = "FLT_WeaponReloadTime") =>
        new(ReloadFamily.Bolter, "G", "SM_X_Reload_01", 2f, enforced, [], magOut, magIn, 0.8f, new Dictionary<string, float> { ["RIG_SpineD"] = 1f }, blendIn, blendOut, []);

    const float Tick = 1f / 30f;

    [Fact]
    public void The_clip_fraction_follows_the_reload_time_and_the_weight_blends_in_and_out()
    {
        var t = new ReloadTimeline();
        t.Begin(Clip(), 1.3f, 0f);
        Assert.True(t.Active);
        Assert.Equal(0f, t.Weight);
        t.Advance(0.05f, null, Tick);
        Assert.Equal(0.5f, t.Weight, 4);                                   // half of the 0.1 s blend in
        Assert.Equal(0.05f / 1.3f, t.ClipFraction, 4);                     // enforced: the clip is stretched over the reload
        t.Advance(0.65f, null, Tick);
        Assert.Equal(1f, t.Weight);
        Assert.Equal(0.7f / 1.3f, t.ClipFraction, 4);
        t.Advance(5f, null, Tick);
        Assert.Equal(1.3f, t.Clock);                                       // never past the end
        t.Finish(completed: true);
        Assert.True(t.Ending);
        Assert.Equal(1f, t.ClipFraction);
        Assert.Equal(1f, t.Weight);
        t.Advance(0.0665f, null, Tick);                                    // half of the 0.133 s blend out
        Assert.Equal(0.5f, t.Weight, 3);
        t.Advance(0.1f, null, Tick);
        Assert.False(t.Shown);
        Assert.Equal(0f, t.Weight);
        // Without an enforced duration the clip plays at its own speed: 1.3 s into a 2 s clip.
        var own = new ReloadTimeline();
        own.Begin(Clip(enforced: null), 1.3f, 1.3f);
        Assert.Equal(1.3f / 2f, own.ClipFraction, 4);
    }

    [Fact]
    public void A_cancelled_reload_fades_from_the_weight_it_had()
    {
        var t = new ReloadTimeline();
        t.Begin(Clip(), 1.3f, 0f);
        t.Advance(0.05f, null, Tick);                                      // weight 0.5, in the blend in
        t.Finish(completed: false);
        Assert.Equal(0.5f, t.Weight, 4);
        t.Advance(ReloadTimeline.CancelFadeSeconds / 2, null, Tick);
        Assert.Equal(0.25f, t.Weight, 3);
        t.Advance(1f, null, Tick);
        Assert.False(t.Shown);
        // The clip fraction is held where it stopped; finishing an inactive reload does nothing.
        t.Finish(completed: true);
        Assert.False(t.Shown);
    }

    [Fact]
    public void The_clock_follows_the_simulation_within_a_tick_and_snaps_beyond()
    {
        var t = new ReloadTimeline();
        t.Begin(Clip(), 1.3f, 0.2f);
        t.Advance(0.01f, 0.2f, Tick);
        Assert.Equal(0.21f, t.Clock, 4);                                   // within a tick of the target: its own pace
        t.Advance(0.01f, 0.5f, Tick);
        Assert.Equal(0.5f, t.Clock, 4);                                    // beyond: snapped to the target
        t.Advance(0.01f, 0.1f, Tick);
        Assert.Equal(0.1f, t.Clock, 4);
    }

    [Fact]
    public void The_magazine_leaves_and_returns_once_each_and_later_when_the_hand_is_later()
    {
        var t = new ReloadTimeline();
        t.Begin(Clip(), 1.3f, 0f);                                         // tags: out 0.05, in 0.3 of the clip
        Assert.False(t.TakeMagazineOut());
        t.Advance(0.04f * 1.3f, null, Tick);
        Assert.False(t.TakeMagazineOut());
        t.Advance(0.02f * 1.3f, null, Tick);                               // fraction 0.06
        Assert.True(t.TakeMagazineOut());
        Assert.False(t.TakeMagazineOut());                                 // once
        Assert.False(t.TakeMagazineIn());
        t.Advance(0.25f * 1.3f, null, Tick);                               // fraction 0.31
        Assert.True(t.TakeMagazineIn());
        Assert.False(t.TakeMagazineIn());
        // The hand takes it at 0.11 and is back at 0.35: the later of the tag and the hand for both.
        var hand = new ReloadTimeline();
        hand.Begin(Clip(), 1.3f, 0f, handLeaves: 0.11f, handArrives: 0.35f);
        Assert.Equal((0.11f, 0.35f), (hand.MagazineOutAt!.Value, hand.MagazineInAt!.Value));
        hand.Advance(0.10f * 1.3f, null, Tick);
        Assert.False(hand.TakeMagazineOut());
        hand.Advance(0.02f * 1.3f, null, Tick);
        Assert.True(hand.TakeMagazineOut());
        hand.Advance(0.2f * 1.3f, null, Tick);                             // 0.32: past the load tag, the hand is not back
        Assert.False(hand.TakeMagazineIn());
        hand.Advance(0.04f * 1.3f, null, Tick);
        Assert.True(hand.TakeMagazineIn());
        // The new magazine is never in before the old one is out; a clip without tags has no magazine moments.
        var odd = new ReloadTimeline();
        odd.Begin(Clip(magOut: 0.4f, magIn: 0.3f), 1.3f, 0f);
        Assert.Equal(0.4f, odd.MagazineInAt!.Value);
        var none = new ReloadTimeline();
        none.Begin(Clip(magOut: null, magIn: null), 1.3f, 0f, handLeaves: 0.1f, handArrives: 0.3f);
        none.Advance(1.3f, null, Tick);
        Assert.Null(none.MagazineOutAt);
        Assert.False(none.TakeMagazineOut());
        Assert.False(none.TakeMagazineIn());
    }

    [Fact]
    public void A_reload_replaced_or_reset_starts_fresh_and_counts_its_serial()
    {
        var t = new ReloadTimeline();
        t.Begin(Clip(), 1.3f, 0f);
        t.Advance(0.5f, null, Tick);
        Assert.True(t.TakeMagazineOut());
        int first = t.Serial;
        t.Begin(Clip(), 0.75f, 0f);                                        // another reload replaces it
        Assert.NotEqual(first, t.Serial);
        Assert.False(t.MagazineGone);
        Assert.Equal(0f, t.Clock);
        t.Reset();
        Assert.False(t.Shown);
        Assert.NotEqual(first + 1, t.Serial);                              // a scheduled sound of the reload before no longer matches
    }

    // ---- ReloadInference: another player's reload from the shots seen ----

    static WeaponSpec Gun(int clip, int reserve, float reload = 1f, TriggerKind trigger = TriggerKind.FullAuto, float cooldown = 0.2f) =>
        new("gun", WeaponSlot.Ranged, trigger, 10f, cooldown, clip, reserve, reload, 100f, 0f, 0f, 1f, 1f, 1f, 1f, 1f);

    static IReadOnlyList<ReloadInference.Shot> Shots(int weapon, int count, int age = 0) =>
        Enumerable.Range(0, count).Select(_ => new ReloadInference.Shot(weapon, 1, age)).ToList();

    static readonly IReadOnlyList<ReloadInference.Shot> NoShots = [];

    /// <summary>Steps a frame at 60 Hz; returns the reload to show.</summary>
    static (int Weapon, float Total, float Elapsed)? Frame(ReloadInference r, IReadOnlyList<WeaponSpec> w, IEnumerable<ReloadInference.Shot> shots, ref double now,
        bool alive = true, bool busy = false, int slot = 0, Func<int, bool>? reloads = null)
    {
        now += 1.0 / 60;
        return r.Update(w, reloads ?? (_ => true), shots, alive, busy, slot, now, 1f / 60, Tick);
    }

    [Fact]
    public void The_shot_that_empties_the_clip_starts_a_reload_after_a_tick_and_completing_it_refills()
    {
        var weapons = new[] { Gun(3, 9, reload: 1f) };
        var r = new ReloadInference();
        double now = 0;
        Assert.Null(Frame(r, weapons, Shots(0, 2), ref now));
        Assert.Equal(2, r.Used(0));
        Assert.False(r.Inferring);
        Frame(r, weapons, Shots(0, 1), ref now);                           // emptied: waits one tick (full auto)
        Assert.True(r.Inferring);
        (int Weapon, float Total, float Elapsed)? shown = null;
        for (int i = 0; i < 4 && shown is null; i++) shown = Frame(r, weapons, NoShots, ref now);
        Assert.Equal(0, shown!.Value.Weapon);
        Assert.Equal(1f, shown.Value.Total);
        Assert.InRange(shown.Value.Elapsed, 0f, 0.05f);
        for (int i = 0; i < 70; i++) shown = Frame(r, weapons, NoShots, ref now);
        Assert.Null(shown);
        Assert.False(r.Inferring);
        Assert.Equal(0, r.Used(0));                                        // refilled
    }

    [Fact]
    public void A_semi_auto_weapon_waits_a_shot_cooldown_for_the_next_press()
    {
        var weapons = new[] { Gun(2, 8, reload: 1f, trigger: TriggerKind.SemiAuto, cooldown: 0.25f) };
        var r = new ReloadInference();
        double now = 0;
        Frame(r, weapons, Shots(0, 2), ref now);
        Assert.True(r.Inferring);
        Assert.True(r.Elapsed < -0.2f);                                    // not started: the press that starts it has not come
        for (int i = 0; i < 10; i++) Assert.Null(Frame(r, weapons, NoShots, ref now));   // 0.17 s: still waiting
        (int Weapon, float Total, float Elapsed)? shown = null;
        for (int i = 0; i < 10 && shown is null; i++) shown = Frame(r, weapons, NoShots, ref now);
        Assert.NotNull(shown);
        // The shot's age counts: a shot already 0.3 s old (9 ticks) is past the 0.25 s wait at once.
        var late = new ReloadInference();
        double t2 = 0;
        Assert.NotNull(Frame(late, weapons, Shots(0, 2, age: 9), ref t2));
    }

    [Fact]
    public void A_weapon_with_nothing_left_to_load_does_not_reload()
    {
        var weapons = new[] { Gun(2, 2, reload: 0.5f) };
        var r = new ReloadInference();
        double now = 0;
        Frame(r, weapons, Shots(0, 2), ref now);
        Assert.True(r.Inferring);                                          // 2 of 4 rounds fired: a reload
        for (int i = 0; i < 40; i++) Frame(r, weapons, NoShots, ref now);
        Assert.False(r.Inferring);
        Frame(r, weapons, Shots(0, 2), ref now);                           // all 4 fired: the reserve is spent
        Assert.False(r.Inferring);
        // A death brings the rounds back (the player spawns full).
        Frame(r, weapons, NoShots, ref now, alive: false);
        Frame(r, weapons, Shots(0, 2), ref now);
        Assert.True(r.Inferring);
    }

    [Fact]
    public void A_weapon_not_fired_for_longer_than_its_reload_was_refilled_by_hand()
    {
        var weapons = new[] { Gun(3, 9, reload: 1f) };
        var r = new ReloadInference();
        double now = 0;
        Frame(r, weapons, Shots(0, 2), ref now);
        now += 2.5;                                                        // longer than the reload time + 1 s
        Frame(r, weapons, Shots(0, 1), ref now);
        Assert.Equal(1, r.Used(0));
        Assert.False(r.Inferring);
        // Inside the window the count carries on (2 + 1 = the clip).
        var q = new ReloadInference();
        double n2 = 0;
        Frame(q, weapons, Shots(0, 2), ref n2);
        n2 += 1.5;
        Frame(q, weapons, Shots(0, 1), ref n2);
        Assert.True(q.Inferring);
    }

    [Fact]
    public void A_switch_a_reaction_or_death_cancels_and_the_clip_stays_empty()
    {
        var weapons = new[] { Gun(2, 8, reload: 1f), Gun(10, 10) };
        var r = new ReloadInference();
        double now = 0;
        Frame(r, weapons, Shots(0, 2), ref now);
        for (int i = 0; i < 5; i++) Frame(r, weapons, NoShots, ref now);
        Assert.True(r.Inferring);
        Assert.Null(Frame(r, weapons, NoShots, ref now, slot: 1));         // switched
        Assert.False(r.Inferring);
        Assert.Equal(2, r.Used(0));
        var b = new ReloadInference();
        double t2 = 0;
        Frame(b, weapons, Shots(0, 2), ref t2);
        for (int i = 0; i < 5; i++) Frame(b, weapons, NoShots, ref t2);
        Assert.Null(Frame(b, weapons, NoShots, ref t2, busy: true));       // hit or meleeing
        Assert.False(b.Inferring);
        // No reload is started from an emptying shot while busy, nor for a weapon that has none.
        var c = new ReloadInference();
        double t3 = 0;
        Frame(c, weapons, Shots(0, 2), ref t3, busy: true);
        Assert.False(c.Inferring);
        Frame(c, weapons, Shots(1, 10), ref t3, reloads: w => w != 1);
        Assert.False(c.Inferring);
        Assert.Equal(0, c.Used(1));
    }
}
