using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The ranged Orks' shots in Musou Mode (added content; <see cref="MusouOrkGunRules"/>): each round the simulation reports (a <see cref="MusouEventKind.Tracer"/> event: the shooter, the end point and
/// whether it struck a player) plays the gun wargear's own muzzle flash at the Ork's muzzle (<see cref="MusouCrowd.MuzzleOf"/>), on his tracer rounds the wargear's tracer to the end, and the wargear's
/// impact by what it strikes: the player's surface on a hit, on a miss the wall or floor the round flies on into (<see cref="MusouOrkGunRules.MissReach"/>), none in the open. Which rounds get
/// which effect is <see cref="MusouGunFxPlanner"/>'s (distances and rates, nearest first). The fire sound is the crowd's sounds' (<c>NetworkView.MusouSounds.cs</c>: the wargear's
/// <c>play_or_shoota_fire</c> under the crowd's voice caps and the data's instance limits). The effects are compiled once on a worker. Every 10 s one log line of counts and the cost.
/// <c>--no-musou-guns</c>: no guns in hand and no shot effects (the behaviour before). Capture aid <c>--musou-gun-cam</c> (with <c>--spectate shot</c>): the shot view frames the latest Ork tracer.
/// </summary>
public partial class NetworkView
{
    Task<(EffectCatalogue Catalogue, Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals, List<string> Failures, List<string> Notes)>? _musouGunFxLoad;
    EffectCatalogue? _musouGunCatalogue;
    bool _musouGunFxReady;
    readonly MusouGunFxPlanner _musouGunPlan = new();
    readonly List<MusouGunShot> _musouGunShots = new();
    readonly List<(MusouEvent Event, int Shown)> _musouGunRounds = new();
    MusouGunFx[] _musouGunFx = new MusouGunFx[64];
    long _musouGunNoMuzzle, _musouGunNoWall;
    double _musouGunMs, _musouGunLogAt;
    int _musouGunFrames;

    bool MusouGunsOn => _musouGunsOn ??= !CmdFlag("no-musou-guns");
    bool? _musouGunsOn;
    bool MusouGunCam => _musouGunCam ??= CmdFlag("musou-gun-cam");
    bool? _musouGunCam;
    (double At, Vector3 From, Vector3 End)? _musouGunCamShot;

    /// <summary>This frame's rounds (before the crowd's frame: the shooters take their aiming stance).</summary>
    void MusouGunsFrame(IReadOnlyList<MusouShown> shown, Vector3 camera, float delta)
    {
        if (!MusouGunsOn || _musouCrowd is not { } crowd) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        MusouGunFxLoad();
        _musouGunPlan.Refill(delta);
        _musouGunShots.Clear(); _musouGunRounds.Clear();
        if (MusouGunCam && _musouGunCamShot is { } cam && _connectedFor - cam.At < 1.4) { _fxLastShotAt = cam.At; _fxLastFrom = cam.From; _fxLastEnd = cam.End; }   // the shot view stays on the Orks' fire
        foreach (var e in _musouState.NewEvents)
        {
            if (e.Kind != MusouEventKind.Tracer) continue;
            int i = MusouFind(shown, e.Enemy);
            if (i < 0 || MusouOrkGunRules.WargearOf(shown[i].Type) is null) continue;
            crowd.Fired(e.Enemy);
            var p = shown[i].Position;
            _musouGunShots.Add(new MusouGunShot(e.Enemy, e.Tick, new Vector3(p.X, p.Y, p.Z).DistanceTo(camera), e.Player != 0, MusouOrkGunRules.IsElite(shown[i].Type)));
            _musouGunRounds.Add((e, i));
        }
        if (_musouGunShots.Count > 0)
        {
            if (_musouGunFx.Length < _musouGunShots.Count) _musouGunFx = new MusouGunFx[Math.Max(_musouGunShots.Count, _musouGunFx.Length * 2)];
            _musouGunPlan.Plan(_musouGunShots, _musouGunFx);
            bool play = _musouGunFxReady && _musouGunCatalogue is not null && FxActive && _gameSpace is not null;
            for (int k = 0; k < _musouGunRounds.Count && play; k++)
                if (_musouGunFx[k] != MusouGunFx.None) MusouGunRound(_musouGunRounds[k].Event, shown[_musouGunRounds[k].Shown], _musouGunFx[k]);
        }
        _musouGunMs += watch.Elapsed.TotalMilliseconds;
        _musouGunFrames++;
        MusouGunsLog();
    }

