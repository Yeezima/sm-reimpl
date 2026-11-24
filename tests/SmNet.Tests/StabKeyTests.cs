using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// classic-stab-on-t (protocol 81; owner, 2026-11-17): the T key's direct stab. From rest it starts the weapon's stab strike (MeleeStep.Stab) with no precursor move, it is refused where a
/// heavy press is (stun, mid-attack), F + F chains into the stomp finisher and never the stab (sim/stab-key-only, owner 2026-11-23), a weapon without a stab ignores it, and the wire carries the bit. Synthetic timings.
/// </summary>
public class StabKeyTests
{
    static float T(float ticks) => ticks / 30f;

    static readonly ReactionEntry Stagger = new(0f, ReactionKind.Stagger, Backward: 1f, Duration: 0.2f);

    static MeleeStep Quick() => new MeleeStep(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), 0f, 0f, 3.5f)
        { YNext = 2, YComboOpen = T(14.5f), YComboClose = T(24.5f) };

    static MeleeStep Stab(bool marked = true) => new MeleeStep(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), 100f, 0f, 3.5f)
        { PrimaryReaction = Stagger, ShieldDamage = 50f, Stab = marked };

    /// <summary>sim/stab-key-only (owner 2026-11-23): the Y,Y finisher is the stomp again (a blast); the stab is the strike after it, which no chain names.</summary>
    internal static readonly SplashSpec StompBlast = new(7f, new DamageSpec(25f, 50f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(7f, 1f)], false, true, "target_all");
    static readonly MeleeStep StompFinisher = new(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), 0f, 0f, 3.5f) { Stomp = StompBlast };

    static SimConfig Config(bool marked = true) => new()
    {
        Classes =
        [
            new ClassLoadout("sword", -1, -1, [new MeleeStep(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 65f, 30f, 3.5f)]) { MeleeHeavy = [Quick(), StompFinisher, Stab(marked)], Side = Team.SpaceMarines },
            new ClassLoadout("target", -1, -1, [new MeleeStep(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 10f, 5f, 3.5f)]) { Side = Team.Chaos },
        ],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        HealthMax = 200f, ShieldMax = 300f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static World Make(bool marked = true)
    {
        var w = new World(Config(marked), null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f)]);
        w.Spawn(1, 0); w.Spawn(2, 1);
        return w;
    }

    static void Run(World w, int ticks, Func<int, InputButtons> buttons, float moveY = 0f, List<HitEvent>? hits = null, int from = 0)
    {
        for (int k = from; k < from + ticks; k++)
        {
            int tick = k;
            w.Step(id => new PlayerInput(0, 0f, id == 1 ? moveY : 0f, id == 1 ? 0f : MathF.PI, id == 1 ? buttons(tick) : InputButtons.None));
            hits?.AddRange(w.LastHits.Where(h => h.Shooter == 1));
        }
    }

    [Fact]
    public void T_from_idle_starts_the_stab_at_once_and_it_hits_100_after_50_off_the_shield()
    {
        var w = Make();
        var hits = new List<HitEvent>();
        Run(w, 1, _ => InputButtons.Stab, hits: hits);
        Assert.Equal((3, true), (w.Players[1].MeleeStep, w.Players[1].MeleeHeavy));   // heavy strike 3 (the stab, after the stomp finisher), not the first heavy strike
        Run(w, 59, _ => InputButtons.None, hits: hits, from: 1);
        var hit = Assert.Single(hits, h => h.Melee);
        Assert.Equal((2, 150f, 0f), (hit.Target, hit.ShieldDamage, hit.HealthDamage));
    }

    [Fact]
    public void T_starts_the_stab_from_a_run_as_well()
    {
        var w = Make();
        Run(w, 5, _ => InputButtons.None, moveY: 1f);
        Run(w, 1, _ => InputButtons.Stab, moveY: 1f, from: 5);
        Assert.Equal((3, true), (w.Players[1].MeleeStep, w.Players[1].MeleeHeavy));
    }

    [Fact]
    public void T_held_through_a_light_attack_stabs_once_it_is_over()
    {
        var w = Make();
        Run(w, 1, _ => InputButtons.Melee);
        Assert.Equal((1, false), (w.Players[1].MeleeStep, w.Players[1].MeleeHeavy));
        Run(w, 5, _ => InputButtons.Stab, from: 1);   // pressed while the swing runs: no new attack mid-swing
        Assert.False(w.Players[1].MeleeHeavy);
        bool stabbed = false;
        for (int k = 6; k < 80 && !stabbed; k++)
        {
            Run(w, 1, _ => InputButtons.Stab, from: k);   // the bit still held when the swing is over (the client's pulse buffer)
            stabbed = w.Players[1].MeleeHeavy && w.Players[1].MeleeStep == 3;
        }
        Assert.True(stabbed);
    }

    [Fact]
    public void A_press_in_the_middle_of_a_strike_is_not_queued_or_turned_into_the_stab()
    {
        var w = Make();
        Run(w, 1, _ => InputButtons.MeleeHeavy);   // heavy strike 1
        Run(w, 3, _ => InputButtons.Stab, from: 1);
        Assert.Equal((1, true), (w.Players[1].MeleeStep, w.Players[1].MeleeHeavy));
    }

    [Fact]
    public void T_is_refused_in_a_stun_like_a_heavy_press()
    {
        var c = Config() with { Reactions = Reaction.Placeholders };
        PlayerState Stunned() => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c) with { Reaction = ReactionKind.Stun, ReactionDirection = ReactionDirection.Front, ReactionTicks = 0 };
        foreach (var button in new[] { InputButtons.Stab, InputButtons.MeleeHeavy })
        {
            var s = Stunned();
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, button), c, out _);
            Assert.Equal(0, s.MeleeStep);
        }
        var free = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c);   // control: the same press starts the stab with no stun
        free = Combat.StepPlayer(free, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Stab), c, out _);
        Assert.Equal(3, free.MeleeStep);
    }

    [Fact]
    public void A_weapon_without_a_stab_ignores_T()
    {
        var w = Make(marked: false);
        Run(w, 10, _ => InputButtons.Stab);
        Assert.Equal(0, w.Players[1].MeleeStep);
    }

    [Fact]
    public void F_then_F_chains_into_the_stomp_finisher_and_never_the_stab()
    {
        // sim/stab-key-only (owner 2026-11-23): was F_then_F_still_chains_into_the_same_stab_strike.
        var w = Make();
        Run(w, 21, k => k == 0 || k == 20 ? InputButtons.MeleeHeavy : InputButtons.None);
        Assert.Equal((2, true), (w.Players[1].MeleeStep, w.Players[1].MeleeHeavy));
        Assert.NotNull(Combat.MeleeStepOf(w.Config, w.Players[1])!.Value.Stomp);
        Assert.False(Combat.MeleeStepOf(w.Config, w.Players[1])!.Value.Stab);
    }

    [Fact]
    public void The_stab_bit_survives_the_wire()
    {
        Assert.Equal(InputButtons.Stab, Protocol.Quantize(new PlayerInput(1, 0, 0, 0, InputButtons.Stab)).Buttons);
        Assert.Equal(90, Protocol.Version);
    }

    [Fact]
    public void The_pulse_holds_the_bit_four_ticks_after_a_press_and_not_while_the_key_stays_down()
    {
        var p = new StabPulse();
        var bits = new List<bool>();
        for (int i = 0; i < 8; i++) bits.Add(p.Sample(true));
        Assert.Equal([true, true, true, true, false, false, false, false], bits);
        Assert.False(p.Sample(false));
        Assert.True(p.Sample(true));   // a new press
    }
}

