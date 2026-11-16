using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// W1c test aid <c>--tfw-autocannon-impact-capture</c> (with <c>--class "sm - devastator" --primary "auto cannon" --content musou</c>, before <c>--tfw-test &lt;dir&gt;</c>, a <c>--windowed</c> run for the PNGs;
/// server <c>SM_TFW_KEEP_HUMAN=1</c>): the stills of the autocannon's impacts on the level (the owner, 2026-11-10: "Loyalist impact"). After the landing the player turns to the wall in sight 4.5 to 12 m
/// away nearest 6 m (the level's weapon collision, a face standing within 20 degrees of upright; with none that close, he first walks towards the nearest within 45 m) and fires through the real
/// actions, then at the ground about 4.5 m ahead (each on a heading no other character stands across); for the second and third round of each the
/// player's own view is saved on the frame after the round leaves and 0.05 s, 0.12 s and 0.3 s later (<c>impact-wall-r&lt;n&gt;-&lt;k&gt;</c>, <c>impact-ground-r&lt;n&gt;-&lt;k&gt;</c>), so the burst's flash, fire and
/// smoke are each in one. <c>ACCAPTURE</c> lines name the effects as loaded (<see cref="AutocannonEffectsLine"/>), the wall and the ground found (distance, surface) and each still. Quits 0.
/// <c>--old-autocannon-fx</c> gives the same stills of the twin's purple burst.
/// </summary>
public partial class NetworkView
{
    async Task TfwAutocannonImpactCapture()
    {
        var client = _client!;
        if (!await AutocannonDeploy()) return;
        await TfwWait(1.5);
        string held = _config is { } c && Loadouts.InHand(c, client.Predicted) is { } w ? w.Name : "?";
        GD.Print($"ACCAPTURE t {_connectedFor:F2} in hand: {held}");
        AutocannonEffectsLine();
        TfwCheck("the autocannon is in hand", held == SmSim.Extras.Musou.Arsenal.ArsenalRules.Autocannon, held);
        int impacts = _fxImpacts;
        // No wall close by (a landing in the open): walk towards the nearest one in sight within 45 m until it is about 6 m ahead.
        if (AutocannonWall(4.5f, 12f) is null && AutocannonWall(4.5f, 45f) is { } far)
        {
            GD.Print($"ACCAPTURE wall: the nearest in sight is {far.Distance:F1} m away; walking towards it");
            var me = client.Predicted.Position;
            var dir = AutocannonDirection(far.Yaw, 0f);
            await TfwWalkTo(me + dir * (far.Distance - 6f), "walked towards a wall", 20.0);
            await TfwWait(1.5);   // the retainer and the squad settle
        }
        if ((AutocannonWall(4.5f, 12f, clear: true) ?? AutocannonWall(4.5f, 12f)) is { } wall)
        {
            GD.Print($"ACCAPTURE wall: {wall.Distance:F1} m away, surface {_catalogue?.SurfaceName(wall.Surface) ?? "?"} ({wall.Surface}), normal ({wall.Normal.X:F2}, {wall.Normal.Y:F2}, {wall.Normal.Z:F2})");
            await AutocannonImpactStills("impact-wall", wall.Yaw, wall.Pitch);
        }
        else TfwCheck("a wall in sight 4.5 to 12 m away", false, "none found round the landing point");
        if (AutocannonGround() is { } ground)
        {
            GD.Print($"ACCAPTURE ground: {ground.Distance:F1} m along the aim, surface {_catalogue?.SurfaceName(ground.Surface) ?? "?"} ({ground.Surface})");
            await AutocannonImpactStills("impact-ground", ground.Yaw, ground.Pitch);
        }
        else TfwCheck("the ground ahead", false, "no ground within 20 m along the aim");
        GD.Print($"ACCAPTURE mark done t {_connectedFor:F3}: world impacts played {_fxImpacts - impacts}");
        TfwCheck("the rounds burst on the level", _fxImpacts > impacts, (_fxImpacts - impacts).ToString());
        await TfwWait(1.0);
        GetTree().Quit(0);
    }

    /// <summary>Fires at (<paramref name="yaw"/>, <paramref name="pitch"/>) and saves four stills each of the second and third round's impact (the first can wait on the raise).</summary>
    async Task AutocannonImpactStills(string name, float yaw, float pitch)
    {
        _yaw = yaw; _pitch = pitch;
        await TfwFrames(20);
        await TfwShot(name + "-r0-before");
        TfwAction(GameAction.Fire, true);
        double gaveUp = _connectedFor + 6.0;
        for (int round = 1; round <= 3 && _connectedFor < gaveUp; round++)
        {
            int shots = _fxOwnShots;
            while (_fxOwnShots == shots && _connectedFor < gaveUp) { _yaw = yaw; _pitch = pitch; await TfwFrames(); }
            if (_fxOwnShots == shots) break;
            if (round == 1) continue;
            double left = _connectedFor;
            int world = _fxImpacts;
            await TfwFrames(1);
            await TfwShot($"{name}-r{round - 1}-0");
            foreach (var (k, after) in new[] { (1, 0.05), (2, 0.12), (3, 0.3) })
            {
                while (_connectedFor < left + after) { _yaw = yaw; _pitch = pitch; await TfwFrames(); }
                await TfwShot($"{name}-r{round - 1}-{k}");
            }
            GD.Print($"ACCAPTURE still {name} round {round}: t {left:F3} and 0.05 s, 0.12 s, 0.3 s after; world impacts drawn {_fxImpacts - world}");
        }
        TfwAction(GameAction.Fire, false);
        await TfwWait(1.2);   // the heat falls a little, the smoke clears
    }

