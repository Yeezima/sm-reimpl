using System.Linq;
using Godot;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Draws the war's map from <see cref="TfwMap.Solids"/>, so what is seen is the collision (TF1): the checker arena, the Stormbird's darker shell with its ceiling lights, and the two
/// station consoles with a pad of their radius (drawn only: no collision, the map's hash does not hold them). Placeholder demonstration, nothing from the install. Lives in game
/// space (Z up, metres).
/// </summary>
public partial class TfwMapView : Node3D
{
    static readonly Color DestinationColor = new(0.25f, 0.6f, 1f), EquipmentColor = new(1f, 0.65f, 0.2f);

    /// <summary>The view under the name <c>MapVisuals</c>; <paramref name="ground"/>: the flat ground's checker, of which the arena takes a copy.</summary>
    public static TfwMapView Build(StandardMaterial3D ground)
    {
        var view = new TfwMapView { Name = "MapVisuals" };
        var arena = (StandardMaterial3D)ground.Duplicate();
        arena.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var hull = (StandardMaterial3D)arena.Duplicate();
        hull.AlbedoColor = new Color(0.55f, 0.6f, 0.68f);
        view.AddChild(new MeshInstance3D { Name = "Arena", Mesh = Solids(false), MaterialOverride = arena });
        view.AddChild(new MeshInstance3D { Name = "Stormbird", Mesh = Solids(true), MaterialOverride = hull });
        view.AddStation(TfwMap.DestinationStation, DestinationColor);
        view.AddStation(TfwMap.EquipmentStation, EquipmentColor);
        float z = TfwMap.StormbirdCentre.Z + TfwMap.StormbirdSize.Z - 0.4f;
        foreach (float y in new[] { -2.5f, 2.5f })
            view.AddChild(new OmniLight3D
            {
                Position = new Vector3(TfwMap.StormbirdCentre.X, TfwMap.StormbirdCentre.Y + y, z), OmniRange = 10f, LightEnergy = 1.6f, LightColor = new Color(0.8f, 0.88f, 1f),
            });
        return view;
    }

    /// <summary>One surface of the boxes of the arena or of the Stormbird, flat-shaded. Godot's front faces wind clockwise, so each outward triangle is written a, c, b.</summary>
    static ArrayMesh Solids(bool stormbird)
    {
        var corners = TfwMap.Solids.Where(s => s.Stormbird == stormbird).SelectMany(TfwMap.Triangles).ToArray();
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
