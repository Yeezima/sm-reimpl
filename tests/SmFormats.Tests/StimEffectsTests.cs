using SmContent;
using SmFormats.Baf;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// fx/stim-original (owner, 2026-11-14): the stim's look is the data's own. A synthetic wargear file in the install's layout (no original bytes) with the action's
/// <c>play_fx</c> commands per <c>actions_by_upgrade</c> entry: the default entry's five effects (the aura at the torso, both clavicles and the head, the icon at the entity's centre) and
/// Master-Crafted Wargear's bigger ones resolve to the data's names, markers and scales.
/// </summary>
public class StimEffectsTests
{
    const string Master = "mp_perk_equipment_effectiveness";

    static (string, object) N(string name, params (string, object)[] items) => (name, items);
    static (string, object) Fx(string name, float scale, string marker) => N("play_fx", ("$ref", "action_commands\\play_fx"), ("fx_package", "master_fx"), ("fx_name", name), ("scale", scale), ("length", 1f),
        ("attach_to_marker", true), ("trigger_point", "self"), N("marker", ("marker", marker)), ("lifetime", 5f), ("on_stop_policy", "stop_emitting"));
    static (string, object) Entry(string? upgrade, float k) => N("entry",
        N("required_upgrade_name", upgrade is null ? N("required_list") : N("required_list", ("upgrade_name", upgrade))),
        N("action", N("commands",
            Fx(StimTuning.GlowEffect, 3 * k, "fx_torso"), Fx(StimTuning.GlowEffect, 1.5f * k, "rig_r_clavicle"), Fx(StimTuning.GlowEffect, 1 * k, "fx_head"),
            Fx(StimTuning.GlowEffect, 1.5f * k, "rig_l_clavicle"), Fx(StimTuning.IconEffect, k, StimTuning.CentreMarker))));

    static ThrownEffects Read() => ThrownEffects.Read("sm_mp_eq_stimpack", BafDocument.Read(SyntheticBafWriter.Build(
        N("action_data", N("triggered_actions", N("actn_stims_activated", N("commands", N("actions_by_upgrade", N("actions", Entry(null, 1f), Entry(Master, 2f)))))))))
        , null, []);

    [Fact]
    public void The_default_entry_plays_the_data_s_aura_at_four_body_markers_and_the_icon_at_the_centre()
    {
        var plays = StimTuning.Effects(Read(), []);
        Assert.Equal(["fx_torso", "rig_r_clavicle", "fx_head", "rig_l_clavicle", "fx_cntr"], plays.Select(p => p.Marker));
        Assert.Equal(["stimpack_heal_looping", "stimpack_heal_looping", "stimpack_heal_looping", "stimpack_heal_looping", "stimpack_heal_icon_looping"], plays.Select(p => p.Name));
        Assert.Equal([3f, 1.5f, 1f, 1.5f, 1f], plays.Select(p => p.Scale));
        Assert.All(plays, p => Assert.True(p.Attached && p.Lifetime == 5f && p.OnStop == "stop_emitting" && p.Package == "master_fx"));
        Assert.Equal("fx_cntr", StimTuning.CentreMarker);
    }

    [Fact]
    public void Master_Crafted_Wargear_chooses_its_own_bigger_entry_and_another_perk_does_not()
    {
        var master = StimTuning.Effects(Read(), [Master]);
        Assert.Equal([6f, 3f, 2f, 3f, 2f], master.Select(p => p.Scale));
        Assert.Equal(StimTuning.IconEffect, master[^1].Name);
        Assert.Equal([3f, 1.5f, 1f, 1.5f, 1f], StimTuning.Effects(Read(), ["mp_perk_equipment_double"]).Select(p => p.Scale));
    }
}
