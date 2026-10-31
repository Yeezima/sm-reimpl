using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/no-fire-while-sprinting (protocol 51; the owner 2026-11-01: "lascannon devastator can currently shoot while sprinting. you should not be able to shoot while
/// sprinting"): while the sprint is applied on the ground (Sprint held, stick forward) the trigger does nothing for every weapon: no shot, no charge, no raise. A
/// held charge, a raise and its waiting shot and a burst's later rounds are cancelled by the sprint, so a release during the sprint fires nothing; the held
/// trigger no longer ends the sprint. After the sprint the weapon fires with the sprint's existing exit timing (M31/M32: the raise of a lowered weapon, rifle 6
/// ticks, pistol 5; always-ready weapons at once). Synthetic weapons only.
/// </summary>
public class NoFireWhileSprintingTests(ITestOutputHelper output)
{
    const float Run = 6.5f, Sprint = 10f;

    static WeaponSpec Gun(string name, WeaponSlot slot, TriggerKind trigger, float cooldown, int clip) =>
        new(name, slot, trigger, 20f, cooldown, clip, clip * 4, 1.0f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f);

    // The families' raise delays (spec "Hip fire"): rifle 0.2 s, pistol 0.167 s; the always-ready weapons never wait.
    static readonly WeaponSpec Bolter = Gun("bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 0.133f, 30) with { RaiseDelay = 0.2f };
    static readonly WeaponSpec Stalker = Gun("stalker_burst", WeaponSlot.Ranged, TriggerKind.SemiAuto, 0.475f, 10) with { RaiseDelay = 0.2f, BurstLength = 3, BurstInterval = 0.1f };
    static readonly WeaponSpec BoltPistol = Gun("bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 0.25f, 10) with { RaiseDelay = 1f / 6f };
    static readonly WeaponSpec PlasmaPistol = ChargeTests.PlasmaPistol with { RaiseDelay = 1f / 6f };
    static readonly WeaponSpec PlasmaGun = ChargeTests.PlasmaGun with { RaiseDelay = 0.2f };
    static readonly WeaponSpec PlasmaCannon = ChargeTests.Cannon with { AlwaysReady = true, RaiseDelay = 0.333f };
    static readonly WeaponSpec Lascannon = ChargeTests.LasPress with { AlwaysReady = true, RaiseDelay = 0.2f };     // the real one: fires on the press
    static readonly WeaponSpec HeldLas = ChargeTests.Las with { AlwaysReady = true, RaiseDelay = 0.2f };           // a held-charge ray weapon (the charge machinery)
    static readonly WeaponSpec HeavyBolter = Gun("heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 0.233f, 400) with
    {
        AlwaysReady = true, RaiseDelay = 0.333f, Heat = new HeatSpec(3f, 100f, 75f, 20f, 25f, 0.3f, 0f), Brace = new BraceSpec(0.1f, 1.8f) { EnterTime = 1.433f, ExitTime = 1.3f },
    };
    static readonly WeaponSpec Melta = MeltaTests.Melta with { AlwaysReady = true, RaiseDelay = 0.2f };
    static readonly SplashSpec Blast = new(6f, new DamageSpec(75f, 0f, 1f, 1f, 0f, 0f, false), [new(0f, 1f), new(6f, 0.05f)], true, false, "target_all");
    static readonly ProjectileSpec Grenade = new("ebps\\test\\grenade", 30f, 0f, "raycast", 0f, [new(0f, 1f)], false, false, true, 1, 0f, 0f, 3, null, Blast);
    static readonly WeaponSpec Launcher = Gun("grenade_launcher", WeaponSlot.Ranged, TriggerKind.SemiAuto, 0.2f, 10) with
    {
        AlwaysReady = true, RaiseDelay = 0.2f, Delivery = DeliveryKind.Projectile, Projectile = Grenade,
    };

    static readonly Dictionary<string, WeaponSpec> Weapons = new()
    {
        ["bolter"] = Bolter, ["stalker"] = Stalker, ["bolt pistol"] = BoltPistol, ["plasma pistol"] = PlasmaPistol, ["plasma gun"] = PlasmaGun,
        ["plasma cannon"] = PlasmaCannon, ["lascannon"] = Lascannon, ["held las"] = HeldLas, ["heavy bolter"] = HeavyBolter, ["melta"] = Melta, ["grenade launcher"] = Launcher,
    };

    /// <summary>A class carrying <paramref name="w"/> in its slot (a pistol class carries no primary), install-like speeds.</summary>
    static SimConfig ConfigFor(WeaponSpec w) => new()
    {
        Weapons = [w],
        Classes = [w.Slot == WeaponSlot.Pistol ? new ClassLoadout("t", -1, 0, []) { Evade = -1 } : new ClassLoadout("t", 0, -1, []) { Evade = -1 }],
        RunSpeed = Run, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = Sprint, AimRunSpeed = 3f, AimStrafeSpeed = 3f, AimBackSpeed = 2f,
        GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, HealthMax = 1e6f,
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);

    static float Speed(PlayerState s) => new Vector2(s.Velocity.X, s.Velocity.Y).Length();

    static (PlayerState S, SelfEvents E) Step(PlayerState s, SimConfig c, InputButtons b, float my = 1f)
    {
        var next = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out var e);
        return (next, e);
    }

    /// <summary>Sprints <paramref name="ticks"/> ticks with W and Shift (no trigger), up to sprint speed with the weapon lowered.</summary>
    static PlayerState Sprinted(SimConfig c, PlayerState? from = null, int ticks = 30)
    {
        var s = from ?? Fresh(c);
        for (int k = 0; k < ticks; k++) s = Step(s, c, InputButtons.Sprint).S;
        Assert.Equal(Sprint, Speed(s), 1e-3f);
        return s;
    }

    /// <summary>Ticks from a Fire tap (pressed on tick 0, released on tick 1) until a shot leaves, W held without Sprint; -1: none within 40 ticks.</summary>
    static int TicksToShot(PlayerState s, SimConfig c)
    {
        for (int k = 0; k < 40; k++)
        {
            (s, var e) = Step(s, c, k == 0 ? InputButtons.Fire : InputButtons.None);
            if (e.Fired) return k;
        }
        return -1;
    }

    // ---------------------------------------------------------------- every weapon: nothing leaves during the sprint, the exit timing after it

    [Theory]
    [InlineData("bolter", 6)]             // rifle family: the 0.2 s raise of a lowered weapon (6 ticks)
    [InlineData("stalker", 6)]
    [InlineData("bolt pistol", 5)]        // pistol family: 0.167 s (5 ticks)
    [InlineData("plasma pistol", 5)]      // a tap's basic shot waits for the raise
    [InlineData("plasma gun", 6)]
    [InlineData("plasma cannon", 1)]      // always ready: a tap's basic shot leaves on its release tick
    [InlineData("held las", 1)]
    [InlineData("lascannon", 0)]          // always ready, fires on the press
    [InlineData("heavy bolter", 0)]
    [InlineData("melta", 0)]
    [InlineData("grenade launcher", 0)]
    public void No_shot_leaves_while_sprinting_and_the_weapon_fires_with_the_sprint_exit_timing_after(string name, int afterSprint)
    {
        var w = Weapons[name];
        var c = ConfigFor(w);
        var s = Sprinted(c);
        var (clip, heat) = (s.Clip, s.Heat);
        // 45 ticks of sprinting with the trigger: a tap, a long hold (a charge weapon's charged stage is reached by then were it charging), a release, then taps.
        int shots = 0;
        for (int k = 0; k < 45; k++)
        {
            bool fire = k == 0 || (k >= 3 && k < 30) || (k >= 33 && k % 3 == 0 && k < 44);
            var (n, e) = Step(s, c, InputButtons.Sprint | (fire ? InputButtons.Fire : InputButtons.None));
            if (e.Fired) shots++;
            Assert.False(n.Charging, $"{name}: charging at sprint tick {k}");
            Assert.Equal((0, 0, 0), (n.RaiseTicks, n.PendingShot, n.BurstLeft));
            s = n;
        }
        output.WriteLine($"{name}: {shots} shots while sprinting; clip {clip} -> {s.Clip}, heat {heat:F2} -> {s.Heat:F2}, {Speed(s):F2} m/s");
        Assert.Equal(0, shots);
        Assert.Equal(clip, s.Clip);
        Assert.True(s.Heat <= heat + 1e-4f, $"{name}: heat rose while sprinting");
        Assert.Equal(Sprint, Speed(s), 1e-3f);   // the trigger no longer takes the sprint away
        Assert.Equal(0, s.ReadyTicks);           // the sprint keeps the weapon lowered

        // Shift let go, W still held: the first tick out of the sprint fires with the sprint's exit timing (unchanged).
        Assert.Equal(afterSprint, TicksToShot(s, c));
        // The same as the weapon from a sprint without any trigger use (the existing rule, OwnerRulesTests).
        Assert.Equal(TicksToShot(Sprinted(c), c), TicksToShot(s, c));
    }

    // ---------------------------------------------------------------- the lascannon (the owner's report)

    [Fact]
    public void The_lascannon_press_while_sprinting_fires_nothing_a_held_press_needs_a_new_press_after_and_then_fires_at_once()
    {
        var c = ConfigFor(Lascannon);
        var s = Sprinted(c);
        int clip = s.Clip;
        var (pressed, e) = Step(s, c, InputButtons.Sprint | InputButtons.Fire);
        Assert.False(e.Fired);
        Assert.Equal(clip, pressed.Clip);
        Assert.True(pressed.FireHeld);   // the real button: still held
        s = pressed;
        for (int k = 0; k < 10; k++) { (s, e) = Step(s, c, InputButtons.Sprint | InputButtons.Fire); Assert.False(e.Fired); }
        // Shift let go with the trigger still held: a semi-automatic press weapon waits for a new press.
        for (int k = 0; k < 10; k++) { (s, e) = Step(s, c, InputButtons.Fire); Assert.False(e.Fired); }
        (s, e) = Step(s, c, InputButtons.None);
        Assert.False(e.Fired);
        (s, e) = Step(s, c, InputButtons.Fire);
        Assert.True(e.Fired);   // at once: always ready
        Assert.Equal(clip - 1, s.Clip);
    }

    [Fact]
    public void The_lascannon_fires_on_the_press_when_not_sprinting_and_in_aim_mode_with_shift_held()
    {
        var c = ConfigFor(Lascannon);
        var run = Fresh(c);
        for (int k = 0; k < 20; k++) run = Step(run, c, InputButtons.None).S;
        Assert.True(Step(run, c, InputButtons.Fire).E.Fired);                                          // running
        Assert.True(Step(Fresh(c), c, InputButtons.Sprint | InputButtons.Fire, 0f).E.Fired);           // Shift without W: no sprint
        Assert.True(Step(Sprinted(c), c, InputButtons.Sprint | InputButtons.Aim | InputButtons.Fire).E.Fired);   // aim mode takes the sprint away
    }

    // ---------------------------------------------------------------- held auto-fire, bursts and raises under way

    [Fact]
    public void Held_auto_fire_stops_on_the_first_sprint_tick_and_fires_on_after_the_sprint_without_a_new_press()
    {
        var c = ConfigFor(Bolter);
        var s = Fresh(c);
        int before = 0;
        for (int k = 0; k < 30; k++) { var (n, e) = Step(s, c, InputButtons.Fire); if (e.Fired) before++; s = n; }
        Assert.True(before >= 5, $"{before} shots running");
        int clip = s.Clip;
        for (int k = 0; k < 30; k++)
        {
            var (n, e) = Step(s, c, InputButtons.Fire | InputButtons.Sprint);
            Assert.False(e.Fired, $"shot at sprint tick {k}");
            s = n;
        }
        Assert.Equal(clip, s.Clip);
        Assert.Equal(Sprint, Speed(s), 1e-3f);
        // Shift let go, the trigger held throughout: the full-auto trigger fires on after the rifle's raise (6 ticks), no new press needed.
        int first = -1;
        for (int k = 0; k < 20 && first < 0; k++) { var (n, e) = Step(s, c, InputButtons.Fire); if (e.Fired) first = k; s = n; }
        Assert.Equal(6, first);
    }

    [Fact]
    public void A_raise_under_way_and_its_waiting_shot_are_dropped_when_the_sprint_starts()
    {
        var c = ConfigFor(Bolter);
        var s = Fresh(c);
        for (int k = 0; k < 20; k++) s = Step(s, c, InputButtons.None).S;
        (s, _) = Step(s, c, InputButtons.Fire);   // a tap on the lowered rifle: the raise starts
        Assert.True(s.RaiseTicks > 0);
        (s, _) = Step(s, c, InputButtons.Sprint);
        Assert.Equal(0, s.RaiseTicks);
        for (int k = 0; k < 20; k++) { var (n, e) = Step(s, c, InputButtons.Sprint); Assert.False(e.Fired); s = n; }
        for (int k = 0; k < 20; k++) { var (n, e) = Step(s, c, InputButtons.None); Assert.False(e.Fired); s = n; }   // nothing left over after the sprint
    }

    [Fact]
    public void A_bursts_later_rounds_are_dropped_when_the_sprint_starts()
    {
        var c = ConfigFor(Stalker with { RaiseDelay = 0f });
        var s = Fresh(c);
        var (fired, e) = Step(s, c, InputButtons.Fire);
        Assert.True(e.Fired);
        Assert.Equal(2, fired.BurstLeft);
        int clip = fired.Clip;
        s = fired;
        for (int k = 0; k < 15; k++) { (s, e) = Step(s, c, InputButtons.Sprint); Assert.False(e.Fired); }
        Assert.Equal((0, clip), (s.BurstLeft, s.Clip));
    }

    // ---------------------------------------------------------------- charges: held from before the sprint, released during it

    [Theory]
    [InlineData("plasma gun", 40)]      // past the charged stage (1.1 s = 33 ticks)
    [InlineData("plasma gun", 10)]      // before it (a release would be the tap's basic shot)
    [InlineData("plasma pistol", 25)]   // past 0.65 s
    [InlineData("plasma cannon", 95)]   // past 3 s
    [InlineData("held las", 50)]        // past 1.5 s
    public void A_charge_held_when_the_sprint_starts_is_cancelled_and_its_release_during_the_sprint_fires_nothing(string name, int heldTicks)
    {
        var w = Weapons[name];
        var c = ConfigFor(w);
        foreach (bool releaseWithSprint in new[] { false, true })
        {
            var s = Fresh(c);
            for (int k = 0; k < 15; k++) s = Step(s, c, InputButtons.None).S;
            bool early = false;
            for (int k = 0; k < heldTicks; k++) { var (n, e) = Step(s, c, InputButtons.Fire); early |= e.Fired; s = n; }
            Assert.False(early);
            Assert.True(s.Charging, $"{name}: no charge after {heldTicks} ticks");
            int clip = s.Clip;
            SelfEvents ev;
            if (releaseWithSprint)
            {
                // Shift pressed on the very tick the trigger is let go: the sprint wins, the release fires nothing.
                (s, ev) = Step(s, c, InputButtons.Sprint);
                Assert.False(ev.Fired, $"{name}: the release on the sprint's first tick fired");
            }
            else
            {
                (s, ev) = Step(s, c, InputButtons.Sprint | InputButtons.Fire);   // the sprint starts with the trigger still held: the charge is cancelled
                Assert.False(ev.Fired);
                Assert.False(s.Charging);
                for (int k = 0; k < 10; k++) { (s, ev) = Step(s, c, InputButtons.Sprint | InputButtons.Fire); Assert.False(ev.Fired); Assert.False(s.Charging); }
                (s, ev) = Step(s, c, InputButtons.Sprint);   // released during the sprint
                Assert.False(ev.Fired, $"{name}: the release during the sprint fired");
            }
            Assert.False(s.Charging);
            Assert.Equal(0, s.PendingShot);
            for (int k = 0; k < 20; k++) { (s, ev) = Step(s, c, InputButtons.Sprint); Assert.False(ev.Fired); }
            for (int k = 0; k < 20; k++) { (s, ev) = Step(s, c, InputButtons.None); Assert.False(ev.Fired); }   // nothing pending leaves after the sprint
            Assert.Equal(clip, s.Clip);
            output.WriteLine($"{name} held {heldTicks} ticks, release with sprint {releaseWithSprint}: clip {clip} kept");
        }
    }

    // ---------------------------------------------------------------- what the sprint leaves alone

    [Fact]
    public void Grenade_launcher_detonation_and_reloads_still_work_while_sprinting()
    {
        // Ours (sim/no-fire-while-sprinting): the Reload press's remote detonation is not a shot, so the sprint does not block it (an owner question).
        var c = ConfigFor(Launcher);
        var s = Sprinted(c) with { ProjectilesOut = 2 };
        var (det, e) = Step(s, c, InputButtons.Sprint | InputButtons.Reload);
        Assert.True(e.Detonate);
        Assert.Equal(0, det.ProjectilesOut);
        // A reload started while sprinting runs to its end, as before.
        var b = ConfigFor(Bolter);
        var r = Sprinted(b, Fresh(b) with { Clip = 5 });
        (r, _) = Step(r, b, InputButtons.Sprint | InputButtons.Reload);
        Assert.True(r.ReloadLeft > 0f);
        for (int k = 0; k < 40; k++) r = Step(r, b, InputButtons.Sprint).S;
        Assert.Equal(Bolter.ClipSize, r.Clip);
        Assert.Equal(Sprint, Speed(r), 1e-3f);
    }

    [Fact]
    public void The_braced_heavy_bolter_fires_with_shift_and_w_held_the_brace_has_no_sprint()
    {
        var c = ConfigFor(HeavyBolter);
        var s = Fresh(c);
        (s, _) = Step(s, c, InputButtons.Reload, 0f);   // enter the brace
        for (int k = 0; k < 60 && s.Brace != BracePhase.Braced; k++) s = Step(s, c, InputButtons.None, 0f).S;
        Assert.Equal(BracePhase.Braced, s.Brace);
        var (n, e) = Step(s, c, InputButtons.Sprint | InputButtons.Fire);
        Assert.True(e.Fired);
        // Unbraced, sprinting: nothing.
        Assert.False(Step(Sprinted(c), c, InputButtons.Sprint | InputButtons.Fire).E.Fired);
        _ = n;
    }

    // ---------------------------------------------------------------- server and prediction agree

    [Theory]
    [InlineData("lascannon")]
    [InlineData("bolter")]
    [InlineData("plasma gun")]
    public void Server_and_prediction_agree_and_the_server_fires_nothing_while_sprinting(string name)
    {
        var c = ConfigFor(Weapons[name]);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed: 51, clients: 1, config: c);
        // Phases of 45 ticks: run with taps, sprint with taps, sprint with the trigger held, run with the trigger held, a charge-length hold then Sprint, idle.
        static bool Sprinting(uint tick) => (tick / 45 % 6) is 1 or 2 || (tick / 45 % 6 == 4 && tick % 45 >= 30);
        PlayerInput Script(int client, uint tick)
        {
            uint phase = tick / 45 % 6, k = tick % 45;
            bool fire = phase switch { 0 or 1 => k % 6 == 0, 2 or 3 => true, 4 => k < 40, _ => false };
            var buttons = (fire ? InputButtons.Fire : InputButtons.None) | (Sprinting(tick) ? InputButtons.Sprint : InputButtons.None);
            return new PlayerInput(tick, 0f, phase == 5 ? 0f : 1f, tick * 0.004f, buttons);
        }
        var truth = new SortedDictionary<uint, PlayerState>();
        var predicted = new Dictionary<uint, PlayerState>();
        var shotTicks = new List<uint>();
        uint lastTick = 0;
        h.Run(TimeSpan.FromSeconds(16), Script, () =>
        {
            var client = h.Clients[0];
            if (client.Status != ClientStatus.Connected) return;
            if (client.Tick > 0) predicted[client.Tick] = client.Predicted;
            uint t = h.Server.World.Tick;
            if (t != lastTick && h.Server.World.Players.TryGetValue(client.PlayerId, out var me))
            {
                lastTick = t;
                truth[t] = me;
                if (h.Server.World.LastShots.Any(x => x.Shooter == client.PlayerId)) shotTicks.Add(t);
            }
        });
        h.Run(TimeSpan.FromSeconds(2), (_, t) => new PlayerInput(t, 0, 0, 0f, InputButtons.None));
        // The server fired, but never inside a sprint phase (a margin of 3 ticks for the sprint's first tick and the phase edges).
        Assert.NotEmpty(shotTicks);
        var inSprint = shotTicks.Where(t => Sprinting(t) && Sprinting(t - 3) && Sprinting(t + 3) && truth.TryGetValue(t - 1, out var p) && p.Grounded).ToList();
        output.WriteLine($"{name}: {shotTicks.Count} server shots, {inSprint.Count} inside sprint phases");
        Assert.Empty(inSprint);
        int compared = 0;
        var mismatches = new List<string>();
        uint from = truth.Keys.First() + 90;
        foreach (var (tick, pred) in predicted)
        {
            if (tick < from || !truth.TryGetValue(tick, out var real)) continue;
            compared++;
            if (pred.Clip != real.Clip || pred.Heat != real.Heat || pred.Charging != real.Charging || pred.RaiseTicks != real.RaiseTicks || pred.Position != real.Position)
                mismatches.Add($"tick {tick}: predicted clip {pred.Clip} heat {pred.Heat} charging {pred.Charging} raise {pred.RaiseTicks}; server clip {real.Clip} heat {real.Heat} charging {real.Charging} raise {real.RaiseTicks}");
        }
        foreach (var m in mismatches.Take(5)) output.WriteLine("  " + m);
        Assert.True(compared > 300, $"only {compared} ticks compared");
        Assert.Empty(mismatches);
    }
}
