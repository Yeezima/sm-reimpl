namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// The autocannon's recoil (owner, 2026-11-11: "the arm currently moves completely away from the gun when firing. it should only slightly move straight back from the direction the gun fires in."):
/// the gun and both arms, one body, move a small distance straight back along the barrel's axis and return; the heavy bolter's additive recoil clip (<c>SM_RNG_TB_FireIdle_S_01</c>), which the
/// autocannon borrowed with the heavy bolter's animations, is not laid for it (it turns the hands 7 to 22 degrees on the arms while the gun rides the chest prop bone, so the right hand slid up to
/// 13 cm along the gun). Presentation only. <b>Provisional approximation</b>: the distance and the times are ours (spec/behavior.md "The autocannon's kick").
/// </summary>
public static class AutocannonKick
{
    /// <summary>How far back the kick goes at its peak (m; ours, provisional).</summary>
    public const float PeakMetres = 0.04f;
    /// <summary>Seconds from the shot to the peak, and from the peak back to rest (ours, provisional; the autocannon fires every 0.4 s).</summary>
    public const float RiseSeconds = 0.04f, ReturnSeconds = 0.26f;
    /// <summary>The whole kick (s).</summary>
    public const float TotalSeconds = RiseSeconds + ReturnSeconds;

    /// <summary>The distance back along the barrel (m) <paramref name="age"/> seconds after a shot: 0 before and after, <see cref="PeakMetres"/> at <see cref="RiseSeconds"/>, smooth at both ends.</summary>
    public static float Metres(float age)
    {
        if (!float.IsFinite(age) || age <= 0f || age >= TotalSeconds) return 0f;
        float k = age < RiseSeconds ? age / RiseSeconds : 1f - (age - RiseSeconds) / ReturnSeconds;
        return PeakMetres * k * k * (3f - 2f * k);
    }
}
