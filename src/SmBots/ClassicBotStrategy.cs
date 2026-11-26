using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmBots;

/// <summary>
/// The Classic PvP bots' strategy (test script, **ours**, not the original AI; <see cref="IBotStrategy"/>): the code that ran at these points of the client's
/// BotInput before the move, unchanged. Its parts are the brain's partial files Chase (the chase and map centre), BotRoam (lives, lanes, the idle cycle, via points),
/// BotHunt, BotPosts, BotPush, BotCover (the retreat), Seize and the route test aid in Nav. Stateless: the state is the brain's.
/// </summary>
public sealed class ClassicBotStrategy : IBotStrategy
{
    public static readonly ClassicBotStrategy Instance = new();

    public void Begin(ClassicBotBrain brain, PlayerState me) => brain.ClassicBegin(me);
    public PlayerInput? BeforeFight(ClassicBotBrain brain, PlayerState me, uint tick, double phase) => brain.ClassicBeforeFight(me, tick, phase);
    public void NoteFight(ClassicBotBrain brain, PlayerState me, PlayerState target) => brain.ClassicNoteFight();
    public PlayerInput? FightMove(ClassicBotBrain brain, PlayerState me, PlayerState target, uint tick, float horizontal) => brain.ClassicFightMove(me, target, tick, horizontal);
    public BotPost? Post(ClassicBotBrain brain, PlayerState me) => brain.ClassicPost(me);
    public V3? PostReturn(ClassicBotBrain brain, PlayerState me) => brain.ClassicPostReturn(me);
    public PlayerInput? Idle(ClassicBotBrain brain, PlayerState me, uint tick) => brain.ClassicIdle(me, tick);
}

public sealed partial class ClassicBotBrain
{
    // The Classic strategy's entry points (ClassicBotStrategy), in the order the old BotInput ran them.

    internal void ClassicBegin(PlayerState me)
    {
        BotTrackLife(me);   // 2026-11-07: lives for the lane legs and the idle cycle (ClassicBotBrain.BotRoam.cs)
        if (_client?.Match.Effective() == MatchState.Countdown) ForgetRouteStalls();   // bots A2: a new match forgets the routes avoided in the last one
        if (!SeizeGround) BotPushStep(me);   // 2026-11-08: the periodic melee push, Annihilation (ClassicBotBrain.BotPush.cs)
    }

    internal PlayerInput? ClassicBeforeFight(PlayerState me, uint tick, double phase)
    {
        // 2026-11-08: at low health, cover first, then back into the fight (Classic PvP, both modes; ClassicBotBrain.BotCover.cs).
        if (BotRetreatInput(me, tick) is { } retreatInput) return retreatInput;
        // Seize Ground (M16): go to a point not held, fight on the way and hold it (ClassicBotBrain.Seize.cs).
        if (BotSeizeInput(me, tick, phase) is { } seizeInput) { _botNote = _botSeizeNote; return seizeInput; }
        return null;
    }

    internal void ClassicNoteFight() => BotNoteFight();

    internal PlayerInput? ClassicFightMove(PlayerState me, PlayerState target, uint tick, float horizontal) => BotPushFightInput(me, target, tick, horizontal);

    internal BotPost? ClassicPost(PlayerState me) => BotPostOf(me);

    internal V3? ClassicPostReturn(PlayerState me) => BotPostReturn(me);

    internal PlayerInput? ClassicIdle(PlayerState me, uint tick)
    {
        // --bot-goal / --bot-route (test aid): walk the route instead of chasing (ClassicBotBrain.Nav.cs).
        if (BotRouteInput(me, tick) is { } routeInput) return routeInput;
        if (!(BotFireWalls && NearestLivingTarget(me) is null) && BotChaseInput(me, tick) is { } chaseInput) return chaseInput;   // --bot-fire-walls: stays in its room when alone
        return null;
    }
}
