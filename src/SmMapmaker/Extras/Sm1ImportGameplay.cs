using System.Numerics;
using SmContent;

namespace SmMapmaker;

/// <summary>
/// import-sm1-map, the gameplay half (docs/mapmaker.md "Classic link"): Classic's invisible walls become <c>move-wall</c> pieces, its kill regions <c>kill-zone</c> pieces and its movable cover
/// (Havok layer 3 and the movable props) <c>cover</c> boxes (one axis-aligned box per prop), each carrying an editor-only <see cref="MapObjectSrc"/> (map, key, fingerprint, kind).
/// Spawn-room blockers (BlockGroup 3, <c>marine_blocker_*</c> / <c>chaos_blocker_*</c>) are not walls here: <see cref="MapContent.Walls"/> already leaves them out and the spawn import
/// places them as doors. Everything is placed the way the compiler reads it back: a piece's position is the middle of its footprint on the floor, its size the full size, only a turn about Z.
/// </summary>
internal sealed partial class Batch
{
    /// <summary>One planned object, before it gets an id (so the import can count against the caps first).</summary>
    sealed record Planned(string Name, PieceSpec Piece, MapTransform Transform, MapObjectSrc Src);

    /// <summary>What reading the gameplay left out, for the result's warnings.</summary>
    sealed class GameplayCounts
    {
        public int Spheres, Tilted, Degenerate;
    }

    static List<Planned> PlanWalls(string map, Sm1Gameplay g, GameplayCounts counts)
    {
        var keys = Sm1ClassicKeys.WallKeys(g.Walls);
        var list = new List<Planned>();
        for (int i = 0; i < g.Walls.Count; i++)
        {
            var w = g.Walls[i];
            if (w.Shape == 2) { counts.Spheres++; continue; }   // the document has no round-ball wall
            bool cylinder = w.Shape == 3;
            if (!Usable(w.Scale)) { counts.Degenerate++; continue; }
            if (MathF.Abs(Wrap(w.RotationDegrees.X)) > 0.5f || MathF.Abs(Wrap(w.RotationDegrees.Y)) > 0.5f) counts.Tilted++;
            var piece = new PieceSpec(PieceKind.MoveWall, null, cylinder ? PieceSpec.CylinderShape : null);
            var t = Place(w.Position, w.Scale, w.RotationDegrees.Z);
            list.Add(new Planned(w.Name.Length > 0 ? w.Name : piece.DefaultName, piece, t, new MapObjectSrc(map, keys[i], Sm1ClassicKeys.WallFp(w), MapObjectSrc.Wall, false)));
        }
        return list;
    }

    static List<Planned> PlanKills(string map, Sm1Gameplay g, GameplayCounts counts)
    {
        var keys = Sm1ClassicKeys.KillKeys(g.KillRegions);
        var list = new List<Planned>();
        for (int i = 0; i < g.KillRegions.Count; i++)
        {
            var k = g.KillRegions[i];
            if (!Usable(k.Scale)) { counts.Degenerate++; continue; }
            var piece = new PieceSpec(PieceKind.KillZone);
            var t = Place(k.Position, k.Scale, k.Yaw * 180f / MathF.PI);
            list.Add(new Planned(k.Name.Length > 0 ? k.Name : piece.DefaultName, piece, t, new MapObjectSrc(map, keys[i], Sm1ClassicKeys.KillFp(k), MapObjectSrc.Kill, false)));
        }
        return list;
    }

    static List<Planned> PlanCover(string map, Sm1Gameplay g, GameplayCounts counts)
    {
        var keys = Sm1ClassicKeys.CoverKeys(g.Props);
        var list = new List<Planned>();
        for (int i = 0; i < g.Props.Count; i++)
        {
            var p = g.Props[i];
            var (lo, hi) = Sm1ClassicKeys.CoverBox(p);
            var size = hi - lo;
            if (!Usable(size)) { counts.Degenerate++; continue; }
            var piece = new PieceSpec(PieceKind.Cover);
            var t = new MapTransform(TransformMath.Round(new Vector3((lo.X + hi.X) / 2f, (lo.Y + hi.Y) / 2f, lo.Z)), Vector3.Zero, TransformMath.Round(size));
            list.Add(new Planned(p.Name.Length > 0 ? p.Name : piece.DefaultName, piece, t, new MapObjectSrc(map, keys[i], Sm1ClassicKeys.CoverFp(p), MapObjectSrc.Cover, false)));
        }
        return list;
    }

    /// <summary>A marker's centre and full size to a piece's transform: the piece stands on the floor, so z drops by half the height.</summary>
    static MapTransform Place(Vector3 centre, Vector3 size, float yawDegrees) =>
        new(TransformMath.Round(centre - new Vector3(0, 0, size.Z / 2f)), TransformMath.Round(new Vector3(0, 0, yawDegrees)), TransformMath.Round(size));

    static bool Usable(Vector3 size) => size.X >= 0.01f && size.Y >= 0.01f && size.Z >= 0.01f && float.IsFinite(size.X + size.Y + size.Z);

    static float Wrap(float degrees) { var d = degrees % 360f; return d > 180f ? d - 360f : d < -180f ? d + 360f : d; }
}
