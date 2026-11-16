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
        if (rest.SkipWhile(a => a != "--bod").Skip(1).FirstOrDefault() is { } bodArg)
        {
            // classic-maps-fidelity: prints one entry of the map's region archive as a tree, `--bod Layer:e_walls` or `--bod O3d:column_b` (group:file name); --depth n (default 6), --list-limit n (default 6) items per list
            int maxDepth = int.TryParse(rest.SkipWhile(a => a != "--depth").Skip(1).FirstOrDefault(), out var md) ? md : 6;
            int listLimit = int.TryParse(rest.SkipWhile(a => a != "--list-limit").Skip(1).FirstOrDefault(), out var ll) ? ll : 6;
            string suffix = rest.SkipWhile(a => a != "--archive").Skip(1).FirstOrDefault(); string[] regions = suffix is null ? ["wrld", "r000_main"] : [suffix]; string path = regions.Select(r => Path.Combine(gameDir, "data", "chunks", $"{map}__{r}.oppc")).First(File.Exists);
            using var arc = SmFormats.Obpk.ObpkArchive.Open(path);
            var parts = bodArg.Split(':');
            arc.ReadEntries(e => arc.GroupName(e).Equals(parts[0], StringComparison.OrdinalIgnoreCase) && e.FileName.Equals(parts[1], StringComparison.OrdinalIgnoreCase), (e, data) =>
                { if (parts[0].Equals("lua", StringComparison.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Encoding.Latin1.GetString(data).Replace("\0", ".")); else Dump(SmFormats.Bod.BodDocument.Read(data).Root, 0, "", maxDepth, listLimit); });
            return 0;
        }
        if (rest.Contains("--visual-fields"))
        {
            // classic-maps-fidelity: every distinct value of the StaticMeshVisual fields LODType, FadeStartDistance, FadeEndDistance, DrawOrder, LODLevel, IsStatic, DecalReceiver and Renderable over the map's object
            // definitions, with how many visuals carry it and example definitions (the fields the client does not read: which of them matter?)
            string vpath = new[] { "wrld", "r000_main" }.Select(r => Path.Combine(gameDir, "data", "chunks", $"{map}__{r}.oppc")).First(File.Exists);
            using var varc = SmFormats.Obpk.ObpkArchive.Open(vpath);
            var seen = new SortedDictionary<string, (int N, List<string> Ex)>();
            varc.ReadEntries(e => varc.GroupName(e) == "O3d", (e, data) =>
            {
                try
                {
                    var root = SmFormats.Bod.BodDocument.Read(data).Root as SmFormats.Bod.BodStruct;
                    if (root?["Visuals"] is not SmFormats.Bod.BodList vl) return;
                    foreach (var it in vl.Items)
                        if (it.Value is SmFormats.Bod.BodStruct vs && vs.Class.Text == "StaticMeshVisual")
                            foreach (var f in vs.Fields.Where(f => f.Name.Text is "LODType" or "FadeStartDistance" or "FadeEndDistance" or "DrawOrder" or "IsStatic" or "DecalReceiver" or "Renderable" or "MotionBlur"))
                            {
                                string val = f.Value switch { SmFormats.Bod.BodInt i => i.Value.ToString(), SmFormats.Bod.BodFloat fl => fl.Value.ToString("0.#"), SmFormats.Bod.BodBool b => b.Value.ToString(), _ => "?" };
                                string key = $"{f.Name.Text}={val}"; var cur = seen.GetValueOrDefault(key, (0, new List<string>()));
                                if (cur.Item2.Count < 4) cur.Item2.Add(e.FileName);
                                seen[key] = (cur.Item1 + 1, cur.Item2);
                            }
                }
                catch { }
            });
            foreach (var (k, (n, ex)) in seen) Console.WriteLine($"{k,-28} {n,5}  {string.Join(", ", ex)}");
            return 0;
        }
        if (rest.Contains("--map-entry"))
        {
            // classic-maps-fidelity: the install's multiplayer_map entry of this map (front-end data: objectives per game type, layer lists if any), as a BAF tree
            using var pa = SmFormats.Obpk.ObpkArchive.Open(Path.Combine(gameDir, "data", "packages", "multiplayer_map.oppc"));
            pa.ReadEntries(e => pa.GroupName(e) == "attr_pc", (e, data) =>
            {
                var doc = SmFormats.Baf.BafDocument.Read(data);
                if (doc.Root.String("map_name") == map) Console.WriteLine(SmTool.Dump.BafDump.Format(doc));
            });
            return 0;
        }
        if (rest.Contains("--archives"))
        {
            // classic-maps-fidelity: the map's archives in data/chunks (file names and sizes only) and, for each, its entries by group (count), and with --entries every entry name of the groups not Layer/dcm/dds/bmat/O3d
            foreach (var f in Directory.GetFiles(Path.Combine(gameDir, "data", "chunks"), $"{map}*.oppc").Order())
            {
                Console.WriteLine($"{Path.GetFileName(f)} {new FileInfo(f).Length} bytes");
                using var a = SmFormats.Obpk.ObpkArchive.Open(f);
                var groups = new SortedDictionary<string, List<string>>();
                a.ReadEntries(e => false, (e, d) => { });
                foreach (var e in a.Entries) { var g = a.GroupName(e); if (!groups.TryGetValue(g, out var l)) groups[g] = l = []; l.Add(e.FileName); }
                foreach (var (g, l) in groups)
                    Console.WriteLine($"    {g,-12} {l.Count,5}  {(l.Count <= 40 && g is not ("dcm" or "dds" or "bmat" or "O3d") ? string.Join(",", l) : "")}");
            }
            return 0;
        }
        if (rest.Contains("--flags"))
        {
            // classic-maps-fidelity: per layer, how many StaticObjects have each boolean flag set (Sky, DisableCollision, DisableShadow, DisableWorldOcclusion, Instanced...) and the layer's package names
            string fpath = new[] { "wrld", "r000_main" }.Select(r => Path.Combine(gameDir, "data", "chunks", $"{map}__{r}.oppc")).First(File.Exists);
            using var farc = SmFormats.Obpk.ObpkArchive.Open(fpath);
            farc.ReadEntries(e => farc.GroupName(e) == "Layer", (e, data) =>
            {
                var tally = new SortedDictionary<string, int>(); var pkgs = new SortedDictionary<string, int>(); int total = 0;
                void Walk(SmFormats.Bod.BodValue v, int depth)
                {
                    if (depth > 40) return;
                    switch (v)
                    {
                        case SmFormats.Bod.BodStruct s:
                            if (s.Class.Text == "StaticObject")
                            {
                                total++;
                                foreach (var f in s.Fields) if (f.Value is SmFormats.Bod.BodBool { Value: true } && f.Name.Text is { } n && n != "Disabled" || f.Value is SmFormats.Bod.BodBool { Value: true } && f.Name.Text == "Disabled") tally[f.Name.Text!] = tally.GetValueOrDefault(f.Name.Text!) + 1;
                                if ((s["PackageName"] as SmFormats.Bod.BodString)?.Text is { } pk) pkgs[pk] = pkgs.GetValueOrDefault(pk) + 1;
                            }
                            foreach (var f in s.Fields) Walk(f.Value, depth + 1);
                            break;
                        case SmFormats.Bod.BodList l: foreach (var it in l.Items) Walk(it.Value, depth + 1); break;
                        case SmFormats.Bod.BodRef r: Walk(r.Value, depth + 1); break;
                    }
                }
                try { Walk(SmFormats.Bod.BodDocument.Read(data).Root, 0); } catch { return; }
                Console.WriteLine($"{e.FileName,-22} {total,5} objects  flags {string.Join(" ", tally.Select(t => $"{t.Key}={t.Value}"))}  packages {string.Join(",", pkgs.Take(6).Select(p => $"{p.Key}x{p.Value}"))}");
            });
            return 0;
        }
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
        if (rest.SkipWhile(a => a != "--dump-texture").Skip(1).FirstOrDefault() is { } dump)
        {
            // maps-visual-qa: writes the named textures (as the scene read them) to --out <dir> and prints each DDS header (size, FourCC); for inspecting a format (the sky boxes' YCoCg); nothing enters the repo.
            string dir = rest.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? ".";
            Directory.CreateDirectory(dir);
            foreach (var name in dump.Split(','))
            {
                if (!scene.Textures.TryGetValue(name, out var bytes)) { Console.WriteLine($"{name}: not in the scene's textures"); continue; }
                File.WriteAllBytes(Path.Combine(dir, name + ".dds"), bytes);
                Console.WriteLine($"{name}: {bytes.Length} bytes, {BitConverter.ToInt32(bytes, 16)}x{BitConverter.ToInt32(bytes, 12)}, mips {BitConverter.ToInt32(bytes, 28)}, fourcc {System.Text.Encoding.ASCII.GetString(bytes, 84, 4)}");
            }
            return 0;
        }
        if (rest.SkipWhile(a => a != "--uv-range").Skip(1).FirstOrDefault() is { } uvMeshes)
        {
            // maps-visual-qa: per mesh block and material range, the position box and the UV set 0 / 1 ranges (for the sky boxes: which set carries the picture's mapping).
            foreach (var name in uvMeshes.Split(','))
            {
                if (!scene.Meshes.TryGetValue(name, out var m)) { Console.WriteLine($"{name}: not in the scene"); continue; }
                for (int b = 0; b < m.Blocks.Count; b++)
                {
                    var blk = m.Blocks[b];
                    foreach (var r in blk.Materials)
                    {
                        var idx = Enumerable.Range(r.StartIndex, r.IndexCount).Select(i => blk.Indices[i]).Distinct().ToList();
                        string Range(System.Numerics.Vector2[] uv) => uv.Length != blk.Positions.Length ? "none" : $"u {idx.Min(i => uv[i].X):0.###}..{idx.Max(i => uv[i].X):0.###} v {idx.Min(i => uv[i].Y):0.###}..{idx.Max(i => uv[i].Y):0.###}";
                        var lo = new System.Numerics.Vector3(idx.Min(i => blk.Positions[i].X), idx.Min(i => blk.Positions[i].Y), idx.Min(i => blk.Positions[i].Z));
                        var hi = new System.Numerics.Vector3(idx.Max(i => blk.Positions[i].X), idx.Max(i => blk.Positions[i].Y), idx.Max(i => blk.Positions[i].Z));
                        if (rest.Contains("--verts")) foreach (var i in idx) Console.WriteLine($"    v{i}: pos {blk.Positions[i].X:0},{blk.Positions[i].Y:0},{blk.Positions[i].Z:0} uv0 {(blk.Uv0.Length > i ? blk.Uv0[i].X : 0):0.##},{(blk.Uv0.Length > i ? blk.Uv0[i].Y : 0):0.##}");
                        Console.WriteLine($"{name}[{b}] {r.Name}: {idx.Count} vertices, box {lo.X:0}..{hi.X:0} x {lo.Y:0}..{hi.Y:0} x {lo.Z:0}..{hi.Z:0}; uv0 {Range(blk.Uv0)}; uv1 {Range(blk.Uv1)}");
                    }
                }
            }
            return 0;
        }
        if (rest.SkipWhile(a => a != "--objects").Skip(1).FirstOrDefault() is { } objArg && objArg.Split(',') is [var ox, var oy, var oz, var orad]
            && float.TryParse(ox, out var ocx) && float.TryParse(oy, out var ocy) && float.TryParse(oz, out var ocz) && float.TryParse(orad, out var ocr))
        {
            // classic-maps-fidelity: every placement whose drawn world box comes within <r> metres of x,y,z (game metres): definition, layer, nesting depth, placement name, the transform's
            // position and scale, and for each drawn part the mesh, its LOD mask, the world box and the material names. Finds what a view is made of.
            var centre = new System.Numerics.Vector3(ocx, ocy, ocz);
            foreach (var o in scene.Objects)
            {
                if (!scene.Definitions.TryGetValue(o.Definition, out var def)) continue;
                foreach (var v in def.Visuals)
                {
                    if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh)) continue;
                    var lo = new System.Numerics.Vector3(float.MaxValue); var hi = new System.Numerics.Vector3(float.MinValue);
                    var m = v.NodeTransform * o.Transform;
                    var block = mesh.Blocks[Math.Min(v.MeshId, mesh.Blocks.Count - 1)];
                    foreach (var p in block.Positions) { var w = System.Numerics.Vector3.Transform(p, m); lo = System.Numerics.Vector3.Min(lo, w); hi = System.Numerics.Vector3.Max(hi, w); }
                    if (lo.X > hi.X) continue;
                    var near = System.Numerics.Vector3.Clamp(centre, lo, hi);
                    if ((near - centre).Length() > ocr) continue;
                    var sc = new System.Numerics.Vector3(new System.Numerics.Vector3(o.Transform.M11, o.Transform.M12, o.Transform.M13).Length(), new System.Numerics.Vector3(o.Transform.M21, o.Transform.M22, o.Transform.M23).Length(), new System.Numerics.Vector3(o.Transform.M31, o.Transform.M32, o.Transform.M33).Length());
                    var t = o.Transform.Translation; var sz = hi - lo;
                    Console.WriteLine($"{o.Definition,-26} d{o.Depth} {o.Layer,-14} name {o.Name,-22} pos {t.X:0.0},{t.Y:0.0},{t.Z:0.0} scale {sc.X:0.##},{sc.Y:0.##},{sc.Z:0.##} | {v.MeshName} id{v.MeshId} node {v.RefNode} lod {v.LodLevel} box {sz.X:0.#}x{sz.Y:0.#}x{sz.Z:0.#} at {(lo.X + hi.X) / 2:0.#},{(lo.Y + hi.Y) / 2:0.#},{(lo.Z + hi.Z) / 2:0.#} | {string.Join(",", block.Materials.Select(r => r.Name).Distinct().Take(3))}");
                }
            }
            return 0;
        }
        if (rest.SkipWhile(a => a != "--col-variants").Skip(1).FirstOrDefault() is { } variantDefs)
        {
            // classic-maps-fidelity: for the named definitions, each placement's parts drawn four ways (node chain then placement as now; placement only; node translation only; node rotation and scale
            // only) and the share of each way's sampled vertices within 0.6 m of the character or weapon collision. The way whose share is highest is the one that matches the level's own collision.
            var cw = MapContent.Load(gameDir, map).ToCollisionWorld();
            var ds = new[] { System.Numerics.Vector3.UnitX, -System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, -System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, -System.Numerics.Vector3.UnitZ };
            if (variantDefs == "all")
            {
                // every part whose node transform is not the identity, aggregated by (definition, node): mean share per way over the placements; only (definition, node) pairs with at least 3 placements
                var agg = new Dictionary<(string, string), (int N, float[] Sum)>();
                foreach (var o in scene.Objects)
                {
                    if (!scene.Definitions.TryGetValue(o.Definition, out var def)) continue;
                    foreach (var v in def.NearestLod())
                    {
                        if (v.NodeTransform.IsIdentity || !scene.Meshes.TryGetValue(v.MeshName, out var mesh)) continue;
                        var block = mesh.Blocks[Math.Min(v.MeshId, mesh.Blocks.Count - 1)];
                        var nt = v.NodeTransform; var rotOnly = nt; rotOnly.Translation = System.Numerics.Vector3.Zero;
                        var variants = new[] { nt * o.Transform, o.Transform, System.Numerics.Matrix4x4.CreateTranslation(nt.Translation) * o.Transform, rotOnly * o.Transform };
                        int step = Math.Max(1, block.Positions.Length / 60);
                        var sh = variants.Select(m =>
                        {
                            int hit = 0, n = 0;
                            for (int i = 0; i < block.Positions.Length; i += step)
                            {
                                var w = System.Numerics.Vector3.Transform(block.Positions[i], m); n++;
                                foreach (var d in ds) if (cw.Character.Raycast(w, d, 0.6f) is not null || cw.Weapons.Raycast(w, d, 0.6f) is not null) { hit++; break; }
                            }
                            return n == 0 ? 0f : hit / (float)n;
                        }).ToArray();
                        var key = (o.Definition, v.RefNode);
                        var cur = agg.GetValueOrDefault(key, (0, new float[4]));
                        for (int k = 0; k < 4; k++) cur.Item2[k] += sh[k];
                        agg[key] = (cur.Item1 + 1, cur.Item2);
                    }
                }
                foreach (var ((d, node), (n, sum)) in agg.Where(a => a.Value.N >= 3).OrderByDescending(a => a.Value.N))
                    Console.WriteLine($"{d,-30} node {node,-26} x{n,3}: node+placement {sum[0] / n:0.00}  placement only {sum[1] / n:0.00}  node translation only {sum[2] / n:0.00}  node rot/scale only {sum[3] / n:0.00}");
                return 0;
            }
            var names = variantDefs.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var o in scene.Objects.Where(o => names.Contains(o.Definition)))
            {
                if (!scene.Definitions.TryGetValue(o.Definition, out var def)) continue;
                foreach (var v in def.Visuals)
                {
                    if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh)) continue;
                    var block = mesh.Blocks[Math.Min(v.MeshId, mesh.Blocks.Count - 1)];
                    var nt = v.NodeTransform;
                    var transOnly = System.Numerics.Matrix4x4.CreateTranslation(nt.Translation);
                    var rotOnly = nt; rotOnly.Translation = System.Numerics.Vector3.Zero;
                    var variants = new[] { nt * o.Transform, o.Transform, transOnly * o.Transform, rotOnly * o.Transform };
                    int step = Math.Max(1, block.Positions.Length / 150);
                    var shares = variants.Select(m =>
                    {
                        int hit = 0, n = 0;
                        for (int i = 0; i < block.Positions.Length; i += step)
                        {
                            var w = System.Numerics.Vector3.Transform(block.Positions[i], m); n++;
                            foreach (var d in ds) if (cw.Character.Raycast(w, d, 0.6f) is not null || cw.Weapons.Raycast(w, d, 0.6f) is not null) { hit++; break; }
                        }
                        return n == 0 ? 0 : hit / (float)n;
                    }).ToArray();
                    var t = o.Transform.Translation;
                    Console.WriteLine($"{o.Definition} {o.Layer} at {t.X:0},{t.Y:0},{t.Z:0} {v.MeshName} id{v.MeshId} node {v.RefNode}: node+placement {shares[0]:0.00}, placement only {shares[1]:0.00}, node translation only {shares[2]:0.00}, node rot/scale only {shares[3]:0.00}");
                }
            }
            return 0;
        }
        if (rest.Contains("--col-scale"))
        {
            // classic-maps-fidelity: placements with a non-uniform scale, drawn as now (scale in the object's own axes, then rotate: S*R*T) and the other way (rotate, then scale along the world axes: R*S*T),
            // with the share of sampled vertices within 0.6 m of the level collision for each. Aggregated over all such placements and printed per definition.
            var cw = MapContent.Load(gameDir, map).ToCollisionWorld();
            var ds = new[] { System.Numerics.Vector3.UnitX, -System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, -System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, -System.Numerics.Vector3.UnitZ };
            var agg = new Dictionary<string, (int N, float A, float B, System.Numerics.Vector3 Scale, System.Numerics.Vector3 Rot)>();
            foreach (var o in scene.Objects)
            {
                if (o.Depth != 0 || !scene.Definitions.TryGetValue(o.Definition, out var def)) continue;
                if (!System.Numerics.Matrix4x4.Decompose(o.Transform, out var sc, out var rot, out var tr)) continue;
                float lo = Math.Min(sc.X, Math.Min(sc.Y, sc.Z)), hi = Math.Max(sc.X, Math.Max(sc.Y, sc.Z));
                if (lo <= 0 || hi / lo < 1.1f) continue;
                var alt = System.Numerics.Matrix4x4.CreateFromQuaternion(rot) * System.Numerics.Matrix4x4.CreateScale(sc) * System.Numerics.Matrix4x4.CreateTranslation(tr);
                foreach (var v in def.NearestLod())
                {
                    if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh)) continue;
                    var block = mesh.Blocks[Math.Min(v.MeshId, mesh.Blocks.Count - 1)];
                    int step = Math.Max(1, block.Positions.Length / 60);
                    float Share(System.Numerics.Matrix4x4 m)
                    {
                        int hit = 0, n = 0;
                        for (int i = 0; i < block.Positions.Length; i += step)
                        {
                            var w = System.Numerics.Vector3.Transform(block.Positions[i], m); n++;
                            foreach (var d in ds) if (cw.Character.Raycast(w, d, 0.6f) is not null || cw.Weapons.Raycast(w, d, 0.6f) is not null) { hit++; break; }
                        }
                        return n == 0 ? 0f : hit / (float)n;
                    }
                    float a = Share(v.NodeTransform * o.Transform), b = Share(v.NodeTransform * alt);
                    var key = o.Definition + "|" + v.RefNode; var cur = agg.GetValueOrDefault(key, (0, 0f, 0f, sc, System.Numerics.Vector3.Zero));
                    agg[key] = (cur.Item1 + 1, cur.Item2 + a, cur.Item3 + b, sc, cur.Item5);
                }
            }
            foreach (var (k, (n, a, b, sc, _)) in agg.OrderByDescending(x => x.Value.N))
                Console.WriteLine($"{k,-52} x{n,3} scale e.g. {sc.X:0.##},{sc.Y:0.##},{sc.Z:0.##}: S*R*T {a / n:0.00}   R*S*T {b / n:0.00}");
            return 0;
        }
        if (rest.Contains("--col-audit"))
        {
            // classic-maps-fidelity: for every drawn placement (nearest LOD) with a large box, the share of sampled vertices that lie within 0.6 m of the level's character or weapon collision
            // (six axis rays of 0.6 m). Large drawn geometry with none near it is either decor with no collision (vistas, rubble) or placed wrong; the list is for finding the second kind.
            var world = MapContent.Load(gameDir, map).ToCollisionWorld();
            float minSize = float.TryParse(rest.SkipWhile(a => a != "--min-size").Skip(1).FirstOrDefault(), out var ms) ? ms : 6f;
            var dirs = new[] { System.Numerics.Vector3.UnitX, -System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, -System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, -System.Numerics.Vector3.UnitZ };
            var colRows = new List<(string Def, string Mesh, string Layer, float Share, System.Numerics.Vector3 Size, System.Numerics.Vector3 At, string Mats)>();
            foreach (var o in scene.Objects)
            {
                if (!scene.Definitions.TryGetValue(o.Definition, out var def)) continue;
                foreach (var v in def.NearestLod())
                {
                    if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh)) continue;
                    var block = mesh.Blocks[Math.Min(v.MeshId, mesh.Blocks.Count - 1)];
                    var m = v.NodeTransform * o.Transform;
                    var lo = new System.Numerics.Vector3(float.MaxValue); var hi = new System.Numerics.Vector3(float.MinValue);
                    foreach (var p in block.Positions) { var w = System.Numerics.Vector3.Transform(p, m); lo = System.Numerics.Vector3.Min(lo, w); hi = System.Numerics.Vector3.Max(hi, w); }
                    if (lo.X > hi.X) continue;
                    var size = hi - lo;
                    if (Math.Max(size.X, Math.Max(size.Y, size.Z)) < minSize) continue;
                    int step = Math.Max(1, block.Positions.Length / 120), hit = 0, n = 0;
                    for (int i = 0; i < block.Positions.Length; i += step)
                    {
                        var w = System.Numerics.Vector3.Transform(block.Positions[i], m); n++;
                        foreach (var d in dirs)
                            if (world.Character.Raycast(w, d, 0.6f) is not null || world.Weapons.Raycast(w, d, 0.6f) is not null) { hit++; break; }
                    }
                    colRows.Add((o.Definition, v.MeshName, o.Layer, n == 0 ? 0 : hit / (float)n, size, (hi + lo) / 2f, string.Join(",", block.Materials.Select(r => r.Name).Distinct().Take(3))));
                }
            }
            float maxShare = float.TryParse(rest.SkipWhile(a => a != "--max-share").Skip(1).FirstOrDefault(), out var mx) ? mx : 0.05f;
            foreach (var r in colRows.Where(r => r.Share <= maxShare).OrderByDescending(r => Math.Max(r.Size.X, Math.Max(r.Size.Y, r.Size.Z))))
                Console.WriteLine($"{r.Def,-30} {r.Layer,-18} share {r.Share:0.00} size {r.Size.X,5:0}x{r.Size.Y,5:0}x{r.Size.Z,5:0} at {r.At.X,6:0},{r.At.Y,6:0},{r.At.Z,5:0}  {r.Mats}");
            Console.WriteLine($"{colRows.Count} large parts, {colRows.Count(r => r.Share <= maxShare)} with at most {maxShare:0.00} of their vertices near collision");
            return 0;
        }
        if (rest.Contains("--lod-audit"))
        {
            // classic-maps-fidelity: definitions whose nearest-LOD selection (LODLevel bit 0, minus nodes named LODL) draws two or more parts with nearly the same local box (the same shape twice: a
            // low LOD drawn on top of the high one). Prints the mask, node, triangle count and box of each part and the definition's placement count.
            var counts = scene.Objects.GroupBy(o => o.Definition, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var def in scene.Definitions.Values.Where(d => counts.ContainsKey(d.Name)).OrderBy(d => d.Name))
            {
                var parts = def.NearestLod().Select(v =>
                {
                    if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh)) return (v, tris: 0, lo: System.Numerics.Vector3.Zero, hi: System.Numerics.Vector3.Zero, ok: false);
                    var b = mesh.Blocks[Math.Min(v.MeshId, mesh.Blocks.Count - 1)];
                    var lo = new System.Numerics.Vector3(float.MaxValue); var hi = new System.Numerics.Vector3(float.MinValue);
                    foreach (var p in b.Positions) { var w = System.Numerics.Vector3.Transform(p, v.NodeTransform); lo = System.Numerics.Vector3.Min(lo, w); hi = System.Numerics.Vector3.Max(hi, w); }
                    return (v, tris: b.Materials.Sum(r => r.IndexCount / 3), lo, hi, ok: lo.X <= hi.X);
                }).Where(p => p.ok).ToList();
                var flagged = new HashSet<int>();
                for (int i = 0; i < parts.Count; i++)
                    for (int j = i + 1; j < parts.Count; j++)
                    {
                        var lo = System.Numerics.Vector3.Max(parts[i].lo, parts[j].lo); var hi = System.Numerics.Vector3.Min(parts[i].hi, parts[j].hi);
                        var d = hi - lo; if (d.X <= 0 || d.Y <= 0 || d.Z <= 0) continue;
                        float inter = d.X * d.Y * d.Z;
                        float vi = (parts[i].hi - parts[i].lo).X * (parts[i].hi - parts[i].lo).Y * (parts[i].hi - parts[i].lo).Z, vj = (parts[j].hi - parts[j].lo).X * (parts[j].hi - parts[j].lo).Y * (parts[j].hi - parts[j].lo).Z;
                        if (inter >= 0.7f * Math.Min(vi, vj) && parts[i].v.MeshName == parts[j].v.MeshName || inter >= 0.7f * Math.Min(vi, vj) && Math.Min(parts[i].tris, parts[j].tris) * 2 < Math.Max(parts[i].tris, parts[j].tris))
                        { flagged.Add(i); flagged.Add(j); }
                    }
                if (flagged.Count == 0) continue;
                Console.WriteLine($"{def.Name} x{counts[def.Name]}");
                foreach (var i in flagged.Order()) { var p = parts[i]; Console.WriteLine($"    {p.v.MeshName} id{p.v.MeshId} node {p.v.RefNode} lod {p.v.LodLevel} tris {p.tris} box {(p.hi - p.lo).X:0.#}x{(p.hi - p.lo).Y:0.#}x{(p.hi - p.lo).Z:0.#}"); }
            }
            return 0;
        }
        if (rest.Contains("--layers"))
        {
            // classic-maps-fidelity: the placed objects per layer (layer name, placements, distinct definitions, the first few definitions by count)
            foreach (var g in scene.Objects.GroupBy(o => o.Layer, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
                Console.WriteLine($"{g.Key,-22} {g.Count(),6} placements {g.Select(o => o.Definition).Distinct().Count(),4} defs  nested {g.Count(o => o.Depth > 0),5}  e.g. {string.Join(", ", g.GroupBy(o => o.Definition).OrderByDescending(x => x.Count()).Take(6).Select(x => $"{x.Key}x{x.Count()}"))}");
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
                if (!all && g.Key.BlendMode == 255 && g.Key.CullMode == 255 && g.Key.ShaderName?.Contains("water", StringComparison.OrdinalIgnoreCase) != true && g.Key.ShaderName?.Contains("anim", StringComparison.OrdinalIgnoreCase) != true) continue;
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

    static void Dump(SmFormats.Bod.BodValue v, int depth, string label, int maxDepth, int listLimit)
    {
        string pad = new(' ', depth * 2);
        switch (v)
        {
            case SmFormats.Bod.BodStruct st:
                Console.WriteLine($"{pad}{label}{st.Class.Text} {{");
                if (depth >= maxDepth) { Console.WriteLine($"{pad}  ..."); Console.WriteLine($"{pad}}}"); break; }
                foreach (var f in st.Fields) Dump(f.Value, depth + 1, $"{f.Name.Text} = ", maxDepth, listLimit);
                Console.WriteLine($"{pad}}}");
                break;
            case SmFormats.Bod.BodList l:
                Console.WriteLine($"{pad}{label}list[{l.Items.Count}]");
                if (depth >= maxDepth) break;
                foreach (var it in l.Items.Take(listLimit)) Dump(it.Value, depth + 1, "- ", maxDepth, listLimit);
                break;
            case SmFormats.Bod.BodRef r: Dump(r.Value, depth, label + "&ref ", maxDepth, listLimit); break;
            case SmFormats.Bod.BodArray a: Console.WriteLine($"{pad}{label}[{string.Join(", ", a.Items.Take(8).Select(i => i is SmFormats.Bod.BodFloat f ? f.Value.ToString("0.###") : i.ToString()))}]"); break;
            case SmFormats.Bod.BodString s: Console.WriteLine($"{pad}{label}\"{s.Text}\""); break;
            case SmFormats.Bod.BodInt i: Console.WriteLine($"{pad}{label}{i.Value}"); break;
            case SmFormats.Bod.BodFloat f: Console.WriteLine($"{pad}{label}{f.Value:0.###}"); break;
            case SmFormats.Bod.BodBool b: Console.WriteLine($"{pad}{label}{b.Value}"); break;
            default: Console.WriteLine($"{pad}{label}{v.GetType().Name}"); break;
        }
    }
}
