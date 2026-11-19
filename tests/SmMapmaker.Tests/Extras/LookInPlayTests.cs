using System.Numerics;
using SmContent;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>The map's look in play, engine-free parts (slice S4): the refusal when the install lacks the look's map, and the spinning definitions imported fans are matched by.</summary>
public class LookInPlayTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-look-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    void Archive(string name)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "data", "chunks"));
        File.WriteAllBytes(Path.Combine(_dir, "data", "chunks", name), []);
    }

    [Fact]
    public void AMapWhoseRegionArchiveIsInstalledIsAvailable()
    {
        Archive("mp_tdm_09__wrld.oppc");
        Archive("mp_tdm_17__r000_main.oppc");
        Assert.True(LookCheck.Available(_dir, "mp_tdm_09"));
        Assert.True(LookCheck.Available(_dir, "mp_tdm_17"));
        Assert.Null(LookCheck.Refusal(_dir, "mp_tdm_09"));
    }

    [Fact]
    public void ALackingMapOrInstallOrNameIsRefusedWithAPlainSentence()
    {
        Archive("mp_tdm_09__wrld.oppc");
        Assert.False(LookCheck.Available(_dir, "mp_tdm_21"));
        Assert.False(LookCheck.Available(null, "mp_tdm_09"));
        Assert.False(LookCheck.Available(_dir, "../mp_tdm_09"));
        Assert.False(LookCheck.Available(_dir, null));
        Assert.Equal("This map uses the look of Shattered Bridge, which your game install does not have.", LookCheck.Refusal(_dir, "mp_tdm_21", "Shattered Bridge"));
        Assert.Equal("This map uses the look of mp_tdm_21, which your game install does not have.", LookCheck.Refusal(_dir, "mp_tdm_21"));
        Assert.Equal("This map uses the look of mp_tdm_21, which your game install does not have.", LookCheck.Refusal(_dir, "mp_tdm_21", " "));
    }

    static MapScene Scene(PlacedObject[] objects, Dictionary<int, float> spin) => new()
    {
        Map = "mp_tdm_09", Objects = objects, Definitions = new Dictionary<string, ObjectDefinition>(), Meshes = new Dictionary<string, DcmMesh>(), Materials = new Dictionary<string, BmatMaterial>(),
        Textures = new Dictionary<string, byte[]>(), Notes = [], Spinning = spin,
    };

    [Fact]
    public void SpinningDefinitionsAreKeyedByDefinitionNameAndMatchEveryPlacementOfIt()
    {
        var source = Scene(
        [
            new PlacedObject("a", "ceilingfan_a", "l_props", Matrix4x4.Identity),
            new PlacedObject("b", "crate", "l_props", Matrix4x4.Identity),
            new PlacedObject("c", "ceilingfan_b", "l_props", Matrix4x4.Identity),
        ], new Dictionary<int, float> { [0] = 90f, [2] = -45f, [9] = 5f });   // 9 is out of range and ignored
        var defs = LookCheck.SpinningDefinitions(source);
        Assert.Equal(2, defs.Count);
        Assert.Equal(90f, defs["CEILINGFAN_A"]);
        Assert.Equal(-45f, defs["ceilingfan_b"]);
        // Imported placements (their own list, renamed definitions in the decor view): matched by the definition they were taken from.
        var spun = LookCheck.SpinningFor([new PlacedObject("x", "ceilingfan_a", "custom-map", Matrix4x4.Identity), new PlacedObject("y", "crate", "custom-map", Matrix4x4.Identity), new PlacedObject("z", "ceilingfan_a", "custom-map", Matrix4x4.Identity)], defs);
        Assert.Equal(new Dictionary<int, float> { [0] = 90f, [2] = 90f }, spun);
        Assert.Empty(LookCheck.SpinningFor([new PlacedObject("x", "crate", "custom-map", Matrix4x4.Identity)], defs));
    }

    [Fact]
    public void LookNamesAreSm1MultiplayerMapNames()
    {
        Assert.True(MapLook.IsMapName("mp_tdm_09"));
        Assert.False(MapLook.IsMapName("tfw_war"));
        Assert.False(MapLook.IsMapName("mp_"));
    }
}
