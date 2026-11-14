using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool map-emissive &lt;game-dir|auto&gt; [--map mp_tdm_09] [--all] [--where def,def]</c>: every material of a map's placed meshes that has an <c>EmissiveMap</c>, with its
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
