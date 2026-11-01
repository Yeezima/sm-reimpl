using System;
using System.Collections.Generic;
using Godot;
using SmClient.ImpactRules;
using SmContent;
using SmNet;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// fx/power-flash (the owner's request of 2026-11-01; client presentation only: no simulation, wire or config-hash change; NOT under Extras, so Faithful builds have it).
/// The blue (the daemon maul's: purple) flash and cloud when a power weapon's melee attack connects: the thunder hammer, the power sword, the power axe and the daemon maul
/// (<c>csm_mp_melee_2hand_power_mace</c>), in classic PvP and in Musou Mode. **Original assets**: the hammer's own hit effects <c>thunderhammer_dmg_body</c> and
/// <c>thunderhammer_dmg_body_secondary</c> (flash, soft balls, glitter, arcs), played at the contact point. **Ours (provisional approximation)**: the layered cloud of
/// <see cref="PowerFlash.Build"/>, the per-weapon scale, the purple, the cap on a swing's bursts. Sound: the weapon's own hit event (existing) plus the hammer's blast events.
/// Test aids: <c>--no-power-flash</c> (before / after), <c>--power-flash-demo sword|axe|hammer|maul</c> (a comma list takes them in turn; <c>--power-flash-at x,y,z</c>), <c>--log-power-flash</c>.
/// </summary>
public partial class NetworkView
{
    bool NoPowerFlash => _noPowerFlash ??= CmdFlag("no-power-flash");
    bool? _noPowerFlash;
    readonly PowerFlashRules.Budget _powerSwing = new();
    int _powerFlashes, _powerFlashSmall;

    /// <summary>A power weapon's hit: the lead burst of a swing is the big one, the next hits of the same swing (a Musou cleave) are small and capped.</summary>
    internal void PowerHit(int attackerId, string? wargear, Vector3 contact, Vector3 outward, string source, bool slam = false)
    {
        if (NoPowerFlash || _fx is null || PowerFlashRules.KindOf(wargear) is not { } kind) return;
        var burst = _powerSwing.Take(attackerId, _connectedFor);
        if (burst == PowerBurst.None) return;
        bool lead = burst == PowerBurst.Lead;
        var axes = AxesFromY(outward.LengthSquared() > 1e-6f ? outward.Normalized() : Vector3.Up);
        // fx/power-flash (owner, 2026-11-01): a camera close to the burst sees a smaller one, so the screen is never whited out.
        float dist = _camera is null ? float.PositiveInfinity : _camera.GlobalPosition.DistanceTo(contact);
        float scale = PowerFlashRules.Scale(kind) * (lead ? 1f : PowerFlashRules.SmallScale) * PowerFlashRules.CloseFactor(dist);
        var tint = PowerFlash.Tint(kind);
        int started = 0;
        // A slam (a strike with a blast) brings the data's own displace flash, ball and cloud: no second copy of the hammer's hit effects on top, and a smaller cloud.
        if (!slam && _fx.Play(PowerFlash.MainEffect, contact, axes, scale * PowerFlash.DataBoost, null, null, 0f, 0.0, false, 3, false, tint) is not null) started++;
        if (!slam && lead && _fx.Play(PowerFlash.SecondaryEffect, contact, axes, scale * PowerFlash.DataBoost, null, null, 0f, 0.0, false, 3, false, tint) is not null) started++;
        if (_fx.Play(PowerFlash.CloudName, contact, axes, scale * PowerFlash.CloudBoost * (slam ? PowerFlashRules.SlamCloud : 1f), null, null, 0f, 0.0, false, 3, false, tint) is not null) started++;
        string sounds = lead ? PostPowerBlast(kind, contact) : "-";
        if (lead) _powerFlashes++; else _powerFlashSmall++;
        _fxLastShotAt = _connectedFor; _fxLastEnd = contact; _fxLastFrom = contact;
        if (_bot || CmdFlag("log-power-flash") || CmdFlag("log-melee-fx"))
            GD.Print($"POWERFLASH t {_connectedFor:F2} P{attackerId} {kind} {(lead ? "lead" : "small")} x{scale:0.##}{(slam ? " slam" : "")} cam {dist:0.#} m at {contact.X:F1},{contact.Y:F1},{contact.Z:F1} ({source}); {started} effects; blast sounds {sounds}; shown {_powerFlashes} big, {_powerFlashSmall} small");
    }

    /// <summary>The hammer's blast sounds for the hammer and the maul (data events, <see cref="PowerFlash.BlastEvents"/>); the weapon's own hit sound is posted elsewhere. Returns the events posted.</summary>
    string PostPowerBlast(PowerKind kind, Vector3 at)
    {
        // fx/melee-hit-sound: the generated roar replaces the hammer's and maul's hit sound; a second boom under it would fight it, so the blast layer only plays with --old-melee-hit-sound.
        if (_sound is null || _soundTriggers is null || NoMeleeSounds || !OldMeleeHitSound || kind is not (PowerKind.Hammer or PowerKind.Maul)) return "-";
        foreach (var evt in PowerFlash.BlastEvents)
        {
            var emitter = new SoundEmitter { Position = at, Label = $"power flash {kind}", OneShot = true };
            var play = _sound.Post(evt, emitter);
            if (LogSound("melee") || CmdFlag("log-power-flash")) GD.Print($"POWERBLAST t {_connectedFor:F3} {kind} event {evt} | {VoiceMixText(play, emitter, default)}");
        }
        return string.Join("+", PowerFlash.BlastEvents);
    }

    // ---- --power-flash-demo ----

    string[]? _pfCases;
    double _pfNext = -1, _pfStart;

    void PowerFlashDemo()
    {
        _pfCases ??= CmdArg("power-flash-demo") is { } a ? a.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        if (_pfCases.Length == 0 || _camera is null) return;
        if (_pfNext < 0) { _pfNext = _connectedFor + 3.0; _pfStart = _pfNext; }
        if (_connectedFor < _pfNext) return;
        _pfNext = _connectedFor + 2.5;
        string name = _pfCases[(int)((_connectedFor - _pfStart) / 2.5) % _pfCases.Length];
        var cam = _camera.GlobalPosition;
        Vector3 at = cam + (-_camera.GlobalTransform.Basis.Z) * 4f;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (CmdArg("power-flash-at") is { } s && s.Split(',') is { Length: 3 } p
            && float.TryParse(p[0], inv, out float x) && float.TryParse(p[1], inv, out float y) && float.TryParse(p[2], inv, out float z)) at = ToGodot(new Num.Vector3(x, y, z));
        string wargear = name switch { "hammer" => "sm_mp_melee_2hand_thunder_hammer", "maul" => "csm_mp_melee_2hand_power_mace", "axe" => "sm_mp_melee_power_axe", _ => "sm_mp_melee_powersword" };
        _powerSwing.Forget(-1);
        PowerHit(-1, wargear, at, (cam - at).Normalized(), "demo");
    }
}
