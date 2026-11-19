using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;

namespace SmMapmaker;

/// <summary>
/// refresh-from-classic {map?, preview?, apply?, applyAllUnedited?} (docs/mapmaker.md "Refresh from Classic"): recomputes what Classic's server adds to each original map the document was
/// imported from (placements, walls, kill boxes, cover; the same <see cref="Sm1ClassicKeys"/> keys and fingerprints as the import) and compares it with the objects carrying that
/// <c>src</c> link. The diff basis is (src.map, kind, key).
/// <list type="bullet">
/// <item><b>Added</b>: a Classic item no object carries and the owner did not delete. A run inserts it (into its sub-group, made when missing), honouring the file caps.</item>
/// <item><b>Removed</b>: an object whose key no longer exists in Classic. Listed; applying deletes it (its children keep their world place).</item>
/// <item><b>Changed</b>: the fingerprint differs (placement, size, shape, hidden state). Listed; applying sets the Classic transform, size, shape and visibility and stores the new fingerprint.</item>
/// <item><b>Edited by you</b>: a removed or changed object whose current state is not what the import or refresh left (<see cref="StateHash"/> against <c>src.ob</c>; an object with no
/// stored <c>ob</c>, from before this slice, counts as edited). <c>applyAllUnedited</c> skips them; only an explicit <c>apply</c> entry applies one.</item>
/// <item><b>Deleted by you</b>: the owner's deletions are remembered in the document's editor-only <c>removedSrc</c> list (<see cref="MapDocument.RemovedSrc"/>), so a deleted item is never re-added;
/// it is listed under <c>deleted</c> and can be brought back by naming its key in <c>apply</c>.</item>
/// </list>
/// The whole refresh is one batch step (one undo). Nothing is changed when <c>preview</c> is true (the default).
/// </summary>
internal sealed partial class Batch
{
    /// <summary>One Classic item as the import would make it.</summary>
    sealed record CItem(string Kind, string Key, string Fp, string Name, MapTransform T, PieceSpec? Piece, AssetRef? Asset, bool Hidden);

    /// <summary>The state of an imported object that "edited by you" compares: where it is, how big, its shape and whether it is switched on.</summary>
    public static string StateHash(MapObject o)
    {
        var t = o.Transform;
        return Sm1ClassicKeys.Fp("state", o.Visible ? "on" : "off", Sm1ClassicKeys.V(t.Position), Sm1ClassicKeys.V(t.Rotation), Sm1ClassicKeys.V(t.Scale),
            o.Piece?.Kind ?? "", o.Piece?.Shape ?? "", o.Asset?.Id ?? o.Asset?.Definition ?? "");
    }

    /// <summary>The object with its Classic link carrying the hash of its present state (the import and the refresh call this on what they create or update).</summary>
    internal static MapObject Stamp(MapObject o) => o.Src is null ? o : o with { Src = o.Src with { Ob = StateHash(o) } };

    static string Composite(string map, string kind, string key) => $"{map}|{kind}|{key}";

    /// <summary>Remembers the deleted objects' Classic identities (so a refresh does not re-add what the owner removed).</summary>
    void Forget(IEnumerable<MapObject> gone)
    {
        var have = new HashSet<SrcKey>(Doc.RemovedSrc);
        foreach (var o in gone)
            if (o.Src is { } s && Doc.RemovedSrc.Count < MapDocument.MaxObjects && have.Add(new SrcKey(s.Map, s.Kind, s.Key))) Doc.RemovedSrc.Add(new SrcKey(s.Map, s.Kind, s.Key));
    }

    static bool IsGroupObject(MapObject o) => o.Asset is null && o.Prefab is null && o.Piece is null;

    /// <summary>What Classic has for one map, in import order: walls, kill boxes, cover, then placements (shown, then hidden). Gameplay kinds are absent when the source has no reader for the map.</summary>
    List<CItem> ClassicItems(string map, ISm1Source source, out bool gameplayKnown)
    {
        var scene = source.LoadMap(map);
        var gameplay = source.LoadGameplay(map);
        gameplayKnown = gameplay is not null;
        var list = new List<CItem>();
        if (gameplay is not null)
        {
            var counts = new GameplayCounts();
            foreach (var p in PlanWalls(map, gameplay, counts).Concat(PlanKills(map, gameplay, counts)).Concat(PlanCover(map, gameplay, counts)))
                list.Add(new CItem(p.Src.Kind, p.Src.Key, p.Src.Fp, p.Name, p.Transform, p.Piece, null, false));
        }
        var keys = Sm1ClassicKeys.PlacementKeys(scene.Objects);
        var shown = new List<CItem>();
        var hidden = new List<CItem>();
        for (int i = 0; i < scene.Objects.Count; i++)
        {
            var o = scene.Objects[i];
            if (!scene.Definitions.TryGetValue(o.Definition, out var def) || def.Visuals == 0) continue;
            MapTransform t;
            try { t = TransformMath.Decompose(o.Transform); }
            catch (MapException) { continue; }
            bool isHidden = scene.Hidden?.Contains(i) == true;
            (isHidden ? hidden : shown).Add(new CItem(MapObjectSrc.Placement, keys[i], Sm1ClassicKeys.PlacementFp(o, isHidden), o.Name.Length > 0 ? o.Name : o.Definition, t, null, AssetRef.Sm1(map, o.Definition), isHidden));
        }
        list.AddRange(shown);
        list.AddRange(hidden);
        return list;
    }