/// <summary>classic-stab-on-t: the controls' side (client files linked into the tests).</summary>
public class StabKeyControlsTests
{
    [Theory]
    [InlineData(ContentExtras.None, false)]
    [InlineData(ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks, false)]
    [InlineData(ContentExtras.Musou, true)]
    public void The_T_voice_call_is_off_in_Classic_PvP_and_kept_in_Musou_Mode(ContentExtras extras, bool live) =>
        Assert.Equal(live, SmClient.ControlActions.ShoutKeyLive(extras));

    [Fact]
    public void T_is_the_stab_by_default_and_an_old_file_that_gave_T_to_the_shout_loses_the_shout()
    {
        var d = SmClient.BindingSet.Defaults();
        Assert.Equal("Key:T", d.Get(SmClient.BindDevice.Keyboard, SmClient.GameAction.Stab, 0));
        Assert.Null(d.Get(SmClient.BindDevice.Keyboard, SmClient.GameAction.Shout, 0));
        var old = SmClient.BindingSet.FromJson("{\"version\":1,\"keyboard\":{\"Shout\":[\"Key:T\",null]},\"controller\":{}}", out _);
        Assert.Equal("Key:T", old.Get(SmClient.BindDevice.Keyboard, SmClient.GameAction.Stab, 0));
        Assert.Null(old.Get(SmClient.BindDevice.Keyboard, SmClient.GameAction.Shout, 0));
    }

    [Fact]
    public void The_stab_action_samples_the_stab_button()
    {
        var s = SmClient.BindingSet.Sample(a => a == SmClient.GameAction.Stab, captured: true);
        Assert.Equal(InputButtons.Stab, s.Buttons);
    }
}
