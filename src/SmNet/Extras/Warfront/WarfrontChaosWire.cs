using System.Numerics;
using SmSim.Extras.Warfront.Chaos;

namespace SmNet.Extras.Warfront;

/// <summary>
/// The Chaos section (Chaos Warfront C4; ours): envelope tag <see cref="Tag"/> (51), sent only by a Warfront stage whose army is Chaos, to every identified client. The first byte is a kind:
/// <list type="bullet">
/// <item>kind 1, the breach list, on every change and once a second: <c>[u8 1][u8 n]{[u8 id][i16 x dm][i16 y dm][i16 z dm][u8 state 0 opening, 1 open, 2 closed][u8 Daemons alive]}</c>,
/// n at most <see cref="MaxBreaches"/>, 2 + 9 n bytes; the position is the breach's foot in decimetres (its centre is <see cref="WarfrontBreachRules.CentreHeight"/> above it); n = 0 when the
/// battle has none any more (its end; sent three times, three ticks apart);</item>
/// <item>kind 2, a closure, sent three times (the client keeps the first of a (tick, id) pair): <c>[u8 2][u32 world tick][u8 breach id][u16 the player who closed it]</c>, 8 bytes;</item>
/// <item>kinds 16 to 31 are C5's finale (<c>WarfrontChaosDuelWire</c>, its own file); this decoder refuses them so they are forwarded.</item>
/// </list>
/// Little endian. <see cref="TryDecode"/> never throws: an unknown kind, a bad length, a state above 2 or n above 8 is refused.
/// </summary>
public static class WarfrontChaosWire
{
    public const byte Tag = 51;
    public const byte KindBreaches = 1, KindClosure = 2;
    /// <summary>The kinds reserved for C5's finale (the duel state and the Lord's telegraphs).</summary>
    public const byte DuelKindFirst = 16, DuelKindLast = 31;
    public const int MaxBreaches = WarfrontBreachRules.MaxBreaches, BreachBytes = 9, ClosureLength = 8;
    /// <summary>How many times a closure is sent, and the ticks between two sends.</summary>
    public const int ClosureRepeats = 3, ClosureRepeatTicks = 3;

    /// <summary>One breach on the wire: its id, its foot in decimetres, its state and its Daemons alive.</summary>
    public readonly record struct Breach(byte Id, short Xdm, short Ydm, short Zdm, WarfrontBreachState State, byte Alive)
    {
        /// <summary>The foot in metres.</summary>
        public Vector3 Position => new(Xdm / 10f, Ydm / 10f, Zdm / 10f);
        /// <summary>The centre in metres (the sphere's and the marker's).</summary>
        public Vector3 Centre => Position + new Vector3(0f, 0f, WarfrontBreachRules.CentreHeight);
    }

    /// <summary>A closure on the wire.</summary>
    public readonly record struct Closure(uint Tick, byte Breach, ushort Closer);

    /// <summary>A decoded section: its kind and the one of <see cref="Breaches"/> or <see cref="Closed"/> that kind carries.</summary>
    public readonly record struct Section(byte Kind, Breach[]? Breaches, Closure Closed);

    static short Dm(float metres) => (short)Math.Clamp((int)MathF.Round(float.IsFinite(metres) ? metres * 10f : 0f), short.MinValue, short.MaxValue);

    /// <summary>The wire's entry for a breach.</summary>
    public static Breach Of(WarfrontBreach b) => new(b.Id, Dm(b.Position.X), Dm(b.Position.Y), Dm(b.Position.Z), b.State, (byte)Math.Min(255, b.Alive.Count));

    /// <summary>The list of a battle's breaches (null: none, an empty list).</summary>
    public static byte[] EncodeBreaches(WarfrontBreaches? breaches) =>
        EncodeBreaches(breaches is null ? [] : breaches.All.Take(MaxBreaches).Select(Of).ToArray());

    public static byte[] EncodeBreaches(ReadOnlySpan<Breach> list)
    {
        int n = Math.Min(list.Length, MaxBreaches);
        var b = new byte[2 + BreachBytes * n];
        b[0] = KindBreaches;
        b[1] = (byte)n;
        for (int k = 0; k < n; k++)
        {
            int o = 2 + BreachBytes * k;
            var e = list[k];
            b[o] = e.Id;
            Short(b, o + 1, e.Xdm); Short(b, o + 3, e.Ydm); Short(b, o + 5, e.Zdm);
            b[o + 7] = (byte)e.State;
            b[o + 8] = e.Alive;
        }
        return b;
    }

    public static byte[] EncodeClosure(in Closure c)
    {
        var b = new byte[ClosureLength];
        b[0] = KindClosure;
        b[1] = (byte)c.Tick; b[2] = (byte)(c.Tick >> 8); b[3] = (byte)(c.Tick >> 16); b[4] = (byte)(c.Tick >> 24);
        b[5] = c.Breach;
        b[6] = (byte)c.Closer; b[7] = (byte)(c.Closer >> 8);
        return b;
    }

    /// <summary>The closure's wire form (a player id above 16 bits never occurs; it is clamped).</summary>
    public static Closure Of(in WarfrontBreachClosure c) => new(c.Tick, c.Breach, (ushort)Math.Clamp(c.Closer, 0, ushort.MaxValue));

    static void Short(byte[] b, int o, short v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static short Short(ReadOnlySpan<byte> d, int o) => (short)(d[o] | d[o + 1] << 8);

    /// <summary>Whether the section is one of C5's kinds (16 to 31), which this decoder leaves to <c>WarfrontChaosDuelWire</c>.</summary>
    public static bool IsDuelKind(ReadOnlySpan<byte> data) => data.Length >= 1 && data[0] >= DuelKindFirst && data[0] <= DuelKindLast;

    /// <summary>Decodes kind 1 or 2; false (and nothing) for anything else or anything malformed. Never throws.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out Section section)
    {
        section = default;
        if (data.Length < 1) return false;
        switch (data[0])
        {
            case KindBreaches:
            {
                if (data.Length < 2) return false;
                int n = data[1];
                if (n > MaxBreaches || data.Length != 2 + BreachBytes * n) return false;
                var list = new Breach[n];
                for (int k = 0; k < n; k++)
                {
                    int o = 2 + BreachBytes * k;
                    if (data[o + 7] > (byte)WarfrontBreachState.Closed) return false;
                    list[k] = new Breach(data[o], Short(data, o + 1), Short(data, o + 3), Short(data, o + 5), (WarfrontBreachState)data[o + 7], data[o + 8]);
                }
                section = new Section(KindBreaches, list, default);
                return true;
            }
            case KindClosure:
            {
                if (data.Length != ClosureLength) return false;
                uint tick = (uint)(data[1] | data[2] << 8 | data[3] << 16 | data[4] << 24);
                section = new Section(KindClosure, null, new Closure(tick, data[5], (ushort)(data[6] | data[7] << 8)));
                return true;
            }
            default:
                return false;
        }
    }
}
