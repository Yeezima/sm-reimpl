using System.Numerics;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou;
using Xunit;

namespace SmFormats.Tests;

/// <summary>The DA Dreadnought's presentation table and screen shake (owner, 2026-11-15): every action is wired to the data's sounds, effects and shake; the shake scales with the Camera Shake and Reduce Motion settings and fades with distance.</summary>
public class DreadnoughtFeelTests
{
    [Fact]
    public void Every_action_has_a_row_and_the_walk_and_stomp_rows_carry_the_datas_events()
    {
        foreach (var a in Enum.GetValues<DreadAction>()) Assert.Equal(a, DreadnoughtFeel.For(a).Action);
        Assert.Contains("play_dreadnought_footstep", DreadnoughtFeel.For(DreadAction.FootLeft).Sounds);
        Assert.Contains("play_dreadnought_sprint_footstep", DreadnoughtFeel.For(DreadAction.SprintFootRight).Sounds);
        Assert.Contains("play_sm_stomp_displace", DreadnoughtFeel.For(DreadAction.StompLeft).Sounds);
        Assert.Contains(DreadnoughtFeel.For(DreadAction.StompRight).Effects, e => e.Name == "knockback_stomp_dmg_furied" && e.Scale == 0.5f);
        Assert.Contains(DreadnoughtFeel.For(DreadAction.StompRight).Effects, e => e.Name == "interrupt_stomp_dmg" && e.Scale == 0.75f);
        Assert.Contains(DreadnoughtFeel.For(DreadAction.Entry).Effects, e => e.Name == "powersource_glow" || e.Name == "amb_chaos_portal_clouds");
        Assert.Contains(DreadnoughtFeel.For(DreadAction.Buff).Effects, e => e.Name == "inq_weapon_center_lightning");
        Assert.Equal("play_dreadnought_engine", DreadnoughtFeel.EngineEvent);
        Assert.Equal(1.75f, DreadnoughtFeel.CannonMuzzle.Scale);
        Assert.Contains("play_dreadnought_sm_assault_cannon", DreadnoughtFeel.CannonSounds);
    }

    [Fact]
    public void Footfall_shake_is_the_datas_and_fades_with_distance()
    {
        var f = DreadnoughtFeel.Footfall;
        Assert.Equal((0.1f, 0.05f, 2.5f, 15f), (f.Duration, f.EaseOut, f.Near, f.Far));
        Assert.Equal(0.75f, f.Pitch.Amplitude);
        Assert.Equal(1f, f.Gain(2f));
        Assert.Equal(0f, f.Gain(15f));
        Assert.InRange(f.Gain(8.75f), 0.49f, 0.51f);
        Assert.Equal(1f, f.Envelope(0.05f));
        Assert.Equal(0f, f.Envelope(f.Total + 0.01f));
    }

    [Fact]
    public void Settings_scale_the_shake_and_reduce_motion_removes_it()
    {
        var active = new[] { (DreadnoughtFeel.Stomp, 0.1f, 3f, false) };
        var full = DreadnoughtFeel.Mix(active, 1f, false);
        var half = DreadnoughtFeel.Mix(active, 0.5f, false);
        Assert.True(full.Degrees.Length() > 0.1f);
        Assert.Equal(full.Degrees.Length() * 0.5f, half.Degrees.Length(), 4);
        Assert.Equal(Vector3.Zero, DreadnoughtFeel.Mix(active, 1f, true).Degrees);
        Assert.Equal(Vector3.Zero, DreadnoughtFeel.Mix(active, 0f, false).Offset);
        // the stomp area's shake is for the player it hits only
        Assert.Equal(Vector3.Zero, DreadnoughtFeel.Mix([(DreadnoughtFeel.StompArea, 0.01f, 1f, false)], 1f, false).Degrees);
        Assert.NotEqual(Vector3.Zero, DreadnoughtFeel.Mix([(DreadnoughtFeel.StompArea, 0.01f, 1f, true)], 1f, false).Degrees);
    }

