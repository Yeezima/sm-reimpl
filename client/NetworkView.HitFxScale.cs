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

    bool OldFlashMagnitude => _oldFlashMagnitude ??= CmdFlag(HitFxScaleRules.OldFlashMagnitudeFlag);
    bool? _oldFlashMagnitude;
    bool OldSparkReach => _oldSparkReach ??= CmdFlag(HitFxScaleRules.OldSparkReachFlag);
    bool? _oldSparkReach;

    /// <summary>hit-sparks-flashes: the brightness of the model's yellow and red flashes (1.3 x 1.4).</summary>
    float FlashBrightness => HitFxScaleRules.FlashBrightness(OldHitFxIntensity, OldFlashMagnitude);

    /// <summary>hit-sparks-flashes: the reach cap (m) of a hit-on-player particle effect; 0 with <c>--old-hit-spark-reach</c>.</summary>
    float SparkReachCap => OldSparkReach ? 0f : HitFxScaleRules.SparkReach;

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
        if (OldHitFxRange || HitFxView(at) is not { } v) return (HitFxScaleRules.FlashSize(1f, OldFlashMagnitude), HitFxScaleRules.FlashSize(1f, OldFlashMagnitude));
        return (HitFxScaleRules.FlashSize(HitFxScaleRules.RangeScale(v.Distance, v.Fov, HitFxScaleRules.MaxSpot), OldFlashMagnitude), HitFxScaleRules.FlashSize(HitFxScaleRules.RangeScale(v.Distance, v.Fov, HitFxScaleRules.MaxGlow), OldFlashMagnitude));
    }
}
