using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The Three-Front War on the client (TF0: the contract and stubs). <see cref="TfwLoadMap"/> answers for the generated map, whatever this client's profile (a client of another
/// profile is then refused by the config check, which names the server's profile): the collision from <see cref="TfwMap.Build"/>, a flat checker slab (placeholder demonstration,
/// nothing from the install) and the warm-ups of a full load. TF1 draws the real shell; TF7 fills <see cref="TfwFrame"/>.
/// </summary>
public partial class NetworkView
{
    /// <summary>Once a frame, with this frame's sections of tags 7 to 10 (the dispatcher <c>NetworkView.Extras.cs</c>). TF0: nothing yet.</summary>
    void TfwFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, List<ExtrasSection> sections) { }

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
        if (_gameSpace is not null && _groundMaterial is not null) _gameSpace.AddChild(TfwStubFloor());
        if (GetNodeOrNull<Node3D>("Ground") is { } ground) ground.Visible = false;
        _warmClock = System.Diagnostics.Stopwatch.StartNew();
        WarmLooks();
        WarmOrk();
        PrewarmWeaponDefaults();
        PrewarmPickups();
        PrewarmCollision(world);
        _mapNote = $"map {name}: generated stub, a flat placeholder slab (placeholder demonstration, nothing from the install)";
        GD.Print($"{_mapNote}; total {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The stub map's slab in game coordinates (the game space turns them), checkered like the flat ground.</summary>
    MeshInstance3D TfwStubFloor()
    {
        Vector2 lo = new(TfwMap.StubFloorMin.X, TfwMap.StubFloorMin.Y), hi = new(TfwMap.StubFloorMax.X, TfwMap.StubFloorMax.Y);
        Vector3[] vertices = [new(lo.X, lo.Y, 0f), new(hi.X, hi.Y, 0f), new(hi.X, lo.Y, 0f), new(lo.X, lo.Y, 0f), new(lo.X, hi.Y, 0f), new(hi.X, hi.Y, 0f)];
        var normals = new Vector3[vertices.Length];
        System.Array.Fill(normals, new Vector3(0f, 0f, 1f));
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var material = (StandardMaterial3D)_groundMaterial!.Duplicate();
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        return new MeshInstance3D { Name = "MapVisuals", Mesh = mesh, MaterialOverride = material };
    }
}
