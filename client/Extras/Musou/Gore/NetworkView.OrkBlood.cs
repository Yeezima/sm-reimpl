using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Fx;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode's original blood (gore-original, the owner 2026-11-07 with a recording of the original: "make the gore and blood splatters and clouds look and behave just like this
/// reference and the original game"; his choice 1: it replaces the dark trailer gore of M10 and 2026-11-01, scaled 1.5x for Musou). Added content, Musou Mode only; classic PvP never
/// reaches this file (<see cref="OrkBloodRules.Active"/>).
/// <list type="bullet">
/// <item>**Original assets loaded**: the Ork blueprint's dismemberment table and the points' effects (<see cref="OrkBlood"/>, <c>smtool ork-blood</c>): a kill plays its point's effects
/// (<c>body_dismemberment</c> and its drip for a head or a side, <c>body_explode</c> with the limbs' bursts for the whole body, the plasma, melta and lascannon variants) at the point's
/// markers, and each effect's ground decal (its "decal dropper", played on the floor under the body). A point that takes the whole body leaves no corpse.</item>
/// <item>A shot's hit on an Ork plays the shooter's weapon's own impact for the Ork's surface (<c>flesh</c>, <c>tough_body</c> / <c>tough_head</c>, the Ork blueprint's), whoever
/// shoots (<see cref="MusouHitFx"/>).</item>
/// <item>Ours: the 1.5x (<see cref="OrkBloodRules.MusouScale"/>, <c>--ork-blood-scale f</c>) on every blood effect's size and on the amount of each blood spray
/// (<see cref="FxEffect.Amount"/>); the blood mist is drawn although the no-smoke rule is on (<see cref="FxEffect.KeepSmoke"/>); the markers placed by name
/// (<see cref="OrkBloodRules.MarkerOffset"/>); the budget (<see cref="OrkBloodKillsPerFrame"/>, <see cref="OrkBloodMetres"/>, limbs only within <see cref="OrkBloodLimbMetres"/>).</item>
/// </list>
/// Switches: <c>--old-musou-gore</c> (the dark trailer gore as before), <c>--no-musou-gore</c> (no gore at all). Test aids: <c>--ork-blood-weapon wargear</c> (every kill and hit is
/// by that weapon), <c>--ork-blood-ability name</c> (a melee kill's ability), <c>--ork-blood-zone head|body|left_arm|...</c>, <c>--log-ork-blood</c>.
/// </summary>
public partial class NetworkView
{
    sealed record OrkBloodLoad(OrkBlood Data, Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals, HashSet<string> Droppers);

    /// <summary><c>--old-musou-gore</c>: Musou keeps the dark trailer gore (M10, 2026-11-01) instead of the original blood.</summary>
    static readonly bool OldMusouGore = CmdFlag("old-musou-gore");
    static readonly bool LogOrkBlood = CmdFlag("log-ork-blood");
    /// <summary><c>--old-blood-colours</c>: the blood's data colours drawn raw (lighter), as every other effect is (before / after of <see cref="FxEffect.GammaColours"/>).</summary>
    static readonly bool OldBloodColours = CmdFlag("old-blood-colours");
    /// <summary>The owner's factor (<see cref="OrkBloodRules.MusouScale"/>); <c>--ork-blood-scale f</c> tunes it (1: the data's own size and amount).</summary>
    static readonly float OrkBloodScale = CmdFloat("ork-blood-scale") is var s && float.IsFinite(s) && s > 0f ? s : OrkBloodRules.MusouScale;
    static readonly string? OrkBloodWeaponArg = CmdArg("ork-blood-weapon"), OrkBloodAbilityArg = CmdArg("ork-blood-ability"), OrkBloodZoneArg = CmdArg("ork-blood-zone");

    /// <summary>Kills that play their blood in one frame (the rest play none; ours).</summary>
    const int OrkBloodKillsPerFrame = 40;   // 40 (was 8; owner 2026-11-09: a cleave struck up to 40). Since 2026-11-14 a cleave has no cap: this stays a draw budget, kills past it play no blood
    /// <summary>Kills farther from the camera play no blood; the limbs' bursts of a whole-body point and the drips only within the nearer distance (m, ours).</summary>
    const float OrkBloodMetres = 45f, OrkBloodLimbMetres = 20f;

    /// <summary>Whether Musou's original blood draws (Musou Mode only: every caller is Musou code).</summary>
    static bool OrkBloodOn => OrkBloodRules.Active(musou: true, GoreOff, OldMusouGore);

    Task<OrkBloodLoad?>? _orkBloodLoad;
    OrkBlood? _orkBlood;
    HashSet<string> _orkBloodDroppers = new(StringComparer.Ordinal);
    bool _orkBloodFailed;
    int _orkBloodFrameKills, _orkBloodKills, _orkBloodPoints, _orkBloodPlays, _orkBloodHitFx;
    uint _orkBloodFrame;

