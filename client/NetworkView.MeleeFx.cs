using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Fx;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// fx/impact-pass (the owner's requests of 2026-10-29; client presentation only, no simulation or protocol change):
/// <list type="bullet">
/// <item><b>Stomps and slams</b> ("make a pass where you add effects such as ground breaking/shockwaves/etc. to give the stomps impact and weight"). Every melee step with a blast
/// (<see cref="MeleeStep.Stomp"/>: the Tactical's and Assault's foot stomp, the heavy classes' stomp, the quick stomp, the axe's and the hammer's slams) plays, when its hit time passes
/// (the own player from the prediction, the others from their records), the data's effect for that blast (<see cref="MeleeEffects"/>: <c>interrupt_stomp_dmg</c>, the hammer's
/// <c>thunderhammer_dmg_displace</c>; the Furious Stomp entry's when the step's blast is that entry's) on the ground under its marker (<c>fx_l_foot</c>, <c>fx_r_foot</c> or the weapon's
/// <c>fx_swing</c>), and **ours**, sized to the blast's radius as the M41 pound is (<see cref="StompFx"/>): an expanding shockwave ring and a flash light, extra cracks, the thin
/// ring at the radius and ground chunks. No dust or smoke (the owner removed them); no camera shake (another owner's).</item>
/// <item><b>The power sword</b> ("should glow blue and flare up when hitting an enemy"). While a power sword is in hand: the data's <c>powersword_idle</c> at its <c>fx_powersword_swing</c>
/// (blue edges and light) and the blade glow (<see cref="PowerBlade"/>, ours). When an attack's hit time passes with an enemy in reach (presentation guess as the melee lock's; the
/// server decides): the effects package's <c>powersword_dmg</c> (flash, spikes and sparks; the MP wargear binds no hit effect, so the choice is ours) at the contact point on the
/// victim and the blade's flare.</item>
/// </list>
/// Test aids: <c>--no-stomp-fx</c>, <c>--no-sword-fx</c> (before / after), <c>--stomp-ring &lt;sprite&gt;</c> (the shockwave's ring sprite), <c>--log-melee-fx</c>.
/// </summary>
public partial class NetworkView
{
    sealed record MeleeFxLoad(MeleeEffects Data, Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals);

    Task<MeleeFxLoad?>? _meleeFxLoad;
    MeleeEffects? _meleeFx;
    bool _meleeFxOff;
    readonly Dictionary<int, (int Step, bool Heavy, float Time)> _meleeFxSeen = new();
    readonly Dictionary<int, double> _meleeFxStompAt = new();
    readonly Dictionary<int, FxEffect> _swordIdle = new();
    readonly Dictionary<int, double> _swordFlareAt = new();
    readonly Dictionary<int, MeshInstance3D> _swordGlowMesh = new();
    int _stompFx, _swordHits, _swordIdleStarted;

    /// <summary>The effects package's power sword hit (flash, flash spikes, spark beam, sparks; ours to play it on a power sword hit).</summary>
    const string SwordHitFx = "powersword_dmg";

    bool NoStompFx => _noStompFx ??= CmdFlag("no-stomp-fx");
    bool? _noStompFx;
    bool NoSwordFx => _noSwordFx ??= CmdFlag("no-sword-fx");
    bool? _noSwordFx;

    /// <summary>Called once per frame from <see cref="UpdateFx"/> after the eye glow: loads the melee effects once, then plays the stomps and the power swords.</summary>
    void FxMelee(PlayerState me)
    {
        if (_fx is null || _config is null || _client is null || _meleeFxOff) return;
        if (_meleeFx is null)
        {
            if (!LoadMeleeFx()) return;
        }
        var all = new List<PlayerState>(_lastOthers.Count + 1) { me };
        all.AddRange(_lastOthers);
        var seen = new HashSet<int>();
        foreach (var s in all)
        {
            seen.Add(s.PlayerId);
            MeleeFxFor(s, s.PlayerId == _client.PlayerId, all);
        }
        foreach (var gone in _swordIdle.Keys.Where(id => !seen.Contains(id)).ToList()) StopSwordIdle(gone);
        foreach (var gone in _meleeFxSeen.Keys.Where(id => !seen.Contains(id)).ToList()) _meleeFxSeen.Remove(gone);
        foreach (var gone in _swordGlowMesh.Keys.Where(id => !seen.Contains(id)).ToList()) _swordGlowMesh.Remove(gone);
    }

