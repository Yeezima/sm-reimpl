using System.Numerics;
using SmContent;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>camera/hop-and-walls: the follow camera's boom, its aim correction, the swept sphere and the evade set's lookup.</summary>
public class CameraBoomTests
{
    const float Dt = 1f / 60f;
    const float Length = 4f;

    static float Run(CameraBoom boom, float soft, float seconds, float hard = 1f)
    {
        for (int i = 0; i < (int)(seconds / Dt); i++) boom.Step(soft, hard, Length, Dt, true);
        return boom.Fraction;
    }

    [Fact]
    public void PullsInFastAndContinuously()
    {
        var boom = new CameraBoom();
        float first = boom.Step(0.25f, 1f, Length, Dt, true);
        Assert.InRange(first, 0.25f, 0.999f);
        Assert.True(1f - first <= CameraBoom.PullMaxSpeed * Dt / Length + 1e-5f);
        Assert.InRange(Run(boom, 0.25f, 0.3f), 0.25f, 0.27f);
    }

    [Fact]
    public void HoldsBeforeItReleasesAndThenEasesOutSlowly()
    {
        var boom = new CameraBoom { Fraction = 0.25f };
        Assert.Equal(0.25f, Run(boom, 1f, CameraBoom.ReleaseHold - 0.05f));
        float previous = Run(boom, 1f, 0.1f);
        float biggest = 0f;
        for (int i = 0; i < 300; i++)
        {
            float f = boom.Step(1f, 1f, Length, Dt, true);
            Assert.True(f >= previous);
            biggest = MathF.Max(biggest, f - previous);
            previous = f;
        }
        Assert.True(biggest * Length <= CameraBoom.ReleaseMaxSpeed * Dt + 1e-4f);
        Assert.True(previous > 0.97f);
    }

    [Fact]
    public void ReachesTheWantedPlaceExactlyEnough()
    {
        var boom = new CameraBoom { Fraction = 0.98f };   // less than the margin from the end
        Assert.True(Run(boom, 1f, 2f) > 0.99f);
    }

    [Fact]
    public void AFreeDistanceFlickeringAtAnEdgeDoesNotPumpTheCamera()
    {
        var boom = new CameraBoom { Fraction = 0.5f };
        // The cast flips between blocked at 0.5 and free every 0.1 s: shorter than the hold, so the camera never starts out.
        for (int i = 0; i < 600; i++)
        {
            boom.Step((i / 6) % 2 == 0 ? 0.5f : 1f, 1f, Length, Dt, true);
            Assert.InRange(boom.Fraction, 0.5f, 0.5001f);
        }
    }

    [Fact]
    public void ASmallGainStaysPut()
    {
        var boom = new CameraBoom { Fraction = 0.5f };
        float small = 0.5f + (CameraBoom.ReleaseMargin - 0.02f) / Length;
        Assert.Equal(0.5f, Run(boom, small, 2f));
        Assert.True(Run(boom, 0.5f + (CameraBoom.ReleaseMargin + 0.3f) / Length, 2f) > 0.55f);
    }

    [Fact]
    public void TheHardCastClampsAtOnceAndRestartsTheHold()
    {
        var boom = new CameraBoom { Fraction = 0.8f };
        Assert.Equal(0.3f, boom.Step(0.3f, 0.3f, Length, Dt, true));
        Assert.Equal(0.3f, boom.Step(1f, 1f, Length, Dt, true));
    }

