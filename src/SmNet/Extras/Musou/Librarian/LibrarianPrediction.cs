using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>An enemy as the client draws it, for <see cref="LibrarianPrediction"/>: its id, where its feet are and its body height (as the server's <c>MusouSim.Body</c>).</summary>
public readonly record struct LibrarianCandidate(ushort Id, Vector3 Feet, float Height);

/// <summary>
/// The own Librarian's chain lightning as the client foresees it (librarian-lightning, owner 2026-11-10: ".1 second max between clicking left mouse button and the chain lighting hitting its target"): the
/// links drawn the frame Fire is pressed, before the server's own links arrive a round trip later. The same rules as the server's <see cref="LibrarianPsyker"/> (the primary: the enemy best lined up with the
/// aim within <see cref="LibrarianRules.AimConeDegrees"/> and <see cref="LibrarianRules.LightningRange"/>, in sight, the nearest of the well lined-up ones; each jump: the nearest not yet struck within
/// <see cref="LibrarianRules.JumpRadius"/> flat and <see cref="LibrarianRules.VerticalReach"/> up or down; at most <see cref="LibrarianRules.MaxTargets"/>), on the enemies where the client draws them. Cosmetic
/// only: the server alone deals the damage. Engine-free (tested against the server's chain on the same pool).
/// </summary>
public static class LibrarianPrediction
{
    /// <summary>The primary pick's vertical reach (the server queries its candidates with 8 m).</summary>
    public const float PrimaryVerticalReach = 8f;

    /// <summary>The body height the server measures an enemy's chest by (<c>MusouSim.Body</c>: the type's height, the Warboss scaled by <see cref="MusouRules.BossScale"/>).</summary>
    public static float HeightOf(MusouRules rules, MusouEnemyType type) => rules.Of(type).Height * (type == MusouEnemyType.Warboss ? rules.BossScale : 1f);

    /// <summary>Where the lightning leaves the Librarian on the server (<see cref="LibrarianPsyker"/>'s hand point: a hand's height below the eye, half a metre ahead).</summary>
    public static Vector3 Hand(Vector3 eye, float yaw) => eye + LibrarianPsyker.Aim(yaw, 0f) * 0.5f - new Vector3(0f, 0f, 0.35f);

    /// <summary>
    /// The chain from a Librarian at <paramref name="feet"/> / <paramref name="eye"/> looking along (<paramref name="yaw"/>, <paramref name="pitch"/>) through <paramref name="enemies"/>, written to
    /// <paramref name="links"/> (cleared first) as the server draws it: hand to the primary's chest, then chest to chest. Nothing in the sights: one link out to the full range (the bolt that crackles out).
    /// <paramref name="blocked"/> says whether the level lies between two points (null: nothing blocks). Returns how many enemies it strikes.
    /// <paramref name="struck"/> is the caller's scratch (as long as <paramref name="enemies"/>; no allocation).
    /// </summary>
    public static int Chain(Vector3 feet, Vector3 eye, float yaw, float pitch, ReadOnlySpan<LibrarianCandidate> enemies, Func<Vector3, Vector3, bool>? blocked, List<LibrarianLink> links, Span<bool> struck)
    {
        links.Clear();
        var aim = LibrarianPsyker.Aim(yaw, pitch);
        var hand = Hand(eye, yaw);
        int primary = PickPrimary(feet, eye, aim, enemies, blocked);
        if (primary < 0) { links.Add(new LibrarianLink(hand, eye + aim * LibrarianRules.LightningRange)); return 0; }
        int n = Math.Min(enemies.Length, struck.Length);
        struck[..n].Clear();
        var from = hand;
        int at = primary, count = 0;
        for (int k = 0; k < LibrarianRules.MaxTargets; k++)
        {
            var e = enemies[at];
            var chest = MusouSim.Chest(e.Feet, e.Height);
            if (links.Count < LibrarianRules.MaxDrawnLinks) links.Add(new LibrarianLink(from, chest));
            struck[at] = true;
            count++;
            from = chest;
            int next = NextJump(e.Feet, enemies, struck);
            if (next < 0) break;
            at = next;
        }
        return count;
    }

    [ThreadStatic] static int[]? _order;
    [ThreadStatic] static float[]? _flats;

    static int PickPrimary(Vector3 feet, Vector3 eye, Vector3 aim, ReadOnlySpan<LibrarianCandidate> enemies, Func<Vector3, Vector3, bool>? blocked)
    {
        // The server visits its candidates nearest first (flat distance from the feet; ties by id) and keeps a later one only when it is clearly truer: the same order here.
        float minCos = MathF.Cos(LibrarianRules.AimConeDegrees * MathF.PI / 180f), best = minCos;
        int pick = -1;
        float range2 = LibrarianRules.LightningRange * LibrarianRules.LightningRange;
        if (_order is null || _order.Length < enemies.Length) { _order = new int[Math.Max(64, enemies.Length)]; _flats = new float[_order.Length]; }
        var order = _order.AsSpan(); var flats = _flats!.AsSpan();
        int m = 0;
        for (int i = 0; i < enemies.Length; i++)
        {
            var d = enemies[i].Feet - feet;
            if (MathF.Abs(d.Z) > PrimaryVerticalReach) continue;
            float flat = d.X * d.X + d.Y * d.Y;
            if (flat > range2) continue;
            // A quick cone test first: most of a crowd is far off the aim and never needs sorting.
            var chest = MusouSim.Chest(enemies[i].Feet, enemies[i].Height);
            var to = chest - eye;
            float len = to.Length();
            if (len > LibrarianRules.LightningRange + 1f) continue;
            float cos = len < 1e-3f ? 1f : Vector3.Dot(to / len, aim);
            if (cos <= minCos) continue;
            order[m] = i; flats[m] = flat; m++;
        }
        // Insertion order by flat distance, then id (the few in the cone).
        for (int a = 1; a < m; a++)
        {
            int oi = order[a]; float of = flats[a]; int b = a;
            while (b > 0 && (flats[b - 1] > of || (flats[b - 1] == of && enemies[order[b - 1]].Id > enemies[oi].Id))) { order[b] = order[b - 1]; flats[b] = flats[b - 1]; b--; }
            order[b] = oi; flats[b] = of;
        }
        for (int k = 0; k < m; k++)
        {
            var e = enemies[order[k]];
            var chest = MusouSim.Chest(e.Feet, e.Height);
            var to = chest - eye;
            float len = to.Length();
            float cos = len < 1e-3f ? 1f : Vector3.Dot(to / len, aim);
            if (pick < 0 ? cos <= best : cos <= best + 0.0005f) continue;
            if (blocked is not null && blocked(eye, chest)) continue;
            best = cos; pick = order[k];
        }
        return pick;
    }

    static int NextJump(Vector3 at, ReadOnlySpan<LibrarianCandidate> enemies, ReadOnlySpan<bool> struck)
    {
        float r2 = LibrarianRules.JumpRadius * LibrarianRules.JumpRadius, best = float.MaxValue;
        int pick = -1;
        for (int i = 0; i < enemies.Length; i++)
        {
            if (i < struck.Length && struck[i]) continue;
            var d = enemies[i].Feet - at;
            if (MathF.Abs(d.Z) > LibrarianRules.VerticalReach) continue;
            float flat = d.X * d.X + d.Y * d.Y;
            if (flat > r2) continue;
            if (flat < best || (flat == best && pick >= 0 && enemies[i].Id < enemies[pick].Id)) { best = flat; pick = i; }
        }
        return pick;
    }
}
