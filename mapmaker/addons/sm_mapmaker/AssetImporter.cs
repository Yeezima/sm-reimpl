using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;
using static SmMapmaker.ModelImport;

namespace SmMapmakerEditor;

/// <summary>What <see cref="AssetImporter.Prepare"/> made: a self-contained .glb in a staging folder plus what the library needs to know about it.</summary>
public sealed record ImportResult(string GlbPath, string Name, string Id, Aabb Bounds, JsonObject Meta, string Summary, string From, string SourceSha);

/// <summary>
/// The Godot half of the Quixel / Fab import: a zip, a folder, an .fbx, .glb or .gltf becomes one self-contained .glb (meshes, textures, scale in the file)
/// that the library imports with the ordinary <c>import-asset</c> operation. What the files hold and which one to open is decided by the core's
/// <see cref="ModelImport"/> (tested without Godot). No prompts: anything that goes wrong comes back as a <see cref="MapException"/> whose message is the toast.
/// </summary>
public static class AssetImporter
{
    /// <summary>Textures larger than this are scaled down (the .glb embeds them; a 4K set would be tens of MB per asset). Ours.</summary>
    public const int MaxTextureSize = 2048;

    public static bool IsImportable(string path) =>
        Directory.Exists(path) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || IsModel(path);

    /// <summary>Unpacks / reads <paramref name="source"/>, loads the model, builds materials, fixes the scale and writes <paramref name="staging"/>/<c>id</c>.glb.</summary>
    public static async Task<ImportResult> Prepare(string source, string staging)
    {
        Directory.CreateDirectory(staging);
        string kind, workDir;
        IReadOnlyList<string> files;
        if (Directory.Exists(source))
        {
            kind = "folder"; workDir = source;
            files = await Task.Run(() => ListFolder(source));
        }
        else if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            kind = "zip"; workDir = Path.Combine(staging, "unzipped");
            files = await Task.Run(() => ExtractZip(source, workDir));
        }
        else if (IsModel(source))
        {
            kind = "file"; workDir = Path.GetDirectoryName(Path.GetFullPath(source))!;
            files = SiblingFiles(workDir, source);
        }
        else throw new MapException("bad-file", $"'{Path.GetFileName(source)}' is not a .zip, .fbx, .glb, .gltf or a folder.");

        ModelChoice? choice = kind == "file" ? new ModelChoice(Path.GetFileName(source), Path.GetExtension(source).TrimStart('.').ToLowerInvariant(), LodOf(source) ?? 0, []) : PickModel(files);
        if (choice is null) throw new MapException("bad-file", $"No .fbx, .glb or .gltf model in '{Path.GetFileName(source)}'.");
        string modelPath = Path.Combine(workDir, choice.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(modelPath)) throw new MapException("bad-file", $"The model '{choice.Path}' is missing.");
        if (!IsPlainUnder(workDir, modelPath)) throw new MapException("bad-file", $"'{choice.Path}' is a link or lies outside the folder; import the real file.");

        if (choice.Format is "glb" or "gltf") GltfReader.Inspect(modelPath);   // Draco / meshopt / broken JSON / missing companions: a clear message before Godot fails silently

