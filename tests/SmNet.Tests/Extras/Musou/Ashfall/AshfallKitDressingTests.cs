using System.Numerics;
using System.Text;
using System.Text.Json;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The kit dressing of Ashfall Citadel (draft 3, docs/musou-ashfall-graybox.md "Draft 3 (scaled replacements)"): the level is v5 untouched, every visible graybox structure is covered by Skin items
/// (scaled replacements) and listed as hidden, items stay inside their structure's bounds grown by 0.5 m, no model is stretched absurdly, plus the road and plaza surfaces and the two exports for the Mapmaker.
/// From the generator alone: no asset bytes, no install, no servers.
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

    static (Vector3 Min, Vector3 Max) Aabb(AshfallDressItem it)
    {
        var f = Footprint(it);
        return (new Vector3(f.X, f.Y, it.Position.Z), new Vector3(f.Z, f.W, it.Position.Z + it.Size.Z));
    }

    static (Vector3 Min, Vector3 Max) PartBounds(int part)
    {
        var c = Level.Parts[part].Corners;
        return (c.Aggregate(Vector3.Min), c.Aggregate(Vector3.Max));
    }

    static bool Meets((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

    static bool IsGroundKind(AshfallKind k) => k is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary or AshfallKind.Objective;

    // ---------------------------------------------------------------- the level and determinism

    [Fact]
    public void The_level_is_ashfall_v5_exactly_and_the_dressing_is_deterministic()
    {
        Assert.Equal(5u, AshfallMap.Version);
        Assert.Equal(0xAE096E323BB5A4D3UL, Level.Hash);
        Assert.Equal(AshfallMap.Build().Hash, Level.Hash);
        Assert.Equal(AshfallMap.BuildWithFeatures().Level.Hash, Level.Hash);
        var (levelB, dressingB) = AshfallMap.BuildWithDressing();
        Assert.Equal(Level.Hash, levelB.Hash);
        Assert.Equal(Dressing.Items.Count, dressingB.Items.Count);
        for (int i = 0; i < Dressing.Items.Count; i++) Assert.Equal(Dressing.Items[i], dressingB.Items[i]);
        Assert.Equal(Dressing.HiddenParts, dressingB.HiddenParts);
        Assert.Equal(AshfallMap.DressVersion, Dressing.Version);
        Assert.Equal(5u, AshfallMap.DressVersion);
        long tris = Dressing.Items.Sum(i => (long)AshfallKit.Get(i.Asset).Triangles);
        output.WriteLine($"{Dressing.Items.Count} items, {tris:N0} model triangles at full detail, {Dressing.HiddenParts.Count} hidden parts, {Dressing.Structures!.Count} structures, {Level.Parts.Count} parts, hash {Level.Hash:X16}");
        foreach (var g in Dressing.Items.GroupBy(i => i.Role).OrderBy(g => g.Key)) output.WriteLine($"{g.Key,-10} {g.Count(),5} items, {g.Sum(i => (long)AshfallKit.Get(i.Asset).Triangles),12:N0} model triangles");
        Assert.InRange(Dressing.Items.Count, 2000, 5200);   // a few thousand at most
    }

    [Fact]
    public void Every_item_names_a_catalogue_model_and_the_flagged_ones_are_never_used()
    {
        var ids = AshfallKit.Models.Select(m => m.Id).ToHashSet();
        Assert.All(Dressing.Items, i => Assert.Contains(i.Asset, ids));
        Assert.All(Dressing.Items, i => Assert.True(float.IsFinite(i.Yaw) && i.Size.X > 0f && i.Size.Y > 0f && i.Size.Z > 0f && float.IsFinite(i.Position.X + i.Position.Y + i.Position.Z)));
        Assert.DoesNotContain(Dressing.Items, i => AshfallKit.Get(i.Asset).Review);
        Assert.All(Dressing.Items, i => Assert.True(i.Role is AshfallDressRole.Skin or AshfallDressRole.Surface));
        Assert.All(Of(AshfallDressRole.Skin), i => Assert.InRange(i.Part, 0, Level.Parts.Count - 1));
        Assert.All(Of(AshfallDressRole.Surface), i => Assert.Equal(-1, i.Part));
    }

    // ---------------------------------------------------------------- draft 3: replacements

    [Fact]
    public void Every_solid_part_except_the_ground_kinds_and_the_boundary_is_hidden_and_covered_by_a_skin_item()
    {
        var hidden = Dressing.HiddenParts.ToHashSet();
        Assert.Equal(Dressing.HiddenParts.Count, hidden.Count);
        for (int p = 0; p < Level.Parts.Count; p++)
        {
            var part = Level.Parts[p];
            bool expected = part.Solid && !IsGroundKind(part.Kind);
            Assert.True(expected == hidden.Contains(p), $"part {p} ({part.Kind}) hidden={hidden.Contains(p)}, expected {expected}");
        }
        var skins = Of(AshfallDressRole.Skin).Select(Aabb).ToList();
        int uncovered = 0;
        foreach (int p in hidden)
        {
            var b = PartBounds(p);
            if (!skins.Any(s => Meets(s, b))) { uncovered++; if (uncovered < 5) output.WriteLine($"uncovered part {p} {Level.Parts[p].Kind} {b.Min} {b.Max}"); }
        }
        Assert.Equal(0, uncovered);
    }

    [Fact]
    public void A_skin_items_box_stays_within_its_structures_bounds_grown_by_half_a_metre()
    {
        var structureOf = new Dictionary<int, int>();
        for (int s = 0; s < Dressing.Structures!.Count; s++) foreach (int p in Dressing.Structures[s].Parts) structureOf[p] = s;
        Assert.Equal(Dressing.HiddenParts.Count, structureOf.Count);
        const float grow = 0.5f + 1e-3f;
        foreach (var it in Of(AshfallDressRole.Skin))
        {
            var st = Dressing.Structures[structureOf[it.Part]];
            var (mn, mx) = Aabb(it);
            Assert.True(mn.X >= st.Min.X - grow && mn.Y >= st.Min.Y - grow && mn.Z >= st.Min.Z - grow && mx.X <= st.Max.X + grow && mx.Y <= st.Max.Y + grow && mx.Z <= st.Max.Z + grow,
                $"{it.Asset} over part {it.Part}: {mn}..{mx} outside {st.Min}..{st.Max} + 0.5");
        }
        // Each structure's bounds are the bounds of its parts' corners.
        foreach (var st in Dressing.Structures)
        {
            var corners = st.Parts.SelectMany(p => Level.Parts[p].Corners).ToList();
            Assert.Equal(corners.Aggregate(Vector3.Min), st.Min);
            Assert.Equal(corners.Aggregate(Vector3.Max), st.Max);
        }
    }

    [Fact]
    public void No_facade_model_is_stretched_past_about_one_to_three_point_seven_along_its_length_and_height()
    {
        foreach (var it in Of(AshfallDressRole.Skin).Where(i => !i.SwapXZ && i.Asset is "AC-001" or "AC-002" or "AC-004" or "AC-005" or "AC-006" or "AC-011" or "AC-014" or "AC-017"))
        {
            var raw = AshfallKit.Get(it.Asset).RawSize;
            float stretch = (it.Size.X / raw.X) / (it.Size.Z / raw.Y);   // 1 = the model's own proportions
            Assert.True(stretch is >= 1f / 3.7f and <= 3.7f, $"{it.Asset} {it.Size} stretch {stretch:0.00}");
        }
    }

    [Fact]
    public void The_mix_uses_many_models_and_the_boundary_and_ground_are_left_to_the_client()
    {
        var used = Of(AshfallDressRole.Skin).Select(i => i.Asset).Distinct().ToList();
        Assert.True(used.Count >= 15, string.Join(",", used));
        foreach (string id in new[] { "AC-001", "AC-004", "AC-005", "AC-006", "AC-007", "AC-010", "AC-011", "AC-022" }) Assert.Contains(id, used);
        Assert.DoesNotContain(Dressing.HiddenParts, p => Level.Parts[p].Kind is AshfallKind.Boundary or AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving);
        // No object stands on the boundary: nothing beyond the backdrop blocks (270 m).
        Assert.DoesNotContain(Of(AshfallDressRole.Skin), i => MathF.Abs(i.Position.X) > 272f || MathF.Abs(i.Position.Y) > 272f);
    }

    [Fact]
    public void Stair_flights_are_covered_by_the_stair_model_turned_on_its_side()
    {
        var flights = Of(AshfallDressRole.Skin).Where(i => i.Asset == "AC-007").ToList();
        Assert.InRange(flights.Count, 100, 400);
        Assert.All(flights, f => Assert.True(f.SwapXZ));
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
