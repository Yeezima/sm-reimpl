using System;
using System.Threading.Tasks;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// plasma-cannon-shots test aid <c>--tfw-plasma-capture</c> (with <c>--content musou --class "sm - devastator" --primary "plasma cannon"</c>, a windowed Movie Maker run; serve <c>SM_TFW_KEEP_HUMAN=1</c>):
/// Ashfall's open ground as the sky stand-in for the owner's references. Deploys on lane B, aims <c>--plasma-pitch</c> degrees up (default 12), fires a burst of normal shots (taps, 0.5 s apart), lets the
/// weapon cool, then holds Fire (the charge fires by itself at its firing stage). <c>PLASMACAPTURE</c> lines carry the marks to cut by. Quits 0.
/// </summary>
public partial class NetworkView
{
    async Task TfwPlasmaCapture()
    {
        var client = _client!;
        if (!await AutocannonDeploy()) return;
        await TfwWait(1.5);
        float pitchDeg = CmdFloat("plasma-pitch") is var p && float.IsFinite(p) ? p : 12f;
        _pitch = pitchDeg * MathF.PI / 180f;
        string held = _config is { } c && Loadouts.InHand(c, client.Predicted) is { } w ? w.Name : "?";
        GD.Print($"PLASMACAPTURE in hand: {held}");
        await TfwWait(1.0);
        GD.Print($"PLASMACAPTURE mark normal t {_connectedFor:F3}");
        for (int i = 0; i < 5; i++) { TfwAction(GameAction.Fire, true); await TfwWait(0.1); TfwAction(GameAction.Fire, false); await TfwWait(0.5); }
        await TfwWait(5.0);
        GD.Print($"PLASMACAPTURE mark charged t {_connectedFor:F3} heat {client.Predicted.Heat:F1} clip {client.Predicted.Clip}");
        TfwAction(GameAction.Fire, true);
        await TfwWait(4.5);
        GD.Print($"PLASMACAPTURE mark released-by-itself t {_connectedFor:F3} charging {client.Predicted.Charging} clip {client.Predicted.Clip}");
        TfwAction(GameAction.Fire, false);
        await TfwWait(4.0);
        GD.Print($"PLASMACAPTURE mark done t {_connectedFor:F3}");
        GetTree().Quit(0);
    }
}
