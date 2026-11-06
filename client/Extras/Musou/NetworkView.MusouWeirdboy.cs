using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmSim.Extras.Musou;
using SmNet.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Weirdboy's effects (Musou Mode only; <see cref="MusouWeirdboy"/>), the original ones his files name, played by name from <c>master_fx</c> (*original asset loaded*): the shield
/// (<c>weirdboy_shield</c>, on him while it is up), a hit it takes (<c>weirdboy_shield_pulse</c>), its break and return (<c>weirdboy_shield_break</c>, <c>weirdboy_shield_restore</c>),
/// the bolt (<c>weirdboy_staff_muzzle</c>, the frazzle's <c>weirdboy_staff_trail</c> flying to where it bursts, the staff's hit container there), the teleport
/// (<c>weirdboy_teleport_away_instant</c>, <c>weirdboy_teleport_in</c>), the summons (<c>weirdboy_summon_staff</c>) and his death's green burst (the charged plasma cannon's explosion, tinted). Where on him each plays (his chest at 1.1 m, the staff's top at
/// 1.6 m) is **ours**: the data's markers (<c>rig_spine1</c>, <c>fx_cntr</c>, <c>fx_staff_bottom</c>) are not looked up. The shield's three strengths (strong, medium, weak by its share)
/// are not shown: the client is not told the shield's points.
/// </summary>
public partial class NetworkView
{
    const string WeirdboyBoltHits = @"material_effect_container\weapon_hits\orks\ranged_weapons\weirdboy_staff";
    const float WeirdboyChest = 1.1f, WeirdboyStaff = 1.6f;
    /// <summary>His death's burst: the charged plasma cannon's own explosion (<c>plasma_gun_mp_overcharged_dmg</c>, which the data plays at 1.5 for its 15 m blast), at 2 for his 20 m, recoloured green (ours).</summary>
    const string WeirdboyBurstEffect = "plasma_gun_mp_overcharged_dmg";
    const float WeirdboyBurstScale = 2f;
    static readonly Color WeirdboyGreen = new(0.35f, 1f, 0.3f);
    readonly Dictionary<ushort, FxEffect> _weirdboyShields = new();
    readonly HashSet<ushort> _weirdboySeen = new();
    readonly List<ushort> _weirdboyGone = new();
    readonly Dictionary<ushort, Num.Vector3> _weirdboyAt = new();

    /// <summary>A cue of his (<see cref="MusouEventKind.Telegraph"/> with a value of <see cref="MusouWeirdboy"/>'s): its effects. False for any other value.</summary>
    bool MusouWeirdboyCue(in MusouEvent e, IReadOnlyList<MusouShown> shown)
    {
        if (e.Value < MusouWeirdboy.BoltCue || e.Value > MusouWeirdboy.BurstCue) return false;
        if (_fx is null || !FxActive) return true;
        int i = MusouFind(shown, e.Enemy);
        var feet = i >= 0 ? shown[i].Position : _weirdboyAt.TryGetValue(e.Enemy, out var last) ? last : e.Vector;
        var up = Basis.Identity;
        switch (e.Value)
        {
            case MusouWeirdboy.BoltCue:
            {
                var from = feet + new Num.Vector3(0f, 0f, WeirdboyStaff);
                var to = e.Vector + new Num.Vector3(0f, 0f, 0.3f);
                float flight = Num.Vector3.Distance(from, to) / MathF.Max(1f, (_config?.MusouRules ?? MusouRules.Default).Of(MusouEnemyType.Weirdboy).RangedBoltSpeed);
                var a = ToGodot(from); var b = ToGodot(to);
                var along = (b - a).LengthSquared() > 1e-6f ? (b - a).Normalized() : Vector3.Up;
                var axes = AxesAlong(along);
                _fx.Play("weirdboy_staff_muzzle", a, axes, 1f);
                ulong start = Time.GetTicksMsec();
                var bolt = _fx.Play("weirdboy_staff_trail", a, axes, 1f, follow: () =>
                {
                    float t = Math.Clamp((Time.GetTicksMsec() - start) / 1000f / MathF.Max(0.01f, flight), 0f, 1f);
                    return t >= 1f ? null : (a.Lerp(b, t), axes);
                }, lifetime: flight + 0.5f);
                if (bolt is not null) bolt.StopOnLost = true;
                if (_catalogue?.Resolve(new FxMaterialTrigger(WeirdboyBoltHits, "hit_material"), SmSim.ShotHit.World, 0) is { Count: > 0 } hits)
                    foreach (var p in hits) _fx.Play(p.Name, b, up, p.Scale <= 0f ? 1f : p.Scale, delay: flight);
                break;
            }
            case MusouWeirdboy.ShieldBreakCue:
                _fx.Play("weirdboy_shield_break", ToGodot(feet + new Num.Vector3(0f, 0f, WeirdboyChest)), up, 1f);
                break;
            case MusouWeirdboy.ShieldBackCue:
                _fx.Play("weirdboy_shield_restore", ToGodot(feet + new Num.Vector3(0f, 0f, WeirdboyChest)), up, 1f);
                break;
            case MusouWeirdboy.TeleportCue:
                _fx.Play("weirdboy_teleport_away_instant", ToGodot(feet), up, 1f);
                _fx.Play("weirdboy_teleport_in", ToGodot(e.Vector), up, 1f, delay: MusouWeirdboy.VanishSeconds);
                break;
            case MusouWeirdboy.SummonCue:
                _fx.Play("weirdboy_summon_staff", ToGodot(feet), up, 0.5f);
                break;
            case MusouWeirdboy.BurstCue:   // his death's burst: the charged plasma cannon's explosion, green, sized to its 20 m (the owner, 2026-11-07)
                var at = ToGodot(e.Vector + new Num.Vector3(0f, 0f, WeirdboyChest));
                _fx.Play(WeirdboyBurstEffect, at, up, WeirdboyBurstScale, priority: 2, tint: WeirdboyGreen);
                _fx.Play("weirdboy_shield_break", at, up, 2f, priority: 2);
                break;
        }
        return true;
    }

