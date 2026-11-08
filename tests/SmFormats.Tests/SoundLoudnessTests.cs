using SmContent;
using SmFormats.Audio;
using Xunit;

namespace SmFormats.Tests;

/// <summary>sound/louder-farther (owner, 2026-11-08): the loudness rule on top of the data's mix (synthetic voices, no install).</summary>
public class SoundLoudnessTests
{
    /// <summary>A positioned voice at -20 dB with a dry volume curve 0 dB at 0 m, -20 at 10 m, -60 at 40 m, and a low-pass curve 0 at 0 m, 40 at 40 m.</summary>
    static SoundVoice Voice()
    {
        var dry = new AttenuationCurve(2, [new CurvePoint(0f, 0f, 4), new CurvePoint(10f, -20f, 4), new CurvePoint(40f, -60f, 4)]);
        var lpf = new AttenuationCurve(0, [new CurvePoint(0f, 0f, 4), new CurvePoint(40f, 40f, 4)]);
        var att = new WwiseAttenuation(1, false, [0, -1, -1, 1, -1], [dry, lpf]);
        return new SoundVoice(1, 2, 0f, -20f, 0f, 1, true, att, 50, []);
    }

    [Fact]
    public void The_default_rule_is_pinned()
    {
        Assert.Equal(9f, SoundLoudness.DefaultGainDb);
        Assert.Equal(2.5f, SoundLoudness.DefaultRangeScale);
        Assert.Equal(SoundLoudness.Default, SoundLoudness.FromArgs([]));
    }

    [Fact]
    public void The_original_rule_is_the_datas_mix()
    {
        var v = Voice();
        foreach (float d in new[] { 0f, 5f, 15f, 30f, 60f })
            Assert.Equal(VoiceMixer.Evaluate(v, null, 1, 0, d, null), SoundLoudness.Original.Evaluate(v, null, 1, 0, d, null));
    }

    [Theory]
    [InlineData(5f, -15f, 2f)]        // the curves read at 2 m: -4 dB; -20 - 4 + 9
    [InlineData(15f, -23f, 6f)]       // at 6 m: -12 dB
    [InlineData(30f, -33.67f, 12f)]   // at 12 m: -22.67 dB
    [InlineData(60f, -49.67f, 24f)]   // at 24 m: -38.67 dB (the data: past the curve's end)
    public void The_default_rule_is_louder_and_reads_the_curves_nearer(float distance, float expectedDb, float expectedLpf)
    {
        var v = Voice();
        var before = VoiceMixer.Evaluate(v, null, 1, 0, distance, null);
        var after = SoundLoudness.Default.Evaluate(v, null, 1, 0, distance, null);
        Assert.InRange(after.VolumeDb, expectedDb - 0.4f, expectedDb + 0.4f);
        Assert.InRange(after.Lpf, expectedLpf - 0.01f, expectedLpf + 0.01f);
        Assert.True(after.VolumeDb - before.VolumeDb >= SoundLoudness.DefaultGainDb);   // never quieter than the gain alone
    }

    [Fact]
    public void The_gain_grows_with_distance()
    {
        var v = Voice();
        float Boost(float d) => SoundLoudness.Default.Evaluate(v, null, 1, 0, d, null).VolumeDb - VoiceMixer.Evaluate(v, null, 1, 0, d, null).VolumeDb;
        Assert.True(Boost(5f) < Boost(15f) && Boost(15f) < Boost(30f));
    }

    [Fact]
    public void A_voice_past_the_curves_end_is_heard_within_the_scaled_range()
    {
        var v = Voice();
        var rule = SoundLoudness.Default;
        // 60 m is past the curve's end (40 m) for the data, inside it for the rule (24 m).
        Assert.False(VoiceAudibility.IsAudible(true, true, v.Attenuation!.MaxDistance, 60f, VoiceMixer.Evaluate(v, null, 1, 0, 60f, null).VolumeDb, 0f, 1, false, false));
        Assert.True(VoiceAudibility.IsAudible(true, true, v.Attenuation.MaxDistance, rule.CurveDistance(60f), rule.Evaluate(v, null, 1, 0, 60f, null).VolumeDb, 0f, 1, false, false));
    }

    [Fact]
    public void Non_positioned_voices_get_the_gain_only()
    {
        var v = Voice() with { Is3D = false };
        Assert.Equal(-20f + SoundLoudness.DefaultGainDb, SoundLoudness.Default.Evaluate(v, null, 1, 0, null, null).VolumeDb, 3);
    }

    [Fact]
    public void Arguments_set_or_restore_the_rule()
    {
        Assert.Equal(SoundLoudness.Original, SoundLoudness.FromArgs(["--loudness-original", "--loudness-gain", "12"]));
        Assert.Equal(new SoundLoudness(12f, 3f), SoundLoudness.FromArgs(["--loudness-gain", "12", "--loudness-range", "3"]));
        var errors = new List<string>();
        Assert.Equal(SoundLoudness.Default, SoundLoudness.FromArgs(["--loudness-gain", "loud", "--loudness-range", "0"], errors.Add));
        Assert.Equal(2, errors.Count);
        Assert.Equal(SoundLoudness.Default, SoundLoudness.FromArgs(["--loudness-range"], errors.Add));   // the flag last: no value
    }
}
