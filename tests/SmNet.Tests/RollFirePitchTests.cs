using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/roll-fire-pitch (protocol 51; the owner 2026-11-01):
/// A. "ensure you can never shoot while the roll animation is playing": for the whole Tactical roll the trigger does nothing for every weapon (no shot, no
/// charge, no raise), does not end the roll at the data's BRANCH_ToRanged any more, and fires nothing in the tick the stick or Aim cuts the roll short; a
/// charge held into the roll is cancelled; a trigger held through the roll's end follows the sprint rule (full auto fires on, semi-automatic and charge
/// weapons need a new press). The heavies' hop and the jump-pack dash are unchanged.
/// B. "there should be no upper bound to the Y axis angle they can shoot at": the simulation accepts a view pitch up to straight up (π/2), shots there go
/// straight up, the wire carries it unclamped, and prediction agrees with the server. Synthetic weapons and evades only.
/// </summary>
public class RollFirePitchTests(ITestOutputHelper output)
{
    const float Deg = MathF.PI / 180f;
    // EvadeTests.Roll: a 1 s clip (30 ticks), the stick (MoveAt) and Aim (RangedAt) end it from 0.6 s (18 ticks played).
    const int RollTicks = 30, CutAt = 19;

    static SimConfig ConfigFor(WeaponSpec w) => new()
    {
        Weapons = [w],
        Classes = [w.Slot == WeaponSlot.Pistol ? new ClassLoadout("t", -1, 0, []) { Evade = 0 } : new ClassLoadout("t", 0, -1, []) { Evade = 0 }],
        Evades = [EvadeTests.Roll],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, AimRunSpeed = 3f, AimStrafeSpeed = 3f, AimBackSpeed = 2f,
        GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, HealthMax = 1e6f,
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);

