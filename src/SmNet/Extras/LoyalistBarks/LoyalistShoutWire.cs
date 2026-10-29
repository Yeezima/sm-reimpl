using System.Buffers.Binary;

namespace SmNet.Extras.LoyalistBarks;

/// <summary>What a shout is (one kind today).</summary>
public enum LoyalistShoutKind : byte { BattleCry = 1 }

/// <summary>
/// A player's accepted shout: who, the server tick it was accepted on (its identity: clients play each once), its kind and the player's shout
/// sequence (1, 2, ... wrapping at 256; the take is picked from it, so every client hears the same take and never the same take twice in a row).
/// </summary>
public readonly record struct LoyalistShout(int PlayerId, uint Tick, LoyalistShoutKind Kind, byte Sequence);

/// <summary>
/// Added content "loyalist barks": the FOR THE EMPEROR key on the wire. A client holds <c>InputButtons.ExtraAction</c> (input bit 13, the T key) for
/// <see cref="HoldTicks"/> ticks; the server accepts its press edge from a living loyalist Space Marine at most once per <see cref="CooldownSeconds"/> and
/// tells every client in its own section of the extras envelope (<see cref="Tag"/>), repeated in <see cref="ResendTicks"/> consecutive messages so a lost
/// datagram does not lose it (clients play each (player, tick) once). Payload: <c>[u8 n]{[u16 player][u32 tick][u8 kind][u8 sequence]}</c>. A server
/// whose match runs no loyalist barks never sends it and drops the bit. All values ours.
/// </summary>
public static class LoyalistShoutWire
{
    /// <summary>The envelope tag of the loyalist barks (gore has 1).</summary>
    public const byte Tag = 2;
    public const int RecordBytes = 8;
    /// <summary>Most shouts in one section (16 players, each at most once per cooldown).</summary>
    public const int MaxShouts = 32;
    /// <summary>Server ticks a shout is repeated in (0.1 s at 30 Hz; ours).</summary>
    public const int ResendTicks = 3;
    /// <summary>Ticks the client holds the bit for one press, so one lost or late input does not lose it (the server acts on the edge only; ours).</summary>
    public const int HoldTicks = 4;
    /// <summary>The least time between two battle cries of one player (seconds; ours: about a take's length). The client keeps the same rule in ticks.</summary>
    public const double CooldownSeconds = 2.0;
    /// <summary>A shout older than this when it arrives is not played (seconds; ours: a late cry would sound out of place).</summary>
    public const double MaxAgeSeconds = 1.0;

    /// <summary>The cooldown in ticks at <paramref name="tickRate"/>.</summary>
    public static uint CooldownTicks(int tickRate) => (uint)Math.Ceiling(CooldownSeconds * tickRate);

    public static byte[] Write(IReadOnlyList<LoyalistShout> shouts)
    {
        int n = Math.Min(shouts.Count, MaxShouts);
        var bytes = new byte[1 + n * RecordBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var s = shouts[shouts.Count - n + i];   // the newest when over the cap
            var span = bytes.AsSpan(1 + i * RecordBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)s.PlayerId);
            BinaryPrimitives.WriteUInt32LittleEndian(span[2..], s.Tick);
            span[6] = (byte)s.Kind;
            span[7] = s.Sequence;
        }
        return bytes;
    }

    /// <summary>The shouts of a payload; null for a malformed one (wrong length, unknown kind).</summary>
    public static LoyalistShout[]? Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (n > MaxShouts || payload.Length != 1 + n * RecordBytes) return null;
        var shouts = new LoyalistShout[n];
        for (int i = 0; i < n; i++)
        {
            var span = payload.Slice(1 + i * RecordBytes, RecordBytes);
            var kind = (LoyalistShoutKind)span[6];
            if (kind != LoyalistShoutKind.BattleCry) return null;
            shouts[i] = new LoyalistShout(BinaryPrimitives.ReadUInt16LittleEndian(span), BinaryPrimitives.ReadUInt32LittleEndian(span[2..]), kind, span[7]);
        }
        return shouts;
    }

    /// <summary>Whether a player whose last cry was at <paramref name="last"/> (null: none) may cry at <paramref name="tick"/>.</summary>
    public static bool CooledDown(uint? last, uint tick, int tickRate) =>
        last is not { } l || tick < l || (tick - l) >= CooldownTicks(tickRate);
}

/// <summary>
/// The client's side of the shouts (engine-free; tested): plays each (player, tick) once, skips the own player's echo (it shouted at the key press)
/// but notes its sequence (<see cref="OwnSequence"/>, from which the next own shout's take is predicted), and skips shouts that arrive too late.
/// </summary>
public sealed class LoyalistShoutInbox
{
    readonly HashSet<(int, uint)> _seen = new();
    readonly Queue<(int, uint)> _order = new();
    const int Remember = 256;

    /// <summary>The sequence of the own player's last shout the server accepted (0 before the first).</summary>
    public byte OwnSequence { get; private set; }

    /// <summary>The sequence the own player's next shout will most likely get (the server's counter is the truth; a refused press shifts it until the next echo).</summary>
    public byte NextOwnSequence => unchecked((byte)(OwnSequence + 1));

    /// <summary>
    /// Adds to <paramref name="play"/> the shouts of <paramref name="shouts"/> to play now: new ones, not the own player's, at most
    /// <see cref="LoyalistShoutWire.MaxAgeSeconds"/> old against <paramref name="serverTick"/> (the latest snapshot's tick).
    /// </summary>
    public void Take(IEnumerable<LoyalistShout> shouts, int ownId, uint serverTick, int tickRate, List<LoyalistShout> play)
    {
        foreach (var s in shouts)
        {
            if (!_seen.Add((s.PlayerId, s.Tick))) continue;
            _order.Enqueue((s.PlayerId, s.Tick));
            while (_order.Count > Remember) _seen.Remove(_order.Dequeue());
            if (s.PlayerId == ownId) { OwnSequence = s.Sequence; continue; }
            if (serverTick > s.Tick && serverTick - s.Tick > LoyalistShoutWire.MaxAgeSeconds * tickRate) continue;
            play.Add(s);
        }
    }

    /// <summary><see cref="Take(IEnumerable{LoyalistShout}, int, uint, int, List{LoyalistShout})"/> into a new list (tests).</summary>
    public List<LoyalistShout> Take(IEnumerable<LoyalistShout> shouts, int ownId, uint serverTick, int tickRate)
    {
        var play = new List<LoyalistShout>();
        Take(shouts, ownId, serverTick, tickRate, play);
        return play;
    }
}
