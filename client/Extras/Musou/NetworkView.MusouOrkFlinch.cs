using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode, Ork hit reactions (owner 2026-11-13, "reference v3"; <see cref="MusouOrkFlinchRules"/>): what the client already receives feeds the crowd's flinch. A hit event names the
/// attacker (<c>MusouEvent.Player</c>), the enemy and a marker (1 body, 2 head, 3 melee): the direction is the Ork's drawn position away from the attacker's; the strength is by the weapon
/// in the attacker's hand (shots) or the melee wargear, its heavy flag and its charge (melee). Explosives come from the detonations the view already plays (every thrown or projectile blast, with
/// its centre and its splash radius): each Ork inside takes 2.5x beside the centre fading to 1x at the edge, away from it; the server's reports of the same blast add no second kick.
/// Visual only (no simulation, wire, protocol or hash change). Switch <c>--no-musou-ork-flinch</c> (the crowd as of 2026-11-06: no reaction). Test aid <c>--log-musou-flinch</c>.
/// Not on the wire, so approximated: the attacker's weapon of another player is the one the snapshot shows in hand; frags, Vengeance rounds and the drop pod arrive as detonations (centre and
/// radius known); the artillery barrage and the plasma cannon's blast also arrive as detonations when the view plays one, otherwise their hits read as shots.
/// </summary>
public partial class NetworkView
{
    static readonly bool LogMusouFlinch = CmdFlag("log-musou-flinch");
    int _musouFlinchHits, _musouFlinchBlasts;

    /// <summary>A hit on an Ork: it flinches away from the attacker.</summary>
    void MusouOrkFlinchHit(in MusouEvent e, IReadOnlyList<MusouShown> shown, PlayerState me)
    {
        if (MusouCrowd.NoOrkFlinch || _musouCrowd is null || _config is null || e.Player == 0 || e.Value == MusouWeirdboy.ShieldMarker) return;
        int i = MusouFind(shown, e.Enemy);
        if (i < 0 || shown[i].State is MusouEnemyState.Dying or MusouEnemyState.Knocked) return;
        PlayerState? attacker = e.Player == _client?.PlayerId ? me : MusouPlayerState(e.Player);
        if (attacker is not { } who) return;
        var away = new Num.Vector2(shown[i].Position.X - who.Position.X, shown[i].Position.Y - who.Position.Y);
        if (away.LengthSquared() < 1e-6f) away = new Num.Vector2(-MathF.Sin(who.Yaw), MathF.Cos(who.Yaw));   // on top of the attacker: along its aim
        float strength;
        string what;
        if (e.Value == 3)
        {
            string? wargear = MeleeWargear(who);
            strength = MusouOrkFlinchRules.MeleeStrength(wargear, who.MeleeHeavy, who.Charging);
            what = $"melee {wargear ?? "-"}{(who.MeleeHeavy ? " heavy" : "")}{(who.Charging ? " charge" : "")}";
        }
        else
        {
            string? weapon = Loadouts.InHand(_config, who)?.Name;
            strength = MusouOrkFlinchRules.ShotStrength(weapon);
            what = $"shot {weapon ?? "-"}";
        }
        _musouCrowd.Hit(e.Enemy, away, strength, shown[i].Type);
        _musouFlinchHits++;
        if (LogMusouFlinch) GD.Print($"MUSOU FLINCH t {_connectedFor:F3} enemy {e.Enemy} {shown[i].Type}: {what} x{strength:F2}");
    }

    /// <summary>A detonation the view plays: every Ork within the blast's splash radius is kicked away from the centre.</summary>
    void MusouOrkFlinchBlast(ProjectileView d)
    {
        if (MusouCrowd.NoOrkFlinch || _musouCrowd is null || _config is null || !MusouActive) return;
        float radius = 0f;
        var spec = Shots.ProjectileOf(_config, d.Weapon, d.Stage, d.Thrown);
        if (spec is not null) foreach (var s in spec.AllSplashes) radius = MathF.Max(radius, s.Radius);
        if (d.Thrown && d.Weapon >= 0 && d.Weapon < _config.Thrown.Count) radius *= _config.ThrownBlastScale(_config.Thrown[d.Weapon]);
        if (!(radius > 0f)) return;
        int n = 0;
        var shown = _tfwShown;
        for (int k = 0; k < shown.Count; k++)
        {
            var o = shown[k];
            if (o.State is MusouEnemyState.Dying or MusouEnemyState.Knocked) continue;
            var offset = new Num.Vector3(o.Position.X - d.Position.X, o.Position.Y - d.Position.Y, o.Position.Z + 1f - d.Position.Z);
            float distance = offset.Length();
            if (distance > radius) continue;
            var away = new Num.Vector2(offset.X, offset.Y);
            if (away.LengthSquared() < 1e-6f) away = new Num.Vector2(1f, 0f);
            _musouCrowd.Blast(o.Id, away, distance, radius, o.Type);
            n++;
        }
        _musouFlinchBlasts += n;
        if (LogMusouFlinch && n > 0) GD.Print($"MUSOU FLINCH t {_connectedFor:F3} blast at {d.Position.X:F1},{d.Position.Y:F1} radius {radius:F1}: {n} Orks kicked");
    }
}
