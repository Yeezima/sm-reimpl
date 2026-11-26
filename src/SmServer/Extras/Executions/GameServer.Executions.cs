using SmNet;
using SmNet.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou.Campaign;
using WireOffer = SmNet.Extras.Executions.ExecutionOffer;

namespace SmServer;

/// <summary>
/// Executions X3 on the server: the tag-50 sections (<see cref="ExecutionWire"/>). Reached only through the dispatcher in <c>GameServer.Extras.cs</c>, under <see cref="ContentExtras.Executions"/>, so a faithful
/// or Musou server never sends tag 50.
/// <list type="bullet">
/// <item>Every Start the world reports (<see cref="World.ExecutionsStartedThisTick"/>) goes to every client in <see cref="ExecutionWire.StartRepeats"/> (3) consecutive messages; every End too
/// (ours: a lost datagram must not leave a client playing a dead run; the client deduplicates by tick and executor).</item>
/// <item>Each Classic offer goes to its killer alone, once (the HUD prompt; the server decides on its own clock).</item>
/// <item>The live Starts are resent at <see cref="ExecutionWire.LiveListHz"/> (2 Hz) for late joiners.</item>
/// <item>Warfront only, the overshield list on a change and at <see cref="ExecutionWire.OvershieldHz"/> (1 Hz) while not empty: every living non-puppet player above its armour maximum (X4 fills it).</item>
/// </list>
/// A log line for every Start, End, offer and press that reached the choice and found nothing (NoFit with its numbers, NoWire); earlier refusals are counted (<see cref="ExecutionRefusalsCounted"/>).
/// </summary>
public sealed partial class GameServer
{
    readonly List<(byte[] Bytes, int Left)> _executionRepeats = new();
    uint _executionLiveNext, _executionOvershieldNext;
    byte[]? _executionOvershieldLast;
    int _executionOvershieldEmptyLeft;   // repeats of the list's change to empty still to send

    /// <summary>Tag-50 messages sent (tests and the log).</summary>
    public int ExecutionMessagesSent { get; private set; }
    /// <summary>Starts, Ends and offers encoded (each once, however often it is repeated).</summary>
    public int ExecutionStartsSent { get; private set; }
    public int ExecutionEndsSent { get; private set; }
    public int ExecutionOffersSent { get; private set; }
    /// <summary>Presses refused before the choice (counted, not logged).</summary>
    public int ExecutionRefusalsCounted { get; private set; }

    partial void ExecutionsCreated()
    {
        var c = World.Config;
        Log?.Invoke($"executions: {c.Executions.Rows.Count} rows of {c.Executions.WireIds.Count} (rules {ExecutionRules.Version}); Classic window {ExecutionRules.WindowTicks(c)} ticks");
    }

