using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// cancel-editor (protocol 80; owner's request of 2026-11-17; ours, a test aid): the Classic PvP cancel table (<see cref="CancelData"/>) edited live from the F8 panel. Same gate as the other developer tools
/// (<see cref="DevToolsGate"/>: <c>--dev-tools</c>, loopback bind, loopback requester); an edit replaces the table of the world's config at once, so the very next tick of every player is played by it, and
/// is sent to every loopback client so their prediction plays by the same table. A server without the tools never reads an edit and sends nothing here; the tick loop pays the one null check of
/// <see cref="DevToolsTick"/>.
/// </summary>
public sealed partial class GameServer
{
    TimeSpan _nextCancelBeacon;
    /// <summary>Cancel edits taken per peer: a burst for an import (a whole file is a few dozen datagrams), then a steady rate.</summary>
    const double CancelEditBurst = 60, CancelEditRate = 30;

    /// <summary>The cancel table in force (null: the shipped default).</summary>
    public CancelData CancelTable => World.Config.Cancel.Active;

    /// <summary>Sets the table (a table equal to the shipped default becomes "none": the sim then pays nothing). For the loopback-only startup option and the tests; edits from clients come through <see cref="OnDevCancelEdit"/>.</summary>
    public void SetCancelTable(CancelData? table)
    {
        World.Config.Cancel.Set(table is null || table.Hash == CancelData.Shipped.Hash ? null : table);
        _devDirty = true;
        if (DevToolsOn) SendCancelSync(null);
    }

    void OnDevCancelEdit(int connection, Peer peer, DevCancelEdit edit)
    {
        peer.LastHeard = _clock.Now;
        if (!DevToolsOn) { NoteDevRefused("this server was not started with --dev-tools on a loopback address"); return; }
        var remote = _transport.EndPointOf(connection);
        if (!DevToolsGate.FromLoopback(remote)) { NoteDevRefused($"{remote?.Address} is not a loopback address"); return; }
        if (peer.Spectator) return;
        peer.CancelEdits ??= new TokenBucket(CancelEditRate, CancelEditBurst);
        if (!peer.CancelEdits.TryTake(_clock.Now)) { Answer(DevNote.TooFast); SendDevStates(); return; }
        var table = CancelTable;
        int applied = 0, bad = 0;
        switch (edit.Op)
        {
            case DevCancelOp.ResetAll: table = CancelData.Shipped; applied = 1; break;
            case DevCancelOp.Apply:
                foreach (var e in edit.Entries)
                {
                    if (Protocol.FromWire(e, World.Config) is not { } ch || ch.Cell.State == CancelState.Inherit && ch.Scope.IsBase) { bad++; continue; }
                    table = table.With(ch.Scope, ch.Row, ch.Col, ch.Cell); applied++;
                }
                break;
            case DevCancelOp.ResetCell:
                foreach (var e in edit.Entries)
                {
                    if (Protocol.FromWire(e, World.Config) is not { } ch) { bad++; continue; }
                    table = table.Reset(ch.Scope, ch.Row, ch.Col); applied++;
                }
                break;
        }
        if (applied > 0 || bad == 0)
        {
            bool changed = table.Hash != CancelTable.Hash;
            World.Config.Cancel.Set(table.Hash == CancelData.Shipped.Hash ? null : table);
            if (changed) Log?.Invoke($"dev tools: cancel table {edit.Op.ToString().ToLowerInvariant()}: {applied} cell(s), now {table.DiffFromShipped().Count} differ from the shipped default (hash {table.Hash:x8}; player {peer.PlayerId})");
        }
        Answer(bad > 0 && applied == 0 ? DevNote.BadValue : DevNote.Applied);
        _devDirty = true;
        SendCancelSync(null);
        SendDevStates();
    }

    /// <summary>Sends the table (its changes against the shipped default, in chunks) to one connection, or to every loopback player and bot (null).</summary>
    void SendCancelSync(int? only)
    {
        if (!DevToolsOn) return;
        var chunks = Protocol.SyncChunks(CancelTable, World.Config);
        if (chunks is null) { Log?.Invoke("dev tools: the cancel table has too many changes to sync to clients"); return; }
        var packets = chunks.Select(Protocol.Write).ToList();
        foreach (var (connection, peer) in _peers)
        {
            if (peer.Spectator || (only is { } o && o != connection) || !DevToolsGate.FromLoopback(_transport.EndPointOf(connection))) continue;
            foreach (var p in packets) _transport.Send(connection, p);
        }
    }

    /// <summary>Once a second to every loopback client: the hash of the table in force, so a client with a different one asks for it (<see cref="DevCommandKind.CancelSync"/>).</summary>
    void CancelBeaconTick()
    {
        if (_clock.Now < _nextCancelBeacon) return;
        _nextCancelBeacon = _clock.Now + TimeSpan.FromSeconds(1);
        var packet = Protocol.Write(new DevCancelSync(CancelTable.Hash, 0, 0, []));
        foreach (var (connection, peer) in _peers)
            if (!peer.Spectator && DevToolsGate.FromLoopback(_transport.EndPointOf(connection))) _transport.Send(connection, packet);
    }
}
