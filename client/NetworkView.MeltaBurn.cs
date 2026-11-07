using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.ImpactRules;

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

    readonly Dictionary<int, double> _meltaBurnAt = new();
    int _meltaBurns;

    /// <summary>The whole-body burn on <paramref name="victim"/>, in this frame. <paramref name="named"/>: the burn effect the container resolved for the hit, when it did (its scale and life are kept).</summary>
    void MeltaBurnBody(int victim, string wargear, bool bodyHit, EffectPlayLike? named)
    {
        if (_fx is null || _client is null || OldMeltaBurn) return;
        double last = _meltaBurnAt.GetValueOrDefault(victim, double.NegativeInfinity);
        if (!MeltaBurnRules.Due(MeltaBurnRules.IsMelta(wargear), bodyHit, victim, _client.PlayerId, _connectedFor, last)) return;
        string name = named?.Name ?? MeltaBurnEffect;
        if (_fx.System(name) is null) return;
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
