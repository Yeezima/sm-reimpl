using SmContent;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>musou/npc-voices-only (owner, 2026-11-12): the ambient voices are off unless restored; NPC and player voices carry farther.</summary>
public class MusouVoiceRulesTests
{
    [Fact]
    public void Ambient_voices_are_off_unless_the_restore_switch_is_given()
    {
        Assert.False(MusouVoiceRules.AmbientVoices([]));
        Assert.False(MusouVoiceRules.AmbientVoices(["--no-musou-battle-ambience"]));
        Assert.True(MusouVoiceRules.AmbientVoices(["--musou-ambient-voices"]));
    }

    [Fact]
    public void Only_the_ork_mob_of_the_far_battle_is_a_voice()
    {
        foreach (var kind in Enum.GetValues<MusouAmbienceKind>())
            Assert.Equal(kind == MusouAmbienceKind.OrkMob, MusouVoiceRules.IsAmbientVoice(kind));
    }

    [Fact]
    public void Reach_is_wider_by_default_and_the_test_aid_restores_one()
    {
        Assert.Equal(1.75f, MusouVoiceRules.ReachFor([]));
        Assert.Equal(1f, MusouVoiceRules.ReachFor(["--no-musou-voice-reach"]));
    }

    [Fact]
    public void Budget_lets_speech_groups_post_farther_by_the_reach_and_leaves_other_groups_alone()
    {
        var plain = new MusouSoundBudget();
        var wide = new MusouSoundBudget { VoiceReach = MusouVoiceRules.Reach };
        float bark = MusouSoundRules.LimitOf(MusouSoundGroup.Bark).Range;
        Assert.Equal(MusouSoundVerdict.TooFar, plain.Take(MusouSoundGroup.Bark, bark * 1.5f, 0, 0));
        Assert.Equal(MusouSoundVerdict.Post, wide.Take(MusouSoundGroup.Bark, bark * 1.5f, 0, 0));
        Assert.Equal(MusouSoundVerdict.TooFar, wide.Take(MusouSoundGroup.Bark, bark * 1.8f, 0, 0));
        float swing = MusouSoundRules.LimitOf(MusouSoundGroup.Swing).Range;
        Assert.Equal(MusouSoundVerdict.TooFar, wide.Take(MusouSoundGroup.Swing, swing * 1.5f, 0, 0));
    }

    [Fact]
    public void Budget_caps_do_not_grow_with_the_reach()
    {
        var wide = new MusouSoundBudget { VoiceReach = MusouVoiceRules.Reach };
        Assert.Equal(MusouSoundVerdict.VoiceCap, wide.Take(MusouSoundGroup.Bark, 5f, MusouSoundRules.MaxVoices, 0));
        Assert.Equal(MusouSoundVerdict.SpeechCap, wide.Take(MusouSoundGroup.Bark, 5f, 0, MusouSoundRules.MaxSpeech));
    }
}
