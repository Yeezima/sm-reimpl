using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>A failed storage step in a committed batch is rolled back: the library, the map and the undo/redo stacks end as they were before the batch (codebase fix 8).</summary>
public class CommitRollbackTests
{
    static MapSession Scene(Lab lab)
    {
        var s = lab.Session();
        lab.ImportBox(s, "pillar");
        lab.ImportBox(s, "other");
        Assert.True(s.Apply([
            Lab.Op("""{"op":"place","asset":{"source":"custom","id":"pillar"},"name":"left","transform":{"position":[100,50,0],"rotation":[0,0,90]}}"""),
            Lab.Op("""{"op":"place","asset":{"source":"custom","id":"other"},"name":"a","transform":{"position":[10,50,0],"rotation":[0,0,90]}}"""),
            Lab.Op("""{"op":"place","asset":{"source":"custom","id":"other"},"name":"b","transform":{"position":[14,50,0],"rotation":[0,0,90]}}"""),
        ]).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o2","o3"],"prefab":"pa","name":"Pa","origin":"first"}""")).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o2","o3"],"prefab":"pb","name":"Pb","origin":"first"}""")).Ok);
        // one entry on the redo side, so the test can see the batch leave it alone
        Assert.True(s.Apply(Lab.Op("""{"op":"place","asset":{"source":"custom","id":"pillar"},"name":"tmp","transform":{"position":[0,0,0]}}""")).Ok);
        Assert.True(s.Undo().Ok);
        return s;
    }

    static List<System.Text.Json.Nodes.JsonObject> Batch(Lab lab) =>
    [
        Lab.Op($$"""{"op":"import-asset","path":{{System.Text.Json.JsonSerializer.Serialize(lab.WriteGltf("boxa.gltf"))}},"id":"boxa"}"""),
        Lab.Op($$"""{"op":"import-asset","path":{{System.Text.Json.JsonSerializer.Serialize(lab.WriteGltf("boxb.gltf"))}},"id":"boxb"}"""),
        Lab.Op("""{"op":"delete","ids":["o1"]}"""),
        Lab.Op("""{"op":"delete-asset","asset":"pillar"}"""),
        Lab.Op("""{"op":"prefab-edit","prefab":"pa","name":"Renamed"}"""),
        Lab.Op("""{"op":"delete-prefab","prefab":"pb"}"""),
    ];

    static SortedDictionary<string, string> Tree(MapLibrary lib) =>
        new(Directory.Exists(lib.Root)
            ? Directory.EnumerateFiles(lib.Root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(lib.Root, f), f => Convert.ToHexString(File.ReadAllBytes(f)))
            : []);

    [Theory]
    [InlineData("asset-write", 3)]   // the second asset's first file
    [InlineData("asset-write", 4)]   // the second asset's asset.json
    [InlineData("trash", 1)]
    [InlineData("prefab-write", 1)]
    [InlineData("prefab-delete", 1)]
    [InlineData("index", 1)]         // inside the trash move
    [InlineData("index", 2)]         // the final rebuild
    public void AFailedStorageStepRollsTheWholeBatchBack(string step, int nth)
    {
        using var lab = new Lab();
        var s = Scene(lab);
        var batch = Batch(lab);
        string docBefore = s.Document.ToJson();
        var treeBefore = Tree(lab.Library);
        int undo = s.UndoDepth, redo = s.RedoDepth, version = s.Version;
        var trashedBefore = s.LastTrashed.ToList();
        Assert.True(redo >= 1);

        int hits = 0;
        lab.Library.StorageFault = st => { if (st == step && ++hits == nth) throw new IOException("injected " + step); };
        var r = s.Apply(batch);
        lab.Library.StorageFault = null;

        Assert.False(r.Ok);
        Assert.Equal("io-error", r.Errors[0].Code);
        Assert.Contains("injected " + step, r.Errors[0].Message);
        Assert.DoesNotContain("only in part", r.Errors[0].Message);
        Assert.Equal(docBefore, s.Document.ToJson());
        Assert.Equal(treeBefore, Tree(lab.Library));
        Assert.Equal(undo, s.UndoDepth);
        Assert.Equal(redo, s.RedoDepth);
        Assert.Equal(version, s.Version);
        Assert.Equal(trashedBefore, s.LastTrashed.ToList());

        // The same batch now succeeds, is one undo step, and undo/redo restore and reapply it.
        r = s.Apply(batch);
        Assert.True(r.Ok, r.ToJson());
        Assert.Equal(undo + 1, s.UndoDepth);
        Assert.Equal(0, s.RedoDepth);
        Assert.Null(lab.Library.GetAsset("pillar"));
        Assert.NotNull(lab.Library.GetAsset("boxb"));
        Assert.Null(lab.Library.GetPrefab("pb"));
        Assert.Equal("Renamed", lab.Library.GetPrefab("pa")!.Name);
        var treeAfter = Tree(lab.Library);
        Assert.True(s.Undo().Ok);
        Assert.Equal(docBefore, s.Document.ToJson());
        Assert.NotNull(lab.Library.GetAsset("pillar"));
        Assert.NotNull(lab.Library.GetPrefab("pb"));
        Assert.True(s.Redo().Ok);
        Assert.Null(lab.Library.GetAsset("pillar"));
        Assert.Equal(treeAfter.Keys.Where(k => !k.Contains("trash")), Tree(lab.Library).Keys.Where(k => !k.Contains("trash")));
    }

    [Fact]
    public void AnInverseThatFailsIsNamedInTheError()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        int index = 0;
        // The index rebuild fails in the batch and again in the rollback.
        lab.Library.StorageFault = st => { if (st == "index" && ++index >= 1) throw new IOException("disk gone"); };
        var r = s.Apply(Batch(lab));
        lab.Library.StorageFault = null;
        Assert.False(r.Ok);
        Assert.Contains("rolled back only in part", r.Errors[0].Message);
        Assert.Contains("rebuild the library index", r.Errors[0].Message);
    }

    [Fact]
    public void ARolledBackBatchLeavesNoScratchFolder()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        int hits = 0;
        lab.Library.StorageFault = st => { if (st == "prefab-write" && ++hits == 1) throw new IOException("x"); };
        Assert.False(s.Apply(Batch(lab)).Ok);
        lab.Library.StorageFault = null;
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(lab.Library.Root, "library")), d => Path.GetFileName(d).StartsWith(".txn-"));
    }
}
