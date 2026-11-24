using System.Numerics;
using SmContent;
using SmMapmaker;
using SmSim;

namespace SmMapmaker.Tests;

/// <summary>
/// Edit mode in play (docs/mapmaker.md "Edit mode in play"; owner-approved brief 2026-11-25 "Blender-style controls", plan M2): what the editor shows is what plays. Edited
/// pieces collide by their mesh, edited markers act by their exact shape (inside the shape activates, inside only the bounding box does not), spawn spots and doors follow the
/// edited boundary, and an unedited map is bit for bit what it was. Synthetic maps only.
/// </summary>
public sealed class EditRuntimeTests
{
    static ulong Fnv(ulong h, uint v) { for (int i = 0; i < 4; i++) { h ^= (v >> (8 * i)) & 0xFF; h *= 0x100000001B3UL; } return h; }
    static ulong Fnv(ulong h, ulong v) => Fnv(Fnv(h, (uint)(v & 0xFFFFFFFF)), (uint)(v >> 32));
    static ulong Fnv(ulong h, float f) => Fnv(h, BitConverter.SingleToUInt32Bits(f));
    static ulong Fnv(ulong h, Vector3 v) => Fnv(Fnv(Fnv(h, v.X), v.Y), v.Z);
    const ulong Offset = 0xCBF29CE484222325UL;

    static MapSession Layout(Lab lab, string id)
    {
        var s = lab.Session(withSm1: false);
        var r = s.Apply(MapTemplate.Find(id)!.Ops());
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
        return s;
    }

    /// <summary>Everything the match reads from a compiled map: the hash, the collision world's hash and side meshes, spots, facings, point presence and kill tests.</summary>
    static ulong PlayFingerprint(CustomMap map)
    {
        ulong h = Fnv(Offset, map.Hash);
        var c = map.Content;
        var world = c.ToCollisionWorld();
        h = Fnv(h, world.ContentHash);
        foreach (var side in new[] { Team.SpaceMarines, Team.Chaos, Team.None }) h = Fnv(h, world.CharacterFor(side).ContentHash);
        var sim = new World(new SimConfig(), world, c.SpawnPoints()) { StartAreas = c.SimStartAreas(), ControlPoints = c.SimControlPoints(), SpawnBlockers = c.SimBlockers() };
        for (int i = 0; i < sim.StartAreas.Count; i++)
        {
            foreach (var s in sim.StartAreaSpots(i)) h = Fnv(h, s);
            foreach (var y in sim.StartAreaFacings(i).Yaws) h = Fnv(h, y);
            foreach (var s in MapCheck.SpotsIn(c.StartAreas[i], world.Character)) h = Fnv(h, s);
        }
        var bounds = map.SolidBounds!.Value;
        for (float x = bounds.Min.X; x <= bounds.Max.X; x += 1.7f)
            for (float y = bounds.Min.Y; y <= bounds.Max.Y; y += 1.7f)
                for (float z = -1f; z <= 8f; z += 1.1f)
                {
                    var p = new Vector3(x, y, z);
                    foreach (var cp in sim.ControlPoints) h = Fnv(h, cp.Contains(p) ? 1u : 0u);
                    foreach (var k in world.KillVolumes) h = Fnv(h, k.Contains(p) ? 1u : 0u);
                    foreach (var b in world.Blockers) h = Fnv(h, b.Contains(p) ? 1u : 0u);
                }
        foreach (var line in MapCheck.Run(map).ToText().Split('\n')) foreach (char ch in line) h = Fnv(h, (uint)ch);
        return h;
    }

    // ---- an unedited map is bit for bit what it was (pinned before M2 wired the volumes in) ----

    [Theory]
    [InlineData("arena", 12107027520657218078UL)]
    [InlineData("open-field", 7892627044423593843UL)]
    [InlineData("blank", 4029968937587745437UL)]
    public void An_unedited_layout_plays_exactly_as_before(string id, ulong expected)
    {
        using var lab = new Lab();
        var map = CustomMap.Compile(Layout(lab, id).Document);
        Assert.Empty(map.Volumes);
        Assert.Equal(expected, PlayFingerprint(map));
    }
}
