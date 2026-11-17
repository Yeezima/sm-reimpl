using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>Renaming custom assets and prefabs in the library: only the display name changes, the id stays, so maps that refer to them keep resolving.</summary>
public class RenameTests
{
    static (Lab Lab, MapSession Session) Scene()
    {
        var lab = new Lab();
        var s = lab.Session(false);
        lab.ImportBox(s, "pillar");
        Assert.True(s.Apply([
            Lab.Op("""{"op":"place","asset":{"source":"custom","id":"pillar"},"name":"left","transform":{"position":[0,0,0]}}"""),
            Lab.Op("""{"op":"place","asset":{"source":"custom","id":"pillar"},"name":"right","transform":{"position":[4,0,0]}}"""),
        ]).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"pair","name":"Pair","origin":"first","replace":true}""")).Ok);
        return (lab, s);
    }

    [Fact]
    public void AssetRenamePersistsAndKeepsTheIdAndBounds()
    {
        var (lab, _) = Scene();
        using var _l = lab;
        var before = lab.Library.GetAsset("pillar")!;
        Assert.Equal("Rock Pillar", lab.Library.RenameAsset("pillar", "  Rock   Pillar \t"));
        var after = new MapLibrary(lab.Library.Root).GetAsset("pillar")!;   // read fresh from disk
        Assert.Equal("Rock Pillar", after.Name);
        Assert.Equal("pillar", after.Id);
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Contains(lab.Library.ListAssets(), a => a.Id == "pillar" && a.Name == "Rock Pillar");
        Assert.Contains("Rock Pillar", File.ReadAllText(lab.Library.IndexPath));
    }

    [Fact]
    public void PrefabRenamePersistsAndKeepsTheId()
    {
        var (lab, _) = Scene();
        using var _l = lab;
        Assert.Equal("Twin pillars", lab.Library.RenamePrefab("pair", "Twin pillars"));
        var p = new MapLibrary(lab.Library.Root).GetPrefab("pair")!;
        Assert.Equal("Twin pillars", p.Name);
        Assert.Equal("pair", p.Id);
        Assert.Equal(2, p.Parts.Count);
        Assert.True(File.Exists(lab.Library.PrefabPath("pair")));
        Assert.Contains("Twin pillars", File.ReadAllText(lab.Library.IndexPath));
    }

    [Fact]
    public void MapsThatAlreadyReferToTheOldNameStillResolve()
    {
        var (lab, s) = Scene();
        using var _l = lab;
        string docBefore = s.Document.ToJson();
        lab.Library.RenamePrefab("pair", "Renamed pair");
        lab.Library.RenameAsset("pillar", "Renamed pillar");
        Assert.Equal(docBefore, s.Document.ToJson());   // the document refers by id and never changed
        Assert.True(s.Validate().Ok, s.Validate().ToJson());
        var parts = MapResolver.Flatten(s.Document, new MapContext(lab.Library, null));
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal("pillar", p.Asset.Id));
        // a fresh session on the saved map text resolves it too
        var reopened = MapSession.New("again", new MapLibrary(lab.Library.Root), null);
        Assert.True(reopened.Apply(Lab.Op("""{"op":"place","prefab":"pair"}""")).Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("bad\u0001name")]
    public void EmptyAndControlNamesAreRejected(string name)
    {
        var (lab, _) = Scene();
        using var _l = lab;
        Assert.Equal("bad-name", Assert.Throws<MapException>(() => lab.Library.RenameAsset("pillar", name)).Code);
        Assert.Equal("bad-name", Assert.Throws<MapException>(() => lab.Library.RenamePrefab("pair", name)).Code);
        Assert.Equal("pillar", lab.Library.GetAsset("pillar")!.Name);   // unchanged
    }

    [Fact]
    public void TooLongNameIsRejected()
    {
        var (lab, _) = Scene();
        using var _l = lab;
        Assert.Equal("bad-name", Assert.Throws<MapException>(() => lab.Library.RenameAsset("pillar", new string('a', MapLibrary.MaxNameLength + 1))).Code);
        Assert.Equal(new string('a', MapLibrary.MaxNameLength), lab.Library.RenameAsset("pillar", new string('a', MapLibrary.MaxNameLength)));
    }

    [Fact]
    public void DuplicateNamesAreRejectedWithoutCaseButTheSameNameOnItselfIsFine()
    {
        var (lab, s) = Scene();
        using var _l = lab;
        lab.ImportBox(s, "beam");
        lab.Library.RenameAsset("pillar", "Column");
        var ex = Assert.Throws<MapException>(() => lab.Library.RenameAsset("beam", "column"));
        Assert.Equal("duplicate-name", ex.Code);
        Assert.Contains("Column".ToLowerInvariant(), ex.Message.ToLowerInvariant());
        Assert.Equal("Column", lab.Library.RenameAsset("pillar", "Column"));      // unchanged: no error
        Assert.Equal("COLUMN", lab.Library.RenameAsset("pillar", "COLUMN"));      // a case change of its own name is fine
        // an asset and a prefab may share a name (separate lists)
        Assert.Equal("COLUMN", lab.Library.RenamePrefab("pair", "COLUMN"));
    }

    [Fact]
    public void UnknownIdsAreErrors()
    {
        using var lab = new Lab();
        Assert.Equal("unknown-asset", Assert.Throws<MapException>(() => lab.Library.RenameAsset("nope", "X")).Code);
        Assert.Equal("unknown-prefab", Assert.Throws<MapException>(() => lab.Library.RenamePrefab("nope", "X")).Code);
        Assert.NotNull(Assert.Throws<MapException>(() => lab.Library.RenamePrefab("../x", "X")));
    }
}
