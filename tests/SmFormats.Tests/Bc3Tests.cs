using SmFormats.Dds;
using Xunit.Abstractions;

namespace SmFormats.Tests;

/// <summary>BC3 codec tests on synthetic images only (no original bytes).</summary>
public class Bc3Tests(ITestOutputHelper output)
{
    const int N = 128;

    static byte[] Image(int w, int h, Func<int, int, (int r, int g, int b, int a)> f)
    {
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var (r, g, b, a) = f(x, y);
            int o = (y * w + x) * 4;
            data[o] = (byte)r; data[o + 1] = (byte)g; data[o + 2] = (byte)b; data[o + 3] = (byte)a;
        }
        return data;
    }

    // RMSE over the given channels (0..3 = R, G, B, A) and the largest single difference.
    static (double rmse, int max) Error(byte[] a, byte[] b, params int[] channels)
    {
        double sum = 0;
        int max = 0, n = 0;
        for (int i = 0; i < a.Length; i += 4)
            foreach (int c in channels)
            {
                int d = a[i + c] - b[i + c];
                sum += d * d;
                max = Math.Max(max, Math.Abs(d));
                n++;
            }
        return (Math.Sqrt(sum / n), max);
    }

    static byte[] RoundTrip(byte[] rgba, int w = N, int h = N) => Bc3.Decode(Bc3.Encode(rgba, w, h), w, h);

    static int E5(int v) => v << 3 | v >> 2;
    static int E6(int v) => v << 2 | v >> 4;

    [Fact]
    public void Emblem_size_is_16384_bytes_and_other_sizes_follow_the_block_count()
    {
        Assert.Equal(16384, Bc3.Encode(new byte[N * N * 4], N, N).Length);
        Assert.Equal(16384, Bc3.EncodedLength(N, N));
        Assert.Equal(16, Bc3.Encode(new byte[4 * 4 * 4], 4, 4).Length);
        Assert.Equal(8 * 16, Bc3.Encode(new byte[8 * 16 * 4], 8, 16).Length);
        Assert.Equal(N * N * 4, Bc3.Decode(new byte[16384], N, N).Length);
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        var rng = new Random(7);
        var noise = new byte[N * N * 4];
        rng.NextBytes(noise);
        var first = Bc3.Encode(noise, N, N);
        for (int i = 0; i < 3; i++) Assert.Equal(first, Bc3.Encode(noise, N, N));
        var grad = Image(N, N, (x, y) => (x * 2, y * 2, (x + y), 255 - x));
        Assert.Equal(Bc3.Encode(grad, N, N), Bc3.Encode(grad, N, N));
    }

    [Fact]
    public void Flat_colours_that_the_format_can_hold_are_exact()
    {
        (int, int, int, int)[] colours =
        [
            (0, 0, 0, 0), (255, 255, 255, 255), (255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255),
            (E5(10), E6(20), E5(3), 77), (E5(31), E6(1), E5(17), 1), (E5(5), E6(33), E5(30), 254),
        ];
        foreach (var (r, g, b, a) in colours)
        {
            var src = Image(N, N, (_, _) => (r, g, b, a));
            var back = RoundTrip(src);
            Assert.Equal(src, back);
        }
    }

    [Fact]
    public void Arbitrary_flat_colours_are_reproduced_within_a_few_levels_and_alpha_exactly()
    {
        int worst = 0;
        for (int v = 0; v < 256; v += 5)
        {
            int r = v, g = 255 - v, b = (v * 7) & 255, a = (v * 3) & 255;
            var src = Image(8, 8, (_, _) => (r, g, b, a));
            var back = RoundTrip(src, 8, 8);
            var (_, rgbMax) = Error(src, back, 0, 1, 2);
            var (_, aMax) = Error(src, back, 3);
            Assert.Equal(0, aMax);
            worst = Math.Max(worst, rgbMax);
        }
        output.WriteLine($"flat colours: worst channel error {worst}");
        Assert.True(worst <= 3, $"worst {worst}");
    }

    [Fact]
    public void Colour_gradients_round_trip_within_bounds()
    {
        var cases = new (string name, Func<int, int, (int, int, int, int)> f, double rmse)[]
        {
            ("grey ramp", (x, y) => (x * 2, x * 2, x * 2, 255), 2.0),
            ("red-green ramp", (x, y) => (x * 2, 255 - x * 2, 0, 255), 2.5),
            ("diagonal rgb", (x, y) => (x * 2, y * 2, (x + y), 255), 2.5),
            ("vertical blue-yellow", (x, y) => (y * 2, y * 2, 255 - y * 2, 255), 2.5),
        };
        foreach (var (name, f, bound) in cases)
        {
            var src = Image(N, N, f);
            var (rmse, max) = Error(src, RoundTrip(src), 0, 1, 2);
            output.WriteLine($"{name}: rgb rmse {rmse:F3} max {max}");
            Assert.True(rmse <= bound, $"{name}: rmse {rmse}");
        }
    }

    [Fact]
    public void Alpha_gradients_round_trip_within_bounds()
    {
        var horizontal = Image(N, N, (x, y) => (200, 100, 50, x * 2));
        var (r1, m1) = Error(horizontal, RoundTrip(horizontal), 3);
        var radial = Image(N, N, (x, y) =>
        {
            double d = Math.Sqrt((x - 64.0) * (x - 64.0) + (y - 64.0) * (y - 64.0));
            return (255, 255, 255, (int)Math.Clamp(255 - d * 4, 0, 255));
        });
        var (r2, m2) = Error(radial, RoundTrip(radial), 3);
        var steep = Image(N, N, (x, y) => (0, 0, 0, x < 64 ? 40 : 220));
        var (r3, m3) = Error(steep, RoundTrip(steep), 3);
        output.WriteLine($"alpha ramp rmse {r1:F3} max {m1}; radial rmse {r2:F3} max {m2}; step rmse {r3:F3} max {m3}");
        Assert.True(r1 <= 1.0, $"ramp {r1}");
        Assert.True(r2 <= 1.0, $"radial {r2}");
        Assert.Equal(0, m3);
    }

    [Fact]
    public void Binary_alpha_is_exact()
    {
        var disc = Image(N, N, (x, y) =>
        {
            bool inside = (x - 64) * (x - 64) + (y - 64) * (y - 64) < 50 * 50;
            return (inside ? 220 : 30, 90, inside ? 10 : 200, inside ? 255 : 0);
        });
        var checker = Image(N, N, (x, y) => (255, 0, 255, ((x / 3 + y / 5) & 1) == 0 ? 255 : 0));
        foreach (var src in new[] { disc, checker })
        {
            var back = RoundTrip(src);
            var (_, max) = Error(src, back, 3);
            Assert.Equal(0, max);
        }
        var (rmse, _) = Error(disc, RoundTrip(disc), 0, 1, 2);
        output.WriteLine($"disc colour rmse {rmse:F3}");
        Assert.True(rmse <= 1.5, $"disc rmse {rmse}");
    }

    [Fact]
    public void Random_noise_error_is_bounded()
    {
        var rng = new Random(12345);
        var src = new byte[N * N * 4];
        rng.NextBytes(src);
        var back = RoundTrip(src);
        var (rgb, rgbMax) = Error(src, back, 0, 1, 2);
        var (alpha, alphaMax) = Error(src, back, 3);
        output.WriteLine($"noise: rgb rmse {rgb:F2} max {rgbMax}; alpha rmse {alpha:F2} max {alphaMax}");
        // uniform noise has no structure to exploit; the bound only guards against a broken fit
        Assert.True(rgb <= 56, $"rgb {rgb}");
        Assert.True(alpha <= 10, $"alpha {alpha}");
    }

    [Fact]
    public void Smooth_noise_on_a_gradient_is_bounded()
    {
        var rng = new Random(99);
        var src = Image(N, N, (x, y) => (Math.Clamp(x * 2 + rng.Next(-6, 7), 0, 255), Math.Clamp(y * 2 + rng.Next(-6, 7), 0, 255),
            Math.Clamp(128 + rng.Next(-6, 7), 0, 255), Math.Clamp(x * 2 + rng.Next(-6, 7), 0, 255)));
        var back = RoundTrip(src);
        var (rgb, _) = Error(src, back, 0, 1, 2);
        var (alpha, _) = Error(src, back, 3);
        output.WriteLine($"noisy gradient: rgb rmse {rgb:F2}; alpha rmse {alpha:F2}");
        Assert.True(rgb <= 4.5, $"rgb {rgb}");
        Assert.True(alpha <= 1.5, $"alpha {alpha}");
    }

    [Fact]
    public void Colour_endpoints_are_ordered_so_four_and_three_colour_readings_agree()
    {
        var rng = new Random(5);
        var src = new byte[N * N * 4];
        rng.NextBytes(src);
        var data = Bc3.Encode(src, N, N);
        for (int b = 0; b < data.Length; b += 16)
        {
            int c0 = data[b + 8] | data[b + 9] << 8;
            int c1 = data[b + 10] | data[b + 11] << 8;
            Assert.True(c0 >= c1);
            if (c0 == c1) Assert.Equal(0u, (uint)(data[b + 12] | data[b + 13] | data[b + 14] | data[b + 15]));
        }
    }

    [Fact]
    public void Decodes_a_hand_built_block_in_both_alpha_modes()
    {
        // Interpolated alpha uses integer truncation (the usual decoder convention; a rounding reference differs by at most 1).
        // colour: c0 = pure red (0xF800), c1 = pure blue (0x001F); indices pixel p -> p & 3
        // alpha 8-value mode: a0 = 255 > a1 = 0, indices pixel p -> p & 7 (p < 8), repeated
        var block = new byte[16];
        block[0] = 255; block[1] = 0;
        ulong aIdx = 0;
        for (int p = 0; p < 16; p++) aIdx |= (ulong)(p & 7) << (3 * p);
        for (int i = 0; i < 6; i++) block[2 + i] = (byte)(aIdx >> (8 * i));
        block[8] = 0x00; block[9] = 0xF8; block[10] = 0x1F; block[11] = 0x00;
        uint cIdx = 0;
        for (int p = 0; p < 16; p++) cIdx |= (uint)(p & 3) << (2 * p);
        block[12] = (byte)cIdx; block[13] = (byte)(cIdx >> 8); block[14] = (byte)(cIdx >> 16); block[15] = (byte)(cIdx >> 24);
        var px = Bc3.Decode(block, 4, 4);
        int[] expectedAlpha8 = [255, 0, 218, 182, 145, 109, 72, 36];
        for (int p = 0; p < 16; p++) Assert.Equal(expectedAlpha8[p & 7], px[p * 4 + 3]);
        Assert.Equal([255, 0, 0], px[0..3]);
        Assert.Equal([0, 0, 255], px[4..7]);
        Assert.Equal([170, 0, 85], px[8..11]);
        Assert.Equal([85, 0, 170], px[12..15]);

        // 6-value mode: a0 = 10 <= a1 = 100; indices 6 and 7 are 0 and 255
        block[0] = 10; block[1] = 100;
        px = Bc3.Decode(block, 4, 4);
        int[] expectedAlpha6 = [10, 100, 28, 46, 64, 82];
        // pixels 0..7 use indices 0..7
        for (int p = 0; p < 6; p++) Assert.Equal(expectedAlpha6[p], px[p * 4 + 3]);
        Assert.Equal(0, px[6 * 4 + 3]);
        Assert.Equal(255, px[7 * 4 + 3]);
    }

    [Fact]
    public void Invalid_sizes_and_lengths_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Bc3.Encode(new byte[16], 0, 4));
        Assert.Throws<ArgumentException>(() => Bc3.Encode(new byte[16], 4, 0));
        Assert.Throws<ArgumentException>(() => Bc3.Encode(new byte[3 * 4 * 4], 3, 4));
        Assert.Throws<ArgumentException>(() => Bc3.Encode(new byte[4 * 5 * 4], 4, 5));
        Assert.Throws<ArgumentException>(() => Bc3.Encode(new byte[16], -4, -1));
        Assert.Throws<ArgumentException>(() => Bc3.Encode(new byte[N * N * 4 - 4], N, N));
        Assert.Throws<ArgumentException>(() => Bc3.Encode(new byte[N * N * 4 + 4], N, N));
        Assert.Throws<ArgumentException>(() => Bc3.Decode(new byte[16384 - 1], N, N));
        Assert.Throws<ArgumentException>(() => Bc3.Decode(new byte[16384 + 16], N, N));
        Assert.Throws<ArgumentException>(() => Bc3.Decode(new byte[16], 6, 4));
        Assert.Throws<ArgumentException>(() => Bc3.EncodedLength(0, 0));
        Assert.Throws<ArgumentException>(() => Bc3.EncodedLength(1 << 15, 1 << 15));
    }

    [Fact]
    public void Decoding_arbitrary_bytes_never_throws_and_stays_in_range()
    {
        var rng = new Random(3);
        var data = new byte[16384];
        rng.NextBytes(data);
        var rgba = Bc3.Decode(data, N, N);
        Assert.Equal(N * N * 4, rgba.Length);
        // re-encoding decoded data is stable in length
        Assert.Equal(16384, Bc3.Encode(rgba, N, N).Length);
    }
}
