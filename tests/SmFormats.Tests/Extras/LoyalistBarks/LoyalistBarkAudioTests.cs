using NVorbis;
using SmContent.Extras.LoyalistBarks;

namespace SmFormats.Tests.Extras.LoyalistBarks;

/// <summary>
/// Added content "loyalist barks": every committed take (the owner's own ElevenLabs voice pack) decodes as mono Vorbis at the manifest's rate, lasts
/// what the manifest says, and leaves headroom (the pack's helmet filter is baked in and normalised toward -16 LUFS / -1.5 dBTP; nothing filters it again).
/// </summary>
public class LoyalistBarkAudioTests
{
    static string Assets()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx"), "client", "Extras", "LoyalistBarks", "assets");
    }

    [Fact]
    public void Every_take_decodes_mono_at_the_manifest_rate_and_length_with_headroom()
    {
        var pack = LoyalistBarkPack.Parse(File.ReadAllText(Path.Combine(Assets(), "loyalist-barks.json")));
        Assert.Equal(32, pack.Clips.Count);
        foreach (var clip in pack.Clips)
        {
            using var reader = new VorbisReader(Path.Combine(Assets(), clip.File));
            Assert.Equal(1, reader.Channels);
            Assert.Equal(pack.SampleRate, reader.SampleRate);
            var buffer = new float[4096];
            long samples = 0; float peak = 0;
            int n;
            while ((n = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                samples += n;
                for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
            }
            double seconds = samples / (double)reader.SampleRate;
            Assert.InRange(seconds, clip.Seconds - 0.06, clip.Seconds + 0.06);
            Assert.InRange(20 * Math.Log10(peak), -20.0, -0.5);   // audible, not clipping
        }
    }
}
