using Godot;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid <c>--wf-guard-cam &lt;height m&gt;</c> (Guardsmen depth, owner 2026-11-20; windowed run on a Warfront lane battle): the camera hangs above the first secured point looking straight down (a little south of it,
/// so up stays defined), for stills and a recording of the Guardsmen's rows (<c>godot.cmd --windowed [--write-movie f.avi --fixed-fps 30] -- --connect &lt;h:p&gt; --content warfront --warfront-ui-demo --wf-guard-cam 55</c>).
/// It reuses the swarm shots' camera hold (<c>NetworkView.TfwSwarmCam.cs</c>); the camera alone moves, nothing is sent, and it is off without the flag.
/// </summary>
public partial class NetworkView
{
    ulong _wfFrameAt;
    int _wfFrameN;

    /// <summary>
    /// <c>--wf-guard-frames &lt;dir&gt;</c> (with <c>--wf-guard-cam</c>): a JPG of the rendered view every 0.5 s of wall time once a point is secured, for a clip of the real client (ffmpeg joins them); frame times are the client's own.
    /// </summary>
    void WarfrontGuardFrames()
    {
        if (CmdArg("wf-guard-frames") is not { Length: > 0 } dir || _tfwSwarmCam is null) return;
        ulong now = Time.GetTicksMsec();
        if (now - _wfFrameAt < 500) return;
        _wfFrameAt = now;
        DirAccess.MakeDirRecursiveAbsolute(dir);
        GetViewport().GetTexture().GetImage().SaveJpg($"{dir}/f{_wfFrameN++:0000}.jpg", 0.9f);
    }

    void WarfrontGuardCam()
    {
        if (!WarfrontOn || !CmdFlag("wf-guard-cam")) return;
        WarfrontGuardFrames();
        float height = float.TryParse(CmdArg("wf-guard-cam"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h) && h > 5f ? h : 55f;
        // --wf-guard-point <0..2>: hang over that objective whatever its state, so a clip can start before the point is secured and before the Orks arrive (QA, 2026-11-20)
        if (CmdArg("wf-guard-point") is { Length: > 0 } pt && int.TryParse(pt, out int pi) && pi >= 0 && pi < _tfwState.Objectives.Count)
        {
            var po = _tfwState.Objectives[pi];
            // --wf-guard-offset dx,dy (metres, sim axes): the frame's centre moves that far from the point, toward where the clash is
            var shift = Num.Vector3.Zero;
            if (CmdArg("wf-guard-offset") is { Length: > 0 } off && off.Split(',') is { Length: 2 } xy
                && float.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dx)
                && float.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dy)) shift = new Num.Vector3(dx, dy, 0f);
            _tfwSwarmCam = (TfwGodot(po.Position + shift + new Num.Vector3(0f, -height * 0.25f, height)), TfwGodot(po.Position + shift));
            return;
        }
        foreach (var o in _tfwState.Objectives)
        {
            if (o.State != TfwPositionState.Secured) continue;
            _tfwSwarmCam = (TfwGodot(o.Position + new Num.Vector3(0f, -height * 0.25f, height)), TfwGodot(o.Position));
            return;
        }
    }
}
