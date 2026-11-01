using System.Linq;
using Godot;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Draws the Stormbird from <see cref="TfwHub.Solids"/>, so what is seen is the collision (TF1, M1): the shell with its two ceiling lights and the two station consoles with a pad of their radius
/// (drawn only: no collision, the map's hash does not hold them), plus a ragdoll floor and walls. Placeholder demonstration, nothing from the install. Lives in game space (Z up, metres); the
/// battlefield is the core's <c>MapView</c>.
/// </summary>
public partial class TfwMapView : Node3D
{
    static readonly Color DestinationColor = new(0.25f, 0.6f, 1f), EquipmentColor = new(1f, 0.65f, 0.2f);

    /// <summary>The view under the name <c>TfwHub</c>; <paramref name="ground"/>: the flat ground's checker, of which the shell takes a copy.</summary>
    public static TfwMapView Build(TfwHub hub, StandardMaterial3D ground)
    {
        var view = new TfwMapView { Name = "TfwHub" };
        var material = (StandardMaterial3D)ground.Duplicate();
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        material.AlbedoColor = new Color(1f, 0.97f, 0.9f);
        view.AddChild(new MeshInstance3D { Name = "Stormbird", Mesh = Shell(hub), MaterialOverride = material });
        view.AddStation(hub.DestinationStation, DestinationColor);
        view.AddStation(hub.EquipmentStation, EquipmentColor);
        float z = hub.Centre.Z + TfwMap.StormbirdSize.Z - 0.4f;
        foreach (float y in new[] { -2.5f, 2.5f })
            view.AddChild(new OmniLight3D
            {
                Position = new Vector3(hub.Centre.X, hub.Centre.Y + y, z), OmniRange = 12f, LightEnergy = 8f, LightColor = new Color(1f, 0.93f, 0.82f),
            });
        var body = new StaticBody3D { Name = "HubRagdollWorld", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = hub.Corners().Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray(), BackfaceCollision = true } });
        view.AddChild(body);
        return view;
    }

    /// <summary>The boxes as one flat-shaded surface. Godot's front faces wind clockwise, so each outward triangle is written a, c, b.</summary>
    static ArrayMesh Shell(TfwHub hub)
    {
        var corners = hub.Corners();
        var vertices = new Vector3[corners.Length];
        var normals = new Vector3[corners.Length];
        for (int t = 0; t + 2 < corners.Length; t += 3)
        {
            Num.Vector3 a = corners[t], b = corners[t + 1], c = corners[t + 2];
            var n = Num.Vector3.Normalize(Num.Vector3.Cross(b - a, c - a));
            vertices[t] = new(a.X, a.Y, a.Z); vertices[t + 1] = new(c.X, c.Y, c.Z); vertices[t + 2] = new(b.X, b.Y, b.Z);
            normals[t] = normals[t + 1] = normals[t + 2] = new(n.X, n.Y, n.Z);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    void AddStation(Num.Vector3 at, Color color)
    {
        var glow = new StandardMaterial3D { AlbedoColor = color, EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = 0.8f };
        const float Tall = 1.2f;
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.8f, 0.8f, Tall) }, MaterialOverride = glow, Position = new Vector3(at.X, at.Y, at.Z + Tall / 2f),
        });
        // A cylinder's axis is Godot's Y: turned onto the game's Z.
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = TfwMap.StationRadius, BottomRadius = TfwMap.StationRadius, Height = 0.02f, RadialSegments = 32 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color.Darkened(0.5f) },
            Position = new Vector3(at.X, at.Y, at.Z + 0.01f), RotationDegrees = new Vector3(90f, 0f, 0f),
        });
    }
}
