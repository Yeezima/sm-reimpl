using System.Numerics;
using SmClient.Bullets;

namespace SmNet.Tests;

/// <summary>feel/bullets: the visible bullet's path is the damaging ray past the join point (client/Bullets/BulletPath.cs, engine-free).</summary>
public class BulletPathTests
{
    static readonly Vector3 Origin = new(0, 0, 1.6f);
    static readonly Vector3 Forward = Vector3.UnitY;

    [Fact]
    public void BulletFromAnOffsetMuzzleJoinsTheRayAndStaysOnItToTheEnd()
    {
        var muzzle = new Vector3(0.5f, 0.7f, 1.3f);   // beside and below the ray, a little ahead
        var end = Origin + Forward * 40f;
        var join = BulletPaths.JoinPoint(muzzle, Origin, Forward, end);
        Assert.NotNull(join);
        // The join point is on the ray, ConvergeDistance past the muzzle's own distance along it.
        Assert.True(BulletPaths.DistanceToSegment(join!.Value, Origin, end) < 1e-4f);
        Assert.Equal(0.7f + BulletPaths.ConvergeDistance, Vector3.Dot(join.Value - Origin, Forward), 3);
        // Every ray point from the join on is on the path: the bullet is exactly where the damage went.
        for (float d = BulletPaths.ConvergeDistance + 0.7f; d <= 40f; d += 3.3f)
            Assert.True(BulletPaths.DistanceToPath(Origin + Forward * d, muzzle, join.Value, end) < 1e-4f, $"at {d} m");
        // The straight muzzle-to-end line of before is off the ray by the muzzle's offset at 5 m.
        Assert.True(BulletPaths.DistanceToSegment(Origin + Forward * 5f, muzzle, end) > 0.3f);
    }

    [Fact]
    public void MuzzleOnTheRayOrAnEndWithinTheConvergenceFliesStraight()
    {
        var end = Origin + Forward * 40f;
        Assert.Null(BulletPaths.JoinPoint(Origin + Forward * 0.6f, Origin, Forward, end));
        Assert.Null(BulletPaths.JoinPoint(new Vector3(0.5f, 0.7f, 1.3f), Origin, Forward, Origin + Forward * 3f));
        Assert.Null(BulletPaths.JoinPoint(new Vector3(0.5f, 0.7f, 1.3f), Origin, Vector3.Zero, end));
        Assert.Null(BulletPaths.JoinPoint(new Vector3(float.NaN, 0, 0), Origin, Forward, end));
    }

    [Fact]
    public void MuzzleBehindTheOriginStillJoinsAheadOfIt()
    {
        var muzzle = new Vector3(0.4f, -0.5f, 1.3f);
        var join = BulletPaths.JoinPoint(muzzle, Origin, Forward, Origin + Forward * 30f);
        Assert.NotNull(join);
        Assert.Equal(BulletPaths.ConvergeDistance, Vector3.Dot(join!.Value - Origin, Forward), 3);
    }

    [Fact]
    public void OriginOfAnOutsideShotIsTheCameraRayPointNearestTheEyeBoundedLikeTheServer()
    {
        var eye = new Vector3(0, 0, 1.6f);
        var camera = new Vector3(0.64f, -3.38f, 2.38f);   // the rig's exploration place (right, behind, up)
        var view = Vector3.Normalize(new Vector3(0, 1, -0.05f));
        var origin = BulletPaths.OriginOnCameraRay(camera, view, eye, 1.5f);
        // On the camera ray, at the point nearest the eye.
        Assert.True(Vector3.Cross(origin - camera, view).Length() < 1e-3f);
        Assert.True(Vector3.Dot(origin - eye, view) is > -1e-3f and < 1e-3f);
        // A short bound cuts the offset in its own direction.
        var cut = BulletPaths.OriginOnCameraRay(camera, view, eye, 0.5f);
        Assert.Equal(0.5f, Vector3.Distance(cut, eye), 3);
        Assert.Equal(eye, BulletPaths.OriginOnCameraRay(camera, Vector3.Zero, eye, 1.5f));
    }

    [Fact]
    public void RebuiltDirectionRunsFromTheOriginToTheEndAndFallsBackToTheView()
    {
        var d = BulletPaths.DirectionTo(Origin, Origin + new Vector3(0, 10, 0), Vector3.UnitX);
        Assert.Equal(Vector3.UnitY, d);
        Assert.Equal(Vector3.UnitX, BulletPaths.DirectionTo(Origin, Origin + new Vector3(0, 0.1f, 0), Vector3.UnitX));
    }

}
