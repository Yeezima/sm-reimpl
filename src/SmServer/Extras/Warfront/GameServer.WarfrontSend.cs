using SmNet;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;

namespace SmServer;

/// <summary>Warfront envelope sections (tags 40 Hub, 41 Stage, 42 Down, 43 Results; <see cref="WarfrontWire"/>). Sections ride unreliable datagrams, so each repeats: the Hub on a change and every second, the Stage five times a second while it matters.</summary>
public sealed partial class GameServer
{
    partial void WarfrontSend()
    {
        if (_wf is not { } s) return;
        int rate = WarfrontRate;
        uint tick = World.Tick;
        bool stageNow = tick % (uint)Math.Max(1, rate / 5) == 0;
        var stage = WarfrontStageSection(s);
        foreach (var (id, key) in s.KeyOf.ToList())
        {
            var sections = new List<(byte Tag, byte[] Payload)>(3);
            byte[] hub = WarfrontWire.Encode(WarfrontWire.HubFor(s.Campaign, key));
            bool changed = !s.LastHub.TryGetValue(id, out var last) || !last.AsSpan().SequenceEqual(hub);
            if (changed || (tick + (uint)id) % (uint)rate == 0) { sections.Add((WarfrontWire.HubTag, hub)); s.LastHub[id] = hub; }
            if (stage is not null && (stageNow || changed)) sections.Add((WarfrontWire.StageTag, stage));
            if (World.Tfw is { } run)
            {
                var d = run.WarfrontDownState(id);
                bool down = d.Phase != WarfrontDownPhase.None;
                if (down ? tick % (uint)Math.Max(1, rate / 4) == 0 : s.DownSent.Remove(id))
                    sections.Add((WarfrontWire.DownTag, WarfrontWire.Encode(new WarfrontDownWire(d.Phase, d.SecondsLeft))));
                if (down) s.DownSent.Add(id);
            }
            if (sections.Count > 0) SendExtras(new ExtrasUpdate(tick, sections.ToArray()), id);
        }
        s.HubDirty = false;
        // players not yet identified still hear a stage change (a stage that says Go reaches everyone)
        if (stage is not null && stageNow)
            foreach (int id in s.Waiting.Keys.ToList()) SendExtras(new ExtrasUpdate(tick, [(WarfrontWire.StageTag, stage)]), id);
        if (s.ResultsBytes is { } results && tick >= s.NextResultsTick)
        {
            s.NextResultsTick = tick + (uint)(5 * rate);
            foreach (var chunk in WarfrontWire.ResultsChunks(results, s.ResultsTransfer))
                SendExtras(new ExtrasUpdate(tick, [(WarfrontWire.ResultsTag, chunk)]));
        }
        WarfrontSendProgression();
        WarfrontSendSquad();
        WarfrontSendCoop();
    }

    /// <summary>The Stage section now, or null when there is nothing to say.</summary>
    byte[]? WarfrontStageSection(WarfrontServerState s)
    {
        if (s.Go is { } go)
            return WarfrontWire.Encode(new WarfrontStageWire(WarfrontStageState.Go, go.Target?.Kind ?? WarfrontTargetKind.Node, go.Target?.Lane ?? -1, go.Map, 100, (int)(World.Tick - go.Tick) / WarfrontRate, 0, s.KeyOf.Count, "go"));
        if (s.Prep is { } p)
        {
            int elapsed = (int)(DateTime.UtcNow - p.Started).TotalSeconds;
            return WarfrontWire.Encode(new WarfrontStageWire(WarfrontStageState.Preparing, p.Target.Kind, p.Target.Lane, p.Map, s.PrepPercent, elapsed, 0, s.KeyOf.Count, p.Status));
        }
        if (s.Arriving || World.Tick < s.ArrivedUntilTick)
        {
            var t = s.Campaign.CurrentTarget;
            return WarfrontWire.Encode(new WarfrontStageWire(WarfrontStageState.Arrived, t?.Kind ?? WarfrontTargetKind.Node, t?.Lane ?? -1, s.Map, 100, 0, s.ArrivedCount, s.Expected.Count, "arrived"));
        }
        if (s.GateRefusal is { } text && World.Tick - s.GateRefusalTick < (uint)(5 * WarfrontRate))
            return WarfrontWire.Encode(new WarfrontStageWire(WarfrontStageState.None, WarfrontTargetKind.Node, -1, s.Map, 0, 0, 0, 0, text));
        return null;
    }
}
