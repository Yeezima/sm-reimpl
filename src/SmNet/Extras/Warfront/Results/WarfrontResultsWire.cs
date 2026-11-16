using System.Text;
using SmSim.Extras.Warfront.Results;

namespace SmNet.Extras.Warfront.Results;

/// <summary>
/// The end-of-run record as bytes (slice W15; ours): a pure encoder/decoder for W5's stage envelope to carry to the clients. It is not a new section and changes no <c>Protocol</c> value.
/// Every count and text length is bounded on decode; a malformed buffer throws <see cref="InvalidDataException"/> and never allocates more than the bounds allow. No key is written.
/// </summary>
public static class WarfrontResultsWire
{
    public const byte Format = 1;
    public const int MaxRows = 32, MaxMembers = 64, MaxBattles = 512, MaxText = 120;

    public static byte[] Encode(WarfrontRunRecord r)
    {
        if (r.Rows.Count > MaxRows || r.Battles.Count > MaxBattles) throw new ArgumentException("record too large for the wire");
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(Format);
        Str(w, r.Result); Str(w, r.EndedUtc);
        w.Write(r.DurationSeconds); w.Write(r.RollsTotal); w.Write(r.RollsLost); w.Write(r.RollsHeld);
        var k = r.Weights;
        foreach (var d in new[] { k.Kill, k.EliteKill, k.BossKill, k.Assist, k.Capture, k.Revive, k.SquadKill, k.SquadEliteKill, k.SquadBossKill, k.SquadAssist, k.SquadRevive, k.SquadCapture }) w.Write(d);
        w.Write((byte)k.TieBreaks.Count);
        foreach (var t in k.TieBreaks) w.Write((byte)t);
        w.Write((ushort)r.Battles.Count);
        foreach (var b in r.Battles) Str(w, b);
        w.Write((byte)r.Rows.Count);
        foreach (var row in r.Rows)
        {
            var p = row.Player;
            w.Write(row.Rank); w.Write(row.JoinIndex); Str(w, row.Name); w.Write(row.Connected);
            w.Write(p.Kills); w.Write(p.EliteKills); w.Write(p.BossKills); w.Write(p.Assists); w.Write(p.Deaths); w.Write(p.Revives); w.Write(p.Captures);
            var members = row.Members ?? Array.Empty<WarfrontRetainerStats>();
            if (members.Count > MaxMembers) throw new ArgumentException("squad too large for the wire");
            w.Write((byte)members.Count);
            foreach (var m in members)
            {
                Str(w, m.Name); Str(w, m.ClassName);
                w.Write(m.Slot); w.Write(m.Kills); w.Write(m.EliteKills); w.Write(m.BossKills); w.Write(m.Assists); w.Write(m.Knockouts); w.Write(m.Revives); w.Write(m.Captures); w.Write(m.BattlesSurvived);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The record, rebuilt: scores, squad totals and top retainers are recomputed from the figures and weights, never trusted from the buffer.</summary>
    public static WarfrontRunRecord Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            using var ms = new MemoryStream(data.ToArray());
            using var rd = new BinaryReader(ms, Encoding.UTF8);
            if (rd.ReadByte() != Format) throw new InvalidDataException("unknown results format");
            string result = Str(rd), ended = Str(rd);
            int dur = rd.ReadInt32(), rollsTotal = rd.ReadInt32(), lost = rd.ReadInt32(), held = rd.ReadInt32();
            var d = new decimal[12];
            for (int i = 0; i < d.Length; i++) d[i] = rd.ReadDecimal();
            int ties = rd.ReadByte();
            if (ties > 8) throw new InvalidDataException("too many tie-breaks");
            var tb = new List<WarfrontTieBreak>();
            for (int i = 0; i < ties; i++)
            {
                byte t = rd.ReadByte();
                if (t > (byte)WarfrontTieBreak.EarlierJoin) throw new InvalidDataException("bad tie-break");
                tb.Add((WarfrontTieBreak)t);
            }
            var weights = new WarfrontScoreWeights
            {
                Kill = d[0], EliteKill = d[1], BossKill = d[2], Assist = d[3], Capture = d[4], Revive = d[5], SquadKill = d[6], SquadEliteKill = d[7], SquadBossKill = d[8],
                SquadAssist = d[9], SquadRevive = d[10], SquadCapture = d[11], TieBreaks = tb,
            };
            if (!weights.IsValid) throw new InvalidDataException("negative weight");
            int nb = rd.ReadUInt16();
            if (nb > MaxBattles) throw new InvalidDataException("too many battles");
            var battles = new List<string>(nb);
            for (int i = 0; i < nb; i++) battles.Add(Str(rd));
            int nr = rd.ReadByte();
            if (nr > MaxRows) throw new InvalidDataException("too many rows");
            var contributors = new List<WarfrontContributor>(nr);
            for (int i = 0; i < nr; i++)
            {
                rd.ReadInt32();   // the sender's rank: recomputed
                int join = rd.ReadInt32(); string name = Str(rd); bool conn = rd.ReadBoolean();
                var p = new WarfrontPlayerStats(name, conn, join, rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32());
                int nm = rd.ReadByte();
                if (nm > MaxMembers) throw new InvalidDataException("squad too large");
                var squad = new List<WarfrontRetainerStats>(nm);
                for (int m = 0; m < nm; m++)
                    squad.Add(new WarfrontRetainerStats(Str(rd), Str(rd), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32(), rd.ReadInt32()));
                contributors.Add(new WarfrontContributor(p, squad));
            }
            if (ms.Position != ms.Length) throw new InvalidDataException("trailing bytes");
            return new WarfrontRunRecord(result, ended, dur, weights, battles, rollsTotal, lost, held, WarfrontLeaderboard.Rank(contributors, weights));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or FormatException or OverflowException) { throw new InvalidDataException("malformed results record", ex); }
    }

    static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        if (b.Length > MaxText) b = b[..MaxText];   // a cut inside a character decodes as a replacement character, never as an error
        w.Write((byte)b.Length);
        w.Write(b);
    }

    static string Str(BinaryReader r)
    {
        int n = r.ReadByte();
        if (n > MaxText) throw new InvalidDataException("text too long");
        var b = r.ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(b);
    }
}