    [Fact]
    public void Speeds_are_one_point_three_times_the_data()
    {
        Assert.Equal(6.5f * 1.3f, DreadnoughtRules.RunSpeed, 4);
        Assert.Equal(11f * 1.3f, DreadnoughtRules.SprintSpeed, 4);
        Assert.Equal(1.3f, DreadnoughtRules.MoveSpeed);
    }

    [Fact]
    public void The_arm_has_a_start_cue_for_every_strike_and_the_datas_sounds_and_effects()
    {
        foreach (var k in Enum.GetValues<DaArmKey>()) Assert.NotNull(DreadnoughtArm.Cue(k, DaArmBeat.Start));
        Assert.Contains("play_dreadnought_melee_pwrup_x1", DreadnoughtArm.Cue(DaArmKey.Light1, DaArmBeat.Start)!.Sounds);
        Assert.Contains("play_dreadnought_melee_pwrup_y1", DreadnoughtArm.Cue(DaArmKey.Slam, DaArmBeat.Start)!.Sounds);
        Assert.Contains("play_dreadnought_swing_end", DreadnoughtArm.Cue(DaArmKey.Slam, DaArmBeat.Antic)!.Sounds);
        Assert.Contains("play_dreadnought_melee_antic_x1", DreadnoughtArm.Cue(DaArmKey.Rush, DaArmBeat.Start)!.Sounds);
        Assert.Contains(DreadnoughtArm.Cue(DaArmKey.Rush, DaArmBeat.Start)!.Effects, e => e.Name == "bullrush_swing" && e.Scale == 3f);
        Assert.Equal(2, DreadnoughtArm.Cue(DaArmKey.Slam, DaArmBeat.Hit)!.Effects.Count(e => e.Name == "thunderhammer_dmg_displace_heavy"));
        Assert.Contains(DreadnoughtArm.Cue(DaArmKey.Slam, DaArmBeat.Swing)!.Effects, e => e.Name == "plasma_charging");
        Assert.Equal(DaArmKey.Stomp, DreadnoughtArm.KeyOf(true, 1));
        Assert.Equal(DaArmKey.Light2, DreadnoughtArm.KeyOf(false, 2));
        // the beats come in order before the hit
        float hit = 0.867f;
        Assert.True(DreadnoughtArm.BeatTime(DaArmBeat.Start, hit) < DreadnoughtArm.BeatTime(DaArmBeat.Antic, hit));
        Assert.True(DreadnoughtArm.BeatTime(DaArmBeat.Antic, hit) <= DreadnoughtArm.BeatTime(DaArmBeat.Swing, hit));
        Assert.True(DreadnoughtArm.BeatTime(DaArmBeat.Swing, hit) <= DreadnoughtArm.BeatTime(DaArmBeat.Hit, hit));
    }

    [Fact]
    public void Lights_exhaust_and_rumble_carry_the_datas_values_and_the_settings_scale_the_rumble()
    {
        Assert.Equal(12, DreadnoughtArm.Lights.Length);
        Assert.Contains(DreadnoughtArm.Lights, l => l.Name == "light_headlight_red" && l.Marker == "fx_lefteye_csm");
        Assert.Contains(DreadnoughtArm.AllMarkers(), m => m == "fx_l_exhaust02");
        Assert.Equal((0.1f, 0.4f), (DreadnoughtArm.FootLeft.Small, DreadnoughtArm.FootLeft.Large));
        Assert.Equal((0.6f, 0.35f, 0.5f), (DreadnoughtArm.StompSelf.Small, DreadnoughtArm.StompSelf.Large, DreadnoughtArm.StompSelf.Seconds));
        Assert.Equal(0f, DreadnoughtArm.RumbleScale(1f, true));
        Assert.Equal(0.5f, DreadnoughtArm.RumbleScale(0.5f, false));
    }
}
