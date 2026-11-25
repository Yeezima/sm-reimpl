using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// Protocol 94 (PLACEHOLDER NUMBER; claude/drop-pod-limit-1125, Phase B T2b): room for Warfront's drop pod Marines, which are World players besides the humans and their retainers (owner 2026-11-24:
/// "Drop pod marines don't count towards the limit"). A snapshot carries at most <see cref="Protocol.MaxPlayers"/> (32) records: the slim and full ones as before, then, when the player count byte's bit 7
/// (<see cref="Protocol.LeanBlockFlag"/>) is set, a count byte and that many lean records of <see cref="Protocol.LeanRecordBytes"/> bytes (the server sends a Warfront run's AI Marines so,
/// <see cref="Snapshot.Lean"/>). A lean record is a slim one with its id in 16 bits, the position in 1/64 m (the projectiles' 24-bit form), the velocity in 1/64 m/s and the yaw and pitch in 16 bits; the
/// rest of the slim record follows byte for byte. The writer keeps every snapshot within <see cref="Protocol.SnapshotBudget"/> by leaving out the last records that do not fit (the recipient's own never),
/// and every scoreboard by leaving out its last rows. Shot records name their shooter by a 5-bit index (<see cref="ShotInfoByte"/>). A snapshot of at most 16 records with no lean block, and a shot of an
/// index under 16, are protocol 91's bytes.
/// </summary>
public static partial class Protocol
{
    /// <summary>Bit 7 of a snapshot's player count byte: a block of lean records follows the slim and full ones (protocol 94).</summary>
    public const int LeanBlockFlag = 0x80;
    /// <summary>Bytes of a lean player record (protocol 94): id u16, position 3 x i24, velocity 3 x i16, yaw i16, pitch i16, then the slim record's last 12 bytes.</summary>
    public const int LeanRecordBytes = 2 + 9 + 6 + 2 + 2 + 12;
    /// <summary>A lean record's velocity steps (1/64 m/s: +-512 m/s).</summary>
    public const float LeanVelocityScale = 64f;
    /// <summary>A lean record's yaw and pitch steps (radians per unit: +-pi in 16 bits).</summary>
    public const float LeanAngleScale = 32767f / MathF.PI;
    /// <summary>The surface field of a player-hit shot whose shooter's index is 16 or more (protocol 94; 15 for an index under 16, as before).</summary>
    const int HighIndexPlayerHitSurface = 14;

    /// <summary>
    /// The records a snapshot writes, in order: the slim and full ones (<paramref name="slim"/>, the recipient's own always among them), then the lean ones (<paramref name="lean"/>). The first
    /// <c>Players.Length - Lean</c> players are slim (or full), the last <see cref="Snapshot.Lean"/> lean. All of them when they fit the budget with every other block of the snapshot; otherwise the later
    /// slim ones and then the later lean ones are left out (the server orders them by priority, nearest first).
    /// </summary>
    static PlayerState[] SnapshotRecords(Snapshot s, out int slim, out int lean)
    {
        var all = s.Players;
        int leanIn = Math.Clamp(s.Lean, 0, all.Length), slimIn = all.Length - leanIn;
        for (int k = slimIn; k < all.Length; k++)
        {
            int id = all[k].PlayerId;
            if (id is < 1 or > ushort.MaxValue || (s.Self != 0 && id == s.Self)) throw new ArgumentException("A lean record names a player 1..65535 that is not the recipient.");
        }
        if (leanIn == 0 && slimIn <= MaxMatchPlayers) { slim = slimIn; lean = 0; return all; }   // a match's players always fit (MaxSnapshotBytes): protocol 91's bytes
        int room = SnapshotBudget - SnapshotOtherBytes(s);
        int self = s.Self == 0 ? -1 : Array.FindIndex(all, 0, slimIn, p => p.PlayerId == s.Self);
        int used = self >= 0 ? FullRecordBytes : 0;
        var kept = new List<PlayerState>(Math.Min(all.Length, MaxPlayers));
        for (int k = 0; k < slimIn; k++)
        {
            if (k == self) { kept.Add(all[k]); continue; }
            if (kept.Count + (self > k ? 1 : 0) >= MaxPlayers || used + SlimRecordBytes > room) continue;
            kept.Add(all[k]);
            used += SlimRecordBytes;
        }
        slim = kept.Count;
        lean = used + 1 + LeanRecordBytes > room ? 0 : Math.Min(Math.Min(leanIn, (room - used - 1) / LeanRecordBytes), MaxPlayers - slim);
        for (int k = 0; k < lean; k++) kept.Add(all[slimIn + k]);
        return kept.ToArray();
    }

