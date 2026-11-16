using System.Text;
using SmSim.Extras.Warfront.Coop;

namespace SmNet.Extras.Warfront;

/// <summary>
/// The co-op rules' wire (slice W12; ours). Its own file and tags; <c>WarfrontWire.cs</c> (W5a) is not edited. Envelope tag <see cref="EnvelopeTag"/> (Coop, per client, on change and every 2 s) and command tag
/// <see cref="CommandTag"/> (kind 17; W5a's is 40, W10's 41). Decoding is <c>TryDecode</c> only and never throws: a malformed buffer is a false.
/// </summary>
public static class WarfrontCoopWire
{
    public const byte EnvelopeTag = 46;
    public const byte CommandTag = 42;
    public const int MaxName = 24;
    public const int MaxAwaited = WarfrontCoop.MaxAwaitedOnWire;

    public const byte FlagYouLead = 1, FlagLeaderChanged = 2, FlagGrantApplied = 4;
    const byte KnownFlags = FlagYouLead | FlagLeaderChanged | FlagGrantApplied;

    /// <summary>The Coop section's content. <see cref="WatchKind"/> and <see cref="WatchId"/> name the camera target of a spectating client (kind 0: none).</summary>
    public readonly record struct Section(WarfrontCoopRole Role, WarfrontWatchKind WatchKind, byte WatchId, byte Flags, byte GateSecondsLeft, WarfrontAwaited[] Awaited)
    {
        public bool YouLead => (Flags & FlagYouLead) != 0;
        public bool LeaderChanged => (Flags & FlagLeaderChanged) != 0;
        public bool GrantApplied => (Flags & FlagGrantApplied) != 0;
    }

    public enum CommandKind : byte { ConfirmClass = 0, Watch = 1 }

    public readonly record struct Command(CommandKind Kind, int Step);

    public static Section FromView(WarfrontCoopView v)
    {
        byte flags = (byte)((v.YouLead ? FlagYouLead : 0) | (v.LeaderChanged ? FlagLeaderChanged : 0) | (v.GrantAppliedThisStop ? FlagGrantApplied : 0));
        return new Section(v.Role, v.Watch.Kind, (byte)Math.Clamp(v.Watch.Id, 0, 255), flags, (byte)Math.Clamp(v.ClassGateSecondsLeft, 0, 255), v.Awaited.Take(MaxAwaited).ToArray());
    }

    public static byte[] Encode(in Section s)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)s.Role);
        ms.WriteByte((byte)s.WatchKind);
        ms.WriteByte(s.WatchId);
        ms.WriteByte((byte)(s.Flags & KnownFlags));
        ms.WriteByte(s.GateSecondsLeft);
        var awaited = s.Awaited ?? Array.Empty<WarfrontAwaited>();
        int n = Math.Min(awaited.Length, MaxAwaited);
        ms.WriteByte((byte)n);
        for (int i = 0; i < n; i++)
        {
            var name = Encoding.UTF8.GetBytes(awaited[i].Name ?? "");
            int len = Math.Min(name.Length, MaxName);
            while (len > 0 && len < name.Length && (name[len] & 0xC0) == 0x80) len--;   // never cut inside a character
            ms.WriteByte((byte)len);
            ms.Write(name, 0, len);
            ms.WriteByte((byte)awaited[i].Reason);
            ms.WriteByte((byte)Math.Clamp(awaited[i].PicksLeft, 0, 255));
        }
        return ms.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out Section section)
    {
        section = default;
        if (data.Length < 6) return false;
        byte role = data[0], kind = data[1], watchId = data[2], flags = data[3], gate = data[4], count = data[5];
        if (role > (byte)WarfrontCoopRole.AwaitPicks || kind > (byte)WarfrontWatchKind.Marine || (flags & ~KnownFlags) != 0 || count > MaxAwaited) return false;
        int at = 6;
        var list = new WarfrontAwaited[count];
        for (int i = 0; i < count; i++)
        {
            if (at >= data.Length) return false;
            int len = data[at++];
            if (len > MaxName || at + len + 2 > data.Length) return false;
            string name = Encoding.UTF8.GetString(data.Slice(at, len));
            at += len;
            byte reason = data[at++], picks = data[at++];
            if (reason > (byte)WarfrontAwaitReason.Picks) return false;
            list[i] = new WarfrontAwaited(name, (WarfrontAwaitReason)reason, picks);
        }
        if (at != data.Length) return false;
        section = new Section((WarfrontCoopRole)role, (WarfrontWatchKind)kind, watchId, flags, gate, list);
        return true;
    }

    /// <summary>ConfirmClass is the kind byte alone; Watch is the kind and an i8 step (-1 previous, +1 next).</summary>
    public static byte[] EncodeCommand(in Command c) =>
        c.Kind == CommandKind.Watch ? new[] { (byte)CommandKind.Watch, unchecked((byte)(sbyte)(c.Step < 0 ? -1 : 1)) } : new[] { (byte)CommandKind.ConfirmClass };

    public static bool TryDecodeCommand(ReadOnlySpan<byte> data, out Command command)
    {
        command = default;
        if (data.Length == 1 && data[0] == (byte)CommandKind.ConfirmClass) { command = new(CommandKind.ConfirmClass, 0); return true; }
        if (data.Length == 2 && data[0] == (byte)CommandKind.Watch)
        {
            int step = (sbyte)data[1];
            if (step is not (-1 or 1)) return false;
            command = new(CommandKind.Watch, step);
            return true;
        }
        return false;
    }
}
