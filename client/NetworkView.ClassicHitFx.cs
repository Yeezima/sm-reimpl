using System.Collections.Generic;
using Godot;
using SmClient.ImpactRules;
using SmSim;

namespace SmClient;

/// <summary>
/// classic-no-player-hit-fx (owner, 2026-11-11; client presentation only, no protocol or config-hash change): in Classic PvP a ranged hit on a player draws none of the on-hit
/// particle effects on or at him (<see cref="PlayerHitFxRules"/>): <c>FxImpact</c> skips the armour-hit effects, the spark fans, the health-hit burst, the (on-hit) melta burn and the hit
/// container for a body or head hit, <c>DetonationBodyHit</c> the armour-hit effects, (SUPERSEDED in part, owner's answers 2026-11-11, claude/hit-fx-answers: a break a shot caused plays <c>sm_armor_break_shoulders</c> again, the bare-armour model flash is back and the melta burns only on a kill; see <see cref="PlayerHitFxRules.DrawsBreakFx"/>, <see cref="PlayerHitFxRules.BareHitFlash"/>). The victim model's flash and
/// glow, the sounds, the HUD, the tracer and the map's impacts are unchanged; Musou Mode keeps everything. <c>--player-hit-fx</c> restores the effects; <c>--log-hit-fx</c> logs a
/// <c>HITFX classic</c> line for each hit and break left bare.
/// </summary>
public partial class NetworkView
{
    bool? _playerHitFx;
    /// <summary>Whether Classic PvP draws the on-hit effects on players: yes since 2026-11-13 (owner: "bring back the spark and burst particles on hit"); <c>--no-player-hit-fx</c> is the look of 2026-11-11 to 2026-11-13.</summary>
    bool PlayerHitFxFlag => _playerHitFx ??= !CmdFlag(PlayerHitFxRules.NoParticlesFlag);   // owner 2026-11-13: drawn again by default; --no-player-hit-fx is the 2026-11-11 look

    /// <summary>The match's content flags (none before the config is in, which reads as Classic).</summary>
    bool? _playerHitBurst;
    /// <summary><c>--player-hit-burst</c>: Classic PvP draws the large burst, smoke and the weapon's hit container on a shot player again (owner 2026-11-13: only the yellow and red sparks and glows by default).</summary>
    bool PlayerHitBurstFlag => _playerHitBurst ??= CmdFlag(PlayerHitFxRules.BurstFlag);

    /// <summary>Whether a ranged hit on a player draws the large burst and the weapon's hit container (always for a hit that is not on a player).</summary>
    bool HitBurstDrawn(ShotHit hit) => !PlayerHitFxRules.OnPlayer(hit) || PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Ranged, MatchExtras, PlayerHitFxFlag, PlayerHitBurstFlag).HasFlag(PlayerHitFxRules.HitLook.Burst);

    ContentExtras MatchExtras => _config?.Extras ?? ContentExtras.None;

    bool? _oldClassicHitFx;
    /// <summary><c>--old-classic-hit-fx</c>: Classic PvP without the bare-armour model flash and without the break effect for a shot's break (the look of the morning of 2026-11-11).</summary>
    bool OldClassicHitFxFlag => _oldClassicHitFx ??= CmdFlag(PlayerHitFxRules.OldLookFlag);

    /// <summary>The brief model flash on a hit with no armour left (owner, 2026-11-11: "the model flash should come back on for bare-armor hits"); see <see cref="PlayerHitFxRules.BareHitFlash"/>.</summary>
    bool BareHitFlashOn => PlayerHitFxRules.BareHitFlash(MatchExtras, false, OldClassicHitFxFlag, OldHealthHitFlag);   // owner 2026-11-13: the flash stays with the particles back

    int _classicBareHits, _classicBareBreaks;
    /// <summary>Players whose armour an own melee strike broke this frame (the hit report says so; their break effect stays).</summary>
    readonly HashSet<int> _meleeBreaks = new();

    /// <summary>Whether a ranged hit (<paramref name="hit"/>) draws its on-hit effects; false for a hit on a player in Classic PvP (counted, and logged with <c>--log-hit-fx</c>).</summary>
    bool HitFxDrawn(ShotHit hit, string wargear, int victim)
    {
        if (PlayerHitFxRules.DrawsHitFx(hit, MatchExtras, PlayerHitFxFlag)) return true;
        _classicBareHits++;
        if (LogHitFx) GD.Print($"HITFX classic t {_connectedFor:F3}: no on-hit effects for {wargear} on player {victim} (total {_classicBareHits})");
        return false;
    }

    /// <summary>The own hit report: an own melee strike broke <paramref name="victim"/>'s armour (the break effect is kept for it).</summary>
    void NoteMeleeBreak(int victim) => _meleeBreaks.Add(victim);

    /// <summary>The armour's break effect on <paramref name="victim"/>, unless a shot broke it in Classic PvP.</summary>
    void ArmourBreakFxIfDrawn(int victim)
    {
        if (PlayerHitFxRules.DrawsBreakFx(_meleeBreaks.Contains(victim), MatchExtras, PlayerHitFxFlag, OldClassicHitFxFlag)) { ArmourBreakFx(victim); return; }
        _classicBareBreaks++;
        if (LogHitFx) GD.Print($"HITFX classic t {_connectedFor:F3}: no break effect on player {victim} (a shot broke the armour; total {_classicBareBreaks})");
    }
}