    string? ParentFor(string map, CItem c)
    {
        // The sub-group the import used: where this map's objects of the same kind (and hidden state) already sit; else a child of the import group by name; else new.
        var mine = Doc.Objects.Where(o => o.Src is { } s && s.Map == map).ToList();
        var groups = Doc.Objects.Where(IsGroupObject).ToDictionary(g => g.Id, StringComparer.Ordinal);
        string? CommonParent(IEnumerable<MapObject> objs) =>
            objs.Where(o => o.Parent is not null && groups.ContainsKey(o.Parent))
                .GroupBy(o => o.Parent!).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        string? importGroup =
            CommonParent(mine.Where(o => o.Src!.Kind == MapObjectSrc.Placement && !o.Src.ClassicHidden))
            ?? mine.Select(o => o.Parent).Where(p => p is not null).Select(p => groups.GetValueOrDefault(p!)).Where(g => g is not null && g.Parent is not null)
                   .Select(g => g!.Parent).FirstOrDefault()
            ?? groups.Values.FirstOrDefault(g => g.Name == map)?.Id;
        if (c.Kind == MapObjectSrc.Placement && !c.Hidden) return importGroup;
        string name = c.Kind switch { MapObjectSrc.Wall => "Walls", MapObjectSrc.Kill => "Kill boxes", MapObjectSrc.Cover => "Cover", _ => "Hidden by Classic" };
        var same = mine.Where(o => o.Src!.Kind == c.Kind && (c.Kind != MapObjectSrc.Placement || o.Src.ClassicHidden == c.Hidden));
        if (CommonParent(same) is { } existing) return existing;
        var named = groups.Values.FirstOrDefault(g => g.Name == name && g.Parent == importGroup);
        return named?.Id;   // null: the caller creates it
    }

