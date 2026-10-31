using System.Linq;
using Godot;
using SmNet;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The war's map on the client (TF1). <see cref="TfwLoadMap"/> answers for the generated map, whatever this client's profile (a client of another profile is then refused by the config
/// check, which names the server's profile): the collision from <see cref="TfwMap.Build"/>, the same map the server hashes; the arena and Stormbird drawn from it
/// (<see cref="TfwMapView"/>, placeholder demonstration, nothing from the install); the ragdolls' world on its walls and floors; the warm-ups of a full load.
/// </summary>
public partial class NetworkView
{
    void TfwLoadMap(string name, ref LoadedMap? loaded)
    {
        if (name != TfwMap.Name) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var data = TfwMap.Build();
        var world = data.Collision;
        _jumpGround = world;   // M45: the flight pose's height above the ground
        _mapCentre = new Vector2(TfwMap.Position2.X, TfwMap.Position2.Y);
        loaded = new LoadedMap(world, data.Hash);
        if (Lean)
        {
            _mapNote = $"map {name}: generated collision only (lean bot)";
            GD.Print(_mapNote);
            return;
        }
        _gameSpace?.GetNodeOrNull("MapCollision")?.QueueFree();
        _gameSpace?.GetNodeOrNull("MapVisuals")?.QueueFree();
        if (_gameSpace is not null && _groundMaterial is not null) _gameSpace.AddChild(TfwMapView.Build(_groundMaterial));
        if (GetNodeOrNull<Node3D>("Ground") is { } ground) ground.Visible = false;
        TfwRagdollWorld();
        _warmClock = System.Diagnostics.Stopwatch.StartNew();
        WarmLooks();
        WarmOrk();
        PrewarmWeaponDefaults();
        PrewarmPickups();
        PrewarmCollision(world);
        _mapNote = $"map {name}: generated arena and Stormbird (placeholder demonstration, nothing from the install)";
        GD.Print($"{_mapNote}; total {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The ragdolls' world on the map's solids, as <see cref="BuildRagdollWorld"/> does for a map of the install (the flat fallback would let a body fall through a wall).</summary>
    void TfwRagdollWorld()
    {
        _ragdollWorld?.QueueFree();
        _ragdollWorld = null;
        if (_gameSpace is null) return;
        var faces = TfwMap.Solids.SelectMany(TfwMap.Triangles).Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
        Ragdoll.LostBelowY = (_gameSpace.Transform * new Vector3(0f, 0f, -TfwMap.WallThickness - 50f)).Y;
        var body = new StaticBody3D { Name = "RagdollWorld", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces, BackfaceCollision = true } });
        _gameSpace.AddChild(body);
        _ragdollWorld = body;
        _ragdollWorldNote = $"the war map's solids, {faces.Length / 3:N0} triangles";
        GD.Print("ragdoll world: " + _ragdollWorldNote);
    }
}
