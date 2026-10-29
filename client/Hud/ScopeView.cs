using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// The scope overlay of the stalker bolter and the lascannon (feel/scope-lascannon; owner, 2026-10-29: "you need to copy over the scoped in overlay that appears when zooming in ...
/// currently it just zooms in a normal camera view"). The original HUD movie's scope sprites (<see cref="ScopeArt"/>: mask with a lens-shaped opening, rings, ticks, the lascannon's red glow;
/// read from the install at runtime, drawn by a software renderer, nothing extracted) are shown over the zoomed camera while aiming with those weapons; the stalker's fades in over the
/// movie's 25-frame "activate" clip (a single alpha ramp the movie gives all its layers: <see cref="ScopeArt.FadeAt"/>), an enemy under the crosshair swaps to the "active" variant, and the
/// normal crosshair is hidden (<see cref="Hud.Frame.Scoped"/>).
/// <para>
/// Cost. Nothing is read or rendered until a player first holds one of the two weapons (<see cref="Prewarm"/>). One low-priority worker thread does everything (the read, then the four renders: idle and
/// active of the kind in hand, at most <see cref="ScopeArt.MaxRenderHeight"/> high, the normal and the additively drawn layer each); the finished pictures are turned into textures one a frame on the main
/// thread, during the prewarm, and their bytes are dropped. A size change waits 0.35 s for the window to settle, cancels the stale renders and keeps the last pictures on screen (scaled by height,
/// centred) until the new ones are ready; there is no plain-zoom gap. Until the first pictures are ready, or when the install lacks the movie, the zoom is the plain view as before.
/// </para>
/// </summary>
public sealed partial class ScopeView : Control
{
    readonly string? _gameDir;
    string _note = "idle";

    public ScopeView(string? gameDir)
    {
        _gameDir = gameDir;
        Name = "ScopeOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;
        _add = new ScopeAddLayer(this);
        AddChild(_add);
        if (gameDir is null) _note = "no install";
    }

    /// <summary>Why nothing is drawn, for the log and the info text.</summary>
    public string Note => _note;

    // ---- the worker ----

    sealed class Lane
    {
        readonly Queue<Action> _q = new();
        readonly object _l = new();
        bool _stop;
        public Lane()
        {
            new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ScopeRender" }.Start();
        }
        public void Post(Action a) { lock (_l) { _q.Enqueue(a); Monitor.Pulse(_l); } }
        public void Stop() { lock (_l) { _stop = true; Monitor.Pulse(_l); } }
        void Run()
        {
            while (true)
            {
                Action a;
                lock (_l)
                {
                    while (_q.Count == 0 && !_stop) Monitor.Wait(_l);
                    if (_stop) return;
                    a = _q.Dequeue();
                }
                try { a(); }
                catch (OperationCanceledException) { }
                catch (Exception e) { GD.PrintErr("scope overlay: " + e.Message); }
            }
        }
    }

    readonly record struct Key(ScopeKind Kind, bool Active);
    sealed class Slot { public ImageTexture? Normal, Add; public int Width, Height; public float Edge; }
    readonly record struct Result(Key Key, ScopePicture Picture, int Generation);

    Lane? _lane;
    volatile ScopeArt? _art;
    volatile bool _artFailed;
    bool _loadPosted;
    CancellationTokenSource _cts = new();
    int _generation;
    readonly ConcurrentQueue<Result> _results = new();
    readonly Dictionary<Key, Slot> _slots = new();
    readonly HashSet<(Key, Vector2I, int)> _requested = new();
    readonly Dictionary<ScopeKind, float[]> _fades = new();
    Vector2I _size, _pendingSize;
    double _pendingSince = -1;
    double _now;

    // ---- what to show (set each frame by the networked view) ----
    ScopeKind? _kind;
    bool _active;
    int _frame;
    double _shownAt = double.NegativeInfinity;

    readonly ScopeAddLayer _add;

    /// <summary>Shows (or keeps showing) the overlay; the networked view calls this every frame while aiming.</summary>
    public void Show(ScopeKind kind, bool active, int frame)
    {
        _kind = kind; _active = active; _frame = frame; _shownAt = _now;
        Request(kind);
        Refresh();
    }

    /// <summary>Stops showing the overlay (the pictures stay for the next time).</summary>
    public new void Hide() { _kind = null; Visible = false; _add.Visible = false; }

    /// <summary>Starts the read and the renders for the weapon in hand (called while it is held, so the first zoom finds them ready).</summary>
    public void Prewarm(ScopeKind kind) => Request(kind);

    Vector2I ViewSize()
    {
        var vs = GetViewportRect().Size;
        return new Vector2I(Math.Max(64, (int)vs.X), Math.Max(64, (int)vs.Y));
    }

