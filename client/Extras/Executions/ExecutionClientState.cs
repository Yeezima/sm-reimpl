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
    /// <summary>
    /// A live run the server no longer re-announces (its Starts are resent at <see cref="ExecutionWire.LiveListHz"/>, 2 Hz) is over after this long without one (seconds, ours: three live-list
    /// periods), as long as snapshots kept coming for as long (<see cref="Prune"/>'s tick): a lost End then never leaves the move playing (X5 review, X5-1). Ends <see cref="ExecutionEndReason.Invalid"/>.
    /// </summary>
    public const double LiveSilenceSeconds = 1.5;
    /// <summary>A live run is over this long past its own last tick (<c>Prune</c>'s <c>lastTick</c>: the track's end tick, or 12 s for a row this install lacks) whatever was heard (seconds, ours).</summary>
    public const double PastEndSeconds = 1.0;

    readonly Dictionary<int, ExecutionRun> _live = new();
    readonly List<ExecutionEndedView> _ended = new();
    readonly Dictionary<int, (uint EndTick, double At)> _lastEnd = new();
    readonly Dictionary<ushort, byte> _overshield = new();
    /// <summary>The silence clock's reading when each live run was last announced; synthetic runs (the test aids) are not listed and only expire by their end tick.</summary>
    readonly Dictionary<int, double> _announced = new();

    /// <summary>
    /// The silence clock now, between prunes: the last prune's reading plus the client time since it (prunes run every frame, so this adds at most a frame; a stall adds nothing to the clock
    /// itself, only this frame's share).
    /// </summary>
    double SilenceNow(double now) => double.IsFinite(_prevNow) ? _silenceClock + Math.Max(0.0, now - _prevNow) : _silenceClock;
    /// <summary>
    /// The silence clock (seconds): it advances by min(the client time, the snapshot ticks over the rate) between prunes, so only while snapshots flow; a receive stall stops it, and the first
    /// snapshot after one adds a frame's time, not the stall's (X5 re-review N1).
    /// </summary>
    double _silenceClock, _prevNow = double.NaN, _prevSnapshot = double.NaN;
    /// <summary>Executors whose run this client ended for silence alone (their start tick): a same-start live-list repeat revives it.</summary>
    readonly Dictionary<int, uint> _silenced = new();
    readonly List<(ExecutionRun Run, bool Past, uint Last)> _lost = new();
    int _ownId = -1;
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
    /// <summary>Live runs ended here because their End was lost (<see cref="Prune"/>).</summary>
    public int Expired { get; private set; }
    /// <summary>Runs ended for silence that the live list then announced again (revived).</summary>
    public int Revived { get; private set; }
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
        _ownId = ownId;
        switch (ExecutionWire.KindOf(payload))
        {
            case ExecutionWire.StartKind when ExecutionWire.TryDecode(payload, out ExecutionStart start): Start(start.ToRun(), now); break;
            case ExecutionWire.EndKind when ExecutionWire.TryDecode(payload, out ExecutionEnd end): End(end, now, ownId); break;
            case ExecutionWire.OfferKind when ExecutionWire.TryDecode(payload, out ExecutionOffer offer): TakeOffer(new ExecutionOfferView(offer.Victim, offer.KillTick, offer.WindowTicks)); break;
            case ExecutionWire.OvershieldKind when ExecutionWire.TryDecode(payload, out OvershieldEntry[] list):
                _overshield.Clear();
                foreach (var e in list) _overshield[e.Player] = e.Fraction;
                _overshieldAt = now;
                break;
            default: Malformed++; break;
        }
    }

    /// <summary>
    /// An offer heard. The server's press takes the open offer with the latest kill tick and, among equal kill ticks, the lowest victim id (its offers are kept by victim and the first of equal
    /// ticks wins: <c>World.ExecutionsClassicPick</c>), so the marker keeps the same one (X6 review X6-10): a held offer with a later kill tick, or the same tick and a lower victim, stays. A spent
    /// offer (<see cref="SpendOffer"/>) is not taken again.
    /// </summary>
    void TakeOffer(ExecutionOfferView o)
    {
        if (_spentOffer is { } spent && spent.Victim == o.Victim && spent.KillTick == o.KillTick) return;
        if (Offer is { } cur && (cur.KillTick > o.KillTick || cur.KillTick == o.KillTick && cur.Victim <= o.Victim)) return;
        Offer = o;
    }

    ExecutionOfferView? _spentOffer;

    /// <summary>The own press took the open offer (the server spends it whether a move fits or not: one try per corpse), so the marker hides (X6 review X6-10).</summary>
    public void SpendOffer()
    {
        if (Offer is { } o) _spentOffer = o;
        Offer = null;
    }

    /// <summary>
    /// A Start (also the presenter's tests and the headless self-test feed it directly). <paramref name="announced"/> false: a synthetic run of a test aid, which no live list re-announces (it
    /// expires only by its end tick, <see cref="Prune"/>).
    /// </summary>
    public void Start(in ExecutionRun run, double now, bool announced = true)
    {
        if (_live.TryGetValue(run.Executor, out var held) && held.StartTick >= run.StartTick)
        {
            if (held.StartTick == run.StartTick && _announced.ContainsKey(run.Executor)) _announced[run.Executor] = SilenceNow(now);   // the live list: still playing
            Repeats++;
            return;   // a repeat, or older than the move playing
        }
        bool revive = _silenced.TryGetValue(run.Executor, out uint silencedStart) && silencedStart == run.StartTick;
        if (revive)
        {
            // A live-list repeat of a move this client ended only for silence (no End heard; X5 re-review 2): the server still plays it, so it plays again, without the fade's ended entry.
            _silenced.Remove(run.Executor);
            int executor = run.Executor; uint start = run.StartTick;
            _ended.RemoveAll(e => e.Run.Executor == executor && e.Run.StartTick == start);
            Revived++;
        }
        else if (_lastEnd.TryGetValue(run.Executor, out var last) && now - last.At <= EndMemorySeconds && run.StartTick <= last.EndTick) { Repeats++; return; }   // a late repeat of an ended move
        if (_live.ContainsKey(run.Executor)) Close(held, ExecutionEndReason.Invalid, run.StartTick, now);   // a newer move replaces one whose End was lost
        _live[run.Executor] = run;
        if (announced) _announced[run.Executor] = SilenceNow(now);
        if (Offer is { } o && run.TargetKind == ExecutionTargetKind.Player && run.Target == o.Victim) Offer = null;   // the offer is taken
        Version++;
    }

    void End(in ExecutionEnd end, double now, int ownId)
    {
        if (_live.TryGetValue(end.Executor, out var run) && run.StartTick <= end.Tick) Close(run, end.Reason, end.Tick, now);
        // The own cooldown's start, also from a Done heard after this client had already ended the run for silence or its end tick (X5 re-review 3).
        if (end.Executor == ownId && end.Reason == ExecutionEndReason.Done && (OwnLastDoneTick is not { } done || end.Tick > done)) OwnLastDoneTick = end.Tick;
        _silenced.Remove(end.Executor);   // the End settles it: no live-list revival
        // Remembered either way: a Start reordered behind its End is then ignored.
        if (!_lastEnd.TryGetValue(end.Executor, out var last) || end.Tick > last.EndTick || now - last.At > EndMemorySeconds) _lastEnd[end.Executor] = (end.Tick, now);
    }

    void Close(in ExecutionRun run, ExecutionEndReason reason, uint tick, double now)
    {
        _live.Remove(run.Executor);
        _announced.Remove(run.Executor);
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
        _live.Clear(); _ended.Clear(); _lastEnd.Clear(); _overshield.Clear(); _announced.Clear(); _silenced.Clear();
        _overshieldAt = double.NegativeInfinity;
        _silenceClock = 0; _prevNow = double.NaN; _prevSnapshot = double.NaN;
        Offer = null; _spentOffer = null; OwnLastDoneTick = null;
        Version++;
    }

    /// <summary>
    /// Drops ended runs past their fade, a passed offer (by the client's view tick, <paramref name="viewTick"/>: the tick the marker and the server's press check use), stale overshields and old
    /// End memories, and ends (<see cref="ExecutionEndReason.Invalid"/>) a live run whose End was lost:
    /// <list type="bullet">
    /// <item>silence: the live list has not announced it for <see cref="LiveSilenceSeconds"/> while snapshots kept arriving for as long (<paramref name="snapshotTick"/>: the newest snapshot's tick,
    /// which stands still through a receive stall, unlike the view tick, which runs on with the wall clock; X5 re-review 2). A same-start repeat from the live list revives it;</item>
    /// <item>past its end: <see cref="PastEndSeconds"/> past its own last tick (<paramref name="lastTick"/>) at the view tick. Final: remembered like an End; the own run's last tick becomes the
    /// own last Done tick (X5 re-review 3: the server ended it Done there or earlier).</item>
    /// </list>
    /// </summary>
    public void Prune(double now, double viewTick, double snapshotTick, Func<ExecutionRun, uint>? lastTick = null, int tickRate = 30)
    {
        double clockRate = Math.Max(1, tickRate);
        if (double.IsFinite(_prevNow) && double.IsFinite(_prevSnapshot))
            _silenceClock += Math.Max(0.0, Math.Min(now - _prevNow, (snapshotTick - _prevSnapshot) / clockRate));
        _prevNow = now; _prevSnapshot = snapshotTick;
        if (_live.Count > 0)
        {
            double rate = Math.Max(1, tickRate), pastTicks = PastEndSeconds * rate;
            _lost.Clear();
            foreach (var (id, run) in _live)
            {
                uint last = lastTick is null ? uint.MaxValue : lastTick(run);
                bool past = lastTick is not null && viewTick > last + pastTicks;
                bool silent = !past && _announced.TryGetValue(id, out var a) && _silenceClock - a > LiveSilenceSeconds;
                if (silent || past) _lost.Add((run, past, last));
            }
            foreach (var (run, past, last) in _lost)
            {
                uint at = viewTick > run.StartTick ? (uint)Math.Min(viewTick, uint.MaxValue) : run.StartTick;
                Close(run, ExecutionEndReason.Invalid, at, now);
                Expired++;
                if (past)
                {
                    _lastEnd[run.Executor] = (Math.Max(at, run.StartTick), now);   // remembered like an End: a straggling repeat does not revive it
                    _silenced.Remove(run.Executor);
                    if (run.Executor == _ownId && (OwnLastDoneTick is not { } done || last > done)) OwnLastDoneTick = last;
                }
                else _silenced[run.Executor] = run.StartTick;
            }
        }
        _ended.RemoveAll(e => now - e.HeardAt > EndedKeepSeconds);
        if (Offer is { } o && viewTick > o.KillTick + (double)o.WindowTicks) Offer = null;
        if (_overshield.Count > 0 && now - _overshieldAt > OvershieldKeepSeconds) _overshield.Clear();
        if (_lastEnd.Count > 0)
        {
            List<int>? old = null;
            foreach (var (id, e) in _lastEnd) if (now - e.At > EndMemorySeconds) (old ??= new()).Add(id);
            if (old is not null) foreach (var id in old) _lastEnd.Remove(id);
        }
    }
}
