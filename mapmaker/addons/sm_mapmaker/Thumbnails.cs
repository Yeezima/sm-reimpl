using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SmContent;

namespace SmMapmakerEditor;

/// <summary>
/// Library thumbnails: each item is rendered once in a small SubViewport and cached as a PNG under <c>%LOCALAPPDATA%\sm-reimpl\mapmaker\thumbs</c> (private,
/// outside the repository; the owner asked for it). A render of an SM1 item is a picture, not the mesh or a texture. Headless runs have no renderer: no
/// thumbnails are made and the list shows a plain icon. An item that cannot be drawn gets a placeholder tile that names the reason.
/// </summary>
public partial class Thumbnails : Node
{
    public const int Size = 112;
    /// <summary>Bump when the picture's look changes; it is part of every cache file name.</summary>
    public const int RenderVersion = 2;

    /// <summary>What a job's builder returns: the node and its box, or a reason there is nothing to draw (then a placeholder icon is shown).</summary>
    public readonly record struct ThumbBuild(Node3D? Node, Aabb Bounds, string? Failure = null);

    sealed record Job(string Key, string Signature, Func<ThumbBuild> Build, Action<Texture2D, string?> Done);

    readonly LinkedList<Job> _queue = new();
    readonly HashSet<string> _pending = new();
    readonly Dictionary<string, Texture2D> _memory = new();
    readonly HashSet<string> _logged = new();
    readonly Dictionary<string, string> _latest = new();   // key -> the memory entry in use, so an older picture of changed content is dropped

    void Remember(string key, string mem, Texture2D tex)
    {
        if (_latest.TryGetValue(key, out var old) && old != mem) _memory.Remove(old);
        _latest[key] = mem; _memory[mem] = tex;
    }
    bool _busy;

    public int Pending => _queue.Count + (_busy ? 1 : 0);

    public static string CacheDir => Path.Combine(GameInstall.DataRoot, "mapmaker", "thumbs");
    public static bool CanRender => DisplayServer.GetName() != "headless";

    static string Safe(string key)
    {
        var safe = new System.Text.StringBuilder();
        foreach (var c in key) safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        if (safe.Length > 100) safe.Length = 100;
        uint h = 2166136261; foreach (var c in key) h = (h ^ c) * 16777619;   // FNV-1a: keeps different long keys apart
        return $"{safe}-{h:x8}";
    }

    static string Stamp(string signature) => $"v{RenderVersion}-{Safe(signature)[^8..]}";
    static string FileFor(string key, string signature) => Path.Combine(CacheDir, $"{Safe(key)}.{Stamp(signature)}.png");

    /// <summary>The cached picture for this content, or null. <paramref name="signature"/> changes when the content does (an asset's hash, a prefab's text).</summary>
    public Texture2D? Cached(string key, string signature = "")
    {
        string mem = key + "|" + Stamp(signature);
        if (_memory.TryGetValue(mem, out var t)) return t;
        var file = FileFor(key, signature);
        try
        {
            if (File.Exists(file) && Image.LoadFromFile(file) is { } image) { var tex = ImageTexture.CreateFromImage(image); Remember(key, mem, tex); return tex; }
        }
        catch (Exception) { /* a damaged cache file is simply rendered again */ }
        return null;
    }

    /// <summary>Drops the jobs that have not started (the list changed); the one in flight finishes.</summary>
    public void ClearPending() { _queue.Clear(); _pending.Clear(); }

    /// <summary>
    /// Renders <paramref name="key"/> when it is not cached; <paramref name="done"/> gets the texture on the main thread, with the reason when it is a
    /// placeholder. <paramref name="urgent"/> jobs (rows in view) go before the background ones.
    /// </summary>
    public void Request(string key, string signature, Func<ThumbBuild> build, Action<Texture2D, string?> done, bool urgent = true)
    {
        if (!CanRender) return;
        if (Cached(key, signature) is { } t) { done(t, null); return; }
        string id = key + "|" + signature;
        if (_pending.Contains(id))
        {
            if (urgent && _queue.FirstOrDefault(j => j.Key + "|" + j.Signature == id) is { } existing) { _queue.Remove(existing); _queue.AddFirst(existing); }
            return;
        }
        _pending.Add(id);
        var job = new Job(key, signature, build, done);
        if (urgent) _queue.AddFirst(job); else _queue.AddLast(job);
        if (!_busy) _ = Pump();
    }

