using System.Numerics;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>anim/melee-interaction: the client's presentation rules (<see cref="MeleeInteraction"/>): the charge hit-stop, the camera kick, the victim at the lock spot, reaction facing.</summary>
public class MeleeInteractionTests
{
    static readonly SimConfig Config = new();
    static PlayerState P(int id, float x, float y, float yaw = 0f, Team team = Team.None) => new(id, new Vector3(x, y, 0f), Vector3.Zero, yaw, true) { Team = team, Alive = true, Health = 100f };

    [Fact]
    public void The_charge_hit_stop_freezes_then_catches_up()
    {
        Assert.Equal(0f, MeleeInteraction.ChargeHold(-1f));
        Assert.Equal(0.05f, MeleeInteraction.ChargeHold(0.05f), 5);   // frozen: held back by the time since
        Assert.Equal(MeleeInteraction.ChargeStopSeconds, MeleeInteraction.ChargeHold(MeleeInteraction.ChargeStopSeconds), 5);
        Assert.Equal(0f, MeleeInteraction.ChargeHold(MeleeInteraction.ChargeStopSeconds + MeleeInteraction.ChargeCatchUpSeconds), 5);
        Assert.False(MeleeInteraction.ChargeHoldActive(0.3f));
        Assert.True(MeleeInteraction.ChargeStopSeconds > MeleePresentation.StopSeconds);   // heavier than the melee hit-stop
    }

    [Fact]
    public void The_camera_kick_rises_peaks_and_returns()
    {
        Assert.Equal(0f, MeleeInteraction.PunchAt(0f));
        Assert.Equal(1f, MeleeInteraction.PunchAt(MeleeInteraction.PunchRise), 3);
        Assert.True(MeleeInteraction.PunchAt(0.15f) < 0.5f);
        Assert.Equal(0f, MeleeInteraction.PunchAt(1f));
        Assert.True(MeleeInteraction.ObserverShake(2f) > MeleeInteraction.ObserverShake(8f));
        Assert.Equal(0f, MeleeInteraction.ObserverShake(20f));
    }

    [Fact]
    public void The_victim_is_the_player_at_the_lock_spot_not_the_nearest()
    {
        var charger = P(1, 0f, 0f) with { Rush = RushPhase.Collision };
        var atSpot = P(2, 0.1f, 0.9f);          // 0.9 m in front: the lock spot
        var beside = P(3, 0.7f, 0.2f);          // nearer but beside
        Assert.Equal(2, MeleeInteraction.ChargeVictim(charger, [charger, beside, atSpot], Config));
        Assert.Equal(0, MeleeInteraction.ChargeVictim(charger, [charger, P(4, 0f, -3f)], Config));   // a wall stop: nobody there
    }

    [Theory]
    [InlineData(ReactionDirection.Front, 0f)]
    [InlineData(ReactionDirection.Back, 180f)]
    [InlineData(ReactionDirection.Right, 90f)]
    [InlineData(ReactionDirection.Left, -90f)]
    public void A_reacting_body_turns_its_clip_side_to_the_attacker(ReactionDirection d, float offsetDegrees)
    {
        var victim = P(1, 0f, 0f);
        var attacker = new Vector3(0f, 2f, 0f);   // straight +Y: bearing 0
        float bearing = MeleeInteraction.Bearing(victim.Position, attacker);
        Assert.Equal(0f, bearing, 5);
        float yaw = MeleeInteraction.FacingFor(d, bearing);
        Assert.Equal(0f, MeleeInteraction.Wrap(yaw - offsetDegrees * MathF.PI / 180f), 4);
        // The sim's own sector rule agrees: with that body yaw, the attacker lies in the clip's sector.
        Assert.Equal(d, Reaction.DirectionOf(victim.Position, yaw, attacker, fourWay: true));
    }

    [Fact]
    public void The_reaction_source_is_the_nearest_attacking_enemy()
    {
        var victim = P(1, 0f, 0f, team: Team.Chaos) with { Reaction = ReactionKind.Stun };
        var idle = P(2, 0.5f, 0f, team: Team.SpaceMarines);
        var swinging = P(3, 1.5f, 0f, team: Team.SpaceMarines) with { MeleeStep = 1 };
        var ally = P(4, 0.8f, 0f, team: Team.Chaos) with { MeleeStep = 1 };
        Assert.Equal(3, MeleeInteraction.ReactionSource(victim, [victim, idle, swinging, ally]));
        Assert.Equal(0, MeleeInteraction.ReactionSource(victim, [victim, idle]));
    }

    [Fact]
    public void Easing_turns_the_short_way()
    {
        float y = MeleeInteraction.Ease(3.0f, -3.0f, 1f / 60f);
        Assert.True(y > 3.0f, $"{y}");   // through ±π, not back across 0
        Assert.Equal(1f, MeleeInteraction.Ease(0f, 1f, 1f), 2);
    }
}
