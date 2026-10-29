using SmFormats.Dds;
using SmFormats.Gfx;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>A rendered piece of a HUD movie: straight RGBA (<see cref="Normal"/>) and the "add" blend layer (<see cref="Additive"/>, RGB with alpha 255; null when the piece has none),
/// <see cref="Width"/> x <see cref="Height"/> pixels; the character's own origin (0, 0) lies at (<see cref="OriginX"/>, <see cref="OriginY"/>) in the picture.</summary>
public sealed record HudPicture(byte[] Normal, byte[]? Additive, int Width, int Height, double OriginX, double OriginY, double Scale);

/// <summary>
/// One Scaleform movie of the original HUD with the atlases it draws from (ui/hud-fidelity): read from the install at runtime (read-only), nothing extracted or cached on disk.
/// Pieces are rendered by the engine-free <see cref="GfxRaster"/> (shapes, gradients, bitmaps, clip layers; no text: the client draws text at the movie's text fields).
/// No action (ActionScript) is read.
/// </summary>
public sealed class HudMovie
{
    readonly Dictionary<string, byte[]> _atlases;
    readonly Dictionary<string, (int W, int H, byte[] Rgba)?> _decoded = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, GfxBitmap?> _bitmaps = new();
    readonly object _lock = new();

    public string Name { get; }
    public GfxMovie Movie { get; }

    public HudMovie(string name, GfxMovie movie, Dictionary<string, byte[]> atlases) { Name = name; Movie = movie; _atlases = atlases; }

    /// <summary>
    /// The placement at a path of instance names from the root ("Reticule_Ingame_0_mc/idle_mc/crosshair": each step searched in the first frame where it appears) with the matrix that
    /// takes its character's pixels to stage pixels (the parents' matrices composed); null when a step is missing.
    /// </summary>
    public (GfxPlacement Placement, GfxMatrix ToStage)? Find(string path, Func<int, int?>? frameOf = null)
    {
        // The HUD asks for the same paths every frame: without a frame choice the answer never changes.
        if (frameOf is null)
        {
            lock (_found)
            {
                if (_found.TryGetValue(path, out var known)) return known;
                return _found[path] = FindUncached(path, null);
            }
        }
        return FindUncached(path, frameOf);
    }