    /// <summary>Starts (once) and finishes the background read of the melee wargear's effects; true when they are in.</summary>
    bool LoadMeleeFx()
    {
        if (_meleeFxLoad is null)
        {
            // After the map's effects and the eye glow (they compile from the same library on a worker; one reader at a time).
            if (_fxMapLoad is not null || _fxMapSpawned != _client!.MapName || (_eyeLoad is { IsCompleted: false })) return false;
            string dir = _gameDir;
            var library = _fx!.Library;
            var names = _config!.Classes.SelectMany(c => c.MeleeChoices.Select(m => m.Wargear)).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _meleeFxLoad = Task.Run(() =>
            {
                try
                {
                    var data = MeleeEffects.Load(dir, names);
                    var wanted = data.EffectNames.Append(SwordHitFx).Distinct(StringComparer.Ordinal).ToList();
                    var systems = library is null ? new Dictionary<string, CSystem>() : CompileSystems(library, wanted, new List<string>());
                    var decals = library is null ? new Dictionary<string, byte[]>() : library.Textures(FxPlayer.DecalNames(systems.Values));
                    return new MeleeFxLoad(data, systems, decals);
                }
                catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                { GD.PrintErr($"melee effects: wargear unreadable: {ex.Message}; stomp and sword effects are off"); return (MeleeFxLoad?)null; }
            });
            return false;
        }
        if (!_meleeFxLoad.IsCompleted) return false;
        if (_meleeFxLoad.IsFaulted || _meleeFxLoad.Result is null)
        {
            if (_meleeFxLoad.IsFaulted) GD.PrintErr($"melee effects: loading failed ({_meleeFxLoad.Exception?.GetBaseException().Message}); stomp and sword effects are off");
            _meleeFxOff = true; return false;
        }
        var loaded = _meleeFxLoad.Result;
        _fx!.Preload(loaded.Systems, loaded.Decals);
        if (PowerBlade.BuildFlare(loaded.Systems.Values) is { } flare) _fx.Preload(new Dictionary<string, CSystem> { [PowerBlade.FlareName] = flare }, new Dictionary<string, byte[]>());
        if (StompFx.Build(loaded.Systems.Values, CmdArg("stomp-ring")) is { } shockwave) _fx.Preload(new Dictionary<string, CSystem> { [StompFx.ShockwaveName] = shockwave }, new Dictionary<string, byte[]>());
        _meleeFx = loaded.Data;
        foreach (var w in _meleeFx.Wargear) foreach (var a in _meleeFx.Stomps(w)) foreach (var e in a.Entries) if (e.Radius > 0f) PoundRing.Request(e.Radius);   // the rings' pixels on a worker now
        var withStomps = _meleeFx.Wargear.Where(w => _meleeFx.Stomps(w).Count > 0).Order(StringComparer.Ordinal).ToList();
        GD.Print($"melee effects: {withStomps.Count} wargear files with blast strikes ({string.Join(", ", withStomps.Select(w => $"{w}: {string.Join("/", _meleeFx.Stomps(w).Select(a => a.Action))}"))}); " +
                 $"{loaded.Systems.Count} effects compiled; shockwave {(_fx.System(StompFx.ShockwaveName) is { } built ? $"built ({built.Roots.OfType<CRing>().First().Material.Name})" : "none (no ring sprite found)")}");
        return true;
    }

    void MeleeFxFor(PlayerState s, bool own, List<PlayerState> all)
    {
        var character = own ? _local : _remotes.GetValueOrDefault(s.PlayerId);
        string? wargear = MeleeWargear(s);
        SwordFrame(s, character, wargear);
        if (!s.Alive || s.MeleeStep <= 0 || Combat.MeleeStepOf(_config!, s) is not { } step) { _meleeFxSeen.Remove(s.PlayerId); return; }
        // The hit time passing in this attack (the same attack: same step and kind, time not gone back).
        var last = _meleeFxSeen.TryGetValue(s.PlayerId, out var known) ? known : (Step: 0, Heavy: false, Time: -1f);
        bool same = last.Step == s.MeleeStep && last.Heavy == s.MeleeHeavy && s.MeleeTime >= last.Time;
        float before = same ? last.Time : -1f;
        _meleeFxSeen[s.PlayerId] = (s.MeleeStep, s.MeleeHeavy, s.MeleeTime);
        if (!(before < step.HitTime && s.MeleeTime >= step.HitTime)) return;
        // The own prediction may cross again after a correction: one effect per attack start.
        double start = _connectedFor - s.MeleeTime;
        if (step.Stomp is { } blast && !NoStompFx && !(_meleeFxStompAt.TryGetValue(s.PlayerId, out double at) && Math.Abs(at - start) < 0.25))
        {
            _meleeFxStompAt[s.PlayerId] = start;
            PlayStomp(s, own, character, wargear, blast);
        }
        if (PowerBlade.IsPowerSword(wargear) && !NoSwordFx && character is not null)
        {
            int victim = MeleePresentation.Victim(s, all.Where(p => p.PlayerId != s.PlayerId), _config!, own ? _client!.MeleeLockTarget : 0);
            if (victim > 0 && all.FirstOrDefault(p => p.PlayerId == victim) is { } struck) SwordHit(s, character, struck);
        }
    }

