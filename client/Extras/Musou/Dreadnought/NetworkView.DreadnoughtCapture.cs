using System;
using System.Threading.Tasks;
using Godot;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// D2+ test aid <c>--tfw-dreadnought-capture</c> (with <c>--content musou --class "sm - dreadnought"</c>, before <c>--tfw-test &lt;dir&gt;</c>, a <c>godot.cmd --windowed</c> run for the PNGs; server <c>SM_TFW_KEEP_HUMAN=1</c>,
/// <c>--bind 127.0.0.1</c>): deploys on lane B (the hub plays the Tactical stand-in; he is the Dreadnought from the landing) and photographs him from the player's view and from third-person views (<see cref="_dnCam"/>:
/// beside, behind and above, in front): the entrance, standing, walking, running, strafing, sprinting. <c>DNCAPTURE</c> lines carry what was seen (class, speed, the clip the pose picked). Quits 0.
/// </summary>
public partial class NetworkView
{
    /// <summary>The third-person view the camera is put in: 0 none, 1 beside, 2 behind and above, 3 in front.</summary>
    int _dnCam;
    string _dnDir = "";

    async Task DnStill(int cam, string name)
    {
        _dnCam = cam;
        await TfwFrames(4);
        await TfwShot(name);
        _dnCam = 0;
    }

    async Task DnViews(string tag)
    {
        await TfwShot($"dn-player-{tag}");
        await DnStill(1, $"dn-side-{tag}");
        await DnStill(2, $"dn-rear-{tag}");
        await DnStill(3, $"dn-front-{tag}");
    }

