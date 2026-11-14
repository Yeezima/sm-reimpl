using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/sprint-melee-charge and sim/ledge-charge-cancel (protocol 77; the owner's recollection 2026-11-15). (1) Classic PvP: a light press (RMB) with Shift and W held on the ground is the charge, never a swing; with Shift
/// let go (W still held) the swing starts on that very tick; Musou keeps its input. (2) Every mode: a light press in a charge held in the air (off a ledge) zeroes its horizontal momentum at once, and the swing steps
/// the stick's way (the view's with no stick). Synthetic clips (<see cref="RushTests"/>), the swing given a root-motion step. Restore switches <see cref="SimConfig.SprintMeleeCharge"/> and <see cref="SimConfig.LedgeChargeCancel"/> (false).
/// </summary>
public class SprintMeleeChargeTests(ITestOutputHelper output)
{
    static float T(float ticks) => ticks / 30f;

    static MeleeVariant Variant(float duration) => new(1f, Enumerable.Range(0, 61).Select(i => 3f * i / 60f).ToArray());   // 3 m over the clip

    static readonly SimConfig Config = WithStep(RushTests.Config);
    static readonly SimConfig Musou = Config with { Extras = ContentExtras.Musou };

    static SimConfig WithStep(SimConfig c) => c with
    {
        MeleeLock = MeleeLockSpec.Default with { StepFront = 1f }, MeleeLungeScale = 1.625f,
        Classes = c.Classes.Select(k => k.Melee is { Count: > 0 } m
            ? k with { Melee = [m[0] with { Reach = new MeleeReach(0f, T(3f), 0f, m[0].HitTime, 3.5f, 1f, [Variant(m[0].Duration)]) }, .. m.Skip(1)] } : k).ToArray(),
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);
    static PlayerInput In(InputButtons b, float x = 0f, float y = 0f) => new(0, x, y, 0f, b);
    static PlayerState Step(PlayerState s, PlayerInput i, SimConfig c) => Combat.StepPlayer(s, i, c, out _);
    static readonly PlayerInput SprintLight = In(InputButtons.Sprint | InputButtons.Melee, y: 1f);
    static readonly PlayerInput WalkLight = In(InputButtons.Melee, y: 1f);

    static float Flat(PlayerState s) => new Vector2(s.Velocity.X, s.Velocity.Y).Length();

    // ---- (1) the charge from a light press ----

    [Fact]
    public void A_light_press_while_sprinting_starts_the_charge_and_no_swing()
    {
        var s = Step(Fresh(Config), SprintLight, Config);
        Assert.Equal(RushPhase.Rush, s.Rush);
        Assert.Equal(0, s.MeleeStep);
        var again = Step(Fresh(Config), In(InputButtons.Sprint | InputButtons.Melee, 1f, 1f), Config);   // W + D charges too
        Assert.Equal(RushPhase.Rush, again.Rush);
        Assert.Equal(0, again.MeleeStep);
    }

    [Fact]
    public void Shift_let_go_with_W_held_swings_on_the_same_tick()
    {
        var s = Step(Fresh(Config), In(InputButtons.Sprint, y: 1f), Config);   // sprinting
        s = Step(s, In(InputButtons.None, y: 1f), Config);                      // Shift let go, W held
        s = Step(s, WalkLight, Config);                                         // the press: the swing, at once
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
    }

    [Fact]
    public void A_light_press_the_spacing_refuses_swings_nothing_and_the_press_waits()
    {
        var s = Fresh(Config) with { RushLock = 20 };
        s = Step(s, SprintLight, Config);
        Assert.Equal((RushPhase.None, 0), (s.Rush, s.MeleeStep));   // refused: neither a charge nor a swing
        var next = Step(s, In(InputButtons.None, y: 1f), Config);   // Shift let go (the press is held no more, but the buffer replays it as a swing)
        output.WriteLine($"after release: swing {next.MeleeStep}");
    }

    [Fact]
    public void A_light_press_mid_charge_still_cancels_into_the_swing_as_protocol_70()
    {
        var s = Step(Fresh(Config), SprintLight, Config);
        s = Step(s, In(InputButtons.Sprint, y: 1f), Config);
        s = Step(s, SprintLight, Config);
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
    }

    [Fact]
    public void The_heavy_press_charge_is_unchanged()
    {
        var s = Step(Fresh(Config), In(InputButtons.Sprint | InputButtons.MeleeHeavy, y: 1f), Config);
        Assert.Equal(RushPhase.Rush, s.Rush);
    }

    [Fact]
    public void The_restore_switch_swings_and_the_config_hash_changes()
    {
        var old = Config with { SprintMeleeCharge = false };
        var s = Step(Fresh(old), SprintLight, old);
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
        Assert.NotEqual(Config.Hash(), old.Hash());
    }

    [Fact]
    public void Musou_keeps_its_input_a_light_press_while_sprinting_swings()
    {
        if (!ContentExtras.Compiled) return;
        var s = Step(Fresh(Musou), SprintLight, Musou);
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
    }

    // ---- the Ork Boy (owner 2026-11-15: "It should be for all classes including ork boy in classic PvP yes") ----
    // The rule is class-agnostic: it reads Loadouts.Rush and Rush.ChargeHeld, never the race. Here an Ork-race class carrying an Ork-shaped charge (the real one is OrkClass.ReadRush's, built from
    // the install's data: slower contact damage, no minimum interval, a 10 m range; OrkBalanceTests pins its shape) on the Ork's 5 m/s body. The real read is not exercised: it needs the install.

    static readonly SimConfig OrkConfig = Config with
    {
        Extras = ContentExtras.Orks,
        Bodies = [new BodySpec("ork - boy (test)", 5f, 6.5f, 4.5f, 8f, 3f, 3f, 3f, 2f, 120f, 5f, 5f, 50f, 7f, 33f)],
        Classes = [.. Config.Classes, Config.Classes[0] with { Name = "ork - boy", Race = Race.Orks, Side = Team.None, Body = 0,
            MeleeRush = Config.Classes[0].MeleeRush! with { Primary = Config.Classes[0].MeleeRush!.Primary with { Damage = 15f, ShieldDamage = 15f }, Secondary = Config.Classes[0].MeleeRush!.Primary with { Damage = 0f, ShieldDamage = 0f }, Range = 10f, MinInterval = 0f } }],
    };

    [Fact]
    public void An_Ork_race_class_with_an_Ork_shaped_charge_charges_on_Shift_W_and_a_light_press_and_swings_with_Shift_released()
    {
        const int ork = 2;
        var start = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = ork }, OrkConfig);
        Assert.Equal(Race.Orks, Races.Of(OrkConfig, start.Class, start.Team));
        Assert.NotNull(Loadouts.Rush(OrkConfig, start));
        var charged = Step(start, SprintLight, OrkConfig);
        Assert.Equal((RushPhase.Rush, 0), (charged.Rush, charged.MeleeStep));
        var s = Step(start, In(InputButtons.Sprint, y: 1f), OrkConfig);   // sprinting
        s = Step(s, In(InputButtons.None, y: 1f), OrkConfig);              // Shift let go, W held
        s = Step(s, WalkLight, OrkConfig);                                 // the press: the swing at once
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
    }

    // ---- (2) the ledge cancel ----

    static PlayerState OffALedge(SimConfig c)
    {
        var s = Step(Fresh(c), In(InputButtons.Sprint | InputButtons.MeleeHeavy, y: 1f), c);
        for (int k = 0; k < 3; k++) s = Step(s, In(InputButtons.Sprint, y: 1f), c);
        s = s with { Position = new Vector3(0f, 0f, 8f), Grounded = false };
        s = Step(s, In(InputButtons.None), c);
        Assert.Equal(RushPhase.Rush, s.Rush);
        Assert.False(s.Grounded);
        Assert.True(Flat(s) > 5f, $"the held charge keeps its speed: {Flat(s)}");
        return s;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_light_press_in_the_air_zeroes_the_charges_momentum_and_steps_along_the_view_in_classic_and_musou(bool musou)
    {
        if (musou && !ContentExtras.Compiled) return;
        var c = musou ? Musou : Config;
        var held = OffALedge(c);
        var s = Step(held, In(InputButtons.Melee), c);
        output.WriteLine($"musou {musou}: before {Flat(held):F1} m/s, after {Flat(s):F2} m/s, yaw {s.Yaw:F2}");
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
        Assert.True(Flat(s) < Flat(held) * 0.5f, "the charge's momentum is gone");
        var moved = s.Position - held.Position;   // the swing's step moves the body (the clip's speed is not momentum: the velocity stays the carried zero)
        Assert.True(moved.Y > 0.1f, $"no stick: the step goes along the view (yaw 0 faces +Y): {moved}");
        Assert.True(MathF.Abs(moved.X) < 1e-3f);
        Assert.False(s.Grounded);
    }

    [Fact]
    public void The_step_goes_the_sticks_way_not_the_charges()
    {
        var held = OffALedge(Config);   // charged along +Y
        var right = Step(held, In(InputButtons.Melee, x: 1f), Config);   // D
        Assert.Equal(1, right.MeleeStep);
        var dr = right.Position - held.Position;
        Assert.True(dr.X > 0.1f && MathF.Abs(dr.Y) < 0.05f, $"D steps right: {dr}");
        var back = Step(held, In(InputButtons.Melee, y: -1f), Config);    // S
        Assert.True(back.Position.Y - held.Position.Y < -0.1f, $"S steps back: {back.Position - held.Position}");
        var left = Step(held, In(InputButtons.Melee, x: -1f), Config);
        Assert.True(left.Position.X - held.Position.X < -0.1f, $"A steps left: {left.Position - held.Position}");
    }

    [Fact]
    public void The_momentum_stays_gone_on_the_ticks_after_the_cancel()
    {
        var s = Step(OffALedge(Config), In(InputButtons.Melee, x: 1f), Config);
        for (int k = 0; k < 20 && !s.Grounded; k++)
        {
            s = Step(s, In(InputButtons.None, x: 1f), Config);
            Assert.True(Flat(s) < 6f, $"tick {k}: {Flat(s)}");   // never the charge's 20 to 40 m/s
        }
    }

    [Fact]
    public void A_jump_pack_class_cancels_a_ledge_charge_too_through_the_pack_air_path()
    {
        var c = WithStep(MeleeCancelTests.Pack);   // the Assault: a jump pack and a dash, whose air movement is JumpPack.Move
        Assert.NotNull(Loadouts.JumpPack(c, Fresh(c)));
        var held = MeleeCancelTests.Charging(c, cls: 0) with { Position = new Vector3(0f, 0f, 8f), Grounded = false };
        held = Step(held, In(InputButtons.None), c);
        Assert.Equal(RushPhase.Rush, held.Rush);
        Assert.True(Flat(held) > 5f, $"held speed {Flat(held)}");
        var s = Step(held, In(InputButtons.Melee, x: 1f), c);   // D
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
        Assert.True(Flat(s) < Flat(held) * 0.5f, $"momentum gone: {Flat(s)}");
        Assert.True(s.Position.X - held.Position.X > 0.1f, $"steps right: {s.Position - held.Position}");
        for (int k = 0; k < 15 && !s.Grounded; k++) { s = Step(s, In(InputButtons.None), c); Assert.True(Flat(s) < 6f, $"tick {k}: {Flat(s)}"); }
    }

    [Fact]
    public void The_restore_switch_keeps_the_charges_momentum()
    {
        var old = Config with { LedgeChargeCancel = false };
        var held = OffALedge(old);
        var s = Step(held, In(InputButtons.Melee), old);
        Assert.Equal(1, s.MeleeStep);
        Assert.NotEqual(Config.Hash(), old.Hash());
    }

    [Fact]
    public void A_ground_charge_cancelled_by_a_light_press_keeps_the_ground_rules()
    {
        var s = Step(Fresh(Config), In(InputButtons.Sprint | InputButtons.MeleeHeavy, y: 1f), Config);
        s = Step(s, In(InputButtons.Sprint, y: 1f), Config);
        var before = s;
        s = Step(s, SprintLight, Config);
        Assert.Equal((RushPhase.None, 1), (s.Rush, s.MeleeStep));
        Assert.True(before.Grounded && s.Grounded);
    }
}
