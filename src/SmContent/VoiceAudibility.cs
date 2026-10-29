namespace SmContent;

/// <summary>
/// Whether a starting or playing voice is audible (engine-free so it is tested without Godot; <c>SoundSystem.Audible</c> feeds it). Positioned voices beyond their
/// attenuation curve's end, or whose level (volume plus attenuation, master bus not counted) is under <see cref="VolumeThreshold"/>, are not audible: this is the
/// behaviour before the footsteps fix and applies to every positioned voice. Non-positioned voices are only culled under the threshold, and only when the post asked for
/// it (<c>thresholded2D</c>: the footstep posts in <c>--sound-limits footsteps</c>, every post in <c>all</c>, none in <c>none</c>, which keeps the old behaviour exactly)
/// and the voice's own sound is set to be killed or made virtual below the threshold (the below-threshold byte of the sound; a node set to keep playing is never culled).
/// A muted voice carries the mute's -96 dB in the level and is culled like any other.
/// </summary>
public static class VoiceAudibility
{
    /// <summary>Below this level (dB, not counting the master bus) a voice is inaudible. Ours (provisional): the project's own threshold is not in the data read.</summary>
    public const float VolumeThreshold = -60f;

    /// <param name="is3D">Positioned (a 3D sound) or not.</param>
    /// <param name="hasAttenuation">The voice has an attenuation (a positioned voice without one is always audible, as before).</param>
    /// <param name="attenuationMaxDistance">The attenuation's end in metres (0 or less: none).</param>
    /// <param name="distance">The listener's distance, or null when there is no listener (then a positioned voice counts as audible).</param>
    /// <param name="levelDb">The voice's live volume including the attenuation, the live modifiers and the master bus.</param>
    /// <param name="masterDb">The master bus volume inside <paramref name="levelDb"/>.</param>
    /// <param name="belowThreshold">The sound node's below-threshold byte (0 keep playing, 1 kill, 2 virtual).</param>
    /// <param name="hasChain">The voice carries its node chain (a voice built by hand does not: it counts as byte 1).</param>
    /// <param name="thresholded2D">Whether this voice's post asked for the threshold on non-positioned voices.</param>
    public static bool IsAudible(bool is3D, bool hasAttenuation, float attenuationMaxDistance, float? distance, float levelDb, float masterDb,
        byte belowThreshold, bool hasChain, bool thresholded2D)
    {
        bool underThreshold = levelDb - masterDb < VolumeThreshold;
        if (!is3D || !hasAttenuation)
        {
            bool thresholded = !is3D && thresholded2D && (!hasChain || belowThreshold != 0);
            return !(thresholded && underThreshold);
        }
        if (distance is not float d) return true;
        return (attenuationMaxDistance <= 0f || d <= attenuationMaxDistance) && !underThreshold;
    }
}
