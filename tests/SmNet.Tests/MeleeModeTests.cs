using System.Numerics;
using SmClient.Customizer.Model;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// camera/melee-orbit (owner 2026-11-25): the shared melee mode's enter and exit rules (<see cref="MeleeMode"/>), the run-where-you-steer body yaw
/// (<see cref="MeleeOrbitSteer"/>), and that the simulation steps the sent input the same on the server and in the prediction. Synthetic data only.
/// </summary>
public class MeleeModeTests
{
    const float Tick = 1f / 30f;
    const float Deg = MathF.PI / 180f;

    static MeleeMode.Inputs In(bool strike = false, bool fire = false, bool aim = false, bool reload = false, bool alive = true, bool excluded = false) =>
        new(alive, strike, fire, aim, reload, excluded);

    static MeleeMode Entered()
    {
        var m = new MeleeMode();
        m.Step(In());
        Assert.Equal(MeleeMode.Change.Entered, m.Step(In(strike: true)));
        for (int i = 0; i < 10; i++) m.Step(In(strike: true));
        m.Step(In());
        Assert.True(m.Active);
        return m;
    }

    // ---- enter ----

    [Theory]
    [InlineData("mp_melee_chainsword", true)]
    [InlineData("mp_melee_power_axe", true)]
    [InlineData("mp_melee_powersword", true)]
    [InlineData("mp_melee_2hand_thunder_hammer", true)]
    [InlineData("mp_melee_2hand_power_mace", true)]
    [InlineData("mp_melee_combat_knife", false)]
    [InlineData("csm_mp_melee_combat_knife", false)]
    [InlineData("sm_mp_core_abil_heavy_evade", false)]   // the Devastator's kick / stomp: no melee weapon
    [InlineData("ork_coop_boy_choppa", true)]
    [InlineData(null, false)]
    public void OnlyMeleeWeaponsEnterNotTheKnife(string? wargear, bool enters) => Assert.Equal(enters, MeleeMode.EntersMeleeMode(wargear));

    [Fact]
    public void AStrikeEntersAndItLastsWithoutATimer()
    {
        var m = Entered();
        for (int i = 0; i < 30 * 60; i++) Assert.Equal(MeleeMode.Change.None, m.Step(In()));
        Assert.True(m.Active);
    }

    [Fact]
    public void NoStrikeNoMode()
    {
        var m = new MeleeMode();
        for (int i = 0; i < 100; i++) m.Step(In());
        Assert.False(m.Active);
    }

    // ---- exit ----

