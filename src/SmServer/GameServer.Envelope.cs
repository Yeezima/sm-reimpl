using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// The extras envelope on the server (added content E3, ported by the loyalist barks without a protocol bump: extras wire 2; docs/added-content.md). Once per tick, after the pickups, the server calls
/// the hook <see cref="ExtrasSend"/>, which added content implements in its <c>Extras/</c> folder to send its sections through
/// <see cref="SendExtras"/>. The core sends nothing itself. A faithful server (<see cref="SimConfig.Extras"/> None) never calls the hook and
/// <see cref="SendExtras"/> refuses there, so no message of kind 16 ever leaves it.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Extras messages sent (a message to several clients counts once).</summary>
    public int ExtrasMessagesSent { get; private set; }
    /// <summary>Bytes of extras messages sent (each message counted once), and the largest message.</summary>
    public long ExtrasBytesSent { get; private set; }
    public int LargestExtrasMessage { get; private set; }
    /// <summary>Extras messages refused: asked for by a server whose match runs no added content, or with no section.</summary>
    public int ExtrasRefused { get; private set; }

    /// <summary>Runs the added content's sends for this tick (only when the match runs added content and a client is connected).</summary>
    void SendExtrasDue()
    {
        if (World.Config.Extras == ContentExtras.None || _peers.Count == 0) return;
        ExtrasSend();   // EXTRAS-HOOK
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md): sends this tick's extras sections through <see cref="SendExtras"/>. Called once per server
    /// tick after the pickups, only when <see cref="SimConfig.Extras"/> is not None and a client is connected; each implementation returns at
    /// once unless its own flag is set. Implemented in an <c>Extras/</c> folder.
    /// </summary>
    partial void ExtrasSend();

    /// <summary>Added-content commands passed to the hook, and commands dropped: the match reads none, a spectator's, or over the rate.</summary>
    public int ExtrasCommandsAccepted { get; private set; }
    public int ExtrasCommandsDropped { get; private set; }

    /// <summary>The most commands a client gets through per second (ours; design: at most 8 a second), as a token bucket of this burst.</summary>
    const double ExtrasCommandRate = 8;

    /// <summary>
    /// One client's command (message kind 17; the session is checked in <see cref="Dispatch"/>). A faithful or extended server (no flag of <c>ContentExtras.CommandUsers</c>) drops
    /// it without reading it, as it drops the action key; nothing acts on the world and nothing is sent back.
    /// </summary>
    void OnExtrasCommand(Peer peer, ExtrasCommandMessage command)
    {
        if ((World.Config.Extras & ContentExtras.CommandUsers) == 0 || peer.Spectator) { ExtrasCommandsDropped++; return; }
        peer.Commands ??= new TokenBucket(ExtrasCommandRate, ExtrasCommandRate);
        if (!peer.Commands.TryTake(_clock.Now)) { ExtrasCommandsDropped++; return; }
        ExtrasCommandsAccepted++;
        ExtrasCommand(peer.PlayerId, command.Tag, command.Payload);   // EXTRAS-HOOK
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md section 4): a command of <paramref name="player"/> arrived. Runs on the packet path between ticks: an implementation
    /// queues it for the next tick and treats the bytes as untrusted. Implemented in an <c>Extras/</c> folder; each feature skips the tags it does not own.
    /// </summary>
    partial void ExtrasCommand(int player, byte tag, byte[] payload);

    /// <summary>
    /// Sends <paramref name="update"/> to every client, or only to <paramref name="toPlayer"/> (a per-recipient section). False, and nothing
    /// sent, when the match runs no added content (a faithful server never sends kind 16), the message has no section, or the player is not
    /// connected. An invalid message (reserved tag 0, over <see cref="Protocol.MaxExtrasMessageBytes"/>) throws ArgumentException, which the
    /// tick's guard logs: split large payloads over several messages.
    /// </summary>
    public bool SendExtras(ExtrasUpdate update, int? toPlayer = null)
    {
        if (World.Config.Extras == ContentExtras.None || update.Sections is not { Length: > 0 }) { ExtrasRefused++; return false; }
        var packet = Protocol.Write(update);
        int sent = 0;
        foreach (var (connection, peer) in _peers)
        {
            if (toPlayer is { } id && peer.PlayerId != id) continue;
            _transport.Send(connection, packet);
            sent++;
        }
        if (sent == 0) return false;
        ExtrasMessagesSent++;
        ExtrasBytesSent += packet.Length;
        LargestExtrasMessage = Math.Max(LargestExtrasMessage, packet.Length);
        return true;
    }
}