    /// <summary>The bytes of a snapshot besides its player records (and the lean block's count byte), with the shot records that always fit (<see cref="MinSnapshotShots"/>).</summary>
    static int SnapshotOtherBytes(Snapshot s) =>
        13 + 1 + Math.Min(s.Projectiles?.Length ?? 0, MaxSnapshotProjectiles) * 14 + 1
        + 9 + 1 + Math.Min(s.Kills?.Length ?? 0, MaxSnapshotKills) * KillEntryBytes + 1 + Math.Min(s.Points?.Length ?? 0, MaxSnapshotPoints) * 2
        + HitBlockBytes + 1 + Math.Min(s.Shots?.Length ?? 0, MinSnapshotShots) * ShotBytes;

    /// <summary>The bytes of a snapshot with these blocks and players (the measure of the budget; the writer leaves records out past <see cref="SnapshotBudget"/>).</summary>
    public static int SnapshotBytesOf(int projectiles, int kills, int points, int shots, int slimOthers, bool full, int lean) =>
        13 + 1 + projectiles * 14 + 1 + 9 + 1 + kills * KillEntryBytes + 1 + points * 2 + HitBlockBytes + 1 + shots * ShotBytes
        + slimOthers * SlimRecordBytes + (full ? FullRecordBytes : 0) + (lean > 0 ? 1 + lean * LeanRecordBytes : 0);

    static void WriteLeanBlock(Writer w, PlayerState[] records, int from, int count)
    {
        w.U8((byte)count);
        for (int k = from; k < from + count; k++)
        {
            var p = records[k];
            w.U16((ushort)p.PlayerId);
            w.I24(Fixed(p.Position.X)); w.I24(Fixed(p.Position.Y)); w.I24(Fixed(p.Position.Z));
            w.I16(LeanVelocity(p.Velocity.X)); w.I16(LeanVelocity(p.Velocity.Y)); w.I16(LeanVelocity(p.Velocity.Z));
            w.I16(LeanAngle(Wrapped(p.Yaw))); w.I16(LeanAngle(p.Pitch));
            // The slim record's last 12 bytes, as the slim writer (Protocol.cs) writes them (a lean record is never the recipient's full one).
            int WeaponOf(WeaponSlot slot) => slot == WeaponSlot.Pistol ? p.Pistol : slot == WeaponSlot.Second ? p.Second : p.Primary;
            w.U8((byte)((p.Grounded ? 1 : 0) | (p.Alive ? 2 : 0) | (p.Charging ? 8 : 0) | (p.Throwing ? 16 : 0)
                | (JumpPack.Jumping(p.Jump) ? 32 : 0) | (JumpPack.Pounding(p.Jump) ? 64 : 0) | (p.Aiming ? 128 : 0)));
            w.U8(ClassAndTeam(p)); w.U8(Index(WeaponOf(p.Slot))); w.U8(Index(p.MeleeChoice)); w.U8(SlotByte(p));
            var (meleeByte, meleeTime) = MeleeField(p);
            w.U8(meleeByte); w.F32(meleeTime);
            w.U8(EvadeByte(p));
            w.U8((byte)((p.DamageCount & 7) | (p.DamageShield ? 8 : 0) | (p.DamageHealth ? 16 : 0) | (p.DamageFresh ? 32 : 0) | (p.HealthRegenWaived ? 64 : 0)));
        }
    }

