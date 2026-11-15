using System.Threading.Tasks;
using Godot;

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
}
