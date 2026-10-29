namespace SmContent;

/// <summary>
/// The owner's footstep level (sound/footsteps-2, owner 2026-10-29: "make them sound like they do in the original game exactly, with slightly lower
/// volume"; recorded as a **deliberate change from the original**, docs/changes-from-original.md): every footstep a character's clip posts plays
/// <see cref="OwnerGainDb"/> under the data's level. Landings (also <c>is_footstep_sound</c> in the data) are not footsteps here and keep the data's level.
/// </summary>
public static class FootstepLevel
{
    /// <summary>The owner's "slightly lower volume": -3 dB (half the power; ours).</summary>
    public const float OwnerGainDb = -3f;

    /// <summary>
    /// Whether a binding is a footstep: an <c>is_footstep_sound</c> command fired by a clip's foot tag (<c>evt_left_foot</c>, <c>evt_right_foot</c>, their
    /// <c>_rush</c> sprint versions and <c>evt_bullrush_stop_*</c>), not a landing (<c>finish_falling_actions</c>, the jump pack's landing entries).
    /// </summary>
    public static bool IsStep(SoundBinding b) =>
        b.IsFootstep && b.Trigger.StartsWith("evt_", StringComparison.OrdinalIgnoreCase)
        && (b.Trigger.Contains("foot", StringComparison.OrdinalIgnoreCase) || b.Trigger.StartsWith("evt_bullrush_stop", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The key under which the client spaces a blend layer's tags (<c>NetworkView.SoundBlendTags</c>): a foot tag and its sprint version are one footfall
    /// (<c>EVT_Left_Foot</c> and <c>EVT_Left_Foot_Rush</c> → <c>evt_left_foot</c>), so a run-to-sprint change cannot post the same foot twice; other tags are their own key
    /// (lower case).
    /// </summary>
    public static string SpacingKey(string tag)
    {
        string t = tag.ToLowerInvariant();
        return t.StartsWith("evt_", StringComparison.Ordinal) && t.EndsWith("_foot_rush", StringComparison.Ordinal) ? t[..^"_rush".Length] : t;
    }

    /// <summary>
    /// The shortest time (s) between two posts of the same foot (ours): a running marine plants the same foot about every 0.95 s, a sprinting one every 0.6 s or so,
    /// so two firings of one foot (with or without <c>_Rush</c>) closer than this are one footfall fired by two clips.
    /// </summary>
    public const double SameFootMinSeconds = 0.25;

    /// <summary>
    /// Whether a foot tag (<c>evt_left_foot</c>, <c>evt_right_foot</c> and the <c>_rush</c> versions) may post now: not when the same foot posted less than
    /// <see cref="SameFootMinSeconds"/> ago (sound/footsteps-2: at a run-to-sprint change the run blend's tag and the sprint clip's tag for one footfall fired
    /// 0.05 to 0.09 s apart). Records the post when it is admitted; other tags are always admitted and not recorded.
    /// </summary>
    public static bool AdmitFootfall(IDictionary<string, double> lastPost, string tag, double now)
    {
        string key = SpacingKey(tag);
        if (key is not ("evt_left_foot" or "evt_right_foot")) return true;
        if (lastPost.TryGetValue(key, out double last) && now - last >= 0 && now - last < SameFootMinSeconds) return false;
        lastPost[key] = now;
        return true;
    }

    /// <summary>The gain (dB) a binding's voices get on top of the data's mix: <see cref="OwnerGainDb"/> for a footstep, else 0.</summary>
    public static float GainDb(SoundBinding b) => IsStep(b) ? OwnerGainDb : 0f;
}
