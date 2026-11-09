using System.Numerics;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Musou.Ashfall;

public static partial class AshfallMap
{
    /// <summary>
    /// The field layout's graybox (draft 11, docs/musou-ashfall-graybox.md "Draft 11 battlefield layout"): one flat ground at <see cref="BlockTop"/> (no street grid, no kerbs), the boundary and its backdrop,
    /// the stronghold exactly as draft 10 builds it (ring, keep, outerworks, towers: the same groups and parts), the Imperial compound's walls, and the plan's island structures as single boxes at two thirds of
    /// their final size (the kit pass grows every placed object 1.5x, draft 6). Objective discs last. Its structure groups feed <c>AshfallDraft5Pass</c> as the v5 groups do.
    /// </summary>
    internal static AshfallBuild BuildFieldGraybox(AshfallFieldPlan plan)
    {
        var b = new AshfallBuilder();
        b.Box(Ground, -Half, -Half, -2f, Half, Half, BlockTop);
        BoundaryLayout(b);
        StrongholdLayout(b);
        // The Imperial compound's walls (draft 10's, unchanged); the two road-cover fragments north of its gate are left out (they stood in the base's assembly area).
        using (b.Group("compound south wall", "wallrun")) b.Box(Wall, -36f, -216f, 0f, 36f, -215f, 2.5f);
        using (b.Group("compound west wall", "wallrun")) b.Box(Wall, -36f, -215f, 0f, -35f, -161f, 2.5f);
        using (b.Group("compound east wall", "wallrun")) b.Box(Wall, 35f, -215f, 0f, 36f, -161f, 2.5f);
        using (b.Group("compound north wall west", "wallrun")) b.Box(Wall, -36f, -161f, 0f, -8f, -160f, 2.5f);
        using (b.Group("compound north wall east", "wallrun")) b.Box(Wall, 8f, -161f, 0f, 36f, -160f, 2.5f);

        foreach (var s in plan.Spawns) b.Reserved.Add(new Vector3(s.X, s.Y, 4f));
        foreach (var p in plan.Points) b.Reserved.Add(new Vector3(p.Center.X, p.Center.Y, 4f));
        var placer = new AshfallPlacer(b);
        StrongholdTowers(placer);

        const float g = 1f / Draft6Scale;
        foreach (var s in plan.Structures)
        {
            var kind = s.Family switch { "cover" => Cover, "rubble" => Rubble, "platform" => Floor, _ => Wall };
            using (b.Group(s.Name, s.Family))
                b.Oriented(kind, s.Centre.X, s.Centre.Y, BlockTop, BlockTop + s.Size.Z * g, s.Size.X * g, s.Size.Y * g, s.YawStep);
        }
        foreach (var p in plan.Points) b.Disc(p.Center.X, p.Center.Y, BlockTop + 0.05f, p.Radius);
        var features = new AshfallFeatures(placer.Platforms, placer.Towers, placer.Ruins, 0, 0f, 0, placer.Points);
        var level = new AshfallLevel(b.Parts, plan.Points, plan.Areas, plan.Spawns, HashOf(b.Parts, plan.HashVersion)) { Layout = plan.Layout };
        return new AshfallBuild(level, features, b.PartGroups(), b.Groups);
    }

    /// <summary>Set while a field-layout pass runs on this thread: <see cref="SurfaceAt(float, float)"/> answers for the field's flat ground.</summary>
    [ThreadStatic] static bool t_field;

    internal readonly struct FieldScope : IDisposable
    {
        readonly bool _was;
        public FieldScope(bool on) { _was = t_field; t_field = on; }
        public void Dispose() => t_field = _was;
    }

    /// <summary>The ground's height at a plan point of <paramref name="level"/>'s layout (the field: <see cref="BlockTop"/> everywhere; draft 10: <see cref="SurfaceAt(float, float)"/>).</summary>
    public static float SurfaceAt(AshfallLevel level, float x, float y) => level.Layout.Planned() ? BlockTop : SurfaceAtV5(x, y);

    /// <summary>The ground function of a layout.</summary>
    public static Func<float, float, float> GroundOf(AshfallLayout layout) => layout.Planned() ? (_, _) => BlockTop : SurfaceAtV5;
}