    void Request(ScopeKind kind)
    {
        if (_gameDir is null || _artFailed) return;
        var now = ViewSize();
        if (_size == default) _size = _pendingSize = now;
        // A size change is acted on once it has been steady for 0.35 s.
        if (now != _pendingSize) { _pendingSize = now; _pendingSince = _now; }
        else if (_pendingSince >= 0 && now != _size && _now - _pendingSince >= 0.35)
        {
            _size = now; _pendingSince = -1;
            _cts.Cancel(); _cts = new CancellationTokenSource(); _generation++;   // the stale renders stop; the pictures on screen stay until the new ones are built
            _requested.Clear();
        }
        _lane ??= new Lane();
        if (!_loadPosted)
        {
            _loadPosted = true; _note = "loading";
            var dir = _gameDir;
            _lane.Post(() =>
            {
                var art = ScopeArt.Load(dir);
                _art = art;
                if (art is null) { _artFailed = true; _note = "the install has no Hud.gfx"; } else _note = "ready";
            });
        }
        for (int variant = 0; variant < 2; variant++)
        {
            bool active = variant == 1;
            var key = new Key(kind, active);
            if (!_requested.Add((key, _size, _generation))) continue;
            var token = _cts.Token; int gen = _generation; var size = _size;
            _lane.Post(() =>
            {
                var art = _art;
                if (art is null || !art.Has(kind) || token.IsCancellationRequested) return;
                var picture = art.Render(kind, active, size.X, size.Y, token);
                _results.Enqueue(new Result(key, picture, gen));
            });
        }
    }

    /// <summary>Per frame: builds at most one finished picture's textures, hides the overlay when the view stopped asking for it.</summary>
    public override void _Process(double delta)
    {
        _now += delta;
        if (_kind is not null && _now - _shownAt > 0.25) Hide();   // a disconnect or a status path that never calls Hide
        if (_results.TryDequeue(out var r))
        {
            if (r.Generation == _generation)
            {
                var p = r.Picture;
                var slot = _slots.TryGetValue(r.Key, out var old) ? old : _slots[r.Key] = new Slot();
                slot.Normal = ImageTexture.CreateFromImage(Image.CreateFromData(p.Width, p.Height, false, Image.Format.Rgba8, p.Normal));
                slot.Add = ImageTexture.CreateFromImage(Image.CreateFromData(p.Width, p.Height, false, Image.Format.Rgba8, p.Additive));
                slot.Width = p.Width; slot.Height = p.Height; slot.Edge = p.EdgeAlpha;   // the bytes are not kept
                if (_art is { } art && !_fades.ContainsKey(r.Key.Kind))
                {
                    var f = new float[art.FrameCount(r.Key.Kind)];
                    for (int i = 0; i < f.Length; i++) f[i] = art.FadeAt(r.Key.Kind, i);
                    _fades[r.Key.Kind] = f;
                }
            }
        }
        if (_kind is not null) Refresh();
    }

    public override void _ExitTree() { _cts.Cancel(); _lane?.Stop(); }

    /// <summary>The picture for what is shown now, or null until one has been built.</summary>
    Slot? Current(out float fade)
    {
        fade = 1f;
        if (_kind is not { } kind) return null;
        if (_fades.TryGetValue(kind, out var f) && f.Length > 0) fade = f[Math.Clamp(_frame, 0, f.Length - 1)];
        // The variant for the enemy under the crosshair; until it is built, the idle one.
        if (_slots.TryGetValue(new Key(kind, _active), out var slot) && slot.Normal is not null) return slot;
        return _slots.TryGetValue(new Key(kind, false), out var idle) && idle.Normal is not null ? idle : null;
    }

    void Refresh()
    {
        bool on = Current(out _) is not null;
        Visible = on; _add.Visible = on;
        QueueRedraw(); _add.QueueRedraw();
    }

    /// <summary>The picture's rectangle: scaled by height and centred (a screen whose shape changed since the render shows the stage undistorted).</summary>
    static Rect2 Fit(Slot s, Vector2 vs)
    {
        float scale = vs.Y / s.Height;
        float w = s.Width * scale;
        return new Rect2((vs.X - w) / 2f, 0f, w, vs.Y);
    }

    /// <summary>Draws the additive layer onto <paramref name="layer"/> (called by its <c>_Draw</c>).</summary>
    internal void DrawAdditive(CanvasItem layer)
    {
        var slot = Current(out float fade);
        if (slot?.Add is null) return;
        layer.DrawTextureRect(slot.Add, Fit(slot, GetViewportRect().Size), false, new Color(fade, fade, fade, 1f));
    }

    public override void _Draw()
    {
        var slot = Current(out float fade);
        if (slot?.Normal is null) return;
        var vs = GetViewportRect().Size;
        var rect = Fit(slot, vs);
        // Beside the picture (a wider screen) the mask's own black.
        if (rect.Position.X > 0.5f)
        {
            var side = new Color(0f, 0f, 0f, slot.Edge * fade);
            DrawRect(new Rect2(0, 0, rect.Position.X + 1f, vs.Y), side);
            DrawRect(new Rect2(rect.End.X - 1f, 0, vs.X - rect.End.X + 1f, vs.Y), side);
        }
        DrawTextureRect(slot.Normal, rect, false, new Color(1f, 1f, 1f, fade));
    }
}

/// <summary>The layer the movie draws with the "add" blend mode (the glows, ticks and reticle lines), drawn additively over the normal layer of its <see cref="ScopeView"/>.</summary>
public sealed partial class ScopeAddLayer : Control
{
    readonly ScopeView _owner;

    public ScopeAddLayer(ScopeView owner)
    {
        _owner = owner; Name = "ScopeAdd";
        MouseFilter = MouseFilterEnum.Ignore; SetAnchorsPreset(LayoutPreset.FullRect);
        Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        Visible = false;
    }

    public override void _Draw() => _owner.DrawAdditive(this);
}
