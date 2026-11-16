using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SmContent;

namespace SmMapmakerEditor;

/// <summary>
/// Library thumbnails: each item is rendered once in a small SubViewport and cached as a PNG under <c>%LOCALAPPDATA%\sm-reimpl\mapmaker\thumbs</c> (private,
/// outside the repository; the owner asked for it). A render of an SM1 item is a picture, not the mesh or a texture. Headless runs have no renderer: no
/// thumbnails are made and the list shows a plain icon.
/// </summary>
public partial class Thumbnails : Node
{
    public const int Size = 112;
    sealed record Job(string Key, Func<(Node3D? Node, Aabb Bounds)> Build, Action<Texture2D> Done);

    readonly Queue<Job> _queue = new();
    readonly Dictionary<string, Texture2D> _memory = new();
    bool _busy;

    public static string CacheDir => Path.Combine(GameInstall.DataRoot, "mapmaker", "thumbs");
    public static bool CanRender => DisplayServer.GetName() != "headless";

    static string FileFor(string key)
    {
        var safe = new System.Text.StringBuilder();
        foreach (var c in key) safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        if (safe.Length > 100) safe.Length = 100;
        uint h = 2166136261; foreach (var c in key) h = (h ^ c) * 16777619;   // FNV-1a: keeps different long keys apart
        return Path.Combine(CacheDir, $"{safe}-{h:x8}.png");
    }

    /// <summary>The cached picture, or null.</summary>
    public Texture2D? Cached(string key)
    {
        if (_memory.TryGetValue(key, out var t)) return t;
        var file = FileFor(key);
        try
        {
            if (File.Exists(file))
            {
                var image = Image.LoadFromFile(file);
                if (image is not null) return _memory[key] = ImageTexture.CreateFromImage(image);
            }
        }
        catch (Exception) { /* a damaged cache file is simply rendered again */ }
        return null;
    }

    /// <summary>Renders <paramref name="key"/> when it is not cached; <paramref name="done"/> gets the texture on the main thread.</summary>
    public void Request(string key, Func<(Node3D? Node, Aabb Bounds)> build, Action<Texture2D> done)
    {
        if (!CanRender) return;
        if (Cached(key) is { } t) { done(t); return; }
        _queue.Enqueue(new Job(key, build, done));
        if (!_busy) _ = Pump();
    }

    async System.Threading.Tasks.Task Pump()
    {
        _busy = true;
        while (_queue.Count > 0)
        {
            var job = _queue.Dequeue();
            try
            {
                var (node, bounds) = job.Build();
                if (node is null) continue;
                var image = await Render(node, bounds);
                if (image is null) continue;
                try { Directory.CreateDirectory(CacheDir); image.SavePng(FileFor(job.Key)); } catch (Exception) { /* cache is optional */ }
                var texture = ImageTexture.CreateFromImage(image);
                _memory[job.Key] = texture;
                job.Done(texture);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { GD.PushWarning($"Mapmaker thumbnail '{job.Key}': {e.Message}"); }
        }
        _busy = false;
    }

    async System.Threading.Tasks.Task<Image?> Render(Node3D node, Aabb bounds)
    {
        var viewport = new SubViewport
        {
            Size = new Vector2I(Size, Size), OwnWorld3D = true, TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once, Msaa3D = Viewport.Msaa.Msaa2X,
        };
        var world = new Node3D();
        viewport.AddChild(world);
        world.AddChild(node);
        float radius = Mathf.Max(bounds.Size.Length() * 0.5f, 0.05f);
        var centre = bounds.GetCenter();
        var camera = new Camera3D { Fov = 35f, Near = radius * 0.05f, Far = radius * 20f };
        world.AddChild(camera);
        var direction = new Vector3(0.7f, 0.55f, 0.9f).Normalized();
        camera.LookAtFromPosition(centre + direction * (radius / Mathf.Sin(Mathf.DegToRad(17.5f)) * 1.05f), centre, Vector3.Up);
        var sun = new DirectionalLight3D { LightEnergy = 1.4f };
        world.AddChild(sun);
        sun.LookAtFromPosition(new Vector3(2, 4, 3), Vector3.Zero, Vector3.Up);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.17f, 0.18f, 0.2f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.65f, 0.67f, 0.72f), AmbientLightEnergy = 0.8f,
        };
        world.AddChild(new WorldEnvironment { Environment = env });
        AddChild(viewport);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = viewport.GetTexture().GetImage();
        viewport.QueueFree();
        return image;
    }
}