        var root = LoadModel(modelPath, choice.Format);
        try
        {
            // Textures sit next to the model or in a Textures folder: the sets of every image in the download.
            var sets = GroupTextures(files);
            if (kind == "file")
            {
                // A lone model: only the texture sets named like it (Rock_LOD0.fbx and rock_4K_Albedo.png); another asset's pictures in the same folder are not its own.
                string key = new string(CleanStem(Path.GetFileNameWithoutExtension(choice.Path), source).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                sets = sets.Where(set => set.Values.Any(t => { string n = new string(t.Set.Where(char.IsLetterOrDigit).ToArray()); return n.Length > 0 && key.Length > 0 && (key.Contains(n) || n.Contains(key)); })).ToList();
            }
            // Reading, decoding and the per-pixel work need no Godot scene API: off the main thread, so the editor stays alive on a big download.
            var (prepared, warnings) = await Task.Run(() => PrepareImages(sets, workDir));
            foreach (var w in warnings) GD.PushWarning("Mapmaker import: " + w);
            var (used, flipped, materials, usedFiles) = ApplyTextures(root, prepared);

            var measured = ContentFactory.MeasureBounds(root);
            if (measured.Size.LengthSquared() < 1e-12f) throw new MapException("no-mesh", $"'{choice.Path}' loaded but holds no mesh to draw.");
            var guess = GuessUnits(LargestSide(new System.Numerics.Vector3(measured.Size.X, measured.Size.Y, measured.Size.Z)));
            if (guess.Scale != 1f) root.Scale *= guess.Scale;
            var bounds = ContentFactory.MeasureBounds(root);

            string stem = CleanStem(Path.GetFileNameWithoutExtension(choice.Path), source);
            string id = SmMapmaker.Json.Slug(stem); if (id.Length == 0) id = "asset";
            string glb = Path.Combine(staging, id + ".glb");
            void Own(Node n) { foreach (var c in n.GetChildren()) { c.Owner = root; Own(c); } }
            Own(root);   // the exporter writes the nodes the scene root owns
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromScene(root, state);
            if (err != Error.Ok) throw new MapException("convert-failed", $"Could not convert '{choice.Path}' to glTF ({err}).");
            err = doc.WriteToFilesystem(state, glb);
            if (err != Error.Ok || !File.Exists(glb)) throw new MapException("convert-failed", $"Could not write the converted model ({err}).");

            // What the download held (model and the textures used), so dropping the same download again is recognised even though the conversion is not byte-for-byte repeatable.
            string sourceSha = await Task.Run(() =>
            {
                using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                h.AppendData(File.ReadAllBytes(modelPath));
                foreach (var t in usedFiles.Order(StringComparer.Ordinal)) { h.AppendData(System.Text.Encoding.UTF8.GetBytes("\n" + t + "\n")); h.AppendData(File.ReadAllBytes(Path.Combine(workDir, t.Replace('/', Path.DirectorySeparatorChar)))); }
                return Convert.ToHexStringLower(h.GetHashAndReset());
            });
            var meta = new JsonObject
            {
                ["source"] = kind, ["format"] = choice.Format, ["model"] = choice.Path, ["lod"] = choice.Lod, ["skippedLods"] = choice.SkippedLods.Count,
                ["scale"] = Math.Round(guess.Scale, 6), ["units"] = guess.Why, ["textures"] = used, ["materials"] = materials, ["normalGreenFlipped"] = flipped,
                ["maxTexture"] = MaxTextureSize, ["sourceSha256"] = sourceSha,
            };
            string scale = guess.Scale == 1f ? "scale 1" : $"scale {guess.Scale:0.###} ({guess.Why})";
            string summary = $"{scale}, {used} texture{(used == 1 ? "" : "s")}" + (choice.Lod > 0 || choice.SkippedLods.Count > 0 ? $", LOD{choice.Lod}" : "");
            return new ImportResult(glb, stem, id, bounds, meta, summary, Path.GetFileName(source), sourceSha);
        }
        finally { root.Free(); }
    }

