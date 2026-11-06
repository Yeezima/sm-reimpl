using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/melee-cancels (protocol 54; the owner, 2026-11-02): a melee press ends the Assault / Raptor hop and the bull rush of the pack and Tactical classes and swings at once, the next
/// swing waits for the cancelled clip's end; the Tactical roll and the Devastator / Havoc hop and charge are not cancelled; a pack class's Space press starts the jump pack while
/// falling and during a charge. Synthetic specs (the dash from <see cref="JumpPackDashTests"/>, the charge from <see cref="RushTests"/>).
/// </summary>
public class MeleeCancelTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    const InputButtons Charge = InputButtons.Sprint | InputButtons.MeleeHeavy;

    /// <summary>The Assault: the dash (hop) and the bull rush as well.</summary>
    internal static readonly SimConfig Pack = JumpPackDashTests.Config with
    {
        Classes = [JumpPackDashTests.Config.Classes[0] with { MeleeRush = RushTests.Spec }, JumpPackDashTests.Config.Classes[1]],
        JumpPacks = [JumpPackDashTests.Config.JumpPacks[0] with { HoldTiers = true }],   // the three jumps by the hold, and the input buffer (the install's)
        InputBufferWindow = 0.3f,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    /// <summary>RushTests' marine (a roll and the bull rush: the Tactical), and a Devastator beside it: the same with the hop.</summary>
    static readonly SimConfig Heavy = RushTests.Config with
    {
        Evades = [EvadeTests.Roll, EvadeTests.Hop],
        Classes = [RushTests.Config.Classes[0] with { Evade = 0 }, RushTests.Config.Classes[0] with { Evade = 1 }],
    };

    internal static PlayerState Fresh(SimConfig c, int cls = 0) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c);

    internal static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float my = 0f) =>
        Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out _);

    static PlayerState Hopping(int ticks = 3)
    {
        var s = Step(Fresh(Pack), Pack, InputButtons.Jump);
        s = Step(s, Pack, InputButtons.None);   // released before the decision tick: the dash
        Assert.True(Evade.Evading(s));
        for (int k = 0; k < ticks; k++) s = Step(s, Pack, InputButtons.None);
        return s;
    }

    internal static PlayerState Charging(SimConfig c, int cls, int ticks = 3)
    {
        var s = Step(Fresh(c, cls), c, Charge, my: 1f);
        Assert.Equal(RushPhase.Rush, s.Rush);
        for (int k = 0; k < ticks; k++) s = Step(s, c, InputButtons.None);
        Assert.Equal(RushPhase.Rush, s.Rush);
        return s;
    }

    [Fact]
    public void A_wait_still_running_keeps_the_hop_so_swing_hop_cancel_swing_is_no_loophole()
    {
        var s = Hopping() with { MeleeLock = 1f };
        s = Step(s, Pack, InputButtons.Melee);
        Assert.True(Evade.Evading(s));
        Assert.Equal(0, s.MeleeStep);
    }

    [Fact]
    public void Assault_a_melee_press_in_a_hop_ends_it_and_swings_at_once_then_the_cooldown_holds_the_next_swing()
    {
        var s = Hopping();
        var hop = JumpPackDashTests.Dash.Forward;
        float played = Evade.Time(s, Pack);
        s = Step(s, Pack, InputButtons.Melee);
        Assert.False(Evade.Evading(s));
        Assert.Equal(1, s.MeleeStep);                                                   // the swing starts in the press tick
        float expected = hop.Duration - played + hop.Landing!.Duration;                 // the hop's clip and its landing, from the press
        output.WriteLine($"hop played {played:F3} s, lock {s.MeleeLock:F3} s, expected about {expected:F3} s");
        Assert.InRange(s.MeleeLock, expected - 2 * Dt, expected + Dt);
        // the swing runs out; presses before the lock's end start nothing, a press after it does
        s = Step(s, Pack, InputButtons.None);
        int guard = 0;
        while (s.MeleeStep > 0 && guard++ < 200) s = Step(s, Pack, InputButtons.None);
        Assert.True(s.MeleeLock > 0f);
        var early = Step(Step(s, Pack, InputButtons.Melee), Pack, InputButtons.None);
        Assert.Equal(0, early.MeleeStep);
        int waited = 0;
        while (s.MeleeLock > 0f && waited++ < 400) s = Step(s, Pack, InputButtons.None);
        s = Step(Step(s, Pack, InputButtons.None), Pack, InputButtons.Melee);
        Assert.Equal(1, s.MeleeStep);
    }

    [Fact]
    public void Assault_a_swing_begun_in_the_air_falls_to_the_ground()
    {
        var s = Hopping(ticks: 6);
        s = Step(s, Pack, InputButtons.Melee);
        Assert.False(s.Grounded);
        for (int k = 0; k < 90 && !s.Grounded; k++) s = Step(s, Pack, InputButtons.None);
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.Position.Z, 2);
    }

    [Fact]
    public void Tactical_a_melee_press_in_a_charge_ends_it_and_swings_at_once_with_the_clips_remaining_time_as_the_lock()
    {
        var s = Charging(Heavy, cls: 0);
        float played = Rush.Time(s, Heavy);
        s = Step(s, Heavy, InputButtons.Melee);
        Assert.Equal(RushPhase.None, s.Rush);
        Assert.Equal(1, s.MeleeStep);
        float expected = RushTests.Spec.Rush.End - played;
        Assert.InRange(s.MeleeLock, expected - Dt, expected + Dt);
    }

    [Fact]
    public void Assault_a_melee_press_in_a_charge_ends_it_too()
    {
        var s = Charging(Pack, cls: 0);
        s = Step(s, Pack, InputButtons.Melee);
        Assert.Equal(RushPhase.None, s.Rush);
        Assert.Equal(1, s.MeleeStep);
        Assert.True(s.MeleeLock > 0.5f);
    }

    [Fact]
    public void Musou_mode_has_the_same_cooldown()
    {
        var musou = Heavy with { Extras = ContentExtras.Musou };
        var s = Charging(musou, cls: 0);
        s = Step(s, musou, InputButtons.Melee);
        Assert.Equal(1, s.MeleeStep);
        Assert.True(s.MeleeLock > 0.5f);
    }

    [Fact]
    public void Tactical_a_roll_is_not_cancelled_by_a_melee_press_before_its_data_branch()
    {
        var s = Step(Fresh(Heavy), Heavy, InputButtons.Jump);
        Assert.True(Evade.Evading(s));
        Assert.True(EvadeTests.Roll.Forward.MeleeAt > 4 * Dt);
        s = Step(Step(s, Heavy, InputButtons.None), Heavy, InputButtons.Melee);
        Assert.True(Evade.Evading(s));
        Assert.Equal(0, s.MeleeStep);
        Assert.Equal(0f, s.MeleeLock);
    }

    [Fact]
    public void Devastator_a_melee_press_in_a_charge_or_a_hop_does_not_cancel()
    {
        var s = Charging(Heavy, cls: 1);
        s = Step(s, Heavy, InputButtons.Melee);
        Assert.Equal(RushPhase.Rush, s.Rush);
        Assert.Equal(0f, s.MeleeLock);
        var h = Step(Fresh(Heavy, 1), Heavy, InputButtons.Jump);
        Assert.True(Evade.Evading(h));
        h = Step(Step(h, Heavy, InputButtons.None), Heavy, InputButtons.Melee);
        Assert.Equal(0f, h.MeleeLock);
        Assert.NotEqual(1, h.MeleeStep);
    }

    /// <summary>
    /// sim/air-jumps (owner, 2026-11-06): in Classic PvP a Space hold while falling no longer lifts the pack in the air (feel/melee-cancels' air start is Musou Mode's alone,
    /// MusouAirJumpTests); the tap in the air is still the hop (the air dash, sim/movement-fixes C).
    /// </summary>
    [Fact]
    public void Assault_holding_space_while_falling_in_classic_does_not_start_the_jump_pack()
    {
        Assert.False(Pack.JumpPackAirJumps);
        var phases = HoldWhileFalling(Pack, out float vz);
        output.WriteLine(string.Join(" ", phases));
        Assert.DoesNotContain(JumpPhase.Ascent, phases);
        Assert.True(vz <= 0f, $"vz {vz}");
    }

    internal static List<JumpPhase> HoldWhileFalling(SimConfig c, out float launchVz)
    {
        var s = Fresh(c) with { Position = new Vector3(0f, 0f, 10f), Grounded = false };
        var phases = new List<JumpPhase>();
        launchVz = 0f;
        for (int k = 0; k < 14; k++)
        {
            s = Step(s, c, InputButtons.Jump);
            phases.Add(s.Jump);
            if (s.Jump == JumpPhase.Ascent && launchVz == 0f) launchVz = s.Velocity.Z;
        }
        if (launchVz == 0f) launchVz = s.Velocity.Z;
        return phases;
    }

    /// <summary>sim/air-jumps: after a full jump's apex a Space hold does not jump again in Classic PvP, but a tap with a direction still hops (the air dash).</summary>
    [Fact]
    public void Assault_in_classic_gets_no_second_full_jump_after_the_apex_but_still_hops()
    {
        var (phases, hopped) = JumpThenAirPresses(Pack);
        output.WriteLine(string.Join(" ", phases));
        Assert.Equal(1, Launches(phases));       // the ground jump alone
        Assert.True(hopped, "the tap with D in the air hops (the dash)");
    }

    internal static int Launches(List<JumpPhase> phases) => Enumerable.Range(1, phases.Count - 1).Count(i => phases[i] == JumpPhase.Ascent && phases[i - 1] != JumpPhase.Ascent);

    /// <summary>A full jump (Space held), then in the fall a Space hold (a second full jump only where air jumps are allowed), then a D + Space tap (the hop).</summary>
    internal static (List<JumpPhase> Phases, bool Hopped) JumpThenAirPresses(SimConfig c)
    {
        var s = Fresh(c);
        var phases = new List<JumpPhase>();
        void Run(int ticks, InputButtons b, float mx = 0f)
        {
            for (int k = 0; k < ticks; k++) { s = Combat.StepPlayer(s, new PlayerInput(0, mx, 0f, 0f, b), c, out _); phases.Add(s.Jump); }
        }
        Run(20, InputButtons.Jump);                                     // the full jump, held
        int guard = 0;
        Run(2, InputButtons.None);                                      // Space let go
        while (s.Jump is not (JumpPhase.Hang or JumpPhase.Fall) && guard++ < 120) Run(1, InputButtons.None);
        Assert.False(s.Grounded);
        s = s with { Position = s.Position + new Vector3(0f, 0f, 20f) };   // high up, so the presses below all happen in the air
        Run(14, InputButtons.Jump);                                     // a hold in the air
        Run(2, InputButtons.None);
        guard = 0;
        while (s.Jump is not (JumpPhase.Hang or JumpPhase.Fall or JumpPhase.None) && guard++ < 200) Run(1, InputButtons.None);   // a tap in the ascent is not the hop
        Run(4, InputButtons.None);
        bool hopped = false;
        Run(1, InputButtons.Jump, 1f);                                  // D + Space tap
        for (int k = 0; k < 8 && !hopped; k++) { Run(1, InputButtons.None, 1f); hopped |= Evade.Evading(s); }
        return (phases, hopped);
    }

    [Fact]
    public void Assault_holding_space_in_a_charge_ends_it_and_starts_the_jump_pack()
    {
        var s = Charging(Pack, cls: 0);
        var phases = new List<JumpPhase>();
        for (int k = 0; k < 14; k++)
        {
            s = Step(s, Pack, InputButtons.Jump);
            phases.Add(s.Jump);
            if (k == 0) Assert.Equal(RushPhase.None, s.Rush);
        }
        Assert.Contains(JumpPhase.Ascent, phases);
    }

    [Fact]
    public void Assault_holding_space_in_a_charge_held_in_the_air_ends_it_but_in_classic_starts_no_jump()
    {
        var phases = ChargeHeldInTheAir(Pack);
        Assert.DoesNotContain(JumpPhase.Ascent, phases);   // sim/air-jumps: Classic PvP (Musou Mode: MusouAirJumpTests)
    }

    internal static List<JumpPhase> ChargeHeldInTheAir(SimConfig c)
    {
        var s = Charging(c, cls: 0) with { Position = new Vector3(0f, 0f, 8f), Grounded = false };
        s = Step(s, c, InputButtons.None);
        Assert.Equal(RushPhase.Rush, s.Rush);   // held in the air: input alone does not end it
        s = Step(s, c, InputButtons.Jump);
        Assert.Equal(RushPhase.None, s.Rush);
        var phases = new List<JumpPhase>();
        for (int k = 0; k < 14; k++) { s = Step(s, c, InputButtons.Jump); phases.Add(s.Jump); }
        return phases;
    }

    [Fact]
    public void Tactical_space_in_a_charge_does_not_start_a_jump_pack()
    {
        var s = Charging(Heavy, cls: 0);
        s = Step(s, Heavy, InputButtons.Jump);
        Assert.Equal(JumpPhase.None, s.Jump);
    }
}
