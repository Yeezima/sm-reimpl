using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Hit reactions (2026-11-11): flinches are drawn only. Gunfire and light melee hits on a player in the middle of an action (a reload, a melee combo, an evade, a weapon switch) leave that action exactly
/// as it runs unhit: the victim's whole action timeline (melee step and time, reload, evade, switch, slot, rush, place and facing) is the same tick for tick with and without the hits, and no reaction
/// starts. The owner's rule (owner's recollection): a light hit only throws off a shooter's aim; the shots' flinch (the data's ranged reaction types) is not a reaction in the simulation either.
/// </summary>
public class HitReactionNoBlockTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = Rifle with { Name = "pistol", Slot = WeaponSlot.Pistol, Trigger = TriggerKind.SemiAuto };
    static readonly ReactionEntry Stun = new(0f, ReactionKind.Stun, Backward: 2f, Duration: 0.2f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f) { PrimaryReaction = Stun };   // a light attack whose table says stun: still only a flinch

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol],
        Classes =
        [
            new ClassLoadout("marine", 0, 1, [Light, Light, Light]) { Evade = 0, Side = Team.SpaceMarines },
            new ClassLoadout("chaos", 0, 1, [Light, Light, Light]) { Evade = 0, Side = Team.Chaos },
        ],
        Evades = [EvadeTests.Roll with { TargetableModeTime = 0.3f, CollisionModeTime = 0.2f }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        MeleeRange = 40f, MeleeConeDegrees = 180f, MeleeVerticalReach = 60f,
        HealthMax = 100000f, ShieldMax = 0f, HealthRegenDelay = 1e6f,
    };

    /// <summary>What the victim does: an input per tick.</summary>
    public static readonly TheoryData<string> Actions = new() { "reload", "melee combo", "evade", "weapon switch" };

    static PlayerInput VictimInput(string action, int tick) => action switch
    {
        "reload" => new(0, 0f, 0f, 0f, tick == 0 ? InputButtons.Fire : tick == 3 ? InputButtons.Reload : InputButtons.None),
        "melee combo" => new(0, 0f, 0f, 0f, tick is 0 or 12 or 24 ? InputButtons.Melee : InputButtons.None),
        "evade" => new(0, 0f, tick <= 2 ? 1f : 0f, 0f, tick == 1 ? InputButtons.Jump : InputButtons.None),
        "weapon switch" => new(0, 0f, 0f, 0f, tick == 0 ? InputButtons.Switch : InputButtons.None),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>The victim's action timeline over 40 ticks; the attacker (player 2) shoots it every tick, swings at it, or does nothing.</summary>
    static (List<string> Timeline, float Health, int Reactions) Run(string action, string attack, float distance)
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, distance, 0), MathF.PI)]);
        w.Spawn(1, 0); w.Spawn(2, 1);
        var timeline = new List<string>();
        int reactions = 0;
        for (int k = 0; k < 40; k++)
        {
            int tick = k; uint view = w.Tick;
            var attackButtons = attack switch { "gunfire" => InputButtons.Fire, "light melee" when tick % 12 == 2 => InputButtons.Melee, _ => InputButtons.None };
            w.Step(id => (id == 1 ? VictimInput(action, tick) : new PlayerInput(0, 0f, 0f, MathF.PI, attackButtons)) with { ViewTick = view });
            reactions += w.LastReactions.Count(r => r.Target == 1);
            var v = w.Players[1];
            timeline.Add($"step {v.MeleeStep} {v.MeleeTime:F3} reload {v.ReloadLeft:F3} evade {v.EvadeTicks} {v.EvadeDirection} switch {v.SwitchLeft:F3} slot {v.Slot} rush {v.Rush} reaction {v.Reaction} " +
                         $"at {v.Position.X:F3},{v.Position.Y:F3},{v.Position.Z:F3} yaw {v.Yaw:F3}");
        }
        return (timeline, w.Players[1].Health, reactions);
    }

    /// <summary>The action really ran in the unhit timeline (so the comparison means something).</summary>
    static void AssertRan(string action, List<string> timeline)
    {
        bool ran = action switch
        {
            "reload" => timeline.Any(l => !l.Contains("reload 0.000")),
            "melee combo" => timeline.Any(l => l.StartsWith("step 2", StringComparison.Ordinal)),
            "evade" => timeline.Any(l => !l.Contains("evade 0 ")),
            "weapon switch" => timeline.Any(l => l.Contains("slot Pistol")),
            _ => false,
        };
        Assert.True(ran, $"{action} did not run: {string.Join(" | ", timeline.Take(6))}");
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Gunfire_on_a_player_in_an_action_leaves_the_action_running_as_unhit(string action)
    {
        var quiet = Run(action, "none", 6f);
        var shot = Run(action, "gunfire", 6f);
        output.WriteLine($"{action}: health {quiet.Health} unhit, {shot.Health} shot; first ticks: {string.Join(" | ", shot.Timeline.Take(3))}");
        AssertRan(action, quiet.Timeline);
        Assert.True(shot.Health < quiet.Health);   // the shots landed
        Assert.Equal(0, shot.Reactions);
        Assert.Equal(quiet.Timeline, shot.Timeline);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void A_light_melee_hit_on_a_player_in_an_action_leaves_the_action_running_as_unhit(string action)
    {
        var quiet = Run(action, "none", 2f);
        var hit = Run(action, "light melee", 2f);
        output.WriteLine($"{action}: health {quiet.Health} unhit, {hit.Health} hit");
        AssertRan(action, quiet.Timeline);
        Assert.True(hit.Health < quiet.Health);   // the swings landed
        Assert.Equal(0, hit.Reactions);
        Assert.Equal(quiet.Timeline, hit.Timeline);
    }
}
