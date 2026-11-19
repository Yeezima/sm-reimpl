using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>refresh-from-classic (slice S5): add, remove, change, edited-by-you, owner deletions, idempotence, one undo, caps. Synthetic source only.</summary>
public sealed class Sm1RefreshTests : IDisposable
{
    readonly Lab _lab = new();
    public void Dispose() => _lab.Dispose();

    /// <summary>A fake map whose Classic data the test edits between refreshes.</summary>
    sealed class Source : ISm1Source
    {
        public List<(string Name, Vector3 At)> Shown = [("s0", new(0, 0, 1)), ("s1", new(3, 0, 1))];
        public List<(string Name, Vector3 At)> Hidden = [("h0", new(0, 3, 1))];
        public List<MapWall> Walls = [Wall("w1", 10), Wall("w2", 20)];
        public List<MapKillRegion> Kills = [new("kill_pit", new Vector3(0, 0, -10), 0f, new Vector3(20, 10, 4), 0)];
        public List<MapProp> Props = [new("crate", "ammo_crate", "e_cover", 4, [new Vector3(5, 5, 0), new Vector3(6, 6, 1)], [0, 1, 1])];

        public static MapWall Wall(string name, float x) => new(name, new Vector3(x, 0, 5), Vector3.Zero, new Vector3(10, 2, 10), 1, 2, 1);

