namespace SmContent;

/// <summary>
/// sound/classic-autocannon (owner, 2026-11-14: "classic pvp mode only - autocannon sound is very washed out and quiet"; Classic PvP's autocannon of the extended profile only). The install's
/// <c>play_ch_autocannon_fire</c> (both sides' <c>*_mp_exotic_auto_cannon</c> play it) is a campaign weapon's event: its body (the switch 0x06921B84: ten small Chaos shot samples) is driven by the
/// shooter's <c>speed</c> RTPC whose volume curve is -96 dB standing, 0 dB at 2.5 m/s and -28 dB from 5 m/s (a footstep-style curve), so a heavy who stands still or runs while firing is silent in the
/// body and what is heard is the map bank's reverb tail layer: "washed out and quiet". Even at the curve's peak the body sits 17 dB under the heavy bolter's loudest voices (-37 dB against -19 dB
/// at the voice, own shot, <c>smtool audio-mix --loudness --local</c>) and is low-passed (LPF 20) and pitched down. Ours: the body voices read the speed RTPC as <see cref="PinnedSpeed"/> (its 0 dB point),
/// get <see cref="BodyGainDb"/> (the heavy bolter's level) and <see cref="BodyLpf"/> (the low-pass off); the tail and every other layer stay as the data has them. Plain events of every
/// other weapon, and Musou Mode (its autocannon plays the Stalker's files), are untouched; <c>--old-classic-mix</c> restores the old sound.
/// </summary>
public static class ClassicAutocannonSound
{
    public const string Event = "play_ch_autocannon_fire";

    /// <summary>The Wwise switch container that holds the shot's body samples (the action's target).</summary>
    public const uint BodyNode = 0x06921B84;

    /// <summary>The speed (m/s) the body reads: the curve's 0 dB point (ours).</summary>
    public const float PinnedSpeed = 2.5f;

    /// <summary>dB added to the body (ours, an estimate): calibrated on another player's shot, whose remote floor and curve are the heavy bolter's, to the heavy bolter's summed voice power at 5 m and 80 m (<c>smtool audio-mix --loudness --scape gunfire --speed 2.5</c>: the autocannon's two body voices -33/-36 dB at 5 m and -43/-46 dB at 80 m against the heavy bolter's 12 voices at -8.7 dB and -20.6 dB summed, so +22.9 near and +20.8 far); own shot about 2 dB under the heavy bolter's sum. Refine in a playtest.</summary>
    public const float BodyGainDb = 22f;

    /// <summary>Low-pass value added to the body (ours: -20 takes the data's LPF 20 off, so the shot is not dull). Refine in a playtest.</summary>
    public const float BodyLpf = -20f;

    /// <summary>The whole gate: the Classic mix is on (not <c>--old-classic-mix</c>, which is also the mode Musou Mode runs the mix in) and the voice is the autocannon's body.</summary>
    public static bool Applies(bool classicMix, string evt, SoundVoice voice) => classicMix && IsBody(evt, voice);

    public static bool IsBody(string evt, SoundVoice voice) => string.Equals(evt, Event, StringComparison.Ordinal) && voice.Path.Contains(BodyNode);

    /// <summary>The post offset the body gets (<see cref="BodyGainDb"/>, <see cref="BodyLpf"/>).</summary>
    public static VoiceOffset BodyOffset => new(BodyGainDb, 0f, BodyLpf);

    /// <summary>The emitter's parameters for a body voice: the speed pinned (a copy; null in, a new one out).</summary>
    public static Dictionary<uint, float> Pin(IReadOnlyDictionary<uint, float>? parameters)
    {
        var d = parameters is null ? new Dictionary<uint, float>() : new Dictionary<uint, float>(parameters);
        d[VoiceMixer.Speed] = PinnedSpeed;
        return d;
    }
}