    [Fact]
    public void UnplacedGoesStraightToTheSoftCast()
    {
        var boom = new CameraBoom { Fraction = 1f };
        Assert.Equal(0.4f, boom.Step(0.4f, 0.9f, Length, Dt, false));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    public void BadCastsPullIn(float soft) => Assert.Equal(0f, new CameraBoom().Step(soft, soft, Length, Dt, false));

    [Fact]
    public void TheFramerateBarelyChangesTheRelease()
    {
        float Time(float dt)
        {
            var boom = new CameraBoom { Fraction = 0.25f };
            float t = 0f;
            while (boom.Fraction < 0.9f && t < 20f) { boom.Step(1f, 1f, Length, dt, true); t += dt; }
            return t;
        }
        Assert.InRange(Time(1f / 30f) / Time(1f / 144f), 0.9f, 1.1f);
    }

    [Fact]
    public void StableLookKeepsTheCrosshairOnTheFocus()
    {
        var rig = new Vector3(0f, -4f, 2f);
        var view = Vector3.UnitY;
        var actual = rig + new Vector3(-0.64f, 1.5f, 0f);   // a wall on the right moved the pivot
        const float focus = 30f;
        var look = CameraBoom.StableLook(rig, actual, view, focus, Vector3.UnitZ);
        var point = rig + view * focus;
        var miss = Vector3.Cross(point - actual, look).Length();
        Assert.True(miss < 1e-3f, $"missed by {miss} m");
    }

    [Fact]
    public void StableLookLeavesAnUncollidedCameraAlone()
    {
        var view = Vector3.Normalize(new Vector3(0.3f, 0.9f, -0.2f));
        Assert.Equal(view, CameraBoom.StableLook(Vector3.Zero, Vector3.Zero, view, 20f, Vector3.UnitZ));
    }

    [Fact]
    public void AMoveAlongTheViewNeedsNoTurn()
    {
        var look = CameraBoom.StableLook(Vector3.Zero, Vector3.UnitY * 2f, Vector3.UnitY, 30f, Vector3.UnitZ);
        Assert.True(Vector3.Distance(look, Vector3.UnitY) < 1e-5f);
    }

    [Fact]
    public void StableLookTurnsAtMostTheLimit()
    {
        var look = CameraBoom.StableLook(Vector3.Zero, new Vector3(-2f, 0f, 0f), Vector3.UnitY, 4f, Vector3.UnitZ);
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(look, Vector3.UnitY), -1f, 1f));
        Assert.InRange(angle, CameraBoom.AimMaxTurn - 1e-3f, CameraBoom.AimMaxTurn + 1e-3f);
        Assert.Equal(1f, look.Length(), 3);
    }

    [Fact]
    public void FocusMovesInLogDistanceAndStaysInItsRange()
    {
        Assert.Equal(CameraBoom.FocusMin, CameraBoom.StepFocus(0f, 0.2f, Dt, false));
        Assert.Equal(CameraBoom.FocusMax, CameraBoom.StepFocus(10f, float.NaN, Dt, false));
        float f = CameraBoom.StepFocus(100f, 5f, Dt, true);
        Assert.InRange(f, 5f, 99.9f);
        Assert.True(f > 50f);   // one frame does not cut it
        for (int i = 0; i < 120; i++) f = CameraBoom.StepFocus(f, 5f, Dt, true);
        Assert.Equal(5f, f, 1);
    }

    [Fact]
    public void TheSpectatorsPullInCapReachesTheBoom()
    {
        var slow = new CameraBoom(); var fast = new CameraBoom();
        slow.Step(0f, 1f, 40f, Dt, true);
        fast.Step(0f, 1f, 40f, Dt, true, 30f);
        Assert.Equal(1f - CameraBoom.PullMaxSpeed * Dt / 40f, slow.Fraction, 4);
        Assert.Equal(1f - 30f * Dt / 40f, fast.Fraction, 4);
    }

    [Theory]
    [InlineData(1.3f)]
    [InlineData(1.55f)]
    [InlineData(1.569f)]
    public void StableLookNeverCrossesTheVertical(float pitch)
    {
        var view = new Vector3(0f, MathF.Cos(pitch), MathF.Sin(pitch));
        // A camera below and behind the rig ray: the unlimited turn would pass straight up.
        var look = CameraBoom.StableLook(Vector3.Zero, new Vector3(0f, 4f, -3f), view, 4f, Vector3.UnitZ);
        Assert.True(look.Y > 0f, $"look {look}");
        Assert.True(look.Z < 1f);
    }

    // ---- fresh aim with a ray off the view ----

    static float PitchOf(Vector3 d) => MathF.Asin(Math.Clamp(d.Z, -1f, 1f));

    [Fact]
    public void TurnRigidMatchesAViewOnTheAxis()
    {
        float yaw = 0.7f, pitch = 0.3f;
        var (_, d) = FreshAim.TurnRigid(Vector3.Zero, Combat.AimDirection(yaw, pitch), Vector3.Zero, 0.1f, 0.05f, yaw + 0.1f);
        Assert.True(Vector3.Distance(d, Combat.AimDirection(yaw + 0.1f, pitch + 0.05f)) < 1e-5f);
    }

    [Theory]
    [InlineData(-1.4f, -0.1f, -0.02f)]   // the view at its lower limit, the ray 6 degrees below it
    [InlineData(-1.38f, -0.1f, -0.02f)]
    public void TurnRigidKeepsARayOffTheViewAtTheLowerLimit(float view, float off, float delta)
    {
        var ray = Combat.AimDirection(0f, view + off);
        var (_, clamped) = FreshAim.Turn(Vector3.Zero, ray, Vector3.Zero, 0f, delta, -1.4f, 1.568f);
        var (_, rigid) = FreshAim.TurnRigid(Vector3.Zero, ray, Vector3.Zero, 0f, delta, 0f);
        Assert.Equal(-1.4f, PitchOf(clamped), 3);   // what the clamp did: a bend of the whole offset
        Assert.Equal(view + off + delta, PitchOf(rigid), 3);
    }

    [Fact]
    public void TurnRigidTurnsTheOriginAboutThePivotOnly()
    {
        var (o, _) = FreshAim.TurnRigid(new Vector3(1f, 0f, 2f), Vector3.UnitY, Vector3.Zero, MathF.PI / 2f, 0.2f, 0f);
        Assert.True(Vector3.Distance(o, new Vector3(0f, 1f, 2f)) < 1e-5f);
    }

    // ---- the shoulder side ----

    static float RunSide(CameraSide side, float right, float left, float seconds)
    {
        float s = 1f;
        for (int i = 0; i < (int)(seconds / Dt); i++) s = side.Step(right, left, Dt, true);
        return s;
    }

    [Fact]
    public void ABlockedRightCrossesOverSmoothlyAndComesBack()
    {
        var side = new CameraSide();
        Assert.Equal(1f, RunSide(side, 0.2f, 1f, CameraSide.CrossHold - 0.05f));
        float previous = 1f, biggest = 0f;
        for (int i = 0; i < 120; i++)
        {
            float s = side.Step(0.2f, 1f, Dt, true);
            biggest = MathF.Max(biggest, previous - s);
            previous = s;
        }
        Assert.Equal(-1f, previous, 3);
        Assert.True(biggest < 0.09f, $"step {biggest}");
        Assert.Equal(-1f, RunSide(side, 1f, 1f, CameraSide.BackHold - 0.05f), 3);
        Assert.Equal(1f, RunSide(side, 1f, 1f, CameraSide.Travel + 0.2f), 3);
    }

    [Fact]
    public void BothSidesBlockedStaysRight() => Assert.Equal(1f, RunSide(new CameraSide(), 0.1f, 0.2f, 3f));

    [Fact]
    public void AFlickeringRightDoesNotSwapSides()
    {
        var side = new CameraSide();
        for (int i = 0; i < 600; i++) Assert.Equal(1f, side.Step((i / 6) % 2 == 0 ? 0.2f : 1f, 1f, Dt, true));
    }

    [Fact]
    public void ResetAndUnplacedGoStraightThere()
    {
        var side = new CameraSide();
        Assert.Equal(-1f, side.Step(0f, 1f, Dt, false), 3);
        side.Reset();
        Assert.Equal(1f, side.Step(1f, 0f, Dt, true));
    }

    // ---- the swept sphere ----

    /// <summary>A wall in the plane x = 1, 20 m wide and high.</summary>
    static StaticMesh Wall() => new(
        [new Vector3(1f, -10f, -10f), new Vector3(1f, 10f, -10f), new Vector3(1f, 10f, 10f), new Vector3(1f, -10f, 10f)],
        [0, 1, 2, 0, 2, 3]);

    [Fact]
    public void SphereStopsARadiusShortOfAFace()
    {
        var hit = Wall().SweepSphere(new Vector3(-3f, 0f, 0f), Vector3.UnitX, 0.3f, 10f);
        Assert.NotNull(hit);
        Assert.Equal(3.7f, hit!.Value.Distance, 3);
        Assert.Equal(new Vector3(-1f, 0f, 0f), hit.Value.Normal);
    }

    [Fact]
    public void SphereMissesWhatARayOffsetBesideItWouldThroughABackFace()
    {
        // Running along the wall 0.5 m off it: a 0.3 m sphere stays clear, wherever it starts.
        Assert.Null(Wall().SweepSphere(new Vector3(0.5f, -5f, 0f), Vector3.UnitY, 0.3f, 10f));
    }

    [Fact]
    public void ACastStartingCloseToAWallRunsAlongIt()
    {
        // 0.1 m off the wall with a 0.28 m sphere: the old ring started one ray behind the wall and read 0.
        Assert.Null(Wall().SweepSphere(new Vector3(0.9f, -5f, 0f), Vector3.UnitY, 0.28f, 8f));
        // Moving into it from there it stops at once.
        var hit = Wall().SweepSphere(new Vector3(0.9f, 0f, 0f), Vector3.UnitX, 0.28f, 8f);
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.Distance, 0f, 0.002f);
    }

    [Fact]
    public void SphereTouchesAPillarEdgeItsCentreRayMisses()
    {
        // The wall's edge at y = 10: a sphere passing 0.2 m beyond the end of it, moving in x.
        var hit = Wall().SweepSphere(new Vector3(-2f, 10.2f, 0f), Vector3.UnitX, 0.3f, 10f);
        Assert.NotNull(hit);
        float expected = 3f - MathF.Sqrt(0.3f * 0.3f - 0.2f * 0.2f);
        Assert.Equal(expected, hit!.Value.Distance, 3);
        Assert.Null(Wall().Raycast(new Vector3(-2f, 10.2f, 0f), Vector3.UnitX, 10f));
    }

    [Fact]
    public void SphereTouchesACorner()
    {
        var mesh = new StaticMesh([new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, -5f), new Vector3(1f, -5f, 0f)], [0, 1, 2]);
        var hit = mesh.SweepSphere(new Vector3(-2f, 0.2f, 0.2f), Vector3.UnitX, 0.5f, 10f);
        Assert.NotNull(hit);
        float side2 = 0.2f * 0.2f * 2f;
        Assert.Equal(3f - MathF.Sqrt(0.25f - side2), hit!.Value.Distance, 3);
    }

    [Fact]
    public void SphereSweepHandlesEmptyAndBadInput()
    {
        Assert.Null(new StaticMesh([], []).SweepSphere(Vector3.Zero, Vector3.UnitX, 0.3f, 10f));
        Assert.Null(Wall().SweepSphere(new Vector3(float.NaN), Vector3.UnitX, 0.3f, 10f));
        Assert.Null(Wall().SweepSphere(Vector3.Zero, Vector3.UnitX, 0.3f, 0.2f));
    }

    [Fact]
    public void SphereSweepOfRadiusZeroAgreesWithTheRay()
    {
        var hit = Wall().SweepSphere(new Vector3(-3f, 1f, 2f), Vector3.Normalize(new Vector3(1f, 0.2f, 0.1f)), 0f, 10f);
        var ray = Wall().Raycast(new Vector3(-3f, 1f, 2f), Vector3.Normalize(new Vector3(1f, 0.2f, 0.1f)), 10f);
        Assert.Equal(ray!.Value.Distance, hit!.Value.Distance, 3);
    }

    // ---- the hop: a set's place follows the view down ----

    static readonly CameraSet Evade = new("evade_heavy_default_mp", 42f, "navigationset_evade_tb", 80f, 125f, 0.25f, 0.1f, 10f, 5f, 0.5f, 0.25f);

    [Theory]
    [InlineData(-1.4f)]
    [InlineData(-1.22f)]
    [InlineData(-0.5f)]
    [InlineData(0.1f)]
    public void TheEvadeSetFollowsTheViewBelowItsUpperLimit(float pitch) => Assert.Equal(pitch, CameraRig.SetPlacePitch(pitch, Evade));

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.56f)]
    public void AboveItsUpperLimitThePlaceStops(float pitch) => Assert.Equal(CameraRig.SimRadians(125f), CameraRig.SetPlacePitch(pitch, Evade), 5);
}