    /// <summary>A hit his shield took (marker <see cref="MusouWeirdboy.ShieldMarker"/>): the pulse on him, no blood. False for any other hit.</summary>
    bool MusouWeirdboyShieldHit(in MusouEvent e, IReadOnlyList<MusouShown> shown)
    {
        if (e.Value != MusouWeirdboy.ShieldMarker) return false;
        if (_fx is null || !FxActive) return true;
        int i = MusouFind(shown, e.Enemy);
        var feet = i >= 0 ? shown[i].Position : e.Vector;
        _fx.Play("weirdboy_shield_pulse", ToGodot(feet + new Num.Vector3(0f, 0f, WeirdboyChest)), Basis.Identity, 1f);
        return true;
    }

    /// <summary>
    /// Each frame: the shield on every Weirdboy shown whose shield is up (any state but stunned, fading or dying), off the others; a Weirdboy who has just appeared (spawning) gets the
    /// teleport-in once.
    /// </summary>
    void MusouWeirdboyFrame(IReadOnlyList<MusouShown> shown)
    {
        if (_fx is null || !FxActive) return;
        _weirdboySeen.Clear();
        for (int k = 0; k < shown.Count; k++)
        {
            var s = shown[k];
            if (s.Type != MusouEnemyType.Weirdboy) continue;
            _weirdboySeen.Add(s.Id);
            bool appeared = !_weirdboyAt.ContainsKey(s.Id) && s.State == MusouEnemyState.Spawning;
            _weirdboyAt[s.Id] = s.Position;
            if (appeared) _fx.Play("weirdboy_teleport_in", ToGodot(s.Position), Basis.Identity, 1f);
            bool up = s.State is not (MusouEnemyState.Stunned or MusouEnemyState.Vanishing or MusouEnemyState.Dying);
            bool has = _weirdboyShields.TryGetValue(s.Id, out var fx) && !fx.Dead;
            if (up && !has)
            {
                ushort id = s.Id;
                var shield = _fx.Play("weirdboy_shield", ToGodot(s.Position + new Num.Vector3(0f, 0f, WeirdboyChest)), Basis.Identity, 1f, repeat: true, follow: () =>
                    _weirdboyAt.TryGetValue(id, out var at) ? (ToGodot(at + new Num.Vector3(0f, 0f, WeirdboyChest)), Basis.Identity) : null);
                if (shield is not null) { shield.StopOnLost = true; _weirdboyShields[id] = shield; }
            }
            else if (!up && has) { fx!.Life = Math.Min(fx.Life, fx.Age + 0.2); _weirdboyShields.Remove(s.Id); }
        }
        _weirdboyGone.Clear();
        foreach (var id in _weirdboyAt.Keys) if (!_weirdboySeen.Contains(id)) _weirdboyGone.Add(id);
        foreach (var id in _weirdboyGone)
        {
            _weirdboyAt.Remove(id);
            if (_weirdboyShields.Remove(id, out var fx)) fx.Life = Math.Min(fx.Life, fx.Age + 0.2);
        }
    }

    /// <summary>An effect's axes with its Y column along <paramref name="along"/> (the player's convention).</summary>
    static Basis AxesAlong(Vector3 along)
    {
        var side = MathF.Abs(along.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : along.Cross(Vector3.Up).Normalized();
        var forward = side.Cross(along).Normalized();
        return new Basis(side, along, forward);
    }
}
