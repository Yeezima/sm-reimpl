using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou combat feel (owner's brief of 2026-11-09; plan, pair and settings approved that day; Musou Mode only, client presentation only). The own contact is shown at the predicted
/// contact, not a server round trip later (phase 1 measured 181 to 209 ms on 127.0.0.1): <see cref="MusouContactRules"/>.
/// <list type="bullet">
/// <item>Melee (the power sword, the owner's pilot): on the frame the own strike's hit time passes, every Ork the client sees in the cleave's reach gets the power sword's slash, the
/// lead one the power flash, the swing its small capped shake, the next two their secondary hit sounds (the lead's hit sound was already played by <c>OwnStrikeHitSound</c>).
/// The server's report of the same Ork then adds no second slash, flash, shake or sound; its blood, deaths and markers are the server's as before.</item>
/// <item>Shots (the bolt pistol, the owner's pilot): the drawn shot stops at the first Ork on its ray (the server's body shape), and the Ork's flesh impact and the gun's hit
/// sound play on the shot's frame; the server's report of that hit adds no second impact or sound. Own gun hit sounds of any gun are one a shot and at most
/// <see cref="MusouContactRules.GunSoundsPerSecond"/> a second.</item>
/// <item>Camera shake: Musou's shakes do not stack (<see cref="MusouContactRules.CombineShake"/>).</item>
/// <item>Hit Orks of the rank and file are pushed back a little, away from the blow, keeping their clip, facing and run (<see cref="MusouCrowd.Recoil"/>); the old flinch
/// (<c>--musou-ork-flinch</c>, a stagger clip pushed along the Ork's own facing, read as a 180-degree turn) stays off.</item>
/// </list>
/// Nothing about a kill is predicted: no kill marker, death or gore before the server's report. Switches: <c>--musou-old-contact</c> (all of it off: the 2026-11-08 behaviour),
/// <c>--no-musou-recoil</c>. Test aids: <c>--log-musou-contact</c> (a MUSOU CONTACT line each 5 s: predicted, matched, unconfirmed, the report's lag after the contact), <c>--feel-probe</c>.
/// </summary>
public partial class NetworkView
{
    static readonly bool MusouOldContact = CmdFlag("musou-old-contact");
    static readonly bool NoMusouRecoil = CmdFlag("no-musou-recoil");
    static readonly bool LogMusouContact = CmdFlag("log-musou-contact");
    readonly MusouContactLedger _musouContact = new();
    readonly List<(ushort Id, MusouEnemyType Type, Num.Vector3 Chest, float Distance)> _musouStrikeTargets = new();
    readonly Dictionary<ushort, double> _musouContactAt = new();   // when the last contact on an enemy was predicted (the log's lag)
    double _musouContactLagSum, _musouContactLagMax, _musouContactLogAt;
    int _musouContactLagN, _musouGunSounds, _musouGunSoundsSkipped;
    (ushort Id, bool Head, MusouEnemyType Type, ulong Frame)? _musouShotEnemy;
    float _musouGunSoundTokens = MusouContactRules.GunSoundBurst;
    uint _musouGunSoundTick;
    readonly HashSet<string> _musouShotNames = new();

    bool MusouContactOn => MusouActive && !MusouOldContact && _config is not null;

    // ---------------------------------------------------------------- melee

    /// <summary>The own strike's hit time has passed (prediction): the contact on every Ork the client sees in the cleave's reach (the power sword only, for now).</summary>
    void MusouOwnStrikeContact(PlayerState me, MeleeStep step)
    {
        if (!MusouContactOn || _client is null || _camera is null || _gameSpace is null) return;
        string? wargear = MeleeWargear(me);
        MusouOwnSwingArc(me, step, wargear);   // owner 2026-11-10: the swing's one arc, every melee weapon (NetworkView.MusouSlashArc.cs)
        if (!MusouContactRules.PredictsMelee(wargear)) { if (LogMusouContact) GD.Print($"MUSOU CONTACT t {_connectedFor:F3} strike {wargear ?? "-"}: not a pilot weapon"); return; }
        MusouStrikeTargets(me, step, wargear);   // the arc's pick of this strike when it made one (the same frame and strike: not counted twice)
        if (_musouStrikeTargets.Count == 0) { if (LogMusouContact) GD.Print($"MUSOU CONTACT t {_connectedFor:F3} strike {wargear}: no Ork in reach of {_tfwShown.Count} shown"); return; }
        var camera = ToGame(_camera.GlobalPosition);
        var style = MusouContactRules.SlashOf(wargear);
        var colour = MusouSlashRules.ColourOf(wargear);
        for (int k = 0; k < _musouStrikeTargets.Count; k++)
        {
            var (id, type, chest, _) = _musouStrikeTargets[k];
            _musouContact.Predict(id, _connectedFor, melee: true);
            _musouContactAt[id] = _connectedFor;
            MusouMeleeShake();   // the swing's cap holds however many
            if (!MusouArcsOn && !NoMusouSlash && Num.Vector3.Distance(chest, camera) <= MusouSlashRules.Metres) MusouSlashAt(chest, me.Position, colour, style, _client.PlayerId);   // the arc in place (owner 2026-11-10)
            var contact = ToGodot(chest);
            if (k == 0)
            {
                var toward = ToGodot(me.Position) - contact; toward.Y = 0f;
                PowerHit(_client.PlayerId, wargear, contact, (toward.LengthSquared() > 1e-6f ? toward.Normalized() : Vector3.Up) + Vector3.Up * 0.3f, "musou predicted contact");
            }
            else if (k <= 2 && !NoMeleeSounds && !NoSound && Num.Vector3.Distance(chest, camera) <= FxRange)
            {
                if (ProceduralHitSound(me, charge: false, dash: false) is { } synth) PlayProceduralHit(synth, contact, "musou predicted contact");
                else PostDataMeleeHit(me, me.MeleeHeavy, me.MeleeStep, secondary: true, "a Musou enemy", contact, "musou predicted contact", MusouFleshRule.Material(type));
                CountSound("melee hit");
            }
        }
        if (LogMusouContact) GD.Print($"MUSOU CONTACT t {_connectedFor:F3} strike {wargear} step {me.MeleeStep}{(me.MeleeHeavy ? " heavy" : "")}: {_musouStrikeTargets.Count} Orks in reach, nearest {_musouStrikeTargets[0].Distance:F2} m");
    }

    /// <summary>The last pick's frame and strike (<see cref="MusouStrikeTargets"/>): the swing's arc, its contact and its punch ask on the same frame and share one pick.</summary>
    (ulong Frame, int Player, int Step, bool Heavy, float Yaw, Num.Vector3 Feet, string? Wargear)? _musouStrikeTargetsOf;

    /// <summary>
    /// The Orks the client sees in the own strike's reach, nearest first, as the server's cleave picks them (<c>World.MusouCleave</c>: <see cref="MusouCleaveShape"/>'s reach, cone and count, the
    /// spear's sweep among them; the core's height band, no wall between). Asked again on the same frame for the same strike, the list is kept as it is.
    /// </summary>
    void MusouStrikeTargets(PlayerState me, MeleeStep step, string? wargear)
    {
        var of = (Engine.GetProcessFrames(), me.PlayerId, me.MeleeStep, me.MeleeHeavy, me.Yaw, me.Position, wargear);
        if (_musouStrikeTargetsOf == of) return;
        _musouStrikeTargetsOf = of;
        _musouStrikeTargets.Clear();
        var config = _config!;
        var shape = MusouCleaveShape.Of(config, step, _tfwState.War is not null, wargear);   // the server's volume: x1.5 x1.3 and 120 degrees each side in the war, the spear's 15 m and 150
        float reach = shape.Reach, minCos = shape.MinCos;
        int most = Math.Min(MusouContactRules.MaxTargets, shape.Most);   // and its count: 40 in the war, 8 in the skirmish, 12 for the spear
        var forward = new Num.Vector2(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw));
        var eye = me.Position + new Num.Vector3(0f, 0f, config.EyeHeight);
        for (int i = 0; i < _tfwShown.Count; i++)
        {
            var e = _tfwShown[i];
            if (e.State == MusouEnemyState.Dying || e.Type == MusouEnemyType.Weirdboy) continue;   // review: the Weirdboy's shield may take the blow (its pulse, no flesh): his are the server's
            var offset = e.Position - me.Position;
            float distance = new Num.Vector2(offset.X, offset.Y).Length();
            if (distance > reach || MathF.Abs(offset.Z) > config.MeleeVerticalReach) continue;
            if (distance > 1e-4f && Num.Vector2.Dot(new Num.Vector2(offset.X, offset.Y) / distance, forward) < minCos) continue;
            var chest = new Num.Vector3(e.Position.X, e.Position.Y, e.Position.Z + GoreChestHeight(e.Type));
            if (_client?.Collision is { } world)
            {
                float length = Num.Vector3.Distance(eye, chest);
                if (length > 1e-4f && world.Weapons.Raycast(eye, (chest - eye) / length, length) is { } wall && wall.Distance < length) continue;
            }
            _musouStrikeTargets.Add((e.Id, e.Type, chest, distance));
        }
        _musouStrikeTargets.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        if (_musouStrikeTargets.Count > most) _musouStrikeTargets.RemoveRange(most, _musouStrikeTargets.Count - most);
    }

    // ---------------------------------------------------------------- shots

    /// <summary>The own shot's ray against the Orks the client sees (the bolt pistol only, for now): <paramref name="distance"/> shortened to the first one met.</summary>
    void MusouOwnShotEnd(WeaponSpec weapon, Num.Vector3 start, Num.Vector3 dir, ref float distance, ref bool enemy)
    {
        _musouShotEnemy = null;
        if (LogMusouContact && _musouShotNames.Add(weapon.Name)) GD.Print($"MUSOU CONTACT t {_connectedFor:F3} first own shot of {weapon.Name}: {(MusouContactRules.PredictsGun(weapon.Name) ? "a pilot gun" : "not a pilot gun")}, {_tfwShown.Count} Orks shown");
        if (!MusouContactOn || !MusouContactRules.PredictsGun(weapon.Name)) return;
        var rules = _config!.MusouRules ?? MusouRules.Default;
        float best = distance;
        for (int i = 0; i < _tfwShown.Count; i++)
        {
            var e = _tfwShown[i];
            if (e.State == MusouEnemyState.Dying) continue;
            var (radius, height) = MusouSim.ShotBodyOf(rules, e.Type);
            if (MusouContactRules.RayBody(start, dir, e.Position, radius, height, best, out bool head) is { } t && t < best)
            {
                best = t;
                // Review: the Weirdboy's shield may take the shot (the server shows its pulse), so a shot that meets him first predicts nothing, behind him either.
                _musouShotEnemy = e.Type == MusouEnemyType.Weirdboy ? null : (e.Id, head, e.Type, Engine.GetProcessFrames());
            }
        }
        if (_musouShotEnemy is null) return;
        distance = best;
        enemy = true;
    }

    /// <summary>The own shot just drawn met an Ork (<see cref="MusouOwnShotEnd"/>): its flesh impact and the gun's hit sound now, and the contact noted for the server's report.</summary>
    void MusouOwnShotContact(PlayerState me, WeaponSpec weapon, Num.Vector3 start, Num.Vector3 dir, Num.Vector3 end)
    {
        if (_musouShotEnemy is not { } hit || hit.Frame != Engine.GetProcessFrames()) return;
        _musouShotEnemy = null;
        _musouContact.Predict(hit.Id, _connectedFor, melee: false);
        _musouContactAt[hit.Id] = _connectedFor;
        if (LogMusouContact) GD.Print($"MUSOU CONTACT t {_connectedFor:F3} shot {weapon.Name}: Ork {hit.Id} {hit.Type} {(hit.Head ? "head" : "body")} at {Num.Vector3.Distance(start, end):F1} m");
        bool? material = MusouFleshRule.Material(hit.Type);
        if (!NoSound && _soundTriggers?.HitSound(weapon.Name) is { } gunHit && MusouGunSoundTake(0u))
        {
            var emitter = new SoundEmitter { Position = ToGodot(end), Label = "musou predicted shot hit", OneShot = true };
            PostPlayerHitSound(gunHit, emitter, ImpactSounds.ImpactOffset(ImpactTreatmentArg, gunHit, hitScan: true), material);
            CountSound("body hit");
            MusouStalkerConfirm(weapon.Name, material == true);   // sound/stalker-hit-confirm: the stalker's confirm on the shooter
            MusouBolterFirstHit(weapon.Name, emitter.Position);   // musou/audio-pass (bolter rifles only: the pilot gun is the pistol)
        }
        if (_camera is null) return;
        _musouGoreHits[(int)MusouGoreSource.Own]++;
        float reach = MusouOldGoreBudget ? FxRange : MusouGorePriority.HitMetres(MusouGoreSource.Own);   // musou-net-budget: the own budget and reach (the shared bucket with --musou-old-gore-budget)
        if (Num.Vector3.DistanceSquared(end, ToGame(_camera.GlobalPosition)) > reach * reach) { _musouGoreHitsFar[(int)MusouGoreSource.Own]++; return; }
        if (!_musouGore.TakeHit(MusouGoreSource.Own)) return;
        _musouHitFx++;
        var endG = ToGodot(end);
        var dirG = DirToGodot(dir).Normalized();
        if (GoreOff) BloodSplashAt(endG, dirG, 0.0);
        if (OrkBloodOn) { OrkBloodHit(me, hit.Head, hit.Type == MusouEnemyType.ChaosMarine, endG, dirG, end, MusouGoreFxPriority(MusouGoreSource.Own)); return; }
        if (_fx is null || _catalogue is null) return;
        var plays = _catalogue.Impact(weapon.Name, hit.Head ? ShotHit.Head : ShotHit.Body, 0, armourUp: false);
        for (int k = 0; k < plays.Count; k++)
            if (plays[k] is var p) _fx.Play(p.Name, endG - dirG * 0.05f, AxesFromY(-dirG), p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, 0.0, false, 0);
    }

    /// <summary>One own gun hit sound a shot (<paramref name="tick"/>: the report's tick; 0 for a predicted shot, which is always its shot's first) within the budget.</summary>
    bool MusouGunSoundTake(uint tick)
    {
        if (tick != 0u && tick == _musouGunSoundTick) { _musouGunSoundsSkipped++; return false; }
        if (_musouGunSoundTokens < 1f) { _musouGunSoundsSkipped++; return false; }
        if (tick != 0u) _musouGunSoundTick = tick;
        _musouGunSoundTokens -= 1f;
        _musouGunSounds++;
        return true;
    }

    // ---------------------------------------------------------------- the server's reports

    /// <summary>
    /// The server's report of an own hit (<see cref="MusouHitFx"/>): true when its contact was already shown at the prediction (so no second slash, flash, shake, impact or
    /// sound); the log's lag from the contact to the report.
    /// </summary>
    bool MusouContactShown(in MusouEvent e)
    {
        if (!MusouContactOn || !_musouContact.Confirm(e.Enemy, _connectedFor, melee: e.Value == 3)) return false;
        if (_musouContactAt.Remove(e.Enemy, out var at))
        {
            double lag = (_connectedFor - at) * 1000.0;
            _musouContactLagSum += lag; _musouContactLagN++;
            _musouContactLagMax = Math.Max(_musouContactLagMax, lag);
        }
        return true;
    }

    /// <summary>Each frame (Musou): the gun sound budget refills, the old contacts expire, the log.</summary>
    void MusouContactFrame(float delta)
    {
        _musouGunSoundTokens = MathF.Min(MusouContactRules.GunSoundBurst, _musouGunSoundTokens + MusouContactRules.GunSoundsPerSecond * delta);
        _musouContact.Expire(_connectedFor);
        FeelProbeFrame();   // test aid --feel-probe (Tfw/NetworkView.FeelProbe.cs)
        if (_musouContactAt.Count > 256) _musouContactAt.Clear();
        if (!LogMusouContact || _connectedFor < _musouContactLogAt) return;
        _musouContactLogAt = _connectedFor + 5.0;
        GD.Print($"MUSOU CONTACT t {_connectedFor:F0} s: predicted {_musouContact.Predicted}, matched by the server {_musouContact.Matched}, not confirmed {_musouContact.Unconfirmed}, waiting {_musouContact.Pending}; " +
                 $"report after the contact {(_musouContactLagN > 0 ? _musouContactLagSum / _musouContactLagN : 0):F0} ms mean, {_musouContactLagMax:F0} max; gun hit sounds {_musouGunSounds}, held back {_musouGunSoundsSkipped}");
    }

    /// <summary>The hit Ork of the rank and file pushed back, away from the attacker (drawn only).</summary>
    void MusouRecoil(in MusouEvent e, IReadOnlyList<MusouShown> shown, PlayerState me)
    {
        if (NoMusouRecoil || MusouOldContact || _musouCrowd is null || e.Player == 0) return;
        int i = MusouFind(shown, e.Enemy);
        if (i < 0 || !MusouContactRules.Recoils(shown[i].Type) || shown[i].State is MusouEnemyState.Dying or MusouEnemyState.Knocked) return;   // review: a knocked or dying Ork plays its own fall
        PlayerState? attacker = e.Player == _client?.PlayerId ? me : MusouPlayerState(e.Player);
        if (attacker is not { } who) return;
        var away = shown[i].Position - who.Position;
        away.Z = 0f;
        if (away.LengthSquared() < 1e-6f) return;
        away = Num.Vector3.Normalize(away);
        _musouCrowd.Recoil(e.Enemy, new Vector3(away.X, away.Y, 0f), e.Value == 3 ? MusouMeleePush(who) : MusouContactRules.RecoilShot);   // MW4: by the weapon (Weight/NetworkView.MusouPunch.cs)
    }

    /// <summary>Musou's camera shakes: the strongest counts, the rest add a share, capped (none of it with <c>--musou-old-contact</c>).</summary>
    void MusouShakeTotal(float current, float amount, ref float total)
    {
        if (!MusouContactOn) return;
        total = MusouContactRules.CombineShake(current, amount);
    }
}