    // ---- stomps ----

    void PlayStomp(PlayerState s, bool own, CharacterViewer? character, string? wargear, SplashSpec blast)
    {
        var entry = _meleeFx!.Match(wargear, blast.Radius, blast.Damage.Damage);
        // The data's marker (the foot or the weapon's head), else the feet half a metre ahead; then the ground under it.
        var marker = entry?.Plays.FirstOrDefault(p => p.Marker.Length > 0)?.Marker ?? "";
        Vector3? place = character is null ? null : character.Weapons is { } rig && rig.TryMeleeMarker(marker, out var w) ? w.Origin : BodyMarkerWorld(character, marker);
        var feet = s.Position + new Num.Vector3(-MathF.Sin(s.Yaw), MathF.Cos(s.Yaw), 0f) * 0.5f;
        var probe = place is { } p ? ToGame(p) : feet;
        if (MathF.Abs(probe.Z - s.Position.Z) > 2.5f) probe = feet;   // a marker far off the body (a missing pose): the feet
        var (point, normal, surface) = GroundUnder(new Num.Vector3(probe.X, probe.Y, s.Position.Z));
        bool near = _camera is not { } cam || cam.GlobalPosition.DistanceTo(point) <= PoundDetailRange;
        var plays = entry?.Plays ?? [];
        float radius = blast.Radius;
        int started = StompFx.Play(_fx!, plays, point, normal, radius, near && !NoShards ? GroundShardsNode() : null, _catalogue?.SurfaceName(surface) ?? "", s.PlayerId * 131 + _stompFx, near);
        _stompFx++;
        _fxLastShotAt = _connectedFor; _fxLastEnd = point; _fxLastFrom = point;   // for --screenshot-on-effect
        if (_bot || CmdFlag("log-melee-fx"))
            GD.Print($"t {_connectedFor:F1} s: {(own ? "own" : $"P{s.PlayerId}'s")} stomp effects ({wargear ?? "-"}, blast {radius:F1} m / {blast.Damage.Damage:F0}; {(entry is null ? "no data entry" : string.Join(", ", entry.Plays.Select(p => $"{p.Name} x{p.Scale:0.##} at {p.Marker}")))}) at {point.X:F1},{point.Y:F1},{point.Z:F1} " +
                     $"marker {(place is null ? "none (feet)" : marker)}; {started} effects; ring, cracks {(near ? "on" : "off (far)")}; stomps shown {_stompFx}");
    }

