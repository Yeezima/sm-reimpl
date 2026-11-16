using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Owner's batch 2026-11-06 (late), stream A, protocol 62. (1) sim/jump-escape: the Assault / Raptor's Jump press ends any reaction, attack, throw, charge phase, landing lock or
/// pound recovery at once, in every mode. (2) sim/hop-cancel-momentum: a hop cancelled by a melee press keeps no upward speed and carries <see cref="MeleeCancel.HopCarry"/> of its
/// horizontal speed under the air swing. (3) sim/descent-recharge: in Classic PvP, with the data's fuel (100, costs 35 / 55, 10 per s), two full jumps fit back to back and a third
/// follows shortly after. Synthetic specs (<see cref="MeleeCancelTests.Pack"/>: the install-shaped dash; <see cref="JumpPackTests.Pack"/>: the install's jump_info values).
/// </summary>
public class JumpEscapeTests(ITestOutputHelper output)
{
    const int Assault = 0, Tactical = 1;

    static readonly ReactionClip Long = new("stun", 2f, 2f, 2f, 2f, 2f, 0.5f);   // no early branch: only its end lets go
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly ThrownSpec Frag = new("frag", 2, 2, 1f, 10f, new ProjectileSpec("ebps\\test\\frag", 20f, 3f, "raycast", 0f, [], true, true, false, 0, 0f, 0f, 0, null,
        new SplashSpec(3f, Plain with { Damage = 1f }, [], true, false, "target_all")));

    /// <summary>The Assault (class 0, the dash and the bull rush) with the data's pack (max_power 100), a holding stun and a stagger, and a frag; the Tactical beside it.</summary>
    static readonly SimConfig Classic = MeleeCancelTests.Pack with
    {
        JumpPacks = [MeleeCancelTests.Pack.JumpPacks[0] with { MaxPower = 100f }],
        Reactions = [new ReactionSpec(ReactionKind.Stun, false, Long, Long, Long, Long), new ReactionSpec(ReactionKind.Stagger, false, Long, Long, Long, Long)],
        Thrown = [Frag],
        Classes = [MeleeCancelTests.Pack.Classes[0] with { EquipmentChoices = [0] }, MeleeCancelTests.Pack.Classes[1] with { EquipmentChoices = [0] }],
    };

