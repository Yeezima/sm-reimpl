using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The ground pound's impact and the jump pack's landings on the ground (M41; client presentation only, no simulation or protocol change). Where the client sees a
/// pound end (<see cref="TrackJump"/>: the own player's prediction, the others' flags) it plays the effect the wargear's <c>actn_gp_hit</c> names (<c>jumppack_land_heavy</c>:
/// the crater and crack decals, flash, dust, debris sprites and shockwave sprites; read by <see cref="JumpPackEffects"/>, played with their collisions by <see cref="FxPlayer"/>),
/// a dark ring at the blast's radius (<see cref="PoundRing"/>, **ours**), a handful of physics ground chunks (<see cref="GroundShards"/>, **ours**) and a short camera shake near it (ours;
/// the data's <c>camera_effect</c> amplitudes are not mapped). The sound is posted at the same moment (<see cref="SoundJump"/>). A landing plays the data's landing effect for its
/// drop (<c>jumppack_land_normal</c> from 15 m, <c>jumppack_land_light</c> below) and nothing of ours. The old placeholder rings (<see cref="AddRing"/>) show only with <c>--show-markers</c>.
/// </summary>
public partial class NetworkView
{
    GroundShards? _groundShards;
    int _poundFx, _landingFx;
    bool _ringsRequested;

    /// <summary>Beyond this distance from the camera (m) a pound lays no ring and throws no chunks (the effect itself is culled by the player at 220 m). Ours.</summary>
    const float PoundDetailRange = 80f;

    /// <summary>Starts making the ring textures for the pound radii of the config's jump packs on a worker thread, once (the first pound then has its ring).</summary>
    void RequestPoundRings()
    {
        if (_ringsRequested || _config is null) return;
        _ringsRequested = true;
        foreach (var pack in _config.JumpPacks) if (pack.Pound is { } pound) PoundRing.Request(pound.Radius);
    }

    /// <summary>
    /// The distance from the camera inside which an impact shakes the screen (m), and the largest shake (radians; decays at 14 per second like the weapon kicks). Ours
    /// (M44, the owner's request for a much heavier impact: 0.075 within 35 m; 0.03 within 30 m in M41). One shake per impact, so many pounds do not stack past a frame's kick.
    /// </summary>
    const float PoundShakeRange = 35f, PoundShakeAmount = 0.09f;   // feel/jump-pack (the owner: "intensify the effect"): 0.075 in M44

    GroundShards GroundShardsNode()
    {
        if (_groundShards is { } g && IsInstanceValid(g)) return g;
        if (_ragdollWorld is null || !IsInstanceValid(_ragdollWorld)) EnsureRagdollWorld();   // the level the chunks fall on is the ragdolls' (or a flat floor)
        _groundShards = new GroundShards { Name = "GroundShards" };
        AddChild(_groundShards);
        return _groundShards;
    }

    /// <summary>
    /// The level under a player's feet (game space): the point, its upward surface normal (Godot space), the surface material id and whether the level was hit (else the feet and straight up).
    /// A ray down from half a metre above the feet, like the sound's surface switch.
    /// </summary>
    (Vector3 Point, Vector3 Normal, int Surface) GroundUnder(Num.Vector3 feet)
    {
        if (_client?.Collision is { } world && world.Weapons.Raycast(feet + new Num.Vector3(0f, 0f, 0.6f), -Num.Vector3.UnitZ, 3f) is { } hit)
        {
            var normal = hit.Normal.Z < 0f ? -hit.Normal : hit.Normal;
            return (ToGodot(hit.Point), DirToGodot(normal).Normalized(), world.Weapons.MaterialOf(hit.Triangle));
        }
        return (ToGodot(feet), Vector3.Up, -1);
    }

    /// <summary>The pound's impact for <paramref name="s"/> at the ground it stands on; <paramref name="pack"/> is its jump pack.</summary>
    void PoundImpactPresentation(PlayerState s, bool own, JumpPackSpec? pack)
    {
        if (!FxActive || _fx is null || _catalogue is null || pack is null) return;
        if (_catalogue.JumpPack(pack.Name) is not { } effects) { if (_fxNoted.Add("jumppack:" + pack.Name)) GD.Print($"  effects: no ground effects for jump pack '{pack.Name}'"); return; }
        var (point, normal, surface) = GroundUnder(s.Position);
        float radius = pack.Pound?.Radius ?? 0f;   // the simulation's pound blast: the hit-detection radius the ring marks
        bool near = _camera is not { } camNear || camNear.GlobalPosition.DistanceTo(point) <= PoundDetailRange;   // far away: the effect only, no ring or chunks
        int started = GroundPoundFx.Play(_fx, GroundPoundFx.WithDust(effects.Pound, effects.LightLanding, near ? effects.LandingFor(float.MaxValue) : null), point, normal, near ? radius : 0f, NoShards || !near ? null : GroundShardsNode(), _catalogue.SurfaceName(surface), s.PlayerId * 31 + _poundFx).Count;
        if (started == 0) return;
        _poundFx++;
        _fxLastShotAt = _connectedFor; _fxLastEnd = point; _fxLastFrom = point;   // for --screenshot-on-effect
        if (_camera is { } cam)
        {
            float distance = cam.GlobalPosition.DistanceTo(point);
            if (distance < PoundShakeRange) FxShake(Math.Clamp(1f - distance / PoundShakeRange, 0f, 1f) * PoundShakeAmount);
        }
        if (_bot) GD.Print($"t {_connectedFor:F1} s: pound effects at {point.X:F1},{point.Y:F1},{point.Z:F1} ({started} effects: {string.Join(", ", effects.Pound.Select(p => p.Name))}; ring {radius:F1} m; surface {_catalogue.SurfaceName(surface)}; shards {(NoShards ? "off" : "on")}; {(own ? "own" : "seen")})");
    }

    /// <summary>A landing after <paramref name="drop"/> metres from the highest point: the data's landing effect for that drop only.</summary>
    void LandingPresentation(PlayerState s, bool own, JumpPackSpec? pack, float drop)
    {
        if (!FxActive || _fx is null || _catalogue is null || pack is null || _catalogue.JumpPack(pack.Name) is not { } effects) return;
        var plays = effects.LandingFor(drop);
        if (plays.Count == 0) return;
        var (point, normal, surface) = GroundUnder(s.Position);
        int started = GroundPoundFx.Play(_fx, plays, point, normal, 0f, null, _catalogue.SurfaceName(surface), 0).Count;
        if (started == 0) return;
        _landingFx++;
        if (_bot) GD.Print($"t {_connectedFor:F1} s: landing effects at {point.X:F1},{point.Y:F1},{point.Z:F1} after a {drop:F1} m drop ({string.Join(", ", plays.Select(p => p.Name))}; {(own ? "own" : "seen")})");
    }

    /// <summary><c>--no-shards</c> (test aid): no physics chunks.</summary>
    bool NoShards => _noShards ??= OS.GetCmdlineUserArgs().Contains("--no-shards");
    bool? _noShards;
}
