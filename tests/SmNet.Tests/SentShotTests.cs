using System.Numerics;
using SmClient.Bullets;
using SmSim;

namespace SmNet.Tests;

/// <summary>feel/bullets: the own shot's ray comes from the input sent for its tick; the rule for other players' rays agrees with the real send path.</summary>
public class SentShotTests
{
    static SelfEvents Fired(bool fired = true) => new(fired, 0, 0, false);

    [Fact]
    public void RingKeepsTheLast64InputsAsTheWireCarriesThem()
    {
        var ring = new SentInputRing();
        var input = new PlayerInput(100, 0, 1, 0.5f, InputButtons.Fire, 0.1f).WithShot(new Vector3(0.7f, 0.2f, 0f), Vector3.UnitY, Vector3.Zero);
        ring.Note(input);
        Assert.True(ring.TryGet(100, out var got));
        Assert.Equal(Protocol.Quantize(input.Sanitized()), got);
        Assert.False(ring.TryGet(101, out _));
        ring.Note(new PlayerInput(164, 0, 0, 0, InputButtons.None, 0));   // 64 ticks later takes the slot
        Assert.False(ring.TryGet(100, out _));
        Assert.True(ring.TryGet(164, out _));
    }

    [Fact]
    public void TheNthOfSeveralShotsOfAFrameBelongsToTheNthOfTheLastFiredEvents()
    {
        var events = new List<(uint Tick, SelfEvents Events)> { (10, Fired()), (11, Fired(false)), (12, Fired()), (13, Fired()) };
        Assert.True(ShotTicks.TryPick(events, 0, 1, out var tick)); Assert.Equal(13u, tick);   // one shot: the latest
        Assert.True(ShotTicks.TryPick(events, 0, 3, out tick)); Assert.Equal(10u, tick);
        Assert.True(ShotTicks.TryPick(events, 1, 3, out tick)); Assert.Equal(12u, tick);
        Assert.True(ShotTicks.TryPick(events, 2, 3, out tick)); Assert.Equal(13u, tick);
        Assert.True(ShotTicks.TryPick(events, 0, 5, out tick)); Assert.Equal(10u, tick);   // more shots than fired events: clamped
        Assert.False(ShotTicks.TryPick([(5, Fired(false))], 0, 1, out _));
        Assert.False(ShotTicks.TryPick([], 0, 1, out _));
    }

    /// <summary>
    /// The shooter's client sends its camera ray (<c>CenterRayTarget</c>: start = the ray's point nearest the eye, <c>t0 = max(0, dot(eye - camera, view))</c>, direction = view;
    /// <c>PlayerInput.WithShot</c>); the server's <c>Combat.ShotOrigin</c> / <c>ShotDirection</c> turn the quantised input into the damaging ray. An observer's rebuilt origin
    /// (<c>BulletPaths.OriginOnCameraRay</c>) and direction (<c>DirectionTo</c> to the shot's end) must be that ray.
    /// </summary>
    [Theory]
    [InlineData(0.0f, 0.0f, 0.64f, -3.38f, 2.38f)]      // the rig's exploration place, looking level
    [InlineData(1.9f, -0.25f, 0.64f, -3.38f, 2.9f)]     // turned, looking down
    [InlineData(-2.6f, 0.4f, 0.64f, -1.78f, 2.16f)]     // aim mode's place, looking up
    [InlineData(0.7f, 0.0f, 0.2f, -6.0f, 3.0f)]         // a far camera: the offset is beyond the server's bound
    public void TheRebuiltRayIsTheRayTheServerResolves(float yaw, float pitch, float camX, float camBack, float camUp)
    {
        var c = new SimConfig();
        var feet = new Vector3(120f, -40f, 3f);
        var state = new PlayerState(1, feet, Vector3.Zero, yaw, true);
        var eye = Combat.EyePosition(state, c);
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        var camera = feet + new Vector3(camX * cos - camBack * sin, camX * sin + camBack * cos, camUp);
        var view = Combat.AimDirection(yaw, pitch);

        // The send path.
        float t0 = MathF.Max(0f, Vector3.Dot(eye - camera, view));
        var start = camera + view * t0;
        var sent = Protocol.Quantize(new PlayerInput(5, 0, 0, yaw, InputButtons.Fire, pitch).WithShot(start, view, eye).Sanitized());
        var serverOrigin = Combat.ShotOrigin(state, sent, c);
        var serverDir = Combat.ShotDirection(sent, c);
        var end = serverOrigin + serverDir * 40f;

        // The observer's rebuild.
        var origin = BulletPaths.OriginOnCameraRay(camera, view, eye, c.MaxShotOriginOffset);
        var dir = BulletPaths.DirectionTo(origin, end, view);
        Assert.True(Vector3.Distance(origin, serverOrigin) < 0.02f, $"origin {origin} vs {serverOrigin}");   // the wire's centimetre steps
        Assert.True(BulletPaths.AngleDegrees(dir, serverDir) < 0.05f);
        Assert.True(Vector3.Distance(origin + dir * 15f, serverOrigin + serverDir * 15f) < 0.06f);
    }
}