    static PlayerState Fresh(SimConfig c, int cls = Assault) => MeleeCancelTests.Fresh(c, cls);
    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float my = 0f) => MeleeCancelTests.Step(s, c, b, my);

    static PlayerState Reacting(SimConfig c, ReactionKind kind, int cls = Assault)
    {
        var s = Fresh(c, cls) with { Reaction = kind, ReactionTicks = 1 };
        s = Step(s, c, InputButtons.None);
        Assert.Equal(kind, s.Reaction);
        return s;
    }

    // ---- (1) the jump escape ----

    [Theory]
    [InlineData(ReactionKind.Stun)]
    [InlineData(ReactionKind.Stagger)]
    public void Assault_jumps_out_of_a_holding_reaction_at_once(ReactionKind kind)
    {
        var s = Step(Reacting(Classic, kind), Classic, InputButtons.Jump);
        Assert.Equal(ReactionKind.None, s.Reaction);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);   // the press starts the take-off in the same tick (a tap: the hop, a hold: the jump)
        // A hold lifts off: the escape is a real jump, not just the end of the clip.
        for (int k = 0; k < 20; k++) s = Step(s, Classic, InputButtons.Jump);
        Assert.True(s.Position.Z > 1f, $"z {s.Position.Z}");
    }

    [Fact]
    public void A_tactical_stays_stunned_by_a_jump_press()
    {
        var s = Step(Reacting(Classic, ReactionKind.Stun, Tactical), Classic, InputButtons.Jump);
        Assert.Equal(ReactionKind.Stun, s.Reaction);
    }

    [Fact]
    public void Musou_mode_jumps_out_of_a_stun_too()
    {
        if (!ContentExtras.Compiled) return;   // a build without added content has no Musou Mode (faithful-build)
        var musou = Classic with { Extras = ContentExtras.Musou };
        var s = Step(Reacting(musou, ReactionKind.Stun), musou, InputButtons.Jump);
        Assert.Equal(ReactionKind.None, s.Reaction);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
    }

    [Fact]
    public void Assault_jumps_out_of_a_swing_on_its_first_tick()
    {
        var s = Step(Fresh(Classic), Classic, InputButtons.Melee);
        Assert.Equal(1, s.MeleeStep);
        s = Step(s, Classic, InputButtons.None);
        s = Step(s, Classic, InputButtons.Jump);
        Assert.Equal(0, s.MeleeStep);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
    }

    [Fact]
    public void Assault_jumps_out_of_a_throw_before_its_release_and_spends_nothing()
    {
        var s = Step(Fresh(Classic), Classic, InputButtons.Grenade);
        Assert.True(s.Throwing);
        s = Step(s, Classic, InputButtons.Jump);
        Assert.False(s.Throwing);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
        for (int k = 0; k < 30; k++) s = Step(s, Classic, InputButtons.Jump);
        Assert.Equal(2, s.Grenades);
        // Control: the Tactical's throw runs on.
        var t = Step(Fresh(Classic, Tactical), Classic, InputButtons.Grenade);
        t = Step(t, Classic, InputButtons.Jump);
        Assert.True(t.Throwing);
    }

    [Theory]
    [InlineData(JumpPhase.Landed)]
    [InlineData(JumpPhase.Recover)]
    public void Assault_jumps_out_of_a_landing_or_a_pound_recovery(JumpPhase phase)
    {
        var s = Fresh(Classic) with { Jump = phase, JumpTicks = 1 };
        s = Step(s, Classic, InputButtons.Jump);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
    }

    [Fact]
    public void Assault_jumps_out_of_the_charge_collision_stop()
    {
        var s = Fresh(Classic) with { Rush = RushPhase.Collision, RushTicks = 1 };
        s = Step(s, Classic, InputButtons.None);
        Assert.Equal(RushPhase.Collision, s.Rush);
        s = Step(s, Classic, InputButtons.Jump);
        Assert.Equal(RushPhase.None, s.Rush);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
    }

    // ---- (2) the hop cancelled by melee ----

    [Fact]
    public void A_hop_cancelled_by_melee_rises_no_more_and_moves_on_horizontally()
    {
        var s = Step(Fresh(Classic), Classic, InputButtons.Jump, my: 1f);
        s = Step(s, Classic, InputButtons.None, my: 1f);   // released before the decision tick: the hop
        Assert.True(Evade.Evading(s));
        for (int k = 0; k < 2; k++) s = Step(s, Classic, InputButtons.None);
        Assert.True(s.Velocity.Z > 0f, "still rising before the cancel");
        float z0 = s.Position.Z, y0 = s.Position.Y;
        s = Step(s, Classic, InputButtons.Melee);
        Assert.False(Evade.Evading(s));
        Assert.Equal(1, s.MeleeStep);
        Assert.True(s.Position.Z <= z0 + 1e-4f, $"rose {s.Position.Z - z0:F3} m in the press tick");
        float top = s.Position.Z, lastY = s.Position.Y;
        int air = 0;
        for (int k = 0; k < 30 && !s.Grounded; k++)
        {
            s = Step(s, Classic, InputButtons.None);
            top = MathF.Max(top, s.Position.Z);
            Assert.True(s.Position.Y > lastY, $"tick {k}: no forward motion in the air");
            lastY = s.Position.Y; air++;
        }
        output.WriteLine($"cancel at z {z0:F2}; air ticks {air}; forward in the air {lastY - y0:F2} m");
        Assert.True(top <= z0 + 1e-4f, $"rose {top - z0:F3} m after the cancel");
        Assert.True(s.Grounded);
        float carry = MeleeCancel.HopCarry * 16.37f;
        Assert.True(lastY - y0 >= carry * air * (1f / 30f) * 0.9f, "the carried speed rides on in the air");
    }

    // ---- (3) the fuel ----

    /// <summary>A full jump (Jump held through the hold), from the press to the landing; returns its apex height and the power at the press.</summary>
    static (float Apex, float PowerBefore) FullJump(ref PlayerState s, SimConfig c)
    {
        float before = s.JumpPower, apex = 0f;
        s = Step(s, c, InputButtons.Jump);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
        for (int k = 0; k < 20; k++) { s = Step(s, c, InputButtons.Jump); apex = MathF.Max(apex, s.Position.Z); }
        for (int k = 0; k < 300 && !(s.Grounded && s.Jump is JumpPhase.Landed or JumpPhase.None); k++) { s = Step(s, c, InputButtons.None); apex = MathF.Max(apex, s.Position.Z); }
        Assert.True(s.Grounded);
        return (apex, before);
    }

    [Fact]
    public void Classic_two_full_jumps_back_to_back_then_a_third_shortly_after()
    {
        Assert.True(Classic.JumpPackDescentRecharge);
        var pack = Loadouts.JumpPack(Classic, Assault)!;
        Assert.Equal((100f, 10f, 35f, 55f), (pack.MaxPower, pack.RechargeRate, pack.CostAt(0f), pack.CostAt(pack.MaxHold)));
        var s = Fresh(Classic);
        Assert.Equal(100f, s.JumpPower);
        var first = FullJump(ref s, Classic);
        var second = FullJump(ref s, Classic);   // pressed on the landing tick (the escape cuts the landing lock)
        output.WriteLine($"apex {first.Apex:F2} / {second.Apex:F2} m; power at the presses {first.PowerBefore:F1} / {second.PowerBefore:F1}; after {s.JumpPower:F1}");
        Assert.True(second.PowerBefore >= 55f, $"second press with {second.PowerBefore}");
        Assert.InRange(second.Apex, first.Apex - 0.05f, first.Apex + 0.05f);   // both full height
        // The third: a short jump's 35 within 2 s of the second landing, a full one's 55 within 4 s.
        int toShort = 0, toFull = 0;
        for (int k = 1; k <= 300; k++)
        {
            s = Step(s, Classic, InputButtons.None);
            if (toShort == 0 && s.JumpPower + 1e-3f >= 35f) toShort = k;
            if (toFull == 0 && s.JumpPower + 1e-3f >= 55f) { toFull = k; break; }
        }
        output.WriteLine($"third jump: short after {toShort / 30f:F2} s, full after {toFull / 30f:F2} s");
        Assert.InRange(toShort, 1, 60);
        Assert.InRange(toFull, toShort, 120);
    }

    [Fact]
    public void Without_the_descent_recharge_the_second_full_jump_falls_short()
    {
        // The rule's control: the same 100 recharging only outside a jump (Musou Mode's rule, the pre-protocol-62 one) cannot pay a second full jump.
        if (!ContentExtras.Compiled) return;
        var ground = Classic with { Extras = ContentExtras.Musou };
        Assert.False(ground.JumpPackDescentRecharge);
        Assert.Equal(100f, Loadouts.JumpPack(ground, Assault)!.MaxPower);   // no DataMaxPower on this synthetic pack: Musou keeps the spec's 100
        var s = Fresh(ground);
        var first = FullJump(ref s, ground);
        var second = FullJump(ref s, ground);
        output.WriteLine($"ground-only: apex {first.Apex:F2} / {second.Apex:F2} m; power at the second press {second.PowerBefore:F1}");
        Assert.True(second.PowerBefore < 55f);
        Assert.True(second.Apex < first.Apex - 0.5f);
    }

    [Fact]
    public void Musou_mode_keeps_the_ground_only_recharge()
    {
        if (!ContentExtras.Compiled) return;
        Assert.False((Classic with { Extras = ContentExtras.Musou }).JumpPackDescentRecharge);
        Assert.True((Classic with { Extras = ContentExtras.Orks | ContentExtras.Gore }).JumpPackDescentRecharge);   // extended Classic PvP too
    }
}
