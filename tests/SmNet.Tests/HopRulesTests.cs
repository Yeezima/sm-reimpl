using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/hop-rules (protocol 72; the owner, 2026-11-14): for the Assault Marine and the Raptor, (1) a full jump out of a charge held in the air (a charge off a ledge), in Classic PvP and Musou Mode;
/// (2) a hop cuts the melee wait still running (<see cref="HopTuning.HopLock"/>) so swing > hop > swing flows, hop-spamming being held by the floor and the hop's own spacing; (3) a Jump press ends the aerial
/// tackle and starts the hop in the same tick. Synthetic specs (<see cref="MeleeCancelTests.Pack"/>, <see cref="JumpPackAttackTests.Config"/>); the restore switch is <see cref="SimConfig.HopRules"/> false.
/// </summary>
public class HopRulesTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    static readonly SimConfig Classic = MeleeCancelTests.Pack;
    static readonly SimConfig Old = MeleeCancelTests.Pack with { HopRules = false };
    static readonly SimConfig Tackle = JumpPackAttackTests.Config;

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float mx = 0f, float my = 0f) => Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b), c, out _);

    // ---- (1) the ledge jump after a charge ----

    static PlayerState ChargedOffALedge(SimConfig c)
    {
        var s = MeleeCancelTests.Charging(c, cls: 0) with { Position = new Vector3(0f, 0f, 8f), Grounded = false };
        s = Step(s, c, InputButtons.None);
        Assert.Equal(RushPhase.Rush, s.Rush);   // held in the air
        return s;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_held_jump_after_charging_off_a_ledge_is_a_full_jump_in_classic_and_musou(bool musou)
    {
        if (musou && !ContentExtras.Compiled) return;
        var c = musou ? Classic with { Extras = ContentExtras.Musou } : Classic;
        var s = ChargedOffALedge(c);
        s = Step(s, c, InputButtons.Jump);
        Assert.Equal(RushPhase.None, s.Rush);
        Assert.True(musou || s.JumpAirOpen, "the press opens the air jump");
        float peak = s.Position.Z;
        var phases = new List<JumpPhase>();
        for (int k = 0; k < 24; k++) { s = Step(s, c, InputButtons.Jump); phases.Add(s.Jump); peak = MathF.Max(peak, s.Position.Z); }
        output.WriteLine($"musou {musou}: {string.Join(" ", phases)}, peak {peak:F2} m");
        Assert.Contains(JumpPhase.Ascent, phases);
        Assert.True(peak > 8f + 2f, $"a real jump rises well above the ledge: {peak}");
        Assert.False(s.JumpAirOpen, "the jump used the opening");
    }

    [Fact]
    public void The_restore_switch_gives_the_protocol_71_fall_in_classic()
    {
        var s = ChargedOffALedge(Old);
        var phases = new List<JumpPhase>();
        for (int k = 0; k < 20; k++) { s = Step(s, Old, InputButtons.Jump); phases.Add(s.Jump); }
        Assert.DoesNotContain(JumpPhase.Ascent, phases);
        Assert.False(s.JumpAirOpen);
    }

    [Fact]
    public void A_tap_after_charging_off_a_ledge_is_still_the_hop()
    {
        var s = ChargedOffALedge(Classic);
        s = Step(s, Classic, InputButtons.Jump, mx: 1f);
        bool hopped = false;
        for (int k = 0; k < 8 && !hopped; k++) { s = Step(s, Classic, InputButtons.None, mx: 1f); hopped |= Evade.Evading(s); }
        Assert.True(hopped);
        Assert.False(s.JumpAirOpen);
    }

    [Fact]
    public void The_opening_closes_on_landing_and_a_plain_fall_never_has_it()
    {
        var fall = MeleeCancelTests.Fresh(Classic) with { Position = new Vector3(0f, 0f, 10f), Grounded = false };
        for (int k = 0; k < 20; k++) { fall = Step(fall, Classic, InputButtons.Jump); Assert.False(fall.JumpAirOpen); Assert.NotEqual(JumpPhase.Ascent, fall.Jump); }
        var open = ChargedOffALedge(Classic);
        open = Step(open, Classic, InputButtons.Jump);
        open = Step(open, Classic, InputButtons.Jump);   // still held: no hop yet, the opening stays
        Assert.True(open.JumpAirOpen);
        for (int k = 0; k < 90 && !open.Grounded; k++) open = Step(open, Classic, InputButtons.None);
        Assert.True(open.Grounded);
        Assert.False(open.JumpAirOpen);
    }

    [Fact]
    public void The_open_air_jump_travels_in_the_own_record()
    {
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, false) { Alive = true, Jump = JumpPhase.None, JumpAirOpen = true, JumpPower = 45f, Grounded = false };
        var own = Protocol.Write(new Snapshot(1, 1, 1, [me], Self: 3));
        Assert.Equal(me, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0]);
        var closed = Protocol.Write(new Snapshot(1, 1, 1, [me with { JumpAirOpen = false }], Self: 3));
        Assert.False(Assert.IsType<Snapshot>(Protocol.Read(closed)).Players[0].JumpAirOpen);
        Assert.NotEqual(own, closed);
        Assert.NotEqual(Classic.Hash(), Old.Hash());
    }

    // ---- (2) a hop shortens the melee wait ----

    /// <summary>Ticks from the hop's start to the next swing, with a melee press repeated each other tick (swing, hop, swing); also the swing's wait at the hop.</summary>
    (int Ticks, float LockAtHop) SwingHopSwing(SimConfig c)
    {
        var s = Step(MeleeCancelTests.Fresh(c), c, InputButtons.Melee);
        Assert.Equal(1, s.MeleeStep);
        s = Step(s, c, InputButtons.None);
        s = Step(s, c, InputButtons.Jump);
        int guard = 0;
        while (!Evade.Evading(s) && guard++ < 12) s = Step(s, c, InputButtons.None);   // released before the decision tick: the hop
        Assert.True(Evade.Evading(s), "the hop started");
        float lockAtHop = s.MeleeLock;
        int ticks = 0;
        while (ticks < 120)
        {
            s = Step(s, c, ticks % 2 == 0 ? InputButtons.Melee : InputButtons.None);
            ticks++;
            if (s.MeleeStep == 1 && !Evade.Evading(s)) break;
        }
        Assert.Equal(1, s.MeleeStep);
        return (ticks, lockAtHop);
    }

    [Fact]
    public void A_hop_after_a_swing_cuts_the_wait_so_the_follow_up_swing_comes_sooner_but_not_at_once()
    {
        var (on, lockOn) = SwingHopSwing(Classic);
        var (off, lockOff) = SwingHopSwing(Old);
        output.WriteLine($"with the rule: lock at the hop {lockOn:F3} s, next swing after {on} ticks; before: lock {lockOff:F3} s, {off} ticks");
        Assert.True(lockOff > HopTuning.LockFloor + 0.1f, "the synthetic swing's wait is long enough to cut");
        Assert.Equal(HopTuning.HopLock(lockOff), lockOn, 3);
        Assert.True(on < off, "sooner");
        Assert.True(on * Dt >= HopTuning.LockFloor - Dt, $"not at once: {on * Dt:F3} s");
    }

    [Fact]
    public void HopLock_halves_the_wait_with_a_floor_and_leaves_short_waits_alone()
    {
        Assert.Equal(0f, HopTuning.HopLock(0f));
        Assert.Equal(0.1f, HopTuning.HopLock(0.1f));
        Assert.Equal(HopTuning.LockFloor, HopTuning.HopLock(HopTuning.LockFloor));
        Assert.Equal(HopTuning.LockFloor, HopTuning.HopLock(0.4f));   // half is 0.2: the floor
        Assert.Equal(0.5f, HopTuning.HopLock(1f), 4);
    }

    [Fact]
    public void Hop_spamming_between_swings_stays_inside_the_cap()
    {
        // swing, hop, swing, hop ... as fast as the rules allow: a hop (a tap) right after each swing, a melee press every other tick.
        var c = Classic;
        var s = Step(MeleeCancelTests.Fresh(c), c, InputButtons.Melee);
        var starts = new List<int> { 0 };
        int tick = 0;
        for (; tick < 400 && starts.Count < 7; tick++)
        {
            var b = (tick % 4) switch { 0 => InputButtons.Jump, 1 => InputButtons.None, _ => tick % 2 == 0 ? InputButtons.Melee : InputButtons.None };
            var before = s;
            s = Step(s, c, b);
            if (s.MeleeStep == 1 && (before.MeleeStep != 1 || before.MeleeTime > s.MeleeTime)) starts.Add(tick + 1);
        }
        var gaps = starts.Zip(starts.Skip(1), (a, b) => (b - a) * Dt).ToArray();
        output.WriteLine($"swing gaps with hops: {string.Join(" ", gaps.Select(g => g.ToString("F2")))} s; floor {HopTuning.LockFloor}, hop spacing {c.Evades[0].MinInterval:F2} s");
        Assert.True(gaps.Length >= 4, "the loop produced swings");
        Assert.All(gaps, g => Assert.True(g >= HopTuning.LockFloor, $"gap {g}"));
        // The hop's own spacing (a hop per swing) holds the cadence: never faster than the spacing allows.
        Assert.All(gaps.Skip(1), g => Assert.True(g >= c.Evades[0].MinInterval - 3 * Dt, $"gap {g} under the hop spacing"));
    }

    // ---- (3) a hop cancels the aerial tackle ----

    static PlayerState Tackling(SimConfig c, int wait)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);
        s = Step(s, c, InputButtons.Jump, 0f, 1f);
        s = Step(s, c, InputButtons.None, 0f, 1f);
        while (s.EvadeTicks < 10) s = Step(s, c, InputButtons.None);
        s = Step(s, c, InputButtons.Melee);
        Assert.Equal(RushPhase.DashAttack, s.Rush);
        for (int k = 0; k < wait; k++) s = Step(s, c, InputButtons.None);
        return s;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void A_jump_press_in_the_aerial_tackle_ends_it_and_hops_in_the_same_tick(int wait)
    {
        var s = Tackling(Tackle, wait);
        Assert.Equal(RushPhase.DashAttack, s.Rush);
        Assert.True(s.EvadeLock > 0, "the hop's spacing is still running: the old rule refused the hop");
        var hop = Step(s, Tackle, InputButtons.Jump, mx: 0f, my: 1f);
        Assert.Equal(RushPhase.None, hop.Rush);
        Assert.True(Evade.Evading(hop), "the hop started in the press tick");
        Assert.Equal(1, hop.EvadeTicks);
        Assert.Equal(0, hop.MeleeStep);
        float z = hop.Position.Z;
        for (int k = 0; k < 6; k++) hop = Step(hop, Tackle, InputButtons.None, my: 1f);
        Assert.True(Evade.Evading(hop));
        output.WriteLine($"wait {wait}: z {z:F2} -> {hop.Position.Z:F2}, velocity {hop.Velocity}, lock {hop.EvadeLock}");
    }

    [Fact]
    public void The_restore_switch_keeps_the_protocol_71_behaviour_no_hop_in_the_tackle()
    {
        var c = Tackle with { HopRules = false };
        var s = Tackling(c, 2);
        var next = Step(s, c, InputButtons.Jump, my: 1f);
        Assert.False(Evade.Evading(next));
    }

    [Fact]
    public void A_hop_cancelled_tackle_can_swing_at_once_by_the_ordinary_cancel()
    {
        var s = Tackling(Tackle, 3);
        s = Step(s, Tackle, InputButtons.Jump, my: 1f);
        Assert.True(Evade.Evading(s));
        s = Step(s, Tackle, InputButtons.Melee, my: 1f);
        Assert.False(Evade.Evading(s));
        Assert.Equal(1, s.MeleeStep);
    }
}
