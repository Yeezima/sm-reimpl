using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// The own Librarian's chain lightning as the client foresees it (librarian-lightning, owner 2026-11-10: ".1 second max between clicking left mouse button and the chain lighting hitting its target"): the
/// links drawn the frame Fire is pressed, before the server's own links arrive a round trip later. It is the server's own rule (<see cref="LibrarianChain"/>), on the enemies where the client draws them: the
/// crowd at the render tick, which is the view tick the client's inputs carry, and the server picks the chain on its crowd as it stood at that view tick (musou-balance 2026-11-11), so the chain drawn is the
/// chain struck. Cosmetic only: the server alone deals the damage. Engine-free (tested against the server's chain on the same pool).
/// </summary>
public static class LibrarianPrediction
{
    /// <summary>The primary pick's vertical reach (the server queries its candidates with 8 m).</summary>
    public const float PrimaryVerticalReach = LibrarianChain.PrimaryVerticalReach;

    /// <summary>The body height the server measures an enemy's chest by (<c>MusouSim.Body</c>: the type's height, the Warboss scaled by <see cref="MusouRules.BossScale"/>).</summary>
    public static float HeightOf(MusouRules rules, MusouEnemyType type) => rules.Of(type).Height * (type == MusouEnemyType.Warboss ? rules.BossScale : 1f);

    /// <summary>Where the lightning leaves the Librarian on the server (<see cref="LibrarianChain.Hand"/>: a hand's height below the eye, half a metre ahead).</summary>
    public static Vector3 Hand(Vector3 eye, float yaw) => LibrarianChain.Hand(eye, yaw);

    [ThreadStatic] static LibrarianChain.Scratch? _scratch;
    [ThreadStatic] static int[]? _chain;

    /// <summary>
    /// The chain from a Librarian at <paramref name="feet"/> / <paramref name="eye"/> looking along (<paramref name="yaw"/>, <paramref name="pitch"/>) through <paramref name="enemies"/>, written to
    /// <paramref name="links"/> (cleared first) as the server draws it: hand to the primary's chest, then chest to chest. Nothing in the sights: one link out to the full range (the bolt that crackles out).
    /// <paramref name="blocked"/> says whether the level lies between two points (null: nothing blocks). Returns how many enemies it strikes.
    /// <paramref name="struck"/> is the caller's scratch (as long as <paramref name="enemies"/>; no allocation): true for each struck candidate (all false when it strikes none).
    /// </summary>
    public static int Chain(Vector3 feet, Vector3 eye, float yaw, float pitch, ReadOnlySpan<LibrarianCandidate> enemies, Func<Vector3, Vector3, bool>? blocked, List<LibrarianLink> links, Span<bool> struck)
    {
        _scratch ??= new LibrarianChain.Scratch();
        _chain ??= new int[LibrarianRules.MaxTargets];
        return LibrarianChain.Run(feet, eye, yaw, pitch, enemies, blocked, links, struck, _chain, _scratch);
    }
}
