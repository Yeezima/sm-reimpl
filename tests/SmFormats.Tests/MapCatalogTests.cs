using System.IO.Compression;
using System.Text;
using SmContent;
using SmFormats.Baf;
using SmFormats.Obpk;
using Xunit;

namespace SmFormats.Tests;

/// <summary>classic-maps-all: <see cref="MapCatalog"/> reads the front-end map list (synthetic archive, synthetic locale; no original bytes).</summary>
public sealed class MapCatalogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-mapcatalog-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static (string, object)[] Objective(string type) => [("objective_game_type", type)];

    static byte[] Map(string name, int display, params string[] types) => SyntheticBafWriter.Build(
        ("map_name", name), ("display_name", display),
        ("map_objectives", types.Select(t => ("o", (object)Objective(t))).ToArray()));

    void Write(params (string Entry, byte[] Data)[] maps)
    {
        string packages = Path.Combine(_dir, "data", "packages"), chunks = Path.Combine(_dir, "data", "chunks"), locale = Path.Combine(_dir, "data", "locale", "english");
        Directory.CreateDirectory(packages); Directory.CreateDirectory(chunks); Directory.CreateDirectory(locale);
        File.WriteAllBytes(Path.Combine(packages, "multiplayer_map.oppc"),
            SyntheticObpkWriter.Build(maps.Select(m => new SyntheticObpkWriter.File("attr_pc", m.Entry, m.Data)).ToList()));
        var raw = Encoding.Unicode.GetBytes("﻿2001\tShattered Bridge\r\n2002\tManufactorum\r\n");
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var file = new byte[4 + packed.Length];
        BitConverter.GetBytes(raw.Length).CopyTo(file, 0);
        packed.ToArray().CopyTo(file, 4);
        File.WriteAllBytes(Path.Combine(locale, "ucs.pc"), file);
        // a region archive makes a map playable for MapContent.Available (only the name is looked at there)
        File.WriteAllBytes(Path.Combine(chunks, "mp_tdm_17__r000_main.oppc"), []);
        File.WriteAllBytes(Path.Combine(chunks, "mp_tdm_09__wrld.oppc"), []);
    }

    [Fact]
    public void Competitive_maps_are_listed_with_their_install_names_modes_and_whether_they_are_playable()
    {
        Write(("sm_bridge", Map("mp_tdm_17", 2001, "teamdeathmatch", "domination", "ctf")),
              ("sm_factory", Map("mp_tdm_09", 2002, "teamdeathmatch")),
              ("dlc3_map2", Map("mp_dlc3_map2", 9999, "teamdeathmatch", "domination")),   // no region archive, no locale text
              ("cp_arena", Map("cp_map_60day", 2001, "coop")),                              // co-op: not competitive
              ("coop_only", Map("mp_dlc3_map1", 2001, "coop", "koth")));                   // no team game type
        var maps = MapCatalog.Read(_dir);
        Assert.Equal(["mp_dlc3_map2", "mp_tdm_09", "mp_tdm_17"], maps.Select(m => m.Name));
        var bridge = maps.Single(m => m.Name == "mp_tdm_17");
        Assert.Equal("Shattered Bridge", bridge.DisplayName);
        Assert.True(bridge.Playable && bridge.SupportsAnnihilation && bridge.SupportsSeize);
        Assert.Contains("ctf", bridge.GameTypes);
        var factory = maps.Single(m => m.Name == "mp_tdm_09");
        Assert.True(factory.SupportsAnnihilation && !factory.SupportsSeize);
        var stub = maps.Single(m => m.Name == "mp_dlc3_map2");
        Assert.False(stub.Playable);
        Assert.Equal("mp_dlc3_map2", stub.DisplayName);   // no text: the archive name
    }

    [Fact]
    public void A_missing_package_gives_an_empty_list()
    {
        Directory.CreateDirectory(_dir);
        Assert.Empty(MapCatalog.Read(_dir));
    }
}
