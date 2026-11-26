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
            bool repeat = (tick + (uint)id) % (uint)rate == 0;
            bool changed = false;
            if (s.HubDirty || repeat || !s.LastHub.ContainsKey(id))   // encoded only when something may have changed, or on the one-second repeat
            {
                byte[] hub = WarfrontWire.Encode(WarfrontWire.HubFor(s.Campaign, key));
                changed = !s.LastHub.TryGetValue(id, out var last) || !last.AsSpan().SequenceEqual(hub);
                if (changed || repeat) { sections.Add((WarfrontWire.HubTag, hub)); s.LastHub[id] = hub; }
            }
            var mine = stage ?? WarfrontNoticeSection(s, id);
            if (mine is not null && (stageNow || changed)) sections.Add((WarfrontWire.StageTag, mine));
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
        if (stageNow)
            foreach (int id in s.Waiting.Keys.ToList())
                if ((stage ?? WarfrontNoticeSection(s, id)) is { } text) SendExtras(new ExtrasUpdate(tick, [(WarfrontWire.StageTag, text)]), id);
        if (s.ResultsBytes is { } results && tick >= s.NextResultsTick)
        {
            s.NextResultsTick = tick + (uint)(5 * rate);
            foreach (var chunk in WarfrontWire.ResultsChunks(results, s.ResultsTransfer))
                SendExtras(new ExtrasUpdate(tick, [(WarfrontWire.ResultsTag, chunk)]));
        }
        WarfrontSendProgression();
        WarfrontSendSquad();
        WarfrontSendCoop();
        WarfrontSendBattle();   // W13b (routed: the lead's fan-out line)
        WarfrontVoxSend();   // Guardsmen G2: tag 52, the Guardsmen's vox lines (GameServer.WarfrontVoxSend.cs; nothing unless a lane battle's line made one)
        WarfrontChaosSend();   // Chaos Warfront C4: tag 51, the breaches (Chaos/GameServer.WarfrontChaos.cs; nothing unless the stage's army is Chaos)
    }

    /// <summary>A player's own notice (a refused Identify) as a Stage None section, or null.</summary>
    byte[]? WarfrontNoticeSection(WarfrontServerState s, int id)
    {
        if (!s.Notices.TryGetValue(id, out var n)) return null;
        if (World.Tick >= n.Until) { s.Notices.Remove(id); return null; }
        return WarfrontWire.Encode(new WarfrontStageWire(WarfrontStageState.None, WarfrontTargetKind.Node, -1, s.Map, 0, 0, 0, 0, n.Text));
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
