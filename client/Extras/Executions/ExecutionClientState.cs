using System;
using System.Collections.Generic;
using System.Numerics;
using SmNet.Extras.Executions;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>An execution that ended: the run, why, the server tick of the end and the client time (seconds) it was heard.</summary>
public readonly record struct ExecutionEndedView(ExecutionRun Run, ExecutionEndReason Reason, uint EndTick, double HeardAt);

/// <summary>The own Classic offer: the corpse may be finished until server tick <see cref="KillTick"/> + <see cref="WindowTicks"/>.</summary>
public readonly record struct ExecutionOfferView(ushort Victim, uint KillTick, byte WindowTicks)
{
    public bool OpenAt(double serverTick) => serverTick >= KillTick && serverTick <= KillTick + (double)WindowTicks;
}

/// <summary>
/// The client's execution state (executions X5a; engine-free, tested in <c>ExecutionClientStateTests</c>): tag 50 payloads (<see cref="ExecutionWire"/>) in, the live runs by
/// executor out. Starts are sent three times and the live ones resent twice a second, Ends three times: a Start already held, or one at or before an End heard for
/// that executor, is ignored, so repeats and reordered datagrams never restart or revive a move. Ended runs stay <see cref="EndedKeepSeconds"/> for the fades. The
/// own offer, the overshield list (<see cref="OvershieldKeepSeconds"/> after the last list) and the own last Done tick (X6's cooldown) are kept too. Nothing here throws on
/// a malformed payload: it is counted (<see cref="Malformed"/>) and dropped.
/// </summary>
public sealed class ExecutionClientState
{
    /// <summary>How long an ended run is kept for the presenter's fade (seconds, ours).</summary>
    public const double EndedKeepSeconds = 0.5;
    /// <summary>An overshield entry expires this long after the last list that carried one (seconds; the server sends at 1 Hz and on a change).</summary>
    public const double OvershieldKeepSeconds = 2.5;
    /// <summary>How long an executor's last End is remembered against late repeats of its Start (seconds, ours: longer than three repeats and a live-list period).</summary>
    public const double EndMemorySeconds = 5.0;

    readonly Dictionary<int, ExecutionRun> _live = new();
    readonly List<ExecutionEndedView> _ended = new();
    readonly Dictionary<int, (uint EndTick, double At)> _lastEnd = new();
    readonly Dictionary<ushort, byte> _overshield = new();
    double _overshieldAt = double.NegativeInfinity;

    /// <summary>The live runs (by executor id).</summary>
    public IReadOnlyDictionary<int, ExecutionRun> Live => _live;
    /// <summary>Runs that ended within <see cref="EndedKeepSeconds"/>, oldest first.</summary>
    public IReadOnlyList<ExecutionEndedView> Ended => _ended;
    /// <summary>The own open Classic offer, if any (cleared by <see cref="Prune"/> once its window has passed).</summary>
    public ExecutionOfferView? Offer { get; private set; }
    /// <summary>The server tick the own player's last execution ended Done (X6: the Warfront cooldown's start), or null.</summary>
    public uint? OwnLastDoneTick { get; private set; }
    /// <summary>Payloads that did not decode.</summary>
    public int Malformed { get; private set; }
    /// <summary>Starts ignored as repeats or as older than an End heard.</summary>
    public int Repeats { get; private set; }
    /// <summary>Bumped whenever a run starts or ends (the presenter's cheap change check).</summary>
    public int Version { get; private set; }

    /// <summary>Whether <paramref name="id"/> is executing now.</summary>
    public bool TryLive(int id, out ExecutionRun run) => _live.TryGetValue(id, out run);

    /// <summary>The live run whose target is <paramref name="target"/> of <paramref name="kind"/>, if any.</summary>
    public bool TryTarget(ExecutionTargetKind kind, int target, out ExecutionRun run)
    {
        foreach (var r in _live.Values) if (r.TargetKind == kind && r.Target == target) { run = r; return true; }
        run = default;
        return false;
    }

    /// <summary>An overshield fraction of its cap (0..1) for <paramref name="player"/>, 0 when none.</summary>
    public float OvershieldOf(int player) => player is >= 0 and <= ushort.MaxValue && _overshield.TryGetValue((ushort)player, out var f) ? f / 255f : 0f;

