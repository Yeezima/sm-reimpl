using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/buffer-speed (the owner's request of 2026-10-29: "slow movement including flying and jumping hopping speed by a small degree ... Don't change
/// charge or lunge attack speed"): <see cref="MovementScale"/> × 0.9 (ours). Synthetic specs shaped like the install's; no original bytes.
/// </summary>
public class MovementScaleTests(ITestOutputHelper output)
{
    const float F = MovementScale.OwnerFactor;
    const int Marine = 0, Assault = 1, Heavy = 2, Tactical = 3;

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly JumpPackSpec Pack = JumpPackTests.Pack with
    {
        AirSpeed = 8f, AirAcceleration = 15f, Slash = JumpPackFeelTests.Snappy, DashAttack = JumpPackAttackTests.DashAttack,
        Variants = [new UpgradeVariant<JumpPackSpec>(["perk"], JumpPackTests.Pack with { ForwardSpeed = 20f, AirSpeed = 8f, AirAcceleration = 15f })],
    };
    static readonly EvadeSpec Side = EvadeTests.Roll with { Name = "side", SideOnly = true, AnimationSet = "ork_hop" };
    static readonly SimConfig Data = new()
    {
        Weapons = [Rifle],
        Classes =
        [
            new ClassLoadout("marine", 0, -1, SimConfig.PlaceholderMeleeSteps) { MeleeRush = RushTests.Spec, Evade = 0 },
            new ClassLoadout("assault", 0, -1, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, Evade = 1 },
            new ClassLoadout("heavy", 0, -1, SimConfig.PlaceholderMeleeSteps) { Evade = 2 },
            new ClassLoadout("ork", 0, -1, SimConfig.PlaceholderMeleeSteps) { Evade = 3, Body = 0 },
        ],
        JumpPacks = [Pack],
        Evades = [EvadeTests.Roll, JumpPackDashTests.Dash, EvadeTests.Hop, Side],
        Bodies = [new BodySpec("ork", 6.5f, 6f, 4.5f, 10f, 3.6f, 3f, 3f, 2f, 100f, 5f, 5f, 100f, 7f, 33f)],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, WalkSpeed = 3.6f, AimRunSpeed = 3f, AimStrafeSpeed = 3f, AimBackSpeed = 2f,
        GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };
    static readonly SimConfig Slow = MovementScale.Apply(Data, F);

    [Fact]
    public void Ground_flight_dash_and_hop_speeds_are_scaled_and_the_charge_lunges_slash_and_roll_are_not()
    {
        Assert.Equal(F, Slow.MoveSpeedScale);
        float[] before = [Data.RunSpeed, Data.StrafeSpeed, Data.BackSpeed, Data.SprintSpeed, Data.WalkSpeed, Data.AimRunSpeed, Data.AimStrafeSpeed, Data.AimBackSpeed, Data.MaxAcceleration];
        float[] after = [Slow.RunSpeed, Slow.StrafeSpeed, Slow.BackSpeed, Slow.SprintSpeed, Slow.WalkSpeed, Slow.AimRunSpeed, Slow.AimStrafeSpeed, Slow.AimBackSpeed, Slow.MaxAcceleration];
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i] * F, after[i], 5);
        Assert.Equal(Data.GroundTimeToMaxSpeed, Slow.GroundTimeToMaxSpeed);
        Assert.Equal((6.5f * F, 10f * F), (Slow.Bodies[0].RunSpeed, Slow.Bodies[0].SprintSpeed));
        Assert.Equal((5.85f, 9f), (Slow.For(Tactical).RunSpeed, Slow.For(Tactical).SprintSpeed));   // the Ork's body too
        var pack = Slow.JumpPacks[0];
        Assert.Equal((16f * F, 8f * F, 15f * F), (pack.ForwardSpeed, pack.AirSpeed, pack.AirAcceleration));
        Assert.Equal((20f * F, 8f * F), (pack.Variants[0].Spec.ForwardSpeed, pack.Variants[0].Spec.AirSpeed));
        // Not scaled: heights, gravities and times; the slash, the pound's dive, the dash attack; the bull rush; the roll and the Ork's side hop.
        Assert.Equal((Pack.HeightMap, Pack.JumpingGravity, Pack.TakeOffDelay, Pack.PoundSpeed), (pack.HeightMap, pack.JumpingGravity, pack.TakeOffDelay, pack.PoundSpeed));
        Assert.Same(Pack.Slash, pack.Slash);
        Assert.Same(Pack.DashAttack, pack.DashAttack);
        Assert.Same(RushTests.Spec, Slow.Classes[Marine].MeleeRush);
        Assert.Same(Data.Classes[Marine].Melee, Slow.Classes[Marine].Melee);   // melee steps and their lunges (feel/melee-lock reach data) unchanged
        Assert.Equal(Data.MeleeLock, Slow.MeleeLock);
        Assert.Same(EvadeTests.Roll, Slow.Evades[0]);
        Assert.Same(Side, Slow.Evades[3]);
        // The dash (flight and landing) and the heavy hop travel 0.9 as far over the same time and as high.
        foreach (int e in new[] { 1, 2 })
            foreach (var d in Enum.GetValues<EvadeDirection>().Take(4))
            {
                var (was, now) = (Data.Evades[e].Move(d), Slow.Evades[e].Move(d));
                Assert.Equal(was.Distance.Length() * F, now.Distance.Length(), 4);
                Assert.Equal((was.Duration, was.Height), (now.Duration, now.Height));
                if (was.Landing is { } l) Assert.Equal(l.Distance.Length() * F, now.Landing!.Distance.Length(), 4);
            }
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementScale.Apply(Data, 0f));
        Assert.Same(Data, MovementScale.Apply(Data, 1f));
    }

    static List<PlayerState> Run(SimConfig c, int cls, InputButtons b, int ticks = 30)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c);
        var states = new List<PlayerState>();
        for (int k = 0; k < ticks; k++) { s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, b), c, out _); states.Add(s); }
        return states;
    }

    [Fact]
    public void Running_and_sprinting_reach_the_scaled_speeds_in_the_same_time_and_the_sprint_gate_scales_with_them()
    {
        foreach (var b in new[] { InputButtons.None, InputButtons.Sprint })
        {
            var (fast, slow) = (Run(Data, Marine, b), Run(Slow, Marine, b));
            for (int k = 0; k < fast.Count; k++) Assert.Equal(fast[k].Velocity.Y * F, slow[k].Velocity.Y, 3);   // the same curve, 0.9 as fast
            output.WriteLine($"{(b == InputButtons.Sprint ? "sprint" : "run")}: {fast[^1].Velocity.Y:F3} -> {slow[^1].Velocity.Y:F3} m/s");
        }
        // The sprint gate (run + 0.25 x (sprint - run)) scales with the speeds; since protocol 39 the charge does not use it (W held is enough), the
        // client's sprint pose and the sprint stop do.
        Assert.Equal(7.375f * F, SprintStop.Threshold(Slow), 4);
    }

    [Fact]
    public void The_scatter_bands_and_the_landing_clip_follow_the_scale_so_every_gait_keeps_its_band()
    {
        // The review of 2026-10-29: the full backpedal (4.5 -> 4.05 m/s) would have dropped below the 4.25 m/s "running" band; the bands scale with the speeds.
        foreach (var (speed, band) in new[] { (Data.BackSpeed, SpreadMovement.Running), (Data.RunSpeed, SpreadMovement.Running), (Data.AimRunSpeed, SpreadMovement.Walking), (0.95f, SpreadMovement.Idle) })
        {
            Assert.Equal(band, Spread.MovementOf(new Vector3(0f, speed, 0f)));
            Assert.Equal(band, Spread.MovementOf(new Vector3(0f, speed * F, 0f), Slow.MoveSpeedScale));
        }
        Assert.Equal(SpreadMovement.Walking, Spread.MovementOf(new Vector3(0f, Data.BackSpeed * F, 0f)));   // unscaled bands: what was wrong
        // The landing clip's keep (client): none at the aimed run, as before.
        Assert.Equal(0f, FallRules.MoveKeep(Data.AimRunSpeed));
        Assert.Equal(0f, FallRules.MoveKeep(Slow.AimRunSpeed, Slow.MoveSpeedScale), 5);
        Assert.Equal(FallRules.MoveKeep(2f), FallRules.MoveKeep(2f * F, F), 5);
    }

    [Fact]
    public void A_jump_pack_jump_is_as_high_and_long_and_covers_less_ground_and_the_roll_is_unchanged()
    {
        PlayerState Land(SimConfig c)
        {
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Assault }, c);
            for (int k = 0; k < 200 && !(k > 10 && s.Jump == JumpPhase.Landed); k++)
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, k < 13 ? InputButtons.Jump : InputButtons.None), c, out _);
            return s;
        }
        var (a, b) = (Land(Data), Land(Slow));
        output.WriteLine($"full jump with W: {a.Position.Y:F2} m -> {b.Position.Y:F2} m");
        Assert.InRange(b.Position.Y / a.Position.Y, F - 0.02f, F + 0.02f);
        // The Tactical roll (unscaled) travels as before.
        PlayerState Roll(SimConfig c, int cls)
        {
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c);
            for (int k = 0; k < 29; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, k == 0 ? InputButtons.Jump : InputButtons.None), c, out _);
            Assert.True(s.EvadeTicks > 0);   // compared inside the evade (after it, the scaled ground movement stops the body)
            return s;
        }
        Assert.Equal(Roll(Data, Marine).Position, Roll(Slow, Marine).Position);
        Assert.Equal(Roll(Data, Heavy).Position.Y * F, Roll(Slow, Heavy).Position.Y, 3);   // the heavy hop
    }
}
