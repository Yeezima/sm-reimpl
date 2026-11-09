using System.Threading.Tasks;
using Godot;
using SmNet;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// musou/sound-ab test aid <c>--tfw-sound-capture</c> (before <c>--tfw-test</c>; server <c>SM_TFW_KEEP_HUMAN=1</c>): the same short fight for each clip of the sound A/B. The scripted player deploys
/// on lane B, walks towards the first position until a crowd is within 22 m, then: <b>heavy</b>, a frag throw into the crowd, 5 s of fire at the nearest Orks with a second throw halfway,
/// 3 s to hear the tails; <b>small</b>, into the crowd until 2 Orks stand within 7 m (at most 8 s), four swings (light, light, stun strike, light), a 1.5 s burst, 3 s of tail; and quits.
/// Every press goes through the real actions. A <c>SOUNDCAP</c> line marks each part with the process frame, so a Movie Maker clip (one frame a frame) can be cut at the same points.
/// </summary>
public partial class NetworkView
{
    void SoundCapMark(string part) => GD.Print($"SOUNDCAP {part} t {_connectedFor:F2} frame {Engine.GetProcessFrames()} grenades {_client?.Predicted.Grenades}");

    async Task TfwSoundCapture()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(1.0);
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "walked towards the Orks", 45.0, () => _musouState.MiddleNear(client.Predicted.Position, 22f) is not null);
        int Near(float within)
        {
            int n = 0;
            var me = client.Predicted.Position;
            foreach (var e in _tfwShown) if (e.State != MusouEnemyState.Dying && Num.Vector3.Distance(e.Position, me) < within) n++;
            return n;
        }

        SoundCapMark("heavy");
        TfwFaceCrowd(30f);
        await TfwActionTap(GameAction.Grenade);
        await TfwWait(0.5);
        TfwAction(GameAction.Fire, true);
        bool second = false;
        for (double start = _connectedFor, until = start + 5.0; _connectedFor < until;)
        {
            TfwAimNearest(30f);
            await TfwWait(0.15);
            if (!second && _connectedFor - start > 2.5)
            {
                second = true;
                TfwAction(GameAction.Fire, false);
                TfwFaceCrowd(30f);
                await TfwActionTap(GameAction.Grenade);
                await TfwWait(0.3);
                TfwAction(GameAction.Fire, true);
            }
        }
        TfwAction(GameAction.Fire, false);
        SoundCapMark("heavy tail");
        await TfwWait(3.0);

        SoundCapMark("small");
        for (int run = 0; run < 3 && Near(7f) < 2; run++)
        {
            var goal = _musouState.MiddleNear(client.Predicted.Position, 30f) ?? point;
            await TfwPathTo(goal, $"walked into the crowd ({run + 1})", 8.0 / 3, () => Near(7f) >= 2);
        }
        GD.Print($"SOUNDCAP Orks within 7 m: {Near(7f)}");
        for (int k = 0; k < 4; k++)
        {
            TfwAimNearest(8f);
            await TfwActionTap(k == 2 ? GameAction.StunStrike : GameAction.Melee);
            await TfwWait(k == 2 ? 1.1 : 0.55);
        }
        await TfwWait(0.4);
        TfwAction(GameAction.Fire, true);
        for (double until = _connectedFor + 1.5; _connectedFor < until;) { TfwAimNearest(25f); await TfwWait(0.15); }
        TfwAction(GameAction.Fire, false);
        SoundCapMark("small tail");
        await TfwWait(3.0);
        SoundCapMark("end");
        GetTree().Quit(0);
    }
}
