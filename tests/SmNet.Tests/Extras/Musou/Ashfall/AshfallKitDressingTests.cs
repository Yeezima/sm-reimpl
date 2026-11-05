using System.Numerics;
using System.Text;
using System.Text.Json;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The kit dressing of Ashfall Citadel (draft 1, docs/musou-ashfall-graybox.md "Kit dressing (draft 1)"): determinism, catalogue ids, skins inside their graybox boxes, hidden parts, decoration heights and places,
/// surfaces on their own tops, the props' placement rules, and the two exports for the Mapmaker (JSON and GLB). From the generator alone: no asset bytes, no install, no servers.
/// </summary>
public class AshfallKitDressingTests(ITestOutputHelper output)
{
    static readonly (AshfallLevel Level, AshfallDressing Dressing) Built = AshfallMap.BuildWithDressing();
    static AshfallLevel Level => Built.Level;
    static AshfallDressing Dressing => Built.Dressing;

    static IEnumerable<AshfallDressItem> Of(AshfallDressRole role) => Dressing.Items.Where(i => i.Role == role);

    /// <summary>The item's footprint as an axis-aligned rectangle (min x, min y, max x, max y).</summary>
    static Vector4 Footprint(AshfallDressItem it)
    {
        float s = MathF.Sin(it.Yaw), c = MathF.Cos(it.Yaw);
        float ex = MathF.Abs(c) * it.Size.X / 2f + MathF.Abs(s) * it.Size.Y / 2f, ey = MathF.Abs(s) * it.Size.X / 2f + MathF.Abs(c) * it.Size.Y / 2f;
        return new Vector4(it.Position.X - ex, it.Position.Y - ey, it.Position.X + ex, it.Position.Y + ey);
    }

