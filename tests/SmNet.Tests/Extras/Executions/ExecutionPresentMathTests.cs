using System.Numerics;
using SmClient;
using SmSim;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X5: the presentation's pure maths (client/Extras/Executions/ExecutionPresentMath.cs) on synthetic rows: the blend, the clocks, the victim's place, the corpse timing.</summary>
public class ExecutionPresentMathTests
{
    /// <summary>A synthetic three-variant row (1 / 3.5 / 7 m names; the reference clip at the authored gap 3.6 m), straight root paths of 0, 2.6 and 6.1 m.</summary>
    static ExecutionRow Row() => new ExecutionRow("T1.chainsword", "*", ExecutionWeapons.Chainsword, ExecutionTargets.OrkBoy, "ATK_3.5m", "VIC", 2.0f, 2.5f,
        [new ExecutionEvent(ExecutionEventKind.Kill, 1.2f), new ExecutionEvent(ExecutionEventKind.End, 2.2f)], new Vector3(0.2f, 3.6f, 0f), MathF.PI, "g", "s")
    {
        AttackerVariants =
        [
            new ExecutionVariant("ATK_1m", 1.0f, [Vector4.Zero, new Vector4(0f, 0f, 0f, 0f)]),
            new ExecutionVariant("ATK_3.5m", 3.5f, [Vector4.Zero, new Vector4(0f, 2.6f, 0f, 0f)]),
            new ExecutionVariant("ATK_7m", 7.0f, [Vector4.Zero, new Vector4(0f, 6.1f, 0f, 0.5f)]),
        ],
        VictimRoot = [Vector4.Zero, new Vector4(0f, -0.5f, 0f, 0f)],
    };

    static ExecutionRun Run(float distance, int slide = 0) =>
        new(1, ExecutionTargetKind.Enemy, 5, 0, distance, new Vector3(-1f, 0f, 0f), new Vector3(10f, 20f, 1f), MathF.PI / 2f, slide, 100, false);

    [Fact]
    public void BlendWeightsFollowRowBlendNotVariantDistance()
    {
        var row = Row();
        // Between the 1 m clip and the reference (whose distance is the authored 3.6 m, not its 3.5 m name).
        var (near, far, w) = ExecutionPresentMath.AttackerClips(row, 2.3f);
        Assert.Equal("ATK_1m", near);
        Assert.Equal("ATK_3.5m", far);
        Assert.Equal((2.3f - 1f) / (3.6f - 1f), w, 4);
        var (n2, f2, _) = ExecutionPresentMath.AttackerClips(row, 3.6f);
        Assert.Equal("ATK_3.5m", n2);
        Assert.Null(f2);
        var (n3, f3, _) = ExecutionPresentMath.AttackerClips(row, 9f);   // clamped to the band's end
        Assert.Equal("ATK_7m", n3);
        Assert.Null(f3);
        var single = Row() with { AttackerVariants = [] };
        Assert.Equal(("ATK_3.5m", (string?)null, 0f), ExecutionPresentMath.AttackerClips(single, 2f));
    }

    [Fact]
    public void DistanceDefaultsToTheAuthoredGap()
    {
        Assert.Equal(3.6f, ExecutionPresentMath.DistanceOf(Run(float.NaN), Row()));
        Assert.Equal(5f, ExecutionPresentMath.DistanceOf(Run(5f), Row()));
    }

    [Fact]
    public void ClipTimeFromRenderTickStartsAfterTheSlide()
    {
        var run = Run(3.6f, slide: 5);
        Assert.Equal(0f, ExecutionPresentMath.ClipSeconds(run, 100, 30));
        Assert.Equal(0f, ExecutionPresentMath.ClipSeconds(run, 104.5, 30));
        Assert.Equal(0f, ExecutionPresentMath.ClipSeconds(run, 105, 30));
        Assert.Equal(1f, ExecutionPresentMath.ClipSeconds(run, 135, 30), 5);
        Assert.Equal(0.5f / 30f, ExecutionPresentMath.ClipSeconds(run, 105.5, 30), 5);
    }

