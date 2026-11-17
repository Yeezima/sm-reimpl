using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/lascannon-shot-first (owner 2026-11-17, Classic PvP): a hop pressed with a lascannon shot due waits for the shot, at most 0.1 s (3 ticks at 30 Hz); a hop with no shot due, other weapons and
/// Musou Mode start the hop in the press tick as before.
/// </summary>
public class LascannonShotFirstTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Lascannon = new("sm_mp_exotic_las_cannon", WeaponSlot.Ranged, TriggerKind.SemiAuto, 150f, 1f, 4, 20, 2.25f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f) { AlwaysReady = true };
    static readonly WeaponSpec Lowered = Lascannon with { AlwaysReady = false, RaiseDelay = 0.2f };
    static readonly WeaponSpec Bolter = Lascannon with { Name = "sm_mp_ranged_bolter" };

    static SimConfig Make(WeaponSpec w) => new()
    {
        Weapons = [w, StompUninterruptibleTests.BoltPistol],
        Thrown = [GrenadeTests.Frag],
        Classes = [new ClassLoadout("devastator", 0, 1, [StompUninterruptibleTests.Kick]) { Evade = 0, EquipmentChoices = [0], Side = Team.SpaceMarines }],
        Evades = [EvadeTests.Hop],
        HealthMax = 1e6f, InputBufferWindow = 0.3f,
    };
    static readonly SimConfig Config = Make(Lascannon);

    static PlayerState Fresh(SimConfig c, int ready = 300) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Team = Team.SpaceMarines, Class = 0 }, c) with { ReadyTicks = ready };

    static (PlayerState S, SelfEvents E) Step(SimConfig c, PlayerState s, InputButtons b)
    {
        var next = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, b), c, out var e);
        return (next, e);
    }

    /// <summary>Runs <paramref name="first"/> for tick 0 then <paramref name="rest"/>; returns the first tick (0-based) with a shot and the first with a hop.</summary>
    (int Shot, int Hop) Run(SimConfig c, PlayerState s, InputButtons first, InputButtons rest, int ticks = 12)
    {
        int shot = -1, hop = -1;
        for (int i = 0; i < ticks; i++)
        {
            var (n, e) = Step(c, s, i == 0 ? first : rest);
            if (shot < 0 && e.Fired) shot = i;
            if (hop < 0 && n.EvadeTicks > 0) hop = i;
            s = n;
        }
        output.WriteLine($"shot tick {shot}, hop tick {hop}");
        return (shot, hop);
    }

    [Fact]
    public void Fire_and_Jump_together_shoots_first_and_hops_one_tick_later()
    {
        var (shot, hop) = Run(Config, Fresh(Config), InputButtons.Fire | InputButtons.Jump, InputButtons.Jump);
        Assert.Equal(0, shot);
        Assert.Equal(1, hop);
        Assert.True(hop * Config.TickSeconds <= 0.1f + 1e-4f);
    }

    [Fact]
    public void A_tap_of_Jump_with_the_shot_is_not_lost()
    {
        var (shot, hop) = Run(Config, Fresh(Config), InputButtons.Fire | InputButtons.Jump, InputButtons.None);
        Assert.Equal(0, shot);
        Assert.Equal(1, hop);
    }

    [Fact]
    public void Without_the_fix_the_hop_swallows_the_shot()
    {
        var old = Config with { LascannonShotFirst = false };
        var (shot, hop) = Run(old, Fresh(old), InputButtons.Fire | InputButtons.Jump, InputButtons.Jump);
        Assert.Equal(-1, shot);
        Assert.Equal(0, hop);
        Assert.NotEqual(Config.Hash(), old.Hash());
    }

    [Fact]
    public void A_hop_with_no_shot_pending_is_not_delayed()
    {
        Assert.Equal((-1, 0), Run(Config, Fresh(Config), InputButtons.Jump, InputButtons.None));
        // a Fire press that cannot shoot (cooldown running, or an empty clip) delays nothing either
        var cooling = Fresh(Config) with { Cooldown = 5f };
        Assert.Equal(0, Run(Config, cooling, InputButtons.Fire | InputButtons.Jump, InputButtons.None).Hop);
        var empty = Fresh(Config) with { Clip = 0, Reserve = 0 };
        Assert.Equal(0, Run(Config, empty, InputButtons.Fire | InputButtons.Jump, InputButtons.None).Hop);
    }

    [Fact]
    public void A_shot_already_out_does_not_delay_a_later_hop()
    {
        var s = Step(Config, Fresh(Config), InputButtons.Fire).S;
        var (n, _) = Step(Config, s, InputButtons.Fire | InputButtons.Jump);
        Assert.True(n.EvadeTicks > 0);
    }

    [Fact]
    public void A_raise_waiting_for_the_shot_holds_the_hop_but_never_past_0_1_s()
    {
        var c = Make(Lowered);
        var (_, hop) = Run(c, Fresh(c, 0), InputButtons.Fire | InputButtons.Jump, InputButtons.Fire | InputButtons.Jump, 30);
        Assert.InRange(hop, 1, LascannonShotFirst.HoldTicks(c));   // delayed, never more than 3 ticks
        Assert.True(hop * c.TickSeconds <= 0.1f + 1e-4f);
        // a short raise (2 ticks) lets the shot out before the hop
        var quick = Make(Lowered with { RaiseDelay = 0.06f });
        var (shot2, hop2) = Run(quick, Fresh(quick, 0), InputButtons.Fire | InputButtons.Jump, InputButtons.Jump, 30);
        Assert.True(shot2 >= 0 && shot2 < hop2, $"shot {shot2} hop {hop2}");
        Assert.True(hop2 * quick.TickSeconds <= 0.1f + 1e-4f);
    }

    [Fact]
    public void Other_weapons_and_Musou_Mode_hop_at_once()
    {
        var bolter = Make(Bolter);
        Assert.Equal(0, Run(bolter, Fresh(bolter), InputButtons.Fire | InputButtons.Jump, InputButtons.Jump).Hop);
        var musou = Config with { Extras = ContentExtras.Musou };
        Assert.Equal(0, Run(musou, Fresh(musou), InputButtons.Fire | InputButtons.Jump, InputButtons.Jump).Hop);
    }

    [Fact]
    public void Two_copies_fed_the_same_inputs_stay_identical()
    {
        // The server and the client's prediction run the same StepPlayer, so the held-back press cannot drift between them.
        var a = Fresh(Config); var b = Fresh(Config);
        var inputs = new[] { InputButtons.Fire | InputButtons.Jump, InputButtons.Jump, InputButtons.None, InputButtons.Fire, InputButtons.None, InputButtons.None };
        foreach (var i in inputs) { a = Step(Config, a, i).S; b = Step(Config, b, i).S; Assert.Equal(a, b); }
    }
}