    /// <summary>Starts (once) and finishes the background read; the data once it is in, else null.</summary>
    OrkBlood? OrkBloodData()
    {
        if (_orkBlood is not null || _orkBloodFailed || _fx is null || !_fx.IsReady) return _orkBlood;
        if (_orkBloodLoad is null)
        {
            // After the map's effects, the eye glow and the melee effects: they compile from the same library on a worker, one reader at a time (as LoadMeleeFx).
            if (_fxMapLoad is not null || _fxMapSpawned != _client?.MapName || _eyeLoad is { IsCompleted: false } || _meleeFxLoad is { IsCompleted: false }) return null;
            string dir = _gameDir;
            var library = _fx.Library;
            _orkBloodLoad = Task.Run(() =>
            {
                try
                {
                    var notes = new List<string>();
                    var data = OrkBlood.Load(dir, notes);
                    var failures = new List<string>();
                    var systems = library is null ? new Dictionary<string, CSystem>() : CompileSystems(library, data.EffectNames, failures);
                    foreach (var f in failures) notes.Add($"ork blood: effect {f} not compiled");
                    var droppers = systems.Where(kv => kv.Value.Roots.Any(r => r.AllNodes().Any(n => n is CSpray { Collides: true }))).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
                    var decals = library is null ? new Dictionary<string, byte[]>() : library.Textures(FxPlayer.DecalNames(systems.Values, droppers));
                    foreach (var n in notes) GD.Print(n);
                    return new OrkBloodLoad(data, systems, decals, droppers);
                }
                catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                { GD.PrintErr($"ork blood: install unreadable: {ex.Message}; Musou kills play no blood"); return (OrkBloodLoad?)null; }
            });
            return null;
        }
        if (!_orkBloodLoad.IsCompleted) return null;
        if (_orkBloodLoad.IsFaulted || _orkBloodLoad.Result is not { } loaded)
        {
            if (_orkBloodLoad.IsFaulted) GD.PrintErr($"ork blood: loading failed ({_orkBloodLoad.Exception?.GetBaseException().Message}); Musou kills play no blood");
            _orkBloodFailed = true;
            return null;
        }
        _fx.Preload(loaded.Systems, loaded.Decals, loaded.Droppers);
        _orkBloodDroppers = loaded.Droppers;
        _orkBlood = loaded.Data;
        GD.Print($"ork blood: {loaded.Data.Types.Count} dismemberment types from {loaded.Data.Source}, {loaded.Data.Points.Count} points, {loaded.Systems.Count} effects " +
                 $"({loaded.Droppers.Count} with a ground decal), {loaded.Data.Weapons.Count} weapons; scale {OrkBloodScale:0.##}");
        return _orkBlood;
    }

    /// <summary>The state of a killer or a shooter (the own player's prediction, another's last snapshot), or null for a retainer, fire support or the scripted feed's killers.</summary>
    PlayerState? MusouPlayerState(ushort id)
    {
        if (id == 0 || _client is null) return null;
        if (id == _client.PlayerId) return _client.Predicted;
        foreach (var o in _lastOthers) if (o.PlayerId == id) return o;
        return null;
    }

    /// <summary>The ranged wargear a hit or a kill is by: <c>--ork-blood-weapon</c>, else the player's gun in hand, else (a retainer, the scripted feed) the bolter (ours).</summary>
    string OrkBloodGun(PlayerState? who)
    {
        if (OrkBloodWeaponArg is { Length: > 0 } forced) return forced;
        if (who is { } s && _config is not null && Loadouts.InHand(_config, s) is { Delivery: not DeliveryKind.Cone } gun) return gun.Name;
        return "sm_mp_ranged_bolter";
    }

    /// <summary>The dismemberment type of a Musou death (<see cref="OrkBloodRules.TypeOf"/>), from the killer's weapons and its strike.</summary>
    string OrkBloodType(OrkBlood data, in MusouDeathShown d)
    {
        var who = MusouPlayerState(d.Killer);
        string? melee = OrkBloodWeaponArg is { Length: > 0 } forced ? forced : who is { } s ? MeleeWargear(s) : null;
        melee ??= "sm_mp_melee_chainsword";   // a retainer or the scripted feed (ours)
        string ability = OrkBloodAbilityArg ?? (who is { } w && MpClasses.MeleeMapFor(melee) is { } map ? (w.MeleeStep <= 0 ? map.AbilityOf(false, 1) : map.AbilityOf(w.MeleeHeavy, w.MeleeStep)) : "");
        bool charging = OrkBloodAbilityArg is null && who is { Rush: not RushPhase.None };
        string gun = OrkBloodGun(who);
        if (d.Cause == MusouDeathKind.Shot && MusouKillerHasLascannon(d.Killer) && OrkBloodWeaponArg is null) gun = "sm_mp_exotic_las_cannon";
        return OrkBloodRules.TypeOf(d.Cause, gun, data.Weapon(gun), data.Weapon(melee), ability, charging);
    }

