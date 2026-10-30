using SmSim;

namespace SmNet;

/// <summary>An enemy's health and armour as the bars message carries them (protocol 44): the player id and the fractions of the class body's maxima, 0..255.</summary>
public readonly record struct EnemyBarView(int PlayerId, byte Health, byte Shield);

/// <summary>
/// Server to client (protocol 44, Tactical Readout and Signum Link; owner's decision of 2026-10-30), outside the snapshots (they have no room): the health
/// and armour of the living enemies, sent only to a player whose perks show them (<see cref="Loadouts.ShowsEnemyBars"/>), about ten times a second. Fractions
/// are against the enemy's class body (<see cref="HitBars"/>), as the hit report's. No names travel. A client without the perk never receives the message,
/// so nothing shows over enemies without it. 6 + 4 bytes per enemy, at most 70.
/// </summary>
public sealed record EnemyBars(uint ServerTick, EnemyBarView[] Entries) : Message;

public static partial class Protocol
{
    /// <summary>Bytes of an enemy-bar record: player id u16, health u8, shield u8.</summary>
    public const int EnemyBarBytes = 4;

    static void WriteBars(Writer w, EnemyBars b)
    {
        int n = Math.Min(b.Entries.Length, MaxPlayers);
        w.U8((byte)Kind.EnemyBars); w.U32(b.ServerTick); w.U8((byte)n);
        for (int k = 0; k < n; k++)
        {
            var e = b.Entries[k];
            if (!PlayerId16(e.PlayerId)) throw new ArgumentException("An enemy bar names a player 1..65535.");
            w.U16((ushort)e.PlayerId); w.U8(e.Health); w.U8(e.Shield);
        }
    }

    static EnemyBars? ReadBars(ref Reader r)
    {
        uint tick = r.U32();
        int n = r.U8();
        if (n > MaxPlayers) return null;
        var entries = new EnemyBarView[n];
        for (int k = 0; k < n; k++)
        {
            int id = r.U16(); byte health = r.U8(), shield = r.U8();
            if (id == 0) return null;
            for (int j = 0; j < k; j++) if (entries[j].PlayerId == id) return null;   // each enemy once
            entries[k] = new EnemyBarView(id, health, shield);
        }
        return new EnemyBars(tick, entries);
    }

    /// <summary>A value as a byte fraction of <paramref name="max"/> (0..255; non-finite or no maximum: 0).</summary>
    public static byte BarByte(float value, float max) => max > 0f && float.IsFinite(value) ? (byte)Math.Clamp((int)MathF.Round(value / max * 255f), 0, 255) : (byte)0;
}
