using System;
using System.IO;

namespace SmClient.Customizer.Model;

/// <summary>The formats the emblem importer accepts (the extension list of <see cref="EmblemPipeline.Extensions"/>) plus GIF, which is recognised only to be refused.</summary>
public enum ImageKind { Unknown, Png, Jpeg, Webp, Bmp, Gif, Tga }

/// <summary>
/// Reads an image's kind and pixel size from its header bytes alone, so a hostile or huge file is refused before any decoder allocates for it (engine-free;
/// docs/customizer-design.md E1: at most 4096 pixels a side). Returns false for anything it cannot parse; callers treat that as "can't read this file".
/// </summary>
public static class ImageHeader
{
    /// <summary>The kind from the magic bytes; a file with no magic is a TGA only when its extension says so (TGA has no signature).</summary>
    public static ImageKind Sniff(ReadOnlySpan<byte> d, string path)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return ImageKind.Png;
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ImageKind.Jpeg;
        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return ImageKind.Webp;
        if (d.Length >= 2 && d[0] == 'B' && d[1] == 'M') return ImageKind.Bmp;
        if (d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return ImageKind.Gif;
        if (Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase) && d.Length >= 18) return ImageKind.Tga;
        return ImageKind.Unknown;
    }

    static int U16LE(ReadOnlySpan<byte> d, int at) => d[at] | d[at + 1] << 8;
    static int U16BE(ReadOnlySpan<byte> d, int at) => d[at] << 8 | d[at + 1];
    static long U32BE(ReadOnlySpan<byte> d, int at) => (long)d[at] << 24 | (long)d[at + 1] << 16 | (long)d[at + 2] << 8 | d[at + 3];
    static long U32LE(ReadOnlySpan<byte> d, int at) => d[at] | (long)d[at + 1] << 8 | (long)d[at + 2] << 16 | (long)d[at + 3] << 24;

    /// <summary>The width and height from the header; false when the header is too short, wrong or names a size of zero.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> d, ImageKind kind, out int width, out int height)
    {
        width = height = 0;
        long w = 0, h = 0;
        switch (kind)
        {
            case ImageKind.Png:
                // 8 signature bytes, then the IHDR chunk: length, "IHDR", width, height (big endian).
                if (d.Length < 24 || d[12] != 'I' || d[13] != 'H' || d[14] != 'D' || d[15] != 'R') return false;
                w = U32BE(d, 16); h = U32BE(d, 20);
                break;
            case ImageKind.Jpeg:
                if (!TryJpeg(d, out w, out h)) return false;
                break;
            case ImageKind.Bmp:
                if (d.Length < 26) return false;
                long dib = U32LE(d, 14);
                if (dib == 12) { w = U16LE(d, 18); h = U16LE(d, 20); }
                else if (dib >= 40) { w = (int)U32LE(d, 18); h = (int)U32LE(d, 22); if (w < 0) return false; h = Math.Abs(h); }
                else return false;
                break;
            case ImageKind.Gif:
                if (d.Length < 10) return false;
                w = U16LE(d, 6); h = U16LE(d, 8);
                break;
            case ImageKind.Tga:
                if (d.Length < 18) return false;
                w = U16LE(d, 12); h = U16LE(d, 14);
                break;
            case ImageKind.Webp:
                if (!TryWebp(d, out w, out h)) return false;
                break;
            default: return false;
        }
        if (w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue) return false;
        width = (int)w; height = (int)h;
        return true;
    }

    static bool TryJpeg(ReadOnlySpan<byte> d, out long w, out long h)
    {
        w = h = 0;
        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            int marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }                                           // fill byte
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }   // markers without a length
            int length = U16BE(d, i + 2);
            if (length < 2) return false;
            bool frame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (frame)
            {
                if (i + 9 > d.Length) return false;
                h = U16BE(d, i + 5); w = U16BE(d, i + 7);
                return true;
            }
            if (marker == 0xDA) return false;                                                 // start of scan before any frame header
            i += 2 + length;
        }
        return false;
    }

    static bool TryWebp(ReadOnlySpan<byte> d, out long w, out long h)
    {
        w = h = 0;
        if (d.Length < 30) return false;
        string chunk = new(new[] { (char)d[12], (char)d[13], (char)d[14], (char)d[15] });
        switch (chunk)
        {
            case "VP8 ":
                if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return false;
                w = U16LE(d, 26) & 0x3FFF; h = U16LE(d, 28) & 0x3FFF;
                return true;
            case "VP8L":
                if (d[20] != 0x2F) return false;
                long bits = U32LE(d, 21);
                w = (bits & 0x3FFF) + 1; h = ((bits >> 14) & 0x3FFF) + 1;
                return true;
            case "VP8X":
                w = (d[24] | d[25] << 8 | d[26] << 16) + 1L; h = (d[27] | d[28] << 8 | d[29] << 16) + 1L;
                return true;
            default: return false;
        }
    }
}
