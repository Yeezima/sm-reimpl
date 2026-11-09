using SmContent;

namespace SmFormats.Tests;

/// <summary>sound/stalker-hit-confirm (owner 2026-11-09): the Stalker Bolter and the Daemon Eye Bolter confirm a hit to the shooter with the data's <c>play_stalker_smack</c>; other weapons keep theirs.</summary>
public class StalkerHitConfirmTests
{
    [Theory]
    [InlineData("sm_mp_ranged_sniper_rifle")]    // Stalker Bolter
    [InlineData("csm_mp_ranged_sniper_rifle")]   // Daemon Eye Bolter
    [InlineData("SM_MP_RANGED_SNIPER_RIFLE")]
    public void Both_rifles_map_to_the_stalker_smack(string wargear)
    {
        Assert.True(StalkerHitConfirm.Is(wargear));
        Assert.Equal("play_stalker_smack", StalkerHitConfirm.EventFor(wargear, "play_bolter_smack"));
        Assert.Equal("play_stalker_smack", StalkerHitConfirm.EventFor(wargear, null));
        Assert.True(StalkerHitConfirm.ReplacesMusouSmack(wargear));
    }

    [Theory]
    [InlineData("sm_mp_ranged_bolter", "play_bolter_smack")]
    [InlineData("csm_mp_ranged_storm_bolter", "play_bolter_smack")]
    [InlineData("sm_mp_exotic_heavy_bolter", "play_bolter_smack")]
    [InlineData("sm_mp_ranged_plasma_gun", "play_plasma_smack")]
    [InlineData("csm_mp_ranged_grenade_launcher", "play_grenade_launcher_smack")]
    [InlineData("sm_mp_pistol_bolt_pistol", "play_bolter_smack")]
    public void Other_weapons_keep_their_own_confirm(string wargear, string dataEvent)
    {
        Assert.False(StalkerHitConfirm.Is(wargear));
        Assert.Equal(dataEvent, StalkerHitConfirm.EventFor(wargear, dataEvent));
        Assert.False(StalkerHitConfirm.ReplacesMusouSmack(wargear));
    }

    [Fact]
    public void Nothing_is_no_stalker()
    {
        Assert.False(StalkerHitConfirm.Is(null));
        Assert.False(StalkerHitConfirm.Is(""));
        Assert.Null(StalkerHitConfirm.EventFor(null, null));
    }

    [Fact]
    public void The_confirm_plays_at_the_datas_level()
    {
        // No distance falloff, no mix-pass gain or high-pass: the data's 2D smack as it is.
        Assert.Equal(VoiceOffset.None, StalkerHitConfirm.Offset);
        Assert.False(StalkerHitConfirm.Offset.HighPass);
    }

    /// <summary>The data the rule rests on, read from the install (skipped without one; no original bytes kept): both rifles bind play_stalker_smack, the bolter play_bolter_smack.</summary>
    [MeleeInstallPinTests.InstallFact]
    public void The_installs_rifles_bind_the_stalker_smack()
    {
        string game = GameInstall.TryLocate()!.Dir;
        string[] wargear = ["sm_mp_ranged_sniper_rifle", "csm_mp_ranged_sniper_rifle", "sm_mp_ranged_bolter"];
        var t = SoundTriggers.Load(game, wargear, [], (IReadOnlyList<SmFormats.Havok.BehaviorGraph>?)null);
        string? Confirm(string w) => t.File(w)?.Of("player_hit_something_action").FirstOrDefault()?.StartEvent;
        Assert.Equal(StalkerHitConfirm.Event, Confirm("sm_mp_ranged_sniper_rifle"));
        Assert.Equal(StalkerHitConfirm.Event, Confirm("csm_mp_ranged_sniper_rifle"));
        Assert.Equal("play_bolter_smack", Confirm("sm_mp_ranged_bolter"));
    }
}
