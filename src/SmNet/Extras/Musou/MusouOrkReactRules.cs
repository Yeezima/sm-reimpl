using System;
using System.Numerics;

namespace SmNet.Extras.Musou;

/// <summary>
/// Musou Mode, Ork hit reactions from the rig's own clips (owner 2026-11-17: "Use reaction clips", answering the open question whether the procedural flinch of 2026-11-13 stays or the Boy rig's
/// reaction clips take its place). Client presentation only: no simulation, wire, protocol or hash change; nothing here runs outside Musou Mode. The Boy rig's 14 additive flinches
/// (<c>BOY_1HM_REC_Flinch_*</c> for the melee Boyz, <c>BOY_2HR_REC_Flinch_*</c> for the gun Boyz: High, Mid and Low, left and right, and a headshot) are laid over the Ork's own pose on the
/// upper body only (spine, neck, head, arms): the root, the pelvis and the legs are the clip's of the moment, so the Ork never turns and its feet never move (the 16 full-body
/// <c>REC_Lt_*</c> / <c>REC_Hvy_*</c> reactions were measured on 2026-11-17: their root turns up to 90 degrees between first and last frame and their ankles travel 0.4 to 1.2 m, which is what read as
/// a 180-degree turn on 2026-11-06). The clip is chosen by the hit's direction, the weight by the flinch's size, so the owner's earlier rules (strength by weapon, magnitude 2x with the 2.5 cap,
/// per-Ork variety, no growth) are the same machinery as before (<see cref="MusouOrkFlinchRules"/>, <see cref="MusouOrkVariety"/>). Every number is ours (kind: ours; confidence: low until a playtest).
/// </summary>
public static class MusouOrkReactRules
{
    /// <summary>The most an additive flinch is laid at (times its authored size): at 4 the arms flailed and the spine folded at the 2.5 cap (seen 2026-11-17), so the weakest clips fall short of the procedural head travel instead (ours).</summary>
    public const float WeightMax = 2.2f;

    /// <summary>How near the best alignment (absolute cosine) another clip may be and still take part in the id's pick: the Orks of a wave hit from one side do not all take the same clip (ours).</summary>
    public const float PickSpread = 0.12f;

    /// <summary>
    /// The clip whose own lean fits the flinch's direction best. <paramref name="direction"/> is the unit direction away from the hit in the Ork's own frame (+Y the way it faces);
    /// <paramref name="clipDirections"/> are the unit horizontal directions each clip moves the head along at its peak (zero: the clip moves it not at all, never picked). A clip is also taken
    /// when it leans the opposite way, laid with a negative weight (<paramref name="sign"/> -1): a hit from behind is then answered by leaning forward. Among the clips within
    /// <see cref="PickSpread"/> of the best fit the Ork's own id picks (the same Ork always takes the same one). Returns -1 when no clip moves.
    /// </summary>
    public static int Pick(Vector2 direction, ReadOnlySpan<Vector2> clipDirections, int id, out float sign)
    {
        sign = 0f;
        if (direction.LengthSquared() < 1e-8f) return -1;
        var d = Vector2.Normalize(direction);
        int best = -1;
        float bestFit = -1f;
        for (int i = 0; i < clipDirections.Length; i++)
        {
            if (clipDirections[i].LengthSquared() < 1e-8f) continue;
            float fit = MathF.Abs(Vector2.Dot(d, clipDirections[i]));
            if (fit > bestFit) { bestFit = fit; best = i; }
        }
        if (best < 0) return -1;
        int near = 0;
        for (int i = 0; i < clipDirections.Length; i++)
            if (clipDirections[i].LengthSquared() >= 1e-8f && MathF.Abs(Vector2.Dot(d, clipDirections[i])) >= bestFit - PickSpread) near++;
        int nth = Math.Min(near - 1, (int)(MusouOrkVariety.Roll(id, 41) * near));
        int pick = best;
        for (int i = 0, seen = 0; i < clipDirections.Length; i++)
        {
            if (clipDirections[i].LengthSquared() < 1e-8f || MathF.Abs(Vector2.Dot(d, clipDirections[i])) < bestFit - PickSpread) continue;
            if (seen++ == nth) { pick = i; break; }
        }
        sign = Vector2.Dot(d, clipDirections[pick]) >= 0f ? 1f : -1f;
        return pick;
    }

    /// <summary>
    /// The weight an additive flinch is laid at so the head travels what the procedural flinch's head travels for the same <paramref name="size"/> (in reference sizes):
    /// <paramref name="referenceMetres"/> the procedural head travel at size 1, <paramref name="clipMetres"/> the clip's head travel at weight 1; signed by <paramref name="sign"/>, never past <see cref="WeightMax"/>.
    /// </summary>
    public static float Weight(float size, float referenceMetres, float clipMetres, float sign)
    {
        if (!(size > 0f) || !(referenceMetres > 0f) || !(clipMetres > 1e-6f)) return 0f;
        return sign * Math.Min(WeightMax, size * referenceMetres / clipMetres);
    }

    /// <summary>The names of the Boy rig's seven additive flinches of a hand set: <c>1HM</c> (the choppa Boyz) or <c>2HR</c> (the gun Boyz).</summary>
    public static string[] ClipNames(bool ranged)
    {
        string hand = ranged ? "2HR" : "1HM";
        return [$"BOY_{hand}_REC_Flinch_High_L_01", $"BOY_{hand}_REC_Flinch_High_R_01", $"BOY_{hand}_REC_Flinch_Mid_L_01", $"BOY_{hand}_REC_Flinch_Mid_R_01",
                $"BOY_{hand}_REC_Flinch_Low_L_01", $"BOY_{hand}_REC_Flinch_Low_R_01", $"BOY_{hand}_REC_Flinch_Headshot"];
    }
}
