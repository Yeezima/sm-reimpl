using System.Threading.Tasks;
using Godot;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

public partial class NetworkView
{
    /// <summary>
    /// <c>--dn-da-death</c> (the DA Dreadnought's proof of his death and wreck; with <c>--write-movie</c>): after the landing he walks to the Orks and stands in the crowd striking until he dies (the crowd wears the 2000 health down), then 8 s of the
    /// wreck from the side view. <c>DNFILM seg death frame N</c> marks the frame he died at.
    /// </summary>
    async Task DnDaDeath()
    {
        var client = _client!;
        await TfwWait(5.4);
        _dnCam = 1;
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "da death: walked towards the Orks", 90.0, () => _musouState.MiddleNear(client.Predicted.Position, 14f) is not null);
        double stop = _connectedFor + 150.0;
        while (_connectedFor < stop && client.Predicted.Alive) { await TfwActionTap(GameAction.Melee); await TfwWait(0.9); }
        GD.Print($"DNFILM seg death frame {Engine.GetFramesDrawn()} t {_connectedFor:F1} alive {client.Predicted.Alive}");
        await TfwWait(8.0);
        GD.Print($"DNFILM seg end frame {Engine.GetFramesDrawn()}");
        _dnCam = 0;
        GetTree().Quit(0);
    }

    /// <summary><c>--dn-buff-probe</c>: after the landing, press the power-up key and log the own state's StimLeft and the item's cooldown every 0.5 s for 14 s (<c>DNBUFF</c> lines; the buff's end is checked against the 30 s the sim gives it).</summary>
    async Task DnBuffProbe()
    {
        var client = _client!;
        await TfwWait(5.6);
        await TfwActionTap(GameAction.Grenade);
        double t0 = _connectedFor, next = t0;
        while (_connectedFor < t0 + 14.0)
        {
            await TfwFrames();
            if (_connectedFor < next) continue;
            next += 0.5; var p = client.Predicted;
            GD.Print($"DNBUFF t {_connectedFor - t0:F1} StimLeft {p.StimLeft:F2} grenades {p.Grenades} cooldown {p.GrenadeCooldown:F2} alive {p.Alive}");
        }
        GetTree().Quit(0);
    }

    /// <summary>
    /// <c>--dn-da-clean</c> (the clean proof clips; <c>--write-movie</c>; server <c>SM_TFW_FIRE_TEST=1</c>): right after the landing, before the Orks arrive, from the side view: the cannon for 4 s, the melta (three shots), the light chain, the stomp and the power-up; then the
    /// test barrage (FireTest) on his own spot every 3 s until he dies (or 60 s), and 8 s of the wreck. <c>DNFILM seg NAME frame N</c> marks each part.
    /// </summary>
    async Task DnDaClean()
    {
        var client = _client!;
        void Seg(string name) => GD.Print($"DNFILM seg {name} frame {Engine.GetFramesDrawn()} t {_connectedFor:F1}");
        await TfwWait(5.4);
        _dnCam = 1;
        Seg("cannon");
        TfwAction(GameAction.Fire, true); await TfwWait(3.5); TfwAction(GameAction.Fire, false);
        await TfwWait(0.5);
        Seg("melta");
        await TfwActionTap(GameAction.NextWeapon); await TfwWait(0.8);
        for (int k = 0; k < 3; k++) { TfwAction(GameAction.Fire, true); await TfwWait(0.3); TfwAction(GameAction.Fire, false); await TfwWait(1.3); }
        await TfwActionTap(GameAction.NextWeapon); await TfwWait(0.8);
        Seg("melee");
        for (int k = 0; k < 3; k++) { await TfwActionTap(GameAction.Melee); await TfwWait(1.0); }
        Seg("stomp");
        await TfwActionTap(GameAction.StunStrike); await TfwWait(4.0);
        Seg("buff");
        await TfwActionTap(GameAction.Grenade); await TfwWait(3.0);
        Seg("death");
        double stop = _connectedFor + 60.0;
        while (_connectedFor < stop && client.Predicted.Alive)
        {
            _tfwState.Commands.Issue(TfwCommand.FireTest, 1, client.Predicted.Position);
            await TfwWait(3.0);
        }
        GD.Print($"DNFILM died {!client.Predicted.Alive} frame {Engine.GetFramesDrawn()} t {_connectedFor:F1}");
        await TfwWait(8.0);
        Seg("end");
        _dnCam = 0;
        GetTree().Quit(0);
    }

    /// <summary><c>--dn-da-overcharge</c> (with <c>--write-movie</c>): the power-up (Overcharge): used at once, then the cannon held for 12 s (no heat), then eight melee swings into whatever stands in front. <c>DNFILM seg NAME frame N</c> marks each part; <c>DNOVER</c> lines log the heat.</summary>
    async Task DnDaOvercharge()
    {
        var client = _client!;
        void Seg(string name) => GD.Print($"DNFILM seg {name} frame {Engine.GetFramesDrawn()} t {_connectedFor:F1}");
        await TfwWait(5.4);
        _dnCam = 1;
        Seg("buff");
        await TfwActionTap(GameAction.Grenade); await TfwWait(1.0);
        // the server runs in real time and a rendering run is slower than its clock: the parts follow the power-up's own time left, not the client's seconds
        Seg("cannon");
        TfwAction(GameAction.Fire, true);
        double t0 = _connectedFor, log = t0;
        while (client.Predicted.StimLeft > 9f && _connectedFor < t0 + 40.0) { await TfwFrames(); if (_connectedFor >= log) { log += 0.5; GD.Print($"DNOVER heat {client.Predicted.Heat:F2} overheated {client.Predicted.Overheated} stim {client.Predicted.StimLeft:F1} cooldown {client.Predicted.GrenadeCooldown:F1} grenades {client.Predicted.Grenades}"); } }
        TfwAction(GameAction.Fire, false);
        await TfwWait(0.3);
        Seg("melee");
        for (int k = 0; k < 8; k++) { await TfwActionTap(GameAction.Melee); await TfwWait(0.9); GD.Print($"DNOVER melee {k} stim {client.Predicted.StimLeft:F1}"); }
        Seg("end");
        _dnCam = 0;
        GetTree().Quit(0);
    }

    /// <summary><c>--dn-da-wreck</c> (server <c>SM_DN_TEST_KILL=&lt;s&gt;</c>; with <c>--write-movie</c>): after the landing he stands in the open from the side view until the server kills him, then 9 s of the wreck. <c>DNFILM seg death</c> marks the frame he died at.</summary>
    async Task DnDaWreck()
    {
        var client = _client!;
        await TfwWait(5.4);
        _dnCam = 1;
        double stop = _connectedFor + 60.0;
        GD.Print($"DNFILM seg wait frame {Engine.GetFramesDrawn()} t {_connectedFor:F1}");
        while (_connectedFor < stop && client.Predicted.Alive) await TfwFrames();
        GD.Print($"DNFILM seg death frame {Engine.GetFramesDrawn()} t {_connectedFor:F1} alive {client.Predicted.Alive}");
        await TfwWait(9.0);
        GD.Print($"DNFILM seg end frame {Engine.GetFramesDrawn()}");
        _dnCam = 0;
        GetTree().Quit(0);
    }
}
