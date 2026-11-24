using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmBots.Extras.Warfront;

/// <summary>What the host tells a retainer's strategy each tick (the server fills it from the World and the retainer registry before the brain steps).</summary>
public sealed class RetainerSituation
{
    /// <summary>The order in force (the jump-pound while one runs).</summary>
    public RetainerOrder Order;
    /// <summary>The owning player's feet and heading, and whether he is alive.</summary>
    public V3 Owner;
    public float OwnerYaw;
    public bool OwnerAlive = true;
    /// <summary>The Hold order's place (this retainer's own ring place), the Attack order's point, the jump-pound's mark.</summary>
    public V3? HoldAt, AttackAt, PoundAt;
    /// <summary>A downed ally this retainer should revive (a downed retainer or a fallen human), or null.</summary>
    public V3? ReviveAt;
    /// <summary>This retainer's place among its owner's retainers (its follow spot) and how many there are.</summary>
    public int Slot, Count = 1;
    /// <summary>Set by the strategy when the jump-pound has played out (the host then drops the order).</summary>
    public bool PoundDone;
}

/// <summary>
/// The Warfront retainers' strategy around the Classic fight layer (Phase B T4, added content; the approved brief 2026-11-23 "Follow and orders", owner 2026-11-24 leash 40 m, owner 2026-11-25 answers):
/// the Classic brain chooses and executes every fight decision (shooting, melee, jumps, charges, rolls, throws, aim) exactly as for a Classic bot; this strategy only decides where it goes when it
/// does not fight, keeps every path goal inside its order's area (<see cref="ClampGoal"/>), and takes over ahead of the fight for a leash return, the jump-pound and a revive.
/// One instance per retainer (it holds that retainer's leash). Engine-free; the clock is the brain's.
/// <list type="bullet">
/// <item>Follow: the <see cref="WarfrontLeash"/> circle of 40 m round the owner; an enemy outside it is closed on only to the reachable edge (the goal is clamped), then held and shot;
/// with nothing to fight it follows its owner to its spot behind him (or chases an enemy last seen inside the circle, as the Classic chase).</item>
/// <item>Hold: fights from its place (goals within <see cref="HoldRadius"/> of it) and walks back to it.</item>
/// <item>Attack: goes for the point; no leash.</item>
/// <item>Jump-pound: a jump-pack retainer's max jump and pound at the mark; others ignore it.</item>
/// <item>Revive: a downed ally within reach of its order is walked to and revived with Use held (no enemy within <see cref="ReviveSafe"/> m in sight).</item>
/// </list>
/// </summary>
public sealed class WarfrontRetainerStrategy : IBotStrategy
{
    /// <summary>A Hold keeps its fight within this of its place (ours). Kind: ours; confidence: low.</summary>
    public const float HoldRadius = 6f;
    /// <summary>A following retainer stands this far behind its owner, its slot spread across his back (ours). Kind: ours; confidence: low.</summary>
    public const float FollowSpot = 3.5f;
    /// <summary>It walks to its follow spot only when farther than this from it (ours, the Classic route's arrive distance doubled). Kind: ours; confidence: low.</summary>
    public const float FollowSlack = 3f;
    /// <summary>It sprints when the path left is longer than this (the Classic chase's sprint). Kind: ours (Classic copy); confidence: medium.</summary>
    public const float SprintBeyond = 30f;
    /// <summary>A Follow retainer chases (the Classic chase) an enemy last seen within this of itself and inside the circle; farther ones are not hunted. Kind: ours; confidence: low.</summary>
    public const float ChaseWithin = 30f;
    /// <summary>A revive is started only with no enemy in sight within this (ours). Kind: ours; confidence: low.</summary>
    public const float ReviveSafe = 8f;
    /// <summary>Within this of the downed ally it stops and holds Use (the squad's revive reach is 2 m). Kind: ours; confidence: medium.</summary>
    public const float ReviveStand = 1.5f;

    public RetainerSituation Situation { get; } = new();
    public WarfrontLeash Leash { get; } = new();
    double _lastFight = double.NegativeInfinity;
    /// <summary>The last tick's decision ("fight", "follow", "return", "hold", "attack", "pound", "revive", ...), for the host's event log.</summary>
    public string Decision { get; private set; } = "";

    static float Flat(V3 a, V3 b) => new System.Numerics.Vector2(a.X - b.X, a.Y - b.Y).Length();

    /// <summary>The Classic bookkeeping (the lives, the periodic melee push), as a Classic bot's.</summary>
    public void Begin(ClassicBotBrain brain, PlayerState me) { Decision = "fight"; brain.ClassicBegin(me); }

    public PlayerInput? BeforeFight(ClassicBotBrain brain, PlayerState me, uint tick, double phase)
    {
        var s = Situation;
        double now = brain.WarfrontNow;
        bool committed = brain.CommittedMove(me);
        Leash.Step(now, s.Order, Flat(me.Position, s.Owner), committed, _lastFight);
        // the jump-pound order: a jump-pack retainer's max jump and pound at its mark (others drop it at once)
        if (s.Order == RetainerOrder.JumpPound && s.PoundAt is { } mark)
        {
            var pound = brain.WarfrontPoundInput(me, mark, tick, out bool done);
            if (done) { s.PoundDone = true; Decision = "pound done"; }
            if (pound is { } p) { Decision = "pound"; brain.WarfrontNote("warfront: jump-pound"); return p; }
        }
        if (Leash.State == LeashState.Return && !committed)
        {
            Decision = "return";
            brain.WarfrontNote("warfront: back to the owner");
            return brain.WarfrontWalk(me, FollowSpotOf(s), "warfront: back to the owner", tick, SprintBeyond * 0.5f);
        }
        if (s.ReviveAt is { } downed && !committed && ReviveAllowed(s, downed) && (brain.BotFightTarget(me, out float enemy) is null || enemy > ReviveSafe))
        {
            Decision = "revive";
            brain.WarfrontNote("warfront: reviving");
            if (Flat(me.Position, downed) <= ReviveStand)
                return brain.WarfrontStand(tick, MathF.Atan2(-(downed.X - me.Position.X), downed.Y - me.Position.Y), InputButtons.Interact);
            return brain.WarfrontWalk(me, downed, "warfront: to the downed ally", tick, SprintBeyond * 0.5f, InputButtons.Interact);
        }
        // the Classic part ahead of the fight (the retreat to cover at low health), its paths kept inside the order's area
        return brain.ClassicBeforeFight(me, tick, phase);
    }

