using System;

namespace SmClient.Customizer.Model;

/// <summary>
/// camera/melee-fov-widen (owner 2026-11-15; a deliberate change from the original in Classic PvP): in melee mode the own player's configured horizontal field of
/// view widens by <see cref="Widen"/> degrees (capped at <see cref="FovMath.Maximum"/>: a player at 120 sees no change, one at 115 goes to 120); both ways blend over
/// <see cref="BlendSeconds"/> (smoothstep). camera/melee-orbit (owner 2026-11-25): when it is on is the shared <see cref="MeleeMode"/> (a melee-weapon strike until a shot,
/// aiming, a reload, death or respawn; the Dreadnought never), the same state that frees the camera's orbit. Camera only: nothing on the wire. Engine-free.
/// </summary>
public sealed class MeleeFov
{
    /// <summary>Owner 2026-11-15: horizontal degrees added over the configured field of view.</summary>
    public const float Widen = 10f;
    /// <summary>Owner 2026-11-15 ("Smooth 0.3 s"): seconds for the blend either way.</summary>
    public const float BlendSeconds = 0.3f;

    /// <summary>The widening is on (the blend is moving toward, or sits at, the wide view): the shared <see cref="MeleeMode.Active"/> of the last <see cref="Advance"/>.</summary>
    public bool Wide { get; private set; }
    /// <summary>Linear 0 to 1 progress of the blend; <see cref="Blend"/> is its smoothstep.</summary>
    public float Progress { get; private set; }
    public float Blend => Progress * Progress * (3f - 2f * Progress);

    /// <summary>Advances the blend one frame toward the wide view while <paramref name="wide"/> (the melee mode is on), else back.</summary>
    public void Advance(bool wide, float delta)
    {
        Wide = wide;
        float step = BlendSeconds > 0f && delta > 0f ? delta / BlendSeconds : (delta > 0f ? 1f : 0f);
        Progress = Math.Clamp(Progress + (Wide ? step : -step), 0f, 1f);
    }

    /// <summary>The widest horizontal field of view for this configured one: +<see cref="Widen"/> over it, never above <see cref="FovMath.Maximum"/> (a configured 120 stays 120; 0, unset, is the data's look).</summary>
    public static float WideTarget(float configuredHorizontal, float aspect) =>
        MathF.Min(FovMath.Maximum, FovMath.Clamp(configuredHorizontal, aspect) + Widen);

    /// <summary>The horizontal field of view to show now: the configured one (<see cref="FovMath.Clamp"/>) blended toward <see cref="WideTarget"/>.</summary>
    public float Horizontal(float configuredHorizontal, float aspect)
    {
        float basis = FovMath.Clamp(configuredHorizontal, aspect);
        return basis + (WideTarget(configuredHorizontal, aspect) - basis) * Blend;
    }

    /// <summary>The factor for <see cref="FovMath.Apply"/>: <see cref="FovMath.Scale"/> of <see cref="Horizontal"/>; equals the configured scale when not widened.</summary>
    public float Scale(float configuredHorizontal, float aspect) =>
        Progress <= 0f ? FovMath.Scale(configuredHorizontal, aspect) : FovMath.Scale(Horizontal(configuredHorizontal, aspect), aspect);

}
