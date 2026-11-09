namespace SmContent;

/// <summary>
/// sound/stalker-hit-confirm (owner, 2026-11-09: "The stalker bolter in both classic PvP and Musou isn't playing the right sound when shooting an enemy target. If a shot lands the shooter
/// hears a sound. Find that sound in the original game files and use it for both the stalker bolter and daemon eye bolter"). The shooter's hit confirmation of the Stalker Bolter
/// (<c>sm_mp_ranged_sniper_rifle</c>) and the Daemon Eye Bolter (<c>csm_mp_ranged_sniper_rifle</c>).
/// <para>
/// The data (wargear.oppc, both files): <c>ability_ranged_primary/delivery/player_hit_something_action/commands/post_sound_event</c> <c>start_event_name = "play_stalker_smack"</c>, trigger
/// point self, delay 0 (supported by evidence). In the In_Game_Default bank the event's smack layer (action 1) and material layer (action 2: the victim's material) are non-positioned (2D, no
/// distance curve) and its kill layer (action 3) reads the death state; the smack layer sits at -5 dB, 3 dB above the bolter family's <c>play_bolter_smack</c>, over the same 12 samples.
/// </para>
/// <para>
/// What went wrong: Classic posted the event, but under the hit confirm's distance falloff (<see cref="ImpactSounds.HitConfirmDistanceDb"/>: -18 dB from 40 m, the Stalker's usual range) and
/// the mix pass's 250 Hz high-pass (the smack's energy is two thirds under 150 Hz), so about -48 dB of a thin tick; Musou never posted it (Ork hits make no hit report) and played the bolter
/// family's <c>play_bolter_smack</c> at the Ork instead, pitched 600 cents down. Now both rifles play <see cref="Event"/> on the shooter at the data's level (<see cref="Offset"/>), in Classic
/// and Musou; every other weapon keeps its sound. Interpretation (the level and 2D placement are the data's; that the original applied no further distance falloff is a reading of it).
/// <c>--old-stalker-hit-confirm</c> restores the former behaviour.
/// </para>
/// </summary>
public static class StalkerHitConfirm
{
    /// <summary>The data's confirm event of both rifles.</summary>
    public const string Event = "play_stalker_smack";

    /// <summary>Whether a wargear is the Stalker Bolter or the Daemon Eye Bolter (the sniper rifle of either side).</summary>
    public static bool Is(string? wargear) => wargear is not null && wargear.EndsWith("mp_ranged_sniper_rifle", StringComparison.OrdinalIgnoreCase);

    /// <summary>The shooter's confirm event for a weapon: <see cref="Event"/> for the two rifles, otherwise the event the weapon's data names (<paramref name="dataEvent"/>, unchanged).</summary>
    public static string? EventFor(string? wargear, string? dataEvent) => Is(wargear) ? Event : dataEvent;

    /// <summary>The offset of the rifles' confirm: none (the data's level; no distance falloff, no mix-pass gain or high-pass).</summary>
    public static VoiceOffset Offset => VoiceOffset.None;

    /// <summary>Musou: whether a weapon's own Ork hits play the confirm on the shooter in place of the bolter family's wet smack (the explosion stays).</summary>
    public static bool ReplacesMusouSmack(string? wargear) => Is(wargear);
}