    void RefreshFromClassic(JsonObject op)
    {
        var only = Json.Str(op["map"], "map", "bad-op", nullable: true);
        bool preview = Json.Bool(op["preview"], "preview", "bad-op", true);
        bool allUnedited = Json.Bool(op["applyAllUnedited"], "applyAllUnedited", "bad-op", false);
        var apply = new HashSet<string>(StringComparer.Ordinal);
        if (op["apply"] is { } an) foreach (var x in Json.Arr(an, "apply", "bad-op")) apply.Add(Json.Str(x, "apply[]", "bad-op")!);
        var source = Sm1 ?? throw new MapException("missing-install", "The Space Marine install was not found, so the map cannot be refreshed from Classic. Set SM_GAME_DIR or pass --game-dir.");
        var maps = Doc.Objects.Where(o => o.Src is not null).Select(o => o.Src!.Map).Concat(Doc.RemovedSrc.Select(r => r.Map)).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
        if (only is not null) maps = maps.Where(m => m == only).ToList();

        var added = new JsonArray(); var removedOut = new JsonArray(); var changedOut = new JsonArray(); var deletedOut = new JsonArray(); var skipped = new JsonArray();
        var report = new JsonObject { ["preview"] = preview, ["maps"] = new JsonArray(maps.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()) };
        var toAdd = new List<(string Map, CItem Item)>();
        var toRestore = new List<(string Map, CItem Item)>();
        var toRemove = new List<(MapObject Obj, JsonObject Row)>();
        var toChange = new List<(MapObject Obj, CItem Item, JsonObject Row)>();

        JsonObject Row(string map, string kind, string key, string name, string? id) =>
            new() { ["map"] = map, ["kind"] = kind, ["key"] = key, ["name"] = name, ["id"] = id };

        foreach (var map in maps)
        {
            var items = ClassicItems(map, source, out bool gameplayKnown);
            if (!gameplayKnown) Warnings.Add($"No gameplay reader for '{map}': its walls, kill boxes and cover were not compared.");
            var byKey = new Dictionary<(string, string), CItem>();
            foreach (var c in items) byKey.TryAdd((c.Kind, c.Key), c);
            var existing = new Dictionary<(string, string), MapObject>();
            foreach (var o in Doc.Objects.Where(o => o.Src is { } s && s.Map == map)) existing.TryAdd((o.Src!.Kind, o.Src.Key), o);
            var tomb = new HashSet<(string, string)>(Doc.RemovedSrc.Where(r => r.Map == map).Select(r => (r.Kind, r.Key)));
            bool Compared(string kind) => kind == MapObjectSrc.Placement || gameplayKnown;

            foreach (var ((kind, key), o) in existing)
            {
                if (!Compared(kind)) continue;
                bool edited = o.Src!.Ob.Length == 0 || StateHash(o) != o.Src.Ob;
                if (!byKey.TryGetValue((kind, key), out var c))
                {
                    var row = Row(map, kind, key, o.Name, o.Id); row["edited"] = edited; row["applied"] = false;
                    removedOut.Add(row); toRemove.Add((o, row));
                }
                else if (c.Fp != o.Src.Fp)
                {
                    var row = Row(map, kind, key, o.Name, o.Id); row["edited"] = edited; row["applied"] = false;
                    row["hidden"] = c.Hidden;
                    changedOut.Add(row); toChange.Add((o, c, row));
                }
            }
            foreach (var c in items)
            {
                if (existing.ContainsKey((c.Kind, c.Key))) continue;
                if (tomb.Contains((c.Kind, c.Key)))
                {
                    var row = Row(map, c.Kind, c.Key, c.Name, null); row["restored"] = false;
                    deletedOut.Add(row);
                    if (apply.Contains(Composite(map, c.Kind, c.Key))) toRestore.Add((map, c));
                }
                else toAdd.Add((map, c));
            }
        }

        // ---- insert the added items, then restore the deleted ones the owner asked for (both honour the caps) ----
        if (!preview)
        {
            var insertList = toAdd.Select(a => (a.Map, a.Item, Restore: false)).Concat(toRestore.Select(a => (a.Map, a.Item, Restore: true))).ToList();
            int objects = Doc.Objects.Count, pieces = Doc.Objects.Count(o => o.Piece is not null);
            var madeGroups = new List<string>();
            var made = new List<(MapObject Obj, JsonObject Row, bool Hidden)>();
            var groupCache = new Dictionary<(string, string, bool), string?>();
            foreach (var (map, c, restore) in insertList)
            {
                var row = Row(map, c.Kind, c.Key, c.Name, null);
                if (c.Hidden) row["hidden"] = true;
                bool isPiece = c.Piece is not null;
                if (objects + 1 > MapDocument.MaxObjects) { row["reason"] = "too-many-objects"; skipped.Add(row); continue; }
                if (isPiece && pieces + 1 > MapDocument.MaxPieces) { row["reason"] = "too-many-pieces"; skipped.Add(row); continue; }
                var gk = (map, c.Kind, c.Hidden);
                if (!groupCache.TryGetValue(gk, out var parent))
                {
                    parent = ParentFor(map, c);
                    if (parent is null && !(c.Kind == MapObjectSrc.Placement && !c.Hidden))
                    {
                        // a sub-group is missing: make it (it costs one object), under the import group, which is made too when the map has none
                        if (objects + 3 > MapDocument.MaxObjects) { row["reason"] = "too-many-objects"; skipped.Add(row); continue; }
                        string name = c.Kind switch { MapObjectSrc.Wall => "Walls", MapObjectSrc.Kill => "Kill boxes", MapObjectSrc.Cover => "Cover", _ => "Hidden by Classic" };
                        var top = ParentFor(map, new CItem(MapObjectSrc.Placement, "", "", "", MapTransform.Identity, null, null, false));
                        if (top is null)
                        {
                            top = NewId();
                            Doc.Objects.Add(new MapObject(top, map, null, "default", true, MapTransform.Identity, null, null, [])); objects++; madeGroups.Add(top);
                        }
                        parent = NewId();
                        Doc.Objects.Add(new MapObject(parent, name, top, "default", true, MapTransform.Identity, null, null, [])); objects++; madeGroups.Add(parent);
                    }
                    groupCache[gk] = parent;
                }
                var id = NewId();
                var obj = Stamp(new MapObject(id, c.Name, parent, "default", !c.Hidden, c.T, c.Asset, null, [], c.Piece, new MapObjectSrc(map, c.Key, c.Fp, c.Kind, c.Hidden)));
                Doc.Objects.Add(obj); objects++; if (isPiece) pieces++;
                made.Add((obj, row, c.Hidden));
            }
            // the 8 MB file limit: drop what this run added, hidden placements first, last first (what is dropped is listed under skipped and comes back as "added" next time)
            var drop = new List<(MapObject Obj, JsonObject Row, bool Hidden)>();
            while (made.Count > 0 && Doc.ToJson().Length > MapDocument.MaxChars)
            {
                var pool = made.Any(m => m.Hidden) ? made.Where(m => m.Hidden).ToList() : made;
                int n = Math.Min(Math.Max(1, made.Count / 20), pool.Count);
                var gone = pool.GetRange(pool.Count - n, n);
                var ids = gone.Select(g => g.Obj.Id).ToHashSet();
                Doc.Objects.RemoveAll(o => ids.Contains(o.Id));
                made.RemoveAll(m => ids.Contains(m.Obj.Id));
                foreach (var g in gone) { g.Row["reason"] = "file-size"; skipped.Add(g.Row); }
            }
            foreach (var g in madeGroups) if (!Doc.Objects.Any(o => o.Parent == g)) Doc.Objects.RemoveAll(o => o.Id == g);
            foreach (var g in madeGroups.AsEnumerable().Reverse()) if (!Doc.Objects.Any(o => o.Parent == g)) Doc.Objects.RemoveAll(o => o.Id == g);
            var restoredKeys = toRestore.Select(r => new SrcKey(r.Map, r.Item.Kind, r.Item.Key)).ToHashSet();
            foreach (var (obj, row, _) in made)
            {
                row["id"] = obj.Id; Created.Add(obj.Id);
                var s = obj.Src!;
                if (restoredKeys.Contains(new SrcKey(s.Map, s.Kind, s.Key)))
                {
                    Doc.RemovedSrc.Remove(new SrcKey(s.Map, s.Kind, s.Key));
                    var d = deletedOut.OfType<JsonObject>().First(x => (string?)x["map"] == s.Map && (string?)x["kind"] == s.Kind && (string?)x["key"] == s.Key);
                    d["restored"] = true; d["id"] = obj.Id;
                }
                else added.Add(row);
            }
            // items that were skipped from the plain added list are reported under skipped only; restores that were skipped stay in 'deleted'
        }
        else
            foreach (var (map, c) in toAdd) { var row = Row(map, c.Kind, c.Key, c.Name, null); if (c.Hidden) row["hidden"] = true; added.Add(row); }

        // ---- removed and changed: only what is chosen (an explicit apply entry, or apply-all for the unedited) ----
        if (!preview)
        {
            bool Chosen(MapObject o, JsonObject row) =>
                apply.Contains(o.Id) || apply.Contains(Composite(o.Src!.Map, o.Src.Kind, o.Src.Key)) || (allUnedited && row["edited"]!.GetValue<bool>() == false);
            foreach (var (o, c, row) in toChange.Where(x => Chosen(x.Obj, x.Row)))
            {
                var parent = o.Parent;
                if (o.Src!.Kind == MapObjectSrc.Placement && c.Hidden != o.Src.ClassicHidden)
                {
                    // the hide state flipped in Classic: move it between the import group and 'Hidden by Classic'
                    var p = ParentFor(o.Src.Map, c);
                    if (p is not null) parent = p;
                }
                var next = Stamp(o with
                {
                    Parent = parent, Transform = c.T, Visible = !c.Hidden, Asset = c.Asset ?? o.Asset, Piece = c.Piece ?? o.Piece,
                    Src = o.Src with { Fp = c.Fp, ClassicHidden = c.Hidden },
                });
                Replace(next);
                row["applied"] = true;
            }
            foreach (var (o, row) in toRemove.Where(x => Chosen(x.Obj, x.Row)))
            {
                // children (the owner's own objects hung on it) stay where they are in the world
                foreach (var child in Doc.ChildrenOf(o.Id).ToList())
                    Run(new JsonObject { ["op"] = "set-parent", ["id"] = child.Id, ["parent"] = o.Parent }, out _);
                Doc.Objects.RemoveAll(x => x.Id == o.Id);   // not remembered: Classic no longer has the item
                Doc.DetachFromGroups(new HashSet<string> { o.Id });
                row["applied"] = true;
            }
        }

        report["added"] = added; report["removed"] = removedOut; report["changed"] = changedOut; report["deleted"] = deletedOut; report["skipped"] = skipped;
        Refresh = report;
        if (maps.Count == 0) Warnings.Add("This map holds no object imported from an SM1 map, so there is nothing to refresh.");
    }
}