    [Fact]
    public void PoseInterpolatesTheTrackBetweenTicks()
    {
        var row = Row();
        var run = Run(3.6f);
        var a = ExecutionTrack.Pose(run, row, 110, 30);
        var b = ExecutionTrack.Pose(run, row, 111, 30);
        var mid = ExecutionPresentMath.Pose(run, row, 110.5, 30);
        Assert.Equal(Vector3.Lerp(a.Feet, b.Feet, 0.5f).X, mid.Feet.X, 4);
        Assert.Equal(Vector3.Lerp(a.Feet, b.Feet, 0.5f).Y, mid.Feet.Y, 4);
        var whole = ExecutionPresentMath.Pose(run, row, 110, 30);
        Assert.Equal(a.Feet, whole.Feet);
        Assert.Equal(a.Yaw, whole.Yaw, 5);
    }

    [Fact]
    public void RawTimeAppliesTheSequencerSpeed()
    {
        // A row of 2.0 s whose raw clip is 2.4 s long (the sequencer plays it at 1.2): half the row is half the clip.
        Assert.Equal(1.2f, ExecutionPresentMath.RawTime(1.0f, 2.0f, 2.4f), 5);
        Assert.Equal(2.4f, ExecutionPresentMath.RawTime(5f, 2.0f, 2.4f), 5);
        Assert.Equal(0f, ExecutionPresentMath.RawTime(-1f, 2.0f, 2.4f));
        Assert.Equal(1.2f, ExecutionPresentMath.Speed(2.0f, 2.4f), 5);
        Assert.Equal(1f, ExecutionPresentMath.Speed(0f, 2.4f));
    }

    [Fact]
    public void VictimPlaceIsTheRowOffsetFromTheOrigin()
    {
        var row = Row();
        var run = Run(3.6f);
        var (feet, yaw) = ExecutionPresentMath.VictimPlace(run, row);
        var w = ExecutionAlign.ToWorld(new Vector2(0.2f, 3.6f), run.Yaw);
        Assert.Equal(10f + w.X, feet.X, 4);
        Assert.Equal(20f + w.Y, feet.Y, 4);
        Assert.Equal(1f, feet.Z);
        Assert.Equal(ExecutionAlign.Wrap(run.Yaw + MathF.PI), yaw, 5);
        Assert.Equal(0.5f, ExecutionPresentMath.VictimPlace(run, row, 0.5f).Feet.Z);
    }

    [Fact]
    public void HideWindowIsTheCrowdCorpseTiming()
    {
        Assert.Equal(11.5, ExecutionPresentMath.AfterEnd(true, 10f, 1.5f), 5);
        Assert.Equal(ExecutionClientState.EndedKeepSeconds, ExecutionPresentMath.AfterEnd(false, 10f, 1.5f));
        Assert.Equal(0f, ExecutionPresentMath.Sink(5, 10f, 1.5f, 1.2f));
        Assert.Equal(0.6f, ExecutionPresentMath.Sink(10.75, 10f, 1.5f, 1.2f), 4);
        Assert.Equal(1.2f, ExecutionPresentMath.Sink(20, 10f, 1.5f, 1.2f), 4);
    }

    [Fact]
    public void DeathPoseHeldForTheWindow()
    {
        var c = new SimConfig();
        float hold = ExecutionRules.DeathPoseHoldSeconds(c);
        Assert.Equal(0.8f, hold, 4);
        Assert.True(ExecutionPresentMath.DeathHeld(0, hold));
        Assert.True(ExecutionPresentMath.DeathHeld(0.79, hold));
        Assert.False(ExecutionPresentMath.DeathHeld(0.81, hold));
        Assert.False(ExecutionPresentMath.DeathHeld(-0.1, hold));
    }
}
