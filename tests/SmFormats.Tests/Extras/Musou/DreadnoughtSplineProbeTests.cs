using System.Numerics;
using SmContent;
using SmContent.Extras.Musou;
using SmFormats.Havok;
using Xunit;
using Xunit.Abstractions;

namespace SmFormats.Tests;

/// <summary>2026-11-17 review: the 160 to 180 degree one-frame turn of RIG_LHand in DN_MEL_XATK_02 was not a sampler sign flip: sampled at 1/60 s the curves never turn more than about 45 degrees (install test; skipped without one).</summary>
public class DreadnoughtSplineProbeTests(ITestOutputHelper o)
{
    [Fact]
    public void The_arm_clips_rotation_curves_are_smooth_no_flipped_control_points_the_twist_is_authored()
    {
        var game = GameInstall.TryLocate()?.Dir;
        if (game is null) return;
        var rig = CharacterModel.LoadRig(game, DreadnoughtRig.Profile);
        foreach (var clipName in new[] { "DN_MEL_XATK_02_3.5m", "DN_MEL_XATK_01_3.5m" })
        {
            var clip = rig.LoadAnimation(clipName);
            o.WriteLine($"{clipName}: duration {clip.Animation.Duration:F3}, tracks {clip.Animation.TrackCount}");
            foreach (var bone in new[] { "RIG_LHand", "RIG_Hose1" })
            {
                int bi = rig.Skeleton.BoneNames.ToList().IndexOf(bone);
                int t = Array.IndexOf(clip.TrackToBone, (short)bi);
                if (t < 0) { o.WriteLine($"  {bone}: no track"); continue; }
                Quaternion prev = default; bool have = false; float worst = 0f;
                for (float time = 0.30f; time <= 1.0f; time += 1f / 60f)
                {
                    var raw = clip.Animation.SampleRaw(time, t);
                    var buf = new HavokTransform[clip.Animation.TrackCount];
                    clip.Animation.SampleInto(time, buf);
                    var q = buf[t].Rotation;
                    float turn = have ? 2f * MathF.Acos(Math.Min(1f, MathF.Abs(Quaternion.Dot(prev, q)))) * 180f / MathF.PI : 0f;
                    worst = MathF.Max(worst, turn);
                    float len = MathF.Sqrt(raw.X * raw.X + raw.Y * raw.Y + raw.Z * raw.Z + raw.W * raw.W);
                    if (turn > 40f || len < 0.6f) o.WriteLine($"  {bone} t {time:F3}: raw length {len:F3}, turn {turn:F1} deg");
                    prev = q; have = true;
                }
                o.WriteLine($"  {bone}: worst turn per 1/60 s {worst:F1} deg");
                Assert.True(worst < 60f, $"{clipName} {bone}: a turn of {worst:F0} degrees in 1/60 s of clip is a sampler flip, not the authored twist (about 29)");
            }
        }
    }
}
