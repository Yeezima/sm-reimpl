using System.Numerics;

namespace SmSim;

/// <summary>One living enemy as the hunt sees it: where it stands and whether that is its spawn room or the no-loiter zone round its spawn.</summary>
public readonly record struct HuntCandidate(int Id, Vector3 Position, bool InRoom, bool InZone);

/// <summary>
/// The owner's hunt for Classic PvP test bots (2026-11-16; test script, **ours**, not the original AI): "make bots leave patrol when no targets are found - they should
/// look for and hunt down enemies that are not in the spawn area. the intent of this hunting is to clear space for their teammates to safely capture points or take up
/// more powerful positions." The choice of whom to hunt, engine-free. What a bot knows is what the snapshots give every client (every player's state, the same
/// knowledge the existing chase uses; no new channel): the bot does not see through walls to shoot, it only knows where the enemy is, as the chase already did.
/// <list type="bullet">
/// <item>an enemy in its spawn room is never hunted (the room's blockers stop the bot, and a spawn kill is not the aim);</item>
/// <item>an enemy in the no-loiter zone round its spawn is not hunted either (owner 2026-11-07: a bot never loiters at the enemy's spawn; room plus zone are "the spawn area"),
/// unless <c>allowZone</c> is set (switch <c>--bot-hunt-zone</c>, default off): then, when no enemy exists outside both, the nearest one outside the room only is the second choice;</item>
/// <item>the target is the nearest enemy outside the spawn area, which is what the plain chase takes too;</item>
/// <item>a target is kept while it stays a candidate of the same tier, unless another is <see cref="SwitchGain"/> m nearer (so the bot does not dither).</item>
/// </list>
/// </summary>
public sealed class BotHunt
{
    /// <summary>Metres a new target must be nearer than the kept one before the bot switches (ours, an estimate).</summary>
    public const float SwitchGain = 15f;

    static readonly bool[] OutsideOnly = [false], BothTiers = [false, true];
    int _target = -1;
    bool _tierTwo;
    /// <summary>Hunts started: one each time the bot takes a target after having none, or switches to another.</summary>
    public int Hunts { get; private set; }
    public int Target => _target;
    /// <summary>Whether the current target stands in the no-loiter zone (the second choice).</summary>
    public bool InZone => _tierTwo;

    /// <summary>
    /// The enemy to hunt from <paramref name="me"/>, or null when every living enemy is in its spawn room (or none is alive). Tier one candidates are outside the room and the
    /// zone, tier two (only with <paramref name="allowZone"/>) outside the room only; the nearest of the better tier wins, the kept target excepted (see the class).
    /// </summary>
    public int? Pick(Vector3 me, IReadOnlyList<HuntCandidate> enemies, bool allowZone = false)
    {
        HuntCandidate? best = null;
        float bestD = float.MaxValue;
        bool tierTwo = false;
        foreach (var tier in allowZone ? BothTiers : OutsideOnly)
        {
            foreach (var e in enemies)
            {
                if (e.InRoom || e.InZone != tier) continue;
                float d = Vector3.DistanceSquared(e.Position, me);
                if (d < bestD) { bestD = d; best = e; }
            }
            if (best is not null) { tierTwo = tier; break; }
        }
        if (best is not { } pick) { _target = -1; _tierTwo = false; return null; }
        foreach (var e in enemies)
        {
            if (e.Id != _target || e.InRoom || e.InZone != tierTwo) continue;
            float kept = MathF.Sqrt(Vector3.DistanceSquared(e.Position, me)), nearest = MathF.Sqrt(bestD);
            if (kept - nearest < SwitchGain) pick = e;
            break;
        }
        if (pick.Id != _target) Hunts++;
        _target = pick.Id; _tierTwo = tierTwo;
        return pick.Id;
    }

    /// <summary>The hunt ends (the bot died, retreats, or fights): the next pick counts as a new hunt.</summary>
    public void End() { _target = -1; _tierTwo = false; }
}
