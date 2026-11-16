using System.IO.Compression;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Fortress;

namespace SmNet.Tests.Extras.Warfront.Fortress;

/// <summary>
/// Test aid (not a check): draws the fortress map's overhead view as a PNG, with or without the roofs, the defence positions, the landing zone, the Ork sources and the defenders' posts, and an optional route.
/// The tests write it only when the environment variable <c>WF_FORTRESS_OVERHEAD_DIR</c> names a folder (media goes outside the repository).
/// </summary>
internal static class WarfrontFortressOverhead
{
    const float Scale = 2f;   // pixels per metre

    public static void Write(string path, WarfrontFortressBuild map, bool roofs, IReadOnlyList<Vector3>? route)
    {
        float x0 = map.WalkArea.Min.X, y1 = map.WalkArea.Max.Y;
        int w = (int)((map.WalkArea.Max.X - map.WalkArea.Min.X) * Scale), h = (int)((map.WalkArea.Max.Y - map.WalkArea.Min.Y) * Scale);
        var z = new float[w * h];
        var rgb = new byte[w * h * 3];
        Array.Fill(z, float.NegativeInfinity);
        var skip = roofs ? new HashSet<int>() : new HashSet<int>(map.RoofParts);
        for (int i = 0; i < map.Level.Parts.Count; i++)
        {
            var part = map.Level.Parts[i];
            if (!part.Solid || part.Kind == AshfallKind.Boundary || skip.Contains(i)) continue;
            var c = Colour(part.Kind);
            for (int t = 0; t + 2 < part.Corners.Length; t += 3) Fill(part.Corners[t], part.Corners[t + 1], part.Corners[t + 2], c, z, rgb, w, h, x0, y1);
        }
        void Dot(Vector3 p, int radius, byte r, byte g, byte b)
        {
            int cx = (int)((p.X - x0) * Scale), cy = (int)((y1 - p.Y) * Scale);
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius) continue;
                    int px = cx + dx, py = cy + dy;
                    if (px < 0 || py < 0 || px >= w || py >= h) continue;
                    int o = (py * w + px) * 3;
                    rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b;
                }
        }
        if (route is not null)
            for (int i = 0; i + 1 < route.Count; i++)
                for (int s = 0; s <= 40; s++) Dot(Vector3.Lerp(route[i], route[i + 1], s / 40f), 1, 255, 255, 255);
        foreach (var s in map.Sources) Dot(s.Position, 3, 220, 40, 200);
        foreach (var p in map.Posts) Dot(p.Position, 2, 40, 220, 220);
        foreach (var pt in map.Level.Points) Dot(pt.Center, 7, 255, 150, 30);
        Dot(map.BossSpot, 6, 255, 40, 40);
        Dot(map.LandingZone, 5, 60, 255, 60);
        Png(path, rgb, w, h);
    }

    static (byte R, byte G, byte B) Colour(AshfallKind k) => k switch
    {
        AshfallKind.Ground => (120, 110, 95), AshfallKind.Wall => (150, 150, 155), AshfallKind.Stronghold => (170, 140, 110), AshfallKind.Cover => (200, 190, 120),
        AshfallKind.Floor => (140, 130, 150), AshfallKind.Stair => (190, 120, 90), _ => (130, 130, 130),
    };

    static void Fill(Vector3 a, Vector3 b, Vector3 c, (byte R, byte G, byte B) col, float[] zb, byte[] rgb, int w, int h, float x0, float y1)
    {
        Vector2 P(Vector3 v) => new((v.X - x0) * Scale, (y1 - v.Y) * Scale);
        var pa = P(a); var pb = P(b); var pc = P(c);
        float den = (pb.Y - pc.Y) * (pa.X - pc.X) + (pc.X - pb.X) * (pa.Y - pc.Y);
        if (MathF.Abs(den) < 1e-6f) return;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X)))), maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y)))), maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float l1 = ((pb.Y - pc.Y) * (px - pc.X) + (pc.X - pb.X) * (py - pc.Y)) / den;
                float l2 = ((pc.Y - pa.Y) * (px - pc.X) + (pa.X - pc.X) * (py - pc.Y)) / den;
                float l3 = 1f - l1 - l2;
                if (l1 < -0.001f || l2 < -0.001f || l3 < -0.001f) continue;
                float zz = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                int i = y * w + x;
                if (zz < zb[i]) continue;
                zb[i] = zz;
                float shade = Math.Clamp(0.55f + 0.45f * zz / 32f, 0.4f, 1f);
                rgb[i * 3] = (byte)(col.R * shade); rgb[i * 3 + 1] = (byte)(col.G * shade); rgb[i * 3 + 2] = (byte)(col.B * shade);
            }
    }

    static void Png(string path, byte[] rgb, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++) { raw.WriteByte(0); raw.Write(rgb, y * w * 3, w * 3); }
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw.GetBuffer(), 0, (int)raw.Length);
        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> head = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head[4..], h);
        head[8] = 8; head[9] = 2; head[10] = 0; head[11] = 0; head[12] = 0;
        Chunk(f, "IHDR", head);
        Chunk(f, "IDAT", packed.GetBuffer().AsSpan(0, (int)packed.Length));
        Chunk(f, "IEND", []);
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        data.CopyTo(body.AsSpan(4));
        s.Write(body);
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in body) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1; }
        Span<byte> c = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(c, ~crc);
        s.Write(c);
    }
}