    /// <summary>The marker of the enemy's killing hit in this batch (2 a head shot), or 0.</summary>
    byte OrkBloodLastHit(ushort enemy)
    {
        byte found = 0;
        var events = _musouState.NewEvents;
        for (int i = 0; i < events.Count; i++) if (events[i].Kind == MusouEventKind.Hit && events[i].Enemy == enemy) found = events[i].Value;
        return found;
    }

    /// <summary>
    /// A Musou death with the original blood on: plays its point (within the budget) and says whether the body goes whole (then no corpse is drawn). False when no point plays.
    /// </summary>
    bool OrkBloodDeath(in MusouDeathShown d, Vector3 camera) => OrkBloodDeath(d, camera, out _);

    /// <summary>As above; <paramref name="played"/>: whether the point was played (the corpse floor, 2026-11-09: a whole-body point too far or over the frame's budget plays nothing, so the body stays).</summary>
    bool OrkBloodDeath(in MusouDeathShown d, Vector3 camera, out bool played)
    {
        played = false;
        if (OrkBloodData() is not { } data || _fx is null || _gameSpace is null) return false;
        if (d.Type == MusouEnemyType.ChaosMarine) return false;   // no Ork: the Ork's table and blood are not his (the marine's death binds none, docs "Death effects")
        if (Engine.GetProcessFrames() is var frame && (uint)frame != _orkBloodFrame) { _orkBloodFrame = (uint)frame; _orkBloodFrameKills = 0; }
        string type = OrkBloodType(data, d);
        uint seed = (uint)(d.Id * 2654435761u) ^ (d.Tick * 40503u);
        float zoneRoll = (seed & 0xFFFF) / 65536f, chanceRoll = ((seed >> 16) & 0xFFFF) / 65536f;
        var kill = OrkBloodRules.Kill(data, type, d.Cause, OrkBloodLastHit(d.Id) == 2, zoneRoll, chanceRoll);
        if (OrkBloodZoneArg is { } z && OrkBlood.Zones.ToList().IndexOf(z) is var zi and >= 0)
        {
            var row = data.Types.GetValueOrDefault(type);
            string point = row is null ? "" : OrkBloodRules.TakesWholeBody(d.Cause) ? row.NothingLeft : row.Zones[zi].Point;
            kill = new OrkBloodKill(type, (OrkBloodZoneKind)zi, point, OrkBloodRules.IsWholeBody(point));
        }
        _orkBloodKills++;
        if (LogOrkBlood) GD.Print($"ORK BLOOD t {_connectedFor:F2} enemy {d.Id} cause {d.Cause} killer {d.Killer} type '{kill.Type}' zone {kill.Zone} point '{kill.Point}' whole {kill.WholeBody}");
        if (kill.Point.Length == 0) return false;
        float metres = new Vector3(d.Position.X, d.Position.Y, d.Position.Z).DistanceTo(camera);
        var source = MusouGoreSourceOf(d.Killer);
        if (metres > OrkBloodMetres) { _musouGoreKillsFar[(int)source]++; return kill.WholeBody; }
        if (!_musouGore.TakeKill(source)) return kill.WholeBody;   // musou-net-budget: the own kills first, then the allies', then the rest (8 a frame shared before)
        int priority = MusouGoreFxPriority(source);
        _orkBloodFrameKills++;
        _orkBloodPoints++;
        played = true;
        float size = GoreChestHeight(d.Type) / 1.1f;
        var chest = new Num.Vector3(d.Position.X, d.Position.Y, d.Position.Z + GoreChestHeight(d.Type));
        // The hit's way: the death's push, else from the killer, else from the camera (game space).
        var push = new Num.Vector3(d.Push.X, d.Push.Y, 0f);
        if (push.LengthSquared() < 1e-6f && MusouPlayerState(d.Killer) is { } k) push = new Num.Vector3(chest.X - k.Position.X, chest.Y - k.Position.Y, 0f);
        if (push.LengthSquared() < 1e-6f) push = new Num.Vector3(chest.X - camera.X, chest.Y - camera.Y, 0f);
        push = Num.Vector3.Normalize(push);
        // The body's frame: forward against the push (it faced its killer), right across it, up.
        var forward = -push;
        var right = new Num.Vector3(forward.Y, -forward.X, 0f);
        var basis = _gameSpace.GlobalTransform.Basis;
        Vector3 Dir(Num.Vector3 v) => (basis * new Vector3(v.X, v.Y, v.Z)).Normalized();
        foreach (var p in data.PointEffects(kill.Point))
        {
            bool limb = p.Name.EndsWith("_limb", StringComparison.Ordinal) || p.Name.EndsWith("_drip", StringComparison.Ordinal);
            if (limb && metres > OrkBloodLimbMetres) continue;
            var off = OrkBloodRules.MarkerOffset(p.Marker) * size;
            var at = chest + right * off.X + forward * off.Y + new Num.Vector3(0f, 0f, off.Z);
            var axis = p.Direction switch { "negative_hit_direction" => Dir(-push), "hit_direction" => Dir(push), _ => Dir(Num.Vector3.UnitZ) };
            OrkBloodPlay(p.Name, ToGodot(at), axis, (p.Scale <= 0f ? 1f : p.Scale) * size, at, priority);
        }
        return kill.WholeBody;
    }

