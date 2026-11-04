using System.Numerics;
using SmFormats.Havok;
using SmSim;
using SmSim.Extras.Musou.Ashfall;

namespace SmContent.Extras.Musou;

/// <summary>
/// Ashfall Citadel as a <see cref="MapContent"/> (G1, docs/musou-ashfall-graybox.md): the generated level in the shape an install's map has, so the server's war
/// (<see cref="TfwWorld"/>) and the client's map loader take it the way they take Manufactorum. Nothing is read from the install. Shared contract of G1.
/// </summary>
public static class AshfallContent
{
    /// <summary>The map name the content carries (not the war's map name, which stays <c>tfw_war</c>).</summary>
    public const string MapName = "ashfall_citadel";

    /// <summary>
    /// The level as map content: the solid triangles as both the character layer and the weapon layer (unwelded: three corners a triangle), the capture points as
    /// cylinder markers (scale: diameter, diameter, full height), the start areas as box markers, the spawns facing north. <see cref="MapContent.Hash"/> is the level's hash.
    /// </summary>
    public static MapContent ToMapContent(AshfallLevel level)
    {
        var corners = level.SolidCorners();
        var indices = new int[corners.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        var character = new HavokCollisionLayer(MapContent.CharacterLayer, corners, indices);
        var weapons = new HavokCollisionLayer(MapContent.WeaponLayer, corners, indices);
        return new MapContent(MapName, MapName, [character, weapons], level.Spawns.Select((s, i) => new MapSpawn($"ashfall_spawn_{i}", s, 0f)).ToArray(), level.Hash)
        {
            ControlPoints = level.Points.Select(p => new MapControlPoint(p.Name, p.Center, 0f, new Vector3(p.Radius * 2f, p.Radius * 2f, p.HalfHeight * 2f), 3)).ToArray(),
            StartAreas = level.Areas.Select(a => new MapStartArea(a.Side == Team.SpaceMarines ? "mkr_start_marine" : "mkr_start_ork", a.Side, a.Center, a.Yaw, a.Size, a.Shape == AreaShape.Cylinder ? 3 : 1)).ToArray(),
        };
    }
}
