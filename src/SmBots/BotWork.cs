using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SmBots;

/// <summary>
/// The brain's work off the frame loop (navigation graph builds and their jump links, the spawn watch, the map plan, cover and approach searches): each job runs on the
/// thread pool once per key, and the task the brain holds completes only at <see cref="Poll"/>, which the bot's host calls once per input tick before the brain runs.
/// So a job's result lands at a tick boundary (before the move it could land at any call within a tick: at most one tick earlier), and two brains that ask for the same
/// key in the same tick (the shadow comparison, <c>--bot-shadow</c>) share one job and see it finish on the same tick.
/// <para>
/// Keys (<see cref="ClassicBotBrain"/>'s <c>MapJob</c> and <c>BrainJob</c>): a map job (the graph, its jump links, the spawn watch, the map plan) is keyed by the map's
/// identity (<see cref="MapScope"/>: its name and the map, collision and configuration objects) and the side, pack and class, so every brain on that map shares one build;
/// a brain's own search (cover, approach) by the brain's lane (unique per brain unless the host shares one, as the shadow does) and a running number, and is forgotten
/// (<see cref="Forget"/>) once the brain has taken its result, so a long-lived bot's store stays bounded. A host calls <see cref="Clear"/> at a map change.
/// </para>
/// </summary>
public sealed class BotWork
{
    interface IPending { bool TryFinish(); }
    interface IShown { Task ShownTask { get; } }

    sealed class Pending<T>(Task<T> inner) : IPending, IShown
    {
        public readonly TaskCompletionSource<T> Shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ShownTask => Shown.Task;

        public bool TryFinish()
        {
            if (!inner.IsCompleted) return false;
            if (inner.IsCompletedSuccessfully) Shown.TrySetResult(inner.Result);
            else if (inner.IsCanceled) Shown.TrySetCanceled();
            else Shown.TrySetException(inner.Exception!.InnerExceptions);
            return true;
        }
    }

    sealed class Id(int value) { public readonly int Value = value; }

    readonly Dictionary<string, object> _byKey = new(StringComparer.Ordinal);
    readonly List<IPending> _running = new();
    readonly ConditionalWeakTable<object, Id> _ids = new();
    int _nextId;
    readonly object _gate = new();

    /// <summary>
    /// The task of job <paramref name="key"/>: started now on the thread pool (as <c>Task.Run(work, cancel)</c>) unless a job of that key was started before (and not
    /// forgotten), whose task is returned again. The task completes at a <see cref="Poll"/> after the work is done.
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

    /// <summary>
    /// Forgets <paramref name="key"/> (its result is no longer held here; a brain that holds the task keeps it). A later <see cref="Run{T}"/> of that key starts a new job.
    /// </summary>
    public void Forget(string key)
    {
        lock (_gate) _byKey.Remove(key);
    }

    /// <summary>Forgets the key whose job's task is <paramref name="task"/> (a brain's one-shot search, once taken); nothing when none is.</summary>
    public void Forget(Task task)
    {
        lock (_gate)
            foreach (var (key, value) in _byKey)
                if (value is IShown s && ReferenceEquals(s.ShownTask, task)) { _byKey.Remove(key); return; }
    }

    /// <summary>Forgets every key (a new map: the old jobs are cancelled through their tokens; their tasks still finish at a poll).</summary>
    public void Clear()
    {
        lock (_gate) _byKey.Clear();
    }

    /// <summary>
    /// The key prefix of a map's jobs: its name and a number per (map, collision, configuration) object that this store gives out once, so brains reading the same
    /// loaded map share its builds and another load of the map (or another configuration) does not.
    /// </summary>
    public string MapScope(string mapName, object map, object collision, object config) =>
        $"map {mapName} #{IdOf(map)}.{IdOf(collision)}.{IdOf(config)}";

    int IdOf(object o)
    {
        lock (_gate) return _ids.GetValue(o, _ => new Id(++_nextId)).Value;
    }

    /// <summary>Keys known (tests: one-shot keys are forgotten once taken).</summary>
    public int Keys { get { lock (_gate) return _byKey.Count; } }

    /// <summary>Jobs not yet handed on (tests).</summary>
    public int Running { get { lock (_gate) return _running.Count; } }
}