    /// <summary>The files beside a lone model: its folder's own files and those of folders named like textures (not the whole tree: it may be Downloads).</summary>
    static IReadOnlyList<string> SiblingFiles(string dir, string model)
    {
        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SafeWalk(false)))
            if ((IsModel(f) || IsImage(f)) && IsPlainUnder(dir, f)) list.Add(Path.GetFileName(f));
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SafeWalk(false)))
        {
            string n = Path.GetFileName(sub);
            if (!IsPlainUnder(dir, sub)) continue;
            if (n.Contains("tex", StringComparison.OrdinalIgnoreCase) || n.Contains("maps", StringComparison.OrdinalIgnoreCase))
                foreach (var f in Directory.EnumerateFiles(sub, "*", SafeWalk()).Take(500))
                    if (IsImage(f) && IsPlainUnder(dir, f)) list.Add(Path.GetRelativePath(dir, f).Replace('\\', '/'));
        }
        return list.Append(Path.GetFileName(model)).Distinct().ToList();
    }

    static string StripLod(string stem) => System.Text.RegularExpressions.Regex.Replace(stem, @"[_\-. ]?lod[_\-. ]?\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static string CleanStem(string stem, string source)
    {
        string s = StripLod(stem).Trim('_', '-', '.', ' ');
        if (s.Length == 0) s = Path.GetFileNameWithoutExtension(source);
        return s.Length == 0 ? "asset" : s;
    }

    static Node3D LoadModel(string path, string format)
    {
        string basePath = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        Node? scene;
        if (format == "fbx")
        {
            var doc = ClassDB.Instantiate("FBXDocument").As<GltfDocument>(); var state = ClassDB.Instantiate("FBXState").As<GltfState>();
            if (doc is null || state is null) throw new MapException("load-failed", "This Godot build has no FBX reader; use the glTF/GLB download.");
            var err = doc.AppendFromFile(path, state, 0, basePath);
            if (err != Error.Ok) throw new MapException("load-failed", $"Godot could not read the FBX '{Path.GetFileName(path)}' ({err}). Try the glTF/GLB download of the same asset.");
            scene = doc.GenerateScene(state);
        }
        else
        {
            var doc = new GltfDocument(); var state = new GltfState();
            var err = doc.AppendFromFile(path, state, 0, basePath);
            if (err != Error.Ok) throw new MapException("load-failed", $"Godot could not read '{Path.GetFileName(path)}' ({err}); the file may be damaged or use an unsupported extension.");
            scene = doc.GenerateScene(state);
        }
        return scene is Node3D root ? ContentFactory.ConvertImporterMeshes(root) : throw new MapException("load-failed", $"'{Path.GetFileName(path)}' has no 3D scene.");
    }

    // ---- materials ----

    /// <summary>One material's pictures, decoded and fixed (green flip, opacity into alpha, size cap) off the main thread; textures are made from them on the main thread.</summary>
    sealed class PreparedSet
    {
        public string Name = "";
        public readonly Dictionary<TextureRole, Image> Images = new();
        public readonly HashSet<string> Files = new();
        public bool Flipped, Cutout;
    }

    /// <summary>Decodes every texture of every set. A picture whose header states more than 8192 x 8192 pixels is refused before decoding; so is one that is a link or outside the folder.</summary>
    static (List<PreparedSet> Sets, List<string> Warnings) PrepareImages(IReadOnlyList<IReadOnlyDictionary<TextureRole, TextureInfo>> sets, string dir)
    {
        var result = new List<PreparedSet>(); var warnings = new List<string>();
        Image? Decode(TextureInfo t, PreparedSet p)
        {
            string path = Path.Combine(dir, t.File.Replace('/', Path.DirectorySeparatorChar));
            if (!IsPlainUnder(dir, path)) { warnings.Add($"texture '{t.File}' skipped: not a plain file inside the folder"); return null; }
            if (!TryImageSize(path, out int w, out int h)) { warnings.Add($"texture '{t.File}' skipped: unreadable header"); return null; }
            if ((long)w * h > MaxImagePixels) { warnings.Add($"texture '{t.File}' skipped: {w} x {h} pixels is over the {MaxImagePixels / (8192 * 8192)} x 8192 x 8192 limit"); return null; }
            var image = Image.LoadFromFile(path);
            if (image is null) { warnings.Add($"texture '{t.File}' could not be read"); return null; }
            p.Files.Add(t.File);
            if (image.IsCompressed()) image.Decompress();
            int big = Math.Max(image.GetWidth(), image.GetHeight());
            if (big > MaxTextureSize)
            {
                float k = MaxTextureSize / (float)big;
                image.Resize(Math.Max(1, (int)(image.GetWidth() * k)), Math.Max(1, (int)(image.GetHeight() * k)), Image.Interpolation.Lanczos);
            }
            return image;
        }
        foreach (var set in sets)
        {
            var p = new PreparedSet { Name = set.Values.FirstOrDefault()?.Set ?? "material" };
            foreach (var (role, info) in set)
            {
                if (role == TextureRole.Opacity) continue;
                var image = Decode(info, p);
                if (image is null) continue;
                if (role == TextureRole.Normal && NormalNeedsGreenFlip(info)) { FlipGreen(image); p.Flipped = true; }
                p.Images[role] = image;
            }
            if (set.TryGetValue(TextureRole.Opacity, out var op) && p.Images.TryGetValue(TextureRole.Albedo, out var albedo) && Decode(op, p) is { } alpha) { MergeAlpha(albedo, alpha); p.Cutout = true; }
            result.Add(p);
        }
        return (result, warnings);
    }

    static (int Textures, bool Flipped, int Materials, IReadOnlyCollection<string> Files) ApplyTextures(Node3D root, IReadOnlyList<PreparedSet> sets)
    {
        if (sets.Count == 0) return (0, false, 0, []);
        var built = new Dictionary<int, StandardMaterial3D>();
        var files = new HashSet<string>();
        bool flipped = false;
        int slot = 0;
        var perMaterial = new Dictionary<Material, int>();   // untextured materials in order of appearance: set picked by name, else round-robin

        StandardMaterial3D For(int set, string materialName)
        {
            if (built.TryGetValue(set, out var m)) return m;
            m = BuildMaterial(sets[set], materialName);
            files.UnionWith(sets[set].Files); flipped |= sets[set].Flipped;
            built[set] = m;
            return m;
        }

        foreach (var mi in AllMeshes(root))
        {
            if (mi.Mesh is not { } mesh) continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var current = mi.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s);
                if (current is BaseMaterial3D { AlbedoTexture: not null }) continue;   // the model brings its own pictures
                if (current is ShaderMaterial) continue;
                int set = 0;
                if (sets.Count > 1)
                {
                    string name = current?.ResourceName ?? "";
                    set = Enumerable.Range(0, sets.Count).FirstOrDefault(i => sets[i].Name.Length > 0 && name.Contains(sets[i].Name, StringComparison.OrdinalIgnoreCase), -1);
                    if (set < 0)
                    {
                        if (current is not null && perMaterial.TryGetValue(current, out var known)) set = known;
                        else { set = slot++ % sets.Count; if (current is not null) perMaterial[current] = set; }
                    }
                }
                var mat = For(set, current?.ResourceName ?? "");
                if (mesh is ArrayMesh am) am.SurfaceSetMaterial(s, mat); else mi.SetSurfaceOverrideMaterial(s, mat);
            }
        }
        return (files.Count, flipped, built.Count, files);
    }

    static IEnumerable<MeshInstance3D> AllMeshes(Node n)
    {
        if (n is MeshInstance3D mi) yield return mi;
        foreach (var c in n.GetChildren()) foreach (var m in AllMeshes(c)) yield return m;
    }

    static StandardMaterial3D BuildMaterial(PreparedSet p, string name)
    {
        var mat = new StandardMaterial3D { ResourceName = name.Length > 0 ? name : p.Name };
        Texture2D? Tex(TextureRole r) => p.Images.TryGetValue(r, out var image) ? ImageTexture.CreateFromImage(image) : null;
        if (Tex(TextureRole.Albedo) is { } albedo) { mat.AlbedoTexture = albedo; if (p.Cutout) mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; }
        if (Tex(TextureRole.Normal) is { } nt) { mat.NormalEnabled = true; mat.NormalTexture = nt; }
        if (Tex(TextureRole.Orm) is { } ot)
        {
            mat.AOEnabled = true; mat.AOTexture = ot; mat.AOTextureChannel = BaseMaterial3D.TextureChannel.Red;
            mat.RoughnessTexture = ot; mat.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green;
            mat.MetallicTexture = ot; mat.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Blue; mat.Metallic = 1f;
        }
        else
        {
            if (Tex(TextureRole.Roughness) is { } rt) { mat.RoughnessTexture = rt; mat.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Red; }
            if (Tex(TextureRole.Metallic) is { } mt) { mat.MetallicTexture = mt; mat.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Red; mat.Metallic = 1f; }
            if (Tex(TextureRole.Occlusion) is { } at) { mat.AOEnabled = true; mat.AOTexture = at; mat.AOTextureChannel = BaseMaterial3D.TextureChannel.Red; }
        }
        if (Tex(TextureRole.Emissive) is { } et) { mat.EmissionEnabled = true; mat.EmissionTexture = et; }
        return mat;
    }

    /// <summary>DirectX normal maps point green down; Godot (and glTF) want it up: invert the green channel.</summary>
    static void FlipGreen(Image image)
    {
        image.Convert(Image.Format.Rgba8);
        var data = image.GetData();
        for (int i = 1; i < data.Length; i += 4) data[i] = (byte)(255 - data[i]);
        image.SetData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, data);
    }

    /// <summary>The opacity map's red channel becomes the albedo's alpha.</summary>
    static void MergeAlpha(Image albedo, Image opacity)
    {
        albedo.Convert(Image.Format.Rgba8);
        if (opacity.GetSize() != albedo.GetSize()) opacity.Resize(albedo.GetWidth(), albedo.GetHeight());
        opacity.Convert(Image.Format.Rgba8);
        var a = albedo.GetData(); var o = opacity.GetData();
        for (int i = 3; i < a.Length; i += 4) a[i] = o[i - 3];
        albedo.SetData(albedo.GetWidth(), albedo.GetHeight(), false, Image.Format.Rgba8, a);
    }
}
