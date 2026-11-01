using System;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid <c>--tfw-test-capture [--tfw-capture-seconds s] [--tfw-test dir]</c> (Musou Mode, the crowds' milestone): a scripted player that plays like a human on a real server, with no test aid
/// on the server: it deploys, walks to the nearest position not secured along the walking graph, and there fights what comes (faces the crowd, taps the trigger, reloads, strikes in melee) while
/// standing on the point, until the point reads Secured, then goes on to the next, up to all three. A <c>TFWCAP</c> line every 5 s says the point, its state, progress, the player's distance and height
/// from its centre against the point's radius, and whether Orks are near. One <c>TFWTEST PASS</c> or <c>FAIL</c> per point. The server may run with <c>SM_TFW_KEEP_HUMAN=1</c> (a player who falls stands up
/// where it fell) and <c>SM_TFW_FIGHT_LOG</c>; neither touches an Ork or a point.
/// </summary>
public partial class NetworkView
{
    async Task TfwTestCapture()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(1.0);
        double seconds = double.TryParse(CmdArg("tfw-capture-seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 600.0;
        double limit = _connectedFor + seconds;
        int taken = 0;
        while (_connectedFor < limit && TfwNow.Phase == TfwRunPhase.Battle && taken < 3)
        {
            int k = -1;
            float best = float.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                if (_tfwState.Objectives[i].State == TfwPositionState.Secured) continue;
                float d = Num.Vector3.Distance(_tfwState.Objectives[i].Position, client.Predicted.Position);
                if (d < best) { best = d; k = i; }
            }
            if (k < 0) break;
            string name = ((char)('A' + k)).ToString();
            var point = _tfwState.Objectives[k].Position;
            GD.Print($"TFWCAP going to point {name} ({k}) at {point}, radius {_tfwState.Objectives[k].Radius:F1}");
            await TfwPathTo(point, $"walked to point {name}", 120.0, () => !client.Predicted.Alive || TfwNow.Phase != TfwRunPhase.Battle || TfwFlat(client.Predicted.Position, point) < 3f || _tfwState.Objectives[k].State == TfwPositionState.Secured);
            double stop = Math.Min(limit, _connectedFor + 240.0), logAt = 0.0, reloadAt = 0.0, cleared = 0.0;
            bool inside = false;
            while (_connectedFor < stop && _tfwState.Objectives[k].State != TfwPositionState.Secured && TfwNow.Phase == TfwRunPhase.Battle)
            {
                var o = _tfwState.Objectives[k];
                var me = client.Predicted.Position;
                float flat = TfwFlat(me, o.Position), dz = me.Z - o.Position.Z;
                inside = flat <= o.Radius;
                if (client.Predicted.Alive)
                {
                    // A player takes a point by standing on it: walk to its centre (facing it) until within 3 m, then stand and fight what comes. --tfw-capture-weak aims at the middle of the crowd, as a poor player does.
                    bool away = flat > 3f;
                    bool crowd = !away && (CmdFlag("tfw-capture-weak") ? TfwFaceCrowd(40f) : TfwAimNearest(40f));
                    if (!crowd) { _yaw = MathF.Atan2(-(o.Position.X - me.X), o.Position.Y - me.Y); _pitch = 0f; }
                    // A player hunts what stands in the ring: when the nearest Ork is over 6 m off and stands inside the point, step towards it while firing (not more than 10 m from the centre).
                    bool hunt = false;
                    if (crowd && _musouState.NearestTo(me, 40f) is { } prey)
                        hunt = TfwFlat(prey, me) > 6f && TfwFlat(prey, o.Position) <= o.Radius && flat < 10f;
                    TfwKey(Key.W, away || hunt);
                    TfwAction(GameAction.Fire, crowd && ((int)(_connectedFor * 4.0) & 1) == 0);
                    if (crowd && _musouState.NearestTo(me, 3.5f) is not null && _connectedFor > cleared) { cleared = _connectedFor + 0.9; await TfwActionTap(GameAction.Melee); }
                    if (_connectedFor > reloadAt) { reloadAt = _connectedFor + 4.0; await TfwActionTap(GameAction.Reload); }
                }
                if (_connectedFor > logAt)
                {
                    logAt = _connectedFor + 5.0;
                    GD.Print(string.Create(CultureInfo.InvariantCulture, $"TFWCAP t {_connectedFor:F0} label \"{TfwHudModel.ObjectiveLabel(o, false, TfwOrksOn(o))}\" point {name}: {o.State} progress {o.Progress:F2}, I am {flat:F1} m from its centre (radius {o.Radius:F1}, inside {inside}), {dz:F1} m above it, alive {client.Predicted.Alive}, Orks within 20 m {(_musouState.MiddleNear(me, 20f) is not null ? "yes" : "no")}, within 12 m of the centre {(_musouState.MiddleNear(o.Position, 12f) is not null ? "yes" : "no")}"));
                }
                await TfwFrames();
            }
            TfwAction(GameAction.Fire, false);
            TfwKey(Key.W, false);
            bool done = _tfwState.Objectives[k].State == TfwPositionState.Secured;
            TfwCheck($"point {name} is secured", done, $"{_tfwState.Objectives[k].State} {_tfwState.Objectives[k].Progress:F2}");
            if (!done) break;
            taken++;
            await TfwShot($"capture-{name}");
        }
        TfwCheck("all three points were secured", taken == 3, taken.ToString());
        if (taken == 3) TfwCheck("the commander came", _tfwState.Objectives[TfwBattle.CommanderObjective].State != TfwPositionState.Locked, _tfwState.Objectives[TfwBattle.CommanderObjective].State.ToString());
    }

    /// <summary>Looks at the nearest enemy known within <paramref name="within"/> metres, yaw and pitch, as a player aims; false with none.</summary>
    bool TfwAimNearest(float within)
    {
        var me = _client!.Predicted.Position;
        if (_musouState.NearestTo(me, within) is not { } at) return false;
        _yaw = MathF.Atan2(-(at.X - me.X), at.Y - me.Y);
        _pitch = MathF.Atan2(at.Z + 1.0f - (me.Z + 1.6f), MathF.Max(0.5f, TfwFlat(at, me)));
        return true;
    }

    static float TfwFlat(Num.Vector3 a, Num.Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
