using System.Threading.Tasks;
using Godot;
using SmNet;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou weight and impact pass, phase 1 test aid <c>--tfw-weight-capture</c> (before <c>--tfw-test</c>; server <c>SM_TFW_KEEP_HUMAN=1</c>): one scripted run through the moves the owner named
/// (2026-11-10: landing, running, jumping, attacks, rolls, hits taken, fire) for the baseline clips and measurements. The player deploys on lane B, walks 3 s and sprints 3 s towards the first
/// position, stops; then three rolls (forward, left, right), or with a jump pack (<c>--tfw-weight-pack</c>) a hop, a ground pound and a low slash; then towards the crowd, 2.5 s of aimed fire,
/// six swings (light, light, stun strike), and 5 s standing still to take hits, and quits. Every press goes through the real actions. A <c>TFWWEIGHT mark</c> line marks each part with the
/// process frame (a Movie Maker clip is one frame a frame); <c>--tfw-weight-log</c> adds a <c>TFWWEIGHT f</c> line a frame (time, horizontal speed, height, vertical speed, grounded, jump
/// phase, evade ticks).
/// </summary>
public partial class NetworkView
{
    bool _tfwWeightDone;

    void TfwWeightMark(string part)
    {
        if (LogMusouWeight) GD.Print($"TFWWEIGHT contacts before {part}: {MusouContactCountsText()}");   // MW1: the contacts of the part that just ended
        GD.Print($"TFWWEIGHT mark {part} t {_connectedFor:F3} frame {Engine.GetProcessFrames()}");
    }

    async Task TfwWeightSampler()
    {
        var client = _client!;
        while (!_tfwWeightDone)
        {
            var p = client.Predicted;
            float flat = new Vector2(p.Velocity.X, p.Velocity.Y).Length();
            GD.Print($"TFWWEIGHT f {_connectedFor:F3} {Engine.GetProcessFrames()} v {flat:F2} z {p.Position.Z:F3} vz {p.Velocity.Z:F2} g {(p.Grounded ? 1 : 0)} j {p.Jump} e {p.EvadeTicks}");
            await TfwFrames();
        }
    }

    async Task TfwWeightCapture()
    {
        var client = _client!;
        bool pack = CmdFlag("tfw-weight-pack");
        if (!await TfwTestDeploy(TfwLane.B)) return;
        if (CmdFlag("tfw-weight-log")) _ = TfwWeightSampler();
        TfwWeightMark("deployed");
        await TfwWait(2.0);
        var point = _tfwState.Objectives[0].Position;
        bool Crowd(float within) => _musouState.MiddleNear(client.Predicted.Position, within) is not null;

        TfwWeightMark("walk");
        double until = _connectedFor + 3.0;
        await TfwPathTo(point, "weight: walked", 4.0, () => _connectedFor > until || Crowd(18f));
        TfwWeightMark("sprint");
        TfwAction(GameAction.Sprint, true);
        until = _connectedFor + 3.0;
        await TfwPathTo(point, "weight: sprinted", 4.0, () => _connectedFor > until || Crowd(18f));
        TfwAction(GameAction.Sprint, false);
        TfwWeightMark("stop");
        await TfwWait(1.5);

        if (!pack)
        {
            TfwWeightMark("roll forward");
            TfwKey(Key.W, true); await TfwActionTap(GameAction.Jump); TfwKey(Key.W, false);
            await TfwWait(1.4);
            TfwWeightMark("roll left");
            TfwKey(Key.A, true); await TfwActionTap(GameAction.Jump); TfwKey(Key.A, false);
            await TfwWait(1.4);
            TfwWeightMark("roll right");
            TfwKey(Key.D, true); await TfwActionTap(GameAction.Jump); TfwKey(Key.D, false);
            await TfwWait(1.6);
        }
        else
        {
            // A tap of the jump button is the pack's dash; a held press the full jump (its landing), with a melee press in the air the ground pound; a dash with a melee press the dash attack.
            async Task Landed(string what)
            {
                await TfwUntil($"weight: {what} took off", () => !client.Predicted.Grounded, 2.0);
                await TfwUntil($"weight: {what} landed", () => client.Predicted.Grounded, 8.0);
                TfwWeightMark($"{what} landed");
            }
            TfwWeightMark("pack dash");
            await TfwActionTap(GameAction.Jump);
            await Landed("pack dash");
            await TfwWait(1.4);
            TfwWeightMark("pack jump");
            TfwAction(GameAction.Jump, true);
            await TfwWait(0.55);
            TfwAction(GameAction.Jump, false);
            await Landed("pack jump");
            await TfwWait(1.6);
            TfwWeightMark("pack pound");
            TfwAction(GameAction.Jump, true);
            await TfwWait(0.55);
            TfwAction(GameAction.Jump, false);
            await TfwActionTap(GameAction.Melee);
            await Landed("pack pound");
            await TfwWait(1.6);
            TfwWeightMark("pack dash attack");
            await TfwActionTap(GameAction.Jump);
            await TfwWait(0.1);
            await TfwActionTap(GameAction.Melee);
            await Landed("pack dash attack");
            await TfwWait(1.4);
        }

        TfwWeightMark("to the crowd");
        await TfwPathTo(point, "weight: walked towards the Orks", 40.0, () => Crowd(16f));
        TfwWeightMark("fire");
        TfwAction(GameAction.Fire, true);
        for (double end = _connectedFor + 2.5; _connectedFor < end;) { TfwAimNearest(30f); await TfwWait(0.1); }
        TfwAction(GameAction.Fire, false);
        await TfwWait(0.6);
        TfwWeightMark("approach");
        for (int run = 0; run < 3; run++)
        {
            var goal = _musouState.MiddleNear(client.Predicted.Position, 30f) ?? point;
            until = _connectedFor + 4.0;
            await TfwPathTo(goal, $"weight: into the crowd ({run + 1})", 5.0, () => _connectedFor > until || TfwNearCount(6f) >= 3);
            if (TfwNearCount(6f) >= 3) break;
        }
        TfwWeightMark("melee");
        for (int k = 0; k < 6; k++)
        {
            TfwAimNearest(8f);
            bool heavy = k % 3 == 2;
            TfwWeightMark(heavy ? "heavy" : "light");
            await TfwActionTap(heavy ? GameAction.StunStrike : GameAction.Melee);
            await TfwWait(heavy ? 1.1 : 0.55);
        }
        TfwWeightMark("hits taken");
        await TfwWait(5.0);
        TfwWeightMark("done");
        _tfwWeightDone = true;
        await TfwWait(0.5);
        GetTree().Quit(0);
    }

    int TfwNearCount(float within)
    {
        int n = 0;
        var me = _client!.Predicted.Position;
        foreach (var e in _tfwShown) if (e.State != MusouEnemyState.Dying && Num.Vector3.Distance(e.Position, me) < within) n++;
        return n;
    }
}
