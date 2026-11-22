using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmNet.Extras.Warfront;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;

namespace SmServer;

/// <summary>Warfront client commands (message kind 17, tag 40; 41 and 42 are passed to the squad and the coop slices). Untrusted bytes: a malformed payload is dropped.</summary>
public sealed partial class GameServer
{
    public int WarfrontCommandsDropped { get; private set; }

    partial void WarfrontCommand(int player, byte tag, byte[] payload, ref bool handled)
    {
        if (_wf is not { } s) return;
        if (tag == TfwWire.CommandTag)
        {
            // Musou Deploy would land without the campaign knowing: in Warfront the Deploy command of tag 40 is the only way. Every other Musou command passes.
            if (TfwWire.ReadCommand(payload) is { Command: TfwCommand.Deploy }) { handled = true; WarfrontCommandsDropped++; }
            return;
        }
        if (tag == WarfrontWire.SquadCommandTag) { bool done = false; WarfrontSquadCommand(player, tag, payload, ref done); handled = true; return; }
        if (tag == WarfrontWire.CoopCommandTag) { bool done = false; WarfrontCoopCommand(player, tag, payload, ref done); handled = true; return; }   // tag 42 (the co-op rules)
        if (tag != WarfrontWire.CommandTag) return;
        handled = true;
        if (!WarfrontWire.TryDecode(payload, out WarfrontClientCommand command)) { WarfrontCommandsDropped++; return; }
        switch (command.Kind)
        {
            case WarfrontClientCommandKind.Identify:
                WarfrontIdentify(player, command.Key, command.Name);
                break;
            case WarfrontClientCommandKind.Deploy:
                WarfrontDeploy(player, command.Target);
                break;
            case WarfrontClientCommandKind.NewRun:
                WarfrontNewRun(player);
                break;
            case WarfrontClientCommandKind.Progress:
                if (command.Chunk is not null) WarfrontProgressCommand(player, command.Chunk);
                break;
            case WarfrontClientCommandKind.Pick:
                // the wire carries the offer's pick index modulo 256: it answers the standing offer only
                if (!WarfrontPickFromWire(World, player, command.Slot, command.Pick)) WarfrontCommandsDropped++;
                break;
        }
    }

    /// <summary>The leader NewRun, only when the run is over: an empty run on the same server (the connected players join it in their old order).</summary>
    void WarfrontNewRun(int player)
    {
        if (_wf is not { } s || !s.KeyOf.TryGetValue(player, out ulong key)) return;
        var old = s.Campaign;
        if (!old.IsOver) { WfLog($"NewRun from P{player} refused: the run is not over"); return; }
        if (old.Leader != key) { WfLog($"NewRun from P{player} refused: only the leader starts a new run"); return; }
        WarfrontBeginNewRun(s, "the leader started it");
    }

    /// <summary>The new run itself, with no leader check (the leader's command, and the dedicated server's own timer: GameServer.WarfrontDedicated.cs). Only when the run is over.</summary>
    void WarfrontBeginNewRun(WarfrontServerState s, string why)
    {
        if (!s.Campaign.IsOver || s.Leaving) return;
        if (s.NoSave) { WfLog($"a new run is refused: {s.NoSaveText} (the campaign file is not ours to replace)"); return; }
        // The wiring, the progression and the co-op rules belong to one campaign: a new run is a new stage process on the empty run (the supervisor reads it; the clients rejoin through Stage Go).
        WfSaveFlush();
        try { File.Delete(Path.Combine(s.Dir, WarfrontSquadsFile)); } catch (IOException) { }
        var fresh = WarfrontRunFiles.NewRun(s.Dir);
        s.Leaving = true;
        WfLog($"a new run (seed {fresh.RunSeed:X}) on the ship ({why}): this stage restarts on it");
        WarfrontStartGo(s, WarfrontStagePlan.HasShip(s.Map) ? s.Map : WarfrontStagePlan.FirstMap, null);
    }
}
