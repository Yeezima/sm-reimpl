using System.Numerics;
using SmContent;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>The SM1 definitions index (owner 2026-11-19): names only, cached per map, renewed when the map's archive changes. Synthetic scenes and a temporary cache folder.</summary>
public sealed class Sm1IndexTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "smindex-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static VisualPart Part() => new("mesh_a", 0, "node_a", Matrix4x4.Identity, 1, true);

    static MapScene Scene() => new()
    {
        Map = "mp_fake",
        Objects = [],
        Definitions = new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["pipe_b"] = new("pipe_b", [Part()], []),
            ["Crate_a"] = new("Crate_a", [Part()], []),
            ["marker"] = new("marker", [], []),
        },
        Meshes = new Dictionary<string, SmFormats.Dcm.DcmMesh>(),
        Materials = new Dictionary<string, SmFormats.Bod.BmatMaterial>(),
        Textures = new Dictionary<string, byte[]>(),
        Notes = [],
    };

    [Fact]
    public void A_scene_lists_the_definitions_that_draw_sorted_and_counts_the_rest()
    {
        var e = Sm1Index.FromScene(Scene(), "Fake Map");
        Assert.Equal(["Crate_a", "pipe_b"], e.Definitions);
        Assert.Equal(1, e.Empty);
        Assert.Equal("Fake Map", e.DisplayName);
    }

    [Fact]
    public void The_cache_round_trips_and_is_ignored_when_the_stamp_or_version_differs()
    {
        var e = Sm1Index.FromScene(Scene(), "Fake Map");
        Sm1Index.Write(_dir, e, "stamp-1");
        var back = Sm1Index.TryRead(_dir, "mp_fake", "stamp-1");
        Assert.NotNull(back);
        Assert.Equal(e.Definitions, back!.Definitions);
        Assert.Equal(1, back.Empty);
        Assert.Null(Sm1Index.TryRead(_dir, "mp_fake", "stamp-2"));   // the archive changed
        Assert.Null(Sm1Index.TryRead(_dir, "mp_other", "stamp-1"));   // no such file
        File.WriteAllText(Path.Combine(_dir, "mp_fake.json"), """{"version":0,"stamp":"stamp-1","map":"mp_fake","definitions":[]}""");
        Assert.Null(Sm1Index.TryRead(_dir, "mp_fake", "stamp-1"));   // an older layout
        File.WriteAllText(Path.Combine(_dir, "mp_fake.json"), "{ not json");
        Assert.Null(Sm1Index.TryRead(_dir, "mp_fake", "stamp-1"));   // corrupt counts as missing
    }

    [Fact]
    public void The_cache_holds_names_only()
    {
        Sm1Index.Write(_dir, Sm1Index.FromScene(Scene(), "Fake Map"), "s");
        var text = File.ReadAllText(Path.Combine(_dir, "mp_fake.json"));
        Assert.Contains("Crate_a", text);
        Assert.True(text.Length < 500, text);
    }

    [Fact]
    public void The_stamp_follows_the_archives_of_the_map_and_is_empty_without_an_install()
    {
        Assert.Equal("", Sm1Index.Stamp(Path.Combine(_dir, "nothing"), "mp_fake"));
        var chunks = Path.Combine(_dir, "data", "chunks");
        Directory.CreateDirectory(chunks);
        File.WriteAllBytes(Path.Combine(chunks, "mp_fake__wrld.oppc"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(chunks, "mp_other__wrld.oppc"), [1]);
        var a = Sm1Index.Stamp(_dir, "mp_fake");
        Assert.Contains("mp_fake__wrld.oppc:3:", a);
        Assert.DoesNotContain("mp_other", a);
        File.WriteAllBytes(Path.Combine(chunks, "mp_fake__wrld.oppc"), [1, 2, 3, 4]);
        Assert.NotEqual(a, Sm1Index.Stamp(_dir, "mp_fake"));
    }

    [Fact]
    public void The_map_list_is_empty_without_the_catalog()
    {
        Assert.Empty(Sm1Index.Maps(_dir));
    }
}