    /// <summary>Reads one tag-50 payload heard at client time <paramref name="now"/> (seconds); <paramref name="ownId"/> is the own player id (-1: an observer).</summary>
    public void Apply(ReadOnlySpan<byte> payload, double now, int ownId)
    {
        switch (ExecutionWire.KindOf(payload))
        {
            case ExecutionWire.StartKind when ExecutionWire.TryDecode(payload, out ExecutionStart start): Start(start.ToRun(), now); break;
            case ExecutionWire.EndKind when ExecutionWire.TryDecode(payload, out ExecutionEnd end): End(end, now, ownId); break;
            case ExecutionWire.OfferKind when ExecutionWire.TryDecode(payload, out ExecutionOffer offer): Offer = new ExecutionOfferView(offer.Victim, offer.KillTick, offer.WindowTicks); break;
            case ExecutionWire.OvershieldKind when ExecutionWire.TryDecode(payload, out OvershieldEntry[] list):
                _overshield.Clear();
                foreach (var e in list) _overshield[e.Player] = e.Fraction;
                _overshieldAt = now;
                break;
            default: Malformed++; break;
        }
    }

    /// <summary>A Start (also the presenter's tests and the headless self-test feed it directly).</summary>
    public void Start(in ExecutionRun run, double now)
    {
        if (_live.TryGetValue(run.Executor, out var held) && held.StartTick >= run.StartTick) { Repeats++; return; }   // a repeat, or older than the move playing
        if (_lastEnd.TryGetValue(run.Executor, out var last) && now - last.At <= EndMemorySeconds && run.StartTick <= last.EndTick) { Repeats++; return; }   // a late repeat of an ended move
        if (_live.ContainsKey(run.Executor)) Close(held, ExecutionEndReason.Invalid, run.StartTick, now);   // a newer move replaces one whose End was lost
        _live[run.Executor] = run;
        if (Offer is { } o && run.TargetKind == ExecutionTargetKind.Player && run.Target == o.Victim) Offer = null;   // the offer is taken
        Version++;
    }

    void End(in ExecutionEnd end, double now, int ownId)
    {
        if (_live.TryGetValue(end.Executor, out var run) && run.StartTick <= end.Tick)
        {
            Close(run, end.Reason, end.Tick, now);
            if (end.Executor == ownId && end.Reason == ExecutionEndReason.Done) OwnLastDoneTick = end.Tick;
        }
        // Remembered either way: a Start reordered behind its End is then ignored.
        if (!_lastEnd.TryGetValue(end.Executor, out var last) || end.Tick > last.EndTick || now - last.At > EndMemorySeconds) _lastEnd[end.Executor] = (end.Tick, now);
    }

    void Close(in ExecutionRun run, ExecutionEndReason reason, uint tick, double now)
    {
        _live.Remove(run.Executor);
        _ended.Add(new ExecutionEndedView(run, reason, tick, now));
        Version++;
    }

    /// <summary>Ends every live run at once (disconnect, map change, the view leaving): they move to <see cref="Ended"/> with <see cref="ExecutionEndReason.Disconnect"/>.</summary>
    public void EndAll(double now)
    {
        if (_live.Count == 0) return;
        foreach (var run in new List<ExecutionRun>(_live.Values)) Close(run, ExecutionEndReason.Disconnect, run.StartTick, now);
    }

    /// <summary>Forgets an executor that left the match (its run ends Disconnect).</summary>
    public void Forget(int id, double now)
    {
        if (TryLive(id, out var run)) Close(run, ExecutionEndReason.Disconnect, run.StartTick, now);
    }

    /// <summary>Everything cleared (a new connection).</summary>
    public void Clear()
    {
        _live.Clear(); _ended.Clear(); _lastEnd.Clear(); _overshield.Clear();
        _overshieldAt = double.NegativeInfinity;
        Offer = null; OwnLastDoneTick = null;
        Version++;
    }

    /// <summary>Drops ended runs past their fade, a passed offer (by the client's estimate of the server tick, <paramref name="serverTick"/>), stale overshields and old End memories.</summary>
    public void Prune(double now, double serverTick)
    {
        _ended.RemoveAll(e => now - e.HeardAt > EndedKeepSeconds);
        if (Offer is { } o && serverTick > o.KillTick + (double)o.WindowTicks) Offer = null;
        if (_overshield.Count > 0 && now - _overshieldAt > OvershieldKeepSeconds) _overshield.Clear();
        if (_lastEnd.Count > 0)
        {
            List<int>? old = null;
            foreach (var (id, e) in _lastEnd) if (now - e.At > EndMemorySeconds) (old ??= new()).Add(id);
            if (old is not null) foreach (var id in old) _lastEnd.Remove(id);
        }
    }
}
