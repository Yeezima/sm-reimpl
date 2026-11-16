using SmSim;

namespace SmNet;

/// <summary>
/// One cell of the cancel table on the wire (protocol 80; cancel-editor, ours): the class (an index into <see cref="SimConfig.Classes"/>) and the weapon (an index into <see cref="SimConfig.Weapons"/>) it is for
/// (<see cref="Any"/>: every one; both <see cref="Any"/> is the table's base cell), the row and column, the state and the window's seconds in milliseconds (<see cref="CancelCell.Natural"/> as 0xFFFF). 10 bytes.
/// </summary>
public readonly record struct CancelWire(int Class, int Weapon, int Row, int Col, CancelState State, int FromMs, int UntilMs)
{
    /// <summary>"Every class" (8 bits) and "every weapon" (16 bits) on the wire.</summary>
    public const int AnyClass = 0xFF, AnyWeapon = 0xFFFF;
    /// <summary>Bytes of one entry.</summary>
    public const int Bytes = 1 + 2 + 1 + 1 + 1 + 2 + 2;
}

/// <summary>What a <see cref="DevCancelEdit"/> asks for.</summary>
public enum DevCancelOp : byte
{
    /// <summary>Applies each entry: sets the cell at its scope (<see cref="CancelState.Inherit"/> at a class or weapon scope removes that scope's override).</summary>
    Apply = 1,
    /// <summary>Puts every cell back to the shipped default (no entries).</summary>
    ResetAll = 2,
    /// <summary>Puts each entry's cell (scope, row, column) back to the shipped default; the entry's state and times are ignored.</summary>
    ResetCell = 3,
}

/// <summary>
/// Client to server (protocol 80; cancel-editor, ours): an edit of the Classic PvP cancel table, made from the F8 panel. Obeyed on the same terms as <see cref="DevCommand"/> (the server was started with
/// <c>--dev-tools</c>, listens on loopback alone, and the request comes from a loopback address); otherwise ignored. Session u32, op u8, count u8, then count entries of <see cref="CancelWire.Bytes"/> bytes (at most <see cref="Protocol.MaxCancelEntries"/>).
/// </summary>
public sealed record DevCancelEdit(uint Session, DevCancelOp Op, IReadOnlyList<CancelWire> Entries) : Message;

/// <summary>
/// Server to client (protocol 80; cancel-editor, ours): the cancel table in force. <see cref="Hash"/> is the hash of the whole table (<see cref="CancelData.Hash"/>): the server sends a message with no chunks (a beacon)
/// once a second to every loopback player and bot of a <c>--dev-tools</c> server, so a client that finds its own table's hash different asks for a sync (<see cref="DevCommandKind.CancelSync"/>). With chunks, the table as its
/// changes against the shipped default, <see cref="Chunks"/> messages of at most <see cref="Protocol.MaxCancelEntries"/> entries; a client that has every chunk of a hash builds the table (a table equal to the shipped default
/// is one chunk with no entries). Hash u32, chunk u8, chunks u8, count u8, entries.
/// </summary>
public sealed record DevCancelSync(uint Hash, int Chunk, int Chunks, IReadOnlyList<CancelWire> Entries) : Message;

public static partial class Protocol
{
    /// <summary>Most entries in one cancel message (60 x 10 bytes, inside one datagram).</summary>
    public const int MaxCancelEntries = 60;
    /// <summary>Most chunks of one sync (the matrix has 592 cells; overrides add more: a table needing more is not synced and the panel says so).</summary>
    public const int MaxCancelChunks = 64;

    static void WriteCancelEntries(Writer w, IReadOnlyList<CancelWire> entries)
    {
        int n = Math.Min(entries.Count, MaxCancelEntries);
        w.U8((byte)n);
        for (int i = 0; i < n; i++)
        {
            var e = entries[i];
            w.U8((byte)Math.Clamp(e.Class, 0, CancelWire.AnyClass)); w.U16((ushort)Math.Clamp(e.Weapon, 0, CancelWire.AnyWeapon));
            w.U8((byte)e.Row); w.U8((byte)e.Col); w.U8((byte)e.State);
            w.U16(e.FromMs == CancelCell.Natural ? (ushort)0xFFFF : (ushort)Math.Clamp(e.FromMs, 0, CancelCell.MaxMs));
            w.U16(e.UntilMs == CancelCell.Natural ? (ushort)0xFFFF : (ushort)Math.Clamp(e.UntilMs, 0, CancelCell.MaxMs));
        }
    }

