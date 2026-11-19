using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;
using SmSim;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>
/// import-sm1-map and refresh-from-classic, follow-up checks (slice S3b): the compiled pieces equal Classic's own data, the wall mesh equals WorldBlocker's, the 8 MB limit counts the spawns,
/// keys do not depend on the caps, and the editor-only link data (src, src.ob, removedSrc) never reaches the hash. Synthetic source only, no original bytes.
/// </summary>
public sealed class Sm1ClassicFidelityTests : IDisposable
{
    readonly Lab _lab = new();
    public void Dispose() => _lab.Dispose();

    /// <summary>Per-map Classic data the tests edit between refreshes.</summary>
    sealed class MapData
    {
        public List<(string Name, Vector3 At)> Shown = [("s0", new(0, 0, 1)), ("s1", new(3, 0, 1))];
        public List<(string Name, Vector3 At)> Hidden = [("h0", new(0, 3, 1))];
        public List<MapWall> Walls = [Wall("w1", 10), Wall("w2", 20)];
        public List<MapKillRegion> Kills = [new("kill_pit", new Vector3(0, 0, -10), 0f, new Vector3(20, 10, 4), 0)];
        public List<MapProp> Props = [Prop("crate", new Vector3(5, 5, 0), new Vector3(1.2f, 3f, 1.5f)), Prop("barrel", new Vector3(9, 5, 0), new Vector3(1f, 1f, 1f))];
        public int NameLength;
    }

    static MapWall Wall(string name, float x) => new(name, new Vector3(x, 0, 5), Vector3.Zero, new Vector3(10, 2, 10), 1, 2, 1);
    static MapProp Prop(string name, Vector3 low, Vector3 size) => new(name, "ammo_crate", "e_cover", 4, [low, low + size], [0, 1, 1]);

    sealed class Source(params string[] maps) : ISm1Source
    {
        public Dictionary<string, MapData> Maps { get; } = maps.ToDictionary(m => m, _ => new MapData());
        public MapData this[string map] => Maps[map];

