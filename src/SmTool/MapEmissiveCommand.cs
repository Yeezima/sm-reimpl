using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool map-emissive &lt;game-dir|auto&gt; [--map mp_tdm_09] [--all] [--where def,def] [--surfaces mesh,mesh]</c>: every material of a map's placed meshes that has an <c>EmissiveMap</c>, with its
/// <c>EmissiveColor</c>, whether that tint is salmon-red (red at least 0.35 and at least twice green and blue; the olive and green tints are flagged separately),
/// and for each the surface's mesh, the object definitions that use the mesh and their placement counts (read-only; fix/manufactorum-salmon-2, 2026-11-14).
/// </summary>
public static class MapEmissiveCommand
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        string map = rest.SkipWhile(a => a != "--map").Skip(1).FirstOrDefault() ?? "mp_tdm_09";
        bool all = rest.Contains("--all");
        var scene = MapScene.Load(gameDir, map);
        if (rest.SkipWhile(a => a != "--surfaces").Skip(1).FirstOrDefault() is { } surf)
        {
            // every material range of the named meshes (nearest-LOD blocks as drawn): material, triangles, share of the block's triangles
            foreach (var name in surf.Split(','))
            {
                if (!scene.Meshes.TryGetValue(name, out var m)) { Console.WriteLine($"{name}: not in the scene"); continue; }
                for (int b = 0; b < m.Blocks.Count; b++)
                {
                    int total = m.Blocks[b].Materials.Sum(r => r.IndexCount / 3);
                    foreach (var r in m.Blocks[b].Materials)
                        Console.WriteLine($"{name}[{b}] {r.Name,-24} {r.IndexCount / 3,6} triangles {100.0 * (r.IndexCount / 3) / Math.Max(1, total),5:0.0}%");
                }
            }
            return 0;
        }
        if (rest.Contains("--big"))
        {
            // classic-maps-all: the placements with the largest drawn extent (world box of the nearest LOD's mesh), with their materials: finds the flat slabs and backdrops that dominate a view.
            var big = new List<(string Name, string Def, string Mesh, System.Numerics.Vector3 Size, System.Numerics.Vector3 Centre, string Mats)>();
            foreach (var o in scene.Objects)
            {
                if (!scene.Definitions.TryGetValue(o.Definition, out var def)) continue;
                foreach (var v in def.NearestLod())
                {
                    if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh)) continue;
                    var lo = new System.Numerics.Vector3(float.MaxValue); var hi = new System.Numerics.Vector3(float.MinValue);
                    var m = v.NodeTransform * o.Transform;
                    foreach (var block in mesh.Blocks)
                        foreach (var p in block.Positions) { var w = System.Numerics.Vector3.Transform(p, m); lo = System.Numerics.Vector3.Min(lo, w); hi = System.Numerics.Vector3.Max(hi, w); }
                    if (lo.X > hi.X) continue;
                    big.Add((o.Name, o.Definition, v.MeshName, hi - lo, (hi + lo) / 2f, string.Join(",", mesh.Blocks.SelectMany(b => b.Materials).Select(r => r.Name).Distinct().Take(3))));
                }
            }
            int top = int.TryParse(rest.SkipWhile(a => a != "--top").Skip(1).FirstOrDefault(), out var t) ? t : 25;
            if (rest.SkipWhile(a => a != "--within").Skip(1).FirstOrDefault() is { } within && within.Split(',') is [var wx, var wy, var wr]
                && float.TryParse(wx, out var cx) && float.TryParse(wy, out var cy) && float.TryParse(wr, out var cr))
                big = big.Where(b => (b.Centre.X - cx) * (b.Centre.X - cx) + (b.Centre.Y - cy) * (b.Centre.Y - cy) <= cr * cr).ToList();
            foreach (var b in big.OrderByDescending(b => Math.Max(b.Size.X, Math.Max(b.Size.Y, b.Size.Z))).Take(top))
                Console.WriteLine($"{b.Def,-34} {b.Mesh,-34} size {b.Size.X,7:0}x{b.Size.Y,7:0}x{b.Size.Z,7:0} at {b.Centre.X,7:0},{b.Centre.Y,7:0},{b.Centre.Z,6:0}  {b.Mats}");
            return 0;
        }
        if (rest.Contains("--blend"))
        {
            // classic-maps-all: every material of the placed meshes by blend and cull mode and shader (which of them the client's material builder reads: BlendMode 8 only, CullMode 0), with the meshes that use it.
            var used = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in scene.Definitions.Values)
                foreach (var v in d.NearestLod())
                    if (scene.Meshes.TryGetValue(v.MeshName, out var mesh))
                        foreach (var block in mesh.Blocks)
                            foreach (var range in block.Materials)
                            {
                                if (!used.TryGetValue(range.Name, out var l)) used[range.Name] = l = [];
                                if (!l.Contains(v.MeshName, StringComparer.OrdinalIgnoreCase)) l.Add(v.MeshName);
                            }
            foreach (var g in used.Where(u => scene.Materials.ContainsKey(u.Key)).GroupBy(u => (scene.Materials[u.Key].BlendMode, scene.Materials[u.Key].CullMode, scene.Materials[u.Key].ShaderName)).OrderBy(g => g.Key.BlendMode).ThenBy(g => g.Key.CullMode))
            {
                if (!all && g.Key.BlendMode == 255 && g.Key.CullMode == 255) continue;
                Console.WriteLine($"blend {g.Key.BlendMode} cull {g.Key.CullMode} shader {g.Key.ShaderName}: {g.Count()} materials");
                foreach (var u in g.OrderBy(u => u.Key).Take(12)) Console.WriteLine($"    {u.Key} (meshes {string.Join(",", u.Value.Take(3))}{(u.Value.Count > 3 ? $",+{u.Value.Count - 3}" : "")})");
            }
            return 0;
        }
        if (rest.SkipWhile(a => a != "--material").Skip(1).FirstOrDefault() is { } materials)
        {
            // classic-maps-all: every field of the named materials (shader, textures, floats, vectors, blend and cull mode), to see why a surface draws flat or bright.
            foreach (var name in materials.Split(','))
            {
                if (!scene.Materials.TryGetValue(name, out var m)) { Console.WriteLine($"{name}: not in the scene"); continue; }
                Console.WriteLine($"{name}: shader {m.ShaderName}; blend {m.BlendMode}, cull {m.CullMode}");
                foreach (var (k, v) in m.Textures) Console.WriteLine($"    texture {k} = {v}");
                foreach (var (k, v) in m.Floats) Console.WriteLine($"    float {k} = {v}");
                foreach (var (k, v) in m.Vectors) Console.WriteLine($"    vector {k} = {string.Join("/", v.Select(x => x.ToString("0.###")))}");
            }
            return 0;
        }
        if (rest.SkipWhile(a => a != "--where").Skip(1).FirstOrDefault() is { } where)
        {
            var names = where.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var o in scene.Objects.Where(o => names.Contains(o.Definition)).OrderBy(o => o.Definition))
                Console.WriteLine($"{o.Definition,-24} {o.Transform.Translation.X,9:0.0} {o.Transform.Translation.Y,9:0.0} {o.Transform.Translation.Z,8:0.0}  layer {o.Layer}");
            return 0;
        }
        var placements = scene.Objects.GroupBy(o => o.Definition, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        // mesh -> definitions that draw it (nearest LOD, as the client does)
        var meshDefs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in scene.Definitions.Values)
            foreach (var v in d.NearestLod())
            {
                if (!meshDefs.TryGetValue(v.MeshName, out var l)) meshDefs[v.MeshName] = l = [];
                if (!l.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) l.Add(d.Name);
            }
        var rows = new List<(string Material, string Tint, string Kind, string Mesh, int Block, string Defs, int Count)>();
        foreach (var (meshName, mesh) in scene.Meshes)
        {
            if (!meshDefs.TryGetValue(meshName, out var defs)) continue;
            int count = defs.Sum(d => placements.GetValueOrDefault(d));
            if (count == 0) continue;
            for (int b = 0; b < mesh.Blocks.Count; b++)
                foreach (var range in mesh.Blocks[b].Materials)
                {
                    if (!scene.Materials.TryGetValue(range.Name, out var bmat) || !bmat.Textures.ContainsKey("EmissiveMap")) continue;
                    var c = bmat.Vectors.TryGetValue("EmissiveColor", out var ec) && ec.Length >= 3 ? ec : [1f, 1f, 1f];
                    string kind = c[0] >= 0.35f && c[0] >= 2f * c[1] && c[0] >= 2f * c[2] ? "SALMON" : c[1] > c[0] * 1.5f && c[1] > c[2] * 1.5f ? "green" : c[0] > c[2] * 2f && c[1] > c[2] * 2f && c[2] < 0.15f ? "olive/yellow" : "other";
                    if (!all && kind != "SALMON") continue;
                    rows.Add((range.Name, $"{c[0]:0.00}/{c[1]:0.00}/{c[2]:0.00}", kind, meshName, b, string.Join(",", defs.OrderBy(d => d)), count));
                }
        }
        foreach (var g in rows.GroupBy(r => (r.Material, r.Tint, r.Kind)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Material))
        {
            Console.WriteLine($"{g.Key.Kind,-12} {g.Key.Material}  emissive {g.Key.Tint}  placements {g.Sum(r => r.Count)}");
            foreach (var r in g.OrderBy(r => r.Mesh)) Console.WriteLine($"    mesh {r.Mesh}[{r.Block}]  defs {r.Defs}  placements {r.Count}");
        }
        Console.WriteLine($"{rows.Select(r => r.Material).Distinct().Count()} materials, {rows.Count} mesh surfaces listed (map {map})");
        return 0;
    }
}