    static (PlayerState S, SelfEvents E) Step(PlayerState s, SimConfig c, InputButtons b, float my = 0f, float pitch = 0f)
    {
        var next = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b, pitch), c, out var e);
        return (next, e);
    }

    /// <summary>Ticks from a Fire tap (pressed on tick 0, released on tick 1) until a shot leaves; -1: none within 40 ticks.</summary>
    static int TicksToShot(PlayerState s, SimConfig c)
    {
        for (int k = 0; k < 40; k++)
        {
            (s, var e) = Step(s, c, k == 0 ? InputButtons.Fire : InputButtons.None);
            if (e.Fired) return k;
        }
        return -1;
    }

    /// <summary>A roll played to its end with no input (Jump on the first tick), then one idle tick: the state right after it.</summary>
    static PlayerState Rolled(SimConfig c)
    {
        var s = Step(Fresh(c), c, InputButtons.Jump).S;
        for (int k = 1; k < RollTicks; k++) s = Step(s, c, InputButtons.None).S;
        Assert.Equal(RollTicks, s.EvadeTicks);
        return s;
    }

    // ---------------------------------------------------------------- A: every weapon family

    [Theory]
    [InlineData("bolter")]
    [InlineData("stalker")]
    [InlineData("bolt pistol")]
    [InlineData("plasma pistol")]
    [InlineData("plasma gun")]
    [InlineData("plasma cannon")]
    [InlineData("lascannon")]
    [InlineData("held las")]
    [InlineData("heavy bolter")]
    [InlineData("melta")]
    [InlineData("grenade launcher")]
    public void No_shot_charge_or_raise_during_the_whole_roll_and_fire_does_not_end_it(string name)
    {
        var w = NoFireWhileSprintingTests.Weapons[name];
        var c = ConfigFor(w);
        var s = Fresh(c);
        var (clip, heat) = (s.Clip, s.Heat);
        int shots = 0;
        // The press tick holds Fire too; then a tap, a long hold (a charge would reach its charged stage), a release, and taps up to the clip's last tick.
        for (int k = 0; k < RollTicks; k++)
        {
            bool fire = k == 0 || (k >= 3 && k < 22) || (k >= 24 && k % 2 == 0);
            var (n, e) = Step(s, c, (k == 0 ? InputButtons.Jump : InputButtons.None) | (fire ? InputButtons.Fire : InputButtons.None));
            if (e.Fired) shots++;
            Assert.Equal(k + 1, n.EvadeTicks);   // the trigger never ends the roll (before: at RangedAt, tick 19)
            Assert.False(n.Charging, $"{name}: charging at roll tick {k}");
            Assert.Equal((0, 0, 0), (n.RaiseTicks, n.PendingShot, n.BurstLeft));
            s = n;
        }
        output.WriteLine($"{name}: {shots} shots in the roll; clip {clip} -> {s.Clip}, heat {heat:F2} -> {s.Heat:F2}");
        Assert.Equal(0, shots);
        Assert.Equal(clip, s.Clip);
        Assert.True(s.Heat <= heat + 1e-4f, $"{name}: heat rose in the roll");
        // After the roll the weapon fires with the timing of a roll without any trigger use (the M21/M31 exit: a lowered weapon's raise).
        int after = TicksToShot(s, c);
        Assert.True(after >= 0, $"{name}: no shot after the roll");
        Assert.Equal(TicksToShot(Rolled(c), c), after);
    }

    [Theory]
    [InlineData("plasma gun")]
    [InlineData("plasma pistol")]
    [InlineData("plasma cannon")]
    [InlineData("held las")]
    public void A_charge_held_when_the_roll_starts_is_cancelled_and_its_release_fires_nothing(string name)
    {
        var w = NoFireWhileSprintingTests.Weapons[name];
        var c = ConfigFor(w);
        var s = Fresh(c);
        for (int k = 0; k < 20; k++) s = Step(s, c, InputButtons.Fire).S;
        Assert.True(s.Charging, $"{name}: no charge before the roll");
        int clip = s.Clip;
        (s, var e) = Step(s, c, InputButtons.Jump | InputButtons.Fire);
        Assert.Equal(1, s.EvadeTicks);
        Assert.False(e.Fired);
        Assert.Equal((false, 0, 0), (s.Charging, s.PendingShot, s.RaiseTicks));
        // Held on, released mid-roll, held again to the roll's end: nothing.
        for (int k = 1; k < RollTicks; k++)
        {
            (s, e) = Step(s, c, k is >= 8 and < 12 ? InputButtons.None : InputButtons.Fire);
            Assert.False(e.Fired, $"{name}: a shot at roll tick {k}");
            Assert.False(s.Charging);
        }
        Assert.Equal(clip, s.Clip);
        // Still held after the roll: no charge starts (it needs a new press, as after the sprint); the release fires nothing either.
        for (int k = 0; k < 10; k++) { (s, e) = Step(s, c, InputButtons.Fire); Assert.False(e.Fired); Assert.False(s.Charging); }
        (s, e) = Step(s, c, InputButtons.None);
        Assert.False(e.Fired);
        // A new press charges again.
        (s, _) = Step(s, c, InputButtons.Fire);
        Assert.True(s.Charging, $"{name}: a new press after the roll starts no charge");
    }

    [Fact]
    public void A_trigger_held_through_the_rolls_end_follows_the_sprint_rule()
    {
        // Full auto (the always-ready heavy bolter): fires on the first tick after the roll's clip end.
        var hb = ConfigFor(NoFireWhileSprintingTests.Weapons["heavy bolter"]);
        var s = Step(Fresh(hb), hb, InputButtons.Jump | InputButtons.Fire).S;
        for (int k = 1; k < RollTicks; k++)
        {
            (s, var e) = Step(s, hb, InputButtons.Fire);
            Assert.False(e.Fired, $"a shot at roll tick {k}");
        }
        (s, var fired) = Step(s, hb, InputButtons.Fire);
        Assert.Equal(0, s.EvadeTicks);
        Assert.True(fired.Fired, "the full-auto trigger held through the roll's end does not fire on");
        // Full auto with a raise (the bolter): the same as a fresh press at the roll's end.
        var bolter = ConfigFor(NoFireWhileSprintingTests.Weapons["bolter"]);
        int Held(SimConfig c)
        {
            var r = Step(Fresh(c), c, InputButtons.Jump | InputButtons.Fire).S;
            for (int k = 1; k < RollTicks; k++) r = Step(r, c, InputButtons.Fire).S;
            for (int k = 0; k < 40; k++) { (r, var e) = Step(r, c, InputButtons.Fire); if (e.Fired) return k; }
            return -1;
        }
        Assert.Equal(TicksToShot(Rolled(bolter), bolter), Held(bolter));
        // Semi-automatic (the bolt pistol, the melta) and press-fired (the lascannon): held through the end, nothing; a new press fires.
        foreach (var name in new[] { "bolt pistol", "melta", "lascannon", "grenade launcher" })
        {
            var c = ConfigFor(NoFireWhileSprintingTests.Weapons[name]);
            Assert.Equal(-1, Held(c));
            var r = Step(Fresh(c), c, InputButtons.Jump | InputButtons.Fire).S;
            for (int k = 1; k < RollTicks + 5; k++) r = Step(r, c, InputButtons.Fire).S;
            Assert.Equal(TicksToShot(Rolled(c), c), TicksToShot(Step(r, c, InputButtons.None).S, c));
        }
    }

    [Fact]
    public void In_the_tick_the_stick_or_aim_cuts_the_roll_short_the_trigger_fires_nothing_it_fires_on_the_next()
    {
        var c = ConfigFor(NoFireWhileSprintingTests.Weapons["heavy bolter"]);   // always ready, full auto: any later delay would be the rule's
        foreach (var (label, held, my) in new[] { ("stick", InputButtons.Fire, 1f), ("aim", InputButtons.Fire | InputButtons.Aim, 0f) })
        {
            var s = Step(Fresh(c), c, InputButtons.Jump).S;
            int shotAt = -1;
            for (int k = 1; k < RollTicks && shotAt < 0; k++)
            {
                // No stick and no Aim before the branch (they would be ignored anyway); the trigger held all along.
                var (n, e) = Step(s, c, k >= CutAt - 1 ? held : InputButtons.Fire, k >= CutAt - 1 ? my : 0f);
                if (k == CutAt - 1) Assert.Equal(0, n.EvadeTicks);   // cut at 0.6 s played
                if (e.Fired) shotAt = k;
                s = n;
            }
            output.WriteLine($"{label}: the roll cut on tick {CutAt - 1}, first shot on tick {shotAt}");
            Assert.Equal(CutAt, shotAt);   // not in the cut tick (before: Fire itself cut the roll and shot in it)
        }
    }

    [Fact]
    public void When_the_stick_cut_sprint_and_fire_coincide_nothing_fires_until_the_sprint_lets_go()
    {
        // The overlap tick: the stick (W) cuts the roll at 0.6 s with Sprint and Fire held. Both the roll's cut-tick rule and the sprint rule block the trigger;
        // the sprint goes on blocking while Shift is held; the first tick without Shift fires (always-ready, full-auto heavy bolter).
        var c = ConfigFor(NoFireWhileSprintingTests.Weapons["heavy bolter"]);
        var s = Step(Fresh(c), c, InputButtons.Jump | InputButtons.Fire).S;
        for (int k = 1; k < CutAt - 1; k++)
        {
            (s, var e) = Step(s, c, InputButtons.Fire | InputButtons.Sprint);   // Sprint held through the roll changes nothing (no stick yet)
            Assert.False(e.Fired);
            Assert.True(s.EvadeTicks > 0);
        }
        (s, var cut) = Step(s, c, InputButtons.Fire | InputButtons.Sprint, my: 1f);
        Assert.Equal(0, s.EvadeTicks);                 // cut by the stick in this tick
        Assert.False(cut.Fired);                       // the overlap tick fires nothing
        Assert.Equal((false, 0, 0), (s.Charging, s.RaiseTicks, s.BurstLeft));
        Assert.True(s.FireHeld);                       // the real button is kept
        for (int k = 0; k < 10; k++)
        {
            (s, var e) = Step(s, c, InputButtons.Fire | InputButtons.Sprint, my: 1f);
            Assert.False(e.Fired, $"a shot while sprinting out of the roll, tick {k}");
        }
        (s, var free) = Step(s, c, InputButtons.Fire, my: 1f);
        Assert.True(free.Fired, "the held full-auto trigger does not fire once Shift is let go");
        // A semi-automatic weapon in the same overlap needs a new press after the sprint.
        var m = ConfigFor(NoFireWhileSprintingTests.Weapons["melta"]);
        var t = Step(Fresh(m), m, InputButtons.Jump | InputButtons.Fire).S;
        for (int k = 1; k < CutAt - 1; k++) t = Step(t, m, InputButtons.Fire | InputButtons.Sprint).S;
        for (int k = 0; k < 12; k++) { (t, var e) = Step(t, m, InputButtons.Fire | InputButtons.Sprint, my: 1f); Assert.False(e.Fired); }
        for (int k = 0; k < 5; k++) { (t, var e) = Step(t, m, InputButtons.Fire, my: 1f); Assert.False(e.Fired); }
        t = Step(t, m, InputButtons.None, my: 1f).S;
        Assert.True(Step(t, m, InputButtons.Fire, my: 1f).E.Fired);
    }

    [Fact]
    public void The_heavies_hop_keeps_its_rule_fire_ends_it_at_the_ranged_branch_and_shoots()
    {
        // Not changed (the owner named the Tactical roll): the hop's fire branch (0.45 s here) still ends it with a shot in that tick.
        var c = EvadeTests.HopConfig;
        Assert.False(Combat.IsRoll(EvadeTests.Hop));
        Assert.True(Combat.IsRoll(EvadeTests.Roll));
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 3 }, c);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Jump | InputButtons.Fire), c, out _);
        int shotAt = -1;
        for (int k = 1; k < 30 && shotAt < 0; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var e);
            if (e.Fired) { shotAt = k; Assert.Equal(0, s.EvadeTicks); }
        }
        output.WriteLine($"hop: fire held ends it with a shot on tick {shotAt}");
        Assert.Equal(14, shotAt);   // 0.45 s = 13.5 ticks played
    }

    // ---------------------------------------------------------------- B: no upper bound on the pitch

    [Fact]
    public void The_simulation_keeps_a_pitch_up_to_straight_up_and_the_lower_limit_is_unchanged()
    {
        Assert.Equal(MathF.PI / 2f, PlayerInput.MaxPitch);
        foreach (float p in new[] { 1.4f, 1.45f, 89f * Deg, 89.9f * Deg, MathF.PI / 2f })
            Assert.Equal(p, new PlayerInput(0, 0, 0, 0, InputButtons.None, p).Sanitized().Pitch);
        Assert.Equal(MathF.PI / 2f, new PlayerInput(0, 0, 0, 0, InputButtons.None, 2.5f).Sanitized().Pitch);   // past straight up: straight up
        Assert.Equal(-1.4f, new PlayerInput(0, 0, 0, 0, InputButtons.None, -1.5f).Sanitized().Pitch);        // down: the old limit
        // The wire carries the pitch as sent (a float), not clamped below 90 degrees.
        foreach (float p in new[] { 89f * Deg, 89.9f * Deg, MathF.PI / 2f })
        {
            var input = new PlayerInput(7, 0, 0, 0.3f, InputButtons.Fire, p);
            var wire = Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(new InputBatch([input])))).Inputs[0];
            Assert.Equal(p, wire.Pitch);
            Assert.Equal(p, wire.Sanitized().Pitch);
        }
        // The snapshot carries the player's pitch the same way.
        var state = new PlayerState(5, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Pitch = MathF.PI / 2f };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [state])))).Players[0];
        Assert.Equal(MathF.PI / 2f, back.Pitch);
    }

    [Theory]
    [InlineData(89f)]
    [InlineData(89.9f)]
    [InlineData(90f)]
    public void A_shot_at_89_to_90_degrees_goes_straight_up_in_hip_fire_and_in_aim_mode(float degrees)
    {
        var c = new SimConfig { HealthMax = 1e6f };
        float pitch = MathF.Min(degrees * Deg, MathF.PI / 2f);
        foreach (var aim in new[] { InputButtons.None, InputButtons.Aim })
        {
            var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f)]);
            world.Spawn(1);
            for (int k = 0; k < 10; k++) world.Step(_ => new PlayerInput(0, 0, 0, 0.7f, aim, pitch));   // in aim mode, raised
            world.Step(_ => new PlayerInput(0, 0, 0, 0.7f, InputButtons.Fire | aim, pitch));
            var shot = Assert.Single(world.LastShots);
            var eye = Combat.EyePosition(world.Players[1], c);
            var d = Vector3.Normalize(shot.End - eye);
            float off = MathF.Acos(Math.Clamp(d.Z, -1f, 1f)) / Deg;
            output.WriteLine($"{degrees} deg, {(aim == 0 ? "hip" : "aim")}: shot {off:F3} deg off vertical, pitch {world.Players[1].Pitch / Deg:F3} deg, aiming {world.Players[1].Aiming}");
            Assert.Equal(pitch, world.Players[1].Pitch);
            Assert.True(MathF.Abs(off - (90f - MathF.Min(degrees, 90f))) < 0.01f, $"shot {off} deg off vertical");
            if (aim != 0) Assert.True(world.Players[1].Aiming);
        }
        // The crosshair's ray (the camera behind the shoulder, looking along the view): the shot goes along it.
        var ray = Combat.AimDirection(0.7f, MathF.Min(degrees, 89.9f) * Deg);
        var eye0 = new Vector3(0, 0, c.EyeHeight);
        var sent = new PlayerInput(0, 0, 0, 0.7f, InputButtons.Fire, MathF.Min(degrees, 89.9f) * Deg).WithShot(eye0 + new Vector3(0.6f, -0.4f, 0.2f), ray, eye0);
        var along = Combat.ShotDirection(sent, c);
        Assert.True(Vector3.Dot(along, ray) > 0.99f && Vector3.Cross(along, ray).Length() < 1e-3f, $"the shot {along} is off the crosshair's ray {ray}");   // under 0.06 degrees
    }

    [Theory]
    [InlineData(89f)]
    [InlineData(89.9f)]
    [InlineData(90f)]
    public void A_cone_weapon_reaches_a_target_straight_above(float degrees)
    {
        // The melta's cone (20 m) along a view at 89 to 90 degrees reaches a player 7 m above the eye; the same shot at level misses it (control).
        var c = new SimConfig
        {
            Weapons = [MeltaTests.Melta], Classes = [new ClassLoadout("melta", 0, -1, []), new ClassLoadout("target", 0, -1, [])],
            HealthMax = 1e6f, ShieldMax = 0f,
        };
        int Hits(float pitch)
        {
            var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 0f, 7f), 0f)]);
            world.Spawn(1, 0); world.Spawn(2, 1);
            world.Step(id => new PlayerInput(0, 0, 0, 0.3f, id == 1 ? InputButtons.Fire : InputButtons.None, id == 1 ? pitch : 0f));
            Assert.Contains(world.LastShots, s => s.Shooter == 1);
            return world.LastHits.Count(h => h.Shooter == 1 && h.Target == 2);
        }
        int up = Hits(MathF.Min(degrees * Deg, MathF.PI / 2f));
        output.WriteLine($"melta at {degrees} deg: {up} hit(s) on the player above; level: {Hits(0f)}");
        Assert.Equal(1, up);
        Assert.Equal(0, Hits(0f));
    }

    [Theory]
    [InlineData(89f, 0f)]
    [InlineData(89.9f, 0f)]
    [InlineData(90f, 0f)]
    [InlineData(89f, 10f)]
    [InlineData(89.9f, 10f)]
    [InlineData(90f, 10f)]
    public void A_projectile_launched_at_89_to_90_degrees_goes_straight_up_and_its_extra_aim_angle_never_takes_it_over_the_head(float degrees, float extra)
    {
        var launcher = NoFireWhileSprintingTests.Weapons["grenade launcher"];
        launcher = launcher with { Projectile = launcher.Projectile! with { ExtraAimAngle = extra } };
        var c = new SimConfig { Weapons = [launcher], Classes = [new ClassLoadout("gl", 0, -1, [])], HealthMax = 1e6f };
        float pitch = MathF.Min(degrees * Deg, MathF.PI / 2f), yaw = 0.3f;
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        world.Spawn(1);
        for (int k = 0; k < 5 && world.Projectiles.Count == 0; k++) world.Step(_ => new PlayerInput(0, 0, 0, yaw, k == 0 ? InputButtons.Fire : InputButtons.None, pitch));
        var p = Assert.Single(world.Projectiles);
        var v = p.Velocity;
        var view = Combat.AimDirection(yaw, 0f);
        float along = v.X * view.X + v.Y * view.Y;   // the horizontal part along the view (negative: over the head, backwards)
        float wanted = MathF.Min(degrees + extra, 90f);
        output.WriteLine($"{degrees} deg + {extra}: velocity {v}, horizontal along the view {along:F4} m/s, |v| {v.Length():F2}");
        Assert.True(along >= -1e-3f, $"launched over the head: {along} m/s backwards");
        Assert.Equal(MathF.Cos(wanted * Deg) * v.Length(), along, 2e-2f);   // the gravity of the launch tick only changes Z
    }

    [Fact]
    public void The_fresh_aim_ray_keeps_separate_up_and_down_limits()
    {
        var pivot = Vector3.Zero;
        var origin = new Vector3(0.5f, -3f, 2f);
        float up = 89.9f * Deg, down = -1.4f;
        // Up: a turn past straight up stops at 89.9 degrees (not at the old 1.4 rad, not past vertical).
        var (_, d) = FreshAim.Turn(origin, Combat.AimDirection(0.2f, 1.5f), pivot, 0f, 0.5f, down, up);
        Assert.Equal(up, MathF.Asin(d.Z), 1e-3f);
        Assert.True(Vector3.Dot(new Vector3(d.X, d.Y, 0f), Combat.AimDirection(0.2f, 0f)) > 0f);   // still facing the view's yaw
        // Down: stops at -1.4 rad, not at -89.9 degrees.
        (_, d) = FreshAim.Turn(origin, Combat.AimDirection(0.2f, -1.3f), pivot, 0f, -0.5f, down, up);
        Assert.Equal(down, MathF.Asin(d.Z), 1e-3f);
        // The symmetric overload is unchanged.
        (_, d) = FreshAim.Turn(origin, Combat.AimDirection(0.2f, 1.2f), pivot, 0f, 0.5f, 1.3f);
        Assert.Equal(1.3f, MathF.Asin(d.Z), 1e-3f);
    }

    [Theory]
    [InlineData(311)]
    [InlineData(312)]
    public void Prediction_matches_the_server_rolling_and_firing_with_the_view_straight_up(int seed)
    {
        var config = EvadeTests.Harmless(EvadeTests.Config);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        const int Tactical = 1;
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = Tactical });
        PlayerInput Input(int i, uint t)
        {
            uint phase = (t + (uint)i * 17) % 60;
            var buttons = InputButtons.None;
            if (phase is 0 or 1) buttons |= InputButtons.Jump;                       // a roll every 2 s
            if (phase is >= 5 and <= 50) buttons |= InputButtons.Fire;               // fire held through the roll and its end
            if (i == 1 && phase is >= 20 and <= 30) buttons |= InputButtons.Aim;      // the second one aims out of it
            float pitch = (t / 60 % 3) switch { 0 => MathF.PI / 2f, 1 => 89.9f * Deg, _ => 1.5f };
            return new PlayerInput(t, 0f, i == 0 && phase is >= 19 and <= 25 ? 1f : 0f, 0.4f * i, buttons, pitch);
        }
        var truth = new Dictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        uint from = truth.Keys.Min() + 90;
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0, mismatches = 0;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var w) || !w.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) { mismatches++; if (mismatches <= 2) output.WriteLine($"client {i} tick {tick}: predicted {pred}; server {real}"); }
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int rolls = mine.Count(p => p.EvadeTicks == 1);
            float top = mine.Max(p => p.Pitch);
            output.WriteLine($"client {i}: compared {compared}, mismatches {mismatches}, rolls {rolls}, highest pitch {top / Deg:F2} deg");
            Assert.True(compared > 300);
            Assert.True(rolls >= 5);
            Assert.Equal(MathF.PI / 2f, top);
            Assert.Equal(0, mismatches);
            Assert.Equal(0, h.Clients[i].CorrectionErrors.Count(e => e > 1e-4f));
        }
    }
}
