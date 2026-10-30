using SmContent;

namespace SmFormats.Tests;

/// <summary>fix/camera-zoom-misc (owner, 2026-10-30): one reload plays one sample once; the later tag keeps it. Synthetic tags and samples only.</summary>
public class ReloadSoundOnceTests
{
    static IReadOnlyCollection<uint> Samples(string tag) => tag switch
    {
        "remove" => [1u], "load" => [2u], "pull" => [3u], "release" => [2u],   // load and release resolve to the same sample (the stalker)
        "foleyA" => [1u, 4u], "foleyB" => [4u], "unknown" => [],
        _ => [],
    };

    [Fact]
    public void The_same_sample_in_two_differently_named_tags_plays_once_at_the_later_one()
    {
        var kept = SoundTriggers.OncePerSample([("remove", 0f), ("load", 0.214f), ("pull", 0.543f), ("release", 0.614f)], Samples);
        Assert.Equal(["remove", "pull", "release"], kept.Select(t => t.Tag));
        Assert.Equal(0.614f, kept.Single(t => t.Tag == "release").At);
    }

    [Fact]
    public void A_tag_the_clip_repeats_and_tags_with_other_or_unknown_samples_stay()
    {
        var kept = SoundTriggers.OncePerSample([("load", 0.283f), ("load", 0.4f), ("foleyA", 0.5f), ("foleyB", 0.6f), ("unknown", 0.7f), ("unknown", 0.8f)], Samples);
        Assert.Equal(6, kept.Count);
    }

    [Fact]
    public void The_stalker_bolter_keeps_only_its_last_reload_tag()
    {
        var tags = new (string Tag, float At)[] { ("remove", 0f), ("pull", 0.543f), ("release", 0.614f) };
        Assert.Equal(["release"], SoundTriggers.OnePostWhereSingle("sm_mp_ranged_sniper_rifle", tags).Select(t => t.Tag));
        Assert.Equal(["release"], SoundTriggers.OnePostWhereSingle("csm_mp_ranged_sniper_rifle", tags).Select(t => t.Tag));
        Assert.Equal(3, SoundTriggers.OnePostWhereSingle("sm_mp_ranged_bolter", tags).Count);
    }
}
