using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 5 (ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Draft 5"): every visible graybox structure of the v5 generator (a tagged group) is replaced by ONE asset of the owner's library,
/// scaled to the structure's visual mass and placed on it; the level's collision is the fitted boxes of those assets (<see cref="AshfallKitCollision"/>) transformed with the placement. The stronghold is rebuilt as a breached ruin
/// enclosure. Draft 6 (<c>AshfallDraft6.cs</c>) grows every placed object 1.5x, adds AC-008 shooting platforms and keeps a bridge only on supports. Deterministic: integer hashes (<see cref="AshfallBuilder.Mix"/>), the 15 degree table for every turn, total-order sorts, no System.Random, no logarithms.
/// </summary>
internal static partial class AshfallDraft5Pass
{
    /// <summary>A placed box in its item's local frame: centre, half sizes along the turned x, y and up, and the yaw step (x 15 degrees).</summary>
    readonly record struct WBox(Vector3 Centre, Vector3 Half, int YawStep)
    {
        public Vector2 Extent
        {
            get { var (s, c) = AshfallBuilder.Trig(YawStep); return new(MathF.Abs(c) * Half.X + MathF.Abs(s) * Half.Y, MathF.Abs(s) * Half.X + MathF.Abs(c) * Half.Y); }
        }

        public Vector4 Rect { get { var e = Extent; return new(Centre.X - e.X, Centre.Y - e.Y, Centre.X + e.X, Centre.Y + e.Y); } }
    }

    /// <summary>Models with walkable tops or stairs (the finer fit of the collision command): their cell, scaled, must stay under the step height.</summary>
    static readonly HashSet<string> Walkable = [.. AshfallKitFit.WalkableIds];

    const float StepHeight = 0.4f, Grow = 1f, MinBox = 0.15f;

    sealed class Ctx
    {
        public readonly List<AshfallPart> Parts = [];
        public readonly List<AshfallDressItem> Items = [];
        public readonly List<(int First, int Count)> ItemParts = [];
        public readonly List<AshfallStructure> Structures = [];
        public readonly List<string> Flags = [];
        public readonly List<AshfallPlacement> Log = [];
        public readonly List<(string Id, Vector2 At)> Placed = [];
        public readonly List<(int Item, WBox Box)> AllBoxes = [];
        public readonly Dictionary<string, int> Uses = [];
        public readonly Dictionary<string, int> DroppedBoxes = [];
        public readonly List<string> Dropped = [];
        public int RampsKept, RampsDropped, RampsMisfit;
        public uint Seed;
        public uint Counter;
        public List<Vector4> BannedRects = [];
        public List<Vector3> BannedDiscs = [];
        public List<Vector4> BreachRects = [];
        /// <summary>Draft 6: the bridges, placed last against what stands at their ends (name, v5 bounds, v5 deck top).</summary>
        public readonly List<(string Name, Vector3 Min, Vector3 Max, float Deck)> Bridges = [];
        /// <summary>Draft 6: items moved back from a protected place after the growth, and items left out because they could not fit.</summary>
        public int Moved, LeftOut, NarrowKept, Spread, NarrowDropped;
        /// <summary>Phase 2: the last growth failed because a small piece could not settle its narrow gaps (left out, not a protected place).</summary>
        public bool NarrowDrop;
        /// <summary>Draft 6: the footprints of the placed boxes in the walking band (bottom under 2.5 m, top over 0.4 m: what the route check counts as in the way) by 8 m cell, for the narrow-gap rule.</summary>
        public readonly Dictionary<(int, int), List<(int Item, Vector4 Rect)>> Low = [];
        public readonly List<string> LeftOutNames = [];
        /// <summary>Draft 7: every placed box but the bridges', by 16 m cell, for the overlap rule; the items it moved or shrank, and those it left out.</summary>
        public readonly Dictionary<(int, int), List<(int Item, WBox Box)>> Solid = [];
        public bool OverlapRule = true;
        public int OverlapSettled, OverlapDropped;
        public readonly List<string> OverlapDroppedNames = [];
        /// <summary>Draft 8: the roadway strips (grown by the margin), whether the rule runs, the items it moved or shrank, and those it left out.</summary>
        public List<AshfallBox> Roads = [];
        public bool RoadwayRule = true;
        public int RoadSettled, RoadDropped, RoadPlatformsSkipped;
        public readonly List<string> RoadDroppedNames = [];
        /// <summary>Draft 9 (late batch F): whether its rules run (stairs from the meshes, stair zones, drawn boxes), the placed drawn boxes and stair zones by 16 m cell, and the stair counts and notes.</summary>
        public bool Draft9 = true;
        public readonly Dictionary<(int, int), List<(int Item, WBox Box, bool Ring)>> DrawnBoxes = [];
        public readonly Dictionary<(int, int), List<(int Item, WBox Box)>> Zones = [];
        public int StairsKept, StairsDropped, StairsCarved, Draft9Settled, Draft9Dropped;
        public readonly List<string> StairNotes = [];
        /// <summary>Draft 10: models refused for a flight with no access point (group: model), and the structures left out because no model of their pool had none.</summary>
        public readonly List<string> StairAccessRefused = [], StairAccessDroppedNames = [];
        public int StairAccessDropped;
        public readonly List<string> Draft9DroppedNames = [];
        /// <summary>Draft 9's urban rules: the buildings' graybox footprints, the debris left out away from them, the pieces turned onto the street grid.</summary>
        public readonly List<Vector4> Buildings = [];
        public int DebrisAlone, Snapped;
        /// <summary>Draft 11: the battlefield plan when the field layout runs (null: draft 10, every rule as before).</summary>
        public AshfallFieldPlan? Field;
    }

    sealed record Spec(string Group, string Family, Vector3 Min, Vector3 Max, AshfallBox? Box, string[] Pool)
    {
        public float GrowAlong = Grow, GrowAcross = Grow;
        public int[]? Yaws;
        public bool UseNet = true;
        public Vector2? Out;
        public AshfallKind? Kind;
        public bool NoCollision;
        public bool Required;
        /// <summary>Draft 6: the length along the item's local X keeps its draft-5 value when the item grows (the stronghold's leg walls and gate: a breach never closes).</summary>
        public bool KeepLength;
        /// <summary>Draft 6, phase 2: a walkable model on a required route (the gatehouse's passage) grows no taller than its walk cell allows, so its floor's quantised sill stays under the 0.4 m step.</summary>
        public bool KeepWalkCell;
    }

    // ---------------------------------------------------------------- entry

    public static AshfallDraft5 Run() => Run(overlapRule: true);

    /// <summary>Draft 11: the pass on the field layout's graybox (<see cref="AshfallMap.BuildFieldGraybox"/>), every standing rule of drafts 7 to 10 on, its corridors as roadways.</summary>
    public static AshfallDraft5 Run(AshfallFieldPlan field) => Run(overlapRule: true, roadwayRule: true, draft9: true, field: field);

    /// <summary>The pass; <paramref name="overlapRule"/> false skips draft 7's overlap rule, <paramref name="roadwayRule"/> false draft 8's roadway rule (report aids: the placement without the rule, under the same version).</summary>
    public static AshfallDraft5 Run(bool overlapRule, bool roadwayRule = true, bool draft9 = true, AshfallFieldPlan? field = null)
    {
        var draft10 = field is null ? null : AshfallMap.BuildDraft5();   // draft 11: the fortress comes from draft 10 (built first, on draft 10's own ground)
        using var ground = new AshfallMap.FieldScope(field is not null);
        bool wasCity = t_city; t_city = field is not null;   // draft 12: the planned layouts' stair rules (the city and, since the owner's stair pass of 2026-11-09, the field; draft 10 stays the rollback)
        var wasRoads = t_cityRoads;
        try { return RunPass(overlapRule, roadwayRule, draft9, field, draft10); } finally { t_city = wasCity; t_cityRoads = wasRoads; }
    }

    static AshfallDraft5 RunPass(bool overlapRule, bool roadwayRule, bool draft9, AshfallFieldPlan? field, AshfallDraft5? draft10)
    {
        var gb = field is null ? AshfallMap.BuildGrayboxWithFeatures() : AshfallMap.BuildFieldGraybox(field);
        var g5 = gb.Level;
        var c = new Ctx { Seed = AshfallBuilder.Mix(field is null ? AshfallMap.VariationSeed : field.Params.DressSeed, 0xA5F411u), OverlapRule = overlapRule, RoadwayRule = roadwayRule, Draft9 = draft9, Field = field };
        c.Roads = field is null ? AshfallRoadway.Strips(g5) : AshfallRoadway.FieldStrips(field);
        if (t_city) t_cityRoads = c.Roads;
        foreach (var p in g5.Parts)
            if (p.Kind is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary or AshfallKind.Objective) c.Parts.Add(p);
        // Draft 10's lanes (AshfallLanes): paved connectors to the points and the gate, roadways for the draft-8 rule, drawn as road.
        var lanes = draft9 && field is null ? AshfallLanes.Parts() : [];
        c.Parts.AddRange(lanes);
        foreach (var p in lanes)
            if (AshfallBox.Of(p) is { } lb) c.Roads.Add(lb with { Z0 = -2f, Z1 = 1000f, Length = lb.Length + 2f * AshfallRoadway.Margin, Depth = lb.Depth + 2f * AshfallRoadway.Margin });
        int groundCount = c.Parts.Count;

        BuildBanned(c, g5);

        var items = new List<AshfallDressItem>();
        if (field is null) AshfallDressPass.SurfaceItems([.. g5.Parts, .. lanes], items); else AshfallDressPass.FieldSurfaceItems(field, items);
        foreach (var it in items) { c.Items.Add(it); c.ItemParts.Add((0, 0)); }

        // Objective A: capture point on the ground; draft 6: the expansion's round objective dais (AC-076, made for it) as a walk-over dais (visual), in place of draft 5's squashed plaza centre tile (AC-037).
        var a = g5.Points[0];
        var points = g5.Points.ToList();
        points[0] = a with { Center = new Vector3(a.Center.X, a.Center.Y, AshfallMap.BlockTop) };
        c.Items.Add(new AshfallDressItem("AC-076", new Vector3(a.Center.X, a.Center.Y, AshfallMap.BlockTop + 0.02f), 0f, new Vector3(DaisSize, DaisSize, DaisHeight), false, AshfallDressRole.Surface, -1));
        c.ItemParts.Add((0, 0));
        c.Flags.Add($"A: the plinth with its four stairs is gone; the control point stands on the ground (z 0.25) on AC-076, the round objective dais, {DaisSize:0} m across squashed to a {DaisHeight:0.0#} m walk-over dais (visual only; its 1 m of steps would put collision in the capture zone)");

        // The tagged groups, in creation order.
        var partGroup = gb.PartGroup!;
        var groups = gb.Groups!;
        var byGroup = new List<int>[groups.Count];
        for (int i = 0; i < byGroup.Length; i++) byGroup[i] = [];
        var untagged = new List<int>();
        for (int i = 0; i < g5.Parts.Count; i++)
        {
            var k = g5.Parts[i].Kind;
            if (k is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary or AshfallKind.Objective) continue;
            if (partGroup[i] >= 0) byGroup[partGroup[i]].Add(i); else untagged.Add(i);
        }
        foreach (int i in untagged)
            c.Flags.Add($"untagged graybox part {i} ({g5.Parts[i].Kind}) has no structure group and is dropped");

        var stronghold = new List<int>();
        var debris = new List<int>();   // draft 9: loose debris is placed after the buildings, against the ones that stand
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            if (byGroup[gi].Count == 0) continue;
            if (g.Family.StartsWith("stronghold") || g.Family is "terrace" or "outerwall" || g.Name == "keep bridge") { stronghold.Add(gi); continue; }
            if (field is not null && IsTransplanted(g.Name)) continue;   // draft 11: the compound's walls come from draft 10 with the fortress
            var (min, max) = Bounds(g5.Parts, byGroup[gi]);
            AshfallBox? box = byGroup[gi].Count == 1 ? AshfallBox.Of(g5.Parts[byGroup[gi][0]]) : null;
            if (draft9 && g.Family is "rubble" or "wallpiece") { debris.Add(gi); continue; }
            if (g.Family == "bridge") c.Bridges.Add((g.Name, min, max, DeckTop(g5.Parts, byGroup[gi]))); else Generic(c, g, min, max, box, g5);
        }
        if (draft9)
        {
            // Draft 9's urban rules: the buildings that stand (their drawn boxes), then the debris at their feet.
            foreach (var l in c.Log)
                if (l.Item >= 0 && IsBuildingFamily(l.Family, c.Items[l.Item].Size.Z))
                {
                    var it = c.Items[l.Item];
                    int ys = ((int)MathF.Round(it.Yaw / (MathF.PI / 12f)) % 24 + 24) % 24;
                    c.Buildings.Add(Drawn(it, ys).Rect);
                }
            foreach (int gi in debris)
            {
                var (min, max) = Bounds(g5.Parts, byGroup[gi]);
                AshfallBox? box = byGroup[gi].Count == 1 ? AshfallBox.Of(g5.Parts[byGroup[gi][0]]) : null;
                Generic(c, groups[gi], min, max, box, g5);
            }
        }
        if (draft10 is null) Stronghold(c, groups, byGroup, g5, stronghold); else Transplant(c, draft10);
        ExtraPlatforms(c);
        RubbleProps(c);
        foreach (var (name, mn, mx, deck) in c.Bridges) Bridge6(c, name, mn, mx, deck);

        c.Flags.Add($"draft 6 growth (every placed object 1.5x its draft-5 size): {c.Moved} items moved ({c.Spread} of them to open a gap under {MinGap} m to a neighbour), {c.NarrowKept} kept a narrow gap they could not open, {c.NarrowDropped} {(c.Draft9 ? "pieces" : "small pieces (cover, rubble)")} left out rather than leave a gap under {MinGap} m, {c.LeftOut} left out{(c.LeftOut > 0 ? ": " + string.Join(", ", c.LeftOutNames.Take(12)) : "")}");
        c.Flags.Add($"draft 7 overlap rule (no two items meet deeper than {AshfallOverlap.Tolerance} m): {c.OverlapSettled} items moved or shrunk to clear an overlap, {c.OverlapDropped} left out{(c.OverlapDropped > 0 ? ": " + string.Join(", ", c.OverlapDroppedNames.Take(12)) : "")}");
        c.Flags.Add(roadwayRule
            ? $"draft 8 roadway rule (no item's collision within {AshfallRoadway.Margin} m of a roadway; bridges clear by {AshfallRoadway.Headroom} m): {c.RoadSettled} items moved or shrunk off a roadway, {c.RoadDropped} left out{(c.RoadDropped > 0 ? ": " + string.Join(", ", c.RoadDroppedNames.Take(12)) : "")}; {c.RoadPlatformsSkipped} added-platform candidates skipped"
            : "draft 8 roadway rule OFF (report aid)");
        if (c.Dropped.Count > 0)
            c.Flags.Add($"{c.Dropped.Count} structures dropped: they stood in a lane, a start area, a capture zone's 6 m ring, on a spawn marker or within 2 m of the boundary (the boundary-foot walls all); first: {string.Join(", ", c.Dropped.Take(6))}");
        if (draft9)
        {
            c.Flags.Add($"draft 9 stairs (found in the walkable models' meshes, a ramp over the nosings, the model's boxes cut under it, walked up and down on the item alone; draft 10: narrow flights widened, a corridor carved where the walk stalled): {c.StairsKept} kept ({c.StairsCarved} carved), {c.StairsDropped} dropped" + (c.StairNotes.Count > 0 ? ": " + string.Join("; ", c.StairNotes.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(80).Select(g => $"{g.Key} x{g.Count()}")) : ""));
            c.Flags.Add($"draft 10 stair access (no model with a flight that has no access point; the stronghold's pieces authored): {c.StairAccessRefused.Count} model choices refused ({string.Join(", ", c.StairAccessRefused.Select(r => r[(r.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]).GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}"))}), {c.StairAccessDropped} structures left out{(c.StairAccessDropped > 0 ? ": " + string.Join(", ", c.StairAccessDroppedNames.Take(12)) : "")}");
            c.Flags.Add($"draft 9 urban rules: {c.Buildings.Count} buildings face their nearest street; {c.Snapped} built pieces (cover, walls, facades) turned onto the street grid; {c.DebrisAlone} loose debris pieces left out (more than {DebrisReach:0} m from any building)");
            c.Flags.Add($"draft 9 placement (no drawn models meet deeper than {AshfallOverlap.Tolerance} m, nothing in a stair's foot or landing): {c.Draft9Settled} items moved or shrunk for it, {c.Draft9Dropped} left out{(c.Draft9Dropped > 0 ? ": " + string.Join(", ", c.Draft9DroppedNames.Take(12)) : "")}");
        }
        else
            c.Flags.Add($"walkable ramps (stairs and rubble runs found in the top heightfields of the walkable models): {c.RampsKept} kept as sloped wedges, {c.RampsDropped} dropped (scaled slope over {MaxRampDegrees:0} degrees, or since draft 6 phase 2 off the item's stepped tops or outside its box: {c.RampsMisfit})");
        // Review flags for models whose collision wants a manual clean-up.
        foreach (var (id, n) in c.Uses.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (AshfallKitCollision.NeedsCleanup(id)) c.Flags.Add($"{id} used {n}x: collision needs manual clean-up ({AshfallKitCollision.CleanupReason(id)})");
        foreach (var (id, n) in c.DroppedBoxes.OrderBy(k => k.Key, StringComparer.Ordinal))
            c.Flags.Add($"{id}: {n} collision boxes under {MinBox} m dropped as dust across its placements");

        if (field is not null) EnforceStairAccess(c, g5, field);   // draft 12: every stair reachable at both ends on the final collision

        var spawns = g5.Spawns.Select(s => s == g5.Points[0].Center ? points[0].Center : s).ToList();
        var level = new AshfallLevel(c.Parts, points, g5.Areas, spawns, AshfallMap.HashOf(c.Parts, field is null ? AshfallMap.Version : field.HashVersion)) { Layout = field is null ? AshfallLayout.Draft10 : field.Layout };
        var hidden = Enumerable.Range(groundCount, c.Parts.Count - groundCount).ToList();
        var dressing = new AshfallDressing(c.Items, hidden, AshfallMap.DressVersion, c.Structures) { ItemParts = c.ItemParts, Flags = c.Flags };
        return new AshfallDraft5(level, dressing, gb, c.Log);
    }

    static (Vector3 Min, Vector3 Max) Bounds(IReadOnlyList<AshfallPart> parts, List<int> idx)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (int i in idx) foreach (var v in parts[i].Corners) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max);
    }

    // ---------------------------------------------------------------- banned places (the checks' list)

    static void BuildBanned(Ctx c, AshfallLevel g5)
    {
        if (c.Field is { } f) { BuildFieldBanned(c, g5, f); return; }
        const float lane = 4.2f;
        foreach (float v in new[] { -92f, 92f, -232f, 232f })
        {
            c.BannedRects.Add(new(v - lane, -240f, v + lane, 240f));
            c.BannedRects.Add(new(-240f, v - lane, 240f, v + lane));
        }
        c.BannedRects.Add(new(-6f, -160f, 6f, 80f));
        c.BannedRects.Add(new(-80f, -6f, 80f, 6f));
        foreach (var a in g5.Areas) c.BannedRects.Add(new(a.Center.X - a.Size.X / 2f, a.Center.Y - a.Size.Y / 2f, a.Center.X + a.Size.X / 2f, a.Center.Y + a.Size.Y / 2f));
        foreach (var p in g5.Points) c.BannedDiscs.Add(new(p.Center.X, p.Center.Y, p.Radius + 6f));
        foreach (var s in g5.Spawns) c.BannedDiscs.Add(new(s.X, s.Y, 4f));
        // Draft 6: the stronghold's breaches (the leg's band, along the breach): nothing else grows into them.
        uint seed = 7000;
        foreach (var (_, alongX, t0, t1, lo, hi, _, _) in Legs)
            foreach (var (a, b) in LegLayout(lo, hi, seed += 101).Breaches)
                c.BreachRects.Add(alongX ? new(a, t0, b, t1) : new(t0, a, t1, b));
    }

    /// <summary>Whether a box breaks a banned place or stands within 2 m of the boundary's inner face.</summary>
    static bool Banned(Ctx c, WBox b)
    {
        var r = b.Rect;
        if (r.X < -238f || r.Y < -238f || r.Z > 238f || r.W > 238f) return true;
        if (b.Centre.Z - b.Half.Z >= 8f) return false;   // above reach (a bridge over an avenue, an upper storey): the places below stay clear
        foreach (var z in c.BannedRects) if (r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y) return true;
        foreach (var d in c.BannedDiscs)
        {
            float dx = MathF.Max(MathF.Max(r.X - d.X, d.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - d.Y, d.Y - r.W), 0f);
            if (dx * dx + dy * dy < d.Z * d.Z) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- pools

    // Draft 6 (the Meshy expansion, phase 2): the purpose-made models join the pools and are preferred where they fit (<see cref="Preferred"/>). AC-043 and AC-048 (traversal quarantined) stand only in the
    // backdrop pool (no collision); AC-041 (intact ends) is never a breach end; the boundary panels and the ground slabs are not Props (the slabs are surfaces, the boundary stays the client's flat wall).
    static readonly string[] Shell = ["AC-051", "AC-005", "AC-003", "AC-004", "AC-030"], Facade = ["AC-041", "AC-004", "AC-001", "AC-006", "AC-002"], Tower = ["AC-052", "AC-044", "AC-010", "AC-005", "AC-030"],
        Cover = ["AC-054", "AC-055", "AC-056", "AC-057", "AC-058", "AC-059", "AC-011", "AC-017", "AC-014", "AC-018", "AC-012"], HighCover = ["AC-060", "AC-061", "AC-018", "AC-014"],
        Landmark = ["AC-027", "AC-028", "AC-029", "AC-031", "AC-032", "AC-013"],
        RubbleLow = ["AC-020", "AC-024", "AC-025"], RubbleMid = ["AC-053", "AC-019", "AC-021", "AC-018"], RubbleTall = ["AC-053", "AC-023", "AC-021", "AC-019"],
        Platform = ["AC-050", "AC-008"];   // draft 9: AC-049 left out (its stair stops about 1.8 m under its deck: a stair that ends in its own wall)

    /// <summary>The shooting platforms: the first library's AC-008 and the expansion's low and medium firing platforms (bare edges: nothing is added to them).</summary>
    internal static readonly string[] PlatformIds = ["AC-008", "AC-049", "AC-050"];

    /// <summary>The bridges, in the order a span tries them: the expansion's stone bridge on arches (AC-045), then the first library's walkway span (AC-009).</summary>
    internal static readonly string[] BridgeIds = ["AC-045", "AC-009"];

    /// <summary>A v5 wall this low is a cover wall: the expansion's high cover (AC-061 broken wall, AC-060 buttress, 2.2 m at design) joins its pool (ours).</summary>
    const float LowWallHeight = 2.5f;

    static readonly string[] LowWall = ["AC-061", "AC-060", "AC-041", "AC-004", "AC-001", "AC-006", "AC-002"];

    /// <summary>The cover height over which a v5 cover is high cover (the expansion's 2.2 m pieces; ours: over the 1.6 m eye).</summary>
    const float HighCoverHeight = 1.6f;

    /// <summary>Objective A's dais (AC-076, visual only): its diameter and its squashed height (ours; draft 5's AC-037 dais was 16 m and 0.3 m).</summary>
    internal const float DaisSize = 16f, DaisHeight = 0.3f;

    /// <summary>
    /// Draft 10: the stronghold's pieces and the models they keep (the draft-10 level's picks before the stair-access rule, commit 82de356f): the seeded pick runs on a counter shared with every placement, so a
    /// building replaced elsewhere would otherwise change the fortress (a breach end then narrowed the courtyard route to 1.7 m). The owner keeps the stronghold as authored.
    /// </summary>
    static readonly Dictionary<string, string> StrongholdModels = new(StringComparer.Ordinal)
    {
        ["ring gate"] = "AC-042",
        ["ring corner south-west"] = "AC-003",
        ["ring corner south-east"] = "AC-003",
        ["ring corner north-west"] = "AC-003",
        ["ring corner north-east"] = "AC-003",
        ["ring south leg west wall 0 curtain"] = "AC-041",
        ["ring south leg west wall 0 end 2"] = "AC-005",
        ["ring south leg west wall 1 end"] = "AC-030",
        ["ring south leg west wall 1 curtain"] = "AC-041",
        ["ring south leg east wall 0 curtain"] = "AC-041",
        ["ring south leg east wall 0 end 2"] = "AC-005",
        ["ring south leg east wall 1 end"] = "AC-004",
        ["ring south leg east wall 1 curtain"] = "AC-041",
        ["ring west leg wall 0 curtain"] = "AC-041",
        ["ring west leg wall 0 end 2"] = "AC-004",
        ["ring west leg wall 1 end"] = "AC-005",
        ["ring west leg wall 1 curtain"] = "AC-041",
        ["ring west leg wall 1 end 2"] = "AC-006",
        ["ring west leg wall 2 end"] = "AC-030",
        ["ring west leg wall 2 curtain"] = "AC-041",
        ["ring north leg wall 0 curtain"] = "AC-041",
        ["ring north leg wall 0 end 2"] = "AC-006",
        ["ring north leg wall 1 end"] = "AC-006",
        ["ring north leg wall 1 curtain"] = "AC-041",
        ["ring north leg wall 1 end 2"] = "AC-004",
        ["ring north leg wall 2 end"] = "AC-006",
        ["ring north leg wall 2 curtain"] = "AC-041",
        ["ring north leg wall 2 end 2"] = "AC-006",
        ["ring north leg wall 3"] = "AC-001",
        ["ring north leg wall 4 end"] = "AC-030",
        ["ring north leg wall 4 curtain"] = "AC-041",
        ["ring east leg wall 0 curtain"] = "AC-041",
        ["ring east leg wall 0 end 2"] = "AC-006",
        ["ring east leg wall 1 end"] = "AC-006",
        ["ring east leg wall 1 curtain"] = "AC-041",
        ["ring east leg wall 1 end 2"] = "AC-004",
        ["ring east leg wall 2 end"] = "AC-004",
        ["ring east leg wall 2 curtain"] = "AC-041",
        ["keep"] = "AC-044",
        ["Keep tower"] = "AC-052",
        ["Stair tower SW"] = "AC-046",
        ["Stair tower SE"] = "AC-046",
        ["terrace 1"] = "AC-008",
        ["terrace 3"] = "AC-008",
        ["east outer wall 1"] = "AC-041",
    };

    /// <summary>
    /// Draft 10: models tried only in place of a model refused by the stair-access rule (never in the first pick, so the pick of every other structure stays as it was): the expansion's stair tower (AC-046, reachable
    /// stairs inside) for a tower.
    /// </summary>
    static string[] ReplacementsOf(string family) => family == "tower" ? ["AC-046"] : [];

    static string[] PoolOf(string family, string name, float height) => family switch
    {
        "shell" or "shell2" or "corner" => Shell,
        "facade" => Facade,
        "wall" => height <= LowWallHeight ? LowWall : Facade,
        "tower" => Tower,
        "spire" => ["AC-030", "AC-029", "AC-028"],   // draft 12: the city's landmark towers (no stairs: a wayfinding mass, drawn full height; a stair model there is capped to its walkable height or refused)
        "platform" => Platform,
        "cover" => height > HighCoverHeight ? HighCover : Cover,
        "wallrun" => ["AC-041", "AC-001", "AC-017", "AC-014"],
        "wallpiece" => ["AC-047", .. RubbleTall],   // a collapsed wall: the expansion's rubble ramp (AC-047, a walkable slope to a 3 m landing) where it fits
        "rubble" => height <= 0.4f ? RubbleLow : height <= 1.6f ? RubbleMid : RubbleTall,
        "monument" => ["AC-027", "AC-029", "AC-028"],
        "arch" => ["AC-002"],
        "obelisk" => ["AC-029", "AC-010"],
        "wreck" => ["AC-030", "AC-028", "AC-027"],
        "bunker" => ["AC-005", "AC-030", "AC-028"],
        "bridge" => BridgeIds,
        "backdrop" => ["AC-052", "AC-044", "AC-048", "AC-043", "AC-010", "AC-005", "AC-030"],
        _ => Landmark,
    };

    /// <summary>
    /// Draft 6, phase 2: a model of the Meshy expansion (purpose-made for these families) is preferred where it fits. When the best expansion model's score is within <see cref="PreferSlack"/> times the best
    /// score plus <see cref="PreferMargin"/>, only expansion models are considered (ours: the owner's "ensure that the new ashfall models are utilized").
    /// </summary>
    static bool Preferred(string id) => AshfallKit.Get(id).SourceId.Length > 0;

    const float PreferSlack = 2f, PreferMargin = 0.6f;

    /// <summary>A preferred model must also fill the structure's bounds sensibly: its drawn volume between these shares of the bounds' volume (ours; the mismatch flag is outside 0.35..3).</summary>
    static readonly (float Min, float Max) PreferMass = (0.4f, 2.5f);

    // ---------------------------------------------------------------- generic structures

    static void Generic(Ctx c, AshfallGroup g, Vector3 min, Vector3 max, AshfallBox? box, AshfallLevel g5)
    {
        if (g.Family == "plinth") { c.Log.Add(new(g.Name, g.Family, "AC-037", c.Items.Count - 1, "replaced: control point on the ground, dais tile")); return; }
        if (g.Family == "obelisk" && c.Field is null)
        {
            // A's obelisk would stand inside the capture zone's 6 m ring: moved 30 m west, 6 m south.
            box = null;
            float tall = max.Z - min.Z;
            min = new Vector3(min.X - 30f, min.Y - 6f, AshfallMap.BlockTop); max = new Vector3(max.X - 30f, max.Y - 6f, AshfallMap.BlockTop + tall);
            c.Flags.Add("A's obelisk moved 30 m west and 6 m south (it stood inside the capture zone's ring)");
        }
        if (c.Draft9 && g.Family is "rubble" or "wallpiece" && !NearBuilding(c, min, max))
        {
            c.Log.Add(new(g.Name, g.Family, "", -1, $"dropped: urban, loose debris more than {DebrisReach:0} m from any building (draft 9: debris lies at the foot of the ruins it fell from)"));
            c.DebrisAlone++;
            return;
        }
        if (c.Draft9 && box is { } sb && g.Family is "cover" or "wall" or "wallrun" or "facade" && sb.YawStep % 6 != 0)
        {
            // draft 9: built pieces follow the street grid (quarter turns); the long side keeps its length
            int snapped = (int)(MathF.Round(sb.YawStep / 6f) * 6f) % 24;
            box = sb with { YawStep = snapped };
            c.Snapped++;
        }
        var spec = new Spec(g.Name, g.Family, min, max, box, PoolOf(g.Family, g.Name, max.Z - min.Z))
        {
            KeepWalkCell = g.Family == "platform",   // phase 2: a shooting platform's stair stays climbable (its riser cell under the step) after the growth
            NoCollision = g.Family == "backdrop",
            UseNet = g.Family is not ("wallrun" or "monument" or "arch"),   // the compound's walls, the monument and the arch were hand-placed on the avenues and around the start area
            Kind = g.Family is "rubble" or "wallpiece" ? AshfallKind.Rubble : null,
            Out = c.Draft9 && IsBuildingFamily(g.Family, max.Z - min.Z) ? (c.Field is { } fp ? FieldStreetSide(fp, new Vector2((min.X + max.X) / 2f, (min.Y + max.Y) / 2f)) : StreetSide(new Vector2((min.X + max.X) / 2f, (min.Y + max.Y) / 2f))) : null,
        };
        if (g.Family == "backdrop") spec.GrowAlong = spec.GrowAcross = 0f;
        Place(c, spec);
    }

    // ---------------------------------------------------------------- the placer

    sealed record Cand(string Id, int Yaw, Vector3 Size, float Score, float Mass, bool Risers);

    static float Dev(float a, float b) => a > b ? a / b - 1f : b / a - 1f;

    static int Place(Ctx c, Spec s, int onlyYaw = -1)
    {
        float h = MathF.Max(s.Max.Z - s.Min.Z, 0.05f);
        Vector2 centre = s.Box is { } bx ? new(bx.Cx, bx.Cy) : new((s.Min.X + s.Max.X) / 2f, (s.Min.Y + s.Max.Y) / 2f);
        float wx = s.Max.X - s.Min.X, wy = s.Max.Y - s.Min.Y;
        int[] yaws = onlyYaw >= 0 ? [onlyYaw] : s.Yaws ?? (s.Box is { } b2 ? [b2.YawStep, (b2.YawStep + 12) % 24] : [0, 6, 12, 18]);
        var cands = new List<Cand>();
        var extras = c.Draft9 ? ReplacementsOf(s.Family).Where(id => !s.Pool.Contains(id)).ToArray() : [];
        foreach (var id in s.Pool.Concat(extras))
        {
            var m = AshfallKit.Get(id);
            var r = m.RawSize;
            foreach (int yaw in yaws)
            {
                float tx, ty;
                if (s.Box is { } bb) { tx = bb.Length; ty = bb.Depth; }
                else if (yaw % 12 == 0) { tx = wx; ty = wy; }
                else { tx = wy; ty = wx; }
                float kx = tx / r.X, kh = h / r.Y, kd = ty / r.Z;
                float sc = MathF.Sqrt(kx * kh);
                float Cl(float k) => MathF.Min(MathF.Max(k, 0.8f * sc), 1.25f * sc);
                float lx = MathF.Min(Cl(kx) * r.X, tx + 2f * s.GrowAlong), ly = MathF.Min(Cl(kd) * r.Z, ty + 2f * s.GrowAcross), hh = MathF.Min(Cl(kh) * r.Y, h);
                bool risers = false;
                if (Walkable.Contains(id))
                {
                    float cell = AshfallKitCollision.Metrics(id).CellSize;
                    float limit = StepHeight / cell * r.Y;   // tallest the model may be drawn before one cell exceeds the step
                    if (hh > limit) { hh = limit; risers = hh < 0.8f * h; }
                }
                float score = Dev(lx, tx) + Dev(hh, h) + 0.7f * Dev(ly, ty) + (risers ? 2f : 0f);
                cands.Add(new Cand(id, yaw, new Vector3(lx, ly, hh), score, lx * ly * hh / (tx * ty * h), risers));
            }
        }
        // Best model first; models within 15 % of the best form the pool the hash picks from; a model already placed within 12 m is avoided.
        var perModel = cands.Where(k => !extras.Contains(k.Id)).GroupBy(k => k.Id).Select(grp => grp.OrderBy(k => k.Score).ThenBy(k => k.Yaw).First()).OrderBy(k => k.Score).ThenBy(k => k.Id, StringComparer.Ordinal).ToList();
        // Draft 6: the expansion's models first where they fit (the rest stay as the fallback below).
        var preferred = perModel.Where(k => Preferred(k.Id) && k.Mass >= PreferMass.Min && k.Mass <= PreferMass.Max).ToList();
        if (preferred.Count > 0 && preferred.Count < perModel.Count && preferred[0].Score <= perModel[0].Score * PreferSlack + PreferMargin)
            perModel = [.. preferred, .. perModel.Where(k => !preferred.Contains(k))];
        float best = perModel[0].Score;
        bool preferring = preferred.Count > 0 && perModel[0] == preferred[0];
        var band = perModel.Where(k => k.Score <= best * 1.15f + 0.03f && (!preferring || preferred.Contains(k))).ToList();
        var fresh = band.Where(k => !c.Placed.Any(p => p.Id == k.Id && Vector2.Distance(p.At, centre) < 12f)).ToList();
        if (fresh.Count > 0) band = fresh;
        uint pick = AshfallBuilder.Mix(c.Seed, c.Counter++);
        var chosen = band[(int)(pick % (uint)band.Count)];
        // draft 10: the authored stronghold keeps the models it had (StrongholdModels), so a change elsewhere (a building replaced, a debris piece more or less) never changes the fortress
        if (c.Draft9 && StrongholdModels.TryGetValue(s.Group, out var frozen) && perModel.FirstOrDefault(k => k.Id == frozen) is { } fk) chosen = fk;
        // Among the model's turns the score decides; ties go to the front facing away from the map's middle.
        var outDir = s.Out ?? (centre.LengthSquared() > 1f ? Vector2.Normalize(centre) : Vector2.UnitY);
        Cand TurnOf(string id)
        {
            var turns = cands.Where(k => k.Id == id).OrderBy(k => k.Score).ThenBy(k => k.Yaw).ToList();
            var pickTurn = turns[0];
            foreach (var t in turns.Skip(1))
            {
                if (t.Score > turns[0].Score + 0.02f) break;
                var (sn, cs) = AshfallBuilder.Trig(t.Yaw);
                var (sn0, cs0) = AshfallBuilder.Trig(pickTurn.Yaw);
                // the front faces local -Y turned by the yaw: (sin, -cos)
                if (Vector2.Dot(new Vector2(sn, -cs), outDir) > Vector2.Dot(new Vector2(sn0, -cs0), outDir) + 1e-4f) pickTurn = t;
            }
            return pickTurn;
        }
        // Draft 6: when the chosen model cannot grow 1.5x where it stands, the pool's other models are tried in their score order (the new models gave "Twin tower east" a second chance); the last one decides.
        var order = new List<string> { chosen.Id };
        order.AddRange(perModel.Select(k => k.Id).Where(id => id != chosen.Id));
        bool replacing = false;
        for (int k = 0; k < order.Count; k++)
        {
            // a new model that cannot open every narrow gap gives way to the next model; a first-library model keeps its fewest-gaps place as in draft 6
            // draft 9: no piece keeps a gap under 2.4 m to a neighbour (draft 6 let the last model or a first-library one keep its fewest-gaps place; a route then squeezed past a moved platform)
            int refused = c.StairAccessRefused.Count;
            int r = Commit(c, s, centre, TurnOf(order[k]), order[k], h, final: k == order.Count - 1, keepNarrow: !c.Draft9 && (k == order.Count - 1 || !Preferred(order[k])), needStairs: replacing && AshfallKitCollision.Stairs(order[k]).Count > 0 && k < order.Count - 1);
            if (r != Retry) return r;
            if (!replacing && c.StairAccessRefused.Count > refused)
            {
                // draft 10: a model refused for its stairs is replaced first by the pool's models with stairs (in their score order; each must keep a walkable flight where it stands), then by the rest
                replacing = true;
                var rest = order.Skip(k + 1).Concat(extras.Where(id => !order.Contains(id)).OrderBy(id => cands.Where(x => x.Id == id).Min(x => x.Score)).ThenBy(id => id, StringComparer.Ordinal)).ToList();
                order = [.. order.Take(k + 1), .. rest.Where(id => AshfallKitCollision.Stairs(id).Count > 0), .. rest.Where(id => AshfallKitCollision.Stairs(id).Count == 0)];
            }
        }
        return -1;
    }

    /// <summary><see cref="Commit"/>'s answer when the model could not grow 1.5x where it stands and another model of the pool may still fit.</summary>
    const int Retry = -2;

    static int Commit(Ctx c, Spec s, Vector2 centre, Cand cand, string id, float h, bool final = true, bool keepNarrow = true, bool needStairs = false)
    {
        var size = cand.Size;
        float bottom = s.Min.Z;
        AshfallDressItem Item(Vector3 sz) => new(id, new Vector3(centre.X, centre.Y, bottom), cand.Yaw * (MathF.PI / 12f), sz, false, AshfallDressRole.Prop, -1);
        var item = Item(size);
        var boxes = s.NoCollision ? [] : CoarseBoxes(s, item, cand.Yaw, id);
        string outcome = "placed";
        string? shrunk = null;
        if (s.UseNet && !s.NoCollision)
        {
            foreach (float f in new[] { 1f, 0.85f, 0.7f })
            {
                size = new Vector3(cand.Size.X * f, cand.Size.Y * f, cand.Size.Z);
                item = Item(size);
                boxes = CoarseBoxes(s, item, cand.Yaw, id);
                if (!boxes.Any(b => Banned(c, b))) { if (f < 1f) { outcome = "placed, shrunk " + f; shrunk = $"{s.Group}: {id} shrunk to {f:0.##} of its size to stay out of a banned place"; } break; }
                if (f == 0.7f) { c.Log.Add(new(s.Group, s.Family, "", -1, "dropped: inside a banned place (lane, start area, capture zone ring, spawn or the boundary's 2 m)")); c.Dropped.Add(s.Group); return -1; }
            }
        }
        // Draft 6: every placed object 1.5x its draft-5 size, moved back from a protected place or left out.
        float d5Height = size.Z;
        if (!Grow6(c, s, id, cand.Yaw, bottom, boxes, ref centre, ref size, out float moved, keepNarrow))
        {
            if (!final) return Retry;
            if (c.NarrowDrop)
            {
                c.Log.Add(new(s.Group, s.Family, "", -1, $"dropped: narrow gap, grown 1.5x this {(c.Draft9 ? "piece (draft 9: no piece keeps one)" : "small piece")} leaves a gap under 2.4 m to a neighbour that no move within its reach opens or closes"));
                c.NarrowDropped++;
                return -1;
            }
            c.Log.Add(new(s.Group, s.Family, "", -1, "dropped: grown 1.5x it stands in a protected place (lane, start area, capture zone ring, spawn, the boundary's 2 m or a breach) and moving it back did not clear it"));
            c.LeftOut++; c.LeftOutNames.Add(s.Group);
            return -1;
        }
        // Draft 8: the grown item stands in no roadway (moved off it, shrunk, the pool's next model, else left out).
        if (!Unroad(c, s, id, cand.Yaw, bottom, ref centre, ref size, out string roadNote))
        {
            if (!final) return Retry;
            c.Log.Add(new(s.Group, s.Family, "", -1, "dropped: roadway, grown 1.5x it stands in a roadway and no move or smaller footprint within reach clears it (" + roadNote + ")"));
            c.RoadDropped++; c.RoadDroppedNames.Add(s.Group);
            return -1;
        }
        if (roadNote.Contains("(roadway)")) outcome += ", " + roadNote.Trim();
        // Draft 7: the grown item meets no placed item deeper than the overlap tolerance (moved, shrunk, the pool's next model, else left out).
        string overlapNote = "";
        if (c.OverlapRule && !Unoverlap(c, s, id, cand.Yaw, bottom, ref centre, ref size, out overlapNote))
        {
            if (!final) return Retry;
            c.Log.Add(new(s.Group, s.Family, "", -1, "dropped: overlap, grown 1.5x it meets a placed item deeper than " + AshfallOverlap.Tolerance + " m and no move or smaller footprint within reach clears it (" + overlapNote + ")"));
            if (overlapNote.Contains("(draft 9:") || overlapNote.Contains("drawn models") || overlapNote.Contains("foot or landing")) { c.Draft9Dropped++; c.Draft9DroppedNames.Add(s.Group); }
            else { c.OverlapDropped++; c.OverlapDroppedNames.Add(s.Group); }
            return -1;
        }
        // Draft 10 (owner, 2026-11-09: "don't use asset buildings that have stairs with no access point. replace them with properly scaled ones that do."): a model with a visible flight that cannot be
        // reached or walked where it stands gives way to the pool's next model (scaled by the same fit); the last one left out. The stronghold's pieces are authored (the owner keeps the keep as it is).
        if (c.Draft9 && !s.NoCollision && !s.Family.StartsWith("stronghold") && s.Family is not ("terrace" or "outerwall") && (c.Field is not null || s.Family is not ("rubble" or "wallpiece" or "cover")) && AshfallKitCollision.Stairs(id).Count > 0)   // draft 12: on the planned layouts a walkable rubble mound's flights count too
        {
            var at = Item(size);
            var plan = Stairs(at, cand.Yaw, id, CoarseBoxes(s, at, cand.Yaw, id));
            var dead = plan.Notes.Where(NoAccess).ToList();
            dead.AddRange(Unreached(plan, at.Position.Z));   // a kept flight that neither the ground nor another reached flight leads to (a roof stair)
            if (dead.Count == 0 && needStairs && plan.Kept == 0) return Retry;   // a replacement with stairs keeps a walkable flight, else the next model
            if (dead.Count > 0)
            {
                c.StairAccessRefused.Add($"{s.Group}: {id}");
                if (!final) return Retry;
                c.Log.Add(new(s.Group, s.Family, "", -1, $"dropped: stair access (draft 10), {id} has {dead.Count} flight(s) with no access point here ({dead[0]}) and no model of its pool without one fits"));
                c.StairAccessDropped++; c.StairAccessDroppedNames.Add(s.Group);
                return -1;
            }
        }
        if (overlapNote.Length > 0) { outcome += ", " + overlapNote.Trim(); if (overlapNote.Contains("(draft 9)")) c.Draft9Settled++; else c.OverlapSettled++; }
        if (roadNote.Contains("(roadway)")) c.RoadSettled++;   // counted once the item is kept (a retry with the next model is not)
        if (shrunk is not null) c.Flags.Add(shrunk);
        item = Item(size);
        boxes = s.NoCollision ? [] : CoarseBoxes(s, item, cand.Yaw, id);
        if (moved > 0.01f) { outcome += $", moved {moved:0.00} m"; c.Moved++; }
        if (Walkable.Contains(id) && cand.Risers) c.Flags.Add($"{s.Group}: {id} drawn lower than the structure ({d5Height:0.#} m of {h:0.#} m) to keep risers under the step height (draft 5; draft 6 grows it to {size.Z:0.#} m)");
        if (cand.Mass < 0.35f || cand.Mass > 3f) c.Flags.Add($"{s.Group} ({s.Family}): {id} mass {cand.Mass:0.##} of the structure's bounds (mismatch)");
        else if (cand.Score > 3f) c.Flags.Add($"{s.Group} ({s.Family}): {id} is the closest fit, off by {cand.Score:0.##} in total");

        if (c.StairAccessRefused.Any(r => r.StartsWith(s.Group + ": ", StringComparison.Ordinal))) outcome += ", draft 10: in place of a model with stairs without access (" + string.Join(", ", c.StairAccessRefused.Where(r => r.StartsWith(s.Group + ": ", StringComparison.Ordinal)).Select(r => r[(s.Group.Length + 2)..]).Distinct()) + ")";
        return Emit(c, s, item, cand.Yaw, id, boxes, outcome, centre);
    }

    /// <summary>Adds a placed item: its collision parts (dust dropped), its ramps, its parts range, its structure and the log line.</summary>
    static int Emit(Ctx c, Spec s, AshfallDressItem item, int yaw, string id, List<WBox> boxes, string outcome, Vector2 centre)
    {
        int index = c.Items.Count;
        int first = c.Parts.Count;
        c.Items.Add(item);
        c.Uses[id] = c.Uses.GetValueOrDefault(id) + 1;
        c.Placed.Add((id, centre));
        int dust = 0;
        var ids = new List<int>();
        StairPlan? stairs = null;
        if (c.Draft9 && !s.NoCollision && AshfallKitCollision.Stairs(id).Count > 0)
        {
            stairs = Stairs(item, yaw, id, boxes);
            boxes = stairs.Boxes;
            c.StairsKept += stairs.Kept; c.StairsDropped += stairs.Dropped; c.StairsCarved += stairs.Carved;
            foreach (var n in stairs.Notes) c.StairNotes.Add($"{id} {n}");
            if (stairs.Kept + stairs.Notes.Count > 0) outcome += $", stairs: {stairs.Kept} kept" + (stairs.Notes.Count > 0 ? $", {string.Join("; ", stairs.Notes)}" : "");
        }
        foreach (var b in boxes)
        {
            var e = b.Half * 2f;
            if (e.X < MinBox && e.Y < MinBox && e.Z < MinBox) { dust++; continue; }
            AddPart(c, b, item.Position.Z, s.Kind);
            c.AllBoxes.Add((index, b));
            AddLow(c, index, b);
            if (!BridgeIds.Contains(id)) AddSolid(c, index, b);
        }
        if (dust > 0) c.DroppedBoxes[id] = c.DroppedBoxes.GetValueOrDefault(id) + dust;
        if (stairs is not null)
            foreach (var rc in stairs.Ramps)
            {
                c.Parts.Add(new AshfallPart(AshfallKind.Stair, rc));
                // draft 9: the ramp's box (it may reach past the model where its foot is carried to the floor) counts for the narrow-gap and overlap rules of the items after it
                var rb = RampBox(item, yaw, rc);
                c.AllBoxes.Add((index, rb));
                AddLow(c, index, rb);
                AddSolid(c, index, rb);
            }
        if (c.Draft9) AddDraft9(c, s, index, item, yaw, id, stairs?.Zones ?? []);
        if (!s.NoCollision && !c.Draft9)
            foreach (var rp in AshfallKitCollision.Ramps(id))
            {
                var corners = RampCorners(item, yaw, rp, out bool ok);
                if (ok && !RampMeetsItsTops(item, boxes, corners)) { ok = false; c.RampsMisfit++; }
                if (ok) { c.Parts.Add(new AshfallPart(AshfallKind.Stair, corners)); c.RampsKept++; } else c.RampsDropped++;
            }
        c.ItemParts.Add((first, c.Parts.Count - first));
        var parts = Enumerable.Range(first, c.Parts.Count - first).ToList();
        c.Structures.Add(new AshfallStructure(parts, s.Min, s.Max));
        c.Log.Add(new(s.Group, s.Family, id, index, outcome));
        return index;
    }

    // ---------------------------------------------------------------- collision transform

    /// <summary>
    /// The item's collision boxes as the level pass keeps them: rubble, cover and collapsed-wall models with through-openings get the opening-aware merge (<see cref="AshfallOpeningMerge"/>, the openings pass of 2026-11-06),
    /// the other models of those families the six slabs, everything else its fitted boxes.
    /// </summary>
    static List<WBox> CoarseBoxes(Spec s, AshfallDressItem item, int yawStep, string id)
    {
        if (s.Family is ("rubble" or "wallpiece" or "cover") && AshfallKitCollision.Openings(id).Count > 0) return Boxes(item, yawStep, id, AshfallOpeningMerge.Reduced(id));
        return Coarse(s, Boxes(item, yawStep, id));
    }

    /// <summary>Rubble piles and covers: the fitted boxes are merged into at most <see cref="Slabs"/> boxes (the union of the boxes whose centres fall in the same slab along the item's long axis), which keeps the silhouette along the run and stays near the 69 k triangles of v5.</summary>
    const int Slabs = 6;

    static List<WBox> Coarse(Spec s, List<WBox> boxes)
    {
        int slabs = s.Family == "rubble prop" ? 3 : Slabs;   // draft 9's rubble props: three slabs a pile (the triangle budget)
        if (s.Family is not ("rubble" or "wallpiece" or "cover" or "rubble prop") || boxes.Count <= slabs) return boxes;
        // Draft 6, phase 2: a walkable rubble model (AC-053) is merged too (the triangle budget); its ramp wedges are kept only where they still meet the slabs' tops (RampMeetsItsTops).
        var (sn, co) = AshfallBuilder.Trig(boxes[0].YawStep);
        bool alongX = true;
        float lo = float.MaxValue, hi = float.MinValue, lo2 = float.MaxValue, hi2 = float.MinValue;
        foreach (var b in boxes)
        {
            float u = b.Centre.X * co + b.Centre.Y * sn, v = -b.Centre.X * sn + b.Centre.Y * co;
            lo = MathF.Min(lo, u - b.Half.X); hi = MathF.Max(hi, u + b.Half.X); lo2 = MathF.Min(lo2, v - b.Half.Y); hi2 = MathF.Max(hi2, v + b.Half.Y);
        }
        alongX = hi - lo >= hi2 - lo2;
        if (!alongX) { lo = lo2; hi = hi2; }
        var mn = new Vector3[slabs]; var mx = new Vector3[slabs]; var used = new bool[slabs];
        for (int i = 0; i < slabs; i++) { mn[i] = new Vector3(float.MaxValue); mx[i] = new Vector3(float.MinValue); }
        foreach (var b in boxes)
        {
            float u = b.Centre.X * co + b.Centre.Y * sn, v = -b.Centre.X * sn + b.Centre.Y * co, a = alongX ? u : v;
            int k = Math.Clamp((int)((a - lo) / MathF.Max(hi - lo, 1e-3f) * slabs), 0, slabs - 1);
            used[k] = true;
            // corners in the turned frame (u, v, z)
            mn[k] = Vector3.Min(mn[k], new Vector3(u - b.Half.X, v - b.Half.Y, b.Centre.Z - b.Half.Z));
            mx[k] = Vector3.Max(mx[k], new Vector3(u + b.Half.X, v + b.Half.Y, b.Centre.Z + b.Half.Z));
        }
        var list = new List<WBox>();
        for (int k = 0; k < slabs; k++)
        {
            if (!used[k]) continue;
            var c3 = (mn[k] + mx[k]) / 2f; var h3 = (mx[k] - mn[k]) / 2f;
            list.Add(new WBox(new Vector3(c3.X * co - c3.Y * sn, c3.X * sn + c3.Y * co, c3.Z), h3, boxes[0].YawStep));
        }
        return list;
    }

    /// <summary>
    /// The item's collision boxes in the simulation frame. The mapping is the item contract (<see cref="AshfallDressItem"/>) with SwapXZ false: the model's raw bounds are fitted into the item's box, glTF X is local X, glTF -Z is local Y,
    /// glTF Y is up; the local frame is turned by the yaw about the box's centre.
    /// </summary>
    static List<WBox> Boxes(AshfallDressItem item, int yawStep, string id) => Boxes(item, yawStep, id, AshfallKitCollision.Boxes(id));

    static List<WBox> Boxes(AshfallDressItem item, int yawStep, string id, IReadOnlyList<(Vector3 Min, Vector3 Max)> source)
    {
        var m = AshfallKit.Get(id);
        var r = m.RawSize;
        var list = new List<WBox>();
        foreach (var (mn, mx) in source)
        {
            var u0 = (mn - m.RawMin) / r; var u1 = (mx - m.RawMin) / r;
            float lx0 = (u0.X - 0.5f) * item.Size.X, lx1 = (u1.X - 0.5f) * item.Size.X;
            float ly0 = (0.5f - u1.Z) * item.Size.Y, ly1 = (0.5f - u0.Z) * item.Size.Y;
            float z0 = item.Position.Z + u0.Y * item.Size.Z, z1 = item.Position.Z + u1.Y * item.Size.Z;
            list.Add(Local(item.Position, yawStep, (lx0 + lx1) / 2f, (ly0 + ly1) / 2f, z0, z1, (lx1 - lx0) / 2f, (ly1 - ly0) / 2f));
        }
        return list;
    }

    /// <summary>A box given in an item's local frame (offset from the item's centre, half sizes) as a world box.</summary>
    static WBox Local(Vector3 itemPos, int yawStep, float cx, float cy, float z0, float z1, float hx, float hy)
    {
        var (s, co) = AshfallBuilder.Trig(yawStep);
        float wx = itemPos.X + co * cx - s * cy, wy = itemPos.Y + s * cx + co * cy;
        return new WBox(new Vector3(wx, wy, (z0 + z1) / 2f), new Vector3(hx, hy, (z1 - z0) / 2f), yawStep);
    }

    /// <summary>Largest slope a placed ramp may keep after the scale (the sim walks 60 degrees; ours, a margin).</summary>
    const float MaxRampDegrees = 55f;

    /// <summary>
    /// A model's ramp (a wedge: flat bottom, a plane for a top, raw frame) moved by the item's transform. <paramref name="ok"/> is false when the scaled slope of its top is over <see cref="MaxRampDegrees"/>
    /// (non-uniform scale changes slopes) or the top does not rise.
    /// </summary>
    static Vector3[] RampCorners(AshfallDressItem item, int yawStep, AshfallRamp r, out bool ok)
    {
        var m = AshfallKit.Get(item.Asset);
        var (sn, co) = AshfallBuilder.Trig(yawStep);
        Vector3 W(float x, float y, float z)
        {
            var u = (new Vector3(x, y, z) - m.RawMin) / m.RawSize;
            float lx = (u.X - 0.5f) * item.Size.X, ly = (0.5f - u.Z) * item.Size.Y;
            return new Vector3(item.Position.X + co * lx - sn * ly, item.Position.Y + sn * lx + co * ly, item.Position.Z + u.Y * item.Size.Z);
        }
        float TopAt(float x, float z)
        {
            float t = r.Dir switch { 0 => (x - r.Min.X) / (r.Max.X - r.Min.X), 1 => 1f - (x - r.Min.X) / (r.Max.X - r.Min.X), 2 => (z - r.Min.Z) / (r.Max.Z - r.Min.Z), _ => 1f - (z - r.Min.Z) / (r.Max.Z - r.Min.Z) };
            return r.YLow + t * (r.YHigh - r.YLow);
        }
        float mx = (r.Min.X + r.Max.X) / 2f, mz = (r.Min.Z + r.Max.Z) / 2f;
        var lowMid = r.Dir switch { 0 => W(r.Min.X, r.YLow, mz), 1 => W(r.Max.X, r.YLow, mz), 2 => W(mx, r.YLow, r.Min.Z), _ => W(mx, r.YLow, r.Max.Z) };
        var highMid = r.Dir switch { 0 => W(r.Max.X, r.YHigh, mz), 1 => W(r.Min.X, r.YHigh, mz), 2 => W(mx, r.YHigh, r.Max.Z), _ => W(mx, r.YHigh, r.Min.Z) };
        float run = MathF.Sqrt((highMid.X - lowMid.X) * (highMid.X - lowMid.X) + (highMid.Y - lowMid.Y) * (highMid.Y - lowMid.Y)), rise = highMid.Z - lowMid.Z;
        ok = rise > 1e-3f && run > 1e-3f && rise <= run * 1.4281480f;   // tan 55 degrees
        if (!ok) return [];
        // raw vertices in the order Frame uses (x, y up, z right-handed): bottom (-,-,-) (+,-,-) (+,+,-) (-,+,-)... with y the "up" axis
        float[] xs = [r.Min.X, r.Max.X], zs = [r.Min.Z, r.Max.Z];
        Vector3 V(int sx, int sy, int sz)
        {
            float x = xs[sx], z = zs[sz];
            return W(x, sy == 0 ? r.Min.Y : TopAt(x, z), z);
        }
        Vector3[] p = [V(0, 0, 0), V(1, 0, 0), V(1, 1, 0), V(0, 1, 0), V(0, 0, 1), V(1, 0, 1), V(1, 1, 1), V(0, 1, 1)];
        int[][] faces = [[3, 2, 1, 0], [4, 5, 6, 7], [0, 1, 5, 4], [2, 3, 7, 6], [3, 0, 4, 7], [1, 2, 6, 5]];
        var corners = new Vector3[36];
        for (int f = 0; f < 6; f++)
        {
            int[] q = faces[f];
            ReadOnlySpan<int> order = [q[0], q[1], q[2], q[0], q[2], q[3]];
            for (int k = 0; k < 6; k++) corners[f * 6 + k] = p[order[k]];
        }
        return corners;
    }

    /// <summary>
    /// Draft 6, phase 2: a ramp wedge is kept only when it lies inside its item's box and its top plane follows the item's stepped box tops: the median distance of samples of its top face to the highest box top
    /// under them within half the largest riser there or 1.5 scaled cells (the check of <c>AshfallDraft5Tests</c>). A heavily stretched walkable model (the keep, AC-044) can leave a detected ramp off its steps.
    /// </summary>
    static bool RampMeetsItsTops(AshfallDressItem item, List<WBox> boxes, Vector3[] corners)
    {
        float s = MathF.Sin(item.Yaw), co = MathF.Cos(item.Yaw);
        Vector3 Local(Vector3 v) { float dx = v.X - item.Position.X, dy = v.Y - item.Position.Y; return new Vector3(co * dx + s * dy, -s * dx + co * dy, v.Z); }
        const float eps = 0.02f;
        foreach (var v in corners)
        {
            var l = Local(v);
            if (MathF.Abs(l.X) > item.Size.X / 2f + eps || MathF.Abs(l.Y) > item.Size.Y / 2f + eps || l.Z < item.Position.Z - eps || l.Z > item.Position.Z + item.Size.Z + eps) return false;
        }
        var local = boxes.Select(b => { var cl = Local(b.Centre); return (Min: cl - b.Half, Max: cl + b.Half); }).ToList();
        float cellY = AshfallKitCollision.Metrics(item.Asset).CellSize * item.Size.Z / AshfallKit.Get(item.Asset).RawSize.Y;
        var pts = new List<Vector3>();
        for (int t = 0; t < 12; t++)
        {
            Vector3 a = Local(corners[t * 3]), b = Local(corners[t * 3 + 1]), cc = Local(corners[t * 3 + 2]);
            if (Vector3.Cross(b - a, cc - a).Z <= 1e-3f) continue;
            var cen = (a + b + cc) / 3f; pts.Add(cen);
            foreach (var v in new[] { a, b, cc }) pts.Add(cen + (v - cen) * 0.5f);
        }
        var tops = new List<float>(); var diffs = new List<float>();
        foreach (var q in pts)
        {
            float top = float.MinValue;
            foreach (var bx in local) if (q.X >= bx.Min.X - 0.01f && q.X <= bx.Max.X + 0.01f && q.Y >= bx.Min.Y - 0.01f && q.Y <= bx.Max.Y + 0.01f) top = MathF.Max(top, bx.Max.Z);
            if (top == float.MinValue) continue;
            tops.Add(top); diffs.Add(MathF.Abs(q.Z - top));
        }
        if (diffs.Count == 0) return true;
        var distinct = new List<float>();
        foreach (float t in tops.OrderBy(t => t)) if (distinct.Count == 0 || t - distinct[^1] > 0.05f) distinct.Add(t);
        float riser = 0f;
        for (int k = 1; k < distinct.Count; k++) riser = MathF.Max(riser, distinct[k] - distinct[k - 1]);
        float tol = MathF.Max(riser / 2f, 1.5f * cellY) + 0.05f;
        diffs.Sort();
        return diffs[diffs.Count / 2] <= tol - 0.01f;
    }

    static void AddPart(Ctx c, WBox b, float itemBottom, AshfallKind? forced)
    {
        var (s, co) = AshfallBuilder.Trig(b.YawStep);
        float dx = b.Half.X * 2f, dy = b.Half.Y * 2f, dz = b.Half.Z * 2f;
        float top = b.Centre.Z + b.Half.Z - itemBottom, bottom = b.Centre.Z - b.Half.Z - itemBottom;
        var kind = forced ?? (top <= 1.3f ? AshfallKind.Cover : dz <= 0.6f && dx * dy >= 4f && bottom > 0.5f ? AshfallKind.Floor : AshfallKind.Wall);
        var corners = AshfallBuilder.Frame(b.Centre, new Vector3(co, s, 0f), new Vector3(-s, co, 0f), Vector3.UnitZ, b.Half.X, b.Half.Y, b.Half.Z);
        c.Parts.Add(new AshfallPart(kind, corners));
    }

    // ---------------------------------------------------------------- the stronghold

    static void Stronghold(Ctx c, IReadOnlyList<AshfallGroup> groups, List<int>[] byGroup, AshfallLevel g5, List<int> ids)
    {
        (Vector3 Min, Vector3 Max) B(string name)
        {
            int gi = ids.First(i => groups[i].Name == name);
            return Bounds(g5.Parts, byGroup[gi]);
        }

        // The gate: x -12..12 in the south leg; draft 6: the expansion's gatehouse (AC-042: two broken towers, a wide open passage, a stair to a mid deck; its tower roofs are never critical).
        {
            var s = new Spec("ring gate", "stronghold-gate", new(-12f, 112f, 0f), new(12f, 116f, 12f), null, ["AC-042"]) { Yaws = [0], GrowAlong = 0f, UseNet = false, KeepLength = true, KeepWalkCell = true };
            Place(c, s);
            c.Flags.Add("the gatehouse (AC-042) is drawn over the gate; its collision is the fitted passage (see the gate width in the report); its tower roofs are not proven reachable (02: roof access unverified) and nothing depends on them");
        }
        // The four corners: AC-003, the L's mass turned toward the ring's outer corner.
        foreach (var (name, x, y, h) in new[] { ("ring corner south-west", -72f, 112f, 12f), ("ring corner south-east", 66f, 112f, 18f), ("ring corner north-west", -72f, 214f, 12f), ("ring corner north-east", 66f, 214f, 18f) })
        {
            var s = new Spec(name, "stronghold-corner", new(x, y, 0f), new(x + 6f, y + 6f, h), null, ["AC-003"]) { UseNet = false, GrowAlong = 0f, GrowAcross = 0f };
            var outward = new Vector2(x < 0f ? -1f : 1f, y < 150f ? -1f : 1f);
            int bestYaw = 0; float bestDot = float.MinValue;
            foreach (int yaw in new[] { 0, 6, 12, 18 })
            {
                var item = new AshfallDressItem("AC-003", new Vector3(x + 3f, y + 3f, 0f), yaw * (MathF.PI / 12f), new Vector3(6f, 6f, h), false, AshfallDressRole.Prop, -1);
                var bx = Boxes(item, yaw, "AC-003");
                float vol = 0f; var cm = Vector2.Zero;
                foreach (var b in bx) { float v = b.Half.X * b.Half.Y * b.Half.Z; vol += v; cm += new Vector2(b.Centre.X - (x + 3f), b.Centre.Y - (y + 3f)) * v; }
                float dot = vol > 0f ? Vector2.Dot(cm / vol, outward) : 0f;
                if (dot > bestDot + 1e-4f) { bestDot = dot; bestYaw = yaw; }
            }
            Place(c, s, bestYaw);
        }
        // The legs: lines of standalone wall ruins with breaches.
        uint seed = 7000;
        foreach (var (name, alongX, t0, t1, lo, hi, height, yaw) in Legs)
            Leg(c, name, alongX, t0, t1, lo, hi, height, yaw, seed += 101);

        // The keep, its tower, the stair towers, the bridge, the terraces and the outer walls: one asset each.
        {
            var (mn, mx) = B("keep");
            var s = new Spec("keep", "stronghold-keep", mn, mx, null, ["AC-044", "AC-030", "AC-005"]) { Yaws = [0], UseNet = false };
            int k = Place(c, s);
            c.Flags.Add(k >= 0 && c.Items[k].Asset == "AC-044"
                ? "the keep: the expansion's gothic hall keep (AC-044), the library's first keep asset, stretched over the 64 x 48 x 18 m graybox keep (its design size is 17 x 15 x 13 m)"
                : "the keep: the gothic hall keep (AC-044) did not fit; the closest mass is used");
        }
        foreach (var name in new[] { "Keep tower", "Stair tower SW", "Stair tower SE" })
        {
            var (mn, mx) = B(name);
            // Draft 6: the stair towers are the expansion's freestanding stair (AC-046, the owner's list: "AC-046 stair towers"; its landing is drawn at about 2/3 of the tower: the walk cell keeps it under the step).
            var pool = name == "Keep tower" ? new[] { "AC-052", "AC-010" } : new[] { "AC-046" };
            Place(c, new Spec(name, name == "Keep tower" ? "stronghold-keeptower" : "stronghold-stairtower", mn, mx, null, pool) { UseNet = false });
        }
        foreach (var name in new[] { "terrace 1", "terrace 2", "terrace 3" })
        {
            var (mn, mx) = B(name);
            Place(c, new Spec(name, "terrace", mn, mx, null, Platform) { Yaws = [0, 12], UseNet = false, KeepWalkCell = true });
        }
        foreach (var name in new[] { "east outer wall 1", "east outer wall 2" })
        {
            var (mn, mx) = B(name);
            Place(c, new Spec(name, "outerwall", mn, mx, null, ["AC-041", "AC-001"]) { Yaws = [0, 12], UseNet = false });
        }
        {
            int gi = ids.First(i => groups[i].Name == "keep bridge");
            var (mn, mx) = Bounds(g5.Parts, byGroup[gi]);
            c.Bridges.Add(("keep bridge", mn, mx, DeckTop(g5.Parts, byGroup[gi])));
        }
    }

    /// <summary>The ring's five legs: name, along x, the band across (t0..t1), the span between the corner pieces, height, yaw step.</summary>
    static readonly (string Name, bool AlongX, float T0, float T1, float Lo, float Hi, float Height, int Yaw)[] Legs =
    [
        ("ring south leg west", true, 112f, 116f, -66f, -12f, 12f, 0),
        ("ring south leg east", true, 112f, 116f, 12f, 66f, 18f, 0),
        ("ring west leg", false, -72f, -68f, 118f, 214f, 12f, 18),
        ("ring north leg", true, 216f, 220f, -66f, 66f, 12f, 12),
        ("ring east leg", false, 68f, 72f, 118f, 214f, 18f, 6),
    ];

    /// <summary>Draft 6: the broken piece kept at a leg wall's breach end, and the shortest curtain (AC-041) between the ends (ours).</summary>
    const float LegEnd = 6f, LegCurtainMin = 8f;

    /// <summary>A leg's walls and breaches along it (seeded): walls 14..28 m, breaches 6..14 m, the first breach 8..14 m.</summary>
    static (List<(float A, float B)> Segs, List<(float A, float B)> Breaches) LegLayout(float lo, float hi, uint seed)
    {
        float pos = lo; uint k = 0; bool first = true;
        var segs = new List<(float A, float B)>(); var breaches = new List<(float A, float B)>();
        while (hi - pos > 1e-3f)
        {
            float rem = hi - pos;
            float lw = 14f + AshfallBuilder.Mix(seed, k++) % 15u, lb = (first ? 8f : 6f) + AshfallBuilder.Mix(seed, k++) % (first ? 7u : 9u);
            if (first) lw = MathF.Min(lw, rem - 22f);
            if (rem - lw < lb + 14f && !first) { segs.Add((pos, hi)); break; }
            if (first) lb = MathF.Min(lb, rem - lw - 14f);
            if (rem - lw - lb < 14f && !first) { segs.Add((pos, hi)); break; }
            segs.Add((pos, pos + lw));
            breaches.Add((pos + lw, pos + lw + lb));
            pos += lw + lb;
            first = false;
        }
        return (segs, breaches);
    }

    static void Leg(Ctx c, string name, bool alongX, float t0, float t1, float lo, float hi, float height, int yaw, uint seed)
    {
        var (segs, breaches) = LegLayout(lo, hi, seed);
        int n = 0;
        string[] broken = ["AC-004", "AC-001", "AC-006", "AC-005", "AC-030"];
        void Piece(string group, float a, float b, string[] pool)
        {
            var mn = alongX ? new Vector3(a, t0, 0f) : new Vector3(t0, a, 0f);
            var mx = alongX ? new Vector3(b, t1, height) : new Vector3(t1, b, height);
            Place(c, new Spec(group, "stronghold-wall", mn, mx, null, pool) { Yaws = [yaw], GrowAlong = 0f, UseNet = false, KeepLength = true });
        }
        foreach (var (a, b) in segs)
        {
            // Draft 6: the expansion's curtain wall (AC-041) runs between broken pieces. Its ends are squared and intact, so it is never a breach end: a wall that meets a breach keeps a broken piece of
            // LegEnd m at that end; a wall too short for both is one broken piece, as in draft 5.
            bool before = a > lo + 1e-3f, after = b < hi - 1e-3f;
            float ea = before ? LegEnd : 0f, eb = after ? LegEnd : 0f;
            string group = $"{name} wall {n++}";
            if (b - a - ea - eb < LegCurtainMin) { Piece(group, a, b, broken); continue; }
            if (before) Piece(group + " end", a, a + ea, broken);
            Piece(group + " curtain", a + ea, b - eb, ["AC-041"]);
            if (after) Piece(group + " end 2", b - eb, b, broken);
        }
        n = 0;
        foreach (var (a, b) in breaches)
        {
            bool rubble = n > 0 && b - a >= 8f && AshfallBuilder.Mix(seed, 500u + (uint)n) % 2u == 0;
            if (rubble)
            {
                float off = MathF.Max((b - a) / 2f - 2.2f * Scale6, 0f), mid = (a + b) / 2f + (AshfallBuilder.Mix(seed, 600u + (uint)n) % 2u == 0 ? -off : off);
                float tm = (t0 + t1) / 2f;
                var mn = alongX ? new Vector3(mid - 1.8f, tm - 1.5f, 0f) : new Vector3(tm - 1.5f, mid - 1.8f, 0f);
                var mx = alongX ? new Vector3(mid + 1.8f, tm + 1.5f, 1.8f) : new Vector3(tm + 1.5f, mid + 1.8f, 1.8f);
                Place(c, new Spec($"{name} breach {n} rubble", "rubble", mn, mx, null, ["AC-053"]) { Yaws = [yaw], UseNet = false, Kind = AshfallKind.Rubble, GrowAlong = 0.2f, GrowAcross = 0.2f });
            }
            c.Flags.Add($"{name} breach {n}: {b - a:0.#} m wide{(rubble ? ", rubble pile" : "")}");
            n++;
        }
    }
}