    /// <summary>A body marker of the character's rig (<c>fx_l_foot</c> ...) in Godot world space, or null.</summary>
    Vector3? BodyMarkerWorld(CharacterViewer character, string marker)
    {
        if (marker.Length == 0 || _weaponAssets is null || character.Skeleton is not { } skeleton) return null;
        var m = _weaponAssets.Catalogue.BodyMarkers.GetValueOrDefault(marker)
                ?? _weaponAssets.Catalogue.BodyMarkers.Values.FirstOrDefault(b => b.Name.Equals(marker, StringComparison.OrdinalIgnoreCase));
        if (m is null) return null;
        int bone = skeleton.FindBone(_weaponAssets.CanonicalBone(m.Bone) ?? m.Bone);
        if (bone < 0) return null;
        return (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone) * MarineAssets.ToGodot(m.Local)).Origin;
    }

    // ---- the power sword ----

    void SwordFrame(PlayerState s, CharacterViewer? character, string? wargear)
    {
        bool sword = PowerBlade.IsPowerSword(wargear) && !NoSwordFx && s.Alive && character is not null;
        var held = sword ? character!.Weapons?.MeleeMeshInHand : null;
        // A sword that is stowed (or no longer the player's) stops glowing: the overlay goes with the hand.
        if (_swordGlowMesh.TryGetValue(s.PlayerId, out var glowing) && (held is not { } now || !ReferenceEquals(now.Mesh, glowing)))
        {
            if (GodotObject.IsInstanceValid(glowing)) glowing.MaterialOverlay = null;
            _swordGlowMesh.Remove(s.PlayerId);
        }
        if (held is not { } h) { StopSwordIdle(s.PlayerId); return; }
        _swordGlowMesh[s.PlayerId] = h.Mesh;
        // The blade's glow and flare (ours).
        var overlay = PowerBlade.OverlayFor(h.Model);
        if (!ReferenceEquals(h.Mesh.MaterialOverlay, overlay)) h.Mesh.MaterialOverlay = overlay;
        float flare = _swordFlareAt.TryGetValue(s.PlayerId, out double hitAt) ? PowerBlade.FlareAt((float)(_connectedFor - hitAt)) : 0f;
        if (overlay is not null) h.Mesh.SetInstanceShaderParameter(PowerBlade.FlareParameter, flare);
        // The data's idle effect at its marker, attached.
        if (_swordIdle.TryGetValue(s.PlayerId, out var idle) && !idle.Dead) { idle.Hidden = !character!.Visible; return; }
        var play = _meleeFx!.Deployed(wargear).FirstOrDefault();
        if (play is null || character!.Weapons is not { } rig || !rig.TryMeleeMarker(play.Marker, out var at)) return;
        int id = s.PlayerId;
        string marker = play.Marker;
        Func<(Vector3, Basis)?> follow = () =>
        {
            var c = _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
            return c?.Weapons is { } r && r.TryMeleeMarker(marker, out var t) ? (t.Origin, t.Basis) : null;
        };
        if (_fx!.Play(play.Name, at.Origin, at.Basis, play.Scale <= 0f ? 1f : play.Scale, null, follow, 0f, 0.0, repeat: true, priority: 1) is { } e)
        {
            e.StopOnLost = true;
            _swordIdle[id] = e;
            _swordIdleStarted++;
        }
    }

    void StopSwordIdle(int id)
    {
        if (!_swordIdle.Remove(id, out var e)) return;
        e.Repeat = false; e.Life = Math.Min(e.Life, Math.Max(0.0, e.Age) + 0.1);
    }

    void SwordHit(PlayerState attacker, CharacterViewer character, PlayerState victim)
    {
        if (character.Weapons is not { } rig || !rig.TryMeleeMarker(PowerBlade.GripLocator, out var grip) || !rig.TryMeleeMarker(PowerBlade.TipLocator, out var tip)) return;
        // The contact point (ours): the point of the blade nearest the victim's chest, brought to the body's surface (a body about 0.4 m in radius).
        var chest = ToGodot(victim.Position + new Num.Vector3(0f, 0f, 1.35f));
        var along = tip.Origin - grip.Origin;
        float f = along.LengthSquared() > 1e-6f ? Math.Clamp((chest - grip.Origin).Dot(along) / along.LengthSquared(), 0.2f, 1f) : 0.6f;
        var onBlade = grip.Origin + along * f;
        var outward = onBlade - chest;
        outward = outward.LengthSquared() > 1e-6f ? outward.Normalized() : (ToGodot(attacker.Position) - ToGodot(victim.Position)).Normalized();
        var contact = chest.DistanceTo(onBlade) > 0.4f ? chest + outward * 0.4f : onBlade;
        _fx!.Play(SwordHitFx, contact, AxesFromY(outward), 1f, null, null, 0f, 0.0, false, 2);
        _fx.Play(PowerBlade.FlareName, contact, AxesFromY(outward), 1f, null, null, 0f, 0.0, false, 2);
        _swordFlareAt[attacker.PlayerId] = _connectedFor;
        _swordHits++;
        _fxLastShotAt = _connectedFor; _fxLastEnd = contact; _fxLastFrom = onBlade;
        if (_bot || CmdFlag("log-melee-fx")) GD.Print($"t {_connectedFor:F1} s: P{attacker.PlayerId} power sword hit on P{victim.PlayerId} at {contact.X:F1},{contact.Y:F1},{contact.Z:F1} (sword hits shown {_swordHits})");
    }

    /// <summary>For the effects log line.</summary>
    string MeleeFxLog() => $"melee fx: stomps {_stompFx}, sword hits {_swordHits}, sword glows started {_swordIdleStarted}";
}
