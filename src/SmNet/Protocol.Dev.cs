using SmSim;

namespace SmNet;

/// <summary>What a developer-tools request asks for (protocol 80).</summary>
public enum DevCommandKind : byte
{
    /// <summary>Sets one <see cref="DevToolsFlags"/> bit (the argument) on or off (the value).</summary>
    SetFlag = 1,
    /// <summary>Starts a test bot (a headless client process of the server's machine) on a side: argument 0 the Loyalists (Space Marines), 1 Chaos.</summary>
    AddBot = 2,
    /// <summary>Stops the newest bot the tools started on a side: argument 0 the Loyalists, 1 Chaos.</summary>
    RemoveBot = 3,
}

/// <summary>The server's answer to the last request, shown under the panel (protocol 80).</summary>
public enum DevNote : byte
{
    None = 0, Applied = 1, BotStarting = 2, BotRemoved = 3, NoBotToRemove = 4, SideFull = 5, ServerFull = 6, LauncherMissing = 7, LaunchFailed = 8, NotInThisMode = 9, TooManyStarting = 10, TooFast = 11,
}

/// <summary>Side arguments of <see cref="DevCommandKind.AddBot"/> and <see cref="DevCommandKind.RemoveBot"/>.</summary>
public static class DevSides
{
    public const byte Loyalists = 0, Chaos = 1;
}

/// <summary>
/// Client to server (protocol 80; ours): a developer-tools request from the panel. The server obeys it only when it was started with <c>--dev-tools</c>, listens on loopback alone and the
/// request comes from a loopback address (<c>GameServer.DevTools.cs</c>); otherwise it ignores it (and says so once in its log). Session u32, kind u8, argument u8, value u8. 8 bytes.
/// </summary>
public sealed record DevCommand(uint Session, DevCommandKind Kind, byte Argument, bool Value) : Message;

/// <summary>
/// Server to client (protocol 80; ours): the developer tools' state, sent once a second and whenever it changes, only by a server that has them on and only to a player on a loopback address (never to a bot or
/// spectator; a server without <c>--dev-tools</c> sends nothing). The panel shows exactly this. Flags u8 (<see cref="DevToolsFlags"/>), the bots the tools started per side (u8, u8), the players per side (u8, u8),
/// the answer to the last request (<see cref="DevNote"/>) with a counter that rises with each answer. 8 bytes.
/// </summary>
public sealed record DevState(DevToolsFlags Flags, int ToolBotsLoyalists, int ToolBotsChaos, int PlayersLoyalists, int PlayersChaos, DevNote Note, byte NoteSerial) : Message;

public static partial class Protocol
{
    /// <summary>Bytes of a DevCommand.</summary>
    public const int DevCommandBytes = 1 + 4 + 1 + 1 + 1;
    /// <summary>Bytes of a DevState.</summary>
    public const int DevStateBytes = 1 + 1 + 2 + 2 + 1 + 1;

    static void WriteDevCommand(Writer w, DevCommand c)
    {
        w.U8((byte)Kind.DevCommand); w.U32(c.Session); w.U8((byte)c.Kind); w.U8(c.Argument); w.U8(c.Value ? (byte)1 : (byte)0);
    }

    static DevCommand? ReadDevCommand(ref Reader r)
    {
        uint session = r.U32();
        int kind = r.U8(), argument = r.U8(), value = r.U8();
        if (kind is < (int)DevCommandKind.SetFlag or > (int)DevCommandKind.RemoveBot || value > 1) return null;
        return new DevCommand(session, (DevCommandKind)kind, (byte)argument, value == 1);
    }

    static void WriteDevState(Writer w, DevState s)
    {
        w.U8((byte)Kind.DevState); w.U8((byte)(s.Flags & DevTools.All));
        w.U8((byte)Math.Clamp(s.ToolBotsLoyalists, 0, MaxPlayers)); w.U8((byte)Math.Clamp(s.ToolBotsChaos, 0, MaxPlayers));
        w.U8((byte)Math.Clamp(s.PlayersLoyalists, 0, MaxPlayers)); w.U8((byte)Math.Clamp(s.PlayersChaos, 0, MaxPlayers));
        w.U8((byte)s.Note); w.U8(s.NoteSerial);
    }

    static DevState? ReadDevState(ref Reader r)
    {
        int flags = r.U8(), bl = r.U8(), bc = r.U8(), pl = r.U8(), pc = r.U8(), note = r.U8();
        byte serial = r.U8();
        if ((flags & ~(int)DevTools.All) != 0 || bl > MaxPlayers || bc > MaxPlayers || pl > MaxPlayers || pc > MaxPlayers || note > (int)DevNote.TooFast) return null;
        return new DevState((DevToolsFlags)flags, bl, bc, pl, pc, (DevNote)note, serial);
    }
}
