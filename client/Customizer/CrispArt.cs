using System.Linq;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// ui/text-scale: painted pieces (pills, switches, slider channel, fill and knob) drawn straight from the concept's 4x art and shrunk once, by the GPU, onto the window's pixels. The older way resampled the art to
/// the virtual size first (a Lanczos copy) and let the window scale that copy again: soft at 1440p and 720p, blurry when magnified at 2160p. A nine-patch here keeps its caps at the art's own proportion: the
/// corner and cap pieces are drawn at <c>k</c> virtual pixels per art pixel and only the middle stretches. The host control must have <see cref="CanvasItem.TextureFilter"/> LinearWithMipmaps (the art has mipmaps)
/// and the window snaps drawing to whole pixels (<see cref="UiType.ApplyWindow"/>), so every edge is a pixel edge.
/// </summary>
public static class CrispArt
{
    static readonly System.Collections.Generic.Dictionary<(string, int, int), ImageTexture> Fits = new();

    /// <summary>
    /// A painted piece that carries words (the main menu's plates) resampled once, with Lanczos, to the exact window pixels it will cover (<paramref name="virtualSize"/> times the window scale), so it is drawn
    /// 1:1: the lettering keeps the edge it was painted with instead of being averaged by a mipmap or skipped by a point sample. Cached by size.
    /// </summary>
    public static Texture2D? Fitted(string name, Vector2 virtualSize)
    {
        if (ConceptArt.Get(name) is not { } src) return null;
        float w = UiType.WindowScale;
        int pw = Mathf.Max(2, Mathf.RoundToInt(virtualSize.X * w)), ph = Mathf.Max(2, Mathf.RoundToInt(virtualSize.Y * w));
        if (Fits.TryGetValue((name, pw, ph), out var cached)) return cached;
        foreach (var old in Fits.Keys.Where(k => k.Item1 == name).ToList()) Fits.Remove(old);   // only the current size of a plate is kept (a dragged window would otherwise keep every size)
        var img = (Image)src.GetImage().Duplicate();
        img.Decompress();
        if (img.HasMipmaps()) img.ClearMipmaps();
        img.Resize(pw, ph, Image.Interpolation.Lanczos);
        return Fits[(name, pw, ph)] = ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// Draws <paramref name="tex"/> into <paramref name="dest"/> as a nine-patch with margins in art pixels (<paramref name="ml"/>, <paramref name="mr"/>, <paramref name="mt"/>, <paramref name="mb"/>), the margins
    /// drawn <paramref name="k"/> virtual pixels per art pixel.
    /// </summary>
    public static void Patch(CanvasItem ci, Texture2D tex, Rect2 dest, float ml, float mr, float mt, float mb, float k, Color mod)
    {
        float tw = tex.GetWidth(), th = tex.GetHeight();
        float dl = ml * k, dr = mr * k, dt = mt * k, db = mb * k;
        // A destination narrower than its caps shares the room between them.
        if (dl + dr > dest.Size.X) { float f = dest.Size.X / (dl + dr); dl *= f; dr *= f; }
        if (dt + db > dest.Size.Y) { float f = dest.Size.Y / (dt + db); dt *= f; db *= f; }
        float[] sx = [0f, ml, tw - mr, tw], sy = [0f, mt, th - mb, th];
        float[] dx = [dest.Position.X, dest.Position.X + dl, dest.End.X - dr, dest.End.X], dy = [dest.Position.Y, dest.Position.Y + dt, dest.End.Y - db, dest.End.Y];
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                var d = new Rect2(dx[i], dy[j], dx[i + 1] - dx[i], dy[j + 1] - dy[j]);
                var src = new Rect2(sx[i], sy[j], sx[i + 1] - sx[i], sy[j + 1] - sy[j]);
                if (d.Size.X <= 0f || d.Size.Y <= 0f || src.Size.X <= 0f || src.Size.Y <= 0f) continue;
                ci.DrawTextureRectRegion(tex, d, src, mod);
            }
    }
}
