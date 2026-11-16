using System.Numerics;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>A step of a route that may leave the ground: the place, and whether it is reached by a jump or a drop rather than a walk.</summary>
public readonly record struct SquadRouteStep(Vector3 Position, bool Jump);

/// <summary>
/// What a jump-pack Marine's follow needs beyond <see cref="ISquadArena"/> (W11): a route to a goal that keeps its jump and drop links, where <see cref="ISquadArena.FindPath"/> ends at the first
/// one. Optional: without it a Marine leaps only straight to a validated landing. Set as <see cref="WarfrontSquadSim.JumpArena"/>.
/// </summary>
public interface ISquadJumpArena
{
    /// <summary>A route from <paramref name="from"/> to <paramref name="to"/> with its jump and drop steps marked; null when there is none.</summary>
    IReadOnlyList<SquadRouteStep>? FindRoute(Vector3 from, Vector3 to);
}

/// <summary>The walking graph's routes with their jump links, for the real sim (<see cref="MusouSquadArena"/> keeps the walking part only).</summary>
public sealed class MusouSquadJumpArena(NavGraph? nav) : ISquadJumpArena
{
    /// <summary>Routes searched so far.</summary>
    public int RoutesBuilt { get; private set; }

    /// <summary>Nodes one search may expand at most: a jump link worth following is near, so a search for an unreachable owner stops early (bench: 60 000 cost up to 72 ms).</summary>
    public const int MaxExpanded = 12_000;

    public IReadOnlyList<SquadRouteStep>? FindRoute(Vector3 from, Vector3 to)
    {
        if (nav is null) return null;
        RoutesBuilt++;
        if (nav.FindPath(from, to, 10f, MaxExpanded) is not { } path) return null;
        var steps = new List<SquadRouteStep>(path.Waypoints.Count);
        foreach (var w in path.Waypoints) steps.Add(new SquadRouteStep(w.Position, w.Kind != NavLinkKind.Walk));
        return steps;
    }
}
