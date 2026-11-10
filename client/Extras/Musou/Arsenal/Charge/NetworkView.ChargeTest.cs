using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Arsenal;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid <c>--tfw-test-charge [--tfw-test dir] --equipment demolition</c> (W4, the Remote Detonators): on a real Musou server (<c>SM_TFW_KEEP_HUMAN=1</c>) the scripted player deploys, walks to
/// the Orks, throws a charge at the crowd through the real action, and stills are saved of the throw, the stuck charge and the rolling ring (<c>charge-*.png</c>, the normal view and the view looking
/// up); a <c>TFWCHARGE</c> line gives each step's time, then the spots drawn and the frame times round the ring. One <c>TFWTEST PASS</c> / <c>FAIL</c> line a check; exit 0 or 1.
/// </summary>
public partial class NetworkView
{
    async Task TfwTestCharge()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(1.0);
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "walked towards the Orks", 45.0, () => _musouState.MiddleNear(client.Predicted.Position, 45f) is not null);
        TfwCheck("a crowd is near", TfwFaceCrowd(60f));
        await TfwShot("charge-00-crowd");
        var item = Loadouts.Equipment(_config!, client.Predicted);
        TfwCheck("the player carries the Remote Detonators", item is not null && item.Name == ArsenalRules.RemoteDetonators && client.Predicted.Grenades > 0, item?.Name ?? "none");
        int seen = ChargesSeen, drawn = ChargeSpotsDrawn;
        TfwFaceCrowd(60f);
        float towards = _yaw;
        await TfwActionTap(GameAction.Grenade);
        double thrownAt = _connectedFor;
        await TfwWait(0.25);
        await TfwShot("charge-10-thrown");
        double stuck = double.NaN;
        bool turned = false, running = false;
        while (_connectedFor < thrownAt + 9.0 && ChargesSeen == seen)
        {
            if (double.IsNaN(stuck) && client.Projectiles().Any(p => p.Thrown && (p.Flags & ProjectileFlags.Stuck) != 0)) { stuck = _connectedFor; await TfwWait(0.1); await TfwShot("charge-20-stuck"); }
            // The thrower is in the blast like anybody (the data's ignore_self false): once it has stuck he runs away for 3.5 s, then turns round to watch.
            if (!running && !double.IsNaN(stuck)) { running = true; _yaw = towards + MathF.PI; TfwAction(GameAction.Sprint, true); TfwKey(Key.W, true); }
            if (running && !turned && _connectedFor > stuck + 3.7) { turned = true; TfwKey(Key.W, false); TfwAction(GameAction.Sprint, false); _yaw = towards; }
            await TfwFrames();
        }
        TfwKey(Key.W, false);
        TfwAction(GameAction.Sprint, false);
        TfwCheck("the charge went off (its detonation reached the view)", ChargesSeen > seen, $"{ChargesSeen - seen}");
        double wentOff = _connectedFor;
        GD.Print($"TFWCHARGE thrown at {thrownAt:F2} s, stuck at {stuck:F2} s, went off at {wentOff:F2} s ({wentOff - (double.IsNaN(stuck) ? thrownAt : stuck):F2} s after)");
        var frames = new System.Collections.Generic.List<double>();
        double last = Time.GetTicksUsec() / 1000.0;
        for (int k = 0; k < 8; k++)
        {
            await TfwFrames();
            await TfwShot($"charge-30-ring-{k}");
            double now = Time.GetTicksUsec() / 1000.0;
            frames.Add(now - last);
            last = now;
            await TfwWait(0.12);
        }
        await TfwWait(2.0);
        // Frame times over a quiet second after, for the report (a PNG costs ~330 ms: these were taken with the stills).
        var times = new System.Collections.Generic.List<double>();
        for (int k = 0; k < 90; k++) { double a = Time.GetTicksUsec() / 1000.0; await TfwFrames(); times.Add(Time.GetTicksUsec() / 1000.0 - a); }
        GD.Print($"TFWCHARGE spots drawn {ChargeSpotsDrawn - drawn} of {DemolitionChargeRules.Spots}; frame ms after the ring: median {times.Order().ElementAt(times.Count / 2):F1}, worst {times.Max():F1}");
        TfwCheck("the ring's 16 spots were drawn", ChargeSpotsDrawn - drawn >= DemolitionChargeRules.Spots - 2, $"{ChargeSpotsDrawn - drawn}");
        GetTree().Quit(0);
    }
}
