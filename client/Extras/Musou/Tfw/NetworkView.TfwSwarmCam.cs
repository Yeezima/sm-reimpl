using System.Threading.Tasks;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid <c>--tfw-swarm-shots</c> (with <c>--tfw-test-fight --tfw-test &lt;dir&gt;</c> in a <c>--windowed</c> run; Musou Mode's packs, 2026-11-06): beside each of the fight's PNGs, overhead PNGs
/// (<c>swarm-NN</c> above the player, <c>pool-A-NN</c> to <c>pool-C-NN</c> above each point still sending, where the crowds and musters stand). The camera alone moves for the few frames of a shot; nothing
/// is sent. <c>--tfw-swarm-height m</c> (default 45).
/// </summary>
public partial class NetworkView
{
    (Vector3 Eye, Vector3 Look)? _tfwSwarmCam;

    /// <summary>Holds the camera above a place for a shot (after the view's own camera this frame).</summary>
    void TfwSwarmCamFrame()
    {
        if (_tfwSwarmCam is not { } cam || _camera is not { } camera) return;
        camera.GlobalTransform = Transform3D.Identity.Translated(cam.Eye).LookingAt(cam.Look, Vector3.Up);
    }

    static Vector3 TfwGodot(Num.Vector3 p) => new(p.X, p.Z, -p.Y);   // game space to Godot's

    /// <summary>One overhead PNG of <paramref name="at"/> (game space).</summary>
    async Task TfwOverhead(string name, Num.Vector3 at)
    {
        float height = float.TryParse(CmdArg("tfw-swarm-height"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h) ? h : 45f;
        _tfwSwarmCam = (TfwGodot(at + new Num.Vector3(0f, -height * 0.25f, height)), TfwGodot(at));   // a little south of straight down, so "up" stays defined
        await TfwFrames(8);
        await TfwShot(name);
        _tfwSwarmCam = null;
    }

    /// <summary>The fight's overhead shots: above the player, then above each point still Held (its crowd and its muster).</summary>
    async Task TfwSwarmShots(int shot)
    {
        if (!CmdFlag("tfw-swarm-shots") || _client is not { } client) return;
        if (CmdFlag("tfw-fight-capture"))   // capture banners (2026-11-10): above the point being captured only, its banner and circle in the middle
        {
            int near = TfwMarkerRules.OnPosition(_tfwState.Objectives, client.Predicted.Position);
            if (near >= 0) await TfwOverhead($"capture-{shot:00}", _tfwState.Objectives[near].Position);
            return;
        }
        await TfwOverhead($"swarm-{shot:00}", client.Predicted.Position);
        for (int k = 0; k < TfwBattle.CommanderObjective && k < _tfwState.Objectives.Count; k++)
            if (_tfwState.Objectives[k].State == TfwPositionState.Held) await TfwOverhead($"pool-{(char)('A' + k)}-{shot:00}", _tfwState.Objectives[k].Position);
    }
}
