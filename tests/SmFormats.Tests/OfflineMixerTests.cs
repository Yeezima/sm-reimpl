using SmContent;

namespace SmFormats.Tests;

/// <summary>sound/original-melee-hits: the offline mixer behind <c>smtool audio-render</c>, on synthetic samples only (no original bytes).</summary>
public class OfflineMixerTests
{
    static float[] Ramp(int n, float scale = 1f) => Enumerable.Range(0, n).Select(i => i * scale).ToArray();

    [Fact]
    public void Gain_is_amplitude_decibels()
    {
        Assert.Equal(1f, OfflineMixer.Gain(0f), 5);
        Assert.Equal(2f, OfflineMixer.Gain(6.0206f), 3);
        Assert.Equal(0.5f, OfflineMixer.Gain(-6.0206f), 3);
        Assert.Equal(0.1f, OfflineMixer.Gain(-20f), 5);
    }

    [Fact]
    public void Pitch_ratio_is_cents_as_octaves()
    {
        Assert.Equal(1f, OfflineMixer.PitchRatio(0f), 5);
        Assert.Equal(2f, OfflineMixer.PitchRatio(1200f), 4);
        Assert.Equal(0.5f, OfflineMixer.PitchRatio(-1200f), 4);
    }

    [Fact]
    public void A_mono_track_at_the_output_rate_is_copied_to_both_channels_with_its_gain()
    {
        var t = new MixTrack([0.1f, 0.2f, 0.3f, 0.4f], 1, 1000, 0f, -20f, 0f);
        var mix = OfflineMixer.Mix([t], outRate: 1000, outChannels: 2);
        Assert.Equal(8, mix.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0.01f * (i + 1), mix[i * 2], 5);
            Assert.Equal(mix[i * 2], mix[i * 2 + 1]);
        }
    }

    [Fact]
    public void A_stereo_track_keeps_its_channels_and_averages_into_a_mono_mix()
    {
        var t = new MixTrack([1f, -1f, 0.5f, 0f], 2, 1000, 0f, 0f, 0f);
        var stereo = OfflineMixer.Mix([t], 1000, 2);
        Assert.Equal([1f, -1f, 0.5f, 0f], stereo);
        var mono = OfflineMixer.Mix([t], 1000, 1);
        Assert.Equal([0f, 0.25f], mono);
    }

    [Fact]
    public void Delay_shifts_the_start_and_lengthens_the_mix()
    {
        var t = new MixTrack([1f, 1f], 1, 1000, 0.003f, 0f, 0f);
        var mix = OfflineMixer.Mix([t], 1000, 1);
        Assert.Equal([0f, 0f, 0f, 1f, 1f], mix);
        Assert.Equal(5, OfflineMixer.FramesOf(t, 1000));
    }

    [Fact]
    public void Tail_adds_silence_after_the_last_track()
    {
        var t = new MixTrack([1f], 1, 1000, 0f, 0f, 0f);
        Assert.Equal(1 + 20, OfflineMixer.Mix([t], 1000, 1, tailSeconds: 0.02f).Length);
    }

    [Fact]
    public void Pitch_up_an_octave_halves_the_length_and_reads_every_second_sample()
    {
        var t = new MixTrack(Ramp(8), 1, 1000, 0f, 0f, 1200f);
        var mix = OfflineMixer.Mix([t], 1000, 1);
        Assert.Equal(4, mix.Length);
        Assert.Equal([0f, 2f, 4f, 6f], mix);
    }

    [Fact]
    public void Pitch_down_an_octave_doubles_the_length_and_interpolates_between_samples()
    {
        var t = new MixTrack([0f, 2f, 4f], 1, 1000, 0f, 0f, -1200f);
        var mix = OfflineMixer.Mix([t], 1000, 1);
        Assert.Equal(6, mix.Length);
        Assert.Equal([0f, 1f, 2f, 3f, 4f, 4f], mix);   // the last half step holds the final sample
    }

    [Fact]
    public void A_source_at_another_rate_is_resampled_to_the_output_rate()
    {
        var t = new MixTrack(Ramp(6), 1, 500, 0f, 0f, 0f);   // 500 Hz into 1000 Hz: twice the frames
        var mix = OfflineMixer.Mix([t], 1000, 1);
        Assert.Equal(12, mix.Length);
        Assert.Equal(0f, mix[0], 5);
        Assert.Equal(0.5f, mix[1], 5);
        Assert.Equal(1f, mix[2], 5);
        Assert.Equal(5f, mix[10], 5);
    }

    [Fact]
    public void Tracks_are_summed_and_the_longest_sets_the_length()
    {
        var a = new MixTrack([0.25f, 0.25f, 0.25f], 1, 1000, 0f, 0f, 0f);
        var b = new MixTrack([0.5f], 1, 1000, 0.001f, 0f, 0f);
        var mix = OfflineMixer.Mix([a, b], 1000, 1);
        Assert.Equal([0.25f, 0.75f, 0.25f], mix);
    }

    [Fact]
    public void Sum_clips_to_16_bit_and_the_peak_is_reported_before_clipping()
    {
        var a = new MixTrack([0.9f, -0.9f], 1, 1000, 0f, 0f, 0f);
        var mix = OfflineMixer.Mix([a, a], 1000, 1);
        Assert.Equal(1.8f, OfflineMixer.Peak(mix), 5);
        Assert.Equal([32767, -32767], OfflineMixer.ToPcm16(mix).Select(s => (int)s).ToArray());
        Assert.Equal([16384, 0], OfflineMixer.ToPcm16([0.5f, 0f]).Select(s => (int)s).ToArray());
    }

    [Fact]
    public void An_empty_mix_and_bad_arguments_are_safe()
    {
        Assert.Empty(OfflineMixer.Mix([], 1000, 2));
        Assert.Empty(OfflineMixer.Mix([new MixTrack([], 1, 1000, 0f, 0f, 0f)], 1000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => OfflineMixer.Mix([], 1000, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => OfflineMixer.Mix([], 0, 2));
        Assert.Equal(0f, OfflineMixer.Peak([]));
    }

    [Fact]
    public void Cutoff_follows_the_clients_mapping()
    {
        Assert.Equal(20000f, OfflineMixer.CutoffHz(0f), 1);
        Assert.Equal(150f, OfflineMixer.CutoffHz(100f), 1);
        Assert.Equal(MathF.Sqrt(20000f * 150f), OfflineMixer.CutoffHz(50f), 0);
    }

    [Fact]
    public void Low_pass_keeps_low_tones_and_cuts_high_ones()
    {
        int rate = 48000, n = 4800;
        float[] Sine(double hz) => Enumerable.Range(0, n).Select(i => (float)Math.Sin(2 * Math.PI * hz * i / rate)).ToArray();
        float Rms(float[] x) => MathF.Sqrt(x.Skip(n / 2).Sum(v => v * v) / (n / 2));
        float low = Rms(OfflineMixer.LowPass(Sine(100), 1, rate, 2000f)), high = Rms(OfflineMixer.LowPass(Sine(10000), 1, rate, 2000f));
        Assert.InRange(low, 0.69f, 0.72f);      // sine RMS 0.707, passed
        Assert.True(high < 0.05f, $"10 kHz through a 2 kHz two-pole filter should be down more than 20 dB, rms {high}");
        var same = Sine(100);
        Assert.Same(same, OfflineMixer.LowPass(same, 1, rate, 30000f));   // a cutoff above the band leaves it alone
    }

    [Fact]
    public void A_track_with_a_low_pass_value_is_filtered_in_the_mix()
    {
        int rate = 48000;
        var tone = Enumerable.Range(0, 4800).Select(i => (float)Math.Sin(2 * Math.PI * 10000 * i / rate)).ToArray();
        float Peak(float lpf) => OfflineMixer.Peak(OfflineMixer.Mix([new MixTrack(tone, 1, rate, 0f, 0f, 0f, lpf)], rate, 1));
        Assert.True(Peak(50f) < 0.2f * Peak(0f));   // value 50 = about 1.7 kHz
    }

    [Fact]
    public void Wav_bytes_have_a_canonical_header()
    {
        var wav = OfflineMixer.WavBytes([1, -2, 3, -4], 2, 48000);
        Assert.Equal(44 + 8, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal((short)2, BitConverter.ToInt16(wav, 22));
        Assert.Equal(48000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(48000 * 4, BitConverter.ToInt32(wav, 28));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(8, BitConverter.ToInt32(wav, 40));
        Assert.Equal((short)-2, BitConverter.ToInt16(wav, 46));
    }
}
