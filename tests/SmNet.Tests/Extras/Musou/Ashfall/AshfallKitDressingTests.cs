using System.Numerics;
using System.Text;
using System.Text.Json;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The kit dressing of Ashfall Citadel (draft 5, docs/musou-ashfall-graybox.md "Draft 5"): the level is the draft-5 level (version 9), every item is a catalogue model, the hidden parts are exactly the non-ground parts,
/// each item owns a range of collision parts, plus the road and plaza surfaces (turns by the variation seed only) and the two exports for the Mapmaker. From the generator alone: no asset bytes, no install, no servers.
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

    static bool IsGroundKind(AshfallKind k) => k is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary or AshfallKind.Objective;

    // ---------------------------------------------------------------- the level and determinism

    [Fact]
    public void The_level_is_the_draft_6_level_and_the_dressing_is_deterministic()
    {
        Assert.Equal(13u, AshfallMap.Version);
        Assert.Equal(5u, AshfallMap.GrayboxVersion);
        Assert.Equal(AshfallDraft5Tests.PinnedLevelHash, Level.Hash);
        Assert.Equal(AshfallMap.Build().Hash, Level.Hash);
        Assert.Equal(AshfallMap.BuildWithFeatures().Level.Hash, Level.Hash);
        var fresh = AshfallMap.RebuildDraft5();   // not the cached one: a second full run
        Assert.Equal(Level.Hash, fresh.Level.Hash);
        Assert.Equal(Dressing.Items.Count, fresh.Dressing.Items.Count);
        for (int i = 0; i < Dressing.Items.Count; i++) Assert.Equal(Dressing.Items[i], fresh.Dressing.Items[i]);
        Assert.Equal(Dressing.HiddenParts, fresh.Dressing.HiddenParts);
        Assert.Equal(Dressing.ItemParts, fresh.Dressing.ItemParts);
        Assert.Equal(Dressing.Flags, fresh.Dressing.Flags);
        Assert.Equal(AshfallMap.DressVersion, Dressing.Version);
        Assert.Equal(11u, AshfallMap.DressVersion);
        long tris = Dressing.Items.Sum(i => (long)AshfallKit.Get(i.Asset).Triangles);
        output.WriteLine($"{Dressing.Items.Count} items, {tris:N0} model triangles at full detail, {Dressing.HiddenParts.Count} hidden parts, {Dressing.Structures!.Count} structures, {Level.Parts.Count} parts, hash {Level.Hash:X16}");
        foreach (var g in Dressing.Items.GroupBy(i => i.Role).OrderBy(g => g.Key)) output.WriteLine($"{g.Key,-10} {g.Count(),5} items, {g.Sum(i => (long)AshfallKit.Get(i.Asset).Triangles),12:N0} model triangles");
        Assert.InRange(Dressing.Items.Count, 600, 1600);
    }

    [Fact]
    public void Every_item_names_a_catalogue_model_and_the_flagged_ones_are_never_used()
    {
        var ids = AshfallKit.Models.Select(m => m.Id).ToHashSet();
        Assert.All(Dressing.Items, i => Assert.Contains(i.Asset, ids));
        Assert.All(Dressing.Items, i => Assert.True(float.IsFinite(i.Yaw) && i.Size.X > 0f && i.Size.Y > 0f && i.Size.Z > 0f && float.IsFinite(i.Position.X + i.Position.Y + i.Position.Z)));
        Assert.DoesNotContain(Dressing.Items, i => AshfallKit.Get(i.Asset).Review);
        Assert.All(Dressing.Items, i => Assert.True(i.Role is AshfallDressRole.Prop or AshfallDressRole.Surface));
        Assert.All(Dressing.Items, i => Assert.False(i.SwapXZ));
        Assert.All(Of(AshfallDressRole.Surface), i => Assert.Equal(-1, i.Part));
        Assert.DoesNotContain(Dressing.Items, i => i.Asset == "AC-040");   // its 1.2 m crater would float over the flat collision of a walked road
    }

    [Fact]
    public void Hidden_parts_are_exactly_the_non_ground_parts_and_every_item_owns_a_range_of_them()
    {
        var hidden = Dressing.HiddenParts.ToHashSet();
        Assert.Equal(Dressing.HiddenParts.Count, hidden.Count);
        for (int p = 0; p < Level.Parts.Count; p++)
            Assert.True(!IsGroundKind(Level.Parts[p].Kind) == hidden.Contains(p), $"part {p} ({Level.Parts[p].Kind}) hidden={hidden.Contains(p)}");
        Assert.Equal(Dressing.Items.Count, Dressing.ItemParts.Count);
        var owned = new HashSet<int>();
        for (int i = 0; i < Dressing.Items.Count; i++)
        {
            var (first, count) = Dressing.ItemParts[i];
            if (Dressing.Items[i].Role == AshfallDressRole.Surface) { Assert.Equal((0, 0), (first, count)); continue; }
            for (int p = first; p < first + count; p++) { Assert.True(hidden.Contains(p), $"item {i} owns part {p}, which is not hidden"); Assert.True(owned.Add(p), $"part {p} owned twice"); }
        }
        Assert.Equal(hidden.Count, owned.Count);   // every collision part belongs to exactly one item
        Assert.Contains(Dressing.Items.Select((it, i) => (it, i)), x => x.it.Role == AshfallDressRole.Prop && Dressing.ItemParts[x.i].Count == 0);   // the backdrop blocks have no collision
        Assert.All(Dressing.Structures!, st => Assert.All(st.Parts, p => Assert.True(hidden.Contains(p))));
        Assert.NotEmpty(Dressing.Flags);
    }

    // ---------------------------------------------------------------- surfaces

    [Fact]
    public void Surfaces_lie_on_the_road_cells_or_on_a_road_or_paving_top_and_never_over_a_block()
    {
        var all = Of(AshfallDressRole.Surface).ToList();
        var surfaces = all.Where(i => i.Asset != "AC-076").ToList();
        Assert.InRange(surfaces.Count, 150, 400);   // the ground tiles are draw-call heavy: a few hundred at most
        var tops = Level.Parts.Where(p => p.Kind is AshfallKind.Road or AshfallKind.Paving).Select(p => (Box: AshfallBox.Of(p)!.Value, p)).ToList();
        Assert.Equal(4, tops.Count);
        foreach (var it in surfaces)
        {
            // The surface it lies on: the tile's walk level (the first library's tiles: their top; the expansion's slabs: the median of their up-facing surface) is lifted 3 cm above it (AshfallDress.Surfaces Lift).
            var m = AshfallKit.Get(it.Asset);
            float top = it.Position.Z + (m.WalkY - m.RawMin.Y) / m.RawSize.Y * it.Size.Z - 0.03f;
            Assert.True(it.Asset is "AC-033" or "AC-034" or "AC-035" or "AC-062" or "AC-063" or "AC-064" or "AC-065" or "AC-066" or "AC-067" or "AC-068" or "AC-069" or "AC-070" or "AC-071" or "AC-072", it.Asset);
            // the first library's tiles keep their raw height in metres; the expansion's slabs their design thickness (the relief at its real height)
            Assert.Equal(m.SourceId.Length == 0 ? m.RawSize.Y : m.RawSize.Y * m.DesignScale, it.Size.Z, 1e-4f);
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
        // Draft 6, phase 2: the expansion's round objective dais is used once, under A, squashed so it stays under the step height; B's centre stays flat. AC-037 is no longer used.
        Assert.DoesNotContain(all, i => i.Asset == "AC-037");
        var dais = Assert.Single(all, i => i.Asset == "AC-076");
        Assert.Equal(Level.Points[0].Center.X, dais.Position.X);
        Assert.Equal(Level.Points[0].Center.Y, dais.Position.Y);
        Assert.InRange(dais.Position.Z + dais.Size.Z - AshfallMap.BlockTop, 0f, 0.4f);
    }

    [Fact]
    public void Surfaces_turn_only_by_their_allowed_steps_with_the_variation_seed()
    {
        Assert.Equal(1u, AshfallMap.VariationSeed);
        int Step(AshfallDressItem it) => (int)MathF.Round(it.Yaw / (MathF.PI / 12f)) % 24;
        var surfaces = Of(AshfallDressRole.Surface).Where(i => i.Asset != "AC-076").ToList();
        Assert.All(surfaces, i => Assert.Equal(0, ((Step(i) % 6) + 6) % 6));   // quarter turns only, never a 15 degree turn
        // Straight tiles: half turns only (the seams stay valid), and the seed does vary them.
        var straight = surfaces.Where(i => i.Asset is "AC-033" or "AC-068" or "AC-069").ToList();
        Assert.Equal([0, 6, 12, 18], straight.Select(Step).Distinct().Order().ToArray());
        foreach (var grp in straight.GroupBy(i => Step(i) % 12 == 0)) Assert.True(grp.Count() > 10);
        // Edge and corner tiles never get a free turn: the plaza rim tiles face outward, so only inside cells (neither on the rim) vary.
        var edges = surfaces.Where(i => i.Asset == "AC-070").Select(Step).OrderBy(x => x).ToArray();
        Assert.Equal([0, 6, 12, 18], edges);   // the four corners, each a different fixed turn
        // T-junctions are never turned by the seed: eight of them, each pointing its stem away from the perimeter (four per axis, two turns each).
        Assert.Equal(8, surfaces.Count(i => i.Asset == "AC-034"));
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
        var parts = root.GetProperty("itemParts");
        Assert.Equal(Dressing.ItemParts.Count, parts.GetArrayLength());
        for (int i = 0; i < Dressing.ItemParts.Count; i += 11) { Assert.Equal(Dressing.ItemParts[i].First, parts[i][0].GetInt32()); Assert.Equal(Dressing.ItemParts[i].Count, parts[i][1].GetInt32()); }
        Assert.Equal(Dressing.Flags.Count, root.GetProperty("flags").GetArrayLength());
        Assert.Equal(Dressing.Flags[0], root.GetProperty("flags")[0].GetString());
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
