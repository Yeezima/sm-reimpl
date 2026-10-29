using SmFormats.Dds;
using SmFormats.Gfx;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>Which weapons' aim mode shows a scope overlay.</summary>
public enum ScopeKind { Stalker, Lascannon }

/// <summary>One rendered scope picture: the normal layer (straight RGBA) and the layer the movie draws with the "add" blend mode (RGB, alpha 255), both <see cref="Width"/> x <see cref="Height"/>.</summary>
public sealed record ScopePicture(byte[] Normal, byte[] Additive, int Width, int Height, float EdgeAlpha);

/// <summary>
/// The scope's reticle clip at one spread frame (ui/hud-2): straight RGBA and the additive layer, <see cref="Width"/> x <see cref="Height"/> pixels; <see cref="OriginX"/>, <see cref="OriginY"/> is where the
/// stage centre (the screen centre) lies in the picture, <see cref="RenderHeight"/> the height the stage was rendered at (the same scale as the overlay's picture).
/// </summary>
public sealed record ScopeCrosshair(byte[] Normal, byte[] Additive, int Width, int Height, float OriginX, float OriginY, int RenderHeight);

/// <summary>
/// The scope overlays of the original HUD (feel/scope-lascannon; owner, 2026-10-29): the stalker bolter's and the lascannon's zoomed view is not a plain camera, the HUD
/// movie (<c>Hud.gfx</c> in <c>data/packages/ui_ingame.oppc</c>) draws a mask with a lens-shaped opening and the reticle (<c>stalker_idle_zoom_mc</c>, <c>stalker_active_zoom_mc</c>,
/// <c>lascannon_scope_mc</c>, <c>lascannon_scope_active_mc</c>: the idle and the aimed-at-an-enemy variants) over it. The movie's vector shapes (the mask, rings, ticks, gradients)
/// and its bitmaps (sub-images of the movie's <c>hud_i*</c> atlases, also in the package) are read at runtime and drawn by a small software renderer (<see cref="GfxRaster"/>);
/// nothing is extracted or cached. Blend modes: the movie places the glows, ticks and reticle lines with "add" (blend 8) and its masks with "multiply" (blend 3) of black, which
/// is the normal alpha blend; the added parts are rendered to their own layer (<see cref="ScopePicture.Additive"/>) that the client draws additively. The stalker's overlay fades in over
/// its 25-frame "activate" clip (30 fps): every layer's alpha is one ramp (<see cref="FadeAt"/>), so only the finished picture is rendered and the client modulates it; the lascannon's is a single frame.
/// Read: names, placements and shapes only; no action (ActionScript, DoAction, DoABC) is read at all. The container's place (the stage centre) is an interpretation.
/// </summary>
public sealed class ScopeArt
{
    public const string Package = UiArt.IngamePackage;

    /// <summary>The tallest picture rendered, pixels: a taller screen is rendered at this height and scaled up (soft vector art; bounds the memory of the canvases, 59 MB at 1440 x 2560 for both layers).</summary>
    public const int MaxRenderHeight = 1440;

    /// <summary>The clip's frame rate: the movie's (30 fps).</summary>
    public const double FramesPerSecond = 30.0;

    /// <summary>Largest atlas read, pixels (the movie's own are 256 x 256).</summary>
    const int MaxAtlasPixels = 4096 * 4096;

    readonly GfxMovie _movie;
    readonly Dictionary<string, byte[]> _atlases;
    readonly Dictionary<string, (int W, int H, byte[] Rgba)?> _decoded = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, GfxBitmap?> _bitmaps = new();
    readonly object _lock = new();

    ScopeArt(GfxMovie movie, Dictionary<string, byte[]> atlases) { _movie = movie; _atlases = atlases; DetachCrosshairs(); }

    /// <summary>A scope sprite's reticle clip taken out of it: its character and its placement in the sprite (pixels).</summary>
    readonly record struct CrosshairSlot(int Character, GfxMatrix Place);

    readonly Dictionary<(ScopeKind, bool), CrosshairSlot> _crosshairs = new();

