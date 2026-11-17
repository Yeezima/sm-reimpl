using System;
using Godot;
using SmContent;

namespace SmClient;

// Grenade effects pass (owner 2026-11-17: "Yes, match the video"): the orange in-flight marker of a thrown grenade. The icon (the small canister, Decorator.gfx sprite 29, a 30-frame
// glow pulse) and the chevron (sprite 23, a 15-frame outward slide and fade) are the install's own art; the ring placement, the arc and the timing are measured from the four reference
// clips (ours / estimate; spec "Grenade in-flight marker").
public sealed partial class Hud
{
    /// <summary>One in-flight grenade marker this frame: the grenade's direction on the screen (radians, 0 right, counter-clockwise up), seconds since the release and the fade (1 shown, 0 gone).</summary>
    public readonly record struct GrenadeMark(float Angle, float Age, float Fade);

    /// <summary>Sprite ids of the grenade canister and the chevron in <c>Decorator.gfx</c> (the install's; looked up by their shapes once).</summary>
    const int GrenadeIconSprite = 29, ChevronSprite = 23;

    /// <summary>
    /// The markers: for each, the canister at <see cref="GrenadeRules.RingRadius"/> from the screen centre turned to the direction (cap outward), a thin orange arc outside it and the sliding
    /// chevron beyond. The movie art draws only with the HUD movies loaded (else the markers are not drawn at all).
    /// </summary>
    void DrawGrenadeMarkers(Vector2 vs)
    {
        if (Data.GrenadeMarks.Count == 0 || OldHud || _movieArt?.Movies?.Decorator is not { } deco) return;
        float s = StageToVirtual;
        var c = vs / 2f;
        foreach (var m in Data.GrenadeMarks)
        {
            var u = new Vector2(MathF.Cos(m.Angle), -MathF.Sin(m.Angle));   // screen direction (y down)
            float godotAngle = MathF.Atan2(u.Y, u.X);
            float fade = Math.Clamp(m.Fade, 0f, 1f);
            // The canister: upright when the direction is up, its cap turned outward.
            int iconFrame = (int)(m.Age * DecoratorRules.FrameRate) % 30;
            var iconPos = c + u * GrenadeRules.RingRadius * s;
            float iconTurn = godotAngle + MathF.PI / 2f;
            var iconRoot = new Transform2D(iconTurn, new Vector2(1f, 1f) * (s * GrenadeRules.IconScale), 0f, iconPos) * new Transform2D(0f, new Vector2(-8f, -16f));   // the art's origin is its top-left corner (16 x 32)
            _walkTint = new Color(1f, 0.55f, 0.12f);
            DrawWalk(deco, GrenadeIconSprite, iconFrame, iconRoot, null, _ => false, _ => null, null, fade);
            // The arc: concentric with the screen centre, fading out toward its tips.
            const int segments = 14;
            float half = GrenadeRules.ArcHalfAngle, r = (GrenadeRules.RingRadius + GrenadeRules.ArcOffset) * s;
            for (int i = 0; i < segments; i++)
            {
                float t0 = i / (float)segments, t1 = (i + 1) / (float)segments;
                float a0 = godotAngle - half + 2f * half * t0, a1 = godotAngle - half + 2f * half * t1;
                float mid = 1f - MathF.Abs(0.5f * (t0 + t1) - 0.5f) * 2f;   // 1 in the middle, 0 at the tips
                var col = new Color(1f, 0.42f, 0.04f, Math.Clamp(mid * 1.3f, 0f, 1f) * fade * GrenadeRules.ArcFadeShare(m.Age));
                DrawLine(c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r, c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r, col, GrenadeRules.ArcWidth * s, true);
            }
            // The chevron: its own clip slides it outward along its x axis, so it turns to point away from the centre.
            int chevFrame = (int)(m.Age * DecoratorRules.FrameRate) % 15;
            var chevPos = c + u * (GrenadeRules.RingRadius + GrenadeRules.CaretOffset) * s;
            var chevRoot = new Transform2D(godotAngle + MathF.PI, new Vector2(1f, 1f) * (s * GrenadeRules.CaretScale), 0f, chevPos);
            DrawWalk(deco, ChevronSprite, chevFrame, chevRoot, null, _ => false, _ => null, null, fade);
            _walkTint = Colors.White;
        }
    }
}
