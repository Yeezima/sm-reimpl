using SmContent;
using Xunit;

namespace SmFormats.Tests;

/// <summary>sound/mix-and-reloads: the mix pass's values and the reload sounds' single source (no install needed).</summary>
public class SoundMixPassTests
{
    [Fact]
    public void Fire_and_hit_offsets_are_louder_and_hits_are_high_passed()
    {
        Assert.Equal(SoundMixPass.FireGainDb, SoundMixPass.Fire(false).GainDb);
        Assert.False(SoundMixPass.Fire(false).HighPass);
        var hit = SoundMixPass.Hit(false);
        Assert.True(hit.GainDb > 0f && hit.HighPass);
        Assert.True(SoundMixPass.Fire(true).IsNone && SoundMixPass.Hit(true).IsNone && SoundMixPass.JumpTail(true).IsNone);   // --sound-mix-classic
    }

    [Fact]
    public void An_offset_sum_keeps_the_high_pass_and_the_deepest_tail()
    {
        var sum = new VoiceOffset(0f, -400f, 40f).Plus(SoundMixPass.Hit(false));   // the deeper bolter impact, then the pass
        Assert.True(sum.HighPass);
        Assert.Equal(-400f, sum.PitchCents);
        Assert.Equal(SoundMixPass.HitGainDb, sum.GainDb);
        Assert.Equal(-15f, new VoiceOffset(TailDb: -15f).Plus(new VoiceOffset(TailDb: -6f)).TailDb);
        Assert.False(VoiceOffset.None.Plus(VoiceOffset.None).HighPass);
    }

    [Fact]
    public void The_master_is_ten_percent_up()
    {
        Assert.Equal(1.1f, MathF.Pow(10f, SoundMixPass.MasterBusDb(1f, false) / 20f), 3);
        Assert.Equal(0f, SoundMixPass.MasterBusDb(1f, true), 3);
        Assert.Equal(-80f, SoundMixPass.MasterBusDb(0f, false));   // muted stays muted
        Assert.Equal(1.1f * 0.5f, MathF.Pow(10f, SoundMixPass.MasterBusDb(0.5f, false) / 20f), 3);
    }

    [Fact]
    public void The_jump_pack_tail_keeps_the_first_burst_and_fades_after_it()
    {
        float tail = SoundMixPass.JumpTailDb;
        Assert.Equal(0f, SoundMixPass.TailGainDb(tail, 0.0));
        Assert.Equal(0f, SoundMixPass.TailGainDb(tail, SoundMixPass.JumpTailHoldSeconds));
        Assert.Equal(tail / 2f, SoundMixPass.TailGainDb(tail, SoundMixPass.JumpTailHoldSeconds + SoundMixPass.JumpTailFadeSeconds / 2), 3);
        Assert.Equal(tail, SoundMixPass.TailGainDb(tail, 3.0));
        Assert.Equal(0f, SoundMixPass.TailGainDb(0f, 3.0));   // a voice without the fade
    }

    [Theory]
    [InlineData("SM_RNG_Bolter_Reload_UB_S_01")]
    [InlineData("SM_RNG_StalkerBolter_Reload_UB_S_01")]
    [InlineData("SM_1HR_RNG_BoltPistol_Reload_UB_S_01")]
    [InlineData("BOY_2HR_RNG_Reload_S_01")]
    public void A_reload_clip_posts_no_tag_sounds_from_the_animation(string clip)
    {
        Assert.True(SoundTriggers.IsReloadClip(clip));
        Assert.False(SoundTriggers.ClipTagsPostSounds(clip));
    }

    [Theory]
    [InlineData("SM_MEL_Combo_A_01")]
    [InlineData("SM_2HR_RNG_GrenadeToss")]
    public void Other_clips_still_post_their_tag_sounds(string clip) => Assert.True(SoundTriggers.ClipTagsPostSounds(clip));

    // The bolter's reload tags (fractions of the reload, as in fix/camera-zoom-misc's notes: remove clip, load clip, pull bolt, release bolt), over a 2.2 s reload.
    static readonly (string, float)[] BolterTags = [("remove", 0f), ("load", 0.214f), ("pull", 0.543f), ("release", 0.614f)];

    [Fact]
    public void A_fresh_reload_schedules_every_sound_once_at_its_time()
    {
        var due = ReloadSoundGuard.Remaining(BolterTags, 2.2f, 0f, 0.05);
        Assert.Equal(new[] { "remove", "load", "pull", "release" }, due.Select(d => d.Tag));
        Assert.Equal(0.214 * 2.2, due[1].Delay, 3);
    }

    [Fact]
    public void A_reload_picked_up_again_plays_only_what_is_left()
    {
        // A prediction correction cancelled the reload and the next frame sees it again 1.0 s in: the clip and load sounds already played, only the bolt is left.
        var due = ReloadSoundGuard.Remaining(BolterTags, 2.2f, 1.0f, 0.05);
        Assert.Equal(new[] { "pull", "release" }, due.Select(d => d.Tag));
        Assert.Equal(0.543 * 2.2 - 1.0, due[0].Delay, 3);
    }

    [Fact]
    public void The_whole_reload_sequence_posts_each_sound_once()
    {
        // Simulated frames of one reload with a misprediction blip at 0.6 s (the reload left drops to 0 for one frame and comes back): every tag is posted exactly once.
        var guard = new ReloadSoundGuard();
        var posted = new List<string>();
        var pending = new List<(double Due, string Tag, int Instance)>();
        const float length = 2.2f, tick = 1f / 30f;
        float prevLeft = 0f;
        for (int frame = 0; frame <= 90; frame++)
        {
            double now = frame * tick;
            float left = frame == 18 ? 0f : Math.Max(0f, length - (float)now);   // blip
            if (prevLeft > 0f && left <= 0f && prevLeft > tick * 1.5f) guard.Cancel(1);
            if (left > 0f && prevLeft <= 0f && guard.Start(1, now, length) is int instance)
                foreach (var (tag, delay) in ReloadSoundGuard.Remaining(BolterTags, length, length - left, tick * 1.5))
                    pending.Add((now + delay, tag, instance));
            foreach (var p in pending.Where(p => p.Due <= now + 1e-9).ToList())
            {
                pending.Remove(p);
                if (left > 0f && guard.Current(1, p.Instance)) posted.Add(p.Tag);
            }
            prevLeft = left;
        }
        Assert.Equal(new[] { "remove", "load", "pull", "release" }, posted);
    }
}
