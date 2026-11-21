using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode and Warfront (added content), the Dreadnought's first-stomp hitch (2026-11-21): the first stomp of a match built its ring texture, decal textures, chunk node and the draw states of the
/// shockwave, the decals and the chunks in one frame (about 160 ms measured by <c>--stomp-test --fx-frame-times --fx-prior</c>, later stomps 7 ms). Once the melee effects are in and the ring's pixels are made,
/// one speck-sized stomp (<see cref="StompFx.Warm"/>: the data's effects at 0.04 of their size, the shockwave, both decals and one chunk) plays eight metres ahead of the camera, a second and a half
/// after joining, so the first real stomp finds all of it built. Only a Musou-flagged match does this (Classic PvP is untouched).
/// </summary>
public partial class NetworkView
{
    bool _stompWarmed;
    int _stompWarmTries;

    /// <summary>The distance ahead of the camera (m) the warm-up stomp plays at: in view, so the renderer builds its draw states, but the specks are too small to see.</summary>
    const float StompWarmAhead = 8f;

    partial void ExtrasStompWarm(PlayerState me)
    {
        if (_stompWarmed || _fx is null || _meleeFx is null || _config is null || !HitchFixes || _connectedFor < 1.5) return;
        if (!_config.Extras.HasFlag(ContentExtras.Musou)) return;
        if (_camera is not { } cam || !IsInstanceValid(cam) || !me.Alive || _gameSpace is null) return;
        var plays = _meleeFx.Wargear.SelectMany(w => _meleeFx.Stomps(w)).SelectMany(a => a.Entries).SelectMany(e => e.Plays).GroupBy(p => p.Name).Select(g => g.First()).ToList();
        var fwd = -cam.GlobalTransform.Basis.Z; fwd.Y = 0f;
        if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.Forward;   // looking straight up or down: ahead of the feet instead
        var spot = ToGame(cam.GlobalPosition + fwd.Normalized() * StompWarmAhead);
        var (point, normal, surface) = GroundUnder(new Num.Vector3(spot.X, spot.Y, me.Position.Z));
        var extra = DreadnoughtFeel.For(DreadAction.StompLeft).Effects.Select(e => e.Name).Concat(DreadnoughtFeel.For(DreadAction.StompRight).Effects.Select(e => e.Name)).Distinct().ToList();
        var shards = NoShards ? null : GroundShardsNode();
        if (StompFx.Warm(_fx, plays, extra, point, normal, DreadnoughtMelee.StompRadius, shards, _catalogue?.SurfaceName(surface) ?? ""))
        {
            _stompWarmed = true;
            GD.Print($"stomp warmed at {_connectedFor:F1} s: {plays.Count} data effects, {extra.Count} Dreadnought effects, ring radius {DreadnoughtMelee.StompRadius:F1} m");
        }
        else if (++_stompWarmTries > 600) _stompWarmed = true;   // the ring never came (about 10 s): the first stomp shows no ring, as it always did
    }
}
