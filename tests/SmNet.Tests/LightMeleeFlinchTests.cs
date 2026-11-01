using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/light-melee-flinch (protocol 52; the owner, 2026-11-01): a light melee hit (every attack of the normal combo, whatever its data table says) starts no
/// reaction, no knockback and ends no action; it only throws off the aim of a victim who shoots. Stun strikes, the charge and the heavy classes' kick keep theirs.
/// </summary>
public class LightMeleeFlinchTests(ITestOutputHelper output)
{
    static readonly ReactionEntry Stun = new(0f, ReactionKind.Stun, Backward: 2f, Duration: 0.2f);
    static readonly ReactionEntry Stagger = new(0f, ReactionKind.Stagger, Backward: 3f, Duration: 0.3f);
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);

    /// <summary>A light attack whose table says stun (the worst case): hit at tick 6.</summary>
    static readonly MeleeStep StunningLight = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f) { PrimaryReaction = Stun, SecondaryReaction = Stagger };
    static readonly MeleeStep KickStep = StunningLight with { PrimaryReaction = Stagger, Kick = true };
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 20f, 5f, 3.5f) { PrimaryReaction = Stun };

    static SimConfig ConfigWith(MeleeStep light) => new()
    {
        Weapons = [Rifle],
        Classes =
        [
            new ClassLoadout("marine", 0, -1, [light, light, light]) { MeleeHeavy = [Heavy, Heavy], MeleeRush = RushTests.Spec, Evade = 0, Side = Team.SpaceMarines },
            new ClassLoadout("chaos", 0, -1, [light, light, light]) { MeleeHeavy = [Heavy, Heavy], MeleeRush = RushTests.Spec, Evade = 0, Side = Team.Chaos },
        ],
        Evades = [EvadeTests.Roll with { TargetableModeTime = 0.3f, CollisionModeTime = 0.2f }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        MeleeRange = 40f, MeleeConeDegrees = 180f, MeleeVerticalReach = 60f,
        HealthMax = 1000f, ShieldMax = 0f, HealthRegenDelay = 1e6f,
    };

    static World Arena(SimConfig c, params Vector3[] at)
    {
        var w = new World(c, null, at.Select(p => new SpawnPoint(p, 0f)).ToArray());
        for (int i = 0; i < at.Length; i++) w.Spawn(i + 1, i % 2);
        return w;
    }

    static PlayerInput Away(InputButtons b = InputButtons.None) => new(0, 0f, 0f, MathF.PI, b);
    static PlayerInput Idle(int _) => new(0, 0f, 0f, 0f, InputButtons.None);

    /// <summary>Player 1 (the victim) follows <paramref name="victim"/>; player 2 (the attacker) presses <paramref name="press"/> at tick 0.</summary>
    static void Run(World w, int ticks, Func<int, PlayerInput> victim, InputButtons press, Action<int>? each = null)
    {
        for (int k = 0; k < ticks; k++)
        {
            int tick = k; uint view = w.Tick;
            w.Step(id => (id == 1 ? victim(tick) : Away(tick == 0 ? press : InputButtons.None)) with { ViewTick = view });
            each?.Invoke(tick);
        }
    }

    [Fact]
    public void A_light_hit_whose_table_says_stun_starts_no_reaction_and_no_knockback()
    {
        var w = Arena(ConfigWith(StunningLight), Vector3.Zero, new Vector3(0, 2, 0));
        var start = w.Players[1].Position;
        var reactions = new List<ReactionEvent>();
        Run(w, 12, Idle, InputButtons.Melee, _ => reactions.AddRange(w.LastReactions));
        Assert.Empty(reactions);
        var v = w.Players[1];
        Assert.Equal((ReactionKind.None, 0, start), (v.Reaction, v.PushDistance, v.Position));
        Assert.True(v.Health < 1000f);   // the damage landed
        Assert.True(v.FlinchTicks > 0);
    }

    [Fact]
    public void A_light_hit_on_a_player_in_a_melee_combo_or_a_heavy_strike_interrupts_neither()
    {
        foreach (var (button, heavy) in new[] { (InputButtons.Melee, false), (InputButtons.MeleeHeavy, true) })
        {
            var w = Arena(ConfigWith(StunningLight), Vector3.Zero, new Vector3(0, 2, 0));
            int stepAtHit = -1;
            Run(w, 12, k => new PlayerInput(0, 0f, 0f, 0f, k == 0 ? button : InputButtons.None), InputButtons.Melee, k => { if (k == 8) stepAtHit = w.Players[1].MeleeStep; });
            var v = w.Players[1];
            Assert.True(v.Health < 1000f);
            Assert.Equal((ReactionKind.None, 1, heavy), (v.Reaction, stepAtHit, v.MeleeHeavy));   // still in its first attack
        }
    }

    [Fact]
    public void A_light_hit_on_a_charging_player_leaves_the_charge_unbroken()
    {
        var w = Arena(ConfigWith(StunningLight), Vector3.Zero, new Vector3(30, 0, 0));
        var phases = new List<RushPhase>();
        // Player 1 charges (sprint + W + F at tick 10); player 2 swings at it at tick 20 (the charge's hyper armour ended at 0.3 s), the hit lands at tick 26.
        for (int k = 0; k < 50; k++)
        {
            int tick = k; uint view = w.Tick;
            w.Step(id => (id == 1
                ? new PlayerInput(0, 0f, tick <= 10 ? 1f : 0f, 0f, (tick <= 10 ? InputButtons.Sprint : InputButtons.None) | (tick == 10 ? InputButtons.MeleeHeavy : InputButtons.None))
                : Away(tick == 20 ? InputButtons.Melee : InputButtons.None)) with { ViewTick = view });
            phases.Add(w.Players[1].Rush);
        }
        output.WriteLine($"rush ticks {phases.Count(p => p == RushPhase.Rush)}, health {w.Players[1].Health}");
        Assert.True(w.Players[1].Health < 1000f);
        Assert.Equal(30, phases.Count(p => p == RushPhase.Rush));   // the whole 1.0 s
        Assert.Equal(ReactionKind.None, w.Players[1].Reaction);
    }

    [Fact]
    public void A_flinched_shooter_scatters_its_shot_and_the_flinch_wears_off()
    {
        var w = Arena(ConfigWith(StunningLight), Vector3.Zero, new Vector3(0, 2, 0));
        Run(w, 8, Idle, InputButtons.Melee);
        Assert.InRange(w.Players[1].FlinchTicks, Spread.FlinchTicksOf(w.Config) - 3, Spread.FlinchTicksOf(w.Config));   // the hit landed a tick or two ago
        var fire = new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire);
        w.Step(id => id == 1 ? fire : Away());
        var shooter = w.Players[1];
        float limit = w.Config.MeleeFlinchDegrees * MathF.PI / 180f + 1e-4f;
        Assert.True(MathF.Abs(shooter.SpreadYaw) > 0f || MathF.Abs(shooter.SpreadPitch) > 0f);
        Assert.True(MathF.Abs(shooter.SpreadYaw) <= limit && MathF.Abs(shooter.SpreadPitch) <= limit);
        // Unflinched: the same shot goes straight (the rifle has no scatter set).
        var calm = Arena(ConfigWith(StunningLight), Vector3.Zero, new Vector3(0, 2, 0));
        calm.Step(id => id == 1 ? fire : Away());
        Assert.Equal((0f, 0f), (calm.Players[1].SpreadYaw, calm.Players[1].SpreadPitch));
        // The flinch is gone after its ticks.
        for (int k = 0; k < 20; k++) w.Step(id => id == 1 ? Idle(0) : Away());
        Assert.Equal(0, w.Players[1].FlinchTicks);
    }

    [Fact]
    public void A_stun_strike_still_stuns_and_the_heavy_classes_kick_still_staggers()
    {
        var w = Arena(ConfigWith(StunningLight), Vector3.Zero, new Vector3(0, 2, 0));
        var kinds = new List<ReactionKind>();
        Run(w, 14, Idle, InputButtons.MeleeHeavy, _ => kinds.AddRange(w.LastReactions.Select(r => r.Kind)));
        Assert.Equal([ReactionKind.Stun], kinds);

        var kw = Arena(ConfigWith(KickStep), Vector3.Zero, new Vector3(0, 2, 0));
        kinds.Clear();
        Run(kw, 12, Idle, InputButtons.Melee, _ => kinds.AddRange(kw.LastReactions.Select(r => r.Kind)));
        Assert.Equal([ReactionKind.Stagger], kinds);
        Assert.Equal(0, kw.Players[1].FlinchTicks);
    }

    [Fact]
    public void The_flinch_travels_in_the_own_record()
    {
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, FlinchTicks = 11 };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 1, 1, [me], Self: 3))));
        Assert.Equal(me, back.Players[0]);
        Assert.Equal(11, back.Players[0].FlinchTicks);
    }
}
