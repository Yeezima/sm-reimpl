using System.Numerics;
using System.Text;
using SmContent;
using SmFormats.Bod;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;
using static SmFormats.Bod.SyntheticBodWriter;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Seize Ground content (M16) from a synthetic install: control points named by the region script's strings and found as
/// cylinder markers in the d_mission layers, start areas, notes, the fallback without a script, the hash, and the
/// Seize Ground script name check. The "script" is a made-up byte buffer holding only names chosen here.
/// </summary>
public class SeizeGroundContentTests
{
    static readonly CollisionMesh Floor = new([new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)], [(0, 1, 2), (0, 2, 3)]);
    static byte[] Collision() => BuildWorldCollision([new CollisionVariant("2", [Floor], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)]),
        new CollisionVariant("8", [Floor], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)])]);

    /// <summary>A marker as the mission layers hold them (class, Name, Position, Rotation in degrees, Scale, Shape, Disabled).</summary>
    static Node Marker(string name, float[] position, float[] scale, int shape, float yawDegrees = 0f, bool disabled = false, string cls = "base/missionmarkersphere") =>
        new Ref(new Struct(cls, ("MarkerShape", new I(shape)), ("Position", new Vec(position)), ("Rotation", new Vec(0f, 0f, yawDegrees)), ("Scale", new Vec(scale)),
            ("Shape", new I(shape)), ("Name", new S(name)), ("Disabled", new B(disabled))));

    static byte[] Layer(params Node[] markers) =>
        Document(new Struct("RegionLayer", ("ID", new I(26)), ("Root", new Ref(new Struct("WorldGroup", ("Name", new S("")), ("Objects", new List(markers)))))));

    /// <summary>A stand-in for a compiled region script: a Lua 5.1 signature and length-prefixed, NUL-terminated strings.</summary>
    static byte[] Script(params string[] strings)
    {
        var b = new List<byte>([0x1B, 0x4C, 0x75, 0x61, 0x51, 0, 1, 4, 4, 4, 8, 0]);
        foreach (var s in strings) { b.Add(4); b.AddRange(BitConverter.GetBytes(s.Length + 1)); b.AddRange(Encoding.ASCII.GetBytes(s)); b.Add(0); }
        return [.. b];
    }

    static string Install(string map, byte[]? script, params (string Name, byte[] Data)[] layers)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-seize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        var folder = $"\\mp\\{map}\\wrld\\";
        var files = new List<SyntheticObpkWriter.File> { new("hkt", "wrld", Collision()) };
        foreach (var (name, data) in layers) files.Add(new("Layer", name, data, folder));
        if (script is not null) files.Add(new("lua", "wrld", script, folder));
        File.WriteAllBytes(Path.Combine(dir, "data", "chunks", $"{map}__wrld.oppc"), SyntheticObpkWriter.Build(files));
        return dir;
    }

    static readonly Node[] Markers =
    [
        Marker("mkr_start_marine", [245f, -937.38f, 5.5f], [10f, 8f, 5f], 1),
        Marker("mkr_Start_Ork", [154.5f, -664.4f, 5.55f], [10f, 8f, 5f], 1, 180f),
        Marker("mkr_objective_2", [200f, -800f, 0f], [27f, 27f, 20f], 3, 180f),
        Marker("mkr_objective_1", [110f, -785f, 4f], [30f, 30f, 20f], 3, -90f),
        Marker("mkr_objective_3", [290f, -815f, 4f], [30f, 30f, 20f], 3, cls: "base/missionmarkercylinder"),
        Marker("mkr_objective_4", [430f, -755f, 0f], [1f, 1f, 1f], 2),          // a small sphere the script does not list
        Marker("mkr_objective_1_cam", [1f, 2f, 3f], [1f, 1f, 1f], 2),
    ];

    [Fact]
    public void Points_are_the_scripts_marker_names_in_its_order_and_start_areas_come_by_side()
    {
        string dir = Install("mp_test", Script("require", "GetDefaultVersusObjectives", "mkr_objective_1", "mkr_objective_2", "mkr_objective_3", "marine_flag", "mkr_objective_1_cam"),
            ("d_mission_objects", Layer(Markers)));
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal(["mkr_objective_1", "mkr_objective_2", "mkr_objective_3"], map.ControlPoints.Select(p => p.Name));
            var a = map.ControlPoints[0];
            Assert.Equal((new Vector3(110f, -785f, 4f), new Vector3(30f, 30f, 20f), 3), (a.Position, a.Scale, a.Shape));
            Assert.Equal(-MathF.PI / 2, a.Yaw, 5);
            var sim = map.SimControlPoints();
            Assert.Equal(new ControlPoint("mkr_objective_1", new Vector3(110f, -785f, 4f), 15f, 10f), sim[0]);   // radius half the X scale, height centred
            Assert.Equal(13.5f, sim[1].Radius);
            Assert.Empty(map.Notes);
            Assert.Equal([("mkr_start_marine", Team.SpaceMarines), ("mkr_Start_Ork", Team.Chaos)], map.StartAreas.Select(s => (s.Name, s.Side)));
            var areas = map.SimStartAreas();
            Assert.Equal(new StartArea(Team.SpaceMarines, new Vector3(245f, -937.38f, 5.5f), new Vector3(10f, 8f, 5f), 0f, AreaShape.Box), areas[0]);
            Assert.Equal(MathF.PI, areas[1].Yaw, 5);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Script_order_counts_and_names_that_are_not_cylinders_or_missing_are_noted()
    {
        string dir = Install("mp_test", Script("mkr_cp_03", "mkr_objective_4", "mkr_cp_01", "mkr_cp_09", "xmkr_cp_05", "mkr_cp_01"),
            ("d_mission", Layer(
                Marker("mkr_cp_01", [1f, 2f, 3f], [25f, 25f, 20f], 3, cls: "base/missionmarkercylinder"),
                Marker("mkr_cp_03", [4f, 5f, 6f], [26f, 25f, 10f], 3, cls: "base/missionmarkercylinder"),
                Marker("mkr_cp_05", [0f, 0f, 0f], [20f, 20f, 10f], 3),
                Marker("mkr_objective_4", [0f, 0f, 0f], [1f, 1f, 1f], 2),
                Marker("mkr_cp_07", [0f, 0f, 0f], [20f, 20f, 10f], 3, disabled: true),
                Marker("mkr_start_marine", [0f, 0f, 0f], [10f, 10f, 1f], 3))));
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal(["mkr_cp_03", "mkr_cp_01"], map.ControlPoints.Select(p => p.Name));   // script order; a repeat is ignored; "xmkr_cp_05" is no name
            Assert.Contains(map.Notes, n => n.Contains("mkr_objective_4") && n.Contains("not a cylinder"));
            Assert.Contains(map.Notes, n => n.Contains("mkr_cp_09") && n.Contains("no mission layer"));
            Assert.Contains(map.Notes, n => n.Contains("mkr_cp_03") && n.Contains("X and Y scale differ"));
            Assert.Contains(map.Notes, n => n.Contains("mkr_start_ork"));
            var area = Assert.Single(map.SimStartAreas());
            Assert.Equal((Team.SpaceMarines, AreaShape.Cylinder), (area.Side, area.Shape));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Without_a_script_the_named_cylinders_are_the_points_in_name_order_and_at_most_eight_are_kept()
    {
        var many = Enumerable.Range(1, 10).Select(i => Marker($"mkr_objective_{i}", [i, 0f, 0f], [10f, 10f, 4f], 3)).Reverse().ToArray();
        string dir = Install("mp_test", null, ("d_mission_objects", Layer([.. many, Marker("mkr_objective_x", [0f, 0f, 0f], [9f, 9f, 9f], 3)])));
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal(Enumerable.Range(1, 8).Select(i => $"mkr_objective_{i}"), map.ControlPoints.Select(p => p.Name));
            Assert.Contains(map.Notes, n => n.Contains("no region script"));
            Assert.Contains(map.Notes, n => n.Contains("more than 8"));
            Assert.Empty(map.StartAreas);
        }
        finally { Directory.Delete(dir, recursive: true); }

        string none = Install("mp_test", null);
        try { Assert.Empty(MapContent.Load(none, "mp_test").ControlPoints); }
        finally { Directory.Delete(none, recursive: true); }
    }

    [Fact]
    public void The_hash_covers_the_mission_layers_and_the_script()
    {
        var layer = Layer(Markers);
        var script = Script("mkr_objective_1", "mkr_objective_2");
        string a = Install("mp_test", script, ("d_mission_objects", layer));
        string b = Install("mp_test", script, ("d_mission_objects", layer));
        string c = Install("mp_test", Script("mkr_objective_2", "mkr_objective_1"), ("d_mission_objects", layer));
        string d = Install("mp_test", script, ("d_mission_objects", Layer([.. Markers[..^1], Marker("mkr_objective_1_cam", [1f, 2f, 4f], [1f, 1f, 1f], 2)])));
        string e = Install("mp_test", null, ("d_mission_objects", layer));
        try
        {
            ulong h = MapContent.Load(a, "mp_test").Hash;
            Assert.Equal(h, MapContent.Load(b, "mp_test").Hash);
            Assert.NotEqual(h, MapContent.Load(c, "mp_test").Hash);
            Assert.NotEqual(h, MapContent.Load(d, "mp_test").Hash);
            Assert.NotEqual(h, MapContent.Load(e, "mp_test").Hash);
            Assert.Equal(8, MapContent.LoaderVersion);   // M18: characters use layers 2 + 8, kill regions; M22: spawn-room blockers; 5: blockers solid for the other side; 6: M25a surface materials; 7: M31 blockers reach above the level; 8: M39 props
        }
        finally { foreach (var x in new[] { a, b, c, d, e }) Directory.Delete(x, recursive: true); }
    }

    [Fact]
    public void The_Seize_Ground_script_names_are_found_per_script()
    {
        var scripts = new Dictionary<string, byte[]>
        {
            [GameModes.ObjectivesScript.ToUpperInvariant()] = Script("Domination", "DominationTimer", "Dominate", "captureDuration", "decapDuration", "scoreSpeed"),
            [GameModes.DominateScript] = Script("Network_AddCapturePoint", "DominateUpdate", "owningTeam"),
        };
        var found = GameModes.FindSeizeGroundMarkers(scripts);
        Assert.Equal(GameModes.SeizeGroundMarkers.Count - 1, found.Count);
        Assert.DoesNotContain($"{GameModes.DominateScript}|Network_GetScoreLimit", found);
        // A name in the wrong script does not count.
        Assert.Empty(GameModes.FindSeizeGroundMarkers(new Dictionary<string, byte[]> { [GameModes.DominateScript] = Script("Domination", "captureDuration") }));
    }
}