    async System.Threading.Tasks.Task Pump()
    {
        _busy = true;
        try
        {
        while (_queue.Count > 0)
        {
            var job = _queue.First!.Value;
            _queue.RemoveFirst();
            _pending.Remove(job.Key + "|" + job.Signature);
            try
            {
                var built = job.Build();
                string? failure = built.Failure;
                var bounds = built.Bounds;
                if (failure is null && built.Node is null) failure = "no mesh";
                if (failure is null && bounds.Size.LengthSquared() < 1e-10f)
                {
                    bounds = ContentFactory.MeasureBounds(built.Node!);
                    if (bounds.Size.LengthSquared() < 1e-10f) failure = "no mesh";
                }
                if (failure is not null)
                {
                    if (built.Node is { } dead && !dead.IsInsideTree()) dead.Free();
                    var tile = await PlaceholderFor(failure);
                    if (_logged.Add(job.Key + "|" + failure)) GD.Print($"Mapmaker thumbnail '{job.Key}': {failure}");
                    if (tile is not null) job.Done(tile, failure);
                    continue;
                }
                var image = await Render(built.Node!, bounds);
                if (image is null) continue;
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    foreach (var old in Directory.EnumerateFiles(CacheDir, Safe(job.Key) + ".*.png")) File.Delete(old);   // an older picture of changed content
                    image.SavePng(FileFor(job.Key, job.Signature));
                }
                catch (Exception) { /* cache is optional */ }
                var texture = ImageTexture.CreateFromImage(image);
                Remember(job.Key, job.Key + "|" + Stamp(job.Signature), texture);
                job.Done(texture, null);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                if (_logged.Add(job.Key + "|error")) GD.PushWarning($"Mapmaker thumbnail '{job.Key}': {e.Message}");
                if (await PlaceholderFor("render failed") is { } p) job.Done(p, "render failed");
            }
        }
        }
        finally { _busy = false; }
    }

    /// <summary>The picture shown instead of a render: a tile with the reason in words (shared by all rows with that reason, cached on disk).</summary>
    async System.Threading.Tasks.Task<Texture2D?> PlaceholderFor(string reason)
    {
        string mem = "placeholder|" + reason;
        if (_memory.TryGetValue(mem, out var t)) return t;
        var file = FileFor("placeholder-" + reason, "");
        try { if (File.Exists(file) && Image.LoadFromFile(file) is { } cached) return _memory[mem] = ImageTexture.CreateFromImage(cached); } catch (Exception) { }
        var viewport = new SubViewport { Size = new Vector2I(Size, Size), TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Once, Disable3D = true };
        viewport.AddChild(new ColorRect { Color = new Color(0.30f, 0.16f, 0.16f), Size = new Vector2(Size, Size) });
        viewport.AddChild(new ColorRect { Color = new Color(0.17f, 0.18f, 0.2f), Position = new Vector2(4, 4), Size = new Vector2(Size - 8, Size - 8) });
        var mark = new Label { Text = "?", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Position = new Vector2(0, 8), Size = new Vector2(Size, 56) };
        mark.AddThemeFontSizeOverride("font_size", 44); mark.AddThemeColorOverride("font_color", new Color(0.8f, 0.45f, 0.4f));
        var text = new Label { Text = reason, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, Position = new Vector2(6, 66), Size = new Vector2(Size - 12, 38) };
        text.AddThemeFontSizeOverride("font_size", 15); text.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.92f));
        viewport.AddChild(mark); viewport.AddChild(text);
        AddChild(viewport);
        await DrawNow();
        var image = viewport.GetTexture().GetImage();
        viewport.QueueFree();
        try { Directory.CreateDirectory(CacheDir); image.SavePng(file); } catch (Exception) { }
        return _memory[mem] = ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Makes sure the viewport just added is drawn: the editor idles in low-processor mode and then draws no frame (so waiting for FramePostDraw never
    /// returns); a forced draw does not depend on that.
    /// </summary>
    async System.Threading.Tasks.Task DrawNow()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw(false);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw(false);
    }

    /// <summary>A larger picture of one node (test aids and documentation shots); frees the node.</summary>
    public System.Threading.Tasks.Task<Image?> RenderLarge(Node3D node, Aabb bounds, int size) => Render(node, bounds, size);

    async System.Threading.Tasks.Task<Image?> Render(Node3D node, Aabb bounds, int size = Size)
    {
        var viewport = new SubViewport
        {
            Size = new Vector2I(size, size), OwnWorld3D = true, TransparentBg = false,
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
        await DrawNow();
        var image = viewport.GetTexture().GetImage();
        viewport.QueueFree();
        return image;
    }
}
