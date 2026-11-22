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
                .Concat(Directory.EnumerateDirectories(lib.Root, "*", SearchOption.AllDirectories).Select(d => new KeyValuePair<string, string>(Path.GetRelativePath(lib.Root, d) + Path.DirectorySeparatorChar, "<dir>")))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
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

    // An asset folder that exists but has no readable asset.json is "absent" to GetAsset, so an import of that id writes over the folder.
    [Fact]
    public void AReplacedAssetFolderComesBackByteIdenticalAfterAFailedBatch()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        var dir = lab.Library.AssetDir("ghost");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "boxa.gltf"), "old content");
        File.WriteAllText(Path.Combine(dir, "keep.bin"), "left over");
        File.WriteAllText(Path.Combine(dir, "sub", "deep.bin"), "deeper");
        Directory.CreateDirectory(Path.Combine(dir, "emptydir"));
        try
        {
            File.SetAttributes(Path.Combine(dir, "keep.bin"), FileAttributes.ReadOnly);
            Assert.Null(lab.Library.GetAsset("ghost"));
            var before = Tree(lab.Library);

            var batch = new List<System.Text.Json.Nodes.JsonObject>
            {
                Lab.Op($$"""{"op":"import-asset","path":{{System.Text.Json.JsonSerializer.Serialize(lab.WriteGltf("boxa.gltf"))}},"id":"ghost"}"""),
                Lab.Op("""{"op":"delete-prefab","prefab":"pb"}"""),
            };
            lab.Library.StorageFault = st => { if (st == "prefab-delete") throw new IOException("injected"); };
            var r = s.Apply(batch);
            lab.Library.StorageFault = null;

            Assert.False(r.Ok);
            Assert.DoesNotContain("only in part", r.Errors[0].Message);
            Assert.Equal(before, Tree(lab.Library));
            Assert.Equal("old content", File.ReadAllText(Path.Combine(dir, "boxa.gltf")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(lab.Library.Root, "library")), d => Path.GetFileName(d).StartsWith(".txn-"));
    
        }
        finally { File.SetAttributes(Path.Combine(dir, "keep.bin"), FileAttributes.Normal); }
    }

    [Fact]
    public void AFailedTrashStepLeavesNoEmptyTrashFolder()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        Assert.False(Directory.Exists(lab.Library.TrashAssetsDir));
        lab.Library.StorageFault = st => { if (st == "trash") throw new IOException("injected"); };
        var r = s.Apply([Lab.Op("""{"op":"delete","ids":["o1"]}"""), Lab.Op("""{"op":"delete-asset","asset":"pillar"}""")]);
        lab.Library.StorageFault = null;
        Assert.False(r.Ok);
        Assert.False(Directory.Exists(lab.Library.TrashAssetsDir));
        Assert.False(Directory.Exists(Path.GetDirectoryName(lab.Library.TrashAssetsDir)));   // library/trash is gone too
    }

    [Fact]
    public void AFailedAtomicWriteLeavesNoTmpFile()
    {
        using var lab = new Lab();
        var target = Path.Combine(lab.Dir, "victim");
        Directory.CreateDirectory(target);   // File.Move cannot replace a directory
        Assert.ThrowsAny<Exception>(() => MapLibrary.WriteAtomic(target, [1, 2, 3]));
        Assert.Empty(Directory.EnumerateFiles(lab.Dir, "*.tmp"));
    }

    static string Q(string path) => System.Text.Json.JsonSerializer.Serialize(path);
    static string PillarText(Lab lab) => File.ReadAllText(Path.Combine(lab.Library.AssetDir("pillar"), "pillar.gltf"));

    static List<System.Text.Json.Nodes.JsonObject> ReplaceBatch(Lab lab) =>
    [
        Lab.Op("""{"op":"delete-asset","asset":"pillar"}"""),
        Lab.Op($$"""{"op":"import-asset","path":{{Q(lab.WriteGltf("pillar.gltf", "[0,2,0]"))}},"id":"pillar"}"""),
    ];

    // Owner's decision 2026-11-23: delete-asset then import-asset of the same id in one batch: the import wins.
    [Fact]
    public void DeleteThenImportOfTheSameIdInOneBatch_TheImportWinsAndUndoRedoSwap()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        Assert.True(s.Apply(Lab.Op("""{"op":"delete","ids":["o1"]}""")).Ok);
        string oldText = PillarText(lab);
        Assert.DoesNotContain("0,2,0", oldText);

        var r = s.Apply(ReplaceBatch(lab));
        Assert.True(r.Ok, r.ToJson());
        Assert.NotNull(lab.Library.GetAsset("pillar"));
        string newText = PillarText(lab);
        Assert.Contains("0,2,0", newText);
        Assert.Single(Directory.GetDirectories(lab.Library.TrashAssetsDir));
        Assert.Equal(oldText, File.ReadAllText(Path.Combine(Directory.GetDirectories(lab.Library.TrashAssetsDir)[0], "pillar.gltf")));

        Assert.True(s.Undo().Ok);
        Assert.Equal(oldText, PillarText(lab));
        Assert.NotNull(lab.Library.GetAsset("pillar"));
        Assert.True(s.Redo().Ok);
        Assert.Equal(newText, PillarText(lab));
        Assert.True(s.Undo().Ok);
        Assert.Equal(oldText, PillarText(lab));
        Assert.Contains("pillar", lab.Library.ListAssets().Select(a => a.Id));
    }

    [Theory]
    [InlineData("trash", 1)]        // before the old copy moved
    [InlineData("asset-write", 1)]  // the old copy is in the trash, the new main file is not written
    [InlineData("asset-write", 2)]  // the new asset.json
    [InlineData("index", 1)]        // inside the trash move
    [InlineData("index", 2)]        // the final rebuild, after trash and write
    public void DeleteThenImportOfTheSameId_AFailureAtAnyStepBringsTheOldAssetBack(string step, int nth)
    {
        using var lab = new Lab();
        var s = Scene(lab);
        Assert.True(s.Apply(Lab.Op("""{"op":"delete","ids":["o1"]}""")).Ok);
        string oldText = PillarText(lab);
        var before = Tree(lab.Library);
        int undo = s.UndoDepth, redo = s.RedoDepth;
        int hits = 0;
        lab.Library.StorageFault = st => { if (st == step && ++hits == nth) throw new IOException("injected " + step); };
        var r = s.Apply(ReplaceBatch(lab));
        lab.Library.StorageFault = null;
        Assert.False(r.Ok);
        Assert.DoesNotContain("only in part", r.Errors[0].Message);
        Assert.Equal(oldText, PillarText(lab));
        Assert.Equal(before, Tree(lab.Library));
        Assert.Equal(undo, s.UndoDepth);
        Assert.Equal(redo, s.RedoDepth);
        Assert.False(Directory.Exists(lab.Library.TrashAssetsDir));
    }

    [Fact]
    public void ImportThenDeleteOfTheSameIdInOneBatch_TheLaterDeleteWins()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        var before = Tree(lab.Library);
        // A new id: the staged copy is dropped, nothing is written or trashed.
        var r = s.Apply([
            Lab.Op($$"""{"op":"import-asset","path":{{Q(lab.WriteGltf("fresh.gltf"))}},"id":"fresh"}"""),
            Lab.Op("""{"op":"delete-asset","asset":"fresh"}"""),
        ]);
        Assert.True(r.Ok, r.ToJson());
        Assert.Null(lab.Library.GetAsset("fresh"));
        Assert.False(Directory.Exists(lab.Library.AssetDir("fresh")));
        Assert.Equal(before, Tree(lab.Library));

        // An id already in the library with the same content: the import is a no-op, the delete trashes the existing copy.
        Assert.True(s.Apply(Lab.Op("""{"op":"delete","ids":["o1"]}""")).Ok);
        r = s.Apply([
            Lab.Op($$"""{"op":"import-asset","path":{{Q(lab.WriteGltf("pillar.gltf"))}},"id":"pillar"}"""),
            Lab.Op("""{"op":"delete-asset","asset":"pillar"}"""),
        ]);
        Assert.True(r.Ok, r.ToJson());
        Assert.Null(lab.Library.GetAsset("pillar"));
        Assert.Single(Directory.GetDirectories(lab.Library.TrashAssetsDir));
        Assert.True(s.Undo().Ok);
        Assert.NotNull(lab.Library.GetAsset("pillar"));
    }

    [Fact]
    public void DeleteImportDeleteOfTheSameId_TheOldCopyIsTrashedAndTheStagedOneDropped()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        Assert.True(s.Apply(Lab.Op("""{"op":"delete","ids":["o1"]}""")).Ok);
        var r = s.Apply([.. ReplaceBatch(lab), Lab.Op("""{"op":"delete-asset","asset":"pillar"}""")]);
        Assert.True(r.Ok, r.ToJson());
        Assert.Null(lab.Library.GetAsset("pillar"));
        Assert.Single(Directory.GetDirectories(lab.Library.TrashAssetsDir));
        Assert.True(s.Undo().Ok);
        Assert.DoesNotContain("0,2,0", PillarText(lab));
    }

    [Fact]
    public void AFailedAtomicWriteLeavesAnotherWritersTempFileAlone()
    {
        using var lab = new Lab();
        var target = Path.Combine(lab.Dir, "victim");
        Directory.CreateDirectory(target);
        var other = Path.Combine(lab.Dir, ".victim.tmp");   // the old fixed temp name: another writer's file
        File.WriteAllText(other, "another writer");
        Assert.ThrowsAny<Exception>(() => MapLibrary.WriteAtomic(target, [1]));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void ARestoreClearsAndRemovesAReadOnlyFileThatOnlyTheTargetHas()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        // A folder without asset.json is "absent" to GetAsset, so importing that id replaces it; a failure restores it exactly.
        var dir = lab.Library.AssetDir("ghost");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.bin"), "left over");
        string stray = Path.Combine(dir, "boxa.gltf");   // written by the import (not in the backup), made read-only by the fault
        try
        {
            lab.Library.StorageFault = st =>
            {
                if (st == "asset-write" && File.Exists(stray)) File.SetAttributes(stray, FileAttributes.ReadOnly);
                if (st == "index") throw new IOException("injected");
            };
            Assert.False(s.Apply(Lab.Op($$"""{"op":"import-asset","path":{{Q(lab.WriteGltf("boxa.gltf"))}},"id":"ghost"}""")).Ok);
            lab.Library.StorageFault = null;
            Assert.Equal(["keep.bin"], Directory.EnumerateFiles(dir).Select(Path.GetFileName).ToArray());
        }
        finally { if (File.Exists(stray)) File.SetAttributes(stray, FileAttributes.Normal); }
    }
}
