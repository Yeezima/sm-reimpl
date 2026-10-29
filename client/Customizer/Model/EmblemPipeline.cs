using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SmContent.Customization;
using SmFormats.Dds;
using SmSim;

namespace SmClient.Customizer.Model;

/// <summary>
/// The custom emblem import without any image decoder (docs/customizer-architecture.md decision 5, design F7): the limits and their messages (checked on the
/// header bytes before any decoding), the square crop (pan and zoom), the Lanczos resample to 128 x 128 that touches only the cropped region, the BC3 encoding
/// (exactly <see cref="EmblemHash.BlobBytes"/> bytes) and the "My emblems" library on disk. Decoding the picked file into RGBA is the Godot side's job; everything
/// else is here and tested.
/// </summary>
public static class EmblemPipeline
{
    public const long MaxFileBytes = 8L * 1024 * 1024;
    public const int MaxSide = 4096, OutSide = EmblemHash.Side, MaxLibrary = 8;
    public const float MaxZoom = 8f;
    /// <summary>The share of the square the whole picture takes in the default crop: the pad's curvature clips the edge of a 128 x 128 emblem, so the default leaves about a fifth free.</summary>
    public const float InsetFraction = 0.8f;

    /// <summary>The picked file formats (lower case, no dot).</summary>
    public static readonly IReadOnlyList<string> Extensions = ["png", "jpg", "jpeg", "webp", "bmp", "tga"];

    public const string ErrorUnreadable = "Can't read this file", ErrorFormat = "Unsupported format (use PNG, JPG, WebP, BMP or TGA)",
        ErrorTooLarge = "Image too large", ErrorFileTooLarge = "File too large", ErrorAnimated = "Animated images are not supported";

    /// <summary>The limits as the customizer states them next to "Custom image..." (a tester's request: the refusal named no limit).</summary>
    public static string LimitsText =>
        $"PNG, JPG, WebP, BMP or TGA, at most {MaxSide} x {MaxSide} pixels and {MaxFileBytes / (1024 * 1024)} MB. It is resized to {OutSide} x {OutSide} on the pad; a square picture fits best.";

