using Godot;
using SmClient.ImpactRules;

namespace SmClient;

/// <summary>
/// onhit-range-intensity (owner, 2026-11-13; client presentation only): the on-player hit effects keep their size on screen at range and are 30 % stronger (<see cref="HitFxScaleRules"/>).
/// <c>--old-hit-fx-range</c> and <c>--old-hit-fx-intensity</c> restore either; <c>--log-hit-fx</c> adds the factors to its lines.
/// </summary>
public partial class NetworkView
{
    bool OldHitFxRange => _oldHitFxRange ??= CmdFlag(HitFxScaleRules.OldRangeFlag);
    bool? _oldHitFxRange;
    bool OldHitFxIntensity => _oldHitFxIntensity ??= CmdFlag(HitFxScaleRules.OldIntensityFlag);
    bool? _oldHitFxIntensity;

    /// <summary>The brightness factor of the hit glows this run (1.3, or 1 with <c>--old-hit-fx-intensity</c>).</summary>
    float HitFxBrightness => OldHitFxIntensity ? 1f : HitFxScaleRules.Intensity;

    /// <summary>The distance (m) and vertical field of view (degrees) of the camera that sees a point; null without a camera.</summary>
    (float Distance, float Fov)? HitFxView(Vector3 at)
    {
        var cam = GetViewport()?.GetCamera3D();
        return cam is null ? null : (cam.GlobalPosition.DistanceTo(at), cam.Fov);
    }

    /// <summary>The size factor of a particle effect played at <paramref name="at"/> on a hit player: range growth times intensity.</summary>
    float HitFxParticleScale(Vector3 at) =>
        HitFxView(at) is { } v ? HitFxScaleRules.ParticleScale(v.Distance, v.Fov, OldHitFxRange, OldHitFxIntensity) : (OldHitFxIntensity ? 1f : HitFxScaleRules.Intensity);

    /// <summary>The range growth of a model's hit spot and of its break glow card this frame (1, 1 with <c>--old-hit-fx-range</c>).</summary>
    (float Spot, float Glow) HitFxModelScale(Vector3 at)
    {
        if (OldHitFxRange || HitFxView(at) is not { } v) return (1f, 1f);
        return (HitFxScaleRules.RangeScale(v.Distance, v.Fov, HitFxScaleRules.MaxSpot), HitFxScaleRules.RangeScale(v.Distance, v.Fov, HitFxScaleRules.MaxGlow));
    }
}
