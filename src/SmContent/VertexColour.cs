using System.Numerics;

namespace SmContent;

/// <summary>
/// The DCM per-vertex <c>u32 colour</c> and the two-layer blend rules built on it (docs/discovery.md, "Second layers").
/// Bytes are B, G, R, A (D3DCOLOR order; supported by evidence: a rust-coloured junk pile reads warm only this way). RGB is
/// mostly one grey value, a tint (white = none); A is the second layer's blend input, 255 selecting layer 1.
/// The blend formulas are a **provisional approximation**; the client's shader repeats <see cref="Layer2Weight"/>.
/// </summary>
public static class VertexColour
{
    /// <summary>(R, G, B, A) as 0 to 1 from the stored u32 (little-endian bytes B, G, R, A).</summary>
    public static Vector4 Unpack(uint colour) => new(
        ((colour >> 16) & 255) / 255f, ((colour >> 8) & 255) / 255f, (colour & 255) / 255f, (colour >> 24) / 255f);

    /// <summary>The colour of a vertex with no stored colour: opaque white (no tint, layer 1).</summary>
    public const uint None = 0xFFFFFFFFu;

    public enum BlendKind
    {
        /// <summary><c>vtxblend</c>: weight = 1 - alpha.</summary>
        Vertex,
        /// <summary><c>advvtxbld</c>: alpha perturbed by a noise texture, then sharpened by a contrast.</summary>
        VertexWithNoise,
        /// <summary><c>mskblend</c>: the mask texture is the weight.</summary>
        Mask,
    }

    /// <summary>
    /// The weight of layer 2 (0 = layer 1 only, 1 = layer 2 only). <paramref name="alpha"/> is the vertex alpha (0 to 1),
    /// <paramref name="mask"/> the <c>AlphaMaskMap</c> sample (0 to 1), <paramref name="contrast"/> and <paramref name="amplitude"/> the first two
    /// <c>VtxBlendParams</c> values. Advanced: clamp(((1 - alpha + (mask - 0.5) * amplitude) - 0.5) * contrast + 0.5).
    /// </summary>
    public static float Layer2Weight(BlendKind kind, float alpha, float mask, float contrast, float amplitude)
    {
        float w = 1f - Math.Clamp(alpha, 0f, 1f);
        return kind switch
        {
            BlendKind.Vertex => w,
            BlendKind.VertexWithNoise => Math.Clamp((w + (mask - 0.5f) * amplitude - 0.5f) * contrast + 0.5f, 0f, 1f),
            _ => Math.Clamp(mask, 0f, 1f),
        };
    }
}
