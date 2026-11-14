namespace SmContent;

/// <summary>
/// sound/classic-autocannon (owner, 2026-11-14, twice: "autocannon sound is very washed out and quiet", then "autocannon in classic pvp makes the dreadnaught walking noise when shooting.. completely
/// remove that and ensure its only the autocannon shooting sound effect playing"; Classic PvP's autocannon of the extended profile only). The install's <c>play_ch_autocannon_fire</c> (both sides'
/// <c>*_mp_exotic_auto_cannon</c> play it) is a campaign weapon's event, and one of its four actions plays the switch 0x06921B84 ("the body"): its nine samples are the Chaos marine's FOOTSTEP samples
/// (the same wems <c>play_ch_marine_footstep</c> plays: <c>smtool audio-event</c> of both), pitched down and driven by the shooter's <c>speed</c> RTPC (-96 dB standing, 0 dB at 2.5 m/s, -28 dB from
/// 5 m/s), i.e. the footstep layer of a heavy walker. The first pass of this tuning mistook them for shot samples and pinned and boosted them (+22 dB), so every shot played a heavy footstep thud, which
/// the owner hears as a Dreadnought walking. Ours: those body voices are never played (<see cref="Applies"/>); the shot's own layers (the map bank's tail layer, the click) stay as the data has them.
/// Plain events of every other weapon, and Musou Mode (its autocannon plays the Stalker's files), are untouched; <c>--old-classic-mix</c> restores the old sound.
/// </summary>
public static class ClassicAutocannonSound
{
    public const string Event = "play_ch_autocannon_fire";

    /// <summary>The Wwise switch container that holds the footstep samples (the action's target) the shot must not play.</summary>
    public const uint BodyNode = 0x06921B84;

    /// <summary>The whole gate: the Classic mix is on (not <c>--old-classic-mix</c>, which is also the mode Musou Mode runs the mix in) and the voice is the footstep body, which is dropped.</summary>
    public static bool Applies(bool classicMix, string evt, SoundVoice voice) => classicMix && IsBody(evt, voice);

    /// <summary>
    /// The switches the shot is resolved with. The event's local-player case (switch group 0x0d500bea set to the player's own value) reaches two tail objects (0x00db28ec, 0x05567177) that no loaded bank holds,
    /// so with the footstep body gone an own shot would be a 0.05 s click. The own shot therefore resolves as every other player's does (the group unset: the <c>default</c> case, whose tail layer is in the map bank),
    /// which sounds at the player's own position. Any other event, and the old mix, keep the emitter's switches.
    /// </summary>
    public static SoundSwitches ShotSwitches(bool classicMix, string evt, SoundSwitches switches)
    {
        if (!classicMix || !string.Equals(evt, Event, StringComparison.Ordinal) || switches.Get(AudioCatalog.PlayerGroup) is null) return switches;
        var c = switches.Clone();
        c.Remove(AudioCatalog.PlayerGroup);
        return c;
    }

    public static bool IsBody(string evt, SoundVoice voice) => string.Equals(evt, Event, StringComparison.Ordinal) && voice.Path.Contains(BodyNode);
}