    partial void ExecutionsSend()
    {
        var w = World;
        uint tick = w.Tick;
        int rate = Math.Max(1, w.Config.TickRate);
        foreach (var run in w.ExecutionsStartedThisTick)
        {
            _executionRepeats.Add((ExecutionWire.Encode(ExecutionStart.Of(run)), ExecutionWire.StartRepeats));
            ExecutionStartsSent++;
            var row = w.Config.Executions.AtWire(run.Wire);
            string type = run.TargetKind == ExecutionTargetKind.Enemy && w.Musou is { } pool && pool.TryGet((ushort)run.Target, out var e) ? $" (type {e.Type}, kind {row?.Target ?? "?"})" : "";   // X4b: a Chaos stage's type byte and the kind it took
            Log?.Invoke($"executions: start player {run.Executor} on {run.TargetKind} {run.Target}{type} with {row?.Id ?? "?"} (wire {run.Wire}) at tick {run.StartTick}: distance {run.Distance:0.000} m, slide {run.SlideTicks} ticks{(run.RaceSwapped ? ", races swapped" : "")}");
        }
        foreach (var end in w.ExecutionsEndedThisTick)
        {
            _executionRepeats.Add((ExecutionWire.Encode(new ExecutionEnd(end.Tick, (ushort)end.Run.Executor, end.Reason)), ExecutionWire.StartRepeats));
            ExecutionEndsSent++;
            Log?.Invoke($"executions: end player {end.Run.Executor} {end.Reason} at tick {end.Tick}{(end.Run.KillDone ? " (kill event passed)" : "")}");
        }
        // A refusal before the choice is counted, not logged (a mashing client would write a line every few ticks); a press that reached the choice and found nothing is logged.
        foreach (var (id, why) in w.ExecutionsRefusedThisTick)
        {
            if (why is ExecutionRefusal.NoFit or ExecutionRefusal.NoWire) Log?.Invoke($"executions: press of player {id} at tick {tick} did nothing ({why})");
            else if (why == ExecutionRefusal.Refused && w.ExecutionsWarfrontRefusedLast(id) is { } warfront)
            {
                // X4: Warfront's own reasons are logged, one line per press (a press is a 0-to-1 edge, so a held key writes nothing more).
                uint ready = w.ExecutionsReadyAt(id);
                Log?.Invoke($"executions: press of player {id} at tick {tick} refused by Warfront ({warfront}{(warfront == ExecutionWarfrontRefusal.Cooldown ? $", ready at tick {ready}, {(ready - tick) / (float)Math.Max(1, w.Config.TickRate):0.0} s left" : "")})");
            }
            else ExecutionRefusalsCounted++;
        }
        foreach (var r in w.ExecutionsRewardedThisTick)
            Log?.Invoke($"executions: reward player {r.Player} for Ork {r.Ork} at tick {r.Tick}: health {r.Health:0}, armour {r.Shield:0}, ready again at tick {r.ReadyAt}");
        foreach (var n in w.ExecutionsNoFitThisTick) Log?.Invoke($"executions: no move fits player {n.Executor} on the corpse of {n.Victim}: gap {n.Gap:0.00} m, height {n.Height:0.00} m, first {n.First}, tried {string.Join(",", n.Tried)}");

        // Offers: to the killer alone.
        byte window = (byte)Math.Min(255, ExecutionRules.WindowTicks(w.Config));
        foreach (var offer in w.ExecutionsOfferedThisTick)
        {
            var bytes = ExecutionWire.Encode(new WireOffer((ushort)offer.Victim, offer.KillTick, window));
            if (SendExtras(new ExtrasUpdate(tick, [(ExecutionWire.Tag, bytes)]), offer.Killer)) ExecutionMessagesSent++;
            ExecutionOffersSent++;
            Log?.Invoke($"executions: offer to player {offer.Killer} for the kill of {offer.Victim} at tick {offer.KillTick} ({window} ticks)");
        }

        var sections = new List<(byte Tag, byte[] Payload)>();
        for (int i = 0; i < _executionRepeats.Count; i++)
        {
            var (bytes, left) = _executionRepeats[i];
            sections.Add((ExecutionWire.Tag, bytes));
            _executionRepeats[i] = (bytes, left - 1);
        }
        _executionRepeats.RemoveAll(r => r.Left <= 0);

        // The live runs for late joiners (2 Hz).
        if (tick >= _executionLiveNext)
        {
            _executionLiveNext = tick + (uint)Math.Max(1, (int)Math.Round(rate / ExecutionWire.LiveListHz));
            foreach (var run in w.ExecutionsLive)
                if (run.StartTick < tick) sections.Add((ExecutionWire.Tag, ExecutionWire.Encode(ExecutionStart.Of(run))));   // this tick's own Starts are already in the repeats
        }

        // The overshield list: Warfront only (Classic never has one); on a change, and at 1 Hz while it is not empty (a late joiner, a lost datagram); never encoded in Classic.
        if (w.Config.Extras.HasFlag(ContentExtras.Warfront))
        {
            var shields = ExecutionsOvershields(w);
            var list = ExecutionWire.Encode(shields);
            bool changed = _executionOvershieldLast is null || !list.AsSpan().SequenceEqual(_executionOvershieldLast);
            if (changed) _executionOvershieldEmptyLeft = shields.Length == 0 ? ExecutionWire.StartRepeats : 0;   // the change to an empty list goes out StartRepeats times (a lost datagram would leave a stale overshield drawn)
            if (changed || (shields.Length > 0 && tick >= _executionOvershieldNext) || _executionOvershieldEmptyLeft > 0)
            {
                if (_executionOvershieldEmptyLeft > 0) _executionOvershieldEmptyLeft--;
                _executionOvershieldLast = list;
                _executionOvershieldNext = tick + (uint)Math.Max(1, (int)Math.Round(rate / ExecutionWire.OvershieldHz));
                sections.Add((ExecutionWire.Tag, list));
            }
        }

        // The shared sections in as few messages as fit the envelope (Protocol.MaxExtrasMessageBytes, with its header and each section's).
        var chunk = new List<(byte Tag, byte[] Payload)>();
        int size = Protocol.ExtrasHeaderBytes;
        foreach (var s in sections)
        {
            int need = Protocol.ExtrasSectionHeaderBytes + s.Payload.Length;
            if (chunk.Count > 0 && size + need > Protocol.MaxExtrasMessageBytes)
            {
                if (SendExtras(new ExtrasUpdate(tick, chunk.ToArray()))) ExecutionMessagesSent++;
                chunk.Clear();
                size = Protocol.ExtrasHeaderBytes;
            }
            chunk.Add(s);
            size += need;
        }
        if (chunk.Count > 0 && SendExtras(new ExtrasUpdate(tick, chunk.ToArray()))) ExecutionMessagesSent++;
    }

    /// <summary>Every living non-puppet player with armour above its maximum, as a fraction of the overshield's cap, by id, at most <see cref="ExecutionWire.MaxOvershieldEntries"/>.</summary>
    static OvershieldEntry[] ExecutionsOvershields(World w)
    {
        List<OvershieldEntry>? list = null;
        foreach (var (id, p) in w.Players.OrderBy(kv => kv.Key))
        {
            if (!p.Alive || TfwContracts.IsPuppet(id) || id <= 0 || id > ushort.MaxValue) continue;
            float max = Loadouts.Body(w.Config, p).ShieldMax;
            float over = ExecutionRules.Overshield(p.Shield, max);
            byte f = ExecutionWire.Fraction(over, ExecutionRules.OvershieldCap(max));
            if (f == 0) continue;
            (list ??= new()).Add(new OvershieldEntry((ushort)id, f));
            if (list.Count >= ExecutionWire.MaxOvershieldEntries) break;
        }
        return list?.ToArray() ?? [];
    }
}
