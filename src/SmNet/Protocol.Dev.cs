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
    /// <summary>Ends the current match now (the normal end screen, then the next match or map).</summary>
    EndMatch = 4,
    /// <summary>Restarts the match on the same map with fresh scores.</summary>
    RestartMatch = 5,
    /// <summary>Ends the match and hurries the end screen to the next match (the next map on a rotating server).</summary>
    SkipMatch = 6,
    /// <summary>Teleports the requester: argument <see cref="DevTeleportKind"/>, extra the side, point index or player id.</summary>
    Teleport = 7,
    /// <summary>Kills the requester (the Deploy's suicide).</summary>
    KillMe = 8,
    /// <summary>Kills every bot: argument 0 the Loyalists', 1 Chaos', 2 both sides'.</summary>
    KillBots = 9,
    /// <summary>Sets the time scale: argument an index into <see cref="DevTools.TimeScales"/>.</summary>
    SetTimeScale = 10,
    /// <summary>Sets every bot's accuracy: extra permille 0 to 1000, <see cref="Protocol.DevNoValue"/> back to each bot's own.</summary>
    SetBotAccuracy = 11,
    /// <summary>Sets the damage every bot deals: extra permille 0 to 1000 (1000 is normal).</summary>
    SetBotDamage = 12,
    /// <summary>Sets the distance in metres a bot opens fire from: extra 5 to 1000, 0 back to each weapon's own range.</summary>
    SetBotRange = 13,
    /// <summary>Asks the server to send the cancel table again (cancel-editor: the client's table hash differs from the server's).</summary>
    CancelSync = 14,
    /// <summary>Protocol 85: ends the current match at once and starts a new one on another map; argument an index into <c>DevMapPicks.For</c> (the maps of the server's mode, as the panel lists them). The server only changes map when it runs under its supervisor (a developer-tools server on loopback does); clients rejoin as they do after a rotation.</summary>
    PickMap = 15,
}

/// <summary>The server's answer to the last request, shown under the panel (protocol 80).</summary>
public enum DevNote : byte
{
    None = 0, Applied = 1, BotStarting = 2, BotRemoved = 3, NoBotToRemove = 4, SideFull = 5, ServerFull = 6, LauncherMissing = 7, LaunchFailed = 8, NotInThisMode = 9, TooManyStarting = 10, TooFast = 11,
    NotPlaying = 12, TeleportBlocked = 13, BadValue = 14,
    /// <summary>Protocol 85: this server cannot change its map (it does not run under its supervisor, or it has no map list).</summary>
    NoMapChange = 15,
}

/// <summary>Side arguments of <see cref="DevCommandKind.AddBot"/> and <see cref="DevCommandKind.RemoveBot"/>.</summary>
public static class DevSides
{
    public const byte Loyalists = 0, Chaos = 1;
}

/// <summary>
/// Client to server (protocol 80; ours): a developer-tools request from the panel. The server obeys it only when it was started with <c>--dev-tools</c>, listens on loopback alone and the
/// request comes from a loopback address (<c>GameServer.DevTools.cs</c>); otherwise it ignores it (and says so once in its log). Session u32, kind u8, argument u8, value u8, extra u16. 10 bytes.
/// </summary>
public sealed record DevCommand(uint Session, DevCommandKind Kind, byte Argument, bool Value, int Extra = 0) : Message;

/// <summary>
/// Server to client (protocol 80; ours): the developer tools' state, sent once a second and whenever it changes, only by a server that has them on and only to a player on a loopback address (never to a bot or
/// spectator; a server without <c>--dev-tools</c> sends nothing). The panel shows exactly this. Flags u8 (<see cref="DevToolsFlags"/>), the bots the tools started per side (u8, u8), the players per side (u8, u8),
/// the answer to the last request (<see cref="DevNote"/>) with a counter that rises with each answer, then the time scale (an index into <see cref="DevTools.TimeScales"/>), the bots' accuracy and damage in permille (accuracy <see cref="Protocol.DevNoValue"/>: each bot's own) and the bots' range in metres (0: each weapon's own). Bots get it too, since they take the bot values and the time scale from it. 15 bytes.
/// </summary>
public sealed record DevState(DevToolsFlags Flags, int ToolBotsLoyalists, int ToolBotsChaos, int PlayersLoyalists, int PlayersChaos, DevNote Note, byte NoteSerial,
    int TimeScale = 0, int BotAccuracy = Protocol.DevNoValue, int BotDamage = 1000, int BotRange = 0) : Message;

public static partial class Protocol
{
    /// <summary>Bytes of a DevCommand.</summary>
    public const int DevCommandBytes = 1 + 4 + 1 + 1 + 1 + 2;
    /// <summary>A bot accuracy on the wire that means "not set by the tools".</summary>
    public const int DevNoValue = 0xFFFF;
    /// <summary>Bytes of a DevState.</summary>
    public const int DevStateBytes = 1 + 1 + 2 + 2 + 1 + 1 + 1 + 2 + 2 + 2;

    static void WriteDevCommand(Writer w, DevCommand c)
    {
        w.U8((byte)Kind.DevCommand); w.U32(c.Session); w.U8((byte)c.Kind); w.U8(c.Argument); w.U8(c.Value ? (byte)1 : (byte)0); w.U16((ushort)Math.Clamp(c.Extra, 0, ushort.MaxValue));
    }

    static DevCommand? ReadDevCommand(ref Reader r)
    {
        uint session = r.U32();
        int kind = r.U8(), argument = r.U8(), value = r.U8(), extra = r.U16();
        if (kind is < (int)DevCommandKind.SetFlag or > (int)DevCommandKind.PickMap || value > 1) return null;
        return new DevCommand(session, (DevCommandKind)kind, (byte)argument, value == 1, extra);
    }

    static void WriteDevState(Writer w, DevState s)
    {
        w.U8((byte)Kind.DevState); w.U8((byte)(s.Flags & DevTools.All));
        w.U8((byte)Math.Clamp(s.ToolBotsLoyalists, 0, MaxPlayers)); w.U8((byte)Math.Clamp(s.ToolBotsChaos, 0, MaxPlayers));
        w.U8((byte)Math.Clamp(s.PlayersLoyalists, 0, MaxPlayers)); w.U8((byte)Math.Clamp(s.PlayersChaos, 0, MaxPlayers));
        w.U8((byte)s.Note); w.U8(s.NoteSerial);
        w.U8((byte)Math.Clamp(s.TimeScale, 0, DevTools.TimeScales.Length - 1));
        w.U16((ushort)Math.Clamp(s.BotAccuracy, 0, ushort.MaxValue)); w.U16((ushort)Math.Clamp(s.BotDamage, 0, 1000)); w.U16((ushort)Math.Clamp(s.BotRange, 0, ushort.MaxValue));
    }

    static DevState? ReadDevState(ref Reader r)
    {
        int flags = r.U8(), bl = r.U8(), bc = r.U8(), pl = r.U8(), pc = r.U8(), note = r.U8();
        byte serial = r.U8();
        int scale = r.U8(), accuracy = r.U16(), damage = r.U16(), range = r.U16();
        if ((flags & ~(int)DevTools.All) != 0 || bl > MaxPlayers || bc > MaxPlayers || pl > MaxPlayers || pc > MaxPlayers || note > (int)DevNote.NoMapChange
            || scale >= DevTools.TimeScales.Length || damage > 1000 || (accuracy > 1000 && accuracy != DevNoValue)) return null;
        return new DevState((DevToolsFlags)flags, bl, bc, pl, pc, (DevNote)note, serial, scale, accuracy, damage, range);
    }
}
