using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;
using SmMapmaker;
using SmSim;

namespace SmMapmaker.Tests;

/// <summary>
/// A hostile or damaged <c>.smmap</c> whose stored mesh has no inside (M5 review): a flat shape (one plane, one line, one point) is refused when the file is read (<c>bad-file</c>), and a
/// thin one (thinner than the 1/4096 m grid once sized) is left out by the compiler with a note. Neither crashes <c>smtool map check</c> (<see cref="MapCheck"/>), the server's
/// <c>--custom-map</c> hook or the simulation's record checks. One case per marker kind, plus a solid piece. Synthetic maps only.
/// </summary>
public sealed class DegenerateMeshTests
{
    const string Floor = """{"op":"place","piece":{"kind":"box"},"name":"floor","transform":{"position":[0,0,-1],"scale":[80,80,1]}}""";

    public static TheoryData<string, string> Kinds => new()
    {
        { "blocker", """{"kind":"blocker","side":"marines"}""" },
        { "spawn-area", """{"kind":"spawn-area","side":"marines"}""" },
        { "capture-point", """{"kind":"capture-point"}""" },
        { "kill-zone", """{"kind":"kill-zone"}""" },
        { "move-wall", """{"kind":"move-wall"}""" },
        { "cover", """{"kind":"cover"}""" },
        { "box", """{"kind":"box"}""" },
    };

    /// <summary>A map with a floor and one <paramref name="piece"/> (o2), its stored mesh the stock shape with every vertex mapped by <paramref name="shape"/>, as file text.</summary>
    static string FileWith(string piece, Func<Vector3, Vector3> shape, string scale = "[6,4,3]")
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        foreach (var op in new[] { Floor, "{\"op\":\"place\",\"piece\":" + piece + ",\"name\":\"bad\",\"transform\":{\"position\":[0,0,0],\"scale\":" + scale + "}}" })
        {
            var r = s.Apply(op);
            Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Code + ": " + e.Message)));
        }
        var doc = s.Document;
        var o = doc.Objects.Single(x => x.Name == "bad");
        var stock = EditMesh.Stock(o.Piece!);
        var mesh = stock.WithVertices(stock.Vertices.Select(shape).ToArray());
        var node = JsonNode.Parse(doc.ToJson())!.AsObject();
        node["formatVersion"] = MapDocument.FormatVersion;
        var obj = node["objects"]!.AsArray().Single(x => (string?)x!["name"] == "bad")!.AsObject();
        obj["mesh"] = mesh.ToNode();
        return node.ToJsonString();
    }

    static readonly Func<Vector3, Vector3> FlatZ = v => v with { Z = 0.5f };
    static readonly Func<Vector3, Vector3> OnePoint = _ => new Vector3(0.1f, 0.2f, 0.3f);
    static readonly Func<Vector3, Vector3> Line = v => new Vector3(v.X, 0f, 0.5f);
    /// <summary>Not flat in unit space, but 3e-6 m tall at scale 3: below the 1/4096 m grid once placed.</summary>
    static readonly Func<Vector3, Vector3> Thin = v => v with { Z = v.Z * 1e-6f };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_flat_stored_mesh_is_refused_as_a_bad_file(string kind, string piece)
    {
        foreach (var shape in new[] { FlatZ, OnePoint, Line })
        {
            var e = Assert.Throws<MapException>(() => MapDocument.Parse(FileWith(piece, shape)));
            Assert.Equal("bad-file", e.Code);
            Assert.Contains("flat", e.Message);
            _ = kind;
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_flat_stored_mesh_makes_the_server_refuse_cleanly(string kind, string piece)
    {
        using var lab = new Lab();
        string file = Path.Combine(lab.Dir, kind + ".smmap");
        File.WriteAllText(file, FileWith(piece, OnePoint));
        string? name = null;
        var map = SmServer.GameServer.CustomMap(file, ref name, out var error);
        Assert.Null(map);
        Assert.Contains("cannot play", error);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_thin_stored_mesh_compiles_checks_and_plays_without_crashing(string kind, string piece)
    {
        using var lab = new Lab();
        string file = Path.Combine(lab.Dir, kind + ".smmap");
        File.WriteAllText(file, FileWith(piece, Thin));
        var doc = MapDocument.Parse(File.ReadAllText(file));   // not flat: the file is readable
        var map = CustomMaps.Load(file);
        bool marker = kind is not ("box" or "cover");
        if (marker)
        {
            Assert.Contains(map.Notes, n => n.Contains("'bad'") && n.Contains("no inside"));
            Assert.DoesNotContain("o2", map.RecordIds.Values);
            Assert.Empty(map.Volumes);
        }
        var report = MapCheck.Run(map);   // smtool map check's path
        Assert.NotNull(report.ToText());
        var c = map.Content;
        var world = new World(new SimConfig(), map.ToCollisionWorld(), c.SpawnPoints()) { StartAreas = c.SimStartAreas(), ControlPoints = c.SimControlPoints(), SpawnBlockers = c.SimBlockers() };
        Assert.NotNull(world);
        string? name = null;
        var game = SmServer.GameServer.CustomMap(file, ref name, out var error);
        Assert.Null(error);
        Assert.NotNull(game);
        Assert.Equal(CustomMap.Compile(doc).Hash, game!.Hash);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_stock_marker_scaled_far_below_a_centimetre_compiles_checks_and_plays(string kind, string piece)
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        foreach (var op in new[] { Floor, "{\"op\":\"place\",\"piece\":" + piece + ",\"name\":\"bad\",\"transform\":{\"position\":[0,0,0],\"scale\":[6,4,0.00001]}}" })
            Assert.True(s.Apply(op).Ok);
        var map = CustomMap.Compile(s.Document);
        _ = kind;   // an unedited marker's record keeps its (tiny, positive) size: the simulation accepts it
        _ = MapCheck.Run(map).ToText();
        string file = Path.Combine(lab.Dir, kind + ".smmap");
        File.WriteAllText(file, s.Document.ToJson());
        string? name = null;
        Assert.NotNull(SmServer.GameServer.CustomMap(file, ref name, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Set_mesh_still_refuses_a_flat_shape_as_bad_mesh()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Assert.True(s.Apply("""{"op":"place","piece":{"kind":"blocker","side":"marines"},"transform":{"position":[0,0,0],"scale":[6,4,3]}}""").Ok);
        var stock = EditMesh.Stock(s.Document.Objects[0].Piece!);
        var flat = stock.WithVertices(stock.Vertices.Select(FlatZ).ToArray());
        var r = s.Apply($$"""{"op":"set-mesh","id":"o1","mesh":{{flat.ToNode().ToJsonString()}}}""");
        Assert.False(r.Ok);
        Assert.Equal("bad-mesh", r.Errors[0].Code);
    }
}
