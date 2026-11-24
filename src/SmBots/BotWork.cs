using System.Threading;
using System.Threading.Tasks;

namespace SmBots;

/// <summary>
/// The brain's work off the frame loop (navigation graph builds and their jump links, the spawn watch, the map plan, cover and approach searches): each job runs on the
/// thread pool once per key, and the task the brain holds completes only at <see cref="Poll"/>, which the bot's host calls once per input tick before the brain runs.
/// So a job's result lands at a tick boundary (before the move it could land at any call within a tick: at most one tick earlier), and two brains that ask for the same
/// key in the same tick (the shadow comparison, <c>--bot-shadow</c>) share one job and see it finish on the same tick. Keys name the asker (its lane) and the job.
/// </summary>
public sealed class BotWork
{
    interface IPending { bool TryFinish(); }

    sealed class Pending<T>(Task<T> inner) : IPending
    {
        public readonly TaskCompletionSource<T> Shown = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryFinish()
        {
            if (!inner.IsCompleted) return false;
            if (inner.IsCompletedSuccessfully) Shown.TrySetResult(inner.Result);
            else if (inner.IsCanceled) Shown.TrySetCanceled();
            else Shown.TrySetException(inner.Exception!.InnerExceptions);
            return true;
        }
    }

    readonly Dictionary<string, object> _byKey = new(StringComparer.Ordinal);
    readonly List<IPending> _running = new();
    readonly object _gate = new();

    /// <summary>
    /// The task of job <paramref name="key"/>: started now on the thread pool (as <c>Task.Run(work, cancel)</c>) unless a job of that key was started before, whose task
    /// is returned again. The task completes at a <see cref="Poll"/> after the work is done.
    /// </summary>
    public Task<T> Run<T>(string key, Func<T> work, CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var known)) return ((Pending<T>)known).Shown.Task;
            var pending = new Pending<T>(Task.Run(work, cancel));
            _byKey[key] = pending;
            _running.Add(pending);
            return pending.Shown.Task;
        }
    }

    /// <summary>Hands on the jobs that have finished since the last poll (call once per input tick, before the brains).</summary>
    public void Poll()
    {
        lock (_gate)
            for (int i = _running.Count - 1; i >= 0; i--)
                if (_running[i].TryFinish()) _running.RemoveAt(i);
    }

    /// <summary>Forgets every key (a new map: the old jobs are cancelled through their tokens; their tasks still finish at a poll).</summary>
    public void Clear()
    {
        lock (_gate) _byKey.Clear();
    }

    /// <summary>Jobs not yet handed on (tests).</summary>
    public int Running { get { lock (_gate) return _running.Count; } }
}