    /// <summary>A revive the order allows: Follow inside the circle, Hold near its place, Attack and the pound anywhere.</summary>
    static bool ReviveAllowed(RetainerSituation s, V3 downed) => s.Order switch
    {
        RetainerOrder.Follow => Flat(downed, s.Owner) <= WarfrontLeash.Radius,
        RetainerOrder.Hold => s.HoldAt is not { } h || Flat(downed, h) <= HoldRadius * 2f,
        _ => true,
    };

    public void NoteFight(ClassicBotBrain brain, PlayerState me, PlayerState target) { _lastFight = brain.WarfrontNow; Decision = "fight"; }

    /// <summary>The Classic fight move (a melee push closing in), its path goal clamped.</summary>
    public PlayerInput? FightMove(ClassicBotBrain brain, PlayerState me, PlayerState target, uint tick, float horizontal) => brain.ClassicFightMove(me, target, tick, horizontal);

    public BotPost? Post(ClassicBotBrain brain, PlayerState me) => null;

    /// <summary>Hold: back onto its place while fighting when it drifted off it.</summary>
    public V3? PostReturn(ClassicBotBrain brain, PlayerState me) =>
        Situation.Order == RetainerOrder.Hold && Situation.HoldAt is { } h && Flat(me.Position, h) > HoldRadius ? h : null;

    public PlayerInput? Idle(ClassicBotBrain brain, PlayerState me, uint tick)
    {
        var s = Situation;
        switch (s.Order)
        {
            case RetainerOrder.Hold when s.HoldAt is { } h:
                Decision = "hold";
                if (Flat(me.Position, h) > 1f) return brain.WarfrontWalk(me, h, "warfront: to the hold", tick, SprintBeyond);
                return brain.WarfrontStand(tick, s.OwnerYaw);
            case RetainerOrder.Attack when s.AttackAt is { } a:
                Decision = "attack";
                if (Flat(me.Position, a) > HoldRadius) return brain.WarfrontWalk(me, a, "warfront: attack point", tick, SprintBeyond);
                return brain.WarfrontClassicIdle(me, tick) ?? brain.WarfrontStand(tick, me.Yaw);
            default:
                // Follow (and a jump-pound waiting for its press): an enemy last seen near, inside the circle, is chased the Classic way (the goal clamped); else its spot behind the owner
                if (EnemyToChase(brain, me, s) && brain.WarfrontClassicIdle(me, tick) is { } chase) { Decision = "chase"; return chase; }
                Decision = "follow";
                var spot = FollowSpotOf(s);
                if (Flat(me.Position, spot) > FollowSlack) return brain.WarfrontWalk(me, spot, "warfront: follow the owner", tick, SprintBeyond);
                return brain.WarfrontStand(tick, s.OwnerYaw);
        }
    }

    bool EnemyToChase(ClassicBotBrain brain, PlayerState me, RetainerSituation s)
    {
        foreach (var o in brain.WarfrontOthers)
            if (o.Alive && World.Hostile(me.Team, o.Team) && o.Team != Team.None && Flat(o.Position, me.Position) <= ChaseWithin && Flat(o.Position, s.Owner) <= WarfrontLeash.Radius) return true;
        return false;
    }

    /// <summary>The retainer's place behind its owner: <see cref="FollowSpot"/> metres back, the slots spread over a quarter circle across his back.</summary>
    public static V3 FollowSpotOf(RetainerSituation s)
    {
        float spread = s.Count <= 1 ? 0f : (s.Slot / (float)(s.Count - 1) - 0.5f) * (MathF.PI / 2f);
        float back = s.OwnerYaw + MathF.PI + spread;   // direction (-sin yaw, cos yaw)
        return s.Owner + new V3(-MathF.Sin(back), MathF.Cos(back), 0f) * FollowSpot;
    }

    /// <summary>Every path goal stays inside the order's area: Follow's circle round the owner (to its reachable edge), Hold's ring round its place; Attack, the pound and a return are free.</summary>
    public V3 ClampGoal(ClassicBotBrain brain, PlayerState me, V3 goal)
    {
        var s = Situation;
        if (s.Order == RetainerOrder.Follow && Leash.State != LeashState.Return) return Clamp(goal, s.Owner, WarfrontLeash.Radius - 1f);
        if (s.Order == RetainerOrder.Hold && s.HoldAt is { } h) return Clamp(goal, h, HoldRadius);
        return goal;
    }

    static V3 Clamp(V3 goal, V3 centre, float radius)
    {
        var d = new System.Numerics.Vector2(goal.X - centre.X, goal.Y - centre.Y);
        float l = d.Length();
        if (l <= radius) return goal;
        d *= radius / l;
        return new V3(centre.X + d.X, centre.Y + d.Y, goal.Z);
    }

    /// <summary>A new life or battle.</summary>
    public void Reset(ClassicBotBrain brain)
    {
        Leash.Reset();
        _lastFight = double.NegativeInfinity;
        brain.WarfrontPoundReset();
    }
}
