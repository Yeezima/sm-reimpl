using System;
using System.Collections.Generic;
using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid <c>--mek-gallery [--out file.png]</c> (in the model viewer; needs the install): expansion S4's contraptions as the networked view draws them (<see cref="MekModels"/>,
/// <see cref="NetworkView.OriginalBody"/>) on a grey floor: the shield generator under its dome, the beacon, the turret, the trap, the turret's and pylon's wrecks, and a drop's rok in the air. A PNG
/// (default: the private cache's previews\musou-meks\gallery.png), then it quits.
/// </summary>
public sealed partial class MekGalleryView : Node3D
{
    readonly MekModels _models;
    readonly string _out;
    int _frames = -1;

    public MekGalleryView(string gameDir, string? output)
    {
        _models = new MekModels(gameDir);
        _out = output ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "cache", "previews", "musou-meks", "gallery.png");
    }

    public override void _Process(double delta)
    {
        if (_frames < 0)
        {
            if (!_models.Done) { _models.Step(); foreach (var n in _models.Notes) GD.Print(n); _models.Notes.Clear(); return; }
            Build();
            _frames = 0;
            return;
        }
        if (++_frames < 20) return;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_out)!);
        GetViewport().GetTexture().GetImage().SavePng(_out);
        GD.Print("mek gallery -> " + _out);
        GetTree().Quit();
    }

    void Build()
    {
        var game = new Node3D { Name = "GameSpace", Basis = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)) };
        AddChild(game);
        game.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80f, 80f) }, Basis = new Basis(Vector3.Right, MathF.PI / 2f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.31f, 0.3f), Roughness = 0.95f } });
        void Put(MusouContraptionKind kind, Vector3 at, float yaw = 0f)
        {
            var part = kind switch { MusouContraptionKind.ShieldGenerator => MekModels.ShieldGenerator, MusouContraptionKind.Beacon => MekModels.Beacon, MusouContraptionKind.Turret => MekModels.Turret, _ => MekModels.Trap };
            if (_models.Get(part) is not { } model) { GD.Print($"mek gallery: no {part.Mesh}"); return; }
            var body = NetworkView.OriginalBody(kind, model, _models);
            body.Transform = new Transform3D(new Basis(Vector3.Back, yaw), at);
            game.AddChild(body);
        }
        Put(MusouContraptionKind.ShieldGenerator, new Vector3(-12f, 4f, 0f));
        if (_models.Get(MekModels.Dome) is { } bubble)
        {
            var size = bubble.Bounds.Size;
            float d = MusouMekRules.ShieldRadius * 2f;
            var dome = MekModels.Place(bubble, new Vector3(d / MathF.Max(size.X, 1e-3f), d / MathF.Max(size.Y, 1e-3f), d / MathF.Max(size.Z, 1e-3f)), false, "Dome");
            var texture = (bubble.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D)?.AlbedoTexture;
            foreach (var child in dome.GetChildren()) if (child is MeshInstance3D shell) shell.MaterialOverride = NetworkView.DomeMaterial(texture);
            dome.Position = new Vector3(-12f, 4f, 0f);
            game.AddChild(dome);
        }
        Put(MusouContraptionKind.Beacon, new Vector3(1f, 0f, 0f));
        Put(MusouContraptionKind.Turret, new Vector3(5f, 0f, 0f), -MathF.PI / 2f);   // aimed at the camera's right (+X)
        Put(MusouContraptionKind.Trap, new Vector3(10f, 0f, 0f));
        foreach (var (part, at) in new[] { (MekModels.TurretWreck, new Vector3(5f, 7f, 0f)), (MekModels.ShieldWreck, new Vector3(-3f, 7f, 0f)) })
            if (_models.Get(part) is { } wreck)
            {
                var healthy = _models.Get(part == MekModels.TurretWreck ? MekModels.Turret : MekModels.ShieldGenerator);
                float h = part == MekModels.TurretWreck ? 1.7f : NetworkView.ShieldModelHeight;
                var node = MekModels.Place(wreck, Vector3.One * (healthy is null ? 1f : MekModels.ToHeight(healthy, h)), true, part.Mesh);
                node.Position += at;
                game.AddChild(node);
            }
        if (_models.Get(MekModels.Rok) is { } rok)
        {
            var node = MekModels.Place(rok, Vector3.One * MekModels.ToWidth(rok, 2.5f), false, "Rok");
            node.Position += new Vector3(15f, 4f, 7f);
            game.AddChild(node);
        }
        AddChild(new DirectionalLight3D { Rotation = new Vector3(-0.9f, 0.6f, 0f), LightEnergy = 1.4f, ShadowEnabled = true });
        AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.2f, 0.22f, 0.26f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.55f, 0.6f), AmbientLightEnergy = 0.8f } });
        var camera = new Camera3D { Fov = 60f, Current = true };
        AddChild(camera);
        camera.LookAtFromPosition(new Vector3(2f, 5.5f, 13f), new Vector3(2f, 1.2f, -1f));   // Godot axes: game (2, 13 behind, 5.5 up) looking at the row
    }
}
