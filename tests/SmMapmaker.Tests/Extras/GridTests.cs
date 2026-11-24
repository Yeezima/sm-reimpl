using System.Text.Json.Nodes;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>The Mapmaker's 3D grid settings: editor-only data of the .smmap (defaults, set-grid, undo, round trip, old files, and no effect on the compiled map or its hash).</summary>
public class GridTests
{
    static OpResult Run(MapSession s, string json) => s.Apply(Lab.Op(json));

    [Fact]
    public void ANewMapHasTheDefaultGridAndWritesNoGridKey()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Assert.Equal(MapGrid.Default, s.Document.Grid);
        Assert.True(MapGrid.Default.Show && MapGrid.Default.CellSize == 1f && MapGrid.Default.Height == 0f && !MapGrid.Default.DrawOver && MapGrid.Default.Colour == "#80808080");
        Assert.DoesNotContain("\"grid\"", s.Document.ToJson());
    }

    [Fact]
    public void SetGridChangesNamedFieldsAndRefusesBadValues()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Assert.True(Run(s, """{"op":"set-grid","cellSize":2.5,"height":3,"colour":"#ff000040","drawOver":true,"show":false}""").Ok);
        Assert.Equal(new MapGrid(false, "#ff000040", 2.5f, 3f, true), s.Document.Grid);
        Assert.True(Run(s, """{"op":"set-grid","height":-1.5}""").Ok);
        Assert.Equal(new MapGrid(false, "#ff000040", 2.5f, -1.5f, true), s.Document.Grid);
        foreach (var bad in new[] { """{"op":"set-grid","cellSize":0}""", """{"op":"set-grid","cellSize":100.5}""", """{"op":"set-grid","cellSize":"a"}""", """{"op":"set-grid","colour":"red"}""", """{"op":"set-grid","show":"yes"}""", """{"op":"set-grid","height":1e9}""" })
            Assert.Equal("bad-op", Run(s, bad).Errors[0].Code);
        Assert.Equal(-1.5f, s.Document.Grid.Height);   // a refused batch changes nothing
    }

    [Fact]
    public void GridEditsMarkTheMapDirtyAndUndoRedoOneStepEach()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Run(s, """{"op":"set-grid","cellSize":2}""");
        Run(s, """{"op":"set-grid","cellSize":4}""");
        Assert.True(s.Dirty);
        Assert.True(s.Undo().Ok);
        Assert.Equal(2f, s.Document.Grid.CellSize);
        Assert.True(s.Undo().Ok);
        Assert.Equal(MapGrid.Default, s.Document.Grid);
        Assert.True(s.Redo().Ok); Assert.True(s.Redo().Ok);
        Assert.Equal(4f, s.Document.Grid.CellSize);
    }

    [Fact]
    public void TheGridRoundTripsThroughSaveAndOpenAtTheCurrentFormatVersion()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Run(s, """{"op":"set-grid","cellSize":0.5,"height":2.25,"colour":"#336699cc","drawOver":true,"show":false}""");
        var path = Path.Combine(lab.Dir, "grid.smmap"); s.Save(path);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(MapDocument.BaseFormatVersion, (int)node["formatVersion"]!);
        Assert.NotNull(node["grid"]);
        var loaded = MapSession.Load(path, lab.Library);
        Assert.Equal(s.Document.Grid, loaded.Document.Grid);
        Assert.Equal(s.Document.ToJson(), loaded.Document.ToJson());
    }

    [Fact]
    public void OldMapsWithoutGridDataOpenWithTheDefaults()
    {
        using var lab = new Lab();
        var json = lab.Session().Document.ToJson();
        var doc = MapDocument.Parse(json);
        Assert.Equal(MapGrid.Default, doc.Grid);
        var v1 = JsonNode.Parse(json)!.AsObject(); v1["formatVersion"] = 1;
        Assert.Equal(MapGrid.Default, MapDocument.Parse(v1.ToJsonString()).Grid);
    }

    [Fact]
    public void OddGridDataInAFileIsRepairedNotRefused()
    {
        using var lab = new Lab();
        var node = JsonNode.Parse(lab.Session().Document.ToJson())!.AsObject();
        node["grid"] = JsonNode.Parse("""{"show":"x","colour":"nope","cellSize":-4,"height":1e30,"drawOver":true}""");
        var g = MapDocument.Parse(node.ToJsonString()).Grid;
        Assert.True(g.Show);
        Assert.Equal("#80808080", g.Colour);
        Assert.Equal(MapGrid.MinCell, g.CellSize);
        Assert.Equal(MapGrid.MaxHeight, g.Height);
        Assert.True(g.DrawOver);
        node["grid"] = 5;
        Assert.Equal(MapGrid.Default, MapDocument.Parse(node.ToJsonString()).Grid);
    }

    [Fact]
    public void GridSettingsDoNotChangeTheCompiledMapOrItsHash()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Assert.True(Run(s, """{"op":"place","piece":{"kind":"box"},"name":"Block","transform":{"position":[0,0,0]}}""").Ok);
        var plain = CustomMap.Compile(s.Document);
        Run(s, """{"op":"set-grid","cellSize":7,"height":12,"colour":"#11223344","drawOver":true,"show":false}""");
        Assert.Contains("\"grid\"", s.Document.ToJson());   // the file has it ...
        Assert.DoesNotContain("\"grid\"", s.Document.ToGameJson());   // ... the game-side text does not
        var withGrid = CustomMap.Compile(s.Document);
        Assert.Equal(plain.Hash, withGrid.Hash);
        Assert.Equal(CustomMap.HashOf(MapDocument.Parse(s.Document.ToJson())), plain.Hash);
        Assert.Equal(plain.SolidCount, withGrid.SolidCount);
        Assert.Equal(plain.SolidBounds, withGrid.SolidBounds);
    }
}
