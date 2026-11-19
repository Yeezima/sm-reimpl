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
        if (rest.Contains("--classes"))
        {
            // hab-center-floor audit: every struct class of every layer of the map's region archive with its count (and the first few object names), to see which placed things the scene reader does not take
            // (it reads StaticObject, ScreenSpaceDecal and the PhoenixEntity after a BezierCurve only).
            string cpath = new[] { "wrld", "r000_main" }.Select(r => Path.Combine(gameDir, "data", "chunks", $"{map}__{r}.oppc")).First(File.Exists);
            using var carc = SmFormats.Obpk.ObpkArchive.Open(cpath);
            var counts = new SortedDictionary<string, (int N, List<string> Names)>(StringComparer.Ordinal);
            void Walk(SmFormats.Bod.BodValue v, string layer, int depth)
            {
                if (depth > 64) return;
                switch (v)
                {
                    case SmFormats.Bod.BodStruct s:
                    {
                        string key = $"{layer,-22} {s.Class.Text}" + (s["Disabled"] is SmFormats.Bod.BodBool { Value: true } ? " (disabled)" : "");
                        var cur = counts.GetValueOrDefault(key, (0, new List<string>()));
                        if (cur.Item2.Count < 4) cur.Item2.Add(((s["ObjectName"] ?? s["BlueprintName"] ?? s["Name"]) as SmFormats.Bod.BodString)?.Text ?? "");
                        counts[key] = (cur.Item1 + 1, cur.Item2);
                        foreach (var f in s.Fields) Walk(f.Value, layer, depth + 1);
                        break;
                    }
                    case SmFormats.Bod.BodList l: foreach (var i in l.Items) Walk(i.Value, layer, depth + 1); break;
                    case SmFormats.Bod.BodRef r: Walk(r.Value, layer, depth + 1); break;
                }
            }
            carc.ReadEntries(e => carc.GroupName(e) == "Layer", (e, data) => { try { Walk(SmFormats.Bod.BodDocument.Read(data).Root, e.FileName, 0); } catch (Exception ex) { Console.WriteLine($"{e.FileName}: unreadable: {ex.Message}"); } });
            foreach (var (k, (n, names)) in counts) Console.WriteLine($"{k,-60} {n,5}  {string.Join(", ", names.Distinct())}");
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
        if (rest.Contains("--light-classes"))
        {
            // classic-maps-fidelity pass 2: per layer, the class names (exact spelling) of every object whose class mentions light, sun or environment, with counts: where the map's lights and environment regions are
            string lpath = new[] { "wrld", "r000_main" }.Select(r => Path.Combine(gameDir, "data", "chunks", $"{map}__{r}.oppc")).First(File.Exists);
            using var larc = SmFormats.Obpk.ObpkArchive.Open(lpath);
            larc.ReadEntries(e => larc.GroupName(e) == "Layer", (e, data) =>
            {
                var tally = new SortedDictionary<string, int>();
                void Walk(SmFormats.Bod.BodValue v, int depth)
                {
                    if (depth > 40) return;
                    switch (v)
                    {
                        case SmFormats.Bod.BodStruct s:
                            if (s.Class.Text is { } c && (c.Contains("light", StringComparison.OrdinalIgnoreCase) || c.Contains("sun", StringComparison.OrdinalIgnoreCase) || c.Contains("environment", StringComparison.OrdinalIgnoreCase)) && !c.EndsWith("Desc"))
                                tally[c] = tally.GetValueOrDefault(c) + 1;
                            foreach (var f in s.Fields) Walk(f.Value, depth + 1);
                            break;
                        case SmFormats.Bod.BodList l: foreach (var it in l.Items) Walk(it.Value, depth + 1); break;
                        case SmFormats.Bod.BodRef r: Walk(r.Value, depth + 1); break;
                    }
                }
                try { Walk(SmFormats.Bod.BodDocument.Read(data).Root, 0); } catch { return; }
                if (tally.Count > 0) Console.WriteLine($"{e.FileName,-20} {string.Join("  ", tally.Select(t => $"{t.Key}={t.Value}"))}");
            });
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
                if (!scene.TryGetDefinition(o, out var def)) continue;
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
                    if (!scene.TryGetDefinition(o, out var def)) continue;
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
                if (!scene.TryGetDefinition(o, out var def)) continue;
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
                if (o.Depth != 0 || !scene.TryGetDefinition(o, out var def)) continue;
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
        if (rest.Contains("--floor-audit"))
        {
            // hab-center-floor audit: walkable floor (an upward-facing triangle of the character collision, normal.Z > 0.5) with no drawn surface within 0.5 m of its height at the same X,Y.
            // Samples every --step m (default 2) over the box --box x0,y0,x1,y1 (default: the box of the placements); the drawn surfaces are every triangle of the placed parts'
            // nearest-LOD meshes (sky boxes left out). "bare": nothing drawn there; "cutout": only BlendMode 8/10 surfaces (alpha cut-outs); "ok": an opaque surface.
            var world = MapContent.Load(gameDir, map).ToCollisionWorld();
            float step = float.TryParse(rest.SkipWhile(a => a != "--step").Skip(1).FirstOrDefault(), out var st) ? st : 2f;
            const float cell = 8f;
            var grid = new Dictionary<(int, int), List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C, bool Cut, string Name)>>();
            var box = new[] { float.MaxValue, float.MaxValue, float.MinValue, float.MinValue };
            foreach (var o in scene.Objects)
            {
                var t = o.Transform.Translation; box[0] = Math.Min(box[0], t.X); box[1] = Math.Min(box[1], t.Y); box[2] = Math.Max(box[2], t.X); box[3] = Math.Max(box[3], t.Y);
                if (!scene.TryGetDefinition(o, out var def)) continue;
                foreach (var v in def.DrawnParts(false))
                {
                    if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh) || v.MeshId >= mesh.Blocks.Count) continue;
                    var block = mesh.Blocks[v.MeshId]; var m = v.NodeTransform * o.Transform;
                    foreach (var range in block.Materials)
                    {
                        var bm = scene.Materials.GetValueOrDefault(range.Name);
                        if (bm?.ShaderName is { } sh && sh.StartsWith("sky_", StringComparison.Ordinal)) continue;
                        bool cut = bm is not null && bm.BlendMode != 255;
                        int count = range.IndexCount - range.IndexCount % 3;
                        if (count < 3 || range.StartIndex < 0 || range.StartIndex + count > block.Indices.Length) continue;
                        for (int i = 0; i < count; i += 3)
                        {
                            int a = block.Indices[range.StartIndex + i], b = block.Indices[range.StartIndex + i + 1], c = block.Indices[range.StartIndex + i + 2];
                            if (a >= block.Positions.Length || b >= block.Positions.Length || c >= block.Positions.Length) continue;
                            var pa = System.Numerics.Vector3.Transform(block.Positions[a], m); var pb = System.Numerics.Vector3.Transform(block.Positions[b], m); var pc = System.Numerics.Vector3.Transform(block.Positions[c], m);
                            int x0 = (int)Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X)) / cell), x1 = (int)Math.Floor(Math.Max(pa.X, Math.Max(pb.X, pc.X)) / cell);
                            int y0 = (int)Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y)) / cell), y1 = (int)Math.Floor(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y)) / cell);
                            if ((x1 - x0 + 1) * (y1 - y0 + 1) > 400) continue;
                            for (int gx = x0; gx <= x1; gx++) for (int gy = y0; gy <= y1; gy++)
                            { if (!grid.TryGetValue((gx, gy), out var l)) grid[(gx, gy)] = l = []; l.Add((pa, pb, pc, cut, o.Definition + "/" + v.MeshName + "#" + v.MeshId + " " + range.Name)); }
                        }
                    }
                }
            }
            if (rest.SkipWhile(a => a != "--box").Skip(1).FirstOrDefault() is { } bx && bx.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is { Length: 4 } bv) box = bv;
            // 0 = ok (opaque), 1 = cutout only, 2 = bare
            var zr = (rest.SkipWhile(a => a != "--z").Skip(1).FirstOrDefault() ?? "-1000,1000").Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); float zLo = zr[0], zHi = zr[1];
            int ok = 0, cutN = 0, bare = 0;
            var ascii = new SortedDictionary<float, System.Text.StringBuilder>();
            var bareList = new List<System.Numerics.Vector3>();
            for (float y = box[1]; y <= box[3]; y += step)
            {
                var sb = new System.Text.StringBuilder();
                for (float x = box[0]; x <= box[2]; x += step)
                {
                    char ch = ' '; float z0 = 200f;
                    for (int level = 0; level < 8; level++)
                    {
                        var hit = world.Character.Raycast(new System.Numerics.Vector3(x, y, z0), -System.Numerics.Vector3.UnitZ, 400f);
                        if (hit is null) break;
                        z0 = hit.Value.Point.Z - 0.05f;
                        if (hit.Value.Normal.Z < 0.5f) continue;
                        float fz = hit.Value.Point.Z; if (fz < zLo || fz > zHi) continue; bool opaque = false, cutout = false;
                        if (grid.TryGetValue(((int)Math.Floor(x / cell), (int)Math.Floor(y / cell)), out var tris))
                            foreach (var (a, b, c, cut, name) in tris)
                            {
                                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y); if (Math.Abs(d) < 1e-9f) continue;
                                float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d, w = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d, r = 1 - u - w;
                                if (u < 0 || w < 0 || r < 0) continue;
                                if (rest.Contains("--probe")) Console.WriteLine($"  probe {x:0},{y:0} floor {fz:0.0}: tri z {u * a.Z + w * b.Z + r * c.Z:0.0} {name}{(cut ? " (cut-out)" : "")}");
                                if (Math.Abs(u * a.Z + w * b.Z + r * c.Z - fz) <= 0.5f) { if (cut) cutout = true; else opaque = true; }
                            }
                        char here = opaque ? '#' : cutout ? 'o' : '.';
                        if (here == '#') ok++; else if (here == 'o') cutN++; else { bare++; if (bareList.Count < 4000) bareList.Add(new System.Numerics.Vector3(x, y, fz)); }
                        if (ch == ' ' || here == '.' || (here == 'o' && ch == '#')) ch = here;   // the worst floor of the column
                    }
                    sb.Append(ch);
                }
                ascii[y] = sb;
            }
            Console.WriteLine($"{map} floor audit step {step} m over {box[0]:0},{box[1]:0} to {box[2]:0},{box[3]:0}: opaque {ok}, cut-out only {cutN}, bare {bare} (collision floor samples)");
            if (rest.Contains("--ascii")) foreach (var (y, sb) in ascii.Reverse()) Console.WriteLine($"{y,6:0} {sb}");
            foreach (var p in bareList.Take(rest.Contains("--all-bare") ? 4000 : 60)) Console.WriteLine($"  bare {p.X:0},{p.Y:0},{p.Z:0.0}");
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
                if (!scene.TryGetDefinition(o, out var def)) continue;
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
        if (rest.Contains("--lod-names"))
        {
            // classic-maps-fidelity review: every visual node name containing "lod" (any case) of the definitions the map places, split by whether LodFamily reads a suffix from it
            var placed = scene.Objects.Select(o => o.Definition).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = scene.Definitions.Values.Where(d => placed.Contains(d.Name)).SelectMany(d => d.Visuals.Select(v => v.RefNode)).Where(n => n.Contains("lod", StringComparison.OrdinalIgnoreCase)).ToList();
            var unmatched = names.Where(n => ObjectDefinition.LodFamily(n) is null).GroupBy(n => n).OrderBy(g => g.Key).ToList();
            Console.WriteLine($"{map}: {names.Count} node names with 'lod', {names.Count(n => ObjectDefinition.LodFamily(n) is not null)} read as family members, {unmatched.Sum(g => g.Count())} not read ({unmatched.Count} distinct){(unmatched.Count > 0 ? ": " + string.Join(", ", unmatched.Take(12).Select(g => g.Key)) : "")}");
            return 0;
        }
        if (rest.Contains("--lod-cover"))
        {
            // hab-center-floor: the family members the LOD-suffix family rule drops that MapScene.DrawnParts(keepUncovered) draws again (their better-ranked siblings do not cover their box), per definition
            // with the placements, triangles and the member's and the siblings' boxes; totals for the map.
            var counts3 = scene.Objects.GroupBy(o => o.Definition, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            int cDefs = 0, cPlaces = 0; long cTris = 0;
            foreach (var def in scene.Definitions.Values.Where(d => counts3.ContainsKey(d.Name)).OrderBy(d => d.Name))
            {
                var extra = scene.UncoveredFamilyMembers(def);
                if (extra.Count == 0) continue;
                cDefs++; cPlaces += counts3[def.Name];
                foreach (var v in def.Visuals.Where(extra.Contains))
                {
                    int tris = scene.Meshes.TryGetValue(v.MeshName, out var mesh) && v.MeshId < mesh.Blocks.Count ? mesh.Blocks[v.MeshId].Indices.Length / 3 : 0;
                    cTris += (long)tris * counts3[def.Name];
                    Console.WriteLine($"{def.Name} x{counts3[def.Name]}: {v.RefNode} id{v.MeshId} mask {v.LodLevel} tris {tris}");
                }
            }
            Console.WriteLine($"{map}: {cDefs} definitions, {cPlaces} placements get extra family members; {cTris} extra triangles in all");
            return 0;
        }
        if (rest.Contains("--lod-families"))
        {
            // classic-maps-fidelity pass 2: per definition, visual parts whose node names share a stem and differ only in a LOD suffix (LODH, LODM, LODC, LODL, LOD<n>); prints each family's parts with
            // mask, triangle count and local box, and totals (definitions, placements) for the map. Stem = node name with the suffix cut.
            var counts2 = scene.Objects.GroupBy(o => o.Definition, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
int defs = 0, places = 0; bool quiet = rest.Contains("--summary");
            foreach (var def in scene.Definitions.Values.Where(d => counts2.ContainsKey(d.Name)).OrderBy(d => d.Name))
            {
                var fam = def.Visuals.Select(v => (v, f: ObjectDefinition.LodFamily(v.RefNode))).Where(x => x.f is not null).GroupBy(x => x.f!.Value.Stem).Where(g => g.Select(x => x.f!.Value.Rank).Distinct().Count() > 1).ToList();
                if (fam.Count == 0) continue;
                defs++; places += counts2[def.Name];
                if (quiet) continue;
                Console.WriteLine($"{def.Name} x{counts2[def.Name]}");
                foreach (var g in fam)
                    foreach (var (v, _) in g)
                    {
                        string info = "mesh missing";
                        if (scene.Meshes.TryGetValue(v.MeshName, out var mesh))
                        {
                            var b = mesh.Blocks[Math.Min(v.MeshId, mesh.Blocks.Count - 1)];
                            var lo = new System.Numerics.Vector3(float.MaxValue); var hi = new System.Numerics.Vector3(float.MinValue);
                            foreach (var p in b.Positions) { var w = System.Numerics.Vector3.Transform(p, v.NodeTransform); lo = System.Numerics.Vector3.Min(lo, w); hi = System.Numerics.Vector3.Max(hi, w); }
                            info = $"tris {b.Materials.Sum(r => r.IndexCount / 3)} box {(hi - lo).X:0.#}x{(hi - lo).Y:0.#}x{(hi - lo).Z:0.#} at {(hi + lo).X / 2:0.#},{(hi + lo).Y / 2:0.#},{(hi + lo).Z / 2:0.#}";
                        }
                        Console.WriteLine($"    {v.RefNode,-40} id{v.MeshId} mask {v.LodLevel,3}  {info}");
                    }
            }
            Console.WriteLine($"{defs} definitions with LOD-suffix families, {places} placements (map {map})");
            // what the default rule (ObjectDefinition.HighestLod; --old-lod-rule is NearestLod) changes against the current NearestLod: per definition, parts dropped and parts added
            int changedDefs = 0, changedPlacements = 0, droppedDraws = 0, addedDraws = 0;
            foreach (var def in scene.Definitions.Values.Where(d => counts2.ContainsKey(d.Name)).OrderBy(d => d.Name))
            {
                var a = def.NearestLod().ToList(); var b = def.HighestLod().ToList();
                var dropped = a.Except(b).ToList(); var added = b.Except(a).ToList();
                if (dropped.Count == 0 && added.Count == 0) continue;
                changedDefs++; changedPlacements += counts2[def.Name]; droppedDraws += dropped.Count * counts2[def.Name]; addedDraws += added.Count * counts2[def.Name];
                if (!quiet) Console.WriteLine($"  B differs: {def.Name} x{counts2[def.Name]} drops [{string.Join(", ", dropped.Select(p => p.RefNode))}] adds [{string.Join(", ", added.Select(p => p.RefNode))}]");
            }
            Console.WriteLine($"B (HighestLod) differs from A (NearestLod) in {changedDefs} definitions, {changedPlacements} placements: {droppedDraws} part draws dropped, {addedDraws} added");
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
        if (rest.SkipWhile(a => a != "--placements").Skip(1).FirstOrDefault() is { } around && around.Split(',') is [var ax, var ay, var ar]
            && float.TryParse(ax, out var pcx) && float.TryParse(ay, out var pcy) && float.TryParse(ar, out var pcr))
        {
            // hab-center-floor audit: every placement whose position is within pcr metres (game X,Y) of the point: layer, definition, whether it resolves, the drawn parts and whether their meshes
            // loaded, each part's world box. Tells placements the scene lacks (no definition, no mesh) from ones it has but the view loses.
            foreach (var o in scene.Objects.Where(o => { var t = o.Transform.Translation; return (t.X - pcx) * (t.X - pcx) + (t.Y - pcy) * (t.Y - pcy) <= pcr * pcr; })
                         .OrderBy(o => o.Layer, StringComparer.Ordinal).ThenBy(o => o.Definition, StringComparer.Ordinal))
            {
                var t = o.Transform.Translation;
                if (!scene.TryGetDefinition(o, out var def)) { Console.WriteLine($"{o.Layer,-20} {o.Definition,-34} at {t.X,6:0},{t.Y,6:0},{t.Z,6:0}  NO DEFINITION"); continue; }
                var parts = def.DrawnParts(false).ToList();
                var desc = new List<string>();
                foreach (var v in parts)
                {
                    if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh)) { desc.Add($"{v.MeshName}:NO MESH"); continue; }
                    var lo = new System.Numerics.Vector3(float.MaxValue); var hi = new System.Numerics.Vector3(float.MinValue);
                    var m = v.NodeTransform * o.Transform;
                    if (v.MeshId < mesh.Blocks.Count) foreach (var p in mesh.Blocks[v.MeshId].Positions) { var w = System.Numerics.Vector3.Transform(p, m); lo = System.Numerics.Vector3.Min(lo, w); hi = System.Numerics.Vector3.Max(hi, w); }
                    var mats = v.MeshId < mesh.Blocks.Count ? string.Join("/", mesh.Blocks[v.MeshId].Materials.Select(r => $"{r.Name}[{scene.Materials.GetValueOrDefault(r.Name)?.BlendMode.ToString() ?? "?"},{scene.Materials.GetValueOrDefault(r.Name)?.CullMode.ToString() ?? "?"}]")) : "NO BLOCK";
                    desc.Add($"{v.MeshName}#{v.MeshId} lod{v.LodLevel} box {(hi - lo).X:0.#}x{(hi - lo).Y:0.#}x{(hi - lo).Z:0.#} z {lo.Z:0.#}..{hi.Z:0.#} {mats}");
                }
                Console.WriteLine($"{o.Layer,-20} {o.Definition,-34} at {t.X,6:0},{t.Y,6:0},{t.Z,6:0}  {(parts.Count == 0 ? "NO DRAWN PART" : string.Join(" | ", desc))}");
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
                if (!scene.TryGetDefinition(o, out var def)) continue;
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
        if (rest.Contains("--vertex-colours"))
        {
            // classic-maps-all: per material of the placed meshes, the vertex colour the client tints "vtxtint" shaders with: vertices, mean R/G/B/A, and the share whose RGB is not one grey
            // (spread above 0.12: a coloured tint, the cause of tinted-salmon metal), with the shader and the meshes. Read-only.
            var stats = new Dictionary<string, (long N, double R, double G, double B, double A, long Colour, List<string> Meshes)>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in scene.Definitions.Values)
                foreach (var v in d.NearestLod())
                    if (scene.Meshes.TryGetValue(v.MeshName, out var mesh))
                        foreach (var block in mesh.Blocks)
                            foreach (var range in block.Materials)
                            {
                                if (block.Colors.Length != block.Positions.Length) continue;
                                if (!stats.TryGetValue(range.Name, out var s)) s = (0, 0, 0, 0, 0, 0, []);
                                for (int i = 0; i < range.VertexCount && range.FirstVertex + i < block.Colors.Length; i++)
                                {
                                    var c = SmContent.VertexColour.Unpack(block.Colors[range.FirstVertex + i]);
                                    s.N++; s.R += c.X; s.G += c.Y; s.B += c.Z; s.A += c.W;
                                    if (Math.Max(c.X, Math.Max(c.Y, c.Z)) - Math.Min(c.X, Math.Min(c.Y, c.Z)) > 0.12f) s.Colour++;
                                }
                                if (!s.Meshes.Contains(v.MeshName, StringComparer.OrdinalIgnoreCase)) s.Meshes.Add(v.MeshName);
                                stats[range.Name] = s;
                            }
            foreach (var (name, s) in stats.OrderByDescending(p => p.Value.N == 0 ? 0 : (double)p.Value.Colour / p.Value.N))
            {
                if (s.N == 0 || !scene.Materials.TryGetValue(name, out var bm)) continue;
                Console.WriteLine($"{name,-26} {bm.ShaderName,-44} verts {s.N,7} rgb {s.R / s.N:0.00}/{s.G / s.N:0.00}/{s.B / s.N:0.00} a {s.A / s.N:0.00} coloured {100.0 * s.Colour / s.N,5:0}%  {string.Join(",", s.Meshes.Take(4))}");
            }
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