    /// <summary>The lean block's count after the slim records: 0 without the flag, -1 (malformed) for an empty block or more than <see cref="MaxPlayers"/> records in all.</summary>
    static int ReadLeanCount(ref Reader r, int countByte, int slim)
    {
        if ((countByte & LeanBlockFlag) == 0) return 0;
        int m = r.U8();
        return m == 0 || slim + m > MaxPlayers ? -1 : m;
    }

    static void ReadLeanHead(ref Reader r, out int id, out Vector3 position, out Vector3 velocity, out float yaw, out float pitch)
    {
        id = r.U16();
        position = new Vector3(r.I24(), r.I24(), r.I24()) / PositionScale;
        velocity = new Vector3(r.I16(), r.I16(), r.I16()) / LeanVelocityScale;
        yaw = r.I16() / LeanAngleScale; pitch = r.I16() / LeanAngleScale;
    }

    static short LeanVelocity(float v) => float.IsNaN(v) ? (short)0 : (short)Math.Clamp(MathF.Round(v * LeanVelocityScale), -short.MaxValue, short.MaxValue);
    static short LeanAngle(float a) => float.IsFinite(a) ? (short)Math.Clamp(MathF.Round(a * LeanAngleScale), -short.MaxValue, short.MaxValue) : (short)0;
    /// <summary>An angle in -pi..pi (the same heading).</summary>
    static float Wrapped(float a) => float.IsFinite(a) ? a - MathF.Tau * MathF.Round(a / MathF.Tau) : 0f;

    /// <summary>
    /// A shot record's third byte (protocol 22; 94): the stage (bits 0..2), the surface field (3..6) and bit 7. A world hit or a miss puts its surface (15 for a miss) in the field and the shooter's index
    /// bit 4 in bit 7; a player hit, which names no surface, puts the armour flag in bit 7 and the index bit 4 in the field (15 under 16, 14 from 16). Under 16 the byte is protocol 91's.
    /// </summary>
    static byte ShotInfoByte(ShotView shot, int index)
    {
        bool high = index >= 16;
        bool player = shot.Hit is ShotHit.Body or ShotHit.Head;
        int field = player ? (high ? HighIndexPlayerHitSurface : UnknownSurface) : SurfaceCode(shot);
        bool bit7 = player ? ArmouredHit(shot) : high;
        return (byte)(shot.Stage | (field << 3) | (bit7 ? 128 : 0));
    }

    /// <summary>The shooter's index, the surface (15 none) and the armour flag of a shot record's first and third bytes (<see cref="ShotInfoByte"/>); null for a player hit naming a surface.</summary>
    static (int Index, int Surface, bool Armoured)? ShotInfoFields(int head, int info)
    {
        int low = head & 15, field = (info >> 3) & 15;
        bool bit7 = (info & 128) != 0;
        if ((ShotHit)((head >> 4) & 3) is ShotHit.Body or ShotHit.Head)
            return field switch { UnknownSurface => (low, UnknownSurface, bit7), HighIndexPlayerHitSurface => (low | 16, UnknownSurface, bit7), _ => null };
        return (low | (bit7 ? 16 : 0), field, false);
    }

    /// <summary>The scoreboard rows written: at most <see cref="MaxPlayers"/>, and only the first that keep the board, with its spectators, within <see cref="SnapshotBudget"/> (one packet).</summary>
    static int ScoreRowsThatFit(Scoreboard b, int written)
    {
        int bytes = written + 1;
        if (b.Spectators is { Length: > 0 } spectators)
        {
            bytes += 1;
            foreach (var e in spectators.Take(MaxSpectators)) bytes += 4 + 1 + PlayerNames.Sanitise(e.Name).Length;
        }
        int rows = 0;
        foreach (var e in b.Players)
        {
            if (rows == MaxPlayers) break;
            bytes += ScoreRowBytes + PlayerNames.Sanitise(e.Name).Length;
            if (bytes > SnapshotBudget) break;
            rows++;
        }
        return rows;
    }
}