    [Fact] public void AShotLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Shot, m.Step(In(fire: true))); Assert.False(m.Active); }
    [Fact] public void AimingLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Aimed, m.Step(In(aim: true))); Assert.False(m.Active); }
    [Fact] public void AReloadPressLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Reloaded, m.Step(In(reload: true))); Assert.False(m.Active); }
    [Fact] public void DeathLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Died, m.Step(In(alive: false))); Assert.False(m.Active); }
    [Fact] public void ExclusionLeaves() { var m = Entered(); Assert.Equal(MeleeMode.Change.Excluded, m.Step(In(excluded: true))); Assert.False(m.Active); }

    [Fact]
    public void RespawnStartsOutsideTheMode()
    {
        var m = Entered();
        m.Step(In(alive: false));
        Assert.Equal(MeleeMode.Change.None, m.Step(In()));   // the respawn tick: already out
        Assert.False(m.Active);
    }

    [Fact]
    public void ReloadHeldIntoAStrikeDoesNotLeaveAgain()
    {
        var m = new MeleeMode();
        m.Step(In(reload: true));
        m.Step(In(reload: true, strike: true));
        m.Step(In(reload: true));
        Assert.True(m.Active);   // a press leaves, not a hold that began before the strike
    }

    [Fact]
    public void ThrowsAndSprintsKeepIt()
    {
        // Neither a grenade throw nor sprinting is an input the mode reads: only fire, aim, reload, death and respawn leave it.
        var m = Entered();
        for (int i = 0; i < 60; i++) Assert.Equal(MeleeMode.Change.None, m.Step(In()));
        Assert.True(m.Active);
    }

    [Fact]
    public void FireHeldBeforeTheStrikeEndsItOnceTheStrikeIsOver()
    {
        var m = new MeleeMode();
        m.Step(In(fire: true));
        Assert.Equal(MeleeMode.Change.Entered, m.Step(In(fire: true, strike: true)));   // a strike start beats the fire on the same tick
        m.Step(In(fire: true, strike: true));
        Assert.True(m.Active);
        Assert.Equal(MeleeMode.Change.Shot, m.Step(In(fire: true)));
    }

    [Fact]
    public void NoEntryWhileAiming()
    {
        var m = new MeleeMode();
        m.Step(In(aim: true, strike: true));
        Assert.False(m.Active);
    }

    [Fact]
    public void ANewStrikeAfterAShotEntersAgain()
    {
        var m = Entered();
        m.Step(In(fire: true));
        m.Step(In());
        Assert.Equal(MeleeMode.Change.Entered, m.Step(In(strike: true)));
    }

    // ---- the body yaw ----

    [Fact]
    public void TheStickIsReadRelativeToTheCamera()
    {
        float cam = 0.7f;
        Assert.Equal(cam, MeleeOrbitSteer.StickYaw(cam, 0f, 1f), 5);                       // W: the camera's look
        Assert.Equal(cam - MathF.PI / 2f, MeleeOrbitSteer.StickYaw(cam, 1f, 0f), 5);        // D: to the camera's right (yaw grows left)
        Assert.Equal(cam + MathF.PI / 2f, MeleeOrbitSteer.StickYaw(cam, -1f, 0f), 5);       // A
        Assert.Equal(MathF.PI, MathF.Abs(MeleeOrbitSteer.Wrap(MeleeOrbitSteer.StickYaw(cam, 0f, -1f) - cam)), 4);   // S: toward the camera
    }

    [Fact]
    public void TheStickIsSentAsForwardWithItsMagnitude()
    {
        var r = new MeleeOrbitSteer().Step(0f, 0f, 0.6f, 0.8f, snap: true, strikePress: false, Tick);
        Assert.Equal(0f, r.MoveX);
        Assert.Equal(1f, r.MoveY, 4);
        var half = new MeleeOrbitSteer().Step(0f, 0f, 0f, 0.5f, snap: false, strikePress: false, Tick);
        Assert.Equal(0.5f, half.MoveY, 4);
    }

    [Fact]
    public void IdleStickHoldsTheBody()
    {
        var steer = new MeleeOrbitSteer();
        for (int i = 0; i < 30; i++)
        {
            var r = steer.Step(cameraYaw: i * 0.2f, bodyYaw: 1.25f, 0f, 0f, snap: false, strikePress: false, Tick);   // the camera orbits, nothing held
            Assert.Equal(1.25f, r.Yaw);
            Assert.Equal((0f, 0f), (r.MoveX, r.MoveY));
        }
    }

    [Fact]
    public void AStrikeWithNoKeyGoesTheWayTheCameraLooks()
    {
        var r = new MeleeOrbitSteer().Step(cameraYaw: 2.5f, bodyYaw: -0.4f, 0f, 0f, snap: true, strikePress: true, Tick);
        Assert.Equal(2.5f, r.Yaw, 5);
    }

    [Fact]
    public void AStrikeWithAKeyGoesAlongTheKeyAtOnce()
    {
        var r = new MeleeOrbitSteer().Step(cameraYaw: 0f, bodyYaw: 0f, 1f, 0f, snap: true, strikePress: true, Tick);
        Assert.Equal(-MathF.PI / 2f, r.Yaw, 5);
        Assert.Equal(1f, r.MoveY, 5);
    }

    [Fact]
    public void TheBodyTurnsAtTheDataRateAfterTheRamp()
    {
        var steer = new MeleeOrbitSteer();
        float body = 0f;
        // The first tick: the ramp's share (1/30 s of 0.15 s) of 1080 degrees a second for one tick.
        var first = steer.Step(MathF.PI - 0.01f, body, 0f, 1f, false, false, Tick);
        Assert.Equal(1080f * (Tick / 0.15f) * Tick, first.Yaw / Deg, 2);
        body = first.Yaw;
        int ticks = 1;
        while (MathF.Abs(MeleeOrbitSteer.Wrap(MathF.PI - 0.01f - body)) > 1e-4f && ticks < 100) { body = steer.Step(MathF.PI - 0.01f, body, 0f, 1f, false, false, Tick).Yaw; ticks++; }
        // About 180 degrees: 81 degrees in the 0.15 s ramp, the rest at 1080 a second: 0.24 s, 8 ticks.
        Assert.InRange(ticks, 7, 9);
        // At full rate a tick turns 36 degrees.
        var full = new MeleeOrbitSteer();
        for (int i = 0; i < 5; i++) full.Step(1f, 1f, 0f, 1f, false, false, Tick);   // arrived each tick: the ramp restarts
        float b = 0f, prev = 0f;
        for (int i = 0; i < 6; i++) { prev = b; b = full.Step(3f, b, 0f, 1f, false, false, Tick).Yaw; }
        Assert.Equal(36f, (b - prev) / Deg, 1);
    }

    // ---- the simulation steps the sent input: the server and the prediction agree, the marine runs where he steers ----

    static readonly SimConfig Data = new()
    {
        Weapons = [new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f)],
        Classes = [new ClassLoadout("assault", 0, -1, SimConfig.PlaceholderMeleeSteps)],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f,
    };

    [Fact]
    public void TheServerAndThePredictionStepTheSameSentInput()
    {
        var server = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), Data);
        var client = server;
        var steer = new MeleeOrbitSteer();
        float cam = 0f;
        for (uint tick = 1; tick <= 180; tick++)
        {
            cam += 90f * Deg * Data.TickSeconds;   // the camera orbits a full turn in 4 s
            float stickX = tick > 120 ? 1f : 0f, stickY = tick > 120 ? 0f : 1f;   // W, then D
            var r = steer.Step(cam, client.Yaw, stickX, stickY, false, false, Data.TickSeconds);
            var input = new PlayerInput(tick, r.MoveX, r.MoveY, r.Yaw, InputButtons.None, 0f);
            server = Combat.StepSelf(server, input, Data, out _);
            client = Combat.StepSelf(client, input, Data, out _);
            Assert.Equal(server.Position, client.Position);
            Assert.Equal(server.Yaw, client.Yaw);
            if (tick is > 30 and <= 120)
            {
                // Running forward along the camera's look (it turns at 90 degrees a second, well under the body's rate): the body faces its run.
                Assert.InRange(MathF.Abs(MeleeOrbitSteer.Wrap(server.Yaw - cam)) / Deg, 0f, 4f);
                var v = new Vector2(server.Velocity.X, server.Velocity.Y);
                Assert.InRange(MathF.Abs(MeleeOrbitSteer.Wrap(MathF.Atan2(-v.X, v.Y) - server.Yaw)) / Deg, 0f, 15f);
            }
        }
        // D held for 2 s: running to the camera's right at the forward run speed (not the strafe speed), facing it.
        Assert.InRange(MathF.Abs(MeleeOrbitSteer.Wrap(server.Yaw - (cam - MathF.PI / 2f))) / Deg, 0f, 4f);
        Assert.True(new Vector2(server.Velocity.X, server.Velocity.Y).Length() > 6.2f);
    }
}
