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
        if (tag == WarfrontWire.CoopCommandTag) { bool done = false; WarfrontCoopCommand(player, tag, payload, ref done); handled = true; return; }
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
            case WarfrontClientCommandKind.Pick:
                break;   // W8 reads it through its own seam; until then it is dropped
        }
    }

    /// <summary>The leader NewRun, only when the run is over: an empty run on the same server (the connected players join it in their old order).</summary>
    void WarfrontNewRun(int player)
    {
        if (_wf is not { } s || !s.KeyOf.TryGetValue(player, out ulong key)) return;
        var old = s.Campaign;
        if (!old.IsOver) { WfLog($"NewRun from P{player} refused: the run is not over"); return; }
        if (old.Leader != key) { WfLog($"NewRun from P{player} refused: only the leader starts a new run"); return; }
        var fresh = WarfrontRunFiles.NewRun(s.Dir);
        foreach (var k in old.JoinOrder)
            if (s.KeyOf.ContainsValue(k) && old.Player(k) is { } p) fresh.Join(k, p.Name, out _);
        s.Campaign = fresh;
        s.ResultsBytes = null;
        s.HubDirty = true;
        s.LastHub.Clear();
        WarfrontSave.Write(s.Dir, fresh);
        WfLog($"a new run (seed {fresh.RunSeed:X}) on the ship; {s.KeyOf.Count} player(s) carried over");
        if (!WarfrontStagePlan.HasShip(s.Map)) WarfrontStartGo(s, WarfrontStagePlan.FirstMap, null);
        else WarfrontCoopShipStop();
    }
}