        public Sm1Map LoadMap(string map)
        {
            if (!Maps.TryGetValue(map, out var d)) throw new MapException("unknown-map", $"No map '{map}'.");
            var objects = new List<Sm1Object>();
            var hide = new HashSet<int>();
            string N(string n) => d.NameLength > 0 ? n.PadRight(d.NameLength, 'x')[..d.NameLength] : n;
            foreach (var (n, at) in d.Shown) objects.Add(new(N(n), "def_wall", "l", MapScene.Transform(at, Vector3.Zero, Vector3.One), 0));
            foreach (var (n, at) in d.Hidden) { hide.Add(objects.Count); objects.Add(new(N(n), "def_wall", "l", MapScene.Transform(at, Vector3.Zero, Vector3.One), 0)); }
            var defs = new Dictionary<string, Sm1Definition>(StringComparer.OrdinalIgnoreCase) { ["def_wall"] = new("def_wall", 1, new Bounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 2))) };
            return new Sm1Map(map, objects, defs, []) { Hidden = hide };
        }

        public Sm1Gameplay? LoadGameplay(string map) => new Sm1Gameplay(Maps[map].Walls, Maps[map].Kills, Maps[map].Props);
    }

    MapSession Import(Source src, params string[] maps)
    {
        var s = MapSession.New("m", _lab.Library, src);
        foreach (var m in maps)
        {
            var r = s.Apply(Lab.Op($$"""{"op":"import-sm1-map","map":"{{m}}"}"""));
            Assert.True(r.Ok, r.ToJson());
        }
        return s;
    }

    static JsonObject Refresh(MapSession s, bool preview, string extra = "")
    {
        var r = s.Apply(Lab.Op($$"""{"op":"refresh-from-classic","preview":{{(preview ? "true" : "false")}}{{extra}}}"""));
        Assert.True(r.Ok, r.ToJson());
        return r.Refresh!;
    }

    static MapObject Find(MapSession s, string map, string key) => s.Document.Objects.Single(o => o.Src?.Map == map && o.Src.Key == key);
    static MapObject Group(MapSession s, string name) => s.Document.Objects.Single(o => o.Name == name && o.Asset is null && o.Piece is null);
    static void Near(Vector3 a, Vector3 b, float tol, string what) => Assert.True(Vector3.Distance(a, b) <= tol, $"{what}: {a} vs {b}");

    // ---- 1. compiled pieces equal the source data ----

    [Fact]
    public void Compiled_walls_kill_boxes_and_cover_equal_the_source_data()
    {
        const float Yaw = 33.333f;
        var src = new Source("mp_fake");
        var d = src["mp_fake"];
        d.Walls =
        [
            new("w_yawed", new Vector3(12, -7, 9), new Vector3(0, 0, Yaw), new Vector3(8, 3, 6), 1, 2, 1),
            new("w_round", new Vector3(-5, 11, 3.5f), Vector3.Zero, new Vector3(6, 6, 7), 3, 2, 1),
            new("w_high", new Vector3(2, 2, 20), new Vector3(0, 0, 90), new Vector3(5, 4, 5), 1, 2, 1),
        ];
        d.Kills = [new("k_yawed", new Vector3(3, 4, -15), Yaw * MathF.PI / 180f, new Vector3(12, 8, 6), 0)];
        d.Props = [Prop("p1", new Vector3(5, 5, 0.5f), new Vector3(1.2f, 3f, 1.5f)), Prop("p2", new Vector3(-9, 2.25f, 4), new Vector3(2f, 0.5f, 1f))];
        var s = Import(src, "mp_fake");
        var map = CustomMap.Compile(s.Document);

        var walls = map.Content.Walls;
        Assert.Equal(d.Walls.Count, walls.Count);
        for (int i = 0; i < walls.Count; i++)
        {
            var want = d.Walls[i].ToSim();
            var got = walls[i].ToSim();
            Assert.Equal(want.Vertices.Length, got.Vertices.Length);
            Near(got.Min, want.Min, 1e-3f, $"wall {i} min");
            Near(got.Max, want.Max, 1e-3f, $"wall {i} max");
            for (int v = 0; v < want.Vertices.Length; v++) Near(got.Vertices[v], want.Vertices[v], 1e-3f, $"wall {i} vertex {v}");
            Assert.Equal(want.Indices, got.Indices);
        }

        var kills = map.Content.KillRegions.Where(k => k.Name.StartsWith("cm_kill_") && k.Name != "cm_kill_floor").ToList();
        Assert.Single(kills);
        var wk = d.Kills[0].ToSim(); var gk = kills[0].ToSim();
        Near(gk.Center, wk.Center, 1e-3f, "kill centre");
        Near(gk.HalfExtents, wk.HalfExtents, 1e-3f, "kill half extents");
        Assert.Equal(wk.Yaw, gk.Yaw, 1e-3f);

        Assert.Equal(d.Props.Count, map.Content.Props.Count);
        for (int i = 0; i < d.Props.Count; i++)
        {
            var (wlo, whi) = Sm1ClassicKeys.CoverBox(d.Props[i]);
            var gv = map.Content.Props[i].Vertices;
            Near(gv.Aggregate(Vector3.Min), wlo, 1e-3f, $"cover {i} low");
            Near(gv.Aggregate(Vector3.Max), whi, 1e-3f, $"cover {i} high");
        }
    }

    // ---- 2. the deterministic wall mesh equals WorldBlocker's ----

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(33.333, false)]
    [InlineData(0.0, true)]
    [InlineData(33.333, true)]
    public void The_deterministic_wall_mesh_matches_WorldBlocker(double yaw, bool cylinder)
    {
        var s = _lab.Session();
        var shape = cylinder ? ",\"shape\":\"cylinder\"" : "";
        var r = s.Apply(Lab.Op($$$"""{"op":"place","piece":{"kind":"move-wall"{{{shape}}}},"transform":{"position":[7,-3,2],"rotation":[0,0,{{{yaw.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}],"scale":[6,4,3]}}"""));
        Assert.True(r.Ok, r.ToJson());
        var w = CustomMap.Compile(s.Document).Content.Walls.Single();
        var world = MapScene.Transform(w.Position, new Vector3(0, 0, (float)yaw), w.Scale);
        var want = cylinder ? WorldBlocker.Cylinder("x", world) : WorldBlocker.Box("x", world);
        Assert.Equal(want.Indices, w.Indices);   // the same triangles, the same winding
        Assert.Equal(want.Vertices.Length, w.Vertices!.Length);
        for (int i = 0; i < want.Vertices.Length; i++) Near(w.Vertices[i], want.Vertices[i], 1e-4f, $"vertex {i}");
    }

    // ---- 3. the size limit counts the spawns ----

    [Theory]
    [InlineData(6800)]
    [InlineData(6900)]
    [InlineData(7000)]
    [InlineData(7100)]
    [InlineData(7200)]
    public void The_size_limit_counts_the_spawn_pieces_too(int perKind)
    {
        var src = new Source("mp_fake");
        var d = src["mp_fake"];
        d.NameLength = 200;
        d.Shown = [.. Enumerable.Range(0, perKind).Select(i => ($"s{i}", new Vector3(i, 0, 1)))];
        d.Hidden = [.. Enumerable.Range(0, perKind).Select(i => ($"h{i}", new Vector3(0, i, 1)))];
        var s = Import(src, "mp_fake");
        var json = s.Document.ToJson();
        Assert.True(json.Length <= MapDocument.MaxChars, $"{json.Length} chars");
        Assert.Equal(2, s.Document.Objects.Count(o => o.Piece?.Kind == PieceKind.SpawnArea));   // the spawns are there (placeholders: the fake has no spawn data)
        Assert.Equal(3, s.Document.Objects.Count(o => o.Piece?.Kind == PieceKind.CapturePoint));
        Assert.Equal(s.Document.Objects.Count, MapDocument.Parse(json).Objects.Count);   // and the file loads
    }

    // ---- 4. keys ----

    [Fact]
    public void Keys_do_not_change_when_the_caps_drop_items()
    {
        var small = new Source("mp_fake");
        small["mp_fake"].Shown = [.. Enumerable.Range(0, 5).Select(i => ($"s{i}", new Vector3(i, 0, 1)))];
        small["mp_fake"].Hidden = [];
        var big = new Source("mp_fake");
        big["mp_fake"].Shown = [.. Enumerable.Range(0, Sm1Import.MaxObjects - 10).Select(i => ($"s{i}", new Vector3(i, 0, 1)))];   // over the cap: the tail is dropped
        big["mp_fake"].Hidden = [];
        var a = Import(small, "mp_fake");
        var b = Import(big, "mp_fake");
        Assert.True(b.Document.Objects.Count(o => o.Src?.Kind == MapObjectSrc.Placement) < Sm1Import.MaxObjects - 10, "the cap must have dropped something");
        (string, string, string) Id(MapObject o) => (o.Src!.Kind, o.Src.Key, o.Src.Fp);
        var kept = b.Document.Objects.Where(o => o.Src is not null).Select(Id).ToHashSet();
        foreach (var o in a.Document.Objects.Where(o => o.Src is not null)) Assert.Contains(Id(o), kept);   // every key and fingerprint of the small import is the same in the capped one
    }

    [Fact]
    public void Walls_with_equal_names_get_keys_from_their_position_not_their_list_order()
    {
        static MapWall W(string n, float x) => new(n, new Vector3(x, 0, 5), Vector3.Zero, new Vector3(10, 2, 10), 1, 2, 1);
        string[] KeysOf(params MapWall[] walls)
        {
            var src = new Source("mp_fake");
            src["mp_fake"].Walls = [.. walls];
            return [.. Import(src, "mp_fake").Document.Objects.Where(o => o.Src?.Kind == MapObjectSrc.Wall).Select(o => o.Src!.Key).OrderBy(k => k, StringComparer.Ordinal)];
        }
        var ab = KeysOf(W("d_blockers", 10), W("d_blockers", 20));
        var ba = KeysOf(W("d_blockers", 20), W("d_blockers", 10));
        Assert.Equal(ab, ba);
        Assert.Equal(["wall@10,0,5", "wall@20,0,5"], ab);
        // a full tie (same name, same place) is told apart by list order only, the same on every import
        var tie1 = KeysOf(W("d_blockers", 10), W("d_blockers", 10));
        var tie2 = KeysOf(W("d_blockers", 10), W("d_blockers", 10));
        Assert.Equal(tie1, tie2);
        Assert.Equal(["wall@10,0,5", "wall@10,0,5#2"], tie1);
    }

    // ---- S5 review: the editor-only link data ----

    [Fact]
    public void The_game_json_and_the_hash_do_not_depend_on_removedSrc_or_the_link()
    {
        var s = Import(new Source("mp_fake"), "mp_fake");
        Assert.True(s.Apply(Lab.Op($$"""{"op":"delete","ids":["{{Find(s, "mp_fake", "w1").Id}}"]}""")).Ok);   // leaves a removedSrc entry
        Assert.NotEmpty(s.Document.RemovedSrc);
        var plain = MapDocument.Parse(s.Document.ToJson());
        var stripped = MapDocument.Parse(s.Document.ToJson());
        stripped.RemovedSrc.Clear();
        for (int i = 0; i < stripped.Objects.Count; i++) if (stripped.Objects[i].Src is { } l) stripped.Objects[i] = stripped.Objects[i] with { Src = l with { Ob = "" } };
        var gone = MapDocument.Parse(s.Document.ToJson());
        gone.RemovedSrc.Clear();
        for (int i = 0; i < gone.Objects.Count; i++) gone.Objects[i] = gone.Objects[i] with { Src = null };
        var extra = MapDocument.Parse(s.Document.ToJson());
        extra.RemovedSrc.Add(new SrcKey("mp_other", MapObjectSrc.Wall, "zz"));
        Assert.NotEqual(plain.ToJson(), gone.ToJson());   // the saved file does differ
        var g = plain.ToGameJson();
        Assert.Equal(g, stripped.ToGameJson());
        Assert.Equal(g, gone.ToGameJson());
        Assert.Equal(g, extra.ToGameJson());
        var h = CustomMap.Compile(plain).Hash;
        Assert.Equal(h, CustomMap.Compile(stripped).Hash);
        Assert.Equal(h, CustomMap.Compile(gone).Hash);
        Assert.Equal(h, CustomMap.Compile(extra).Hash);
    }

    [Fact]
    public void Deleting_a_duplicate_adds_no_tombstone()
    {
        var s = Import(new Source("mp_fake"), "mp_fake");
        var w1 = Find(s, "mp_fake", "w1");
        var before = s.Document.Objects.Select(o => o.Id).ToHashSet();
        Assert.True(s.Apply(Lab.Op($$"""{"op":"duplicate","ids":["{{w1.Id}}"],"offset":[0,5,0]}""")).Ok);
        var copy = s.Document.Objects.Single(o => !before.Contains(o.Id));
        Assert.Null(copy.Src);   // a copy is the owner's own object
        Assert.True(s.Apply(Lab.Op($$"""{"op":"delete","ids":["{{copy.Id}}"]}""")).Ok);
        Assert.Empty(s.Document.RemovedSrc);
        Assert.Empty((JsonArray)Refresh(s, true)["deleted"]!);
    }

    [Fact]
    public void Deleting_a_group_remembers_every_linked_child()
    {
        var src = new Source("mp_fake");
        var s = Import(src, "mp_fake");
        Assert.True(s.Apply(Lab.Op($$"""{"op":"delete","ids":["{{Group(s, "Walls").Id}}"]}""")).Ok);
        Assert.Equal([new SrcKey("mp_fake", MapObjectSrc.Wall, "w1"), new SrcKey("mp_fake", MapObjectSrc.Wall, "w2")], s.Document.RemovedSrc.OrderBy(k => k.Key, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(s.Document.Objects, o => o.Src?.Kind == MapObjectSrc.Wall);
        // the whole import: every linked object (placements, kill box, cover, the rest) is remembered; the groups themselves are not
        var root = s.Document.Objects.Single(o => o.Parent is null && o.Name == "mp_fake");
        int linked = s.Document.Subtree(root.Id).Count(o => o.Src is not null);
        Assert.True(s.Apply(Lab.Op($$"""{"op":"delete","ids":["{{root.Id}}"]}""")).Ok);
        Assert.Equal(linked + 2, s.Document.RemovedSrc.Count);
        Assert.Empty((JsonArray)Refresh(s, true)["added"]!);
    }

    [Fact]
    public void A_hidden_state_flip_moves_the_object_between_the_import_group_and_Hidden_by_Classic()
    {
        var src = new Source("mp_fake");
        var s = Import(src, "mp_fake");
        var d = src["mp_fake"];
        var root = s.Document.Objects.Single(o => o.Parent is null).Id;
        // shown -> hidden
        var s1 = d.Shown.Single(x => x.Name == "s1");
        d.Shown.Remove(s1); d.Hidden.Add(s1);
        Refresh(s, false, ""","applyAllUnedited":true""");
        var o1 = Find(s, "mp_fake", "s1");
        Assert.False(o1.Visible);
        Assert.True(o1.Src!.ClassicHidden);
        Assert.Equal("Hidden by Classic", s.Document.Objects.Single(x => x.Id == o1.Parent).Name);
        // hidden -> shown again
        d.Hidden.Remove(s1); d.Shown.Add(s1);
        Refresh(s, false, ""","applyAllUnedited":true""");
        var o2 = Find(s, "mp_fake", "s1");
        Assert.True(o2.Visible);
        Assert.False(o2.Src!.ClassicHidden);
        Assert.Equal(root, o2.Parent);
    }

    [Fact]
    public void Two_imported_maps_with_the_same_keys_stay_separate()
    {
        var src = new Source("mp_fake", "mp_fake2");
        var s = Import(src, "mp_fake", "mp_fake2");
        Assert.Equal(2, s.Document.Objects.Count(o => o.Src is { Kind: MapObjectSrc.Wall, Key: "w1" }));
        var other = Find(s, "mp_fake2", "w1");
        // a change in one map only touches that map's object
        src["mp_fake"].Walls[0] = Wall("w1", 11);
        Refresh(s, false, ""","applyAllUnedited":true""");
        Assert.Equal(11f, Find(s, "mp_fake", "w1").Transform.Position.X);
        Assert.Equal(10f, Find(s, "mp_fake2", "w1").Transform.Position.X);
        Assert.Equal(other.Id, Find(s, "mp_fake2", "w1").Id);
        // a deletion in one map is remembered for that map only
        Assert.True(s.Apply(Lab.Op($$"""{"op":"delete","ids":["{{Find(s, "mp_fake2", "w1").Id}}"]}""")).Ok);
        Assert.Equal(new SrcKey("mp_fake2", MapObjectSrc.Wall, "w1"), Assert.Single(s.Document.RemovedSrc));
        var r = Refresh(s, false);
        Assert.Empty((JsonArray)r["added"]!);
        Assert.Equal("mp_fake2", (string)((JsonArray)r["deleted"]!).Single()!["map"]!);
        Assert.NotNull(Find(s, "mp_fake", "w1"));
    }

    [Fact]
    public void An_object_edited_and_then_reverted_counts_as_unedited()
    {
        var src = new Source("mp_fake");
        var s = Import(src, "mp_fake");
        var w1 = Find(s, "mp_fake", "w1");
        var home = w1.Transform.Position;
        Assert.True(s.Apply(Lab.Op($$"""{"op":"transform","id":"{{w1.Id}}","position":[99,0,0]}""")).Ok);
        src["mp_fake"].Walls[0] = Wall("w1", 11);
        Assert.True((bool)((JsonArray)Refresh(s, true)["changed"]!).Single()!["edited"]!);   // while moved: edited
        Assert.True(s.Apply(Lab.Op($$"""{"op":"transform","id":"{{w1.Id}}","position":[{{home.X}},{{home.Y}},{{home.Z}}]}""")).Ok);
        Assert.False((bool)((JsonArray)Refresh(s, true)["changed"]!).Single()!["edited"]!);   // back where it was: unedited
        Refresh(s, false, ""","applyAllUnedited":true""");
        Assert.Equal(11f, Find(s, "mp_fake", "w1").Transform.Position.X);
    }
}