        public Sm1Map LoadMap(string map)
        {
            if (map != "mp_fake") throw new MapException("unknown-map", $"No map '{map}'.");
            var objects = new List<Sm1Object>();
            var hide = new HashSet<int>();
            foreach (var (n, at) in Shown) objects.Add(new(n, "def_wall", "l", MapScene.Transform(at, Vector3.Zero, Vector3.One), 0));
            foreach (var (n, at) in Hidden) { hide.Add(objects.Count); objects.Add(new(n, "def_wall", "l", MapScene.Transform(at, Vector3.Zero, Vector3.One), 0)); }
            var defs = new Dictionary<string, Sm1Definition>(StringComparer.OrdinalIgnoreCase) { ["def_wall"] = new("def_wall", 1, new Bounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 2))) };
            return new Sm1Map(map, objects, defs, []) { Hidden = hide };
        }

        public Sm1Gameplay? LoadGameplay(string map) => new Sm1Gameplay(Walls, Kills, Props);
    }

    (MapSession S, Source Src) Imported()
    {
        var src = new Source();
        var s = MapSession.New("m", _lab.Library, src);
        Assert.True(s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}""")).Ok);
        return (s, src);
    }

    static JsonObject Refresh(MapSession s, bool preview, string extra = "") =>
        Report(s.Apply(Lab.Op($$"""{"op":"refresh-from-classic","preview":{{(preview ? "true" : "false")}}{{extra}}}""")));

    static JsonObject Report(OpResult r)
    {
        Assert.True(r.Ok, r.ToJson());
        return r.Refresh!;
    }

    static MapObject Find(MapSession s, string key) => s.Document.Objects.Single(o => o.Src?.Key == key);

    static string[] Keys(JsonNode? list) => [.. ((JsonArray)list!).Select(x => (string)x!["key"]!)];

    [Fact]
    public void Nothing_to_do_right_after_the_import_and_preview_changes_nothing()
    {
        var (s, _) = Imported();
        var r = Refresh(s, true);
        foreach (var k in new[] { "added", "removed", "changed", "deleted", "skipped" }) Assert.Empty((JsonArray)r[k]!);
    }

    [Fact]
    public void Added_items_are_listed_in_preview_and_inserted_into_their_sub_groups_by_a_run()
    {
        var (s, src) = Imported();
        src.Walls.Add(Source.Wall("w3", 30));
        src.Shown.Add(("s2", new(6, 0, 1)));
        src.Hidden.Add(("h1", new(0, 6, 1)));
        int before = s.Document.Objects.Count;
        var p = Refresh(s, true);
        Assert.Equal(["w3", "s2", "h1"], Keys(p["added"]));
        Assert.Equal(before, s.Document.Objects.Count);
        var r = Refresh(s, false);
        Assert.Equal(before + 3, s.Document.Objects.Count);
        var wall = Find(s, "w3");
        Assert.Equal("Walls", s.Document.Objects.Single(o => o.Id == wall.Parent).Name);
        Assert.Equal(new Vector3(30, 0, 0), wall.Transform.Position);
        Assert.False(Find(s, "h1").Visible);
        Assert.Equal("Hidden by Classic", s.Document.Objects.Single(o => o.Id == Find(s, "h1").Parent).Name);
        Assert.Equal(s.Document.Objects.Single(o => o.Parent is null).Id, Find(s, "s2").Parent);
        Assert.All(((JsonArray)r["added"]!), row => Assert.NotNull(row!["id"]));
        // idempotent: a second run has nothing left to add, and the stored fingerprints say nothing changed
        var again = Refresh(s, false);
        foreach (var k in new[] { "added", "removed", "changed", "skipped" }) Assert.Empty((JsonArray)again[k]!);
    }

    [Fact]
    public void A_missing_sub_group_is_made()
    {
        var (s, src) = Imported();
        src.Kills.Clear();
        Assert.True(s.Apply(Lab.Op($$"""{"op":"delete","ids":["{{s.Document.Objects.Single(o => o.Name == "Kill boxes").Id}}"]}""")).Ok);
        src.Kills.Add(new MapKillRegion("kill_new", new Vector3(0, 0, -20), 0f, new Vector3(5, 5, 5), 0));
        Refresh(s, false);
        var kill = Find(s, "kill_new");
        Assert.Equal("Kill boxes", s.Document.Objects.Single(o => o.Id == kill.Parent).Name);
        Assert.Equal(s.Document.Objects.Single(o => o.Parent is null).Id, s.Document.Objects.Single(o => o.Name == "Kill boxes").Parent);
    }

    [Fact]
    public void Removed_items_are_listed_and_applied_by_id_or_by_apply_all()
    {
        var (s, src) = Imported();
        src.Walls.RemoveAll(w => w.Name == "w2");
        src.Shown.RemoveAll(x => x.Name == "s1");
        var p = Refresh(s, true);
        Assert.Equal(["w2", "s1"], Keys(p["removed"]));
        Assert.All((JsonArray)p["removed"]!, row => Assert.False((bool)row!["edited"]!));
        Assert.Equal(2, s.Document.Objects.Count(o => o.Src?.Key is "w2" or "s1"));
        var id = Find(s, "w2").Id;
        var r = Refresh(s, false, $$""","apply":["{{id}}"]""");
        Assert.DoesNotContain(s.Document.Objects, o => o.Src?.Key == "w2");
        Assert.Contains(s.Document.Objects, o => o.Src?.Key == "s1");   // not chosen: stays, still listed
        Assert.Equal(["s1"], Keys(Refresh(s, true)["removed"]));
        Refresh(s, false, ""","applyAllUnedited":true""");
        Assert.DoesNotContain(s.Document.Objects, o => o.Src?.Key == "s1");
        Assert.True((bool)((JsonArray)r["removed"]!).First(x => (string)x!["key"]! == "w2")!["applied"]!);
        // removing what Classic dropped is not a deletion by the owner: nothing is remembered
        Assert.Empty(s.Document.RemovedSrc);
    }

    [Fact]
    public void Changed_items_are_listed_and_applied_with_a_new_fingerprint()
    {
        var (s, src) = Imported();
        src.Walls[0] = Source.Wall("w1", 11);
        src.Hidden.Clear();
        src.Shown.Add(("h0", new(0, 3, 1)));   // Classic stopped hiding h0: same key, different fingerprint
        var p = Refresh(s, true);
        Assert.Equal(["w1", "h0"], Keys(p["changed"]).OrderByDescending(k => k == "w1").ToArray());
        Refresh(s, false, ""","applyAllUnedited":true""");
        Assert.Equal(11f, Find(s, "w1").Transform.Position.X);
        var h = Find(s, "h0");
        Assert.True(h.Visible);
        Assert.False(h.Src!.ClassicHidden);
        Assert.Equal(s.Document.Objects.Single(o => o.Parent is null).Id, h.Parent);
        var again = Refresh(s, true);
        foreach (var k in new[] { "added", "removed", "changed" }) Assert.Empty((JsonArray)again[k]!);
    }

    [Fact]
    public void An_object_the_owner_moved_is_flagged_edited_and_apply_all_leaves_it()
    {
        var (s, src) = Imported();
        var w1 = Find(s, "w1");
        Assert.True(s.Apply(Lab.Op($$"""{"op":"transform","id":"{{w1.Id}}","position":[99,0,0]}""")).Ok);
        src.Walls[0] = Source.Wall("w1", 11);
        src.Walls.RemoveAll(w => w.Name == "w2");
        var p = Refresh(s, true);
        var changed = ((JsonArray)p["changed"]!).Single()!;
        Assert.True((bool)changed["edited"]!);
        Refresh(s, false, ""","applyAllUnedited":true""");
        Assert.Equal(99f, Find(s, "w1").Transform.Position.X);   // untouched
        Assert.DoesNotContain(s.Document.Objects, o => o.Src?.Key == "w2");   // the unedited removal was applied
        Refresh(s, false, $$""","apply":["{{w1.Id}}"]""");   // an explicit choice applies an edited one
        Assert.Equal(11f, Find(s, "w1").Transform.Position.X);
    }

    [Fact]
    public void An_object_the_owner_deleted_is_not_added_again_but_can_be_restored_by_key()
    {
        var (s, _) = Imported();
        Assert.True(s.Apply(Lab.Op($$"""{"op":"delete","ids":["{{Find(s, "w1").Id}}"]}""")).Ok);
        Assert.Single(s.Document.RemovedSrc);
        var p = Refresh(s, false);
        Assert.Empty((JsonArray)p["added"]!);
        Assert.Equal(["w1"], Keys(p["deleted"]));
        Assert.DoesNotContain(s.Document.Objects, o => o.Src?.Key == "w1");
        // the memory is editor-only: saved and read back, outside the map's hash
        var text = s.Document.ToJson();
        Assert.Contains("removedSrc", text);
        Assert.DoesNotContain("removedSrc", s.Document.ToGameJson());
        Assert.Single(MapDocument.Parse(text).RemovedSrc);
        var r = Refresh(s, false, ""","apply":["mp_fake|wall|w1"]""");
        Assert.Contains(s.Document.Objects, o => o.Src?.Key == "w1");
        Assert.Empty(s.Document.RemovedSrc);
        Assert.True((bool)((JsonArray)r["deleted"]!).Single()!["restored"]!);
    }

    [Fact]
    public void The_whole_refresh_is_one_undo_step_and_undo_restores_the_deletion_memory()
    {
        var (s, src) = Imported();
        src.Walls.Add(Source.Wall("w3", 30));
        src.Walls.RemoveAll(w => w.Name == "w2");
        src.Kills[0] = new MapKillRegion("kill_pit", new Vector3(1, 0, -10), 0f, new Vector3(20, 10, 4), 0);
        string before = s.Document.ToJson();
        Refresh(s, false, ""","applyAllUnedited":true""");
        Assert.NotEqual(before, s.Document.ToJson());
        Assert.True(s.Undo().Ok);
        Assert.Equal(before, s.Document.ToJson());
        Assert.True(s.Redo().Ok);
        Assert.Contains(s.Document.Objects, o => o.Src?.Key == "w3");
    }

    [Fact]
    public void The_caps_are_respected_and_skipped_items_come_back_next_time()
    {
        var (s, src) = Imported();
        var filler = Enumerable.Range(0, MapDocument.MaxObjects - s.Document.Objects.Count - 1).Select(i => $$"""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"f{{i}}"}""");
        // fill the map with plain placements until one object is free; a wall and a placement are wanted
        foreach (var chunk in filler.Chunk(500)) Assert.True(s.Apply(chunk.Select(Lab.Op)).Ok);
        Assert.Equal(MapDocument.MaxObjects - 1, s.Document.Objects.Count);
        src.Shown.Add(("s2", new(6, 0, 1)));
        src.Shown.Add(("s3", new(9, 0, 1)));
        var r = Refresh(s, false);
        Assert.Equal(MapDocument.MaxObjects, s.Document.Objects.Count);
        Assert.Single((JsonArray)r["added"]!);
        Assert.Equal(["s3"], Keys(r["skipped"]));
        Assert.Equal("too-many-objects", (string)((JsonArray)r["skipped"]!).Single()!["reason"]!);
        Assert.Equal(["s3"], Keys(Refresh(s, true)["added"]));
    }

    [Fact]
    public void Refuses_cleanly_without_an_install()
    {
        var s = MapSession.New("m", _lab.Library, null);
        var r = s.Apply(Lab.Op("""{"op":"refresh-from-classic"}"""));
        Assert.False(r.Ok);
        Assert.Equal("missing-install", r.Errors[0].Code);
    }
}
