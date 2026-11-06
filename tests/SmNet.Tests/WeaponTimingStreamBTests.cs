using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Stream B of the owner's batch of 2026-11-06 late (protocol 62): (1) sim/holstered-cooldown, each weapon keeps its own shot cooldown through a switch, so the bolt pistol
/// fires 0.1 s after a switch from a lascannon that has just fired, as after a switch from a tactical weapon, while the lascannon still waits for its own 1 s; (4) sim/string-reset,
/// the thunder hammer's and daemon maul's last light swing frees a new string 1 s before its clip's end (the owner's answer; 1.333 s of 2.333 s; the data's <c>BRANCH_Melee_String_Reset</c> is at 1.544 s), X1 and X2 at their tags. Synthetic specs; the install
/// part checks the loader's values and is skipped without an install.
/// </summary>
public class WeaponTimingStreamBTests(ITestOutputHelper output)
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    /// <summary>A fact that is skipped unless the game install is found on this machine (never in CI).</summary>
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (never in CI)";
        }
    }

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, out SelfEvents e) => Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, b), c, out e);

    // ---------------------------------------------------------------- (1) the cooldown follows its weapon

    static WeaponSpec Gun(string name, WeaponSlot slot, float cooldown, float raise) =>
        new WeaponSpec(name, slot, TriggerKind.SemiAuto, 10f, cooldown, 30, 90, 1f, 250f, 245f, 250f, 1f, 1f, 1f, 1f, 1f) { RaiseDelay = raise };

    /// <summary>A primary with the given shot cooldown (the install's lascannon: 1.0 s; the bolter: 0.15 s) and the bolt pistol (0.1 s), with the owner's quickdraw switch (0.1 s).</summary>
    static SimConfig Config(float primaryCooldown) => new()
    {
        Weapons = [Gun("primary", WeaponSlot.Ranged, primaryCooldown, 0.2f), Gun("pistol", WeaponSlot.Pistol, 0.1f, 1f / 6f)],
        Classes = [new ClassLoadout("t", 0, 1, SimConfig.PlaceholderMeleeSteps) { Evade = -1 }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        WeaponSwitchTime = 0.1f, QuickdrawSwitch = true,
    };

    /// <summary>Fires the primary (the press waits for the raise), then on the tick after the shot presses the pistol key with the trigger, pressing every other tick: the ticks from
    /// the switch press to the pistol's shot, and the state when it left.</summary>
    static (int Ticks, PlayerState State) ShotThenPistol(SimConfig c)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { Slot = WeaponSlot.Ranged };
        s = Step(s, c, InputButtons.Fire, out var e);
        for (int k = 0; k < 30 && !e.Fired; k++) s = Step(s, c, InputButtons.None, out e);
        Assert.True(e.Fired);
        s = Step(s, c, InputButtons.SlotPistol | InputButtons.Fire, out e);
        if (e.Fired) return (0, s);
        for (int k = 1; k < 60; k++)
        {
            s = Step(s, c, k % 2 == 0 ? InputButtons.Fire : InputButtons.None, out e);
            if (e.Fired) return (k, s);
        }
        return (-1, s);
    }

    [Fact]
    public void The_pistol_fires_as_soon_after_a_lascannon_shot_as_after_a_bolter_shot()
    {
        var (afterLascannon, state) = ShotThenPistol(Config(1.0f));
        var (afterBolter, _) = ShotThenPistol(Config(0.15f));
        Assert.Equal(WeaponSlot.Pistol, state.Slot);
        Assert.InRange(afterLascannon, 3, 4);   // the switch's 3 ticks (0.1 s), the shot on the first press edge after them
        Assert.Equal(afterBolter, afterLascannon);
        Assert.True(state.OtherCooldown > 0.7f, $"{state.OtherCooldown}");   // the lascannon's cooldown went into the holster with it and runs down there
        output.WriteLine($"switch press to the pistol's shot: after the bolter {afterBolter} ticks, after the lascannon {afterLascannon} ticks (before protocol 62: about 30, the lascannon's 1 s); " +
                         $"the holstered lascannon's cooldown then {state.OtherCooldown:0.000} s");
    }

    [Fact]
    public void The_lascannon_taken_back_still_waits_for_its_own_cooldown()
    {
        var c = Config(1.0f);
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { Slot = WeaponSlot.Ranged };
        s = Step(s, c, InputButtons.Fire, out var e);
        int t = 0;
        for (; t < 30 && !e.Fired; t++) s = Step(s, c, InputButtons.None, out e);
        int shotTick = t;
        // Straight to the pistol and back, then the trigger pressed every other tick: the lascannon's second shot waits for 1 s after its first.
        s = Step(s, c, InputButtons.SlotPistol, out _); t++;
        for (int k = 0; k < 4; k++, t++) s = Step(s, c, InputButtons.None, out _);
        s = Step(s, c, InputButtons.SlotPrimary, out _); t++;
        int second = -1;
        for (int k = 0; k < 60 && second < 0; k++, t++)
        {
            s = Step(s, c, k % 2 == 0 ? InputButtons.Fire : InputButtons.None, out e);
            if (e.Fired) second = t + 1;
        }
        Assert.Equal(WeaponSlot.Ranged, s.Slot);
        Assert.InRange(second - shotTick, 30, 32);   // 1 s (30 ticks) after the first shot, on a press edge
        output.WriteLine($"lascannon shot at tick {shotTick}, switched away and back, second shot at tick {second} ({(second - shotTick) / 30f:0.000} s later)");
    }

    [Fact]
    public void The_holstered_cooldowns_travel_in_the_own_record()
    {
        var p = Combat.Respawned(new PlayerState(7, Vector3.Zero, Vector3.Zero, 0f, true), Config(1f)) with { OtherCooldown = 0.75f, ThirdCooldown = 0.25f };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [p], Self: 7))));
        Assert.Equal((0.75f, 0.25f), (back.Players[0].OtherCooldown, back.Players[0].ThirdCooldown));
        Assert.Equal(62, Protocol.Version);
        Assert.Equal(13, Protocol.MaxSnapshotProjectiles);
        Assert.True(Protocol.MaxSnapshotBytes <= Protocol.SnapshotBudget, $"{Protocol.MaxSnapshotBytes}");
    }

    // ---------------------------------------------------------------- (4) the hammer's string reset

    /// <summary>The install's thunder hammer light swings (spec "Thunder hammer X1..X3"): durations, hits, combo windows, ranged and move gates (s).</summary>
    static readonly MeleeStep X1 = new(2.667f, 0.604f, 0.933f, 1.92f, 1.245f, 1.28f, 200f, 100f, 0f) { HeavyNext = 2 };
    static readonly MeleeStep X2 = new(2.333f, 0.567f, 0.967f, 1.981f, 1.656f, 1.4f, 200f, 100f, 0f) { HeavyNext = 2 };
    static MeleeStep X3(float resetAt) => new(2.333f, 0.7f, 0.892f, 1.544f, 0.961f, 1.51f, 200f, 100f, 0f) { StringEnd = true, HeavyNext = -1, StringResetAt = resetAt };
    static readonly MeleeStep Y = new(1.667f, 0.433f, 0.533f, 1f, 1f, 1f, 50f, 0f, 0f);

    static SimConfig Hammer(float resetAt) => new()
    {
        Classes = [new ClassLoadout("hammer", -1, -1, [X1, X2, X3(resetAt)]) { MeleeHeavy = [Y, Y], Evade = -1 }],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    /// <summary>X3 just begun (as the chain starts it: its melee lock up to its combo window), then the button pressed every other tick: the tick a new string begins and how.</summary>
    static (int Tick, PlayerState State) PressThroughX3(SimConfig c, InputButtons button)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { MeleeStep = 3, MeleeTime = 0f, MeleeLock = 0.892f };
        for (int k = 1; k < 120; k++)
        {
            s = Step(s, c, k % 2 == 0 ? button : InputButtons.None, out _);
            if (s.MeleeStep is > 0 and not 3 || s.MeleeHeavy) return (k, s);
        }
        return (-1, s);
    }

    [Fact]
    public void A_new_light_string_starts_at_the_string_reset_instead_of_the_clips_end()
    {
        var (reset, after) = PressThroughX3(Hammer(1.333f), InputButtons.Melee);
        var (old, _) = PressThroughX3(Hammer(float.PositiveInfinity), InputButtons.Melee);
        Assert.Equal((1, false), (after.MeleeStep, after.MeleeHeavy));
        Assert.InRange(reset, 40, 42);   // the owner's 1 s before the end: 1.333 s = 40 ticks, on a press edge
        Assert.InRange(old, 70, 72);     // 2.333 s = 70 ticks, the clip's end
        output.WriteLine($"after X3's start a new string begins at tick {reset} ({reset / 30f:0.000} s); before sim/string-reset at tick {old} ({old / 30f:0.000} s): {(old - reset) / 30f:0.000} s sooner");
    }

    [Fact]
    public void A_heavy_press_after_the_reset_starts_heavy_strike_one_and_an_earlier_press_does_nothing()
    {
        var (reset, after) = PressThroughX3(Hammer(1.333f), InputButtons.MeleeHeavy);
        Assert.True(after.MeleeHeavy);
        Assert.Equal(1, after.MeleeStep);
        Assert.InRange(reset, 40, 42);
        // A press before the reset (after X3's hit) leaves X3 running.
        var c = Hammer(1.544f);
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { MeleeStep = 3, MeleeTime = 1.2f };
        s = Step(s, c, InputButtons.Melee, out _);
        Assert.Equal(3, s.MeleeStep);
    }

    [Fact]
    public void A_press_after_the_first_swings_combo_window_starts_a_new_string_at_its_reset()
    {
        // X1 alone (no press in its combo window, 0.933..1.92 s): a press from its reset (1.92 s) starts X1 again; before, nothing until its end (2.667 s).
        var c = new SimConfig
        {
            Classes = [new ClassLoadout("hammer", -1, -1, [X1 with { StringResetAt = 1.92f }, X2, X3(1.544f)]) { Evade = -1 }],
            MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { MeleeStep = 1, MeleeTime = 1.95f, MeleeLock = 0f };
        s = Step(s, c, InputButtons.Melee, out _);
        Assert.Equal((1, false), (s.MeleeStep, s.MeleeHeavy));
        Assert.True(s.MeleeTime < 0.1f, $"{s.MeleeTime}");   // a new X1, not the old one at 1.98 s
        // In the combo window the press still chains to X2.
        var w = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { MeleeStep = 1, MeleeTime = 1.0f, MeleeLock = 0f };
        w = Step(w, c, InputButtons.Melee, out _);
        Assert.Equal(2, w.MeleeStep);
    }

    [Fact]
    public void The_string_reset_is_in_the_config_hash_only_when_set()
    {
        Assert.NotEqual(Hammer(1.544f).Hash(), Hammer(float.PositiveInfinity).Hash());
        var plain = new SimConfig { Classes = [new ClassLoadout("hammer", -1, -1, [X1, X2, X3(float.PositiveInfinity)])] };
        var same = new SimConfig { Classes = [new ClassLoadout("hammer", -1, -1, [X1, X2, X3(float.PositiveInfinity) with { }])] };
        Assert.Equal(plain.Hash(), same.Hash());
    }

    [InstallFact]
    public void The_install_gives_the_hammer_and_the_maul_the_data_string_reset_and_no_other_weapon()
    {
        var c = GameplayTuning.Load(Game.Value!).Config;
        var seen = new HashSet<string>();
        bool hammer = false;
        foreach (var cls in c.Classes)
            foreach (var m in cls.MeleeChoices)
            {
                if (!seen.Add(m.Wargear) || m.Steps.Count == 0) continue;
                var last = m.Steps[^1];
                bool isHammer = m.Wargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase) || m.Wargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase);
                output.WriteLine($"{m.Wargear}: last light step {last.Duration:0.000} s, string end {last.StringEnd}, string reset {(float.IsFinite(last.StringResetAt) ? $"{last.StringResetAt:0.000} s" : "none")}");
                output.WriteLine($"    light steps' string resets: {string.Join(", ", m.Steps.Select(x => float.IsFinite(x.StringResetAt) ? $"{x.StringResetAt:0.000} of {x.Duration:0.000} s (combo closes {x.ComboClose:0.000})" : "none"))}");
                if (isHammer)
                {
                    hammer = true;
                    Assert.True(last.StringEnd);
                    Assert.Equal(2.3333f - MeleeTuning.OwnerHammerRecoveryCut, last.StringResetAt, 2);   // the owner's 1 s before the end (the data's tag: 0.6618 of 2.333 s = 1.544 s)
                    Assert.Equal(0.72f * 2.6667f, m.Steps[0].StringResetAt, 2);   // X1: 0.72 of 2.667 s
                    Assert.Equal(0.8548f * 2.3333f, m.Steps[1].StringResetAt, 2);   // X2: 0.8548 of 2.333 s
                    Assert.All(m.Steps.Take(2), x => Assert.True(x.StringResetAt + 1e-3f >= x.ComboClose));   // X1, X2: never inside a combo window (X3 chains nowhere)
                }
                else Assert.All(m.Steps, x => Assert.False(float.IsFinite(x.StringResetAt)));
                Assert.All(m.Heavy, h => Assert.False(float.IsFinite(h.StringResetAt)));
            }
        Assert.True(hammer);
    }

    // ---------------------------------------------------------------- (2) the toss follow-through, (3) the predicted own plasma ball (client rules)

    [Fact]
    public void The_toss_plays_on_to_the_clips_end_unless_another_action_shows()
    {
        Assert.True(ActionHandoff.ThrowFollowContinues(0.2f, 0.667f, busy: false));    // the release (clip 0.2 s): the follow-through starts
        Assert.True(ActionHandoff.ThrowFollowContinues(0.6f, 0.667f, busy: false));
        Assert.False(ActionHandoff.ThrowFollowContinues(0.667f, 0.667f, busy: false));  // the clip's end (BRANCH_UseEquipment_End)
        Assert.False(ActionHandoff.ThrowFollowContinues(0.3f, 0.667f, busy: true));     // a shot, a switch, a melee attack ... takes over at once
        Assert.False(ActionHandoff.ThrowFollowContinues(float.NaN, 0.667f, busy: false));
        // The follow-through's clip time: after the release the clip runs at its own speed (the wind-up was fitted to the simulation's 0.1 s).
        Assert.Equal(0.2f + 0.3f, ActionHandoff.TossClipTime(0.1f + 0.3f, 0.1f, 0.2f), 4);
    }

    static ProjectileSpec Ball(float speed, float extraAim = 0f, IReadOnlyList<DistancePoint>? gravity = null) =>
        new("plasma_altfire_mp", speed, 5f, "point", 0.1f, gravity ?? [], true, true, false, 0, 0f, extraAim, 0, null, null);

    [Fact]
    public void The_predicted_ball_flies_the_servers_path_and_stops_at_the_wall()
    {
        var spec = Ball(60f);
        var v = OwnBallFlight.LaunchVelocity(Combat.AimDirection(0.3f, 0.1f), spec);
        Assert.Equal(Combat.AimDirection(0.3f, 0.1f) * 60f, v, new Vector3Comparer(1e-3f));
        // Five ticks in open air: 5 × 2 m; then a wall 1 m ahead stops it there.
        var (pos, vel, travelled) = (Vector3.Zero, new Vector3(0f, 60f, 0f), 0f);
        for (int k = 0; k < 5; k++) (pos, vel, travelled, _) = OwnBallFlight.Step(pos, vel, travelled, spec, 0f, 1f / 30f, float.PositiveInfinity, null);
        Assert.Equal(10f, pos.Y, 3);
        RayHit? Wall(Vector3 o, Vector3 d, float l) => o.Y + d.Y * l >= 11f ? new RayHit((11f - o.Y) / d.Y, o + d * ((11f - o.Y) / d.Y), -Vector3.UnitY, 0) : null;
        var (at, _, flown, stopped) = OwnBallFlight.Step(pos, vel, travelled, spec, 0f, 1f / 30f, float.PositiveInfinity, Wall);
        Assert.True(stopped);
        Assert.Equal((11f, 11f), (at.Y, flown), new FloatPairComparer(1e-3f));
        // The trace met a player 3 m out: it stops there.
        var (p2, _, _, s2) = OwnBallFlight.Step(Vector3.Zero, new Vector3(0f, 60f, 0f), 0f, spec, 9.8f, 0.1f, 3f, null);
        Assert.True(s2);
        Assert.Equal(3f, p2.Y, 3);
        // Extra aim angle raises the launch as the server's Launch does.
        var raised = OwnBallFlight.LaunchVelocity(Vector3.UnitY, Ball(10f, extraAim: 10f));
        Assert.Equal(MathF.Sin(10f * MathF.PI / 180f) * 10f, raised.Z, 3);
    }

    sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }

    sealed class FloatPairComparer(float tolerance) : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => MathF.Abs(a.Item1 - b.Item1) <= tolerance && MathF.Abs(a.Item2 - b.Item2) <= tolerance;
        public int GetHashCode((float, float) v) => 0;
    }
}
