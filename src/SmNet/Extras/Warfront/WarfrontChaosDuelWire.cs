using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmNet.Extras.Warfront;

/// <summary>
/// The Chaos finale's kinds of the Chaos section (Chaos Warfront C5; ours): envelope tag <see cref="WarfrontChaosWire.Tag"/> (51), kinds 16 to 31 (C4 reserved them; <see cref="WarfrontChaosWire.IsDuelKind"/>
/// forwards them here), sent only by a Warfront stage whose army is Chaos, to every identified client. Little endian.
/// <list type="bullet">
/// <item>kind 16, the duel state, on every change and twice a second while the finale is past the bodyguard: <c>[u8 16][u8 phase][u16 champion player id (0 none)][u16 next player id (0 none)]
/// [u8 remaining in the queue after the champion][u8 attempt index (255 none)][u16 the Lord's enemy id][u16 the Lord's health in permille][u32 the Lord's health as f32 bits]</c>, 16 bytes;</item>
/// <item>kind 17, a telegraph of the Lord, sent three times (the client keeps the first of a (tick, ability) pair; the tick is the enemy pool's, as the Musou events'): <c>[u8 17][u32 pool tick][u8 ability (1 slam, 2 charge, 3 bolt, 4 leap)]
/// [i16 x dm][i16 y dm][i16 z dm][u8 seconds x10]</c>, 13 bytes.</item>
/// </list>
/// <see cref="TryDecode"/> never throws: another kind, a bad length, a phase above Lost or an ability outside 1 to 4 is refused.
/// </summary>
public static class WarfrontChaosDuelWire
{
    public const byte KindState = 16, KindTelegraph = 17;
    public const int StateLength = 16, TelegraphLength = 13;

    /// <summary>The duel state on the wire.</summary>
    public readonly record struct State(WarfrontDuelPhase Phase, ushort Champion, ushort Next, byte Remaining, byte Attempt, ushort Lord, ushort Permille, float Health);

    /// <summary>A telegraph on the wire.</summary>
    public readonly record struct Telegraph(uint Tick, byte Ability, short Xdm, short Ydm, short Zdm, byte Tenths)
    {
        public Vector3 Position => new(Xdm / 10f, Ydm / 10f, Zdm / 10f);
        public float Seconds => Tenths / 10f;
    }

    /// <summary>A decoded section: its kind and the one of <see cref="Duel"/> or <see cref="Told"/> that kind carries.</summary>
    public readonly record struct Section(byte Kind, State Duel, Telegraph Told);

    static short Dm(float metres) => (short)Math.Clamp((int)MathF.Round(float.IsFinite(metres) ? metres * 10f : 0f), short.MinValue, short.MaxValue);
    static ushort Id(int id) => (ushort)Math.Clamp(id, 0, ushort.MaxValue);

    /// <summary>The state of the Lord at <paramref name="health"/> of <paramref name="max"/>: the permille rounded up (a living Lord never shows 0).</summary>
    public static State Of(WarfrontDuelPhase phase, int champion, int next, int remaining, int attempt, int lord, float health, float max)
    {
        float h = float.IsFinite(health) ? MathF.Max(0f, health) : 0f;
        int permille = max > 0f && float.IsFinite(max) ? Math.Clamp((int)MathF.Ceiling(h / max * 1000f), 0, 1000) : 0;
        return new State(phase, Id(champion), Id(next), (byte)Math.Clamp(remaining, 0, 255), attempt < 0 ? (byte)255 : (byte)Math.Clamp(attempt, 0, 254), Id(lord), (ushort)permille, h);
    }

    public static Telegraph Of(in WarfrontLordTelegraph t) =>
        new(t.Tick, t.Ability, Dm(t.At.X), Dm(t.At.Y), Dm(t.At.Z), (byte)Math.Clamp((int)MathF.Round(float.IsFinite(t.Seconds) ? t.Seconds * 10f : 0f), 0, 255));

    public static byte[] Encode(in State s)
    {
        var b = new byte[StateLength];
        b[0] = KindState;
        b[1] = (byte)s.Phase;
        U16(b, 2, s.Champion); U16(b, 4, s.Next);
        b[6] = s.Remaining; b[7] = s.Attempt;
        U16(b, 8, s.Lord); U16(b, 10, s.Permille);
        U32(b, 12, (uint)BitConverter.SingleToInt32Bits(s.Health));
        return b;
    }

    public static byte[] Encode(in Telegraph t)
    {
        var b = new byte[TelegraphLength];
        b[0] = KindTelegraph;
        U32(b, 1, t.Tick);
        b[5] = t.Ability;
        U16(b, 6, (ushort)t.Xdm); U16(b, 8, (ushort)t.Ydm); U16(b, 10, (ushort)t.Zdm);
        b[12] = t.Tenths;
        return b;
    }

    static void U16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void U32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    static ushort U16(ReadOnlySpan<byte> d, int o) => (ushort)(d[o] | d[o + 1] << 8);
    static uint U32(ReadOnlySpan<byte> d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);

    /// <summary>Decodes kind 16 or 17; false (and nothing) for anything else or anything malformed. Never throws.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out Section section)
    {
        section = default;
        if (data.Length < 1) return false;
        switch (data[0])
        {
            case KindState:
            {
                if (data.Length != StateLength || data[1] > (byte)WarfrontDuelPhase.Lost) return false;
                float health = BitConverter.Int32BitsToSingle((int)U32(data, 12));
                if (!float.IsFinite(health) || health < 0f || U16(data, 10) > 1000) return false;
                section = new Section(KindState, new State((WarfrontDuelPhase)data[1], U16(data, 2), U16(data, 4), data[6], data[7], U16(data, 8), U16(data, 10), health), default);
                return true;
            }
            case KindTelegraph:
            {
                if (data.Length != TelegraphLength || data[5] is < WarfrontLordTelegraph.Slam or > WarfrontLordTelegraph.Leap) return false;
                section = new Section(KindTelegraph, default, new Telegraph(U32(data, 1), data[5], (short)U16(data, 6), (short)U16(data, 8), (short)U16(data, 10), data[12]));
                return true;
            }
            default:
                return false;
        }
    }
}