    static List<CancelWire>? ReadCancelEntries(ref Reader r)
    {
        int n = r.U8();
        if (n > MaxCancelEntries) return null;
        var list = new List<CancelWire>(n);
        for (int i = 0; i < n; i++)
        {
            int cls = r.U8(), weapon = r.U16(), row = r.U8(), col = r.U8(), state = r.U8(), from = r.U16(), until = r.U16();
            if (state > (int)CancelState.Inherit || row >= CancelLayout.RowCount || col >= CancelLayout.ColCount) return null;
            if ((from != 0xFFFF && from > CancelCell.MaxMs) || (until != 0xFFFF && until > CancelCell.MaxMs)) return null;
            list.Add(new CancelWire(cls, weapon, row, col, (CancelState)state, from == 0xFFFF ? CancelCell.Natural : from, until == 0xFFFF ? CancelCell.Natural : until));
        }
        return list;
    }

    static void WriteDevCancelEdit(Writer w, DevCancelEdit e)
    {
        w.U8((byte)Kind.DevCancelEdit); w.U32(e.Session); w.U8((byte)e.Op);
        WriteCancelEntries(w, e.Entries);
    }

    static DevCancelEdit? ReadDevCancelEdit(ref Reader r)
    {
        uint session = r.U32();
        int op = r.U8();
        if (op < (int)DevCancelOp.Apply || op > (int)DevCancelOp.ResetCell) return null;
        var entries = ReadCancelEntries(ref r);
        return entries is null || (op == (int)DevCancelOp.ResetAll && entries.Count != 0) ? null : new DevCancelEdit(session, (DevCancelOp)op, entries);
    }

    static void WriteDevCancelSync(Writer w, DevCancelSync s)
    {
        w.U8((byte)Kind.DevCancelSync); w.U32(s.Hash); w.U8((byte)Math.Clamp(s.Chunk, 0, 255)); w.U8((byte)Math.Clamp(s.Chunks, 0, MaxCancelChunks));
        WriteCancelEntries(w, s.Entries);
    }

    static DevCancelSync? ReadDevCancelSync(ref Reader r)
    {
        uint hash = r.U32();
        int chunk = r.U8(), chunks = r.U8();
        if (chunks > MaxCancelChunks || (chunks > 0 && chunk >= chunks)) return null;
        var entries = ReadCancelEntries(ref r);
        return entries is null || (chunks == 0 && entries.Count != 0) ? null : new DevCancelSync(hash, chunk, chunks, entries);
    }

    /// <summary>The wire entry of a change, for a config's class and weapon lists (a class or weapon the config does not have is not representable: null).</summary>
    public static CancelWire? ToWire(CancelChange change, SimConfig c)
    {
        int cls = CancelWire.AnyClass, weapon = CancelWire.AnyWeapon;
        if (change.Scope.Class.Length > 0) { cls = IndexOfClass(c, change.Scope.Class); if (cls < 0) return null; }
        if (change.Scope.Weapon.Length > 0) { weapon = IndexOfWeapon(c, change.Scope.Weapon); if (weapon < 0) return null; }
        return new CancelWire(cls, weapon, change.Row, change.Col, change.Cell.State, change.Cell.FromMs, change.Cell.UntilMs);
    }

    /// <summary>The change of a wire entry (null for a class or weapon index the config does not have).</summary>
    public static CancelChange? FromWire(CancelWire e, SimConfig c)
    {
        string cls = "", weapon = "";
        if (e.Class != CancelWire.AnyClass) { if (e.Class >= c.Classes.Count) return null; cls = c.Classes[e.Class].Name; }
        if (e.Weapon != CancelWire.AnyWeapon) { if (e.Weapon >= c.Weapons.Count) return null; weapon = c.Weapons[e.Weapon].Name; }
        return new CancelChange(new CancelScope(cls, weapon), e.Row, e.Col, new CancelCell(e.State, e.FromMs, e.UntilMs));
    }

    static int IndexOfClass(SimConfig c, string name) { for (int i = 0; i < c.Classes.Count; i++) if (c.Classes[i].Name == name) return i; return -1; }
    static int IndexOfWeapon(SimConfig c, string name) { for (int i = 0; i < c.Weapons.Count; i++) if (c.Weapons[i].Name == name) return i; return -1; }

    /// <summary>The sync chunks for a table: its changes against the shipped default, split to datagrams (null when it needs more than <see cref="MaxCancelChunks"/>).</summary>
    public static List<DevCancelSync>? SyncChunks(CancelData table, SimConfig c)
    {
        var wire = new List<CancelWire>();
        foreach (var ch in table.DiffFromShipped()) if (ToWire(ch, c) is { } e) wire.Add(e);
        int chunks = Math.Max(1, (wire.Count + MaxCancelEntries - 1) / MaxCancelEntries);
        if (chunks > MaxCancelChunks) return null;
        var list = new List<DevCancelSync>(chunks);
        for (int i = 0; i < chunks; i++) list.Add(new DevCancelSync(table.Hash, i, chunks, wire.Skip(i * MaxCancelEntries).Take(MaxCancelEntries).ToList()));
        return list;
    }
}
