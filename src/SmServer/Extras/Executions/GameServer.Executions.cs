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
/// <item>The overshield list on a change and at <see cref="ExecutionWire.OvershieldHz"/> (1 Hz): every living non-puppet player above its armour maximum (empty in Classic; X4 fills it).</item>
/// </list>
/// A log line for every Start, End and refused press (the live check and the operators).
/// </summary>
public sealed partial class GameServer
{
    readonly List<(byte[] Bytes, int Left)> _executionRepeats = new();
    uint _executionLiveNext, _executionOvershieldNext;
    byte[]? _executionOvershieldLast;

    /// <summary>Tag-50 messages sent (tests and the log).</summary>
    public int ExecutionMessagesSent { get; private set; }
    /// <summary>Starts, Ends and offers encoded (each once, however often it is repeated).</summary>
    public int ExecutionStartsSent { get; private set; }
    public int ExecutionEndsSent { get; private set; }
    public int ExecutionOffersSent { get; private set; }

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
            Log?.Invoke($"executions: start player {run.Executor} on {run.TargetKind} {run.Target} with {row?.Id ?? "?"} (wire {run.Wire}) at tick {run.StartTick}: distance {run.Distance:0.000} m, slide {run.SlideTicks} ticks{(run.RaceSwapped ? ", races swapped" : "")}");
        }
        foreach (var end in w.ExecutionsEndedThisTick)
        {
            _executionRepeats.Add((ExecutionWire.Encode(new ExecutionEnd(end.Tick, (ushort)end.Run.Executor, end.Reason)), ExecutionWire.StartRepeats));
            ExecutionEndsSent++;
            Log?.Invoke($"executions: end player {end.Run.Executor} {end.Reason} at tick {end.Tick}{(end.Run.KillDone ? " (kill event passed)" : "")}");
        }
        foreach (var (id, why) in w.ExecutionsRefusedThisTick) Log?.Invoke($"executions: press of player {id} at tick {tick} did nothing ({why})");

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

        // The overshield list (on a change and at 1 Hz).
        var shields = ExecutionsOvershields(w);
        var list = ExecutionWire.Encode(shields);
        bool changed = _executionOvershieldLast is null || !list.AsSpan().SequenceEqual(_executionOvershieldLast);
        if (changed || tick >= _executionOvershieldNext)
        {
            _executionOvershieldLast = list;
            _executionOvershieldNext = tick + (uint)Math.Max(1, (int)Math.Round(rate / ExecutionWire.OvershieldHz));
            sections.Add((ExecutionWire.Tag, list));
        }

        // One message per tick at most for the shared sections, split if it would grow past the envelope's size.
        const int perMessage = 32;
        for (int i = 0; i < sections.Count; i += perMessage)
            if (SendExtras(new ExtrasUpdate(tick, sections.Skip(i).Take(perMessage).ToArray()))) ExecutionMessagesSent++;
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
