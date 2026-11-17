using System;

namespace SmClient.Bullets;

/// <summary>
/// plasma-soft-visuals (owner, 2026-11-14: "the trail of blue and purple is a hard line at the edges - can you soften the visual effects out and make them fade ... in a natural, gradient
/// way"; client presentation only, no simulation, wire, protocol or config-hash change; engine-free, tested; <c>--old-plasma-edges</c> restores the hard edges).
/// <para>
/// The install's plasma shots (tracers, charged trails, the cannon's trail, impacts) draw beams and streamers whose texture does not fall to zero at the long sides, so each ribbon
/// ends in a hard line. A plasma effect's beam and streamer quads now multiply their alpha by <see cref="Lateral"/> across their width (full in the core, 0 at the edge) and a ribbon fades in from its head over 0..<see cref="HeadTo"/> and out toward its tail over <see cref="TailFrom"/>..1 of its length (<see cref="Ribbon"/>); colours, sizes and timings are the data's. Ours (the original has the hard edge).
/// </para>
/// <para>
/// The same pass cuts the chambers' crawling arcs to <see cref="ArcLeash"/> metres from the gun: the data lets the cannon's <c>*_innate_arcs</c> spray (<see cref="LeashApplies"/>) offset its lightning up to 2.5 m
/// above the chamber, which read as a blue light floating over the carrier's head (owner, 2026-11-14).
/// </para>
/// </summary>
public static class PlasmaSoftEdges
{
    /// <summary>The lateral position (0 centre .. 1 edge) from which the alpha falls off; the core up to here stays as drawn.</summary>
    public const float CoreHalfWidth = 0.2f;
    /// <summary>plasma-cannon-shots (ours, estimate): the plasma cannon's own effects fall from the axis, squared, with no plateau.</summary>
    public const float CannonCoreHalfWidth = 0f;
    /// <summary>With a known length (metres): the fade-in at the head is at most this long, and the fade-out at the tail at most <see cref="TailMetres"/>, so a 20 to 40 m trail keeps its bright body (the data's colours already fade it along its length).</summary>
    public const float HeadMetres = 1.0f, TailMetres = 4.0f;
    /// <summary>The share of a ribbon's length (head 0 .. tail 1) from which it fades out.</summary>
    public const float TailFrom = 0.55f;
    /// <summary>The share of a ribbon's length (head 0 .. tail 1) over which it fades in from its head: a wide trail (the data's 4 to 6 m) no longer starts as a straight-cut slab.</summary>
    public const float HeadTo = 0.2f;
    /// <summary>How far (metres, after the effect's scale, as the spray places its particles) the arcs' spray may offset them from the marker.</summary>
    public const float ArcLeash = 0.35f;

