using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SmContent;

namespace SmMapmaker;

/// <summary>
/// What Classic's server adds to an original map besides the drawn placements (docs/mapmaker.md "Classic link"): the invisible movement walls, the kill boxes and the movable cover,
/// as the game's own reader (<see cref="MapContent"/>) finds them. Read by the import (<c>import-sm1-map</c>) and, later, by "Refresh from Classic".
/// </summary>
public sealed record Sm1Gameplay(IReadOnlyList<MapWall> Walls, IReadOnlyList<MapKillRegion> KillRegions, IReadOnlyList<MapProp> Props);

/// <summary>
/// The Classic-link identity of one item of an original map, shared by the import (which writes <see cref="MapObjectSrc"/>) and "Refresh from Classic" (which recomputes the same
/// values to tell new, changed and removed items apart). <b>Key</b> = the item's original name, or <c>definition@x,y,z</c> (position to 0.01 m) when the name is empty or not unique among
/// the same kind, plus <c>#n</c> (n from 2, in list order) for what still ties. <b>Fp</b> = a short stable hash of the Classic values (transform, size, shape, hidden), so it changes when any
/// of them does. Both depend only on the Classic data, never on what the owner did with the object, and are computed over the WHOLE list (not only what an import kept), so the same call
/// later gives the same keys.
/// </summary>
public static class Sm1ClassicKeys
{
    /// <summary>The longest key written (the same limit <see cref="MapObjectSrc"/> reads).</summary>
    public const int MaxKey = MapObjectSrc.MaxText;

    /// <summary>The position part of a fallback key: 0.01 m steps, invariant culture ("-0" never appears).</summary>
    public static string At(Vector3 p) => $"@{Q(p.X)},{Q(p.Y)},{Q(p.Z)}";

    static string Q(float v)
    {
        var r = MathF.Round(v * 100f, MidpointRounding.AwayFromZero) / 100f;
        return (r == 0 ? 0f : r).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>One key per item, in order. <paramref name="items"/>: the original name (may be empty), the label used when the name cannot be (a definition or kind), and the position.</summary>
    public static string[] Keys(IReadOnlyList<(string Name, string Label, Vector3 Position)> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var i in items) counts[i.Name] = counts.GetValueOrDefault(i.Name) + 1;
        var keys = new string[items.Count];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int n = 0; n < items.Count; n++)
        {
            var (name, label, pos) = items[n];
            var key = name.Length > 0 && counts[name] == 1 ? name : (label.Length > 0 ? label : "item") + At(pos);
            if (key.Length > MaxKey) key = key[..MaxKey];
            int count = seen[key] = seen.GetValueOrDefault(key) + 1;
            if (count > 1)
            {
                var tail = "#" + count.ToString(CultureInfo.InvariantCulture);
                key = (key.Length + tail.Length > MaxKey ? key[..(MaxKey - tail.Length)] : key) + tail;
            }
            keys[n] = key;
        }
        return keys;
    }

    public static string[] PlacementKeys(IReadOnlyList<Sm1Object> objects) => Keys([.. objects.Select(o => (o.Name, o.Definition, o.Transform.Translation))]);

    public static string[] WallKeys(IReadOnlyList<MapWall> walls) => Keys([.. walls.Select(w => (w.Name, "wall", w.Position))]);

    public static string[] KillKeys(IReadOnlyList<MapKillRegion> kills) => Keys([.. kills.Select(k => (k.Name, "kill", k.Position))]);

    public static string[] CoverKeys(IReadOnlyList<MapProp> props) =>
        Keys([.. props.Select(p => { var (lo, hi) = CoverBox(p); return (p.Name, p.Definition, (lo + hi) / 2f); })]);

    /// <summary>The axis-aligned box of a movable prop's collision mesh (what an imported cover piece is), or an empty box (low = high = 0) for a prop with no vertices.</summary>
    public static (Vector3 Low, Vector3 High) CoverBox(MapProp p) =>
        p.Vertices.Length == 0 ? (Vector3.Zero, Vector3.Zero) : (p.Vertices.Aggregate(Vector3.Min), p.Vertices.Aggregate(Vector3.Max));

    // ---- fingerprints ----

    static string F(float v)
    {
        var r = MathF.Round(v * 1000f, MidpointRounding.AwayFromZero) / 1000f;
        return (r == 0 ? 0f : r).ToString("0.###", CultureInfo.InvariantCulture);
    }

    static string V(Vector3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    /// <summary>12 hex characters of the SHA-256 of the parts (stable across machines: invariant formatting, values to 0.001).</summary>
    public static string Fp(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))))[..12].ToLowerInvariant();

    public static string PlacementFp(Sm1Object o, bool hidden)
    {
        var m = o.Transform;
        return Fp("placement", o.Definition, hidden ? "hidden" : "shown",
            V(new(m.M11, m.M12, m.M13)), V(new(m.M21, m.M22, m.M23)), V(new(m.M31, m.M32, m.M33)), V(m.Translation));
    }

    public static string WallFp(MapWall w) => Fp("wall", V(w.Position), V(w.RotationDegrees), V(w.Scale), w.Shape.ToString(CultureInfo.InvariantCulture));

    public static string KillFp(MapKillRegion k) => Fp("kill", V(k.Position), F(k.Yaw), V(k.Scale));

    public static string CoverFp(MapProp p) { var (lo, hi) = CoverBox(p); return Fp("cover", V(lo), V(hi)); }
}
