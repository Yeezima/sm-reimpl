using System.Globalization;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// The 3D view's reference grid of one map (docs/mapmaker.md "Grid"): editor-only data kept in the <c>grid</c> object of the <c>.smmap</c>. It has no effect in the game, and the compiler
/// and the map's content hash ignore it (<see cref="MapDocument.ToNode(bool)"/> leaves it out of what is hashed). Written only when it differs from <see cref="Default"/>, so an old file reads the same.
/// <see cref="CellSize"/> is the metres between lines; <see cref="Height"/> is the SM1 Z of the grid plane (Z up); <see cref="Colour"/> is "#rrggbbaa".
/// </summary>
public sealed record MapGrid(bool Show, string Colour, float CellSize, float Height, bool DrawOver)
{
    public const float MinCell = 0.1f, MaxCell = 100f, MaxHeight = 100_000f;
    /// <summary>The grid covers this many metres each way from the origin (a fixed area, never scaled by the view).</summary>
    public const float HalfExtent = 200f;

    /// <summary>Shown, grey at 50% alpha, 1 m cells, at height 0, hidden behind objects.</summary>
    public static readonly MapGrid Default = new(true, "#80808080", 1f, 0f, false);

    public JsonObject ToNode() => new() { ["show"] = Show, ["colour"] = Colour, ["cellSize"] = JsonValue.Create(CellSize), ["height"] = JsonValue.Create(Height), ["drawOver"] = DrawOver };

    /// <summary>The colour as four bytes (r, g, b, a); grey 50% when the text is not a valid "#rrggbbaa".</summary>
    public (byte R, byte G, byte B, byte A) Bytes() => TryColour(Colour, out var c) ? c : ((byte)128, (byte)128, (byte)128, (byte)128);

    public static bool TryColour(string? text, out (byte R, byte G, byte B, byte A) c)
    {
        c = default;
        if (text is not { Length: 9 } || text[0] != '#') return false;
        if (!uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) return false;
        c = ((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string ColourText(byte r, byte g, byte b, byte a) => $"#{r:x2}{g:x2}{b:x2}{a:x2}";

    /// <summary>Editor-only data is repaired, never refused: a missing or odd field takes the default, numbers are clamped into range.</summary>
    public static MapGrid FromNode(JsonNode? node)
    {
        if (node is not JsonObject o) return Default;
        bool show = o["show"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) ? sb : Default.Show;
        bool over = o["drawOver"] is JsonValue dv && dv.TryGetValue<bool>(out var db) ? db : Default.DrawOver;
        string? colour = o["colour"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : null;
        string text = TryColour(colour, out var c) ? ColourText(c.R, c.G, c.B, c.A) : Default.Colour;
        return new MapGrid(show, text, Num(o["cellSize"], Default.CellSize, MinCell, MaxCell), Num(o["height"], Default.Height, -MaxHeight, MaxHeight), over);
    }

    static float Num(JsonNode? n, float dflt, float min, float max) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (float)Math.Clamp(d, min, max) : dflt;

    /// <summary>set-grid {show?, colour?, cellSize?, height?, drawOver?}: changes the named fields. Values out of range are refused (<c>bad-op</c>).</summary>
    public MapGrid Apply(JsonObject op)
    {
        var g = this;
        if (op["show"] is { } s) g = g with { Show = Json.Bool(s, "show", "bad-op", Show) };
        if (op["drawOver"] is { } d) g = g with { DrawOver = Json.Bool(d, "drawOver", "bad-op", DrawOver) };
        if (op["colour"] is { } c)
        {
            if (!TryColour(Json.Str(c, "colour", "bad-op"), out var b)) throw new MapException("bad-op", "colour must look like #rrggbbaa.");
            g = g with { Colour = ColourText(b.R, b.G, b.B, b.A) };
        }
        if (op["cellSize"] is { } cs)
        {
            float v = Json.Num(cs, "cellSize", "bad-op");
            if (!(v >= MinCell && v <= MaxCell)) throw new MapException("bad-op", $"cellSize must be between {MinCell} and {MaxCell} m.");
            g = g with { CellSize = v };
        }
        if (op["height"] is { } h)
        {
            float v = Json.Num(h, "height", "bad-op");
            if (!(v >= -MaxHeight && v <= MaxHeight)) throw new MapException("bad-op", $"height must be within {MaxHeight} m.");
            g = g with { Height = v };
        }
        return g;
    }
}
