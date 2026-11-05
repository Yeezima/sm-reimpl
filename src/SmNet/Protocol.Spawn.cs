using SmSim;

namespace SmNet;

/// <summary>
/// Client to server (protocol 57, fix/pvp-deploy-spawn; classic PvP): where the player wants to respawn (<see cref="World.SetSpawnChoice"/>). The choice stands until
/// changed and is checked again at each respawn on the server; a client resends its current choice while dead (the message is small and idempotent, so there is no
/// acknowledgement). Kind u8 (<see cref="SpawnChoiceKind"/>), target u16 (a control point's index, a carrier's player id; 0 for the spawn room). 8 bytes.
/// </summary>
public sealed record SpawnRequest(uint Session, SpawnChoiceKind Kind, int Target) : Message;

public static partial class Protocol
{
    /// <summary>Bytes of a SpawnRequest: kind byte, session u32, choice kind u8, target u16.</summary>
    public const int SpawnRequestBytes = 1 + 4 + 1 + 2;

    static void WriteSpawnRequest(Writer w, SpawnRequest s)
    {
        if (s.Kind > SpawnChoiceKind.Homer) throw new ArgumentException($"Unknown spawn choice {s.Kind}.");
        if (s.Target is < 0 or > ushort.MaxValue) throw new ArgumentException("A spawn choice's target is 0..65535.");
        w.U8((byte)Kind.SpawnRequest); w.U32(s.Session); w.U8((byte)s.Kind); w.U16((ushort)s.Target);
    }

    static SpawnRequest? ReadSpawnRequest(ref Reader r)
    {
        uint session = r.U32();
        int kind = r.U8(), target = r.U16();
        if (kind > (int)SpawnChoiceKind.Homer) return null;
        return new SpawnRequest(session, (SpawnChoiceKind)kind, target);
    }
}
