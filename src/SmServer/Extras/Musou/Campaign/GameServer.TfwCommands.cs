using SmNet.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// A client's commands on the server (TF6; design section H). The core takes at most 8 a second per client (<see cref="OnExtrasCommand"/>); here each sequence counts once: a client resends until tag 7
/// echoes it, so a copy, or an older sequence, is dropped and only a new one reaches the run's queue. The echo moves only when the command is queued: with the queue full, the client's resend tries again.
/// </summary>
public sealed partial class GameServer
{
    readonly Dictionary<int, ushort> _tfwCommandEcho = new();

    /// <summary>Well-formed commands dropped as copies or older than the newest of their client, and commands refused by a full queue.</summary>
    public int TfwCommandsRepeated { get; private set; }
    public int TfwCommandsRefused { get; private set; }

    /// <summary>The newest command applied of <paramref name="player"/> (what its tag 7 echoes; 0 before any).</summary>
    public ushort TfwCommandEcho(int player) => _tfwCommandEcho.GetValueOrDefault(player);

    /// <summary>A client's command (message kind 17): queued for the next tick, only under the war's tag, well formed and new; the session and the rate are the core's.</summary>
    void OnTfwCommand(int player, byte tag, byte[] payload)
    {
        if (World.Tfw is not { } run || tag != TfwWire.CommandTag || TfwWire.ReadCommand(payload) is not { } command) return;
        if (_tfwCommandEcho.TryGetValue(player, out ushort newest) && !TfwWire.IsNewer(command.Sequence, newest)) { TfwCommandsRepeated++; return; }
        if (!run.Enqueue(player, command)) { TfwCommandsRefused++; return; }
        _tfwCommandEcho[player] = command.Sequence;
    }
}