    (GfxPlacement Placement, GfxMatrix ToStage)? FindUncached(string path, Func<int, int?>? frameOf)
    {
        var sprite = Movie.Root;
        var m = GfxMatrix.Identity;
        GfxPlacement? found = null;
        foreach (var step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sprite is null) return null;
            found = null;
            // The frame <paramref name="frameOf"/> names for this sprite first (e.g. the end of a slide-in clip), then the first frame that places the step.
            if (frameOf?.Invoke(sprite.Id) is int at && sprite.Frames.Count > 0)
                foreach (var p in sprite.Frames[Math.Clamp(at, 0, sprite.Frames.Count - 1)].Display.Values) if (p.Name == step && p.Character >= 0) { found = p; break; }
            if (found is null)
            foreach (var f in sprite.Frames)
            {
                foreach (var p in f.Display.Values) if (p.Name == step && p.Character >= 0) { found = p; break; }
                if (found is not null) break;
            }
            if (found is null) return null;
            m = m.After(PixelMatrix(found.Matrix));
            sprite = Movie.Sprites.GetValueOrDefault(found.Character);
        }
        return found is null ? null : (found, m);
    }

    /// <summary>One thing a walk of the display list reaches: a piece drawn as a whole (<see cref="Character"/> at <see cref="Frame"/>) or a text field (<see cref="Text"/> set).</summary>
    public readonly record struct Item(int Character, int Frame, GfxMatrix Matrix, GfxColourTransform Colour, bool Add, string Path, GfxEditText? Text);

    /// <summary>
    /// Walks frame <paramref name="frame"/> of <paramref name="character"/> like the player would show it (ui/hud-fidelity: the reward lines' and feed's animations): nested sprites at
    /// <paramref name="frameOf"/>'s frame (by instance path from the walked character, e.g. "xp_mc"; null: the sprite's first frame); a sprite for which <paramref name="atom"/> is true,
    /// every shape and image, is visited as a piece (to be rendered once and drawn with its matrix and colour); text fields are visited as text. Matrices are in movie pixels of the
    /// walked character; invisible placements (alpha 0) are skipped, and the named ones for which <paramref name="skip"/> is true (by path).
    /// </summary>
    public void Walk(int character, int frame, Func<string, int, int?>? frameOf, Func<int, bool> atom, Action<Item> visit, Func<string, bool>? skip = null) =>
        WalkInto(character, frame, GfxMatrix.Identity, GfxColourTransform.Identity, false, "", frameOf, atom, visit, 0, skip);

    void WalkInto(int ch, int frame, GfxMatrix m, GfxColourTransform cx, bool add, string path, Func<string, int, int?>? frameOf, Func<int, bool> atom, Action<Item> visit, int depth, Func<string, bool>? skip)
    {
        if (depth > 12) return;
        if (Movie.EditTexts.TryGetValue(ch, out var text)) { visit(new Item(ch, 0, m, cx, add, path, text)); return; }
        if (!Movie.Sprites.TryGetValue(ch, out var sprite) || atom(ch)) { visit(new Item(ch, frame, m, cx, add, path, null)); return; }
        if (sprite.Frames.Count == 0) return;
        var f = sprite.Frames[Math.Clamp(frame, 0, sprite.Frames.Count - 1)];
        foreach (var p in f.Display.Values)
        {
            if (p.Character < 0 || p.ClipDepth > 0) continue;
            var pcx = p.Colour is { } c ? cx.After(c) : cx;
            if (pcx.MulA <= 0.001f && pcx.AddA <= 0.001f) continue;
            string sub = p.Name.Length > 0 ? (path.Length > 0 ? path + "/" + p.Name : p.Name) : path;
            if (p.Name.Length > 0 && skip?.Invoke(sub) == true) continue;
            int childFrame = (p.Name.Length > 0 ? frameOf?.Invoke(sub, p.Character) : null) ?? 0;
            WalkInto(p.Character, childFrame, m.After(PixelMatrix(p.Matrix)), pcx, add || p.Blend == 8, sub, frameOf, atom, visit, depth + 1, skip);
        }
    }

    /// <summary>A placement matrix with its translation in pixels (the movie stores twips).</summary>
    public static GfxMatrix PixelMatrix(GfxMatrix? m) => m is { } x ? new GfxMatrix(x.A, x.B, x.C, x.D, x.Tx / 20.0, x.Ty / 20.0) : GfxMatrix.Identity;

    /// <summary>A sprite's frame labels (label -> frame index, 0-based).</summary>
    public IReadOnlyDictionary<string, int> Labels(int sprite)
    {
        lock (_labels)
        {
            if (_labels.TryGetValue(sprite, out var known)) return known;
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (Movie.Sprites.TryGetValue(sprite, out var s))
                for (int i = 0; i < s.Frames.Count; i++) if (s.Frames[i].Label is { } l) d.TryAdd(l.Trim(), i);
            return _labels[sprite] = d;
        }
    }

    readonly Dictionary<int, IReadOnlyDictionary<string, int>> _labels = new();
    readonly Dictionary<string, (GfxPlacement, GfxMatrix)?> _found = new(StringComparer.Ordinal);

    readonly Dictionary<int, bool> _clips = new();

    /// <summary>Whether a sprite has clip layers of its own in any frame: such a sprite is rendered as a whole (its masks apply), a walk descends into the others.</summary>
    public bool HasClip(int character)
    {
        lock (_clips)
        {
            if (_clips.TryGetValue(character, out bool known)) return known;
            _clips[character] = false;   // cycles end here
            bool any = false;
            if (Movie.Sprites.TryGetValue(character, out var s))
                foreach (var f in s.Frames)
                {
                    foreach (var p in f.Display.Values) if (p.ClipDepth > 0) { any = true; break; }
                    if (any) break;
                }
            return _clips[character] = any;
        }
    }

    /// <summary>Frames of a sprite (1 for a shape).</summary>
    public int FrameCount(int character) => Movie.Sprites.TryGetValue(character, out var s) ? Math.Max(1, s.Frames.Count) : 1;

    /// <summary>
    /// Renders frame <paramref name="frame"/> of a character at <paramref name="scale"/> screen pixels per movie pixel, the transform <paramref name="local"/> (movie pixels; e.g. the
    /// placement's own matrix) applied first; nested sprites at <paramref name="frameOf"/>'s frame (null: their first). Clip layers apply. Null when it draws nothing.
    /// </summary>
    public HudPicture? Render(int character, int frame, double scale, GfxMatrix? local = null, Func<int, int>? frameOf = null, int pad = 2, CancellationToken cancel = default,
        GfxColourTransform? colour = null, bool add = false)
    {
        var l = local ?? GfxMatrix.Identity;
        var scaled = new GfxMatrix(scale, 0, 0, scale, 0, 0).After(l);
        var b = GfxRaster.Bounds(Movie, character, frame, scaled, frameOf, visibleOnly: true);
        if (b is not { } box) return null;
        double x0 = Math.Floor(box.X0) - pad, y0 = Math.Floor(box.Y0) - pad;
        int w = (int)Math.Ceiling(box.X1 - x0) + pad, h = (int)Math.Ceiling(box.Y1 - y0) + pad;
        if (w <= 0 || h <= 0 || (long)w * h > 4096L * 2048L) return null;
        var root = new GfxMatrix(1, 0, 0, 1, -x0, -y0).After(scaled);
        var normal = new GfxCanvas(w, h);
        var addCanvas = new GfxCanvas(w, h, additive: true);
        GfxRaster.DrawMasked(Movie, character, frame, root, normal, Bitmap, frameOf, addCanvas, cancel, colour, add);
        byte[] a8 = addCanvas.ToRgba8();
        bool anyAdd = false;
        for (int i = 0; i < a8.Length && !anyAdd; i += 4) anyAdd = a8[i] > 1 || a8[i + 1] > 1 || a8[i + 2] > 1;
        return new HudPicture(normal.ToRgba8(), anyAdd ? a8 : null, w, h, -x0, -y0, scale);
    }

    GfxBitmap? Bitmap(int id)
    {
        lock (_lock)
        {
            if (_bitmaps.TryGetValue(id, out var cached)) return cached;
            GfxBitmap? bmp = null;
            if (!Movie.SubImages.ContainsKey(id) && Movie.Images.TryGetValue(id, out var whole) && Decode(Path.GetFileNameWithoutExtension(whole.File)) is { } image)
                bmp = new GfxBitmap(image.W, image.H, image.Rgba);   // an image placed as a character: all of it
            else if (Movie.SubImages.TryGetValue(id, out var si) && Movie.Images.TryGetValue(si.Image, out var img) && Decode(Path.GetFileNameWithoutExtension(img.File)) is { } atlas)
            {
                int bw = si.X1 - si.X0, bh = si.Y1 - si.Y0;
                if (bw > 0 && bh > 0 && si.X0 >= 0 && si.Y0 >= 0 && si.X1 <= atlas.W && si.Y1 <= atlas.H)
                {
                    var o = new byte[bw * bh * 4];
                    for (int y = 0; y < bh; y++) Array.Copy(atlas.Rgba, ((si.Y0 + y) * atlas.W + si.X0) * 4, o, y * bw * 4, bw * 4);
                    bmp = new GfxBitmap(bw, bh, o);
                }
            }
            return _bitmaps[id] = bmp;
        }
    }

    (int W, int H, byte[] Rgba)? Decode(string name)
    {
        if (_decoded.TryGetValue(name, out var cached)) return cached;
        (int, int, byte[])? result = null;
        if (_atlases.TryGetValue(name, out var dds) && dds.Length > 128 && dds[0] == 'D' && dds[1] == 'D' && dds[2] == 'S' && dds[3] == ' '
            && BitConverter.ToInt32(dds, 4) == 124 && dds[84] == 'D' && dds[85] == 'X' && dds[86] == 'T' && dds[87] == '5')
        {
            int h = BitConverter.ToInt32(dds, 12), w = BitConverter.ToInt32(dds, 16);
            if (w > 0 && h > 0 && w % 4 == 0 && h % 4 == 0 && (long)w * h <= 4096L * 4096L && dds.Length - 128 >= (long)w * h)
                result = (w, h, Bc3.Decode(dds.AsSpan(128), w, h));
        }
        return _decoded[name] = result;
    }

    /// <summary>
    /// Renders the placement at <paramref name="path"/> in the space of <paramref name="relativeTo"/> (a path, typically its container; null: the stage) with the placement's own
    /// colour transform and blend mode; nested sprites at <paramref name="frameOf"/>'s frame. The picture's origin is <paramref name="relativeTo"/>'s origin. Null when missing or empty.
    /// </summary>
    public HudPicture? RenderAt(string path, string? relativeTo, double scale, int frame = 0, Func<int, int>? frameOf = null, CancellationToken cancel = default)
    {
        if (Find(path) is not { } found) return null;
        var parent = relativeTo is null ? GfxMatrix.Identity : Find(relativeTo)?.ToStage;
        if (parent is null || parent.Value.Inverse() is not { } inv) return null;
        var p = found.Placement;
        return Render(p.Character, frame, scale, inv.After(found.ToStage), frameOf, cancel: cancel, colour: p.Colour, add: p.Blend == 8);
    }

    /// <summary>The text field placed at <paramref name="path"/> and the matrix to stage pixels, or null.</summary>
    public (GfxEditText Text, GfxMatrix ToStage)? Text(string path)
        => Find(path) is { } f && Movie.EditTexts.TryGetValue(f.Placement.Character, out var t) ? (t, f.ToStage) : null;
}

