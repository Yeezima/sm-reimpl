using SmSim;

namespace SmClient.ImpactRules;

/// <summary>
/// classic-no-player-hit-fx (owner, 2026-11-11: "simply remove instances of weapon on hit visual effects like the stalker bolter explosion cloud on the player target you shot and hit -
/// classic pvp only"): in Classic PvP (Annihilation and Seize Ground, any match that is not Musou Mode) a ranged hit on a player draws none of the on-hit particle effects on or at him:
/// no armour-hit effects at the hit point or on his body markers, no spark fans, no health-hit burst or blood splash, no on-hit melta burn, no hit container (decals, exit wounds). SUPERSEDED in part by the owner's answers of 2026-11-11 (claude/hit-fx-answers): a break a shot caused plays its
/// armour break effect again (<see cref="DrawsBreakFx"/>), the bare-armour model flash is back (<see cref="BareHitFlash"/>) and the melta burns on a kill, not on a hit (<c>NetworkView.MeltaKillBurn</c>). Kept: the victim model's own flash and glow (a shader on the model, not an effect), the hit sounds, the HUD's hit marker and screen feedback,
/// the tracer or projectile in flight, explosions and everything a shot that misses players draws on the map. Musou Mode keeps all of it. A deliberate change from the original's look
/// (docs/changes-from-original.md); <c>--player-hit-fx</c> brings the effects back in Classic.
/// </summary>
public static class PlayerHitFxRules
{
    /// <summary>The client flag that restores the on-hit effects on players in Classic PvP (as before 2026-11-11).</summary>
    public const string RestoreFlag = "player-hit-fx";
    /// <summary>onhit-range-intensity (owner 2026-11-13: "bring back the spark and burst particles on hit"): the 2026-11-11 removal is reversed as the default; this client flag brings the no-particle look back in Classic PvP (the rules below read the effects as drawn unless it is set).</summary>
    public const string NoParticlesFlag = "no-player-hit-fx";

    /// <summary>Classic PvP: any match that is not Musou Mode (faithful or extended content alike; the Ork Boy is a Classic player too).</summary>
    public static bool Classic(ContentExtras extras) => !extras.HasFlag(ContentExtras.Musou);

    /// <summary>A shot's end struck a player (the body or the head) rather than the map or nothing.</summary>
    public static bool OnPlayer(ShotHit hit) => hit is ShotHit.Body or ShotHit.Head;

    /// <summary>
    /// Whether a ranged hit draws its on-hit particle effects: always on the map; on a player only in Musou Mode or with <paramref name="restore"/> (<see cref="RestoreFlag"/>).
    /// </summary>
    public static bool DrawsHitFx(ShotHit hit, ContentExtras extras, bool restore) => !OnPlayer(hit) || !Classic(extras) || restore;

    /// <summary>
    /// Whether the armour's break effect (the player blueprint's <c>armourshield_broken</c>, <c>sm_armor_break_shoulders</c>) plays on a player whose armour just broke:
    /// in Classic PvP not when a ranged hit broke it (a melee break keeps it: the request is about shots); Musou Mode and <paramref name="restore"/> keep it always.
    /// </summary>
    public static bool DrawsBreakFx(bool melee, ContentExtras extras, bool restore, bool oldLook = false) => !oldLook || melee || !Classic(extras) || restore;

    /// <summary>The client flag that brings back the 2026-11-11 morning look of Classic PvP: no model flash on a bare-armour hit, no break effect for a break a shot caused (owner's answers, 2026-11-11, restored both).</summary>
    public const string OldLookFlag = "old-classic-hit-fx";

    /// <summary>
    /// The brief red model flash on a hit that finds the armour already gone (<c>ArmourFlashTracker.Reflash</c>): back in Classic PvP (owner, 2026-11-11), where the health-hit burst stays off, unless
    /// <paramref name="oldLook"/>; <paramref name="restore"/> (<c>--player-hit-fx</c>: the burst is on again, as before) keeps the burst alone, <paramref name="oldHealthHit"/> (<c>--old-health-hit</c>) always flashes.
    /// </summary>
    public static bool BareHitFlash(ContentExtras extras, bool restore, bool oldLook, bool oldHealthHit) => oldHealthHit || (Classic(extras) && !restore && !oldLook);
}
