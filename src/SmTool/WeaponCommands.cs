using System.Numerics;
using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool weapon-parts [filter] [--game &lt;dir&gt;] [--rig marine|ork] [--markers]</c>: prints the parts (object-tree node, mesh block, visibility, triangles, bounds in the
/// weapon's root space) and the locators of every held multiplayer weapon model whose wargear name contains the filter (test aid for
/// the reload magazines, M35). Read-only against the install.
/// </summary>
static class WeaponCommands
{
    /// <summary>Connected shells of a block (triangles sharing a position), largest first: triangles and bounds in root space.</summary>
    static List<(int Triangles, Vector3 Low, Vector3 High)> Components(SmFormats.Dcm.DcmBlock block, Matrix4x4 toRoot)
    {
        var weld = new Dictionary<(int, int, int), int>();
        var parent = new List<int>();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        int Id(Vector3 p)
        {
            var key = ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f));
            if (!weld.TryGetValue(key, out int id)) { id = parent.Count; parent.Add(id); weld[key] = id; }
            return id;
        }
        var triangles = new List<(int A, int B, int C)>();
        for (int i = 0; i + 2 < block.Indices.Length; i += 3)
        {
            int a = Id(block.Positions[block.Indices[i]]), b = Id(block.Positions[block.Indices[i + 1]]), c = Id(block.Positions[block.Indices[i + 2]]);
            triangles.Add((a, b, c));
            parent[Find(a)] = Find(b); parent[Find(b)] = Find(c);
        }
        var shells = new Dictionary<int, (int N, Vector3 Lo, Vector3 Hi)>();
        var lookup = new Dictionary<int, Vector3>();
        foreach (var p in block.Positions) lookup[Id(p)] = Vector3.Transform(p, toRoot);
        foreach (var (a, _, _) in triangles)
        {
            int root = Find(a);
            var at = lookup[a];
            shells[root] = shells.TryGetValue(root, out var s) ? (s.N + 1, Vector3.Min(s.Lo, at), Vector3.Max(s.Hi, at)) : (1, at, at);
        }
        return shells.Values.OrderByDescending(x => x.N).Select(x => (x.N, x.Lo, x.Hi)).ToList();
    }

    public static int Parts(string[] args)
    {
        string? filter = args.Where((a, i) => !a.StartsWith("--") && !(i > 0 && args[i - 1] is "--game" or "--rig")).FirstOrDefault();
        int g = Array.IndexOf(args, "--game");
        string gameDir = GameInstall.Locate(g >= 0 && g + 1 < args.Length ? args[g + 1] : null);
        var notes = new List<string>();
        int mi = Array.IndexOf(args, "--model");
        if (mi >= 0 && mi + 1 < args.Length)
        {
            // One model by package/visual (e.g. the Ork's held stikkbomb: stikkbomb_stormboy/stikkbomb_stormboy), not through a wargear file.
            var parts = args[mi + 1].Split('/');
            var one = WeaponModel.Load(Path.Combine(gameDir, "data", "packages", parts[0] + ".oppc"), parts.Length > 1 ? parts[1] : parts[0], null, notes);
            foreach (var v in one.Tree.Visuals) Console.WriteLine($"visual {v.Class} node '{v.RefNode}' mesh '{v.MeshName}' id {v.MeshId} lod {v.LodLevel} renderable {v.Renderable}");
            foreach (var part in one.Parts)
            {
                var block = one.Model.Mesh.Blocks[part.BlockIndex];
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                foreach (var p in block.Positions) { var w = Vector3.Transform(p, part.ToRoot); min = Vector3.Min(min, w); max = Vector3.Max(max, w); }
                Console.WriteLine($"part '{part.Node}' block {part.BlockIndex} {(part.Visible ? "visible" : "hidden: " + part.HiddenBecause)} {block.Indices.Length / 3} tris, bounds ({min.X:F3} {min.Y:F3} {min.Z:F3}) to ({max.X:F3} {max.Y:F3} {max.Z:F3})");
            }
            foreach (var l in one.Locators) Console.WriteLine($"locator '{l.Name}' at ({l.Position.X:F3} {l.Position.Y:F3} {l.Position.Z:F3})");
            foreach (var n in notes.Take(20)) Console.Error.WriteLine("note: " + n);
            return 0;
        }
        int r = Array.IndexOf(args, "--rig");
        var rig = r >= 0 && r + 1 < args.Length ? RigProfiles.ByName(args[r + 1]) ?? throw new ArgumentException($"unknown rig '{args[r + 1]}' (marine, ork)") : RigProfiles.Marine;
        var models = WeaponModels.Load(gameDir, rig, notes);
        if (args.Contains("--markers"))
        {
            foreach (var m in models.BodyMarkers.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"marker '{m.Name}' on bone '{m.Bone}' at ({m.Local.M41:F3} {m.Local.M42:F3} {m.Local.M43:F3})");
            foreach (var b in rig.LoadCharacter(gameDir).Skeleton.BoneNames)
                Console.WriteLine($"bone '{b}'");
        }
        foreach (var (wargear, attachments) in models.ByWargear.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (filter is not null && !wargear.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var attachment in attachments)
            {
                Console.WriteLine($"== {wargear} / {attachment.Name}: {attachment.VisualPackage} / {attachment.VisualName} slot {attachment.Slot}{(attachment.IsWorn ? " (worn)" : "")} wield {attachment.Wield?.HandleNode}->{attachment.Wield?.BodyNode}");
                try
                {
                    var model = WeaponModel.Load(gameDir, attachment, notes);
                    if (args.Contains("--markers"))
                        foreach (var v in model.Tree.Visuals)
                            Console.WriteLine($"  visual {v.Class} node '{v.RefNode}' mesh '{v.MeshName}' id {v.MeshId} lod {v.LodLevel} renderable {v.Renderable}");
                    foreach (var part in model.Parts)
                    {
                        var block = model.Model.Mesh.Blocks[part.BlockIndex];
                        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                        foreach (var p in block.Positions) { var w = Vector3.Transform(p, part.ToRoot); min = Vector3.Min(min, w); max = Vector3.Max(max, w); }
                        if (args.Contains("--components") && part.Visible)
                            foreach (var (tris, lo, hi) in Components(block, part.ToRoot))
                                Console.WriteLine($"    shell {tris} tris, bounds ({lo.X:F3} {lo.Y:F3} {lo.Z:F3}) to ({hi.X:F3} {hi.Y:F3} {hi.Z:F3})");
                        Console.WriteLine($"  part '{part.Node}' block {part.BlockIndex}{(args.Contains("--markers") ? $" lod {model.Tree.Visuals.FirstOrDefault(v => v.RefNode == part.Node && v.MeshId == part.BlockIndex)?.LodLevel}" : "")} {(part.Visible ? "visible" : "hidden: " + part.HiddenBecause)}{(part.Skinned ? " skinned" : "")}{(part.Simulated ? " simulated" : "")} " +
                                          $"{block.Indices.Length / 3} tris, root-space bounds ({min.X:F3} {min.Y:F3} {min.Z:F3}) to ({max.X:F3} {max.Y:F3} {max.Z:F3})");
                    }
                    if (args.Contains("--markers") && attachment.Wield is { } wield)
                    {
                        // The model's own axes in the mount bone's frame (the bone's X is along the bone): where its length (+Y of the root) and sides point.
                        var toBone = model.RootToBone(wield);
                        foreach (var (axis, label) in new[] { (Vector3.UnitX, "root +X"), (Vector3.UnitY, "root +Y"), (Vector3.UnitZ, "root +Z") })
                        {
                            var d = Vector3.TransformNormal(axis, toBone);
                            Console.WriteLine($"  {label} in the {wield.Bone} frame: ({d.X:F2} {d.Y:F2} {d.Z:F2})");
                        }
                    }
                    foreach (var l in model.Locators)
                        Console.WriteLine($"  locator '{l.Name}' at ({l.Position.X:F3} {l.Position.Y:F3} {l.Position.Z:F3})");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException)
                {
                    Console.WriteLine($"  not readable: {ex.Message}");
                }
            }
        }
        foreach (var n in notes.Take(20)) Console.Error.WriteLine("note: " + n);
        return 0;
    }
}
