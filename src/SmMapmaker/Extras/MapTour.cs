using System.Globalization;
using System.Numerics;

namespace SmMapmaker;

/// <summary>Camera places for screenshots of a custom map (test aid, derived from the map's own spawns and points): <c>client --observe-tour</c> takes the poses as <c>x,y,z,yaw,pitch</c>.</summary>
public static class MapTour
{
    public sealed record Place(string Label, string Pose);

    static string Pose(Vector3 at, float yawDegrees, float pitchDegrees) =>
        string.Create(CultureInfo.InvariantCulture, $"{at.X:F1},{at.Y:F1},{at.Z:F1},{yawDegrees:F0},{pitchDegrees:F0}");

    static float Yaw(Vector3 from, Vector3 to) => MathF.Atan2(-(to.X - from.X), to.Y - from.Y) * 180f / MathF.PI;

    public static IReadOnlyList<Place> Places(CustomMap map)
    {
        var c = map.Content;
        var world = map.ToCollisionWorld();
        var spots = c.StartAreas.ToDictionary(a => a.Side, a => MapCheck.SpotsIn(a, world.Character));
        var marines = c.StartAreas.FirstOrDefault(a => a.Side == SmSim.Team.SpaceMarines);
        var chaos = c.StartAreas.FirstOrDefault(a => a.Side == SmSim.Team.Chaos);
        var mid = map.SolidBounds is { } b ? (b.Min + b.Max) / 2f : Vector3.Zero;
        var places = new List<Place>();
        Vector3 Floor(SmContent.MapStartArea a) => spots.TryGetValue(a.Side, out var s) && s.Count > 0 ? s[0] : a.Position;
        if (marines is not null && chaos is not null)
        {
            var m = Floor(marines); var h = Floor(chaos);
            places.Add(new("Marine spawn, looking out", Pose(m + new Vector3(0, 0, 1.7f), Yaw(m, h), 0)));
            places.Add(new("Chaos spawn, looking out", Pose(h + new Vector3(0, 0, 1.7f), Yaw(h, m), 0)));
        }
        for (int i = 0; i < c.ControlPoints.Count; i++)
        {
            var p = c.ControlPoints[i].Position - new Vector3(0, 0, c.ControlPoints[i].Scale.Z / 2f);
            places.Add(new($"Point {(char)('A' + i)}, eye height", Pose(p + new Vector3(0, 0, 1.7f), Yaw(p, mid), 0)));
        }
        float height = map.SolidBounds is { } sb ? MathF.Max(sb.Max.X - sb.Min.X, sb.Max.Y - sb.Min.Y) * 0.55f : 40f;
        places.Add(new("Overview from above", Pose(mid + new Vector3(0, -height * 0.35f, height), 0, -62)));
        return places;
    }
}