/// <summary>
/// The original HUD's movies (ui/hud-fidelity): <c>Hud.gfx</c> (reticles, hit marker, weapon box, reward lines, the context button), <c>HudText.gfx</c> (the versus score panel),
/// <c>HudEventLog.gfx</c> (the kill feed) from <c>data/packages/ui_ingame.oppc</c> and <c>Decorator.gfx</c> (control point decorators) from <c>ui_common.oppc</c>. All 1280 x 720 stages.
/// A movie the install lacks is null (the HUD then draws its M44 shapes).
/// </summary>
public sealed class HudMovies
{
    public HudMovie? Hud { get; init; }
    public HudMovie? Text { get; init; }
    public HudMovie? EventLog { get; init; }
    public HudMovie? Decorator { get; init; }

    public const double StageHeight = 720.0;

    public static HudMovies Empty { get; } = new();

    public static HudMovies Load(string gameDir)
    {
        var ingame = ReadMovies(Path.Combine(gameDir, UiArt.IngamePackage), ["Hud", "HudText", "HudEventLog"]);
        Dictionary<string, HudMovie?> common;
        try { common = ReadMovies(Path.Combine(gameDir, UiArt.CommonPackage), ["Decorator"]); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { common = new(); }
        return new HudMovies
        {
            Hud = ingame.GetValueOrDefault("Hud"), Text = ingame.GetValueOrDefault("HudText"), EventLog = ingame.GetValueOrDefault("HudEventLog"),
            Decorator = common.GetValueOrDefault("Decorator"),
        };
    }

    static Dictionary<string, HudMovie?> ReadMovies(string package, string[] names)
    {
        var gfx = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var dds = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var prefixes = names.Select(n => n.ToLowerInvariant() + "_i").ToArray();
        using (var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var archive = ObpkArchive.Open(stream))
            archive.ReadEntries(e =>
            {
                string g = archive.GroupName(e);
                if (g == "gfx") return names.Any(n => n.Equals(e.FileName, StringComparison.OrdinalIgnoreCase));
                return g == "dds" && prefixes.Any(p => e.FileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            }, (e, d) => { if (archive.GroupName(e) == "gfx") gfx[e.FileName] = d.ToArray(); else dds[e.FileName] = d.ToArray(); });
        var result = new Dictionary<string, HudMovie?>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (!gfx.TryGetValue(n, out var bytes)) { result[n] = null; continue; }
            GfxMovie movie;
            try { movie = GfxMovie.Parse(bytes); } catch (InvalidDataException) { result[n] = null; continue; }
            string prefix = n.ToLowerInvariant() + "_i";
            var mine = dds.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            result[n] = new HudMovie(n, movie, mine);
        }
        return result;
    }
}
