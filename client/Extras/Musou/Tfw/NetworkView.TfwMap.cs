using System.Linq;
using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The war's map on the client (TF1, M1). The core's <see cref="LoadMap"/> reads Manufactorum from the install as any map (<see cref="TfwMapFile"/> names it, so its visuals, lighting, effects and
/// sounds load unchanged); <see cref="TfwMapCollision"/> then adds the Stormbird the server adds, gives the hash the handshake compares and draws the room. This answers whatever this client's
/// profile: a client of another profile is refused by the config check, which names the server's profile.
/// </summary>
public partial class NetworkView
{
    /// <summary>The Stormbird of this map (null until the map is loaded, and on any other map).</summary>
    internal TfwHub? TfwHub { get; private set; }

    TfwMapView? _tfwHubView;

    void TfwMapFile(ref string file)
    {
        if (file != TfwMap.Name) return;
        if (!MapContent.Available(_gameDir).Contains(TfwMap.Battlefield)) throw new System.IO.FileNotFoundException(TfwWorld.Missing("the map is not in this install"));
        file = TfwMap.Battlefield;
    }

    void TfwMapCollision(string name, ref CollisionWorld world, ref ulong hash)
    {
        if (name != TfwMap.Name) return;
        var (composed, hub, composedHash) = TfwMap.WithHub(world);
        world = composed;
        hash = composedHash;
        TfwHub = hub;
        GD.Print($"map {name}: {TfwMap.Battlefield} from the install and the Stormbird at {hub.Centre}; map hash {hash:X16}");
        if (Lean) return;
        _gameSpace?.GetNodeOrNull("TfwHub")?.QueueFree();
        if (_gameSpace is null || _groundMaterial is null) return;
        _tfwHubView = TfwMapView.Build(hub, _groundMaterial);
        _gameSpace.AddChild(_tfwHubView);
    }

    /// <summary>The Stormbird is drawn only while the player can be in it: from the battlefield it would hang in the sky beyond the level, its lights among the map's.</summary>
    void TfwHubFrame(in TfwWarView war)
    {
        if (_tfwHubView is not null && IsInstanceValid(_tfwHubView)) _tfwHubView.Visible = !TfwHudModel.InBattle(war.Phase);
    }
}
