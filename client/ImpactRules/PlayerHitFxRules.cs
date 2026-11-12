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

    /// <summary>What damages a player, for the choice of on-hit effects.</summary>
    public enum DamageKind { Ranged, Melee, Charge, Pound, Slash, Blast }

    /// <summary>The parts of the on-hit look: the yellow (armour) and red (health) model glows, the yellow and red sparks and glow effects, and the large burst (health-hit burst, smoke, the weapon's hit container).</summary>
    [System.Flags]
    public enum HitLook { None = 0, Glows = 1, Sparks = 2, Burst = 4 }

    /// <summary>One glow per damage event on a victim: a second glow within this many seconds is the same event seen twice (a shot's record and the reaction it caused, the hit report and the reaction; ours).</summary>
    public const double DedupeSeconds = 0.25;

    /// <summary>Whether a glow may show now: no glow for this victim in the last <see cref="DedupeSeconds"/>.</summary>
    public static bool DedupePasses(double now, double lastGlowAt) => !(now - lastGlowAt < DedupeSeconds);

    /// <summary>
    /// The armour colour of a damage the server confirmed (the hit report's damage split): armour took some of it (<paramref name="shieldDamage"/> above 0): yellow (true); only health was hit
    /// (<paramref name="healthDamage"/> above 0): red (false); no damage: none (null, no glow).
    /// </summary>
    public static bool? ArmourUpOfDamage(float shieldDamage, float healthDamage) => shieldDamage > 0f ? true : healthDamage > 0f ? false : null;

    /// <summary>Whether a confirmed damage on <paramref name="victim"/> draws a glow on his model in this view: not on the own player (his body is not drawn).</summary>
    public static bool ShowsOnModel(int victim, int ownPlayer) => victim > 0 && victim != ownPlayer;

    /// <summary>
    /// Whether another player's snapshot record shows a hit reaction that just started or restarted (a different kind, or the ticks counting up again): the server's confirmation that a melee or blast
    /// damage landed on him. A reaction-less damage (hyper armour) shows none.
    /// </summary>
    public static bool ReactionStarted(ReactionKind wasKind, int wasTicks, ReactionKind nowKind, int nowTicks) =>
        nowKind != ReactionKind.None && (wasKind == ReactionKind.None || nowTicks < wasTicks || nowKind != wasKind);

    /// <summary>Whether a hit report is one the shot path has not drawn: a melee or splash damage (a plain shot has its own record).</summary>
    public static bool ReportNeedsGlow(bool melee, bool splash, int hits) => hits > 0 && (melee || splash);

    /// <summary>The client flag that brings back the large burst, smoke and hit container on a hit player in Classic PvP (the look of 2026-11-13's first preview).</summary>
    public const string BurstFlag = "player-hit-burst";

    /// <summary>
    /// onhit-range-intensity (owner 2026-11-13: "there should only be large explosive visual effects on ranged weapon shots in musou mode. in classic pvp mode its only the yellow and red spawns and
    /// yellow and red glows that are triggered when a player is shot or meleed"; "any sort of damage should trigger the sparks and glows"): the glows always, the sparks unless Classic PvP
    /// is set to <c>--no-player-hit-fx</c> (<paramref name="particles"/> false), the large burst only on a ranged hit in Musou Mode (or in Classic with <paramref name="burstRestore"/>, <see cref="BurstFlag"/>).
    /// </summary>
    public static HitLook LookFor(DamageKind kind, ContentExtras extras, bool particles, bool burstRestore)
    {
        var look = HitLook.Glows;
        if (particles || !Classic(extras)) look |= HitLook.Sparks;
        if (kind == DamageKind.Ranged && (!Classic(extras) || (particles && burstRestore))) look |= HitLook.Burst;
        return look;
    }

    /// <summary>The client flag that brings back the 2026-11-11 morning look of Classic PvP: no model flash on a bare-armour hit, no break effect for a break a shot caused (owner's answers, 2026-11-11, restored both).</summary>
    public const string OldLookFlag = "old-classic-hit-fx";

    /// <summary>
    /// The brief red model flash on a hit that finds the armour already gone (<c>ArmourFlashTracker.Reflash</c>): back in Classic PvP (owner, 2026-11-11), where the health-hit burst stays off, unless
    /// <paramref name="oldLook"/>; <paramref name="restore"/> (<c>--player-hit-fx</c>: the burst is on again, as before) keeps the burst alone, <paramref name="oldHealthHit"/> (<c>--old-health-hit</c>) always flashes.
    /// </summary>
    public static bool BareHitFlash(ContentExtras extras, bool restore, bool oldLook, bool oldHealthHit) => oldHealthHit || (Classic(extras) && !restore && !oldLook);
}
