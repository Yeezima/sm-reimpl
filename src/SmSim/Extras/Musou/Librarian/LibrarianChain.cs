using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>An enemy as a chain lightning sees it: its id, where its feet are and its body height (as <c>MusouSim.Body</c>: the type's height, the Warboss scaled).</summary>
public readonly record struct LibrarianCandidate(ushort Id, Vector3 Feet, float Height);

/// <summary>
/// The chain lightning's one rule (Musou Mode only; <see cref="LibrarianRules"/>), shared by the server's cast (<see cref="LibrarianPsyker"/>) and the own client's foreseen chain (<c>LibrarianPrediction</c>), so what the
/// client draws and what the server strikes are the same function of the same inputs (musou-balance 2026-11-11: the owner's "doesn't seem to be reliably delivering damage").
/// <list type="bullet">
/// <item>The primary: the enemy best lined up with the aim within <see cref="LibrarianRules.AimConeDegrees"/> of it, its chest within <see cref="LibrarianRules.LightningRange"/> (+1 m) of the eye, its feet
/// within the range flat and <see cref="PrimaryVerticalReach"/> up or down, in sight; candidates are visited nearest first (flat distance from the feet, ties by id) and a later one is kept only when it is
/// clearly truer (by more than 0.0005 in the cosine).</item>
/// <item>Each jump: the nearest candidate not yet struck within <see cref="LibrarianRules.JumpRadius"/> flat and <see cref="LibrarianRules.VerticalReach"/> up or down of the last one's feet (ties by id),
/// up to <see cref="LibrarianRules.MaxTargets"/> in all.</item>
/// <item>The links: hand to the primary's chest, then chest to chest; nothing in the sights: one link out to the full range (the bolt that crackles out).</item>
/// </list>
/// Engine-free, allocation-free with a <see cref="Scratch"/> (grown once to the candidates' count).
/// </summary>
public static class LibrarianChain
{
    /// <summary>The primary pick's vertical reach (metres up or down from the feet; the server's candidates were queried with 8 m since S5).</summary>
    public const float PrimaryVerticalReach = 8f;

    /// <summary>The ordering buffers of one caller (the server's psyker, the client's foresight): grown to the candidates' count and kept.</summary>
    public sealed class Scratch
    {
        internal int[] Order = new int[64];
        internal float[] Flats = new float[64];

        internal void Ensure(int n)
        {
            if (Order.Length >= n) return;
            Order = new int[Math.Max(n, Order.Length * 2)];
            Flats = new float[Order.Length];
        }
    }

    /// <summary>Where the lightning leaves the Librarian: a hand's height below the eye, half a metre ahead (ours, cosmetic).</summary>
    public static Vector3 Hand(Vector3 eye, float yaw) => eye + LibrarianPsyker.Aim(yaw, 0f) * 0.5f - new Vector3(0f, 0f, 0.35f);

    /// <summary>
    /// The chain from a Librarian at <paramref name="feet"/> / <paramref name="eye"/> looking along (<paramref name="yaw"/>, <paramref name="pitch"/>) through <paramref name="enemies"/>: its links into
    /// <paramref name="links"/> (cleared first), the struck candidates' indices in chain order into <paramref name="chain"/> (at least <see cref="LibrarianRules.MaxTargets"/> long) and
    /// <paramref name="struck"/> (as long as <paramref name="enemies"/>; true for each struck). <paramref name="blocked"/> says whether the level lies between two points (null: nothing blocks).
    /// Returns how many enemies it strikes (0: nothing in the sights).
    /// </summary>
    public static int Run(Vector3 feet, Vector3 eye, float yaw, float pitch, ReadOnlySpan<LibrarianCandidate> enemies, Func<Vector3, Vector3, bool>? blocked, List<LibrarianLink> links,
        Span<bool> struck, Span<int> chain, Scratch scratch)
    {
        links.Clear();
        int n = Math.Min(enemies.Length, struck.Length);
        struck[..n].Clear();
        var aim = LibrarianPsyker.Aim(yaw, pitch);
        var hand = Hand(eye, yaw);
        int primary = PickPrimary(feet, eye, aim, enemies[..n], blocked, scratch);
        if (primary < 0) { links.Add(new LibrarianLink(hand, eye + aim * LibrarianRules.LightningRange)); return 0; }
        var from = hand;
        int at = primary, count = 0;
        int most = Math.Min(LibrarianRules.MaxTargets, chain.Length);
        for (int k = 0; k < most; k++)
        {
            var e = enemies[at];
            var chest = MusouSim.Chest(e.Feet, e.Height);
            if (links.Count < LibrarianRules.MaxDrawnLinks) links.Add(new LibrarianLink(from, chest));
            struck[at] = true;
            chain[count++] = at;
            from = chest;
            int next = NextJump(e.Feet, enemies[..n], struck);
            if (next < 0) break;
            at = next;
        }
        return count;
    }

    static int PickPrimary(Vector3 feet, Vector3 eye, Vector3 aim, ReadOnlySpan<LibrarianCandidate> enemies, Func<Vector3, Vector3, bool>? blocked, Scratch scratch)
    {
        float minCos = MathF.Cos(LibrarianRules.AimConeDegrees * MathF.PI / 180f), best = minCos;
        int pick = -1;
        float range2 = LibrarianRules.LightningRange * LibrarianRules.LightningRange;
        scratch.Ensure(enemies.Length);
        var order = scratch.Order.AsSpan(); var flats = scratch.Flats.AsSpan();
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
        // Nearest first by flat distance, then id (the few in the cone).
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
            if (pick < 0 ? cos <= best : cos <= best + 0.0005f) continue;   // the nearest of the well-lined-up ones, unless another is clearly truer
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
            if (struck[i]) continue;
            var d = enemies[i].Feet - at;
            if (MathF.Abs(d.Z) > LibrarianRules.VerticalReach) continue;
            float flat = d.X * d.X + d.Y * d.Y;
            if (flat > r2) continue;
            if (flat < best || (flat == best && pick >= 0 && enemies[i].Id < enemies[pick].Id)) { best = flat; pick = i; }
        }
        return pick;
    }
}
