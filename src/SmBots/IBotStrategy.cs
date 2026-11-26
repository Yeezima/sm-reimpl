using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmBots;

/// <summary>
/// Seam (T1, ours): what a bot does around its fight, called by the brain's fight layer (ClassicBotBrain.cs) at fixed points of every input tick, so a new strategy
/// (bots A2's routes, T3; the Warfront retainers' follow, hold and attack with their leash, T4) never edits the fight layer. The Classic strategy
/// (<see cref="ClassicBotStrategy"/>) is the chase, roam, hunt, posts, push, cover and Seize Ground code of the Classic PvP bots, unchanged.
/// Order in a tick: <see cref="Begin"/>; the countdown hold; added content's bot; <see cref="BeforeFight"/>; with an enemy to fight: <see cref="NoteFight"/>, the jump
/// script, <see cref="FightMove"/>, the evade (not while <see cref="Post"/> has one), melee, the gunfight (<see cref="PostReturn"/>, the advance unless on a post);
/// without: <see cref="Idle"/>, else the old running circle. Every path the brain walks goes through <see cref="ClampGoal"/>.
/// </summary>
public interface IBotStrategy
{
    /// <summary>First thing after the test aids and a navigation jump in flight: bookkeeping (Classic: the lives, the periodic melee push).</summary>
    void Begin(ClassicBotBrain brain, PlayerState me);
    /// <summary>An input ahead of the fight (Classic: the retreat to cover at low health, then Seize Ground's walk to a point), or null for the fight.</summary>
    PlayerInput? BeforeFight(ClassicBotBrain brain, PlayerState me, uint tick, double phase);
    /// <summary>The bot fights <paramref name="target"/> this tick (Classic: the lane leg ends, the idle cycle starts again).</summary>
    void NoteFight(ClassicBotBrain brain, PlayerState me, PlayerState target);
    /// <summary>In the fight, after the jump script: a move that replaces the strafe (Classic: a melee push closing in), or null.</summary>
    PlayerInput? FightMove(ClassicBotBrain brain, PlayerState me, PlayerState target, uint tick, float horizontal);
    /// <summary>The post the bot holds (no evade roll, no advance from it), or null.</summary>
    BotPost? Post(ClassicBotBrain brain, PlayerState me);
    /// <summary>The post to step back onto while fighting, or null.</summary>
    V3? PostReturn(ClassicBotBrain brain, PlayerState me);
    /// <summary>Nothing to fight: the strategy's input (Classic: the route test aid, else the chase, hunt, roam and map centre), or null for the old circle.</summary>
    PlayerInput? Idle(ClassicBotBrain brain, PlayerState me, uint tick);
    /// <summary>
    /// The goal a path is planned to (every <c>BotSteer</c>); Classic: unchanged. A Warfront strategy keeps it within its leash.
    /// </summary>
    V3 ClampGoal(ClassicBotBrain brain, PlayerState me, V3 goal) => goal;
}