    /// <summary>
    /// The stalker's scope sprites place a nested <c>crosshair</c> clip (40 frames: ticks that walk outward, labels <c>min_aim</c> 0, <c>max_normal</c> 20, <c>max_aim</c> 39) that the finished picture
    /// would draw at its tightest frame; it is taken out of the parsed (in-memory) sprites and rendered by itself at the frame of the scatter (<see cref="RenderCrosshair"/>, ui/hud-2). The
    /// lascannon's overlay has no such clip.
    /// </summary>
    void DetachCrosshairs()
    {
        foreach (var kind in Kinds)
            foreach (bool active in new[] { false, true })
            {
                if (_movie.FindNamedPlacement(Name(kind, active)) is not { } p || !_movie.Sprites.TryGetValue(p.Character, out var sprite)) continue;
                GfxPlacement? last = null;
                foreach (var frame in sprite.Frames)
                    foreach (var kv in frame.Display.ToList())
                        if (kv.Value.Name == "crosshair" && kv.Value.Character >= 0 && _movie.Sprites.TryGetValue(kv.Value.Character, out var clip) && clip.Frames.Count > 1)
                        {
                            last = kv.Value;
                            frame.Display.Remove(kv.Key);
                        }
                if (last is not null) _crosshairs[(kind, active)] = new CrosshairSlot(last.Character, Pixels(last.Matrix));
            }
    }

    static GfxMatrix Pixels(GfxMatrix? m) => m is { } x ? new GfxMatrix(x.A, x.B, x.C, x.D, x.Tx / 20.0, x.Ty / 20.0) : GfxMatrix.Identity;

    /// <summary>Whether the overlay has a reticle clip that opens with the scatter (the stalker's; the lascannon's has none).</summary>
    public bool HasSpread(ScopeKind kind) => _crosshairs.ContainsKey((kind, false));