    async Task TfwDreadnoughtCapture()
    {
        var client = _client!;
        if (CmdFlag("dn-pick"))
        {
            // the player's own path (no --class): PLAY AS DREADNOUGHT, as the Esc menu row and the customizer page send it; he stays the Tactical stand-in in the Stormbird
            TfwCheck("he is not a Dreadnought before the pick", !IsDreadnoughtClass(client.Predicted.Class), client.Predicted.Class.ToString());
            TfwCheck("PLAY AS DREADNOUGHT is on offer in the Stormbird", DreadnoughtOffered(client.Predicted, out _), "");
            TfwCheck("the pick was sent", PickDreadnought("the capture"), "");
            await TfwWait(3.0);
            TfwCheck("in the Stormbird he still plays the Tactical stand-in", !IsDreadnoughtClass(client.Predicted.Class), client.Predicted.Class.ToString());
        }
        if (!await AutocannonDeploy()) return;
        TfwCheck("he is the Dreadnought after the landing", IsDreadnoughtClass(client.Predicted.Class), client.Predicted.Class.ToString());
        if (CmdFlag("dn-survive")) { await DnSurvive(); return; }
        GD.Print($"DNCAPTURE t {_connectedFor:F2} landed, class {client.Predicted.Class}, health {client.Predicted.Health:F0}, locked {DreadnoughtEntryText()}");
        await TfwWait(0.6);
        await DnStill(1, "dn-side-entrance-1");
        await TfwWait(1.4);
        await DnStill(2, "dn-rear-entrance-2");
        await TfwWait(3.2);
        await DnViews("stand");
        // forward: W is the run; Shift the sprint; A strafes (the keys the human uses)
        TfwKey(Key.W, true);
        await TfwWait(1.2);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} run, speed {SpeedOf(client.Predicted):F1}");
        await DnViews("run");
        TfwKey(Key.W, false);
        _yaw += MathF.PI;   // the Guards stood ahead: sprint the other way, into the open
        await TfwWait(0.4);
        TfwKey(Key.W, true);
        TfwKey(Key.Shift, true);
        await TfwWait(2.0);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} sprint, speed {SpeedOf(client.Predicted):F1}");
        await DnViews("sprint");
        TfwKey(Key.Shift, false);
        TfwKey(Key.W, false);
        TfwKey(Key.A, true);
        await TfwWait(1.0);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} strafe, speed {SpeedOf(client.Predicted):F1}");
        await DnViews("strafe");
        TfwKey(Key.A, false);
        TfwKey(Key.S, true);
        await TfwWait(0.9);
        await DnStill(1, "dn-side-back");
        TfwKey(Key.S, false);
        await TfwWait(0.5);
        // the guns: walk towards the Orks, aim at the nearest and hold Fire (the cannon), then Switch and fire the melta
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "walked towards the Orks", 90.0, () => _musouState.MiddleNear(client.Predicted.Position, 26f) is not null);
        for (int run = 0; run < 3 && !AutocannonAim(); run++) await TfwWait(0.5);
        PlayerState me = client.Predicted;
        string held = _config is { } cfg && Loadouts.InHand(cfg, me) is { } hw ? hw.Name : "?";
        GD.Print($"DNCAPTURE t {_connectedFor:F2} in hand {held}, clip {me.Clip}, reserve {me.Reserve}");
        TfwCheck("the cannon is in hand", held == DreadnoughtRules.CannonWargear, held);
        await TfwShot("dn-player-gun-ready");
        int clip0 = me.Clip;
        TfwAction(GameAction.Fire, true);
        double begun = _connectedFor;
        await TfwWait(0.5);
        await DnStill(1, "dn-side-fire-1");
        while (_connectedFor < begun + 1.2) { AutocannonAim(); await TfwFrames(); }
        await TfwShot("dn-player-fire");
        await DnStill(2, "dn-rear-fire");
        while (_connectedFor < begun + 3.0) { AutocannonAim(); await TfwFrames(); }
        TfwAction(GameAction.Fire, false);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} after {(_connectedFor - begun):F1} s of fire: clip {client.Predicted.Clip} (was {clip0}), reload {client.Predicted.ReloadLeft:F2}");
        TfwCheck("rounds were fired", client.Predicted.Clip < clip0 || client.Predicted.ReloadLeft > 0f, $"{clip0} -> {client.Predicted.Clip}");
        await TfwWait(0.3);
        // the melta: Switch, then fire it at the crowd twice
        await TfwActionTap(GameAction.NextWeapon);
        await TfwWait(1.2);
        held = _config is { } cfg2 && Loadouts.InHand(cfg2, client.Predicted) is { } mw ? mw.Name : "?";
        GD.Print($"DNCAPTURE t {_connectedFor:F2} in hand {held}, clip {client.Predicted.Clip}");
        TfwCheck("Switch brought the melta", held == DreadnoughtRules.MeltaWargear, held);
        await DnStill(1, "dn-side-melta-ready");
        for (int shot = 0; shot < 2; shot++)
        {
            AutocannonAim();
            TfwAction(GameAction.Fire, true);
            await TfwFrames(3);
            int clip = client.Predicted.Clip;
            if (shot == 0) { await TfwShot("dn-player-melta"); await DnStill(1, "dn-side-melta"); }
            TfwAction(GameAction.Fire, false);
            GD.Print($"DNCAPTURE melta shot {shot}: clip {clip}, after {client.Predicted.Clip}");
            await TfwWait(1.5);
        }
        // the arm: switch back to the cannon, face the crowd and strike: a light X, then the heavy Y (the stomp), then the sprint charge
        await TfwActionTap(GameAction.NextWeapon);
        await TfwWait(0.8);
        TfwFaceCrowd(30f);
        int healthBefore = TfwNearCount(12f);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} melee: crowd near {healthBefore}");
        await TfwActionTap(GameAction.Melee);
        for (int k = 0; k < 8; k++) { await TfwWait(0.25); if (k is 1 or 3) await DnStill(1, $"dn-side-melee-x-{k}"); }
        GD.Print($"DNCAPTURE t {_connectedFor:F2} after X: step {client.Predicted.MeleeStep} heavy {client.Predicted.MeleeHeavy}");
        await TfwWait(2.0);
        await TfwActionTap(GameAction.StunStrike);
        for (int k = 0; k < 10; k++) { await TfwWait(0.25); if (k is 3 or 5) await DnStill(1, $"dn-side-melee-y-{k}"); }
        GD.Print($"DNCAPTURE t {_connectedFor:F2} after Y: step {client.Predicted.MeleeStep} heavy {client.Predicted.MeleeHeavy}, crowd near {TfwNearCount(12f)}");
        await TfwWait(2.5);
        // the buff: the equipment key, then what the state shows
        int grenades = client.Predicted.Grenades;
        await TfwActionTap(GameAction.Grenade);
        await TfwWait(1.0);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} buff: stim left {client.Predicted.StimLeft:F1} s, charges {grenades} -> {client.Predicted.Grenades}");
        TfwCheck("the buff is running", client.Predicted.StimLeft > 5f, client.Predicted.StimLeft.ToString("F1"));
        await DnStill(1, "dn-side-buff");
        if (CmdFlag("dn-film")) { await DnSurvive(); return; }
        GD.Print("DNCAPTURE done");
        GetTree().Quit(0);
    }

    /// <summary>
    /// <c>--dn-survive</c> (D8, balance sanity): into the crowd, aim at the nearest Ork and hold the cannon, striking in turn, for 150 s (a Musou war's first minutes); a DNSURVIVE line every 5 s (health, clip, Orks within 12 m, kills seen)
    /// and one for each death and respawn (class, health, how long he lived). Quits 0.
    /// </summary>
    async Task DnSurvive()
    {
        var client = _client!;
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "survive: walked towards the Orks", 90.0, () => _musouState.MiddleNear(client.Predicted.Position, 26f) is not null);
        double start = _connectedFor, next = start; bool alive = client.Predicted.Alive; double lifeStart = start; int deaths = 0, respawns = 0; long kills0 = MusouDeathsSeen;
        float minHealth = float.MaxValue;
        TfwAction(GameAction.Fire, true);
        while (_connectedFor < start + 150.0)
        {
            await TfwFrames();
            var me = client.Predicted;
            if (me.Alive) { AutocannonAim(); minHealth = MathF.Min(minHealth, me.Health); }
            if (alive && !me.Alive) { deaths++; alive = false; GD.Print($"DNSURVIVE t {_connectedFor - start:F1} DIED after {_connectedFor - lifeStart:F1} s of life; kills so far {MusouDeathsSeen - kills0}"); await TfwShot($"dn-death-{deaths}"); }
            if (!alive && me.Alive) { respawns++; alive = true; lifeStart = _connectedFor; GD.Print($"DNSURVIVE t {_connectedFor - start:F1} RESPAWNED class {me.Class} dreadnought {IsDreadnoughtClass(me.Class)} health {me.Health:F0}"); await TfwShot($"dn-respawn-{respawns}"); }
            if (_connectedFor >= next) { next += 5.0; GD.Print($"DNSURVIVE t {_connectedFor - start:F0} health {me.Health:F0} clip {me.Clip} alive {me.Alive} Orks within 12 m {TfwNearCount(12f)} kills {MusouDeathsSeen - kills0}"); }
            if ((int)(_connectedFor - start) % 20 == 5 && me.Alive && me.Clip < 40) { TfwAction(GameAction.Fire, false); await TfwActionTap(GameAction.Reload); TfwAction(GameAction.Fire, true); }
        }
        TfwAction(GameAction.Fire, false);
        GD.Print($"DNSURVIVE done: {deaths} death(s), {respawns} respawn(s), lowest health {minHealth:F0}, kills {MusouDeathsSeen - kills0}");
        TfwCheck("the Dreadnought played the whole span", true, "");
        GetTree().Quit(0);
    }

    static float SpeedOf(PlayerState s) => MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
    string DreadnoughtEntryText() => _client is { } c ? $"yaw {c.Predicted.Yaw:F2}" : "";

    /// <summary>The camera off the player for the capture's third-person views, after the game's own camera; nothing without the aid. The player is 3 m tall: 7 m off.</summary>
    void DreadnoughtCamFrame()
    {
        if (_dnCam == 0 || _camera is not { } camera || _gameSpace is null || _client is null) return;
        var p = _client.Predicted.Position;
        var fwd = new Vector3(-MathF.Sin(_yaw), MathF.Cos(_yaw), 0f);
        var right = new Vector3(MathF.Cos(_yaw), MathF.Sin(_yaw), 0f);
        var feet = new Vector3(p.X, p.Y, p.Z);
        var look = feet + Vector3.Back * 1.6f;
        var eye = _dnCam switch
        {
            1 => feet + right * 7f + Vector3.Back * 2.2f,
            2 => feet - fwd * 7f + right * 1.0f + Vector3.Back * 4.0f,
            _ => feet + fwd * 7f + right * 1.5f + Vector3.Back * 2.2f,
        };
        camera.GlobalTransform = Transform3D.Identity.Translated(_gameSpace.ToGlobal(eye)).LookingAt(_gameSpace.ToGlobal(look), Vector3.Up);
    }
}