    /// <summary>A message for a file that cannot be used by its name and size alone, else null. A too large file's message names its size and the limit.</summary>
    public static string? CheckFile(string path, long length)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!Extensions.Contains(ext)) return ErrorFormat;
        if (length <= 0) return ErrorUnreadable;
        return length > MaxFileBytes
            ? string.Create(CultureInfo.InvariantCulture,
                $"{ErrorFileTooLarge}: {Math.Ceiling(length * 10.0 / (1024 * 1024)) / 10:0.0} MB (at most {MaxFileBytes / (1024 * 1024)} MB)")   // rounded up: never "8.0 MB" over 8
            : null;
    }

    /// <summary>A message for an image that is empty or too big, else null. A too big image's message names its pixel size and the limit.</summary>
    public static string? CheckImage(int width, int height) =>
        width <= 0 || height <= 0 ? ErrorUnreadable
        : width > MaxSide || height > MaxSide ? $"{ErrorTooLarge}: {width} x {height} pixels (at most {MaxSide} x {MaxSide})"
        : null;

    /// <summary>The result of <see cref="Inspect"/>: an error text, or the kind and size to decode.</summary>
    public readonly record struct Inspection(string? Error, ImageKind Kind, int Width, int Height);

    /// <summary>
    /// Checks a whole file's bytes before any decoder sees them: animated images, an unknown format, an unreadable header, zero or more than <see cref="MaxSide"/>
    /// pixels a side. The format comes from the magic bytes (a TGA from its extension), not from the extension alone.
    /// </summary>
    public static Inspection Inspect(ReadOnlySpan<byte> file, string path)
    {
        if (IsAnimated(file)) return new(ErrorAnimated, ImageKind.Gif, 0, 0);
        var kind = ImageHeader.Sniff(file, path);
        if (kind is ImageKind.Unknown) return new(ErrorUnreadable, kind, 0, 0);
        if (kind is ImageKind.Gif) return new(ErrorFormat, kind, 0, 0);
        if (!ImageHeader.TryReadSize(file, kind, out int w, out int h)) return new(ErrorUnreadable, kind, 0, 0);
        return new(CheckImage(w, h), kind, w, h);
    }

    /// <summary>Whether the file's bytes are an animated image (an animated WebP or GIF behind a wrong extension): refused.</summary>
    public static bool IsAnimated(ReadOnlySpan<byte> file)
    {
        if (file.Length >= 6 && file[0] == 'G' && file[1] == 'I' && file[2] == 'F') return true;
        if (file.Length >= 21 && file[0] == 'R' && file[1] == 'I' && file[2] == 'F' && file[3] == 'F' && file[8] == 'W' && file[9] == 'E' && file[10] == 'B' && file[11] == 'P')
        {
            // VP8X header flags: bit 1 (0x02) is the animation flag.
            return file[12] == 'V' && file[13] == 'P' && file[14] == '8' && file[15] == 'X' && (file[20] & 0x02) != 0;
        }
        // PNG: an acTL chunk before the first IDAT marks an APNG.
        if (file.Length > 16 && file[0] == 0x89 && file[1] == 'P' && file[2] == 'N' && file[3] == 'G')
        {
            int at = 8;
            while (at + 12 <= file.Length)
            {
                uint length = (uint)(file[at] << 24 | file[at + 1] << 16 | file[at + 2] << 8 | file[at + 3]);
                var type = file.Slice(at + 4, 4);
                if (type.SequenceEqual("acTL"u8)) return true;
                if (type.SequenceEqual("IDAT"u8)) return false;
                long next = (long)at + 12 + length;
                if (next > file.Length) return false;
                at = (int)next;
            }
        }
        return false;
    }

    // ---- the crop ----

    /// <summary>The square window over the source image, in source pixels (it may extend past the image: transparent there).</summary>
    public readonly record struct Crop(float X, float Y, float Side)
    {
        public float Right => X + Side;
        public float Bottom => Y + Side;
    }

    /// <summary>The zoom at which the whole image just fits the square (the shorter side is padded).</summary>
    public static float FitZoom(int width, int height) => (float)Math.Min(width, height) / Math.Max(width, height);

    /// <summary>The default zoom: the whole picture inside the square with a margin (<see cref="InsetFraction"/> of the square), so the pad's curvature does not clip it.</summary>
    public static float InsetZoom(int width, int height) => FitZoom(width, height) * InsetFraction;

    /// <summary>The smallest zoom: the whole image at half the size of <see cref="FitZoom"/> (a wide margin).</summary>
    public static float MinZoom(int width, int height) => FitZoom(width, height) * 0.5f;

    /// <summary>
    /// The crop for a zoom (1 = the shorter side fills the square, <see cref="MaxZoom"/> = an eighth of it, <see cref="FitZoom"/> = the whole image fits, down to
    /// <see cref="MinZoom"/>) and a centre in source pixels. The centre is kept so that the window stays over the image (when it is smaller than the image's side)
    /// or on its centre (when it is larger).
    /// </summary>
    public static Crop CropFor(int width, int height, float zoom, float centerX, float centerY)
    {
        zoom = Math.Clamp(zoom, MinZoom(width, height), MaxZoom);
        float side = Math.Min(width, height) / zoom;
        float cx = side >= width ? width / 2f : Math.Clamp(centerX, side / 2f, width - side / 2f);
        float cy = side >= height ? height / 2f : Math.Clamp(centerY, side / 2f, height - side / 2f);
        return new Crop(cx - side / 2f, cy - side / 2f, side);
    }

    // ---- resampling ----

    /// <summary>
    /// Resamples the crop of an RGBA8 image (straight alpha) to <paramref name="outSide"/> x <paramref name="outSide"/> RGBA8 with a Lanczos-3 filter (stretched
    /// when shrinking, on premultiplied colour so transparent pixels do not bleed). Only the cropped region (plus the filter's reach) is converted and filtered, so
    /// a 4096 x 4096 source costs as much as its crop. Pixels outside the image are transparent. Deterministic.
    /// </summary>
    public static byte[] Resample(ReadOnlySpan<byte> rgba, int width, int height, Crop crop, int outSide = OutSide)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) throw new ArgumentException("The image is empty or too short.", nameof(rgba));
        if (!(crop.Side > 0f)) throw new ArgumentException("The crop is empty.", nameof(crop));

        // A large shrink is cheaper and as good with an integer box reduction first (area average), then the Lanczos on the smaller picture.
        float scale = crop.Side / outSide;
        int reduce = Math.Max(1, (int)Math.Floor(scale / 2f));
        float margin = 3f * Math.Max(reduce, scale) + reduce;
        int x0 = Math.Max(0, (int)MathF.Floor(crop.X - margin)), y0 = Math.Max(0, (int)MathF.Floor(crop.Y - margin));
        int x1 = Math.Min(width, (int)MathF.Ceiling(crop.Right + margin)), y1 = Math.Min(height, (int)MathF.Ceiling(crop.Bottom + margin));
        var result = new byte[outSide * outSide * 4];
        if (x1 <= x0 || y1 <= y0) return result;          // the crop is entirely outside the image: transparent

        var (src, w, h) = reduce > 1 ? BoxReduceRect(rgba, width, x0, y0, x1 - x0, y1 - y0, reduce) : (PremultiplyRect(rgba, width, x0, y0, x1 - x0, y1 - y0), x1 - x0, y1 - y0);
        var local = new Crop((crop.X - x0) / reduce, (crop.Y - y0) / reduce, crop.Side / reduce);
        scale /= reduce;

        float support = 3f * Math.Max(1f, scale);
        var xs = Taps(local.X, scale, outSide, w, support);
        var ys = Taps(local.Y, scale, outSide, h, support);
        // Horizontal pass for the rows the vertical taps need.
        int rowMin = int.MaxValue, rowMax = int.MinValue;
        foreach (var t in ys) { if (t.Count == 0) continue; rowMin = Math.Min(rowMin, t.First); rowMax = Math.Max(rowMax, t.First + t.Count - 1); }
        if (rowMin > rowMax) return result;
        int rows = rowMax - rowMin + 1;
        var tmp = new float[rows * outSide * 4];
        for (int r = 0; r < rows; r++)
        {
            int sy = rowMin + r;
            for (int ox = 0; ox < outSide; ox++)
            {
                var t = xs[ox];
                float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                for (int k = 0; k < t.Count; k++)
                {
                    int i = (sy * w + t.First + k) * 4;
                    float wgt = t.Weights[k];
                    a0 += src[i] * wgt; a1 += src[i + 1] * wgt; a2 += src[i + 2] * wgt; a3 += src[i + 3] * wgt;
                }
                int o = (r * outSide + ox) * 4;
                tmp[o] = a0; tmp[o + 1] = a1; tmp[o + 2] = a2; tmp[o + 3] = a3;
            }
        }
        for (int oy = 0; oy < outSide; oy++)
        {
            var t = ys[oy];
            for (int ox = 0; ox < outSide; ox++)
            {
                float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                for (int k = 0; k < t.Count; k++)
                {
                    int i = ((t.First + k - rowMin) * outSide + ox) * 4;
                    float wgt = t.Weights[k];
                    a0 += tmp[i] * wgt; a1 += tmp[i + 1] * wgt; a2 += tmp[i + 2] * wgt; a3 += tmp[i + 3] * wgt;
                }
                float alpha = Math.Clamp(a3, 0f, 1f);
                int o = (oy * outSide + ox) * 4;
                if (alpha > 1f / 512f)
                {
                    result[o] = ToByte(a0 / alpha); result[o + 1] = ToByte(a1 / alpha); result[o + 2] = ToByte(a2 / alpha);
                }
                result[o + 3] = ToByte(alpha);
            }
        }
        return result;
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    readonly record struct Tap(int First, int Count, float[] Weights);

    static float Lanczos3(float x)
    {
        x = Math.Abs(x);
        if (x < 1e-6f) return 1f;
        if (x >= 3f) return 0f;
        float px = MathF.PI * x;
        return 3f * MathF.Sin(px) * MathF.Sin(px / 3f) / (px * px);
    }

    /// <summary>
    /// For each output pixel the source pixels it reads and their weights. An output pixel whose centre lies outside the image (the padding of a "fit" crop) reads
    /// nothing: transparent. One whose centre is inside takes only the pixels inside, renormalised, so the edge of the picture does not fade.
    /// </summary>
    static Tap[] Taps(float origin, float scale, int outSide, int size, float support)
    {
        var taps = new Tap[outSide];
        float stretch = Math.Max(1f, scale);
        for (int o = 0; o < outSide; o++)
        {
            float centre = origin + (o + 0.5f) * scale;     // in source pixels (pixel i covers [i, i+1))
            if (centre < 0f || centre >= size) { taps[o] = new Tap(0, 0, []); continue; }
            int first = Math.Max(0, (int)MathF.Floor(centre - support + 0.5f)), last = Math.Min(size - 1, (int)MathF.Floor(centre + support + 0.5f));
            var weights = new float[last - first + 1];
            float sum = 0;
            for (int i = first; i <= last; i++) { weights[i - first] = Lanczos3((i + 0.5f - centre) / stretch); sum += weights[i - first]; }
            if (sum <= 1e-6f) { taps[o] = new Tap(0, 0, []); continue; }
            for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
            taps[o] = new Tap(first, weights.Length, weights);
        }
        return taps;
    }

    static float[] PremultiplyRect(ReadOnlySpan<byte> rgba, int stride, int x0, int y0, int w, int h)
    {
        var f = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = ((y0 + y) * stride + x0 + x) * 4, d = (y * w + x) * 4;
                float a = rgba[s + 3] / 255f;
                f[d] = rgba[s] / 255f * a; f[d + 1] = rgba[s + 1] / 255f * a; f[d + 2] = rgba[s + 2] / 255f * a; f[d + 3] = a;
            }
        return f;
    }

    /// <summary>A box reduction by a whole factor of a rectangle of an RGBA8 image, straight to premultiplied floats (the full image is never converted).</summary>
    static (float[] Pixels, int Width, int Height) BoxReduceRect(ReadOnlySpan<byte> rgba, int stride, int x0, int y0, int width, int height, int k)
    {
        int w = (width + k - 1) / k, h = (height + k - 1) / k;
        var dst = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                int n = 0;
                for (int yy = y * k; yy < Math.Min(height, y * k + k); yy++)
                    for (int xx = x * k; xx < Math.Min(width, x * k + k); xx++)
                    {
                        int i = ((y0 + yy) * stride + x0 + xx) * 4;
                        float a = rgba[i + 3] / 255f;
                        a0 += rgba[i] / 255f * a; a1 += rgba[i + 1] / 255f * a; a2 += rgba[i + 2] / 255f * a; a3 += a;
                        n++;
                    }
                int o = (y * w + x) * 4;
                dst[o] = a0 / n; dst[o + 1] = a1 / n; dst[o + 2] = a2 / n; dst[o + 3] = a3 / n;
            }
        return (dst, w, h);
    }

    /// <summary>
    /// A smaller copy of an RGBA8 image for the live preview of the crop: a box reduction by the smallest whole factor that brings both sides to at most
    /// <paramref name="maxSide"/> (factor 1: the image itself). Crops of the full image are scaled by 1 / factor to match.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height, int Factor) Downscale(byte[] rgba, int width, int height, int maxSide)
    {
        int factor = 1;
        while (Math.Max(width, height) / factor > maxSide) factor++;
        if (factor == 1) return (rgba, width, height, 1);
        var (pixels, w, h) = BoxReduceRect(rgba, width, 0, 0, width, height, factor);
        var result = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            float a = pixels[i * 4 + 3];
            if (a > 1f / 512f) { result[i * 4] = ToByte(pixels[i * 4] / a); result[i * 4 + 1] = ToByte(pixels[i * 4 + 1] / a); result[i * 4 + 2] = ToByte(pixels[i * 4 + 2] / a); }
            result[i * 4 + 3] = ToByte(a);
        }
        return (result, w, h, factor);
    }

    /// <summary>
    /// Gives every fully transparent pixel the colour of its nearest visible neighbour (alpha stays 0): the BC3 colour endpoints of a block that is partly transparent
    /// then do not fade towards black, which would draw a dark fringe along the edge of the picture on the pad.
    /// </summary>
    public static byte[] BleedTransparent(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var result = rgba.ToArray();
        var done = new bool[width * height];
        var queue = new Queue<int>();
        for (int i = 0; i < width * height; i++) if (result[i * 4 + 3] > 0) { done[i] = true; queue.Enqueue(i); }
        if (queue.Count == 0 || queue.Count == width * height) return result;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % width, y = i / width;
            for (int n = 0; n < 4; n++)
            {
                int nx = x + (n == 0 ? -1 : n == 1 ? 1 : 0), ny = y + (n == 2 ? -1 : n == 3 ? 1 : 0);
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;
                int j = ny * width + nx;
                if (done[j]) continue;
                done[j] = true;
                result[j * 4] = result[i * 4]; result[j * 4 + 1] = result[i * 4 + 1]; result[j * 4 + 2] = result[i * 4 + 2];
                queue.Enqueue(j);
            }
        }
        return result;
    }

    /// <summary>The BC3 data of a 128 x 128 RGBA8 image (transparent pixels bled first, see <see cref="BleedTransparent"/>): exactly <see cref="EmblemHash.BlobBytes"/> bytes.</summary>
    public static byte[] ToBc3(ReadOnlySpan<byte> rgba128) => Bc3.Encode(BleedTransparent(rgba128, OutSide, OutSide), OutSide, OutSide);

    /// <summary>The whole pipeline after decoding: crop, resample, encode.</summary>
    public static byte[] Build(ReadOnlySpan<byte> rgba, int width, int height, Crop crop) => ToBc3(Resample(rgba, width, height, crop));

    // ---- "My emblems" ----

    /// <summary>The stored emblems, newest first (valid files only: 16,384 bytes whose hash is the name).</summary>
    public static IReadOnlyList<EmblemHash> Library(ProfileStore store)
    {
        string dir = Path.GetDirectoryName(store.EmblemPath(new EmblemHash(1, 0))) ?? "";
        if (!Directory.Exists(dir)) return [];
        var found = new List<(EmblemHash Hash, DateTime Written)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*" + ProfileStore.EmblemExtension))
                if (EmblemHash.TryParseHex(Path.GetFileNameWithoutExtension(file), out var hash) && !hash.IsNone && store.HasEmblem(hash))
                    found.Add((hash, File.GetLastWriteTimeUtc(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return found.OrderByDescending(f => f.Written).ThenBy(f => f.Hash.ToHex(), StringComparer.Ordinal).Select(f => f.Hash).ToList();
    }

    /// <summary>Stores an emblem; refused (null hash, message) when the library already holds <see cref="MaxLibrary"/> others.</summary>
    public static (EmblemHash Hash, string? Error) Add(ProfileStore store, ReadOnlySpan<byte> bc3)
    {
        if (bc3.Length != EmblemHash.BlobBytes) return (EmblemHash.None, ErrorUnreadable);
        var hash = EmblemHash.Of(bc3);
        var library = Library(store);
        if (!library.Contains(hash) && library.Count >= MaxLibrary) return (EmblemHash.None, $"You can keep {MaxLibrary} images. Delete one first.");
        try { return (store.SaveEmblem(bc3), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (EmblemHash.None, "Could not save the image: " + ex.Message); }
    }

    /// <summary>Deletes an emblem file and makes every pad that showed it show nothing (design E3).</summary>
    public static CustomizationProfile Remove(ProfileStore store, CustomizationProfile profile, EmblemHash hash)
    {
        try { File.Delete(store.EmblemPath(hash)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string hex = hash.ToHex();
        return profile with
        {
            Appearances = profile.Appearances.Select(a => a with
            {
                Decals = a.Decals.ToDictionary(kv => kv.Key, kv => kv.Value.Emblem is { } e && e.Equals(hex, StringComparison.OrdinalIgnoreCase) ? DecalChoice.NoDecal : kv.Value, StringComparer.Ordinal),
            }).ToList(),
        };
    }
}
