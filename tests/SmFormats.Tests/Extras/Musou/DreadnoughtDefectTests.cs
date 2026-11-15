using SmContent.Extras.Musou;
using Xunit;

namespace SmFormats.Tests;

/// <summary>The Dreadnought film defects of 2026-11-15 (muzzle flash in the air, the entrance cue firing on death, the screen tints): the rules the client uses, pinned engine-free.</summary>
public class DreadnoughtDefectTests
{
    /// <summary>Data: <c>autocannon_muzzle</c>'s bloom ring starts 0.809 m ahead of the effect's origin at scale 1 (<c>smtool fx-dump auto autocannon_muzzle</c>); the cannon plays it at 1.75.</summary>
    const float BloomOffset = 0.809f;

    [Fact]
    public void The_flash_core_and_the_tracers_start_sit_within_30_cm_of_the_barrels_tip()
    {
        // the marker is MarkerToTip ahead of the tip; the tracer's start is the marker brought back by TipShift, the flash's origin likewise with its core ahead of it
        float tracerAtTip = DreadnoughtFeel.MarkerToTip - DreadnoughtFeel.TipShift("fx_muzzle_assault_cannon");
        float flashOrigin = DreadnoughtFeel.MarkerToTip - DreadnoughtFeel.TipShift("fx_muzzle_autocannon_top");   // marker-relative distance of the flash's origin ahead of the tip
        float core = flashOrigin + BloomOffset * DreadnoughtFeel.CannonMuzzle.Scale;
        Assert.InRange(MathF.Abs(tracerAtTip), 0f, 0.3f);
        Assert.InRange(MathF.Abs(core), 0f, 0.3f);
        Assert.True(BloomOffset * DreadnoughtFeel.CannonMuzzle.Scale > 1.0f, "unpulled, the core is over a metre ahead of its origin (the detached flash of the film)");
    }

    [Fact]
    public void The_chaos_twins_flash_core_sits_at_its_marker_which_is_on_the_barrel_tip()
    {
        float origin = -DreadnoughtFeel.TipShift("fx_muzzle_autocannon_top", chaos: true);   // metres from the marker
        Assert.InRange(MathF.Abs(origin + BloomOffset * DreadnoughtFeel.CannonMuzzle.Scale), 0f, 0.3f);
        Assert.Equal(0f, DreadnoughtFeel.TipShift("fx_muzzle_assault_cannon", chaos: true));
    }

    [Fact]
    public void Only_the_cannons_muzzle_markers_are_moved_not_the_melta_or_the_casings()
    {
        Assert.True(DreadnoughtFeel.IsFlashMarker("fx_muzzle_autocannon_top"));
        Assert.True(DreadnoughtFeel.IsFlashMarker("FX_MUZZLE_AUTOCANNON_BOTTOM"));
        Assert.False(DreadnoughtFeel.IsFlashMarker("fx_muzzle_assault_cannon"));
        Assert.Equal(DreadnoughtFeel.MarkerToTip, DreadnoughtFeel.TipShift("fx_muzzle_assault_cannon"));
        Assert.Equal(0f, DreadnoughtFeel.TipShift("fx_Muzzle"));
        Assert.Equal(0f, DreadnoughtFeel.TipShift("fx_shells_eject_assault_cannon"));
    }

    [Fact]
    public void The_entrance_cue_plays_on_arrival_and_never_on_death_or_twice()
    {
        Assert.True(DreadnoughtFeel.EntryDue(alive: true, corpse: false, sinceBorn: 0.02, entered: false));
        Assert.False(DreadnoughtFeel.EntryDue(alive: false, corpse: false, sinceBorn: 0.02, entered: false));   // a dead state
        Assert.False(DreadnoughtFeel.EntryDue(alive: true, corpse: true, sinceBorn: 0.02, entered: false));     // the wreck (DeathStart fired Entry 0.02 s later before)
        Assert.False(DreadnoughtFeel.EntryDue(alive: true, corpse: false, sinceBorn: 0.02, entered: true));
        Assert.False(DreadnoughtFeel.EntryDue(alive: true, corpse: false, sinceBorn: 4.0, entered: false));     // long after the arrival
    }

    [Fact]
    public void The_death_row_has_no_entrance_effects_and_the_death_fire_is_the_datas_effect()
    {
        Assert.DoesNotContain(DreadnoughtFeel.For(DreadAction.DeathStart).Effects, e => e.Name is "amb_chaos_portal_clouds" or "cl_force_field");
        Assert.Contains(DreadnoughtFeel.For(DreadAction.DeathBlast).Effects, e => e.Name == DreadnoughtFeel.DeathFireEffect && e.DelaySeconds == DreadnoughtFeel.DeathFireDelay);
    }

    [Fact]
    public void The_scaled_up_effects_have_a_light_reach_so_no_light_tints_the_whole_view()
    {
        Assert.InRange(DreadnoughtFeel.LightReach, 1f, 8f);
        var glow = DreadnoughtFeel.For(DreadAction.Buff).Effects.First(e => e.Name == "powersource_glow");
        Assert.True(glow.Scale >= 11f, "the data scales the power-up glow x11 and x12: its light range would be that many times the data's");
        Assert.Contains("powersource_glow", DreadnoughtArm.AllEffectNames());
        Assert.Contains("amb_chaos_portal_clouds", DreadnoughtArm.AllEffectNames());
    }

    [Fact]
    public void Overcharges_wave_is_drawn_at_the_size_of_its_3_m_radius_with_a_dust_ring()
    {
        // knockback_stomp_dmg_furied's orange ring runs 1.165 to 6.218 m at scale 1 (fx-dump); the wave's radius is 3 m
        foreach (var e in DreadnoughtArm.OverchargeWave.Where(w => w.Name == "knockback_stomp_dmg_furied")) Assert.InRange(6.218f * e.Scale, 2.8f, 3.4f);
        Assert.Contains(DreadnoughtArm.OverchargeWave, e => e.Name == "jump_land");
    }
}
