using System;
using Godot;
using SmClient.ImpactRules;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// claude/hit-fx-answers (owner, 2026-11-11: "the melta should burn the body in classic and in musou mode of targets it kills"; added content, client only, no wire change). A Musou death carries no weapon,
/// only the killer and the cause (<see cref="MusouDeathShown"/>), so, as the gore does for the lascannon (<c>MusouKillerHasLascannon</c>), a death by a shot whose killer holds a melta weapon burns:
/// the data's <c>meltagun_dmg_burn_limb_armor</c> at a few heights over the body's place (<see cref="MeltaCrowdHeights"/>; a crowd body is drawn from a vertex animation with no skeleton to follow, so the
/// effects stand where it fell, ours). Cheap for crowds: at most <see cref="MeltaCrowdSets"/> sets a frame (the rest of a burst of deaths goes without), none beyond <see cref="MeltaCrowdMetres"/> from the camera,
/// pooled effects of the existing effect player at priority 2. <c>--melta-burn-on-hit</c> or <c>--old-melta-burn</c> turn it off (the former burns on hits instead).
/// </summary>
public partial class NetworkView
{
    /// <summary>Heights above a crowd body's feet of the effects standing for the whole-body burn (m, ours).</summary>
    static readonly float[] MeltaCrowdHeights = [0.25f, 0.7f, 1.1f, 1.5f];
    /// <summary>The most sets one frame starts, and the farthest body that gets one (m; ours).</summary>
    const int MeltaCrowdSets = 3;
    const float MeltaCrowdMetres = 45f;

    ulong _musouMeltaFrame;
    int _musouMeltaSets, _musouMeltaBurns, _musouMeltaSkipped;

    /// <summary>The name of the weapon in hand of a killer (the own predicted state or a drawn player's), or null when unknown.</summary>
    string? MusouKillerInHand(ushort killer)
    {
        if (killer == 0 || _config is null || _client is null) return null;
        PlayerState? state = null;
        if (killer == _client.PlayerId) state = _client.Predicted;
        else foreach (var o in _lastOthers) if (o.PlayerId == killer) { state = o; break; }
        return state is { } s ? Loadouts.InHand(_config, s)?.Name : null;
    }

    /// <summary>A death the crowd is about to draw (called for each, <paramref name="metres"/> from the camera): burns its body when a melta shot killed it.</summary>
    void MusouMeltaBurn(in MusouDeathShown d, float metres)
    {
        if (_fx is null || _gameSpace is null || OldMeltaBurn || MeltaBurnOnHit || metres > MeltaCrowdMetres) return;
        if (!MeltaBurnRules.CrowdKillBurns(d.Cause == MusouDeathKind.Shot, MusouKillerInHand(d.Killer))) return;
        ulong frame = Engine.GetProcessFrames();
        if (frame != _musouMeltaFrame) { _musouMeltaFrame = frame; _musouMeltaSets = 0; }
        if (_musouMeltaSets >= MeltaCrowdSets) { _musouMeltaSkipped++; return; }
        if (_fx.System(MeltaBurnEffect) is null) return;
        _musouMeltaSets++;
        foreach (float height in MeltaCrowdHeights)
        {
            var at = _gameSpace.Transform * new Vector3(d.Position.X, d.Position.Y, d.Position.Z + height);
            if (_fx.Play(MeltaBurnEffect, at, Basis.Identity, 1f, null, null, 0f, 0.0, false, 2) is not null) _musouMeltaBurns++;
        }
        if (LogHitFx) GD.Print($"HITFX melta-burn musou t {_connectedFor:F3} enemy {d.Id} killed by P{d.Killer}'s melta: set {_musouMeltaSets} this frame (burns {_musouMeltaBurns}, skipped {_musouMeltaSkipped})");
    }
}