    /// <summary>The gun effects are not in the multiplayer weapons' catalogue: read and compiled once on a worker when the effect player is up.</summary>
    void MusouGunFxLoad()
    {
        if (_musouGunFxReady || _fx is not { IsReady: true, Library: { } library }) return;
        string dir = _gameDir;
        _musouGunFxLoad ??= Task.Run(() =>
        {
            var notes = new List<string>();
            var catalogue = MusouOrkGunEffects.Load(dir, notes);
            var failures = new List<string>();
            var systems = CompileSystems(library, MusouOrkGunEffects.EffectNames(catalogue), failures);
            var decals = library.Textures(FxPlayer.DecalNames(systems.Values));
            return (catalogue, systems, decals, failures, notes);
        });
        if (!_musouGunFxLoad.IsCompleted) return;
        _musouGunFxReady = true;
        if (_musouGunFxLoad.IsFaulted) { GD.PrintErr("musou guns: effects: " + _musouGunFxLoad.Exception?.GetBaseException().Message); return; }
        var (cat, compiled, decalBytes, failed, catalogueNotes) = _musouGunFxLoad.Result;
        _musouGunCatalogue = cat;
        _fx.Preload(compiled, decalBytes);
        foreach (var g in MusouOrkGunRules.Guns)
            GD.Print(cat.Weapon(g.Wargear) is { } w
                ? $"musou guns: {g.Type} {g.Wargear}: muzzle {w.Muzzle?.Name ?? "none"} at {w.MuzzleMarker}, tracer {w.Tracer?.Name ?? "none"}, impacts {w.HitMaterial?.ContainerName ?? "none"}"
                : $"musou guns: {g.Type} {g.Wargear}: no effect data");
        GD.Print($"musou guns: {compiled.Count} effects compiled ({string.Join(", ", compiled.Keys.Order(StringComparer.Ordinal))}){(failed.Count > 0 ? "; failed: " + string.Join(", ", failed) : "")}");
        foreach (var n in catalogueNotes.Take(4)) GD.Print("  musou guns: " + n);
    }

