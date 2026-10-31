using System;

namespace SmNet;

/// <summary>
/// feel/weight-impact (the owner, 2026-11-01: "a lot of actions and animations feel very floaty. send a pass of weight/impact to pretty much all animations/actions that i haven't
/// specifically said feel very good"): the numbers of the weight pass, engine-free so they can be tested. Presentation only: nothing here reaches the simulation, the protocol or the
/// config hash. Every value is ours, an estimate for a human playtest (spec "The weight pass"; refine register). Not here on purpose: a melee hit-stop or a melee hit shake (the owner
/// removed both on 2026-10-29), anything of the roll, the sound of footsteps, the data's run clips.
/// </summary>
public static class WeightFeel
{
    /// <summary>The fall speed (m/s down) from which a landing below the hard clip's speed shows some of the crouch: about a 1.6 m drop at the simulation's 20 m/s².</summary>
    public const float SettleFromSpeed = 8f;
    /// <summary>The fall speed at which the settle is at its largest (the graph's hard-landing test).</summary>
    public const float SettleFullSpeed = 20f;
    /// <summary>The share of the landing crouch at <see cref="SettleFromSpeed"/> and just below the hard clip.</summary>
    public const float SettleMinWeight = 0.3f, SettleMaxWeight = 0.8f;

    static float FallShare(float peakDown) => Math.Clamp((peakDown - SettleFromSpeed) / (SettleFullSpeed - SettleFromSpeed), 0f, 1f);

    /// <summary>The share of the landing clip a fall below the hard speed shows: 0 under <see cref="SettleFromSpeed"/>, then rising with the speed.</summary>
    public static float SettleWeight(float peakDown) =>
        !float.IsFinite(peakDown) || peakDown < SettleFromSpeed ? 0f : SettleMinWeight + (SettleMaxWeight - SettleMinWeight) * FallShare(peakDown);

    /// <summary>The own camera's dip on a landing (degrees): none under <see cref="SettleFromSpeed"/>, 0.35 there, 1.2 from <see cref="SettleFullSpeed"/> up.</summary>
    public static float LandingDipDegrees(float peakDown) =>
        !float.IsFinite(peakDown) || peakDown < SettleFromSpeed ? 0f : 0.35f + 0.85f * FallShare(peakDown);

    /// <summary>The fall speed of a drop in metres under the simulation's gravity (a jump pack's landing reports the drop).</summary>
    public static float SpeedOfDrop(float metres, float gravity = 20f) => metres > 0f && float.IsFinite(metres) ? MathF.Sqrt(2f * gravity * metres) : 0f;

    /// <summary>The own camera's dip when the own ground pound lands (degrees).</summary>
    public const float PoundDipDegrees = 1.1f;

    /// <summary>The range of the existing blast shake (m) and the jolt's peak (degrees).</summary>
    public const float BlastRange = 25f, BlastPeakDegrees = 0.9f;

    /// <summary>The own camera's jolt from an explosion (degrees): largest at the blast, gone at <see cref="BlastRange"/>, falling with the square of the distance's share.</summary>
    public static float BlastJoltDegrees(float distance)
    {
        if (!float.IsFinite(distance) || distance >= BlastRange) return 0f;
        float k = 1f - Math.Max(0f, distance) / BlastRange;
        return BlastPeakDegrees * k * k;
    }

    /// <summary>A Devastator's or Havoc's footfall in the own camera (degrees): running, sprinting.</summary>
    public const float FootfallRunDegrees = 0.05f, FootfallSprintDegrees = 0.09f;

    /// <summary>The body's recoil clip's strength by the weapon (a multiple of the data's additive clip): the heavier the gun, the more of it.</summary>
    public static float RecoilScale(string weaponName)
    {
        string n = weaponName.ToLowerInvariant();
        if (n.Contains("plasma_cannon") || n.Contains("las_cannon") || n.Contains("lascannon")) return 1.5f;
        if (n.Contains("heavy_bolter") || n.Contains("melta") || n.Contains("grenade_launcher")) return 1.35f;
        if (n.Contains("pistol")) return 1f;
        return 1.15f;
    }

    /// <summary>The upward kick of the own camera per shot (degrees) for the heavy weapons, 0 for the others; a charged plasma gun shot kicks too. Half of it while aiming (the view is narrower).</summary>
    public static float CameraRecoilDegrees(string weaponName, int chargeStage, bool aiming)
    {
        string n = weaponName.ToLowerInvariant();
        float k = n.Contains("plasma_cannon") || n.Contains("las_cannon") || n.Contains("lascannon") ? 0.7f
            : n.Contains("melta") || n.Contains("grenade_launcher") ? 0.45f
            : n.Contains("heavy_bolter") ? 0.16f
            : n.Contains("plasma_gun") && chargeStage > 0 ? 0.35f : 0f;
        return aiming ? k * 0.5f : k;
    }

    /// <summary>The flinch's strength over the data's additive clip: a shot, the own melee hit's victim. The owner's 0.05 s in and 0.333 s out are unchanged.</summary>
    public const float FlinchRangedPeak = 1.3f, FlinchMeleePeak = 1.6f;

    /// <summary>The shortest entry fade of a strike or a hit reaction (s): the linear floor the smooth curve's 0.1875 s grew from, nearer the graphs' 0.1 to 0.167 s.</summary>
    public const float StrikeEntryFloor = 0.125f;
}

/// <summary>
/// One camera axis's impulse (degrees): each kick adds to a target, which decays at once; the shown value follows it with a short rise, so a kick is a quick move and a slower return, never
/// a jump in one frame. The total is capped, so a long burst cannot walk the view. Pure, stepped by the frame's time.
/// </summary>
public sealed class CameraImpulse
{
    /// <summary>The time constants of the return and the rise (s), and the largest total (degrees).</summary>
    public const float DecaySeconds = 0.09f, RiseSeconds = 0.018f, MaxDegrees = 2f;

    float _target;

    /// <summary>The value shown now (degrees).</summary>
    public float Now { get; private set; }

    /// <summary>Adds a kick (degrees; the sign picks the way), the total held within <see cref="MaxDegrees"/>.</summary>
    public void Add(float degrees)
    {
        if (!float.IsFinite(degrees)) return;
        _target = Math.Clamp(_target + degrees, -MaxDegrees, MaxDegrees);
    }

    /// <summary>Advances by <paramref name="delta"/> seconds and returns <see cref="Now"/>; below 0.001 degrees it rests at exactly 0.</summary>
    public float Step(float delta)
    {
        if (!(delta > 0f) || !float.IsFinite(delta)) return Now;
        _target *= MathF.Exp(-delta / DecaySeconds);
        Now += (_target - Now) * (1f - MathF.Exp(-delta / RiseSeconds));
        if (MathF.Abs(_target) < 1e-3f && MathF.Abs(Now) < 1e-3f) { _target = 0f; Now = 0f; }
        return Now;
    }

    /// <summary>Drops everything.</summary>
    public void Clear() { _target = 0f; Now = 0f; }
}
