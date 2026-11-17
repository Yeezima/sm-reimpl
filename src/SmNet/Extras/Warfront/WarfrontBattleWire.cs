using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Hq;

namespace SmNet.Extras.Warfront;

/// <summary>
/// The Battle section (slice W13b; ours): envelope tag <see cref="EnvelopeTag"/> (47), to every client of a Warfront battle, on change and every second. The HQ defence's HUD reads it (the fortress's, W14b's, reuses it):
/// <c>[u8 kind: node 0, hq 1, fortress 2][u8 stage 0..3: the positions fallen][u8 wave: the next to leave][u8 waves scheduled][u16 seconds to the next wave, 0: none]</c>
/// <c>[u16 seconds left to survive, 0: no timer][u8 message id][u8 positions held bits][u8 hold: the front position's hold in percent 0..100, in steps of 5]</c>, 11 bytes, little endian. Decoding is <c>TryDecode</c> only and never throws.
/// </summary>
public static class WarfrontBattleWire
{
    public const byte EnvelopeTag = 47;
    public const int Length = 11;

    public readonly record struct Section(byte Kind, byte Stage, byte Wave, byte Waves, ushort SecondsToWave, ushort SecondsLeft, byte Message, byte HeldBits, byte Hold)
    {
        public bool IsHq => Kind == WarfrontHqBattleRules.KindHq;
        public bool Holds(int position) => (HeldBits & (1 << position)) != 0;
    }

    /// <summary>The section of a battle under way: the HQ defence's clocks and stage; any other battle is a node (kind 0, nothing else).</summary>
    public static Section FromBattle(TfwBattle battle, WarfrontTargetKind target)
    {
        if (!battle.WarfrontHq || target != WarfrontTargetKind.Hq) return new Section(WarfrontHqBattleRules.KindNode, 0, 0, 0, 0, 0, 0, 7, 100);
        var (wave, seconds) = battle.HqNextWave;
        return new Section(WarfrontHqBattleRules.KindHq, (byte)Math.Min(3, battle.HqFallenCount), (byte)wave, (byte)WarfrontHqBattleRules.ScheduledWaves,
            (ushort)Math.Ceiling(seconds), (ushort)Math.Ceiling(battle.HqSecondsLeft), battle.HqMessage, battle.HqHeldBits, battle.HqHoldPercent);
    }

    public static byte[] Encode(in Section s)
    {
        var b = new byte[Length];
        b[0] = s.Kind; b[1] = s.Stage; b[2] = s.Wave; b[3] = s.Waves;
        b[4] = (byte)s.SecondsToWave; b[5] = (byte)(s.SecondsToWave >> 8);
        b[6] = (byte)s.SecondsLeft; b[7] = (byte)(s.SecondsLeft >> 8);
        b[8] = s.Message; b[9] = s.HeldBits; b[10] = s.Hold;
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out Section section)
    {
        section = default;
        if (data.Length != Length || data[0] > WarfrontHqBattleRules.KindFortress || data[1] > 3 || data[9] > 7 || data[10] > 100 || data[8] > WarfrontHqBattleRules.MessageWave) return false;
        section = new Section(data[0], data[1], data[2], data[3], (ushort)(data[4] | data[5] << 8), (ushort)(data[6] | data[7] << 8), data[8], data[9], data[10]);
        return true;
    }

    /// <summary>The text of a banner message id (the client's HUD and the log use the same words).</summary>
    public static string MessageText(byte id) => id switch
    {
        WarfrontHqBattleRules.MessageWallFell => "THE WALL HAS FALLEN. FALL BACK TO THE KEEP",
        WarfrontHqBattleRules.MessageKeepFell => "THE KEEP HAS FALLEN. FALL BACK TO THE CAVES",
        WarfrontHqBattleRules.MessageHolds => "THE HQ HOLDS",
        WarfrontHqBattleRules.MessageFallen => "THE HQ HAS FALLEN",
        WarfrontHqBattleRules.MessageWave => "A WAVE IS COMING UP THE RAMP",
        _ => "",
    };

    /// <summary>The line under Musou's objective HUD, with the position's name at all times (A, B, C on Musou's own HUD stand for W, K, V): "HOLD THE WALL", "FALL BACK TO THE KEEP", "LAST STAND: THE CAVES", "THE WALL IS FALLING" while its hold is under half, and the wave clock.</summary>
    public static string Line(in Section s)
    {
        if (!s.IsHq) return "";
        string name = s.Stage switch { 0 => "WALL", 1 => "KEEP", _ => "CAVES" };
        string hold = s.Hold < WarfrontHqBattleRules.HoldFallingBelow * 100f && s.Stage < 3 ? $"THE {name} IS FALLING" : s.Stage switch { 0 => "HOLD THE WALL", 1 => "FALL BACK TO THE KEEP", _ => "LAST STAND: THE CAVES" };
        string wave = s.SecondsToWave > 0 ? $"WAVE {s.Wave} OF {s.Waves} IN {Clock(s.SecondsToWave)}" : $"WAVE {s.Wave} OF {s.Waves}";
        return $"{hold}  {wave}";
    }

    /// <summary>The survive countdown ("SURVIVE 6:12"), or "".</summary>
    public static string Countdown(in Section s) => s.IsHq && s.SecondsLeft > 0 ? "SURVIVE " + Clock(s.SecondsLeft) : "";

    public static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
}
