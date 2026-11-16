using SmContent;
using SmSim;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>musou/audio-pass (owner, 2026-11-09): the gate that keeps Annihilation and Seize Ground's sound as it was, the bolter family, the far Orks.</summary>
public class MusouAudioRulesTests
{
    [Theory]
    [InlineData(ContentExtras.None)]
    [InlineData(ContentExtras.Orks)]
    [InlineData(ContentExtras.Gore)]
    [InlineData(ContentExtras.Orks | ContentExtras.Gore)]
    public void Classic_PvP_runs_none_of_the_pass(ContentExtras extras) => Assert.False(MusouAudioRules.Applies(extras));

    [Fact]
    public void Musou_runs_the_pass() => Assert.True(MusouAudioRules.Applies(ContentExtras.Musou));

    [Theory]
    [InlineData("sm_mp_ranged_bolter")]
    [InlineData("csm_mp_ranged_bolter")]
    [InlineData("sm_mp_ranged_storm_bolter")]
    [InlineData("csm_mp_ranged_storm_bolter")]
    [InlineData("sm_mp_ranged_sniper_rifle")]
    [InlineData("csm_mp_ranged_sniper_rifle")]
    [InlineData("sm_mp_exotic_heavy_bolter")]
    [InlineData("csm_mp_exotic_heavy_bolter")]
    public void The_bolter_rifles_are_found(string wargear) => Assert.True(MusouAudioRules.IsBolterRifle(wargear));

    [Theory]
    [InlineData("sm_mp_ranged_bolt_pistol")]
    [InlineData("sm_mp_ranged_plasma_gun")]
    [InlineData("sm_mp_ranged_melta")]
    [InlineData("sm_mp_ranged_lascannon")]
    [InlineData("sm_mp_mel_chainsword")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_weapons_and_pistols_are_not(string? wargear) => Assert.False(MusouAudioRules.IsBolterRifle(wargear));

    [Fact]
    public void The_far_Orks_are_far_quiet_and_dull_and_the_rest_of_the_battle_is_as_before()
    {
        var ork = MusouAmbienceRules.SpecOf(MusouAmbienceKind.OrkMob);
        Assert.True(MusouAudioRules.IsFarOrk(ork.Kind));
        Assert.True(ork.Near >= 55f && ork.Far >= 120f);
        Assert.Equal((0f, 0f), (ork.GainDb, ork.Lpf));   // far through the original events' own curves only (fifth follow-up)
        var bolter = MusouAmbienceRules.SpecOf(MusouAmbienceKind.Bolter);
        Assert.Equal((40f, 100f, -4f, 10f), (bolter.Near, bolter.Far, bolter.GainDb, bolter.Lpf));
    }

    [Fact]
    public void The_bolter_hit_is_a_smack_then_an_explosion()
    {
        Assert.True(MusouAudioRules.BlastDelaySeconds > 0f);
        Assert.True(MusouAudioRules.DeepPitchCents < 0f);
    }
    [Theory]
    [InlineData("The minions of Chaos are here!", true)]
    [InlineData("Warp daemons ahead.", true)]
    [InlineData("Behold -- traitors in the flesh.", true)]
    [InlineData("Die you bloody heretic!", true)]
    [InlineData("The DEMON is loose", true)]
    [InlineData("Greenskins. Make ready!", false)]
    [InlineData("The Ork horde is upon us!", false)]
    [InlineData("Aye.", false)]
    [InlineData("Backwarped?", false)]   // a word that merely contains a keyword is not about it
    [InlineData("", false)]
    public void Bark_text_about_chaos_the_warp_or_daemons_is_found(string text, bool offTheme) => Assert.Equal(offTheme, MusouAudioRules.IsOffThemeLine(text));

    [Fact]
    public void The_bark_pool_keeps_orks_and_generic_lines_and_may_end_empty()
    {
        var lines = new[] { (1, "Greenskins."), (2, "The forces of Chaos."), (3, "Aye."), (4, (string?)null) };
        Assert.Equal([1, 3, 4], MusouAudioRules.OnTheme(lines, l => l.Item2).Select(l => l.Item1));   // unknown text stays
        Assert.Empty(MusouAudioRules.OnTheme(new[] { "Warp daemons ahead." }, t => t));
    }

    [Fact]
    public void The_smack_is_sixty_percent_of_its_last_level_and_falls_off_twenty_percent_more()
    {
        Assert.InRange(MusouAudioRules.SmackGainDb, -2.46f, -2.42f);   // +2 dB x 0.6 linear
        Assert.Equal(1.2f, MusouAudioRules.SmackFalloffScale);
        Assert.True(MusouAudioRules.SmackPitchCents < 0f && MusouAudioRules.SmackLpf > 0f);
    }

    [Fact]
    public void Melee_hit_sounds_route_like_Classic_PvP_no_hook_no_flag_no_bus()
    {
        // owner 2026-11-09: every melee on-hit sound plays as the original data defines it, through the path Classic PvP uses; Musou adds nothing to it.
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        string root = d!.FullName;
        string hit = File.ReadAllText(Path.Combine(root, "client", "NetworkView.MeleeHitSound.cs"));
        Assert.DoesNotContain("Extras" + "MeleeHitEmitter", hit);
        Assert.Contains("new SoundEmitter { Position = at, Label = $\"melee hit on {target}\", OneShot = true }, LoudnessArg.MeleeHit, armoured)", hit);
        string sound = File.ReadAllText(Path.Combine(root, "client", "SoundSystem.cs"));
        foreach (var gone in new[] { "public bool Tail;", "public bool NoSteal;", "SmTailRev", "SmHitTail" }) Assert.DoesNotContain(gone, sound);
    }

    [Fact]
    public void The_hit_pair_is_half_the_first_passes_level_and_the_own_gun_is_heavier_than_others()
    {
        Assert.Equal(-15f, MusouAudioRules.BlastGainDb);   // was -9
        Assert.True(MusouAudioRules.OwnDeepGainDb > MusouAudioRules.DeepGainDb);
        Assert.True(MusouAudioRules.OwnThumpGainDb > MusouAudioRules.ThumpGainDb);
        Assert.True(MusouAudioRules.OwnSubPitchCents < MusouAudioRules.DeepPitchCents);
    }

    [Fact]
    public void Own_bark_waits_two_seconds_after_a_melee_attack() => Assert.Equal(2f, MusouAudioRules.OwnBarkMeleeQuietSeconds);
}
