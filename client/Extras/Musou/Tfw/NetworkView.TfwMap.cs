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
    /// <summary>The Firing Range beside the level (null as above).</summary>
    internal TfwRange? TfwRangeArea { get; private set; }

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
        TfwRangeArea = TfwRange.For(world);
        var (composed, hub, composedHash) = TfwMap.WithHub(world);
        world = composed;
        hash = composedHash;
        TfwHub = hub;
        _tfwAboard = false;   // a new map (a new camera) gets the hub sky again
        GD.Print($"map {name}: {TfwMap.Battlefield} from the install and the Stormbird at {hub.Centre}; map hash {hash:X16}");
        if (Lean) return;
        _gameSpace?.GetNodeOrNull("TfwHub")?.QueueFree();
        if (_gameSpace is null || _groundMaterial is null) return;
        var notes = new System.Collections.Generic.List<string>();
        _tfwHubView = TfwMapView.Build(hub, _groundMaterial, _gameDir, notes);
        foreach (var note in notes) GD.Print("  hub note: " + note);
        GD.Print($"hub: {(TfwMapView.LastModel.Surfaces > 0 ? $"the Thunderhawk's interior from the install, {TfwMapView.LastModel.Textured} of {TfwMapView.LastModel.Surfaces} surfaces with their original texture" : "the generated shell (placeholder demonstration)")}");
        _gameSpace.AddChild(_tfwHubView);
        _gameSpace.GetNodeOrNull("TfwRange")?.QueueFree();
        _gameSpace.AddChild(TfwMapView.BuildRange(TfwRangeArea, _groundMaterial));
    }

    Godot.Environment? _tfwHubEnvironment;
    bool _tfwAboard;

    /// <summary>
    /// The Stormbird is drawn only while the player can be in it: from the battlefield it would hang in the sky beyond the level, its lights among the map's. While the camera is aboard it carries the hub's
    /// own environment (space, the planet, no fog) and the map's sun is off, so nothing of the battlefield's look reaches the ship; leaving puts both back (the map's own environment and sun are never touched).
    /// </summary>
    void TfwHubFrame(in TfwWarView war)
    {
        bool drawn = _tfwHubView is not null && IsInstanceValid(_tfwHubView) && !TfwHudModel.InBattle(war.Phase);
        if (_tfwHubView is not null && IsInstanceValid(_tfwHubView)) _tfwHubView.Visible = drawn;
        bool aboard = drawn && TfwHub is { } hub && _camera is not null
            && (hub.Contains(new System.Numerics.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y)) || (_client is not null && hub.Contains(_client.Predicted.Position)));
        if (_gameSpace?.GetNodeOrNull<Node3D>("TfwRange") is { } rangeNode) rangeNode.Visible = !aboard;   // from the ship the walls would hang in the sky
        if (aboard == _tfwAboard || _camera is null) return;
        _tfwAboard = aboard;
        _camera.Environment = aboard ? _tfwHubEnvironment ??= TfwHubSky.Build() : null;
        if (_sun is not null) _sun.Visible = !aboard;
        GD.Print(aboard ? $"hub: aboard; sky {(TfwHubSky.UsedImage ? "with the owner's planet image" : "with the procedural planet (no assets/musou/planet.jpg)")}" : "hub: left the ship; the map's environment and sun are back");
    }
}
