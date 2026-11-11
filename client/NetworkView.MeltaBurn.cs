using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.ImpactRules;
using SmNet;

namespace SmClient;

/// <summary>
/// fix/melta-burn-onhit (owner, 2026-11-07, classic PvP; a 4.7 s clip of a melta kill): "the burned on hit effect of the melta needs to appear on targets hit by melta shots as soon as the shot appears
/// to strike the enemy" and "needs to cover the whole silhouette of the body ... only a small amount of flame at the moment in the center of the target".
/// The data's <c>meltagun_dmg_burn_limb_armor</c> (x1.5) is one effect at the hit point and exists in the weapon's container only for the armour-up surface, so a hit that broke the armour (a kill)
/// played none, and the one that did play sat at a single spot. Now every melta hit on another player also plays the same effect (*original asset loaded*) on <see cref="MeltaBurnRules.Bones"/>, head to feet
/// including both arms, attached to the victim's skeleton, in the hit's own frame (no delay: the melta's blast has no flight time). When the hit kills, the follower hands over to the corpse's skeleton
/// (and holds its last place for the frames between), so the burn goes on for the effect's own life. Cost: at most one set per victim per <see cref="MeltaBurnRules.Gap"/> s, <see cref="MeltaBurnRules.Bones"/>
/// pooled effects of the existing effect player (priority 2), the bone lookups cached. Both renderers (the effect player draws with CPU-driven sprites, not GPU particles). Ours: the bone list, scale and gap.
/// Before / after: <c>--old-melta-burn</c>; log: <c>--log-hit-fx</c> (a HITFX melta-burn line with the time).
/// </summary>
public partial class NetworkView
{
    const string MeltaBurnEffect = "meltagun_dmg_burn_limb_armor";

    bool? _oldMeltaBurn;
    /// <summary><c>--old-melta-burn</c>: the burn only at the single hit point, and only on raised armour (as before).</summary>
    bool OldMeltaBurn => _oldMeltaBurn ??= CmdFlag("old-melta-burn");

    bool? _meltaBurnOnHit;
    /// <summary><c>--melta-burn-on-hit</c>: the whole-body burn on every melta hit again (as 2026-11-07 to 2026-11-11), not only on a kill.</summary>
    bool MeltaBurnOnHit => _meltaBurnOnHit ??= CmdFlag("melta-burn-on-hit");

    /// <summary>
    /// Owner, 2026-11-11 ("the melta should burn the body in classic and in musou mode of targets it kills"): the whole-body burn plays when a melta weapon KILLS the target (the kill feed's cause is a weapon
    /// whose name says melta), in Classic PvP and in Musou Mode (Orks, Guardsmen and the like), on the corpse's skeleton; a mere hit burns nothing. Called when the victim's corpse starts. A rig with none
    /// of <see cref="MeltaBurnRules.Bones"/> (an Ork's) burns at the corpse's focus instead (ours).
    /// </summary>
    void MeltaKillBurn(int victim, KillView? kill, Ragdoll? corpse)
    {
        if (MeltaBurnOnHit || kill is not { } k || _config is null || _fx is null) return;
        if (_meltaKilled.TryGetValue(victim, out double done) && _connectedFor - done < MeltaKillGuardSeconds) return;   // once per death (a jump death calls twice: at its start and at its ragdoll)
        if (!MeltaBurnRules.KillBurns(k.Cause, k.Index, _meltaWeaponNames ??= _config.Weapons.Select(w => w.Name).ToList())) return;
        _meltaKilled[victim] = _connectedFor;
        if (_meltaKilled.Count > 64) foreach (var gone in _meltaKilled.Where(kv => _connectedFor - kv.Value > 10.0).Select(kv => kv.Key).ToList()) _meltaKilled.Remove(gone);
        string wargear = _config.Weapons[k.Index].Name;
        int played = MeltaBurnBody(victim, wargear, true, null, kill: true);
        // Only a rig with none of the named bones (an Ork's) burns at the corpse's focus (ours); a refused set (the burn is off, no effect) burns nothing.
        if (played == 0 && corpse is not null && IsInstanceValid(corpse) && _fx.System(MeltaBurnEffect) is not null)
        {
            _fx.Play(MeltaBurnEffect, corpse.FocusPoint, Basis.Identity, 1f, null, () => IsInstanceValid(corpse) ? (corpse.FocusPoint, Basis.Identity) : null, 0f, 0.0, false, 2);
            _meltaBurns++;
            if (LogHitFx) GD.Print($"HITFX melta-burn t {_connectedFor:F3} player {victim}: kill burn at the corpse's focus (no named bones on this rig)");
        }
        else if (LogHitFx) GD.Print($"HITFX melta-burn kill t {_connectedFor:F3} player {victim} by {wargear}: {played} effects");
    }

