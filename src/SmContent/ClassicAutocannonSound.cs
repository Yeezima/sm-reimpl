namespace SmContent;

/// <summary>
/// sound/classic-autocannon-turret (owner, 2026-11-18: "Yes, use 05 for both", after hearing a preview of the install's <c>play_autocannon_turret_fire</c>; Classic PvP's autocannon, both factions). In the Classic mix a post of
/// <c>play_ch_autocannon_fire</c> (what both sides' <c>*_mp_exotic_auto_cannon</c> post) plays <c>play_autocannon_turret_fire</c> instead, at the shooter's position, the own shot included. The turret event is the
/// CAMPAIGN's turret (a singleplayer content dependency the owner accepted; docs/discovery.md "Classic autocannon"). It lives in <c>In_Game_Default</c> and its samples in <c>Ambiences</c>, both base packages
/// (<see cref="AudioCatalog.BasePackages"/>), so no extra bank is loaded. Switches as the preview used: the local-player case of group 0x0d500bea is cleared (<see cref="ShotSwitches"/>), so the own shot resolves as
/// every other player's does (the <c>default</c> case, whose layers are all in the loaded banks). History: sound/classic-autocannon (2026-11-14) dropped the footstep "body" voices of <c>play_ch_autocannon_fire</c> (heard as a
/// Dreadnought walking); the turret event has no such voices, so that drop is gone. Every other event, <c>--old-classic-mix</c> and Musou Mode (its autocannon plays the Stalker's files) are untouched.
/// </summary>
public static class ClassicAutocannonSound
{
    /// <summary>The event the weapon files post (both factions).</summary>
    public const string Event = "play_ch_autocannon_fire";

    /// <summary>The campaign turret's event the Classic mix plays in its place.</summary>
    public const string TurretEvent = "play_autocannon_turret_fire";

    /// <summary>The event to resolve for a post: the turret's in the Classic mix for <see cref="Event"/>, the posted event otherwise.</summary>
    public static string Resolve(bool classicMix, string evt) => classicMix && string.Equals(evt, Event, StringComparison.Ordinal) ? TurretEvent : evt;

    /// <summary>
    /// The switches the shot is resolved with: for the substituted event the local-player case (group 0x0d500bea set to the player's own value) is cleared, so an own shot resolves as every other player's does
    /// (the <c>default</c> case, which sounds at the player's own position). Any other event, and the old mix, keep the emitter's switches.
    /// </summary>
    public static SoundSwitches ShotSwitches(bool classicMix, string evt, SoundSwitches switches)
    {
        if (!classicMix || !string.Equals(evt, Event, StringComparison.Ordinal) || switches.Get(AudioCatalog.PlayerGroup) is null) return switches;
        var c = switches.Clone();
        c.Remove(AudioCatalog.PlayerGroup);
        return c;
    }
}