    /// <summary>Plays one blood effect at <paramref name="where"/> (Godot space) along <paramref name="axis"/>, scaled and thickened by the Musou factor; its ground decal on the floor under <paramref name="game"/>.</summary>
    void OrkBloodPlay(string name, Vector3 where, Vector3 axis, float scale, Num.Vector3 game, int priority = 0)
    {
        if (_fx is null) return;
        if (_fx.Play(name, where, AxesFromY(axis), scale * OrkBloodScale, priority: priority) is { } e) { e.Amount = OrkBloodScale; e.KeepSmoke = true; e.GammaColours = !OldBloodColours; _orkBloodPlays++; MusouGorePlayed(priority); }
        // The effect's dropper on the floor (the M41 rule: a dropper lands where it starts, so its copy is placed on the ground): the data's blood decal.
        if (!_orkBloodDroppers.Contains(name) || GoreGround(game) is not { } floor) return;
        var ground = ToGodot(new Num.Vector3(game.X, game.Y, floor + 0.02f));
        if (_fx.Play(name, ground, AxesFromY(Vector3.Up), scale * OrkBloodScale, priority: priority) is { } g) { g.Collisions = true; g.GroundOnly = true; g.Amount = OrkBloodScale; g.KeepSmoke = true; g.GammaColours = !OldBloodColours; }
    }

    /// <summary>
    /// A shot's hit on a Musou enemy with the original blood on: the shooter's weapon's own impact for the Ork's surface (<c>flesh</c>; <c>tough_head</c> for a head shot, else
    /// <c>tough_body</c>), played at the hit along the shot (Godot space). The Chaos Marine of Musou keeps the marine's surface (<c>broken_ceramite</c>).
    /// </summary>
    void OrkBloodHit(PlayerState? shooter, bool head, bool chaosMarine, Vector3 end, Vector3 dir, Num.Vector3 game, int priority = 0)
    {
        if (_catalogue is null || _fx is null || OrkBloodData() is null) return;
        string gun = OrkBloodGun(shooter);
        var plays = chaosMarine ? _catalogue.Impact(gun, head ? ShotHit.Head : ShotHit.Body, 0, armourUp: false)
            : _catalogue.Container(_catalogue.Weapon(gun)?.HitMaterial)?.Resolve(OrkFleshSurface, head ? "tough_head" : "tough_body") ?? [];
        _orkBloodHitFx++;
        if (LogOrkBlood) GD.Print($"ORK BLOOD HIT t {_connectedFor:F2} {gun} {(head ? "head" : "body")}: {string.Join(", ", plays.Select(p => $"{p.Name} x{p.Scale:0.##} {p.Direction} {p.TriggerPoint}"))}");
        for (int k = 0; k < plays.Count; k++)
        {
            var p = plays[k];
            var axis = p.Direction is "reflect_hit_direction" or "negative_hit_direction" or "hitpoint_normal" ? -dir : p.TriggerPoint == "exit_wound" ? dir : -dir;
            var pos = p.TriggerPoint == "exit_wound" ? end + dir * 0.35f : end - dir * 0.05f;
            OrkBloodPlay(p.Name, pos, axis, p.Scale <= 0f ? 1f : p.Scale, game, priority);
        }
    }

    /// <summary>The Ork blueprint's surface (<c>entity_ext/material_audio_visual</c> of <c>ork_coop_boy</c> and its zones: "flesh").</summary>
    const string OrkFleshSurface = "flesh";

    void OrkBloodLog()
    {
        if (!LogOrkBlood || _connectedFor < _orkBloodLogAt) return;
        _orkBloodLogAt = _connectedFor + 5.0;
        GD.Print($"ork blood: t {_connectedFor:F0} s: {_orkBloodKills} kills, {_orkBloodPoints} points played, {_orkBloodPlays} effects, {_orkBloodHitFx} shot hits; live effects {_fx?.Live}, decals {_fx?.LiveDecals}");
    }
    double _orkBloodLogAt;
}
