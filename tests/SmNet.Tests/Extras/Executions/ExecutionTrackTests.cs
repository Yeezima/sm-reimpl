using System.Numerics;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X3: <see cref="ExecutionTrack"/>, the one pose function the server pin, the prediction pin and the drawn pose share.</summary>
public class ExecutionTrackTests
{
    const int Rate = 30;

    static ExecutionRun Run(int slideTicks = 4, float distance = 1.3f, float yaw = 0f) =>
        new(1, ExecutionTargetKind.Player, 2, 0, distance, new Vector3(0f, -0.5f, 0f), new Vector3(0f, 0f, 0f), yaw, slideTicks, 100, false);

    [Fact]
    public void SlideLerpsByTickThenClipStartsAtOrigin()
    {
        var row = ExecutionTestKit.Row();
        var run = Run();
        var p0 = ExecutionTrack.Pose(run, row, 100, Rate);
        Assert.True(p0.InSlide);
        Assert.Equal(run.SlideFrom, p0.Feet);
        var p2 = ExecutionTrack.Pose(run, row, 102, Rate);
        Assert.Equal(-0.25f, p2.Feet.Y, 5);   // halfway, linear by tick
        var p4 = ExecutionTrack.Pose(run, row, 104, Rate);
        Assert.False(p4.InSlide);
        Assert.Equal(0f, p4.ClipSeconds);
        Assert.Equal(run.Origin, p4.Feet);
        // then the root path from the origin
        var late = ExecutionTrack.Pose(run, row, 104 + 30, Rate);
        Assert.Equal(1f, late.ClipSeconds, 5);
        Assert.Equal(0.2f, late.Feet.Y, 4);   // half of the reference clip's 0.4 m at half its 2 s
        Assert.Equal(104u, ExecutionTrack.ClipStartTick(run));
    }

    [Fact]
    public void RootPathFollowsBlendAtDistance()
    {
        var row = ExecutionTestKit.Row(gap: 1.3f, near: 1.0f);   // reference walks 0.4 m, the near variant 0.1 m
        var mid = Run(slideTicks: 0, distance: 1.15f);
        var end = ExecutionTrack.Pose(mid, row, 100 + 60, Rate);   // clip end
        Assert.Equal(0.25f, end.Feet.Y, 4);   // the blend halfway: 0.25 m, not either variant's own distance
        var at = row.AttackerRootAt(1.15f);
        Assert.Equal(at[^1].Y, end.Feet.Y, 5);
        // turned: the root path is placed at the yaw (yaw 0 faces +Y; pi/2 turns it to -X)
        var turned = ExecutionTrack.Pose(mid with { Yaw = MathF.PI / 2f }, row, 160, Rate);
        Assert.Equal(-0.25f, turned.Feet.X, 4);
        Assert.Equal(0f, turned.Feet.Y, 4);
    }

    [Fact]
    public void RootTurnAddsToYaw()
    {
        var row = ExecutionTestKit.Row() with { AttackerVariants = [], AttackerRoot = ExecutionTestKit.Straight(0f, turn: 1f) };
        var p = ExecutionTrack.Pose(Run(slideTicks: 0, yaw: 0.5f), row, 160, Rate);
        Assert.Equal(1.5f, p.Yaw, 4);
    }

    [Fact]
    public void EventTicksRoundToNearest()
    {
        var row = ExecutionTestKit.Row(kill: 1.01f);   // 30.3 ticks: 30
        var run = Run(slideTicks: 3);
        Assert.Equal(103u + 30u, ExecutionTrack.EventTick(run, row, ExecutionEventKind.Kill, Rate));
        var later = ExecutionTestKit.Row(kill: 1.02f);   // 30.6 ticks: 31
        Assert.Equal(103u + 31u, ExecutionTrack.EventTick(run, later, ExecutionEventKind.Kill, Rate));
        Assert.Equal(103u + 3u, ExecutionTrack.EventTick(run, row, ExecutionEventKind.Sync, Rate));   // 0.1 s
    }

    [Fact]
    public void EndTickIsEndEventElseLongerClip()
    {
        var run = Run(slideTicks: 2);
        Assert.Equal(102u + 54u, ExecutionTrack.EndTick(run, ExecutionTestKit.Row(end: 1.8f), Rate));
        var noEnd = ExecutionTestKit.Row(end: null) with { VictimSeconds = 2.5f };
        Assert.Equal(102u + 75u, ExecutionTrack.EndTick(run, noEnd, Rate));
        // never before the kill
        Assert.Equal(102u + 45u, ExecutionTrack.EndTick(run, ExecutionTestKit.Row(kill: 1.5f, end: 1.2f), Rate));
    }

    [Fact]
    public void PoseIsDeterministicAcrossCalls()
    {
        var row = ExecutionTestKit.Row();
        var run = ExecutionTrack.Quantize(Run(distance: 1.2345f, yaw: 2.2f));
        for (uint t = 95; t < 180; t++)
        {
            var a = ExecutionTrack.Pose(run, row, t, Rate);
            var b = ExecutionTrack.Pose(run, row with { }, t, Rate);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void QuantizeIsIdempotentAndMatchesTheWire()
    {
        var run = ExecutionTrack.Quantize(Run(distance: 1.2346f, yaw: -2.2f) with { SlideFrom = new Vector3(0.1234f, -0.9876f, 0.0004f) });
        Assert.Equal(run, ExecutionTrack.Quantize(run));
        Assert.Equal(1.235f, run.Distance, 5);
        Assert.InRange(run.Yaw, 0f, 2f * MathF.PI);
    }
}
