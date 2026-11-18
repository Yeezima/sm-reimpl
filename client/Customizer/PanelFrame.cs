using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The Options frame of the concept (panel 04) made taller (ui/overhaul): a slice of its plain interior (overlay pixels 750 to 911 at 4x: steel edges and stone with a constant outline) is repeated, alternately mirrored, so the
/// stone and the steel stay continuous at every join. The copies come in pairs (0 or 2) so the picture below the slice joins the slice's own lower edge again. One frame texture per picture and count is built once.
/// Ours (docs/ui-overhaul.md).
/// </summary>
public static class PanelFrame
{
    /// <summary>The slice in overlay pixels (4x v2 pixels) and the picture's size in v2 pixels.</summary>
    public const int SliceY0 = 750, SliceY1 = 911;
    public const float PxPerV2 = 4f, OverlayW = 736f, OverlayH = 342f;

    static readonly Dictionary<(string, int), ImageTexture> Cache = new();

    /// <summary>The picture <paramref name="name"/> (overlay-panel or overlay-options) with <paramref name="slices"/> extra slice copies (0 or 2), or null when the picture is missing.</summary>
    public static ImageTexture? Texture(string name, int slices)
    {
        slices = slices <= 0 ? 0 : 2;
        if (Cache.TryGetValue((name, slices), out var cached)) return cached;
        if (ConceptArt.Get(name) is not { } tex) return null;
        var src = (Image)tex.GetImage().Duplicate(); src.Decompress();
        if (src.HasMipmaps()) src.ClearMipmaps();
        src.Convert(Image.Format.Rgba8);
        int w = src.GetWidth(), h = src.GetHeight(), sh = SliceY1 - SliceY0;
        var dst = Image.CreateEmpty(w, h + slices * sh, false, Image.Format.Rgba8);
        dst.BlitRect(src, new Rect2I(0, 0, w, SliceY1), Vector2I.Zero);
        var slice = src.GetRegion(new Rect2I(0, SliceY0, w, sh));
        var flipped = (Image)slice.Duplicate(); flipped.FlipY();
        for (int i = 0; i < slices; i++) dst.BlitRect(i % 2 == 0 ? flipped : slice, new Rect2I(0, 0, w, sh), new Vector2I(0, SliceY1 + i * sh));
        dst.BlitRect(src, new Rect2I(0, SliceY1, w, h - SliceY1), new Vector2I(0, SliceY1 + slices * sh));
        dst.GenerateMipmaps();
        return Cache[(name, slices)] = ImageTexture.CreateFromImage(dst);
    }

    /// <summary>The height the copies add, in v2 pixels.</summary>
    public static float ExtraV2(int slices) => (slices <= 0 ? 0 : 2) * (SliceY1 - SliceY0) / PxPerV2;
}