    const double MeltaKillGuardSeconds = 4.0;
    readonly Dictionary<int, double> _meltaKilled = new();
    List<string>? _meltaWeaponNames;

    readonly Dictionary<int, double> _meltaBurnAt = new();
    int _meltaBurns;

    /// <summary>The whole-body burn on <paramref name="victim"/>, in this frame. <paramref name="named"/>: the burn effect the container resolved for the hit, when it did (its scale and life are kept).</summary>
    int MeltaBurnBody(int victim, string wargear, bool bodyHit, EffectPlayLike? named, bool kill = false)
    {
        if (_fx is null || _client is null || OldMeltaBurn) return -1;
        double last = kill ? double.NegativeInfinity : _meltaBurnAt.GetValueOrDefault(victim, double.NegativeInfinity);   // a kill burns once per death (the guard of MeltaKillBurn), whatever the gap
        if (!MeltaBurnRules.Due(MeltaBurnRules.IsMelta(wargear), bodyHit, victim, kill ? -1 : _client.PlayerId, _connectedFor, last)) return -1;   // a kill burns the own body too
        string name = named?.Name ?? MeltaBurnEffect;
        if (_fx.System(name) is null) return -1;
        _meltaBurnAt[victim] = _connectedFor;
        if (_meltaBurnAt.Count > 32) foreach (var gone in _meltaBurnAt.Keys.Where(id => !_remotes.ContainsKey(id)).ToList()) _meltaBurnAt.Remove(gone);
        int played = 0;
        string spread = "";
        foreach (var bone in MeltaBurnRules.Bones)
        {
            var follow = BodyOrCorpseMarker(victim, bone);
            if (follow?.Invoke() is not { } place) continue;
            if (_fx.Play(name, place.Position, Basis.Identity, 1f, null, follow, named?.Lifetime ?? 0f, 0.0, false, 2) is not null) played++;
            if (LogHitFx) spread += $" {bone}={place.Position.Y:F2}";
        }
        _meltaBurns += played;
        if (LogHitFx) GD.Print($"HITFX melta-burn t {_connectedFor:F3} player {victim}: {played} of {MeltaBurnRules.Bones.Length} bones (total {_meltaBurns}); heights{spread}");
        return played;
    }

    /// <summary>The effect a melta hit resolved (name, life) without tying the burn to the catalogue's play type.</summary>
    internal readonly record struct EffectPlayLike(string Name, float Lifetime);

    /// <summary>
    /// A follower of a bone of the victim: the living model's while it is drawn, else the latest corpse of that player (a kill hands over), else where it last was (the frames between).
    /// </summary>
    Func<(Vector3 Position, Basis Axes)?>? BodyOrCorpseMarker(int victim, string marker)
    {
        (Vector3 Position, Basis Axes)? held = null;
        bool Found(Skeleton3D? skeleton, out Vector3 at)
        {
            at = default;
            if (skeleton is null || !IsInstanceValid(skeleton) || !skeleton.IsInsideTree()) return false;
            int bone = BoneOf(skeleton, marker);
            if (bone < 0) return false;
            at = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone)).Origin;
            return true;
        }
        var first = Resolve();
        if (first is null) return null;
        held = first;
        return () =>
        {
            if (Resolve() is { } now) held = now;
            return held;
        };

        (Vector3 Position, Basis Axes)? Resolve()
        {
            if (_remotes.GetValueOrDefault(victim) is { } live && IsInstanceValid(live) && live.IsInsideTree() && live.Visible && Found(live.Skeleton, out var p)) return (p, Basis.Identity);
            for (int i = _ragdolls.Count - 1; i >= 0; i--)
                if (_ragdolls[i].PlayerId == victim && IsInstanceValid(_ragdolls[i]) && Found(_ragdolls[i].Character?.Skeleton, out var q)) return (q, Basis.Identity);
            return null;
        }
    }
}
