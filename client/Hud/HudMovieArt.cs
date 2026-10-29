using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// The original HUD movies' pieces as textures (ui/hud-fidelity): <see cref="HudMovies"/> is read on a low-priority worker the first time the HUD asks, then each piece the HUD
/// asks for (a reticle at a spread frame, the hit marker's frames, the score panel, a point decorator state ...) is rendered there by <see cref="HudMovie.Render"/> at the window's
/// scale and turned into textures on the main thread (within <see cref="BuildBudgetMs"/> a frame). <see cref="Get"/> returns null until a piece is ready (the HUD then draws its M44
/// shape), and for pieces the install lacks. A window-height change renders again (the old textures stay on screen until then). Nothing is written to disk.
/// </summary>
public sealed class HudMovieArt
{
    /// <summary>A rendered piece: the normal and the "add" layer, their size and the piece's origin in them, in screen pixels.</summary>
    public sealed class Pic
    {
        public ImageTexture Normal = null!;
        public ImageTexture? Add;
        public Vector2 Size, Origin;
        /// <summary>Screen pixels per movie pixel it was rendered at.</summary>
        public float RenderScale = 1f;
    }

    const double BuildBudgetMs = 2.0;

    readonly string? _gameDir;
    volatile HudMovies? _movies;
    volatile bool _failed;
    Thread? _thread;
    readonly BlockingCollection<Action> _work = new();
    readonly ConcurrentQueue<(string Key, HudPicture? Picture, int Generation)> _done = new();
    readonly Dictionary<string, Pic?> _pics = new();
    readonly HashSet<string> _asked = new();
    int _generation;
    double _scale = -1;

    public HudMovieArt(string? gameDir) { _gameDir = gameDir; if (gameDir is null) _failed = true; }

    /// <summary>The movies, once read (null while reading, or when they could not be read).</summary>
    public HudMovies? Movies => _movies;

    public bool Failed => _failed;

    /// <summary>Whether the movies are being read now (started, neither read nor failed): the only time the HUD draws no reticle at all.</summary>
    public bool Loading => _thread is not null && !_failed && _movies is null;

    /// <summary><c>--hud-movies-fail</c> (test aid): the read throws, to check that the HUD falls back to its M44 drawing.</summary>
    static readonly bool FaultInjected = Array.IndexOf(OS.GetCmdlineUserArgs(), "--hud-movies-fail") >= 0;

    /// <summary>Screen pixels per movie pixel the pieces are rendered at (the window height / 720).</summary>
    public double Scale => _scale;

    void Start()
    {
        if (_thread is not null || _failed) return;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "HudMovieArt" };
        _thread.Start();
        var dir = _gameDir!;
        _work.Add(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                if (FaultInjected) throw new InvalidOperationException("--hud-movies-fail (test aid)");
                var m = HudMovies.Load(dir);
                _movies = m;
                GD.Print($"HUD movies read in {sw.ElapsedMilliseconds} ms: Hud {(m.Hud is null ? "missing" : "ok")}, HudText {(m.Text is null ? "missing" : "ok")}, HudEventLog {(m.EventLog is null ? "missing" : "ok")}, Decorator {(m.Decorator is null ? "missing" : "ok")}");
            }
            // Any failure (but running out of memory) ends the read for good: the HUD then draws its M44 shapes instead of waiting.
            catch (Exception e) when (e is not OutOfMemoryException) { _failed = true; GD.PrintErr("HUD movies not read (the HUD draws its own shapes): " + e.GetType().Name + ": " + e.Message); }
        });
    }

    void Run()
    {
        foreach (var a in _work.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception e) when (e is not OutOfMemoryException) { GD.PrintErr("HUD movie art: " + e.Message); }
        }
    }

    /// <summary>Sets the render scale (window height / 720); a change drops the pieces asked for so far (they are rendered again, the old ones show meanwhile).</summary>
    public void SetScale(double scale)
    {
        Start();
        scale = Math.Round(scale, 3);
        if (Math.Abs(scale - _scale) < 1e-4) return;
        _scale = scale;
        _generation++;
        _asked.Clear();
    }

    /// <summary>
    /// The piece named <paramref name="key"/> (unique per what <paramref name="make"/> renders, without the scale), or null until it is ready. <paramref name="make"/> runs on the
    /// worker with the movies and the scale.
    /// </summary>
    public Pic? Get(string key, Func<HudMovies, double, HudPicture?> make)
    {
        if (_failed || _scale <= 0) return null;
        Start();
        if (_asked.Add(key))
        {
            int gen = _generation; double scale = _scale;
            _work.Add(() =>
            {
                if (gen != _generation) { _done.Enqueue((key, null, -1)); return; }
                var movies = _movies;
                HudPicture? p = null;
                if (movies is not null) p = make(movies, scale);
                _done.Enqueue((key, p, gen));
            });
        }
        return _pics.TryGetValue(key, out var pic) ? pic : null;
    }

    /// <summary>Whether the piece has been asked for and finished (drawn or found empty).</summary>
    public bool Known(string key) => _pics.ContainsKey(key);

    /// <summary>Per frame on the main thread: turns finished pictures into textures within the budget.</summary>
    public void Process()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < BuildBudgetMs && _done.TryDequeue(out var r))
        {
            if (r.Generation < 0) { _asked.Remove(r.Key); continue; }   // skipped by a scale change: ask again
            if (r.Picture is not { } p) { if (r.Generation == _generation || !_pics.ContainsKey(r.Key)) _pics[r.Key] = null; continue; }
            var pic = new Pic
            {
                Normal = ImageTexture.CreateFromImage(Image.CreateFromData(p.Width, p.Height, false, Image.Format.Rgba8, p.Normal)),
                Add = p.Additive is null ? null : ImageTexture.CreateFromImage(Image.CreateFromData(p.Width, p.Height, false, Image.Format.Rgba8, p.Additive)),
                Size = new Vector2(p.Width, p.Height),
                Origin = new Vector2((float)p.OriginX, (float)p.OriginY),
                RenderScale = (float)p.Scale,
            };
            _pics[r.Key] = pic;
        }
    }

    public void Stop() => _work.CompleteAdding();
}
