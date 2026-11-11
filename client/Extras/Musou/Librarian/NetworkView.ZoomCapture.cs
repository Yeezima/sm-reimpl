using System.Threading.Tasks;
using Godot;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid <c>--tfw-zoom-capture</c> (the Librarian's zoom, owner 2026-11-12: "he should have the same zoom as the other classes"; with <c>--class "sm - librarian"</c> or any other class, <c>--content musou</c>, before
/// <c>--tfw-test &lt;dir&gt;</c>, a <c>--windowed</c> run for the PNGs; server <c>SM_TFW_KEEP_HUMAN=1</c>): deploys on lane B, stands still, takes a still unzoomed (<c>zoom-00-hip</c>), holds the Aim action 1.5 s with a
/// <c>ZOOMCAPTURE</c> line each few frames (the camera's field of view, the predicted <c>Aiming</c>), takes a still zoomed (<c>zoom-01-aim</c>), lets go and logs the way back (<c>zoom-02-back</c>). Quits 0.
/// </summary>
public partial class NetworkView
{
    async Task TfwZoomCapture()
    {
        var client = _client!;
        if (!await AutocannonDeploy()) return;
        await TfwWait(1.0);
        await TfwShot("zoom-00-hip");
        string cls = _config is { } c && Loadouts.ClassOf(c, client.Predicted.Class) is { } k ? k.Name : "?";
        GD.Print($"ZOOMCAPTURE class {cls}: unzoomed fov {_camera!.Fov:F1}, aiming {client.Predicted.Aiming}");
        TfwAction(GameAction.Aim, true);
        double begun = _connectedFor;
        bool shot = false;
        while (_connectedFor < begun + 1.5)
        {
            await TfwFrames(6);
            GD.Print($"ZOOMCAPTURE t +{_connectedFor - begun:F2} s: fov {_camera.Fov:F1}, predicted aiming {client.Predicted.Aiming}");
            if (!shot && _connectedFor > begun + 1.2) { shot = true; await TfwShot("zoom-01-aim"); }
        }
        TfwCheck("the Aim key zooms the class", client.Predicted.Aiming && _camera.Fov < 40f, $"aiming {client.Predicted.Aiming}, fov {_camera.Fov:F1}");
        TfwAction(GameAction.Aim, false);
        begun = _connectedFor;
        while (_connectedFor < begun + 0.8)
        {
            await TfwFrames(6);
            GD.Print($"ZOOMCAPTURE back t +{_connectedFor - begun:F2} s: fov {_camera.Fov:F1}, predicted aiming {client.Predicted.Aiming}");
        }
        await TfwShot("zoom-02-back");
        GetTree().Quit(0);
    }
}