    /// <summary>The reticle clip's frame for a spread fraction (0 the narrowest, 1 the widest of the aiming stance): the clip's <c>min_aim</c> .. <c>max_aim</c> (<see cref="ReticleRules.SpreadRange"/>, the same rule as the hip reticles).</summary>
    public int SpreadFrame(ScopeKind kind, bool active, float fraction)
    {
        if (!_crosshairs.TryGetValue((kind, active), out var slot) || !_movie.Sprites.TryGetValue(slot.Character, out var clip)) return 0;
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < clip.Frames.Count; i++) if (clip.Frames[i].Label is { } l) labels.TryAdd(l.Trim(), i);
        return ReticleRules.SpreadFrame(ReticleRules.SpreadRange(labels, clip.Frames.Count, aiming: true), fraction);
    }

    /// <summary>The number of frames of the reticle clip (0 without one).</summary>
    public int SpreadFrames(ScopeKind kind) => _crosshairs.TryGetValue((kind, false), out var slot) && _movie.Sprites.TryGetValue(slot.Character, out var clip) ? clip.Frames.Count : 0;

    /// <summary>
    /// Renders the reticle clip alone at <paramref name="frame"/> for a screen of <paramref name="height"/> pixels (at most <see cref="MaxRenderHeight"/>), in a picture that is the same size for every
    /// frame (the union of the clip's first and last), so the caller draws all of them at one place. Null without a clip.
    /// </summary>
    public ScopeCrosshair? RenderCrosshair(ScopeKind kind, bool active, int frame, int height, CancellationToken cancel = default)
    {
        if (!_crosshairs.TryGetValue((kind, active), out var slot) || _movie.FindNamedPlacement(Name(kind, active)) is not { } placement || !_movie.Sprites.TryGetValue(slot.Character, out var clip)) return null;
        height = Math.Clamp(height, 16, MaxRenderHeight);
        double scale = height / _movie.StageHeight;
        // The stage centre is the origin: the container's sprite and the clip's own placement carry the rest.
        var m = new GfxMatrix(scale, 0, 0, scale, 0, 0).After(Pixels(placement.Matrix)).After(slot.Place);
        var first = GfxRaster.Bounds(_movie, slot.Character, 0, m, visibleOnly: true);
        var lastB = GfxRaster.Bounds(_movie, slot.Character, clip.Frames.Count - 1, m, visibleOnly: true);
        if (first is not { } a || lastB is not { } b) return null;
        double x0 = Math.Floor(Math.Min(a.X0, b.X0)) - 3, y0 = Math.Floor(Math.Min(a.Y0, b.Y0)) - 3;
        int w = (int)Math.Ceiling(Math.Max(a.X1, b.X1) - x0) + 3, h = (int)Math.Ceiling(Math.Max(a.Y1, b.Y1) - y0) + 3;
        if (w <= 0 || h <= 0 || (long)w * h > 2048L * 2048L) return null;
        var root = new GfxMatrix(1, 0, 0, 1, -x0, -y0).After(m);
        var normal = new GfxCanvas(w, h);
        var additive = new GfxCanvas(w, h, additive: true);
        GfxRaster.Draw(_movie, slot.Character, Math.Clamp(frame, 0, clip.Frames.Count - 1), root, normal, Bitmap, null, additive, cancel);
        return new ScopeCrosshair(normal.ToRgba8(), additive.ToRgba8(), w, h, (float)-x0, (float)-y0, height);
    }

    /// <summary>The movie's stage size in its own pixels (1280 x 720).</summary>
    public (double Width, double Height) Stage => (_movie.StageWidth, _movie.StageHeight);

    static string Name(ScopeKind kind, bool active) => (kind, active) switch
    {
        (ScopeKind.Stalker, false) => "stalker_idle_zoom_mc",
        (ScopeKind.Stalker, true) => "stalker_active_zoom_mc",
        (ScopeKind.Lascannon, false) => "lascannon_scope_mc",
        _ => "lascannon_scope_active_mc",
    };

    static readonly ScopeKind[] Kinds = [ScopeKind.Stalker, ScopeKind.Lascannon];

    /// <summary>
    /// Reads the movie and the atlases its four scope sprites reach, in one pass over the package; null when the install has no such movie (the overlay is then not drawn). Only
    /// called when a player first holds one of the two weapons.
    /// </summary>
    public static ScopeArt? Load(string gameDir)
    {
        byte[]? gfx = null;
        var candidates = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var stream = new FileStream(Path.Combine(gameDir, Package), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var archive = ObpkArchive.Open(stream))
        {
            // One pass: the movie and the HUD atlases (at most a few dozen small files), of which only the reached ones are kept below.
            archive.ReadEntries(e =>
            {
                string group = archive.GroupName(e);
                return (group == "gfx" && e.FileName.Equals("Hud", StringComparison.OrdinalIgnoreCase)) || (group == "dds" && e.FileName.StartsWith("hud_i", StringComparison.OrdinalIgnoreCase));
            }, (e, d) =>
            {
                if (archive.GroupName(e) == "gfx") gfx = d.ToArray(); else candidates[e.FileName] = d.ToArray();
            });
        }
        if (gfx is null) return null;
        var movie = GfxMovie.Parse(gfx);
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in Kinds)
            foreach (bool active in new[] { false, true })
                if (movie.FindNamedPlacement(Name(kind, active)) is { } p)
                    foreach (int bitmap in movie.BitmapIds(p.Character))
                        if (movie.SubImages.TryGetValue(bitmap, out var si) && movie.Images.TryGetValue(si.Image, out var img))
                            needed.Add(Path.GetFileNameWithoutExtension(img.File));
        var atlases = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in needed) if (candidates.TryGetValue(name, out var dds)) atlases[name] = dds;
        return new ScopeArt(movie, atlases);
    }

    /// <summary>Names of the atlases kept (the ones the scope sprites reach).</summary>
    public IReadOnlyCollection<string> AtlasNames => _atlases.Keys;

    /// <summary>Whether all four overlay sprites are in the movie.</summary>
    public bool Has(ScopeKind kind) => _movie.FindNamedPlacement(Name(kind, false)) is not null && _movie.FindNamedPlacement(Name(kind, true)) is not null;

    readonly Dictionary<ScopeKind, int> _frameCounts = new();

    /// <summary>Frames of the overlay's own clip: the stalker's "activate" (25), the lascannon's 1.</summary>
    public int FrameCount(ScopeKind kind)
    {
        lock (_lock)
        {
            if (_frameCounts.TryGetValue(kind, out int n)) return n;
            var p = _movie.FindNamedPlacement(Name(kind, false));
            return _frameCounts[kind] = p is not null && _movie.Sprites.TryGetValue(p.Character, out var s) ? Math.Max(1, s.Frames.Count) : 1;
        }
    }

    /// <summary>
    /// How much of the finished overlay shows at clip frame <paramref name="frame"/> (0 to 1): the alpha the clip gives its layers (the movie fades the stalker's reticle, mask and glow with one
    /// ramp: 0 at the first frame, 0.44 at frame 6, 0.75 at 12, 1 from frame 24). The lascannon's single frame is 1.
    /// </summary>
    public float FadeAt(ScopeKind kind, int frame)
    {
        var p = _movie.FindNamedPlacement(Name(kind, false));
        if (p is null || !_movie.Sprites.TryGetValue(p.Character, out var sprite) || sprite.Frames.Count <= 1) return 1f;
        var f = sprite.Frames[Math.Clamp(frame, 0, sprite.Frames.Count - 1)];
        var last = sprite.Frames[^1];
        // The ramp is the alpha of the clip's background layer (its instance name ends in _bg_mc); without one, the largest layer alpha.
        float Alpha(GfxFrame fr) => fr.Display.Values.Where(d => d.Name.EndsWith("_bg_mc", StringComparison.Ordinal)).Select(d => d.Colour?.MulA ?? 1f).DefaultIfEmpty(fr.Display.Values.Select(d => d.Colour?.MulA ?? 1f).DefaultIfEmpty(1f).Max()).Max();
        float end = Alpha(last);
        return end > 1e-4f ? Math.Clamp(Alpha(f) / end, 0f, 1f) : 1f;
    }

    /// <summary>
    /// Renders the finished overlay (<paramref name="active"/>: the variant shown with an enemy in the sights) for a screen of <paramref name="width"/> x <paramref name="height"/> (rendered at
    /// <see cref="MaxRenderHeight"/> at most; <see cref="ScopePicture.Width"/> and <see cref="ScopePicture.Height"/> say the size made): the stage fitted to the height and centred; the area beside
    /// it (wider screens) is left transparent (the caller fills it with <see cref="ScopePicture.EdgeAlpha"/>).
    /// </summary>
    public ScopePicture Render(ScopeKind kind, bool active, int width, int height, CancellationToken cancel = default)
    {
        if (height > MaxRenderHeight) { width = (int)Math.Round(width * (double)MaxRenderHeight / height); height = MaxRenderHeight; }
        width = Math.Max(16, width); height = Math.Max(16, height);
        var placement = _movie.FindNamedPlacement(Name(kind, active)) ?? throw new InvalidOperationException("scope sprite missing");
        double scale = height / _movie.StageHeight;
        double left = (width - _movie.StageWidth * scale) / 2.0;
        // The container of the reticle sprites sits at the stage centre; the sprite's own placement (twips) is relative to it.
        var m = new GfxMatrix(scale, 0, 0, scale, left + _movie.StageWidth / 2.0 * scale, _movie.StageHeight / 2.0 * scale)
            .After(new GfxMatrix(1, 0, 0, 1, (placement.Matrix?.Tx ?? 0) / 20.0, (placement.Matrix?.Ty ?? 0) / 20.0));
        var normal = new GfxCanvas(width, height);
        var additive = new GfxCanvas(width, height, additive: true);
        // The clip's last frame is the finished picture (the stalker's 25 frames end at the full ramp).
        int last = Math.Max(0, FrameCount(kind) - 1);
        GfxRaster.Draw(_movie, placement.Character, last, m, normal, Bitmap, null, additive, cancel);
        // The movie's mask is not quite wide enough to cover its own stage (a line a pixel or three wide at an edge, at the left and the top in the data): carry the mask's own alpha
        // outward from the first fully covered row or column so that the picture has no stripe. The mask's alpha is read inside the stage, away from the lens and the lines.
        int x0 = Math.Max(0, (int)left), x1 = Math.Min(width - 1, (int)Math.Ceiling(left + _movie.StageWidth * scale) - 1);
        float edge = normal.AlphaAt(Math.Min(x1, x0 + 12), height / 2);
        float full = edge * 0.9f;
        int top = 0; while (top < height / 2 && normal.AlphaAt(Math.Min(x1, x0 + 12), top) < full) top++;
        for (int y = 0; y < top; y++) normal.CopyRow(top, y);
        int bottom = height - 1; while (bottom > height / 2 && normal.AlphaAt(Math.Min(x1, x0 + 12), bottom) < full) bottom--;
        for (int y = height - 1; y > bottom; y--) normal.CopyRow(bottom, y);
        int leftCol = x0; while (leftCol < x1 / 2 && normal.AlphaAt(leftCol, height / 2 < 1 ? 0 : 3) < full) leftCol++;
        for (int x = x0; x < leftCol; x++) normal.CopyColumn(leftCol, x);
        int rightCol = x1; while (rightCol > (x0 + x1) / 2 && normal.AlphaAt(rightCol, 3) < full) rightCol--;
        for (int x = x1; x > rightCol; x--) normal.CopyColumn(rightCol, x);
        return new ScopePicture(normal.ToRgba8(), additive.ToRgba8(), width, height, edge);
    }

    GfxBitmap? Bitmap(int id)
    {
        lock (_lock)
        {
            if (_bitmaps.TryGetValue(id, out var cached)) return cached;
            GfxBitmap? bmp = null;
            if (_movie.SubImages.TryGetValue(id, out var si) && _movie.Images.TryGetValue(si.Image, out var img) && DecodeAtlas(Path.GetFileNameWithoutExtension(img.File)) is { } atlas)
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

    /// <summary>An atlas decoded once (each sub-image copies out of it): the DDS must be "DDS " with a DXT5 FourCC, a sane size and all its blocks.</summary>
    (int W, int H, byte[] Rgba)? DecodeAtlas(string name)
    {
        if (_decoded.TryGetValue(name, out var cached)) return cached;
        (int, int, byte[])? result = null;
        if (_atlases.TryGetValue(name, out var dds) && dds.Length > 128 && dds[0] == 'D' && dds[1] == 'D' && dds[2] == 'S' && dds[3] == ' '
            && BitConverter.ToInt32(dds, 4) == 124 && dds[84] == 'D' && dds[85] == 'X' && dds[86] == 'T' && dds[87] == '5')
        {
            int h = BitConverter.ToInt32(dds, 12), w = BitConverter.ToInt32(dds, 16);
            // BC3 is one byte a pixel (16 bytes a 4 x 4 block).
            if (w > 0 && h > 0 && w % 4 == 0 && h % 4 == 0 && (long)w * h <= MaxAtlasPixels && dds.Length - 128 >= (long)w * h)
                result = (w, h, Bc3.Decode(dds.AsSpan(128), w, h));
        }
        return _decoded[name] = result;
    }

    /// <summary>The kind of scope the weapon in hand has (the stalker bolter, the lascannon), or null.</summary>
    public static ScopeKind? KindOf(string? wargear)
    {
        if (string.IsNullOrEmpty(wargear)) return null;
        if (wargear.EndsWith("_sniper_rifle", StringComparison.OrdinalIgnoreCase)) return ScopeKind.Stalker;
        if (wargear.EndsWith("_las_cannon", StringComparison.OrdinalIgnoreCase) || wargear.Contains("lascannon", StringComparison.OrdinalIgnoreCase)) return ScopeKind.Lascannon;
        return null;
    }
}