    static float Gap(Vector4 a, Vector4 b)
    {
        float dx = MathF.Max(MathF.Max(a.X - b.Z, b.X - a.Z), 0f), dy = MathF.Max(MathF.Max(a.Y - b.W, b.Y - a.W), 0f);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ---------------------------------------------------------------- determinism and catalogue

    [Fact]
    public void Two_builds_give_the_same_level_and_the_same_items_in_the_same_order()
    {
        var (levelB, dressingB) = AshfallMap.BuildWithDressing();
        Assert.Equal(Level.Hash, levelB.Hash);
        Assert.Equal(AshfallMap.Build().Hash, Level.Hash);   // Build() is the dressed level's collision
        Assert.Equal(AshfallMap.BuildWithFeatures().Level.Hash, Level.Hash);
        Assert.Equal(Dressing.Items.Count, dressingB.Items.Count);
        for (int i = 0; i < Dressing.Items.Count; i++) Assert.Equal(Dressing.Items[i], dressingB.Items[i]);
        Assert.Equal(Dressing.HiddenParts, dressingB.HiddenParts);
        Assert.Equal(AshfallMap.DressVersion, Dressing.Version);
        Assert.Equal(3u, AshfallMap.DressVersion);
        output.WriteLine($"{Dressing.Items.Count} items, {Dressing.HiddenParts.Count} hidden parts, {Level.Parts.Count} parts, {Level.SolidCorners().Length / 3} collision triangles, hash {Level.Hash:X16}");
        foreach (var g in Dressing.Items.GroupBy(i => i.Role).OrderBy(g => g.Key)) output.WriteLine($"{g.Key,-10} {g.Count(),5} items, {g.Sum(i => AshfallKit.Get(i.Asset).Triangles),10} model triangles");
    }

    [Fact]
    public void Every_item_names_a_catalogue_model_and_the_flagged_ones_are_scatter_only_or_unused()
    {
        var ids = AshfallKit.Models.Select(m => m.Id).ToHashSet();
        Assert.All(Dressing.Items, i => Assert.Contains(i.Asset, ids));
        Assert.All(Dressing.Items, i => Assert.True(float.IsFinite(i.Yaw) && Vector3.Min(i.Size, Vector3.Zero) == Vector3.Zero && i.Size.X > 0f && i.Size.Y > 0f && i.Size.Z > 0f));
        Assert.DoesNotContain(Dressing.Items, i => AshfallKit.Get(i.Asset).Review && i.Role != AshfallDressRole.Decoration);
        // Parts: Skin and Prop items name a part, Surface and Decoration none.
        Assert.All(Dressing.Items, i => Assert.True(i.Role is AshfallDressRole.Skin or AshfallDressRole.Prop ? i.Part >= 0 && i.Part < Level.Parts.Count : i.Part == -1));
    }

    // ---------------------------------------------------------------- surfaces

    [Fact]
    public void Surfaces_lie_on_the_road_cells_or_on_a_road_or_paving_top_and_never_over_a_block()
    {
        var surfaces = Of(AshfallDressRole.Surface).ToList();
        Assert.InRange(surfaces.Count, 150, 400);   // the ground tiles are draw-call heavy: a few hundred at most
        var tops = Level.Parts.Where(p => p.Kind is AshfallKind.Road or AshfallKind.Paving).Select(p => (Box: AshfallBox.Of(p)!.Value, p)).ToList();
        Assert.Equal(4, tops.Count);
        foreach (var it in surfaces)
        {
            float top = it.Position.Z + it.Size.Z - 0.03f;   // the surface it lies on: the tile is lifted 3 cm above it (AshfallDress.Surfaces Lift)
            Assert.True(it.Asset is "AC-033" or "AC-034" or "AC-035" or "AC-036" or "AC-038" or "AC-040", it.Asset);
            Assert.Equal(AshfallKit.Get(it.Asset).RawSize.Y, it.Size.Z, 1e-4f);
            var r = Footprint(it);
            if (MathF.Abs(top) < 1e-4f)
            {
                // On the roads' flat ground (the graybox has no Road part on the avenues): no corner or centre in a block, so never over a curb.
                var cx = (r.X + r.Z) / 2f; var cy = (r.Y + r.W) / 2f;
                foreach (var (x, y) in new[] { (r.X + 0.01f, r.Y + 0.01f), (r.Z - 0.01f, r.Y + 0.01f), (r.X + 0.01f, r.W - 0.01f), (r.Z - 0.01f, r.W - 0.01f), (cx, cy) })
                    Assert.True(AshfallMap.SurfaceAt(x, y) == 0f, $"{it.Asset} at ({x:0.0}, {y:0.0}) is over a block");
            }
            else
            {
                Assert.True(tops.Any(t => MathF.Abs(t.Box.Z1 - top) < 1e-3f && Gap(r, Bounds(t.Box)) < 1e-3f && Within(r, Bounds(t.Box))), $"{it.Asset} at top {top} is on no road or paving top");
            }
        }
        Assert.DoesNotContain(surfaces, i => i.Asset == "AC-037");   // the raised centre tile would sink B's walkers
    }

    static Vector4 Bounds(AshfallBox b)
    {
        float ex = MathF.Abs(b.AxisX.X) * b.Length / 2f + MathF.Abs(b.AxisY.X) * b.Depth / 2f, ey = MathF.Abs(b.AxisX.Y) * b.Length / 2f + MathF.Abs(b.AxisY.Y) * b.Depth / 2f;
        return new Vector4(b.Cx - ex, b.Cy - ey, b.Cx + ex, b.Cy + ey);
    }

    static bool Within(Vector4 inner, Vector4 outer) => inner.X >= outer.X - 0.01f && inner.Y >= outer.Y - 0.01f && inner.Z <= outer.Z + 0.01f && inner.W <= outer.W + 0.01f;

    // ---------------------------------------------------------------- decoration

    [Fact]
    public void Decoration_is_walk_over_and_stays_out_of_the_discs_the_start_areas_and_the_spawn_markers()
    {
        var deco = Of(AshfallDressRole.Decoration).ToList();
        Assert.InRange(deco.Count, 100, 400);
        foreach (var it in deco)
        {
            Assert.True(it.Size.Z <= 0.35f + 1e-4f, $"{it.Asset} is {it.Size.Z} m high");
            Assert.True(it.Asset is "AC-020" or "AC-022" or "AC-024" or "AC-025", it.Asset);
            var r = Footprint(it);
            foreach (var pt in Level.Points)
            {
                float dx = MathF.Max(MathF.Max(r.X - pt.Center.X, pt.Center.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - pt.Center.Y, pt.Center.Y - r.W), 0f);
                Assert.True(dx * dx + dy * dy >= pt.Radius * pt.Radius, $"{it.Asset} at {it.Position} is in the disc of {pt.Name}");
            }
            foreach (var a in Level.Areas)
            {
                float hx = a.Size.X / 2f, hy = a.Size.Y / 2f;
                Assert.False(r.X < a.Center.X + hx && r.Z > a.Center.X - hx && r.Y < a.Center.Y + hy && r.W > a.Center.Y - hy, $"{it.Asset} at {it.Position} is in the {a.Side} start area");
            }
            foreach (var s in Level.Spawns) Assert.True(Vector2.Distance(new Vector2(s.X, s.Y), new Vector2(it.Position.X, it.Position.Y)) >= 4f, $"{it.Asset} at {it.Position} is on a spawn marker");
            Assert.Equal(AshfallMap.SurfaceAt(it.Position.X, it.Position.Y), it.Position.Z);
        }
    }

    // ---------------------------------------------------------------- draft 2: the freestanding kit

    static readonly string[] Architecture = ["AC-001", "AC-002", "AC-003", "AC-004", "AC-005", "AC-006", "AC-010"];

    [Fact]
    public void Nothing_is_attached_to_a_wall_no_skin_no_hidden_part_and_nothing_near_the_boundary()
    {
        Assert.DoesNotContain(Dressing.Items, i => i.Role == AshfallDressRole.Skin);
        Assert.Empty(Dressing.HiddenParts);
        // No item within 2 m of the boundary's inner face (the ground tiles of the perimeter road reach the face: they are the floor).
        foreach (var it in Dressing.Items.Where(i => i.Role != AshfallDressRole.Surface))
        {
            var r = Footprint(it);
            Assert.True(r.X >= -238f && r.Y >= -238f && r.Z <= 238f && r.W <= 238f, $"{it.Asset} at {it.Position} is within 2 m of the boundary");
        }
        // The boundary, the keep, the towers, the plinths and the obelisks stay bare: no Prop or Decoration footprint overlaps a Boundary or Stronghold box.
        var bnd = Level.Parts.Where(p => p.Kind is AshfallKind.Boundary or AshfallKind.Stronghold).Select(p => Bounds(AshfallBox.Of(p)!.Value)).ToList();
        foreach (var it in Dressing.Items.Where(i => i.Role is AshfallDressRole.Prop or AshfallDressRole.Decoration))
        {
            var r = Footprint(it);
            foreach (var b in bnd) Assert.False(r.X < b.Z && r.Z > b.X && r.Y < b.W && r.W > b.Y, $"{it.Asset} at {it.Position} overlaps a boundary or stronghold box");
        }
    }

    [Fact]
    public void Every_kit_piece_is_scaled_uniformly_near_its_manifest_size_and_has_its_proxy_part()
    {
        var props = Of(AshfallDressRole.Prop).ToList();
        Assert.InRange(props.Count, 250, 900);
        foreach (var it in Dressing.Items.Where(i => i.Role is AshfallDressRole.Prop or AshfallDressRole.Decoration))
        {
            var raw = AshfallKit.Get(it.Asset).RawSize;
            float sx = it.Size.X / (it.SwapXZ ? raw.Z : raw.X), sy = it.Size.Y / (it.SwapXZ ? raw.X : raw.Z), sz = it.Size.Z / raw.Y;
            float ratio = MathF.Max(sx, MathF.Max(sy, sz)) / MathF.Min(sx, MathF.Min(sy, sz));
            Assert.True(ratio <= 1.001f, $"{it.Asset} is stretched {ratio:0.000}:1");   // uniform (the rule allows +-10 %)
        }
        foreach (var it in props)
        {
            Assert.InRange(it.Part, 0, Level.Parts.Count - 1);
            var part = Level.Parts[it.Part];
            Assert.True(part.Solid && part.Kind is AshfallKind.Wall or AshfallKind.Rubble or AshfallKind.Cover);
            if (it.Asset != "AC-008") Assert.Equal(AshfallMap.SurfaceAt(it.Position.X, it.Position.Y), it.Position.Z);
        }
        // The proxies are the newest parts: every prop's first part is in the last stretch of the level.
        Assert.True(props.Min(p => p.Part) >= Level.Parts.Count - props.Count * 40);
        // Scale near the manifest's (the base scale, 0.85..1.15) for architecture, landmarks, the perch and the rubble piles; the cover is 1.0..1.2 m tall.
        foreach (var it in props.Where(p => Architecture.Contains(p.Asset) || p.Asset is "AC-027" or "AC-028" or "AC-029" or "AC-030" or "AC-031" or "AC-032" or "AC-007" or "AC-013" or "AC-019" or "AC-021" or "AC-023" or "AC-008"))
        {
            var m = AshfallKit.Get(it.Asset);
            float ratio = it.Size.Z / m.RawSize.Y / AshfallMap.ManifestScale(m);
            Assert.InRange(ratio, 0.85f - 1e-3f, 1.15f + 1e-3f);
        }
        var cover = props.Where(p => p.Asset is "AC-011" or "AC-014" or "AC-017" or "AC-018" or "AC-012").ToList();
        Assert.True(cover.Count > 100);
        Assert.All(cover, p => Assert.InRange(p.Size.Z, 1.0f - 1e-3f, 1.2f + 1e-3f));
        Assert.True(cover.Count(p => p.Asset == "AC-018") * 8 <= cover.Count);   // AC-018 rarely
        Assert.All(cover.Where(p => p.Asset == "AC-012"), p => Assert.True(p.Position.X < -104f && MathF.Abs(p.Position.Y) < 80f));   // AC-012 only by the craters
    }

    [Fact]
    public void The_same_model_never_stands_on_two_neighbouring_pieces()
    {
        var props = Of(AshfallDressRole.Prop).Select(p => (p.Asset, Rect: Footprint(p))).ToList();
        for (int i = 0; i < props.Count; i++)
            for (int j = i + 1; j < props.Count; j++)
                if (props[i].Asset == props[j].Asset)
                    Assert.True(Gap(props[i].Rect, props[j].Rect) >= 4f - 1e-3f, $"two {props[i].Asset} pieces {Gap(props[i].Rect, props[j].Rect):0.0} m apart");
        output.WriteLine("props per model: " + string.Join(", ", Of(AshfallDressRole.Prop).GroupBy(p => p.Asset).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}")));
        Assert.True(props.Select(p => p.Asset).Distinct().Count() >= 18);   // mixed: many models
        Assert.True(AshfallKit.Models.Where(m => m.Review).All(m => Dressing.Items.All(i => i.Asset != m.Id || i.Role == AshfallDressRole.Decoration)));
        Assert.InRange(Dressing.Items.Count(i => i.Asset == "AC-031"), 1, 10);
    }

    [Fact]
    public void Kit_pieces_obey_the_placers_rules_and_the_arches_keep_their_opening()
    {
        var props = Of(AshfallDressRole.Prop).Where(p => p.Asset != "AC-008").ToList();
        foreach (var it in props)
        {
            var r = Footprint(it);
            foreach (var pt in Level.Points)
            {
                float dx = MathF.Max(MathF.Max(r.X - pt.Center.X, pt.Center.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - pt.Center.Y, pt.Center.Y - r.W), 0f);
                Assert.True(MathF.Sqrt(dx * dx + dy * dy) >= pt.Radius + 6f - 1e-3f, $"{it.Asset} at {it.Position} is within 6 m of zone {pt.Name}");
            }
            foreach (var a in Level.Areas)
            {
                float hx = a.Size.X / 2f, hy = a.Size.Y / 2f;
                Assert.False(r.X < a.Center.X + hx && r.Z > a.Center.X - hx && r.Y < a.Center.Y + hy && r.W > a.Center.Y - hy, $"{it.Asset} at {it.Position} is in the {a.Side} start area");
            }
            foreach (float c in new[] { -92f, 92f, -232f, 232f })
            {
                Assert.False(r.X < c + 5.2f && r.Z > c - 5.2f, $"{it.Asset} at {it.Position} is in the lane along x = {c}");
                Assert.False(r.Y < c + 5.2f && r.W > c - 5.2f, $"{it.Asset} at {it.Position} is in the lane along y = {c}");
            }
            Assert.False(r.X < 84f && r.Z > -84f && r.Y < 224f && r.W > 96f, $"{it.Asset} at {it.Position} is in the stronghold block");
            Assert.False(r.X < 8f && r.Z > -8f && r.Y < 80f && r.W > -160f, $"{it.Asset} at {it.Position} is on the paved road or the gate approach");
            Assert.False(r.X < 80f && r.Z > -80f && r.Y < 8f && r.W > -8f, $"{it.Asset} at {it.Position} is on the plaza's axis");
        }
        foreach (var arch in props.Where(p => p.Asset is "AC-002" or "AC-006"))
        {
            // Two piers and a lintel (three parts); the lintel starts at 72 % of the height and the opening between the piers is free.
            var parts = Level.Parts.Skip(arch.Part).Take(3).Select(p => AshfallBox.Of(p)!.Value).ToList();
            Assert.Equal(1, parts.Count(b => b.Z0 > arch.Position.Z + 0.5f * arch.Size.Z));
            Assert.Equal(2, parts.Count(b => b.Z0 <= arch.Position.Z + 0.01f));
        }
    }

    [Fact]
    public void Perches_have_a_walkable_deck_and_a_stair_and_no_graybox_rubble_stays()
    {
        var perches = Of(AshfallDressRole.Prop).Where(p => p.Asset == "AC-008").ToList();
        output.WriteLine($"{perches.Count} perches");
        Assert.InRange(perches.Count, 15, 26);
        // Each perch: a Floor deck top 1.5..5.5 m (0.5 m levels) on a Wall body, with box steps of the Stair kind rising to it.
        foreach (var it in perches)
        {
            Assert.Equal(AshfallKind.Wall, Level.Parts[it.Part].Kind);
            Assert.Equal(AshfallKind.Floor, Level.Parts[it.Part + 1].Kind);
            float top = Level.Parts[it.Part + 1].Corners.Max(c => c.Z);
            Assert.True(top is >= 1.5f and <= 5.5f && top * 2f == MathF.Round(top * 2f), $"perch deck at {top}");
            Assert.Equal(AshfallKind.Stair, Level.Parts[it.Part + 2].Kind);
        }
        // The replaced families are gone from the graybox: every Rubble part is a kit proxy (the version 5 clusters and version 1's pieces were removed).
        var kitParts = new HashSet<int>(Of(AshfallDressRole.Prop).SelectMany(p => new[] { p.Part, p.Part + 1, p.Part + 2 }));
        int graybox = Enumerable.Range(0, Level.Parts.Count).Count(i => Level.Parts[i].Kind == AshfallKind.Rubble && !kitParts.Contains(i));
        Assert.Equal(0, graybox);
    }

    // ---------------------------------------------------------------- exports

    [Fact]
    public void The_dressing_json_round_trips_counts_and_values()
    {
        string json = AshfallExport.DressingJson(Level, Dressing);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal((int)AshfallMap.Version, root.GetProperty("levelVersion").GetInt32());
        Assert.Equal((int)AshfallMap.DressVersion, root.GetProperty("dressVersion").GetInt32());
        Assert.Equal(Level.Hash, Convert.ToUInt64(root.GetProperty("levelHash").GetString(), 16));
        var items = root.GetProperty("items");
        Assert.Equal(Dressing.Items.Count, items.GetArrayLength());
        for (int i = 0; i < Dressing.Items.Count; i += 37)
        {
            var e = items[i]; var it = Dressing.Items[i];
            Assert.Equal(i, e.GetProperty("index").GetInt32());
            Assert.Equal(it.Asset, e.GetProperty("asset").GetString());
            Assert.Equal(it.Role.ToString(), e.GetProperty("role").GetString());
            Assert.Equal(it.Part, e.GetProperty("part").GetInt32());
            Assert.Equal(it.Yaw, e.GetProperty("yaw").GetSingle());
            Assert.Equal(it.SwapXZ, e.GetProperty("swapXZ").GetBoolean());
            var pos = e.GetProperty("position"); var size = e.GetProperty("size");
            Assert.Equal(new Vector3(pos[0].GetSingle(), pos[1].GetSingle(), pos[2].GetSingle()), it.Position);
            Assert.Equal(new Vector3(size[0].GetSingle(), size[1].GetSingle(), size[2].GetSingle()), it.Size);
        }
        Assert.Equal(Dressing.HiddenParts.Count, root.GetProperty("hiddenParts").GetArrayLength());
    }

    [Fact]
    public void The_graybox_glb_has_a_valid_header_chunks_and_one_mesh_per_kind()
    {
        var glb = AshfallExport.GrayboxGlb(Level);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(glb, 4));
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
        uint jsonLen = BitConverter.ToUInt32(glb, 12);
        Assert.Equal(0x4E4F534Au, BitConverter.ToUInt32(glb, 16));
        Assert.Equal(0u, jsonLen % 4);
        uint binLen = BitConverter.ToUInt32(glb, 20 + (int)jsonLen);
        Assert.Equal(0x004E4942u, BitConverter.ToUInt32(glb, 24 + (int)jsonLen));
        Assert.Equal(0u, binLen % 4);
        Assert.Equal(12 + 8 + (int)jsonLen + 8 + (int)binLen, glb.Length);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, (int)jsonLen));
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
        var kinds = Enum.GetValues<AshfallKind>().Where(k => Level.Parts.Any(p => p.Solid && p.Kind == k)).Select(k => k.ToString()).ToList();
        Assert.DoesNotContain("Objective", kinds);
        Assert.Equal(kinds, root.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("name").GetString()).ToList());
        Assert.Equal(kinds.Count, root.GetProperty("meshes").GetArrayLength());
        var accessors = root.GetProperty("accessors");
        Assert.Equal(Level.SolidCorners().Length, accessors.EnumerateArray().Sum(a => a.GetProperty("count").GetInt32()));
        var views = root.GetProperty("bufferViews");
        long total = 0;
        for (int i = 0; i < views.GetArrayLength(); i++)
        {
            Assert.Equal(total, views[i].GetProperty("byteOffset").GetInt64());
            Assert.Equal(accessors[i].GetProperty("count").GetInt64() * 12, views[i].GetProperty("byteLength").GetInt64());
            total += views[i].GetProperty("byteLength").GetInt64();
        }
        Assert.Equal(total, root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt64());
        Assert.True(binLen >= total && binLen - total < 4);
        // Y up: the highest glTF Y over all meshes is the level's highest Z; game Y maps to glTF -Z.
        float maxY = accessors.EnumerateArray().Max(a => a.GetProperty("max")[1].GetSingle());
        Assert.Equal(Level.Parts.Where(p => p.Solid).SelectMany(p => p.Corners).Max(c => c.Z), maxY);
        float minZ = accessors.EnumerateArray().Min(a => a.GetProperty("min")[2].GetSingle());
        Assert.Equal(-Level.Parts.Where(p => p.Solid).SelectMany(p => p.Corners).Max(c => c.Y), minZ);
    }
}
