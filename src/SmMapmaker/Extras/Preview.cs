using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;

namespace SmMapmaker;

/// <summary>A top-down plan of a map as SVG (X to the right, Y up): footprints of the objects' boxes, a 1 m square where a box is unknown, and a label per object.</summary>
public static class MapPreview
{
    static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Render(MapDocument doc, MapContext ctx)
    {
        var parts = MapResolver.Flatten(doc, ctx).Where(p => p.Visible).ToList();
        var shapes = new List<(ResolvedPart Part, Vector2[] Poly, Vector2 Center)>();
        foreach (var p in parts)
        {
            Vector2[] poly;
            if (p.LocalBounds is { } b)
            {
                float z = b.Min.Z;
                poly = new[] { new Vector3(b.Min.X, b.Min.Y, z), new Vector3(b.Max.X, b.Min.Y, z), new Vector3(b.Max.X, b.Max.Y, z), new Vector3(b.Min.X, b.Max.Y, z) }
                    .Select(c => Vector3.Transform(c, p.World)).Select(c => new Vector2(c.X, c.Y)).ToArray();
            }
            else
            {
                var c = new Vector2(p.World.M41, p.World.M42);
                poly = [c + new Vector2(-0.5f, -0.5f), c + new Vector2(0.5f, -0.5f), c + new Vector2(0.5f, 0.5f), c + new Vector2(-0.5f, 0.5f)];
            }
            shapes.Add((p, poly, poly.Aggregate(Vector2.Zero, (a, v) => a + v) / poly.Length));
        }
        var sb = new StringBuilder();
        if (shapes.Count == 0)
        {
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">\n");
            sb.Append("  <rect width=\"200\" height=\"100\" fill=\"#1b1d21\"/>\n  <text x=\"100\" y=\"54\" fill=\"#8a8f98\" font-family=\"sans-serif\" font-size=\"12\" text-anchor=\"middle\">").Append(SecurityElement.Escape(doc.Name)).Append(" (empty)</text>\n</svg>\n");
            return sb.ToString();
        }
        var all = shapes.SelectMany(s => s.Poly).ToList();
        float minX = all.Min(v => v.X), maxX = all.Max(v => v.X), minY = all.Min(v => v.Y), maxY = all.Max(v => v.Y);
        float span = MathF.Max(MathF.Max(maxX - minX, maxY - minY), 1f);
        float margin = span * 0.05f;
        float scale = 1000f / (span + 2 * margin);
        float width = (maxX - minX + 2 * margin) * scale, height = (maxY - minY + 2 * margin) * scale;
        Vector2 P(Vector2 v) => new((v.X - minX + margin) * scale, (maxY - v.Y + margin) * scale);
        string Pt(Vector2 v) { var q = P(v); return N(q.X) + "," + N(q.Y); }

        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(N(width)).Append("\" height=\"").Append(N(height)).Append("\" viewBox=\"0 0 ").Append(N(width)).Append(' ').Append(N(height)).Append("\">\n");
        sb.Append("  <title>").Append(SecurityElement.Escape(doc.Name)).Append("</title>\n");
        sb.Append("  <rect width=\"").Append(N(width)).Append("\" height=\"").Append(N(height)).Append("\" fill=\"#1b1d21\"/>\n");
        foreach (var (part, poly, _) in shapes)
        {
            string fill = part.PartPath is not null ? "#7ba05b" : part.Asset.IsSm1 ? "#6b8fb5" : "#b58a4b";
            sb.Append("  <polygon points=\"").Append(string.Join(' ', poly.Select(Pt))).Append("\" fill=\"").Append(fill)
              .Append("\" fill-opacity=\"0.55\" stroke=\"").Append(fill).Append("\" stroke-width=\"1\"><title>").Append(SecurityElement.Escape(part.ObjectId + " " + part.Name)).Append("</title></polygon>\n");
        }
        foreach (var group in shapes.GroupBy(s => s.Part.ObjectId))
        {
            var first = group.First();
            var c = P(group.Select(g => g.Center).Aggregate(Vector2.Zero, (a, v) => a + v) / group.Count());
            sb.Append("  <text x=\"").Append(N(c.X)).Append("\" y=\"").Append(N(c.Y)).Append("\" fill=\"#e6e8eb\" font-family=\"sans-serif\" font-size=\"11\" text-anchor=\"middle\">")
              .Append(SecurityElement.Escape(first.Part.Name)).Append("</text>\n");
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}
