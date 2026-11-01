using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Draws the hub, the inside of the campaign Thunderhawk (owner's item 29): the original model <c>thunderhawk_interior</c> read from the singleplayer chunk <see cref="TfwShip.Chunk"/> of the player's
/// install, every surface with its original diffuse map (<i>original asset loaded</i>), the rear door posed open on its hinge (<see cref="TfwShip.DoorOpen"/>) and lying flat as the ramp, with warm work lights
/// and the planet's daylight added (ours: the interior is dark). The collision is not the model's (the chunk has none): it is <see cref="TfwShip"/>'s generated shell, the same on the server, and the
/// only thing drawn when the chunk cannot be read (<i>placeholder demonstration</i>). The two stations are marked by a pad on the floor and a coloured light at the prop they use (the hanging monitor, the
/// right tech panel). What lies outside is <see cref="TfwHubSky"/>. Lives in game space (Z up, metres); the battlefield is the core's <c>MapView</c>.
/// </summary>
public partial class TfwMapView : Node3D
{
    static readonly Color DestinationColor = new(0.25f, 0.6f, 1f), EquipmentColor = new(1f, 0.65f, 0.2f), RangeColor = new(0.35f, 1f, 0.45f);

    /// <summary>What the last <see cref="Build"/> drew: surfaces with their original texture of those the model has (28 in the install), 0 for the fallback shell.</summary>
    public static (int Textured, int Surfaces) LastModel { get; private set; }

    /// <summary>The view under the name <c>TfwHub</c>; <paramref name="ground"/>: the flat ground's checker, of which the fallback shell takes a copy; <paramref name="gameDir"/>: the install (null: no model).</summary>
    public static TfwMapView Build(TfwHub hub, StandardMaterial3D ground, string? gameDir, List<string> notes)
    {
        var view = new TfwMapView { Name = "TfwHub" };
        Node3D? ship = null;
        LastModel = (0, 0);
        try { if (gameDir is not null) ship = Thunderhawk(hub, gameDir, notes); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            notes.Add($"hub: the Thunderhawk could not be built ({e.GetType().Name}: {e.Message}); the generated shell is drawn instead");
            ship = null;
        }
        if (ship is null)
        {
            var material = (StandardMaterial3D)ground.Duplicate();
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            material.AlbedoColor = new Color(1f, 0.97f, 0.9f);
            ship = new MeshInstance3D { Name = "Stormbird", Mesh = Shell(hub), MaterialOverride = material };
        }
        view.AddChild(ship);
        view.AddStation(hub.DestinationStation, DestinationColor);
        view.AddStation(hub.EquipmentStation, EquipmentColor);
        view.AddStation(hub.RangeStation, RangeColor);
        view.AddChild(new MeshInstance3D   // the Firing Range's button on the wall: a small lit plate (placeholder demonstration)
        {
            Name = "RangeButton",
            Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.5f, 0.35f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = RangeColor, EmissionEnabled = true, Emission = RangeColor, EmissionEnergyMultiplier = 2f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Position = new Vector3(hub.RangeButton.X, hub.RangeButton.Y, hub.RangeButton.Z),
            Rotation = new Vector3(0f, 0f, hub.ToWorld(new Num.Vector3(1f, 0f, 0f)).X < hub.ToWorld(Num.Vector3.Zero).X ? MathF.PI : 0f),
        });
        view.AddLights(hub);
        var body = new StaticBody3D { Name = "HubRagdollWorld", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = hub.Corners().Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray(), BackfaceCollision = true } });
        view.AddChild(body);
        return view;
    }

    /// <summary>The Firing Range: a flat walled square (generated, placeholder demonstration) with its return pad, and its collision for the ragdolls' world.</summary>
    public static Node3D BuildRange(TfwRange range, StandardMaterial3D ground)
    {
        var view = new TfwMapView { Name = "TfwRange" };
        var material = (StandardMaterial3D)ground.Duplicate();
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        material.AlbedoColor = new Color(0.62f, 0.64f, 0.68f);
        view.AddChild(new MeshInstance3D { Name = "RangeShell", Mesh = Shell(range.Corners()), MaterialOverride = material });
        view.AddStation(range.ReturnStation, RangeColor);
        var body = new StaticBody3D { Name = "RangeRagdollWorld", CollisionLayer = Ragdoll.WorldLayer, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = range.Corners().Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray(), BackfaceCollision = true } });
        view.AddChild(body);
        return view;
    }

    /// <summary>The model as one mesh per block, in the world: each block moved by its object-tree node (the hanging monitor and the release handle are authored elsewhere), the door posed open.</summary>
    static Node3D? Thunderhawk(TfwHub hub, string gameDir, List<string> notes)
    {
        string archive = System.IO.Path.Combine(gameDir, "data", "chunks", TfwShip.Chunk + ".oppc");
        if (!System.IO.File.Exists(archive)) { notes.Add($"hub: {TfwShip.Chunk} is not in this install; the generated shell is drawn instead"); return null; }
        var model = StaticModel.Load(archive, TfwShip.Mesh, notes);
        var tree = ObjectTree.Load(archive, TfwShip.Mesh, notes);
        var node = new Node3D { Name = "Thunderhawk" };
        var offset = hub.ModelToWorld;
        var textures = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
        var placed = new Dictionary<int, Num.Matrix4x4>();
        foreach (var v in tree?.Visuals ?? [])
            if (v.NearLevel && v.MeshId >= 0 && v.MeshId < model.Mesh.Blocks.Count) placed[v.MeshId] = tree!.ModelSpace(v.RefNode);
        int textured = 0, surfaces = 0;
        for (int b = 0; b < model.Mesh.Blocks.Count; b++)
        {
            var block = model.Mesh.Blocks[b];
            if (block.Positions.Length == 0) continue;
            var m = placed.GetValueOrDefault(b, Num.Matrix4x4.Identity);
            if (b == TfwShip.DoorBlock) m *= TfwShip.DoorOpen;
            m *= offset;
            var arrays = BlockArrays(block, m);
            var mesh = new ArrayMesh();
            foreach (var surface in model.Surfaces.Where(s => s.BlockIndex == b))
            {
                int count = surface.Range.IndexCount - surface.Range.IndexCount % 3;
                if (count < 3 || surface.Range.StartIndex + count > block.Indices.Length) continue;
                var indices = new int[count];
                for (int i = 0; i < count; i++) indices[i] = block.Indices[surface.Range.StartIndex + i];
                var one = arrays.Duplicate();
                one[(int)Mesh.ArrayType.Index] = indices;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, one);
                var material = ModelViewer.MaterialFor(surface, textures, notes);
                if (material is StandardMaterial3D standard)
                {
                    standard.Roughness = 0.85f;
                    standard.CullMode = b == TfwShip.DoorBlock ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back;
                }
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
                surfaces++;
                if (surface.DiffuseDds is not null && material is StandardMaterial3D { AlbedoTexture: not null }) textured++;
            }
            if (mesh.GetSurfaceCount() == 0) continue;
            string name = tree?.Visuals.FirstOrDefault(v => v.MeshId == b)?.RefNode ?? $"block{b}";
            node.AddChild(new MeshInstance3D { Name = name, Mesh = mesh });
        }
        model.ReleaseTextures();
        LastModel = (textured, surfaces);
        if (surfaces == 0) { notes.Add("hub: the Thunderhawk model has nothing to draw; the generated shell is drawn instead"); node.Free(); return null; }
        return node;
    }

    /// <summary>One block's vertex arrays in game space (Z up), moved by <paramref name="m"/>; the surfaces of a block share them and differ by their index lists.</summary>
    static Godot.Collections.Array BlockArrays(SmFormats.Dcm.DcmBlock block, Num.Matrix4x4 m)
    {
        int n = block.Positions.Length;
        var vertices = new Vector3[n];
        var normals = new Vector3[n];
        var uv = new Vector2[n];
        for (int v = 0; v < n; v++)
        {
            var p = Num.Vector3.Transform(block.Positions[v], m);
            vertices[v] = new Vector3(p.X, p.Y, p.Z);
            if (block.Normals.Length == n)
            {
                var q = Num.Vector3.Normalize(Num.Vector3.TransformNormal(block.Normals[v], m));
                normals[v] = new Vector3(q.X, q.Y, q.Z);
            }
            if (block.Uv0.Length == n) uv[v] = new Vector2(block.Uv0[v].X, block.Uv0[v].Y);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        if (block.Normals.Length == n) arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        return arrays;
    }

    /// <summary>Warm work lights down the corridor, the planet's daylight at the ramp, and a coloured light at each station's prop (ours: the model is dark and has no lights of its own).</summary>
    void AddLights(TfwHub hub)
    {
        var warm = new Color(1f, 0.78f, 0.52f);
        int k = 0;
        foreach (float y in new[] { -3f, 3.5f, 8f, 12.5f, 17f, 21f })
            AddChild(new OmniLight3D { Name = "WorkLight" + k++, Position = ToVector(hub.ToWorld(new Num.Vector3(0f, y, 5.1f))), OmniRange = 11f, LightEnergy = 2.4f, LightColor = warm, OmniAttenuation = 1.2f });
        AddChild(new OmniLight3D
        {
            Name = "PlanetLight", Position = ToVector(hub.ToWorld(new Num.Vector3(0f, 25.5f, 4.4f))), OmniRange = 18f, LightEnergy = 1.7f, LightColor = new Color(0.72f, 0.84f, 1f),
        });
        AddChild(new OmniLight3D
        {
            Name = "MonitorLight", Position = ToVector(hub.ToWorld(new Num.Vector3(TfwShip.Monitor.X, 19.0f, 4.0f))), OmniRange = 6f, LightEnergy = 1.6f, LightColor = DestinationColor,
        });
        AddChild(new OmniLight3D
        {
            Name = "PanelLight", Position = ToVector(hub.ToWorld(new Num.Vector3(1.2f, TfwShip.Panel.Y, 3.9f))), OmniRange = 5f, LightEnergy = 1.5f, LightColor = EquipmentColor,
        });
    }

    static Vector3 ToVector(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The boxes as one flat-shaded surface. Godot's front faces wind clockwise, so each outward triangle is written a, c, b.</summary>
    static ArrayMesh Shell(TfwHub hub) => Shell(hub.Corners());

    static ArrayMesh Shell(Num.Vector3[] corners)
    {
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

    /// <summary>A thin pad of the station's radius on the floor: where to stand (the prop it uses is the model's).</summary>
    void AddStation(Num.Vector3 at, Color color)
    {
        // A cylinder's axis is Godot's Y: turned onto the game's Z.
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = TfwMap.StationRadius, BottomRadius = TfwMap.StationRadius, Height = 0.02f, RadialSegments = 32 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(color.R, color.G, color.B, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, DisableReceiveShadows = true,
            },
            Position = new Vector3(at.X, at.Y, at.Z + 0.03f), RotationDegrees = new Vector3(90f, 0f, 0f),
        });
    }
}