    /// <summary>One round's planned effects.</summary>
    void MusouGunRound(in MusouEvent e, in MusouShown shooter, MusouGunFx plan)
    {
        var fx = _fx!;
        if (_musouGunCatalogue!.Weapon(MusouOrkGunRules.WargearOf(shooter.Type)!) is not { } w) return;
        if (_musouCrowd!.MuzzleOf(shooter) is not { } muzzle) { _musouGunNoMuzzle++; return; }
        var end = new Vector3(e.Vector.X, e.Vector.Y, e.Vector.Z);
        var line = end - muzzle;
        float length = line.Length();
        if (length < 0.4f) return;
        var dir = line / length;
        bool hitPlayer = e.Player != 0;
        // A miss flies on past the end the simulation gave (beside the target) into the first wall or floor, if any.
        var hit = hitPlayer ? ShotHit.Body : ShotHit.None;
        int surface = -1;
        var normal = -dir;
        if (!hitPlayer && (plan & (MusouGunFx.Tracer | MusouGunFx.Impact)) != 0)
        {
            if (MusouGunWall(muzzle, dir, length + MusouOrkGunRules.MissReach) is { } wall) { end = wall.Point; normal = wall.Normal; surface = wall.Surface; hit = ShotHit.World; }
            else { end = muzzle + dir * (length + MusouOrkGunRules.MissReach); _musouGunNoWall++; }
        }
        var toGodot = _gameSpace!.Transform;
        Vector3 fromG = toGodot * muzzle, endG = toGodot * end, dirG = (toGodot.Basis * dir).Normalized(), normalG = (toGodot.Basis * normal).Normalized();
        var axes = AxesFromY(dirG);
        if ((plan & MusouGunFx.Flash) != 0)
            foreach (var p in w.Fire)
                if (!p.HasTarget && p.Name.Length > 0) fx.Play(p.Name, fromG, axes, p.Scale <= 0f ? MusouOrkGunRules.FlashScale : p.Scale, priority: 0, smokeRein: SmokeRein);
        float travel = 0f;
        float total = fromG.DistanceTo(endG);
        if (TracerHeadSpeed(w.Tracer?.Name ?? "") is > 1f and var head) travel = total / MathF.Max(head, total / MaxTracerFlight);
        if ((plan & MusouGunFx.Tracer) != 0 && w.Tracer is { } tracer && total >= 0.4f)
        {
            var t = fx.Play(tracer.Name, fromG, axes, tracer.Scale <= 0f ? 1f : tracer.Scale, target: endG, priority: 1, smokeRein: SmokeRein);
            if (t is not null && travel > 0f) t.HeadSpeed = total / travel;
            if (t is not null && MusouGunCam && (_musouGunCamShot is not { } last || _connectedFor - last.At > 1.0)) _musouGunCamShot = (_connectedFor, fromG - dirG * 2f, fromG + dirG * 8f);   // framed on the shooter and the first metres of the tracer
        }
        if ((plan & MusouGunFx.Impact) != 0 && hit != ShotHit.None)
            foreach (var p in _musouGunCatalogue.Resolve(w.HitMaterial, hit, surface, armourUp: true))
            {
                var axis = p.Direction switch
                {
                    "reflect_hit_direction" => hit == ShotHit.World ? dirG - 2f * dirG.Dot(normalG) * normalG : -dirG,
                    "negative_hit_direction" => -dirG,
                    "hitpoint_normal" => hit == ShotHit.World ? normalG : -dirG,
                    _ => normalG,
                };
                fx.Play(p.Name, endG + normalG * 0.02f, AxesFromY(axis), p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, travel, false, 0);
            }
    }

    /// <summary>The first wall or floor along a ray in game space: the point, the normal facing the ray, and its surface.</summary>
    (Vector3 Point, Vector3 Normal, int Surface)? MusouGunWall(Vector3 from, Vector3 dir, float reach)
    {
        if (_client?.Collision is not { } world) return null;
        if (world.Weapons.Raycast(new Num.Vector3(from.X, from.Y, from.Z), new Num.Vector3(dir.X, dir.Y, dir.Z), reach) is not { } h) return null;
        var n = new Vector3(h.Normal.X, h.Normal.Y, h.Normal.Z);
        if (n.Dot(dir) > 0f) n = -n;
        return (new Vector3(h.Point.X, h.Point.Y, h.Point.Z), n, world.Weapons.MaterialOf(h.Triangle));
    }

    void MusouGunsLog()
    {
        if (_connectedFor < _musouGunLogAt || _musouGunFrames == 0) return;
        _musouGunLogAt = _connectedFor + 10.0;
        var p = _musouGunPlan;
        GD.Print($"musou guns: t {_connectedFor:F0} s: rounds {p.Rounds}, flashes {p.Flashes}, tracers {p.Tracers}, impacts {p.Impacts}, held by rate {p.OverRate}, too far {p.TooFar}, no muzzle {_musouGunNoMuzzle}, misses into the open {_musouGunNoWall}; " +
                 $"cost {_musouGunMs / _musouGunFrames:F3} ms a frame; effects live {_fx?.Live.ToString() ?? "-"}");
        _musouGunMs = 0; _musouGunFrames = 0;
    }
}
