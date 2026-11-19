namespace SmMapmaker;

/// <summary>
/// The editor's decoration compile off the main thread (docs/mapmaker.md "SM1 pieces in play"): every edit that changes SM1 placements asks for a rebuild with <see cref="Request"/>; the build starts
/// <see cref="Debounce"/> after the last request, runs on a worker thread, and its result is stored in the <see cref="DecorCache"/> and announced (<c>onReady</c>, marshalled by <c>post</c> to the main
/// thread) only when no newer request exists: the latest document version wins and a stale result is dropped.
/// </summary>
public sealed class DecorWorker : IDisposable
{
    readonly DecorCache _cache;
    readonly Func<MapDocument, CompileOptions, DecorResult> _build;
    readonly Action<Action> _post;
    readonly Action _onReady;
    readonly Timer _timer;
    readonly object _gate = new();
    long _generation;
    (MapDocument Doc, CompileOptions Options, string Key, long Generation)? _pending;
    bool _building;
    bool _disposed;

    public TimeSpan Debounce { get; }
    /// <summary>Builds actually started (a test counts them).</summary>
    public int Builds { get; private set; }
    /// <summary>A build is requested, waiting or running (the editor shows "checking...").</summary>
    public bool Pending { get { lock (_gate) return _pending is not null || _building; } }

    /// <param name="post">Runs an action on the thread that owns the editor (the main thread); a test passes <c>a =&gt; a()</c>.</param>
    public DecorWorker(DecorCache cache, TimeSpan debounce, Action<Action> post, Action onReady, Func<MapDocument, CompileOptions, DecorResult>? build = null)
    {
        _cache = cache; Debounce = debounce; _post = post; _onReady = onReady;
        _build = build ?? DecorGeometry.Build;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Asks for the decoration of <paramref name="doc"/> (a clone the caller no longer changes); <paramref name="key"/> is <see cref="DecorCache.KeyOf"/> of it.</summary>
    public void Request(MapDocument doc, CompileOptions options, string key)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _pending = (doc, options, key, _generation);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire()
    {
        (MapDocument Doc, CompileOptions Options, string Key, long Generation) job;
        lock (_gate)
        {
            if (_disposed || _pending is not { } p) return;
            if (_building) { _timer.Change(Debounce, Timeout.InfiniteTimeSpan); return; }   // one build at a time; the next starts when it ends
            job = p; _pending = null; _building = true; Builds++;
        }
        DecorResult? result = null;
        try { result = _build(job.Doc, job.Options); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { result = new DecorResult(); result.Problems.Add($"The decoration could not be compiled: {e.Message}"); }
        bool current;
        lock (_gate) { _building = false; current = job.Generation == _generation && _pending is null; }
        if (!current)
        {
            lock (_gate) { if (_pending is not null) _timer.Change(Debounce, Timeout.InfiniteTimeSpan); }
            return;   // stale: a newer edit exists, its own build is coming
        }
        lock (_gate) { if (_disposed) return; }
        if (result is not null) _cache.Set(job.Key, result);
        _post(() =>
        {
            lock (_gate) { if (_disposed || job.Generation != _generation) return; }   // an edit arrived while this was waiting to be applied
            _onReady();
        });
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _pending = null; }
        _timer.Dispose();
    }
}
