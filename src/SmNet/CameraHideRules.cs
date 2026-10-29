namespace SmNet;

/// <summary>
/// feel/camera-hitstop (the owner's playtest, 2026-10-29), client presentation only: when the follow camera is too close to the own head the whole own character
/// (model, weapons, worn packs and the pack's effects) is hidden. The distances are ours (the original's camera collision is not decoded; the camera rig data has no
/// collision offset): the exploration camera sits 3.5 m from the head and the aim camera 2.0 m (pistol aim about 1.7 m at 45 degrees down), the hide limit is
/// <see cref="HideDistance"/> and the character comes back at <see cref="ShowFactor"/> times that (hysteresis, so it does not flicker at the limit).
/// </summary>
public static class CameraHideRules
{
    /// <summary>Ours: metres from the head below which the own character is hidden (before 2026-10-29: 1.0 m, the model only).</summary>
    public const float HideDistance = 1.4f;
    /// <summary>Ours: the distance the character is shown again at, as a multiple of the hide distance.</summary>
    public const float ShowFactor = 1.15f;

    /// <summary>Whether the own character is hidden with the camera <paramref name="closest"/> metres from the head, given whether it is hidden now (a NaN distance keeps the state).</summary>
    public static bool Hidden(bool hiddenNow, float closest, float limit = HideDistance)
    {
        if (float.IsNaN(closest)) return hiddenNow;
        return hiddenNow ? closest < limit * ShowFactor : closest < limit;
    }
}
