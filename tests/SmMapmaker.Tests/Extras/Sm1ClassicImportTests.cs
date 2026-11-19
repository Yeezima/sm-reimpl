using System.Numerics;
using SmContent;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>import-sm1-map as Classic plays it (slice S3): walls, kill boxes, cover, hidden placements, keys, fingerprints, the look; synthetic source only, no original bytes.</summary>
public sealed class Sm1ClassicImportTests : IDisposable
{
    readonly Lab _lab = new();
    public void Dispose() => _lab.Dispose();

    /// <summary>A fake map: shown placements, placements Classic hides, and Classic's gameplay.</summary>
    sealed class Source(int shown = 3, int hidden = 2, int nameLength = 0, float wallX = 10f, bool gameplay = true) : ISm1Source
    {
        public Sm1Map LoadMap(string map)
        {
            if (map != "mp_fake") throw new MapException("unknown-map", $"No map '{map}'.");
            var objects = new List<Sm1Object>();
            var hide = new HashSet<int>();
            string Name(string n) => nameLength > 0 ? n.PadRight(nameLength, 'x')[..nameLength] : n;
            for (int i = 0; i < shown; i++)
                objects.Add(new(i == 0 ? "" : i == 1 ? "twin" : i == 2 ? "twin" : Name($"s{i}"), "def_wall", "l", MapScene.Transform(new Vector3(i * 3, 0, 1), Vector3.Zero, Vector3.One), 0));
            for (int i = 0; i < hidden; i++) { hide.Add(objects.Count); objects.Add(new(Name($"h{i}"), "def_wall", "l", MapScene.Transform(new Vector3(0, i * 3 + 1, 1), Vector3.Zero, Vector3.One), 0)); }
            var defs = new Dictionary<string, Sm1Definition>(StringComparer.OrdinalIgnoreCase) { ["def_wall"] = new("def_wall", 1, new Bounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 2))) };
            return new Sm1Map(map, objects, defs, []) { Hidden = hide };
        }

        public Sm1Gameplay? LoadGameplay(string map) => !gameplay ? null : new Sm1Gameplay(
            [
                new MapWall("d_blockers", new Vector3(wallX, 0, 5), new Vector3(0, 0, 90), new Vector3(10, 2, 10), 1, 2, 1),
                new MapWall("d_blockers", new Vector3(-20, 4, 5), new Vector3(0, 0, 0), new Vector3(4, 2, 10), 1, 2, 1),
                new MapWall("d_round", new Vector3(0, 30, 4), new Vector3(0, 0, 0), new Vector3(6, 6, 8), 3, 2, 1),
                new MapWall("d_ball", new Vector3(0, 40, 4), new Vector3(0, 0, 0), new Vector3(6, 6, 6), 2, 2, 1),
            ],
            [new MapKillRegion("kill_pit", new Vector3(0, 0, -10), MathF.PI / 2f, new Vector3(20, 10, 4), 0)],
            [
                Prop("crate", new Vector3(5, 5, 0), new Vector3(1.2f, 3f, 1.5f)),
                Prop("", new Vector3(8, 5, 0), new Vector3(1f, 1f, 1f)),
                Prop("", new Vector3(8, 5, 0), new Vector3(1f, 1f, 1f)),   // the same place twice: the keys tie
            ]);

        static MapProp Prop(string name, Vector3 low, Vector3 size) =>
            new(name, "ammo_crate", "e_cover", 4, [low, low + size], [0, 1, 1]);
    }

    MapSession Imported(Source s, out OpResult r)
    {
        var session = MapSession.New("imported", _lab.Library, s);
        r = session.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        return session;
    }

    static IEnumerable<MapObject> Of(MapSession s, string groupName) =>
        s.Document.ChildrenOf(s.Document.Objects.Single(o => o.Name == groupName && o.Asset is null && o.Piece is null).Id);

    [Fact]
    public void Import_brings_classics_walls_kill_boxes_cover_and_hidden_placements_into_sub_groups()
    {
        var s = Imported(new Source(), out var r);
        var walls = Of(s, "Walls").ToList();
        Assert.Equal(3, walls.Count);   // two boxes and a cylinder; the sphere has no piece
        Assert.All(walls, w => Assert.Equal(PieceKind.MoveWall, w.Piece!.Kind));
        Assert.Equal(1, walls.Count(w => w.Piece!.IsCylinder));
        // a piece sits on the floor: the marker's centre minus half its height, turned about Z
        var first = walls[0];
        Assert.Equal(new Vector3(10, 0, 0), first.Transform.Position);
        Assert.Equal(90f, first.Transform.Rotation.Z, 3);
        Assert.Equal(new Vector3(10, 2, 10), first.Transform.Scale);
        Assert.All(walls, w => Assert.Equal(MapObjectSrc.Wall, w.Src!.Kind));
        var kill = Of(s, "Kill boxes").Single();
        Assert.Equal(PieceKind.KillZone, kill.Piece!.Kind);
        Assert.Equal(new Vector3(0, 0, -12), kill.Transform.Position);
        Assert.Equal(90f, kill.Transform.Rotation.Z, 3);
        Assert.Equal(MapObjectSrc.Kill, kill.Src!.Kind);
        var cover = Of(s, "Cover").ToList();
        Assert.Equal(3, cover.Count);
        Assert.All(cover, c => { Assert.Equal(PieceKind.Cover, c.Piece!.Kind); Assert.Equal(MapObjectSrc.Cover, c.Src!.Kind); });
        Assert.Equal(new Vector3(5.6f, 6.5f, 0f), cover[0].Transform.Position);   // the prop's box: middle of the footprint, on its floor
        Assert.Equal(new Vector3(1.2f, 3f, 1.5f), cover[0].Transform.Scale);
        // the placements Classic hides: imported, switched off, marked
        var hidden = Of(s, "Hidden by Classic").ToList();
        Assert.Equal(2, hidden.Count);
        Assert.All(hidden, h => { Assert.False(h.Visible); Assert.True(h.Src!.ClassicHidden); Assert.NotNull(h.Asset); Assert.Equal(MapObjectSrc.Placement, h.Src.Kind); });
        var shown = s.Document.Objects.Where(o => o.Asset is not null && o.Visible).ToList();
        Assert.Equal(3, shown.Count);
        Assert.All(shown, o => { Assert.False(o.Src!.ClassicHidden); Assert.Equal(MapObjectSrc.Placement, o.Src.Kind); });
        // the sub-groups sit under the import group
        var root = s.Document.Objects.Single(o => o.Parent is null);
        foreach (var g in new[] { "Walls", "Kill boxes", "Cover", "Hidden by Classic" })
            Assert.Equal(root.Id, s.Document.Objects.Single(o => o.Name == g && o.Piece is null && o.Asset is null).Parent);
        // the result says what came in
        Assert.Contains(r.Warnings, w => w.Contains("3 invisible wall(s)") && w.Contains("1 kill box(es)") && w.Contains("3 cover box(es)"));
        Assert.Contains(r.Warnings, w => w.Contains("2 placement(s) Classic hides"));
        Assert.Contains(r.Warnings, w => w.Contains("sphere"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("collision and the original's"));
    }

    [Fact]
    public void The_imported_pieces_compile_to_the_walls_kill_regions_and_props_Classic_has()
    {
        var s = Imported(new Source(), out _);
        var map = CustomMap.Compile(s.Document);
        Assert.Equal(3, map.Content.Walls.Count);
        Assert.Contains(map.Content.Walls, w => w.Shape == 3);
        Assert.Contains(map.Content.KillRegions, k => k.Name.StartsWith("cm_kill_") && k.Position == new Vector3(0, 0, -10));
        Assert.Equal(3, map.Content.Props.Count);
    }

    [Fact]
    public void The_look_is_set_in_the_same_batch_and_one_undo_removes_everything()
    {
        var s = Imported(new Source(), out _);
        Assert.Equal("mp_fake", s.Document.Look?.Sm1Map);
        Assert.True(s.Undo().Ok);
        Assert.Empty(s.Document.Objects);
        Assert.Null(s.Document.Look);
        Assert.True(s.Redo().Ok);
        Assert.Equal("mp_fake", s.Document.Look?.Sm1Map);
    }

    [Fact]
    public void Keys_are_unique_stable_and_follow_the_rule()
    {
        var a = Imported(new Source(), out _);
        var b = Imported(new Source(), out _);
        var srcA = a.Document.Objects.Where(o => o.Src is not null).Select(o => o.Src!).ToList();
        var srcB = b.Document.Objects.Where(o => o.Src is not null).Select(o => o.Src!).ToList();
        Assert.Equal(srcA.Select(x => (x.Kind, x.Key, x.Fp)), srcB.Select(x => (x.Kind, x.Key, x.Fp)));   // the same Classic data, the same links
        foreach (var kind in MapObjectSrc.Kinds) Assert.Equal(srcA.Count(x => x.Kind == kind), srcA.Where(x => x.Kind == kind).Select(x => x.Key).Distinct().Count());
        var walls = srcA.Where(x => x.Kind == MapObjectSrc.Wall).Select(x => x.Key).ToList();
        Assert.Contains("d_round", walls);                      // a unique name is the key
        Assert.Contains("wall@10,0,5", walls);                  // a repeated name falls back to label@position (0.01 m)
        Assert.Contains("wall@-20,4,5", walls);
        var placements = srcA.Where(x => x.Kind == MapObjectSrc.Placement).Select(x => x.Key).ToList();
        Assert.Contains("def_wall@0,0,1", placements);          // an empty name
        Assert.Contains("def_wall@3,0,1", placements);          // duplicates of "twin" use the definition and position
        Assert.Contains("def_wall@6,0,1", placements);
        Assert.Contains("h0", placements);
        var cover = srcA.Where(x => x.Kind == MapObjectSrc.Cover).Select(x => x.Key).ToList();
        Assert.Contains("crate", cover);
        Assert.Contains("ammo_crate@8.5,5.5,0.5", cover);
        Assert.Contains("ammo_crate@8.5,5.5,0.5#2", cover);     // a tie gets #n
        // the shared helper computes the same keys a refresh will
        Assert.Equal(placements.Count, Sm1ClassicKeys.PlacementKeys(new Source().LoadMap("mp_fake").Objects).Distinct().Count());
        Assert.Equal("def_wall@1.23,-0.5,0", "def_wall" + Sm1ClassicKeys.At(new Vector3(1.234f, -0.5f, -0.001f)));
    }

    [Fact]
    public void The_fingerprint_changes_when_a_classic_value_changes_and_only_then()
    {
        var a = Imported(new Source(wallX: 10f), out _);
        var same = Imported(new Source(wallX: 10f), out _);
        var moved = Imported(new Source(wallX: 11f), out _);
        string Fp(MapSession s, string key) => s.Document.Objects.Single(o => o.Src?.Key == key).Src!.Fp;
        string Key(MapSession s) => s.Document.Objects.Single(o => o.Src is { Kind: MapObjectSrc.Wall } && o.Transform.Rotation.Z == 90f).Src!.Key;
        Assert.Equal(Fp(a, Key(a)), Fp(same, Key(same)));
        Assert.NotEqual(Fp(a, Key(a)), Fp(moved, Key(moved)));
        Assert.Equal(Fp(a, "d_round"), Fp(moved, "d_round"));   // the other walls did not change
        // hidden is part of the fingerprint: the same placement shown and hidden differ
        var o0 = new Source().LoadMap("mp_fake").Objects[0];
        Assert.NotEqual(Sm1ClassicKeys.PlacementFp(o0, false), Sm1ClassicKeys.PlacementFp(o0, true));
        // editing the imported object does not touch its link (editor data carried by the record)
        var wall = a.Document.Objects.First(o => o.Src is { Kind: MapObjectSrc.Wall });
        var link = wall.Src;
        Assert.True(a.Apply(Lab.Op($$"""{"op":"transform","id":"{{wall.Id}}","move":[1,0,0]}""")).Ok);
        Assert.Equal(link, a.Document.Find(wall.Id)!.Src);
    }

    [Fact]
    public void A_source_without_gameplay_still_imports_placements_and_says_so()
    {
        var s = Imported(new Source(gameplay: false), out var r);
        Assert.DoesNotContain(s.Document.Objects, o => o.Piece?.Kind is PieceKind.MoveWall or PieceKind.Cover or PieceKind.KillZone);
        Assert.Contains(r.Warnings, w => w.Contains("not available for this map"));
        Assert.DoesNotContain(s.Document.Objects, o => o.Name is "Walls" or "Kill boxes" or "Cover");
    }

    [Fact]
    public void Caps_keep_gameplay_and_visible_placements_and_cut_the_hidden_ones_first()
    {
        // 14990 shown + 50 hidden: with the spawns' placeholders (10), three gameplay groups and the hidden group, the cap leaves room for fewer than all
        var s = Imported(new Source(shown: Sm1Import.MaxObjects - 10, hidden: 50), out var r);
        Assert.Equal(3, Of(s, "Walls").Count());
        Assert.Single(Of(s, "Kill boxes"));
        Assert.Equal(3, Of(s, "Cover").Count());
        var shown = s.Document.Objects.Count(o => o.Asset is not null && o.Visible);
        var hidden = s.Document.Objects.Count(o => o.Asset is not null && !o.Visible);
        Assert.True(shown > 0);
        Assert.True(hidden < 50 || shown == Sm1Import.MaxObjects - 10);   // under the cap the hidden are what does not fit
        Assert.True(s.Document.Objects.Count <= MapDocument.MaxObjects);
        Assert.Contains(r.Warnings, w => w.Contains("left out") && w.Contains(MapDocument.MaxObjects.ToString()));
        Assert.Equal(s.Document.Objects.Count, MapDocument.Parse(s.Document.ToJson()).Objects.Count);
    }

    [Fact]
    public void The_size_limit_drops_hidden_placements_first_and_never_gameplay()
    {
        // long names make the file reach 8 MB before the object limit
        var s = Imported(new Source(shown: 7000, hidden: 7000, nameLength: 200), out var r);
        var json = s.Document.ToJson();
        Assert.True(json.Length <= MapDocument.MaxChars, $"{json.Length} chars");
        Assert.Equal(3, Of(s, "Walls").Count());
        Assert.Single(Of(s, "Kill boxes"));
        Assert.Equal(3, Of(s, "Cover").Count());
        var shown = s.Document.Objects.Count(o => o.Asset is not null && o.Visible);
        var hidden = s.Document.Objects.Count(o => o.Asset is not null && !o.Visible);
        Assert.True(hidden < 7000, "some hidden placements had to go");
        Assert.True(hidden == 0 ? shown <= 7000 : shown == 7000, $"shown {shown}, hidden {hidden}");   // visible ones only go once no hidden are left
        if (hidden == 0) Assert.DoesNotContain(s.Document.Objects, o => o.Name == "Hidden by Classic");
        Assert.Contains(r.Warnings, w => w.Contains("to keep the map file under 8 MB"));
    }
}