    /// <summary>The eye's point (the capture's rays start there; the camera's aim comes from the same yaw and pitch).</summary>
    Num.Vector3 AutocannonEye() => _client!.Predicted.Position + new Num.Vector3(0f, 0f, 1.6f);

    static Num.Vector3 AutocannonDirection(float yaw, float pitch) =>
        new(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));

    /// <summary>
    /// The wall in sight <paramref name="near"/> to <paramref name="far"/> m away nearest 6 m (72 headings, level rays at the eye), aimed 0.4 m under the eye's height; null when there is none.
    /// <paramref name="clear"/>: only a heading no other character stands across (<see cref="AutocannonClear"/>: the retainer at the player's side would take the rounds).
    /// </summary>
    (float Yaw, float Pitch, float Distance, int Surface, Num.Vector3 Normal)? AutocannonWall(float near, float far, bool clear = false)
    {
        if (_client?.Collision is not { } world) return null;
        var eye = AutocannonEye();
        (float Yaw, float Pitch, float Distance, int Surface, Num.Vector3 Normal)? best = null;
        for (int i = 0; i < 72; i++)
        {
            float yaw = i * MathF.PI / 36f;
            var dir = AutocannonDirection(yaw, 0f);
            if (world.Weapons.Raycast(eye, dir, far) is not { } hit || hit.Distance < near || MathF.Abs(hit.Normal.Z) > 0.34f || clear && !AutocannonClear(yaw, hit.Distance)) continue;
            float pitch = MathF.Atan2(-0.4f, hit.Distance);
            if (best is null || MathF.Abs(hit.Distance - 6f) < MathF.Abs(best.Value.Distance - 6f))
                best = (yaw, pitch, hit.Distance, world.Weapons.MaterialOf(hit.Triangle), hit.Normal);
        }
        return best;
    }

    /// <summary>The ground about 4.5 m ahead (a face within 45 degrees of level), on the player's heading or the nearest turn from it (10 degree steps) with no character across the line; else null.</summary>
    (float Yaw, float Pitch, float Distance, int Surface)? AutocannonGround()
    {
        if (_client?.Collision is not { } world) return null;
        float pitch = MathF.Atan2(-1.6f, 4.5f);
        for (int k = 0; k < 36; k++)
        {
            float yaw = _yaw + (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * MathF.PI / 18f;
            if (world.Weapons.Raycast(AutocannonEye(), AutocannonDirection(yaw, pitch), 20f) is not { } hit || hit.Normal.Z < 0.7f && hit.Normal.Z > -0.7f) continue;
            if (!AutocannonClear(yaw, hit.Distance)) continue;
            return (yaw, pitch, hit.Distance, world.Weapons.MaterialOf(hit.Triangle));
        }
        return null;
    }

    /// <summary>
    /// Whether no other living character stands within 2.5 m of the line on heading <paramref name="yaw"/> from 4 m behind the player to <paramref name="distance"/> m ahead (the shot leaves the camera, which
    /// stands behind and beside the player, so the margin is wide: a retainer at the player's shoulder took the rounds at 1.3 m).
    /// </summary>
    bool AutocannonClear(float yaw, float distance)
    {
        var me = _client!.Predicted.Position;
        var fwd = new Num.Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        foreach (var other in _lastOthers)
        {
            if (!other.Alive) continue;
            var to = new Num.Vector2(other.Position.X - me.X, other.Position.Y - me.Y);
            float along = Num.Vector2.Dot(to, fwd), across = MathF.Abs(to.X * fwd.Y - to.Y * fwd.X);
            if (along > -4f && along < distance + 0.5f && across < 2.5f) return false;
        }
        return true;
    }

    /// <summary>
    /// W1c: the <c>ACCAPTURE effects</c> line: the autocannon's effects as the client loaded them (the flash, the tracer, the impact container and its world hit) and the blast's look (the frag item the
    /// bolt blast records name, <c>World.TfwBoltBlasts</c>, and its detonation effects).
    /// </summary>
    void AutocannonEffectsLine()
    {
        var shown = _catalogue?.Weapon(SmSim.Extras.Musou.Arsenal.ArsenalRules.Autocannon);
        string world = _catalogue is { } cat && shown is not null
            ? string.Join("+", cat.Impact(SmSim.Extras.Musou.Arsenal.ArsenalRules.Autocannon, ShotHit.World, 3).Select(p => $"{p.Name}@{p.Scale:0.##}")) : "none";
        string blast = "none";
        if (_config is { } config && _catalogue is { } catalogue)
            for (int i = 0; i < config.Thrown.Count; i++)
                if (TfwCombatRules.IsFrag(config.Thrown[i]))
                {
                    var det = catalogue.Thrown.GetValueOrDefault(config.Thrown[i].Name)?.Projectile;
                    blast = $"{config.Thrown[i].Name} -> {(det is null ? "no projectile" : string.Join("+", det.Detonation.Select(p => p.Name)))}";
                    break;
                }
        GD.Print($"ACCAPTURE effects: from {shown?.Wargear ?? "none"}: muzzle {shown?.Muzzle?.Name ?? "none"} at {shown?.Muzzle?.Marker ?? "-"}, tracer {shown?.Tracer?.Name ?? "none"}, " +
                 $"impacts {shown?.HitMaterial?.Container ?? "none"} (world hit on stone {world}); blast {blast}; muzzle locator {(MuzzleWorld(_client!.PlayerId) is not null ? "found" : "none")}");
    }
}
