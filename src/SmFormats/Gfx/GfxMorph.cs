namespace SmFormats.Gfx;

/// <summary>
/// A morph shape (DefineMorphShape, tag 46, and DefineMorphShape2, tag 84): a start shape and an end shape of the same structure (the same edges in the same order, the same style
/// lists), and a placement's <c>ratio</c> (0 to 65535) says how far between them it is drawn (ui/hud-2: the stalker scope's reticle ticks walk outward through such a shape).
/// <see cref="At"/> builds the shape at a ratio: every coordinate, colour, line width and style matrix is interpolated linearly; a straight edge paired with a curve is treated as a curve whose
/// control point is the straight edge's midpoint. Shapes whose two halves do not match (a malformed morph) give the start shape.
/// </summary>
public sealed class GfxMorph
{
    public int Id { get; }
    public GfxShape Start { get; }
    public GfxShape End { get; }

    /// <summary>Whether the start and end halves have the same edges and styles, so that a ratio between them is defined.</summary>
    public bool Consistent { get; }

    readonly Dictionary<int, GfxShape> _cache = new();

    public GfxMorph(int id, GfxShape start, GfxShape end)
    {
        Id = id; Start = start; End = end;
        Consistent = start.Segments.Count == end.Segments.Count && start.Fills.Count == end.Fills.Count && start.Lines.Count == end.Lines.Count;
    }

    /// <summary>The shape at <paramref name="ratio"/> (0 the start, 65535 the end; values outside are clamped).</summary>
    public GfxShape At(int ratio)
    {
        ratio = Math.Clamp(ratio, 0, 65535);
        if (!Consistent || ratio == 0) return Start;
        if (ratio == 65535) return End;
        // A quantised ratio (1/512 of the range) so that a clip playing its frames reuses shapes.
        int key = ratio >> 7;
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var known)) return known;
            return _cache[key] = Lerp((key << 7) / 65535.0);
        }
    }

    GfxShape Lerp(double t)
    {
        var s = new GfxShape { Id = Id };
        for (int i = 0; i < Start.Fills.Count; i++) s.Fills.Add(LerpFill(Start.Fills[i], End.Fills[i], t));
        for (int i = 0; i < Start.Lines.Count; i++)
            s.Lines.Add(new GfxLine { Width = L(Start.Lines[i].Width, End.Lines[i].Width, t), Colour = LerpColour(Start.Lines[i].Colour, End.Lines[i].Colour, t) });
        for (int i = 0; i < Start.Segments.Count; i++)
        {
            var a = Start.Segments[i]; var b = End.Segments[i];
            double acx = a.Curved ? a.CX : (a.X0 + a.X1) / 2, acy = a.Curved ? a.CY : (a.Y0 + a.Y1) / 2;
            double bcx = b.Curved ? b.CX : (b.X0 + b.X1) / 2, bcy = b.Curved ? b.CY : (b.Y0 + b.Y1) / 2;
            bool curved = a.Curved || b.Curved;
            s.Segments.Add(new GfxSegment(L(a.X0, b.X0, t), L(a.Y0, b.Y0, t), curved ? L(acx, bcx, t) : 0, curved ? L(acy, bcy, t) : 0, L(a.X1, b.X1, t), L(a.Y1, b.Y1, t), curved, a.FillLeft, a.FillRight, a.Line));
        }
        return s;
    }

    static double L(double a, double b, double t) => a + (b - a) * t;

    static (float R, float G, float B, float A) LerpColour((float R, float G, float B, float A) a, (float R, float G, float B, float A) b, double t) =>
        ((float)L(a.R, b.R, t), (float)L(a.G, b.G, t), (float)L(a.B, b.B, t), (float)L(a.A, b.A, t));

    static GfxMatrix LerpMatrix(GfxMatrix a, GfxMatrix b, double t) => new(L(a.A, b.A, t), L(a.B, b.B, t), L(a.C, b.C, t), L(a.D, b.D, t), L(a.Tx, b.Tx, t), L(a.Ty, b.Ty, t));

    static GfxFill LerpFill(GfxFill a, GfxFill b, double t)
    {
        var f = new GfxFill { Kind = a.Kind, BitmapId = a.BitmapId, Repeat = a.Repeat, Smooth = a.Smooth, Matrix = LerpMatrix(a.Matrix, b.Matrix, t), Colour = LerpColour(a.Colour, b.Colour, t) };
        if (a.Stops.Length == b.Stops.Length)
        {
            f.Stops = new (float, float, float, float, float)[a.Stops.Length];
            for (int i = 0; i < f.Stops.Length; i++)
            {
                var x = a.Stops[i]; var y = b.Stops[i];
                f.Stops[i] = ((float)L(x.Ratio, y.Ratio, t), (float)L(x.R, y.R, t), (float)L(x.G, y.G, t), (float)L(x.B, y.B, t), (float)L(x.A, y.A, t));
            }
        }
        else f.Stops = a.Stops;
        return f;
    }
}
