namespace SmServer;

/// <summary>
/// Warfront perf pass: writes the periodic autosave's files off the tick thread. The caller serialises on the tick thread (a consistent snapshot) and hands over the finished text;
/// the disk write (temp file, then a replacing move, as the synchronous path does) runs on the thread pool, one file at a time. The latest text for a path replaces an older one still
/// waiting, so a slow disk never builds a queue. <see cref="Flush"/> waits until everything handed over is on disk: every synchronous save, the end of a run and a stage change call
/// it first, so the order on disk is the order of the saves and nothing is written after the process was told to leave.
/// </summary>
public sealed class WarfrontAsyncWriter
{
    readonly object _gate = new();
    readonly Dictionary<string, string> _pending = new();
    readonly List<string> _order = new();
    bool _running;
    readonly Action<string> _log;
    long _written;

    public WarfrontAsyncWriter(Action<string> log) { _log = log; }

    /// <summary>Files written so far (test aid).</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Queues <paramref name="text"/> for <paramref name="path"/> (replacing an older waiting text for it) and returns at once.</summary>
    public void Submit(string path, string text)
    {
        lock (_gate)
        {
            if (!_pending.ContainsKey(path)) _order.Add(path);
            _pending[path] = text;
            if (_running) return;
            _running = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(_ => Drain(), null);
    }

    void Drain()
    {
        while (true)
        {
            string path, text;
            lock (_gate)
            {
                if (_order.Count == 0) { _running = false; Monitor.PulseAll(_gate); return; }
                path = _order[0]; _order.RemoveAt(0);
                text = _pending[path]; _pending.Remove(path);
            }
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, overwrite: true);
                Interlocked.Increment(ref _written);
            }
            catch (Exception e) { _log($"{Path.GetFileName(path)} could not be written: {e.GetType().Name}: {e.Message}"); }   // any failure is logged; a throw on the pool thread would end the server (review)
        }
    }

    /// <summary>Blocks until every text handed over so far is on disk (returns false after <paramref name="timeoutMs"/> on a failing disk).</summary>
    public bool Flush(int timeoutMs = 10000)
    {
        lock (_gate)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (_running || _order.Count > 0)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                Monitor.Wait(_gate, left);
            }
            return true;
        }
    }
}
