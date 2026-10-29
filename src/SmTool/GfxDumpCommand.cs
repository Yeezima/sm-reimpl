using System.IO.Compression;
using SmContent;
using SmFormats.Dds;
using SmFormats.Gfx;
using SmFormats.Obpk;

namespace SmTool;

/// <summary>
/// <c>smtool gfx-dump &lt;game-dir|auto&gt; [--movie Hud] [--package ingame|common] [--grep word] [--frames sprite] [--render name [--frame n] [--scale s] [--all-frames] [--out dir]]</c>: the structure of a Scaleform
/// movie of <c>ui_ingame.oppc</c> (read-only): stage, every sprite's frame count and labels, every named placement (parent sprite, frame, character, position in
/// movie pixels, scale, blend, colour transform). <c>--render</c> draws a named placement's character (its frame <c>n</c>, or every frame) with the movie's
/// bitmaps into PNG files under <c>dir</c> (default: the private cache's <c>gfx-dump</c> folder; never inside the repository or the install). No action (ActionScript) is read.
/// </summary>
public static class GfxDumpCommand
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        string movieName = Opt(rest, "--movie") ?? "Hud";
        string package = Opt(rest, "--package") is "common" ? UiArt.CommonPackage : UiArt.IngamePackage;
        string? grep = Opt(rest, "--grep");
        string? render = Opt(rest, "--render");
        bool writeFiles = Opt(rest, "--out") is not null;
        string? outDir = null;
        if (writeFiles || render is not null)
        {
            // One guard for every file this command writes: never inside the repository or the install; by default the private cache.
            outDir = SafeOutput(Opt(rest, "--out"), gameDir, out string why);
            if (outDir is null) { Console.Error.WriteLine(why); return 2; }
        }
        int frameArg = int.TryParse(Opt(rest, "--frame"), out var fr) ? fr : -1;
        double scale = double.TryParse(Opt(rest, "--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1.0;
        bool allFrames = rest.Contains("--all-frames");

        byte[]? gfx = null;
        var atlases = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string prefix = movieName.ToLowerInvariant() + "_i";
        using (var stream = new FileStream(Path.Combine(gameDir, package), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var archive = ObpkArchive.Open(stream))
            archive.ReadEntries(e =>
            {
                string g = archive.GroupName(e);
                return (g == "gfx" && e.FileName.Equals(movieName, StringComparison.OrdinalIgnoreCase)) || (g == "dds" && e.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }, (e, d) => { if (archive.GroupName(e) == "gfx") gfx = d.ToArray(); else atlases[e.FileName] = d.ToArray(); });
        if (gfx is null) { Console.Error.WriteLine($"no movie '{movieName}' in {package}"); return 1; }
        var movie = GfxMovie.Parse(gfx);

        if (Opt(rest, "--path") is { } path)
        {
            // A placement by its path of instance names, rendered in the space of the path's first step (e.g. the reticle container) at --scale, nested sprites at --frame
            // (every nested sprite with that many frames or more; else its first): prints the picture's size, the first step's origin in it and the drawn box's centre.
            var hm = new HudMovie(movieName, movie, atlases);
            var steps = path.Split('/');
            var parent = hm.Find(steps[0]);
            var found = hm.Find(path);
            if (parent is null || found is null) { Console.Error.WriteLine($"no placement at '{path}'"); return 1; }
            var inv = parent.Value.ToStage.Inverse() ?? GfxMatrix.Identity;
            var local = steps.Length == 1 ? GfxMatrix.Identity : inv.After(found.Value.ToStage);
            int nf = frameArg < 0 ? 0 : frameArg;
            Func<int, int> nested = ch => movie.Sprites.TryGetValue(ch, out var s) && s.Frames.Count > nf ? nf : 0;
            int top = rest.Contains("--top-frame") ? nf : 0;
            var pic = steps.Length > 1 ? hm.RenderAt(path, steps[0], scale, top, nested) : hm.Render(found.Value.Placement.Character, top, scale, local, nested);
            if (pic is null) { Console.WriteLine("draws nothing"); return 0; }
            var b = GfxRaster.Bounds(movie, found.Value.Placement.Character, 0, local, ch => movie.Sprites.TryGetValue(ch, out var s) && s.Frames.Count > nf ? nf : 0, visibleOnly: true);
            Console.WriteLine($"{path}: {pic.Width} x {pic.Height} px at scale {scale}, origin ({pic.OriginX:0.##}, {pic.OriginY:0.##}); box (movie px, origin of '{steps[0]}') {b?.X0:0.##},{b?.Y0:0.##} .. {b?.X1:0.##},{b?.Y1:0.##}, centre ({(b?.X0 + b?.X1) / 2:0.##}, {(b?.Y0 + b?.Y1) / 2:0.##}); additive {(pic.Additive is null ? "no" : "yes")}");
            if (writeFiles && outDir is not null)
            {
                Directory.CreateDirectory(outDir);
                var px = new byte[pic.Width * pic.Height * 4];
                for (int i = 0; i < pic.Width * pic.Height; i++)
                {
                    float al = pic.Normal[i * 4 + 3] / 255f;
                    for (int k = 0; k < 3; k++) px[i * 4 + k] = (byte)Math.Clamp(40 * (1 - al) + pic.Normal[i * 4 + k] * al + (pic.Additive?[i * 4 + k] ?? 0), 0, 255);
                    px[i * 4 + 3] = 255;
                }
                string file = Path.Combine(outDir, $"{movieName}_{path.Replace('/', '.')}_f{nf:D3}.png");
                WritePng(file, pic.Width, pic.Height, px);
                Console.WriteLine(file);
            }
            return 0;
        }

        if (Opt(rest, "--frames") is { } framesOf)
        {
            // Every frame's display list of one sprite (by id or instance name): depth, character, position, scale, alpha.
            int id = int.TryParse(framesOf, out int fid) ? fid : movie.FindNamedPlacement(framesOf)?.Character ?? -1;
            if (!movie.Sprites.TryGetValue(id, out var spr)) { Console.Error.WriteLine($"no sprite '{framesOf}'"); return 1; }
            for (int i = 0; i < spr.Frames.Count; i++)
            {
                var f = spr.Frames[i];
                Console.WriteLine($"frame {i}{(f.Label is null ? "" : " '" + f.Label + "'")}:");
                foreach (var p in f.Display.Values)
                {
                    var m = p.Matrix ?? GfxMatrix.Identity;
                    Console.WriteLine($"  d{p.Depth} c{p.Character}{(movie.DefinedBy.TryGetValue(p.Character, out int tagc) ? "(tag " + tagc + ")" : "")}{(p.Name.Length > 0 ? " '" + p.Name + "'" : "")} at ({m.Tx / 20.0:0.##}, {m.Ty / 20.0:0.##}) m({m.A:0.###},{m.B:0.###},{m.C:0.###},{m.D:0.###}) alpha {p.Colour?.MulA ?? 1f:0.###}{(p.Colour is { } c && (c.AddR != 0 || c.AddG != 0 || c.AddB != 0 || c.MulR != 1) ? $" rgb mul {c.MulR:0.##},{c.MulG:0.##},{c.MulB:0.##} add {c.AddR:0.##},{c.AddG:0.##},{c.AddB:0.##}" : "")}{(p.Blend > 0 ? " blend " + p.Blend : "")}{(p.ClipDepth > 0 ? " clip " + p.ClipDepth : "")}");
                }
            }
            return 0;
        }

        if (render is null)
        {
            Console.WriteLine($"{movieName}.gfx: stage {movie.StageWidth} x {movie.StageHeight}, {movie.Sprites.Count} sprites, {movie.Shapes.Count} shapes, {movie.Images.Count} images, {movie.SubImages.Count} sub-images");
            foreach (var sprite in movie.Sprites.Values.Append(movie.Root).OrderBy(s => s.Id))
            {
                var labels = sprite.Frames.Select((f, i) => f.Label is null ? null : $"{i}:{f.Label}").Where(l => l is not null).ToList();
                var named = new List<string>();
                for (int i = 0; i < sprite.Frames.Count; i++)
                    foreach (var p in sprite.Frames[i].Display.Values)
                        if (p.Name.Length > 0 && (i == 0 || !sprite.Frames[i - 1].Display.TryGetValue(p.Depth, out var prev) || prev.Name != p.Name || prev.Character != p.Character))
                        {
                            var m = p.Matrix ?? GfxMatrix.Identity;
                            double sx = Math.Sqrt(m.A * m.A + m.B * m.B), sy = Math.Sqrt(m.C * m.C + m.D * m.D);
                            string col = p.Colour is { } c ? $" cx(mul {c.MulR:0.##},{c.MulG:0.##},{c.MulB:0.##},{c.MulA:0.##} add {c.AddR:0.##},{c.AddG:0.##},{c.AddB:0.##},{c.AddA:0.##})" : "";
                            string text = movie.EditTexts.TryGetValue(p.Character, out var et)
                                ? $" text[{et.X0:0.#},{et.Y0:0.#}..{et.X1:0.#},{et.Y1:0.#} font {et.FontId} '{movie.FontNames.GetValueOrDefault(et.FontId, "?")}' {et.FontHeight:0.#}px rgba({et.Colour.R * 255:0},{et.Colour.G * 255:0},{et.Colour.B * 255:0},{et.Colour.A * 255:0}) align {et.Align} '{(et.InitialText.Length > 40 ? et.InitialText[..40] : et.InitialText)}']"
                                : "";
                            named.Add($"  f{i} d{p.Depth} '{p.Name}' -> {(movie.Sprites.ContainsKey(p.Character) ? "sprite" : movie.Shapes.ContainsKey(p.Character) ? "shape" : "char")} {p.Character} at ({m.Tx / 20.0:0.##}, {m.Ty / 20.0:0.##}) scale ({sx:0.###}, {sy:0.###}) rot {Math.Atan2(m.B, m.A) * 180 / Math.PI:0.#}{(p.Blend > 0 ? $" blend {p.Blend}" : "")}{col}{text}");
                        }
                bool match = grep is null || named.Any(n => n.Contains(grep, StringComparison.OrdinalIgnoreCase)) || labels.Any(l => l!.Contains(grep, StringComparison.OrdinalIgnoreCase));
                if (!match) continue;
                Console.WriteLine($"sprite {sprite.Id}: {sprite.Frames.Count} frames{(labels.Count > 0 ? ", labels " + string.Join(" ", labels) : "")}");
                foreach (var n in named) if (grep is null || n.Contains(grep, StringComparison.OrdinalIgnoreCase) || labels.Count > 0) Console.WriteLine(n);
            }
            if (grep is null)
                foreach (var (id, im) in movie.Images.OrderBy(k => k.Key))
                    Console.WriteLine($"image {id}: {im.File} {im.Width} x {im.Height}{(atlases.ContainsKey(Path.GetFileNameWithoutExtension(im.File)) ? "" : " (not in the package)")}");
            if (grep is null)
                foreach (var (id, si) in movie.SubImages.OrderBy(k => k.Key))
                    Console.WriteLine($"sub-image {id}: image {si.Image} ({(movie.Images.TryGetValue(si.Image, out var im) ? im.File : "?")}) [{si.X0},{si.Y0} - {si.X1},{si.Y1}]");
            return 0;
        }

        string full = outDir!;
        Directory.CreateDirectory(full);
        int character;
        if (int.TryParse(render, out int byId)) character = byId;
        else if (movie.FindNamedPlacement(render) is { } placement) character = placement.Character;
        else { Console.Error.WriteLine($"no placement named '{render}'"); return 1; }

        var decoded = new Dictionary<string, (int W, int H, byte[] Rgba)?>(StringComparer.OrdinalIgnoreCase);
        GfxBitmap? Bitmap(int id)
        {
            if (!movie.SubImages.TryGetValue(id, out var si) || !movie.Images.TryGetValue(si.Image, out var img)) return null;
            string name = Path.GetFileNameWithoutExtension(img.File);
            if (!decoded.TryGetValue(name, out var atlas))
            {
                atlas = null;
                if (atlases.TryGetValue(name, out var dds) && dds.Length > 128 && dds[84] == 'D' && dds[87] == '5')
                {
                    int h = BitConverter.ToInt32(dds, 12), w = BitConverter.ToInt32(dds, 16);
                    atlas = (w, h, Bc3.Decode(dds.AsSpan(128), w, h));
                }
                decoded[name] = atlas;
            }
            if (atlas is not { } a) return null;
            int bw = si.X1 - si.X0, bh = si.Y1 - si.Y0;
            if (bw <= 0 || bh <= 0 || si.X1 > a.W || si.Y1 > a.H) return null;
            var o = new byte[bw * bh * 4];
            for (int y = 0; y < bh; y++) Array.Copy(a.Rgba, ((si.Y0 + y) * a.W + si.X0) * 4, o, y * bw * 4, bw * 4);
            return new GfxBitmap(bw, bh, o);
        }

        int frames = movie.Sprites.TryGetValue(character, out var sp) ? sp.Frames.Count : 1;
        var which = allFrames ? Enumerable.Range(0, frames) : [Math.Clamp(frameArg < 0 ? 0 : frameArg, 0, frames - 1)];
        int size = (int)Math.Ceiling(512 * scale);
        foreach (int f in which)
        {
            var normal = new GfxCanvas(size, size);
            var add = new GfxCanvas(size, size, additive: true);
            var m = new GfxMatrix(scale, 0, 0, scale, size / 2.0, size / 2.0);
            GfxRaster.Draw(movie, character, f, m, normal, Bitmap, null, add);
            // Composite: the normal layer over dark grey, then the additive layer added (as the HUD would show it over a dark scene).
            var n8 = normal.ToRgba8(); var a8 = add.ToRgba8();
            var px = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                float al = n8[i * 4 + 3] / 255f;
                for (int k = 0; k < 3; k++)
                {
                    float v = 40 * (1 - al) + n8[i * 4 + k] * al + a8[i * 4 + k];
                    px[i * 4 + k] = (byte)Math.Clamp(v, 0, 255);
                }
                px[i * 4 + 3] = 255;
            }
            string file = Path.Combine(full, $"{movieName}_{render}_f{f:D3}.png");
            WritePng(file, size, size, px);
            Console.WriteLine(file);
        }
        return 0;
    }

    /// <summary>
    /// The folder renders go to: <paramref name="requested"/> (full path), or <c>%LOCALAPPDATA%\sm-reimpl\cache\gfx-dump</c> when none is given; null (with the reason) for a folder inside
    /// the repository or the install (compared folder by folder, so a sibling whose name only starts the same is allowed).
    /// </summary>
    public static string? SafeOutput(string? requested, string gameDir, out string why)
    {
        why = "";
        string dir = requested is { Length: > 0 } ? Path.GetFullPath(requested)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "cache", "gfx-dump");
        static bool Inside(string path, string? root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            string d = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
            return d.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        if (Inside(dir, gameDir)) { why = $"refusing an output inside the game install ({dir})"; return null; }
        if (Inside(dir, RepoRoot())) { why = $"refusing an output inside the repository ({dir}): use the private cache"; return null; }
        return dir;
    }

    static string? RepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        return null;
    }

    static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>A minimal RGBA PNG writer (filter 0 rows, zlib).</summary>
    public static void WritePng(string path, int w, int h, byte[] rgba)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    static void BE(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        uint crc = Crc(t, 0xFFFFFFFF); crc = Crc(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4]; BE(c, 0, (int)crc); s.Write(c);
    }

    static uint Crc(byte[] d, uint c)
    {
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c;
    }
}