    /// <summary>Whether an effect's beams and streamers get the soft edges: every plasma effect (shots, trails, tracers, impacts, chamber effects), either side.</summary>
    public static bool Applies(string? effectName) => effectName is { Length: > 0 } n && n.Contains("plasma", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an effect's sprays are leashed: only the chambers' crawling-arc effects (<c>plasma_cannon_innate_arcs</c>, Chaos <c>csm_plasma_cannon_innate_arcs</c>, any <c>*_innate_arcs</c>); the glows, smoke and vents keep their data positions.</summary>
    public static bool LeashApplies(string? effectName) => effectName is { Length: > 0 } n && n.EndsWith("innate_arcs", StringComparison.OrdinalIgnoreCase) && Applies(n);

    static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>The alpha factor across a ribbon: <paramref name="across"/> is 0 on its axis and 1 at its edge (the shader's <c>abs(UV.y * 2 - 1)</c>).</summary>
    public static float Lateral(float across, bool cannon = false) { float k = 1f - Smooth(cannon ? CannonCoreHalfWidth : CoreHalfWidth, 1f, Math.Clamp(across, 0f, 1f)); return cannon ? k * k : k; }   // plasma-cannon-shots: squared for the cannon's effects

    /// <summary>The alpha factor of a camera-facing glow card at <paramref name="r"/> (0 centre .. 1 the middle of an edge): full to 0.55, then 0 at the rim, so a large square card has no straight edge.</summary>
    public static float Radial(float r, bool cannon = false) => cannon ? MathF.Pow(1f - Smooth(0f, 1f, Math.Clamp(r, 0f, 1.5f)), 2f) : 1f - Smooth(0.55f, 1f, Math.Clamp(r, 0f, 1.5f));   // the cannon's cards fall from the centre, squared (plasma-cannon-shots)

    /// <summary>The alpha factor along a ribbon: 1 up to <see cref="TailFrom"/>, then down to 0 at the tail (<paramref name="along"/> 0 head .. 1 tail).</summary>
    public static float Tail(float along, float metres = 0f) => 1f - Smooth(metres > 0f ? MathF.Max(TailFrom, 1f - TailMetres / metres) : TailFrom, 1f, Math.Clamp(along, 0f, 1f));

    /// <summary>The alpha factor at a ribbon's head: 0 at the head, 1 from <see cref="HeadTo"/> on.</summary>
    public static float Head(float along, float metres = 0f) => Smooth(0f, metres > 0f ? MathF.Min(HeadTo, HeadMetres / metres) : HeadTo, Math.Clamp(along, 0f, 1f));

    /// <summary>A ribbon segment's factor (head and tail fades together).</summary>
    public static float Ribbon(float along, float metres = 0f) => Head(along, metres) * Tail(along, metres);

    /// <summary>The alpha factor of a ribbon seen nearly end-on: <paramref name="cos"/> is |axis . view direction|; a ribbon pointing at the camera is foreshortened to a thin straight line, so it fades out from 0.75 to 0.97.</summary>
    public static float EndOn(float cos) => 1f - Smooth(0.75f, 0.97f, Math.Clamp(cos, 0f, 1f));

    /// <summary>The shader's lateral falloff, as the source of the GLSL line (kept next to <see cref="Lateral"/> so the two stay the same curve).</summary>
    public const string ShaderLine = "a *= 1.0 - smoothstep(0.2, 1.0, abs(UV.y * 2.0 - 1.0));";
    /// <summary>The shader's lateral falloff for the plasma cannon's effects (<see cref="Lateral"/> with cannon).</summary>
    public const string CannonShaderLine = "a *= pow(1.0 - smoothstep(0.0, 1.0, abs(UV.y * 2.0 - 1.0)), 2.0);";

    /// <summary>plasma-cannon-shots (owner 2026-11-17: "make the main projectile ball at the front larger to match the reference image"): the loyalist or Chaos cannon's charged trail effect (<c>plasma_cannon_secondary_trail</c>); only its ball rings are enlarged.</summary>
    public const float ChargedBallScale = 1.25f;   // owner 2026-11-17: "make it only 1.25x bigger"
    /// <summary>The share of violet mixed into the charged trail's streamers (ours / estimate; owner: "only the trail has traces of violet").</summary>
    public const float VioletShare = 0.55f;
    static readonly (float R, float G, float B) VioletRgb = (0.66f, 0.45f, 1.0f);

    /// <summary>Whether a ring of the charged cannon trail is the front ball (its <c>ball</c> and <c>ball core</c> rings, not the glow, the distortion or the sparks).</summary>
    public static bool ChargedBall(string? effect, string? ring) =>
        effect is { } e && e.EndsWith("plasma_cannon_secondary_trail", StringComparison.OrdinalIgnoreCase) && ring is "ball" or "ball core";

    /// <summary>Whether a streamer belongs to the loyalist cannon's charged trail (the Chaos one is purple already).</summary>
    public static bool ChargedTrail(string? effect) => string.Equals(effect, "plasma_cannon_secondary_trail", StringComparison.OrdinalIgnoreCase);

    /// <summary>A colour with <see cref="VioletShare"/> of violet mixed in (the alpha is kept).</summary>
    public static (float R, float G, float B, float A) Violet(float r, float g, float b, float a) =>
        (r + (VioletRgb.R * MathF.Max(r, MathF.Max(g, b)) - r) * VioletShare, g + (VioletRgb.G * MathF.Max(r, MathF.Max(g, b)) - g) * VioletShare, b + (VioletRgb.B * MathF.Max(r, MathF.Max(g, b)) - b) * VioletShare, a);

    /// <summary>plasma-cannon-shots: the plasma cannon's own effects (loyalist and Chaos): its trails, muzzle and chamber effects by name, and the impacts its shots play (the charged shot's <c>plasma_gun_mp_overcharged_dmg</c>, the basic shot's <c>plasma_gun_charged_dmg</c>, with their Chaos <c>csm_</c> twins). The plasma gun's and pistol's own effects keep the plasma-soft-visuals curves.</summary>
    public static bool CannonLook(string? effectName) => effectName is { Length: > 0 } n && (n.Contains("plasma_cannon", StringComparison.OrdinalIgnoreCase) || n.EndsWith("plasma_gun_mp_overcharged_dmg", StringComparison.OrdinalIgnoreCase) || n.EndsWith("plasma_gun_charged_dmg", StringComparison.OrdinalIgnoreCase));
}
