using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SmSim;

/// <summary>
/// cancel-editor (owner, 2026-11-17): what an input does to an action in progress. Yes: the input ends the action and starts its own; No: it is ignored; Window: it counts only from
/// <see cref="CancelCell.FromMs"/> to <see cref="CancelCell.UntilMs"/> into the action (no times: the data's own timing); Queued: it waits and starts when the action ends (the input buffer's replay);
/// NotApplicable: no such case (the row cannot meet the input). Inherit is only ever a change, never a table's cell: at a class or weapon scope it removes that scope's override.
/// </summary>
public enum CancelState : byte { Yes = 0, No = 1, Window = 2, Queued = 3, NotApplicable = 4, Inherit = 5 }

/// <summary>One cell: its state and, for a window, the seconds into the action it opens and closes (milliseconds; <see cref="Natural"/> when the data's own timing rules).</summary>
public readonly record struct CancelCell(CancelState State, int FromMs = CancelCell.Natural, int UntilMs = CancelCell.Natural)
{
    /// <summary>"No time set": the action's own timing.</summary>
    public const int Natural = -1;
    /// <summary>Largest time in a cell (the wire's 16 bits).</summary>
    public const int MaxMs = 60000;

    /// <summary>Whether the window has a time of its own.</summary>
    public bool HasTimes => FromMs != Natural || UntilMs != Natural;

    /// <summary>A cell with its times cleaned: only a window keeps times, each clamped to [0, <see cref="MaxMs"/>], and a window that ends before it begins ends where it begins.</summary>
    public CancelCell Normalized()
    {
        if (State != CancelState.Window) return new(State);
        int from = FromMs == Natural ? Natural : Math.Clamp(FromMs, 0, MaxMs);
        int until = UntilMs == Natural ? Natural : Math.Clamp(UntilMs, 0, MaxMs);
        if (from != Natural && until != Natural && until < from) until = from;
        return new(State, from, until);
    }

    public override string ToString() => State == CancelState.Window && HasTimes ? $"window {(FromMs == Natural ? "start" : (FromMs / 1000f).ToString("0.###"))}-{(UntilMs == Natural ? "end" : (UntilMs / 1000f).ToString("0.###"))} s" : State.ToString().ToLowerInvariant();
}

/// <summary>Where a cell applies: a class and a weapon by name, each "" for any. Both "" is the table's base.</summary>
public readonly record struct CancelScope(string Class, string Weapon)
{
    public static readonly CancelScope Any = new("", "");
    public bool IsBase => Class.Length == 0 && Weapon.Length == 0;
    public override string ToString() => IsBase ? "all classes and weapons" : Class.Length > 0 && Weapon.Length > 0 ? $"{Class} with {Weapon}" : Class.Length > 0 ? $"class {Class}" : $"weapon {Weapon}";
}

/// <summary>One override or change: a cell of the matrix at a scope.</summary>
public readonly record struct CancelChange(CancelScope Scope, int Row, int Col, CancelCell Cell);

/// <summary>The row and column ids, in matrix order, and the names the panel and smtool show (read from the baseline file; the layout is data).</summary>
public static class CancelLayout
{
    /// <summary>Action-in-progress ids, in matrix order (37).</summary>
    public static IReadOnlyList<string> Rows => CancelData.Baseline.RowIds;
    /// <summary>Input ids, in matrix order (16).</summary>
    public static IReadOnlyList<string> Cols => CancelData.Baseline.ColIds;
    public static int RowCount => Rows.Count;
    public static int ColCount => Cols.Count;
    public static int RowIndex(string id) { for (int i = 0; i < Rows.Count; i++) if (Rows[i] == id) return i; return -1; }
    public static int ColIndex(string id) { for (int i = 0; i < Cols.Count; i++) if (Cols[i] == id) return i; return -1; }
    /// <summary>"row.column" the way the panel numbers a cell (1-based, as in the reviewed image).</summary>
    public static string Number(int row, int col) => $"{row + 1}.{col + 1}";
}

/// <summary>The human text that goes with the table (labels, notes, source locations of the shipped file); never read by the simulation.</summary>
public sealed class CancelDoc
{
    public string[] RowLabels = [], RowNotes = [], ColLabels = [];
    public string[] CellNotes = [], CellSources = [];
    public string RowLabel(int r) => r >= 0 && r < RowLabels.Length ? RowLabels[r] : "";
    public string ColLabel(int c) => c >= 0 && c < ColLabels.Length ? ColLabels[c] : "";
    public string Note(int row, int col) => CellNotes.Length > row * ColLabels.Length + col ? CellNotes[row * ColLabels.Length + col] : "";
    public string Source(int row, int col) => CellSources.Length > row * ColLabels.Length + col ? CellSources[row * ColLabels.Length + col] : "";
}

/// <summary>
/// cancel-editor (owner, 2026-11-17): the Classic PvP animation cancel table: a state for every (action in progress, input) cell, plus overrides per class and per weapon. Immutable; an edit makes a
/// new table (<see cref="With"/>). Two files are embedded: the baseline (<c>Data/cancel-baseline.json</c>: what the code does, never edited; the gate leaves a cell equal to it alone, so the default
/// table changes nothing) and the shipped default (<c>config/cancel-table.json</c>: what a release plays by; replace it to change the defaults with no code change). The matrix and its rules:
/// spec/behavior.md "Cancel table".
/// </summary>
public sealed class CancelData
{
    readonly CancelCell[] _cells;
    readonly Dictionary<(int Row, int Col, string Class, string Weapon), CancelCell> _over;

    /// <summary>Row ids in matrix order.</summary>
    public IReadOnlyList<string> RowIds { get; }
    /// <summary>Column ids in matrix order.</summary>
    public IReadOnlyList<string> ColIds { get; }
    /// <summary>The base cell of a (row, column).</summary>
    public CancelCell Base(int row, int col) => _cells[row * ColIds.Count + col];
    /// <summary>Whether any cell of this table (base or override) is not what the code does: the gate does nothing at all when this is false.</summary>
    public bool DiffersFromBaseline { get; }
    /// <summary>A stable hash of the whole table (the base cells and the overrides in order): equal tables on server and client have equal hashes.</summary>
    public uint Hash { get; }

    CancelData(IReadOnlyList<string> rows, IReadOnlyList<string> cols, CancelCell[] cells, Dictionary<(int, int, string, string), CancelCell> over, bool isBaseline)
    {
        RowIds = rows; ColIds = cols; _cells = cells; _over = over;
        DiffersFromBaseline = !isBaseline && Differs();
        Hash = ComputeHash();
    }

    // ---- the two embedded files ----
    static readonly Lazy<CancelData> BaselineLazy = new(LoadBaseline);
    static readonly Lazy<CancelData> ShippedLazy = new(LoadShipped);
    static CancelDoc _doc = new();
    static string? _shippedError;

    /// <summary>The code's own behaviour (<c>Data/cancel-baseline.json</c>); the gate never changes a cell equal to it.</summary>
    public static CancelData Baseline => BaselineLazy.Value;
    /// <summary>The release's default table (<c>config/cancel-table.json</c>, embedded); since 2026-11-23 the owner's 11-23 export (4 cells off <see cref="Baseline"/>; spec "Cancel table").</summary>
    public static CancelData Shipped => ShippedLazy.Value;   // lock-free once loaded (the hot path reads it once per player-tick)
    /// <summary>Why the embedded default could not be used (it is malformed, missing or has another layout) and the baseline stands in for it; null when it loaded. The server says so at start.</summary>
    public static string? ShippedError { get { _ = Shipped; return _shippedError; } }
    /// <summary>Labels, notes and sources of the shipped file.</summary>
    public static CancelDoc Doc { get { _ = Shipped; return _doc; } }

    static string Embedded(string name)
    {
        var asm = typeof(CancelData).Assembly;
        using var stream = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"embedded cancel table '{name}' missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static CancelData LoadBaseline()
    {
        using var doc = JsonDocument.Parse(Embedded("cancel-baseline.json"));
        var root = doc.RootElement;
        var rows = root.GetProperty("rows").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var cols = root.GetProperty("cols").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var cells = new CancelCell[rows.Length * cols.Length];
        var cellObj = root.GetProperty("cells");
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < cols.Length; c++)
                cells[r * cols.Length + c] = new(ParseState(cellObj.GetProperty(rows[r] + "|" + cols[c]).GetString()!) ?? throw new InvalidOperationException("bad baseline state"));
        return new CancelData(rows, cols, cells, new(), isBaseline: true);
    }

    static CancelData LoadShipped()
    {
        string json;
        try { json = Embedded("cancel-table.json"); }
        catch (InvalidOperationException e) { json = ""; _shippedError = e.Message; }
        var table = ResolveShipped(json, out string error, out var doc);
        if (error.Length > 0) _shippedError = error;
        _doc = doc;
        return table;
    }

    /// <summary>
    /// The shipped default from the text of its file: the table, or, when the text is missing, malformed or has another layout, the baseline (the code's own behaviour) with <paramref name="error"/> saying why, so a bad
    /// file never throws on every tick of every server.
    /// </summary>
    public static CancelData ResolveShipped(string json, out string error) => ResolveShipped(json, out error, out _);

    static CancelData ResolveShipped(string json, out string error, out CancelDoc doc)
    {
        error = ""; doc = new CancelDoc();
        CancelData? parsed = null;
        if (json.Length > 0) parsed = Parse(json, out error, out doc);
        if (parsed is not null) return parsed;
        if (json.Length == 0) error = "the embedded default table is missing";
        error = "shipped cancel table unusable, the code's own behaviour stands in: " + error;
        doc = new CancelDoc();
        return Baseline;
    }

    // ---- reading ----
    /// <summary>The cell for a player of class <paramref name="cls"/> holding weapon <paramref name="weapon"/> (names; null or "" for none): the most specific override, else the base cell.</summary>
    public CancelCell Resolve(int row, int col, string? cls, string? weapon)
    {
        if (_over.Count > 0)
        {
            cls ??= ""; weapon ??= "";
            if (cls.Length > 0 && weapon.Length > 0 && _over.TryGetValue((row, col, cls, weapon), out var both)) return both;
            if (weapon.Length > 0 && _over.TryGetValue((row, col, "", weapon), out var w)) return w;
            if (cls.Length > 0 && _over.TryGetValue((row, col, cls, ""), out var k)) return k;
        }
        return _cells[row * ColIds.Count + col];
    }

    /// <summary>The cell a scope shows: that scope's own override when it has one, else what a player of that scope would get (the weapon's, the class's, the base).</summary>
    public CancelCell At(CancelScope scope, int row, int col) => scope.IsBase ? Base(row, col) : Resolve(row, col, scope.Class, scope.Weapon);

    /// <summary>Whether the scope has an override of its own for the cell (always true for the base scope: it is the table itself).</summary>
    public bool Overrides(CancelScope scope, int row, int col) => scope.IsBase || _over.ContainsKey((row, col, scope.Class, scope.Weapon));

    /// <summary>Every override, in a fixed order (row, column, class, weapon).</summary>
    public IEnumerable<CancelChange> OverrideList() => _over.OrderBy(o => o.Key.Row).ThenBy(o => o.Key.Col).ThenBy(o => o.Key.Class, StringComparer.Ordinal).ThenBy(o => o.Key.Weapon, StringComparer.Ordinal)
        .Select(o => new CancelChange(new(o.Key.Class, o.Key.Weapon), o.Key.Row, o.Key.Col, o.Value));

    /// <summary>Whether the cell the table gives this player differs from what the code does (the gate's one question).</summary>
    public bool EditedFor(int row, int col, string? cls, string? weapon, out CancelCell cell)
    {
        cell = Resolve(row, col, cls, weapon);
        return cell != Baseline.Base(row, col);
    }

    bool Differs()
    {
        var b = Baseline;
        if (b.RowIds.Count != RowIds.Count || b.ColIds.Count != ColIds.Count) return true;
        for (int i = 0; i < _cells.Length; i++) if (_cells[i] != b._cells[i]) return true;
        foreach (var o in _over) if (o.Value != b._cells[o.Key.Row * ColIds.Count + o.Key.Col]) return true;
        return false;
    }

    uint ComputeHash()
    {
        uint h = 2166136261;
        void Mix(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 16777619; } }
        void MixText(string s) { Mix(s.Length); foreach (char ch in s) Mix(ch); }
        foreach (var c in _cells) { Mix((int)c.State); Mix(c.FromMs); Mix(c.UntilMs); }
        foreach (var o in OverrideList()) { Mix(o.Row); Mix(o.Col); MixText(o.Scope.Class); MixText(o.Scope.Weapon); Mix((int)o.Cell.State); Mix(o.Cell.FromMs); Mix(o.Cell.UntilMs); }
        return h;
    }

    // ---- editing (returns a new table) ----
    /// <summary>The table with a cell set at a scope (the base scope sets the base cell; another scope adds or replaces an override; a cell equal to what the base gives there is still kept, so the owner's intent survives a later base edit). Out-of-range rows and columns: unchanged.</summary>
    public CancelData With(CancelScope scope, int row, int col, CancelCell cell)
    {
        if (row < 0 || row >= RowIds.Count || col < 0 || col >= ColIds.Count) return this;
        cell = cell.Normalized();
        if (cell.State == CancelState.Inherit && scope.IsBase) return this;   // the base has nothing to inherit from
        var cells = (CancelCell[])_cells.Clone();
        var over = new Dictionary<(int, int, string, string), CancelCell>(_over);
        if (scope.IsBase) cells[row * ColIds.Count + col] = cell;
        else if (cell.State == CancelState.Inherit) over.Remove((row, col, scope.Class, scope.Weapon));
        else over[(row, col, scope.Class, scope.Weapon)] = cell;
        return new CancelData(RowIds, ColIds, cells, over, isBaseline: false);
    }

    /// <summary>The table with a cell back at the shipped default: the override removed, or the base cell restored.</summary>
    public CancelData Reset(CancelScope scope, int row, int col)
    {
        if (row < 0 || row >= RowIds.Count || col < 0 || col >= ColIds.Count) return this;
        var cells = (CancelCell[])_cells.Clone();
        var over = new Dictionary<(int, int, string, string), CancelCell>(_over);
        if (scope.IsBase) cells[row * ColIds.Count + col] = Shipped._cells[row * ColIds.Count + col];
        else if (Shipped._over.TryGetValue((row, col, scope.Class, scope.Weapon), out var shipped)) over[(row, col, scope.Class, scope.Weapon)] = shipped;
        else over.Remove((row, col, scope.Class, scope.Weapon));
        return new CancelData(RowIds, ColIds, cells, over, isBaseline: false);
    }

    /// <summary>The changes that turn the shipped default into this table (base cells that differ, and every override that differs from the shipped table's own override or base cell).</summary>
    public IReadOnlyList<CancelChange> DiffAgainst(CancelData other)
    {
        var list = new List<CancelChange>();
        for (int r = 0; r < RowIds.Count; r++)
            for (int c = 0; c < ColIds.Count; c++)
                if (Base(r, c) != other.Base(r, c)) list.Add(new(CancelScope.Any, r, c, Base(r, c)));
        foreach (var o in OverrideList())
        {
            bool had = other._over.TryGetValue((o.Row, o.Col, o.Scope.Class, o.Scope.Weapon), out var there);
            if (!had || there != o.Cell) list.Add(o);
        }
        foreach (var o in other.OverrideList())
            if (!_over.ContainsKey((o.Row, o.Col, o.Scope.Class, o.Scope.Weapon))) list.Add(new(o.Scope, o.Row, o.Col, new CancelCell(CancelState.Inherit)));   // an override the other has and this lacks: this table inherits there
        return list;
    }

    /// <summary>The overrides that name a class or a weapon the config does not have (a file from another build or a typo); they could never apply, and a table with one never hashes the same on server and client.</summary>
    public IReadOnlyList<CancelChange> UnknownScopes(SimConfig c)
    {
        var classes = c.Classes.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
        var weapons = c.Weapons.Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
        return OverrideList().Where(o => (o.Scope.Class.Length > 0 && !classes.Contains(o.Scope.Class)) || (o.Scope.Weapon.Length > 0 && !weapons.Contains(o.Scope.Weapon))).ToList();
    }

    /// <summary>This table without the overrides <see cref="UnknownScopes"/> finds (listed in <paramref name="dropped"/>, in plain words).</summary>
    public CancelData WithoutUnknownScopes(SimConfig c, out List<string> dropped)
    {
        dropped = [];
        var t = this;
        foreach (var o in UnknownScopes(c))
        {
            dropped.Add($"{CancelLayout.Number(o.Row, o.Col)} [{o.Scope}]");
            t = t.With(o.Scope, o.Row, o.Col, new CancelCell(CancelState.Inherit));
        }
        return t;
    }

    /// <summary>The changes against the shipped default (the panel's difference count and the wire's content).</summary>
    public IReadOnlyList<CancelChange> DiffFromShipped() => DiffAgainst(Shipped);

    /// <summary>This table made from the shipped default plus <paramref name="changes"/> (what a client builds from the server's sync).</summary>
    public static CancelData FromShipped(IEnumerable<CancelChange> changes)
    {
        var t = Shipped;
        foreach (var ch in changes) t = t.With(ch.Scope, ch.Row, ch.Col, ch.Cell);
        return t;
    }

    // ---- file ----
    internal static string? StateName(CancelState s) => s switch { CancelState.Yes => "yes", CancelState.No => "no", CancelState.Window => "window", CancelState.Queued => "queued", CancelState.NotApplicable => "na", CancelState.Inherit => "inherit", _ => null };
    public static CancelState? ParseState(string? s) => s?.ToLowerInvariant() switch { "yes" => CancelState.Yes, "no" => CancelState.No, "window" => CancelState.Window, "queued" or "buffer" => CancelState.Queued, "na" or "n/a" => CancelState.NotApplicable, _ => null };

    /// <summary>Parses a table file (the shipped file's format). Null with <paramref name="error"/> when it is not one (unknown ids, states or a different layout).</summary>
    public static CancelData? Parse(string json, out string error) => Parse(json, out error, out _);

    static CancelData? Parse(string json, out string error, out CancelDoc doc)
    {
        error = ""; doc = new CancelDoc();
        try
        {
            using var d = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = d.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not a cancel table (no top-level object)"; return null; }
            if (!root.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.Number || fmt.GetInt32() != 1) { error = "not a cancel table (format 1 expected)"; return null; }
            var rowIds = new List<string>(); var rowLabels = new List<string>(); var rowNotes = new List<string>();
            var colIds = new List<string>(); var colLabels = new List<string>();
            if (!root.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array || !root.TryGetProperty("cols", out var colsEl) || colsEl.ValueKind != JsonValueKind.Array) { error = "rows or cols missing"; return null; }
            foreach (var r in rowsEl.EnumerateArray()) { rowIds.Add(Str(r, "id")); rowLabels.Add(Str(r, "label")); rowNotes.Add(Str(r, "note")); }
            foreach (var c in colsEl.EnumerateArray()) { colIds.Add(Str(c, "id")); colLabels.Add(Str(c, "label")); }
            var b = Baseline;
            if (!rowIds.SequenceEqual(b.RowIds) || !colIds.SequenceEqual(b.ColIds)) { error = $"a different matrix layout ({rowIds.Count} rows x {colIds.Count} columns; this build has {b.RowIds.Count} x {b.ColIds.Count}, or the ids differ)"; return null; }
            var cells = new CancelCell[rowIds.Count * colIds.Count];
            var notes = new string[cells.Length]; var srcs = new string[cells.Length];
            if (!root.TryGetProperty("cells", out var cellsEl) || cellsEl.ValueKind != JsonValueKind.Object) { error = "cells missing"; return null; }
            for (int r = 0; r < rowIds.Count; r++)
                for (int c = 0; c < colIds.Count; c++)
                {
                    string key = rowIds[r] + "|" + colIds[c];
                    if (!cellsEl.TryGetProperty(key, out var cell)) { error = $"cell {key} missing"; return null; }
                    if (!ReadCell(cell, out var parsed, out error)) { error = $"cell {key}: {error}"; return null; }
                    cells[r * colIds.Count + c] = parsed; notes[r * colIds.Count + c] = Str(cell, "note"); srcs[r * colIds.Count + c] = Str(cell, "src");
                }
            var over = new Dictionary<(int, int, string, string), CancelCell>();
            if (root.TryGetProperty("overrides", out var ovEl) && ovEl.ValueKind == JsonValueKind.Array)
                foreach (var o in ovEl.EnumerateArray())
                {
                    int r = rowIds.IndexOf(Str(o, "row")), c = colIds.IndexOf(Str(o, "col"));
                    if (r < 0 || c < 0) { error = $"override names an unknown cell ({Str(o, "row")} | {Str(o, "col")})"; return null; }
                    if (!ReadCell(o, out var parsed, out error)) { error = $"override {Str(o, "row")}|{Str(o, "col")}: {error}"; return null; }
                    string cls = Str(o, "class"), wpn = Str(o, "weapon");
                    if (cls.Length == 0 && wpn.Length == 0) { error = "an override with neither class nor weapon"; return null; }
                    over[(r, c, cls, wpn)] = parsed;
                }
            doc = new CancelDoc { RowLabels = [.. rowLabels], RowNotes = [.. rowNotes], ColLabels = [.. colLabels], CellNotes = notes, CellSources = srcs };
            return new CancelData(rowIds, colIds, cells, over, isBaseline: false);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            error = "not valid JSON: " + e.Message;
            return null;
        }
    }

    static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static bool ReadCell(JsonElement e, out CancelCell cell, out string error)
    {
        cell = default; error = "";
        var state = ParseState(Str(e, "state"));
        if (state is null) { error = $"unknown state '{Str(e, "state")}'"; return false; }
        int Time(string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return CancelCell.Natural;
            if (v.ValueKind != JsonValueKind.Number) throw new FormatException(name + " is not a number");
            double s = v.GetDouble();
            return double.IsFinite(s) && s >= 0 ? (int)Math.Min(CancelCell.MaxMs, Math.Round(s * 1000)) : CancelCell.Natural;
        }
        try { cell = new CancelCell(state.Value, Time("from"), Time("until")).Normalized(); }
        catch (FormatException f) { error = f.Message; return false; }
        return true;
    }

    /// <summary>The table as a file (the shipped file's format: rows, columns, every cell with its note and source from the shipped file, then the overrides).</summary>
    public string ToJson(string name, string? generated = null)
    {
        var doc = Doc;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteString("name", name);
            if (generated is not null) w.WriteString("generated", generated);
            w.WriteString("description", "Classic PvP animation cancel table (state of every action-in-progress x input cell, plus per-class and per-weapon overrides). state: yes | no | window | queued | na; from/until: seconds into the action (null: the data's own timing).");
            w.WriteStartArray("rows");
            for (int r = 0; r < RowIds.Count; r++) { w.WriteStartObject(); w.WriteString("id", RowIds[r]); w.WriteString("label", doc.RowLabel(r)); w.WriteString("note", r < doc.RowNotes.Length ? doc.RowNotes[r] : ""); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("cols");
            for (int c = 0; c < ColIds.Count; c++) { w.WriteStartObject(); w.WriteString("id", ColIds[c]); w.WriteString("label", doc.ColLabel(c)); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartObject("cells");
            for (int r = 0; r < RowIds.Count; r++)
                for (int c = 0; c < ColIds.Count; c++)
                {
                    var cell = Base(r, c);
                    w.WriteStartObject(RowIds[r] + "|" + ColIds[c]);
                    WriteCell(w, cell);
                    w.WriteString("note", doc.Note(r, c)); w.WriteString("src", doc.Source(r, c));
                    w.WriteEndObject();
                }
            w.WriteEndObject();
            w.WriteStartArray("overrides");
            foreach (var o in OverrideList())
            {
                w.WriteStartObject();
                w.WriteString("class", o.Scope.Class); w.WriteString("weapon", o.Scope.Weapon);
                w.WriteString("row", RowIds[o.Row]); w.WriteString("col", ColIds[o.Col]);
                WriteCell(w, o.Cell);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static void WriteCell(Utf8JsonWriter w, CancelCell cell)
    {
        w.WriteString("state", StateName(cell.State));
        if (cell.FromMs == CancelCell.Natural) w.WriteNull("from"); else w.WriteNumber("from", cell.FromMs / 1000.0);
        if (cell.UntilMs == CancelCell.Natural) w.WriteNull("until"); else w.WriteNumber("until", cell.UntilMs / 1000.0);
    }

    /// <summary>A plain sentence for a change ("light melee (X1..) x Heavy / stun melee: yes -> no"), the way smtool and the panel report it.</summary>
    public string Describe(CancelChange change, CancelData? against = null)
    {
        var doc = Doc;
        against ??= Shipped;
        var was = against.At(change.Scope, change.Row, change.Col);
        string scope = change.Scope.IsBase ? "" : $" [{change.Scope}]";
        return $"{CancelLayout.Number(change.Row, change.Col)} {Short(doc.RowLabel(change.Row))} while {Short(doc.ColLabel(change.Col)).ToLowerInvariant()}{scope}: {was} -> {change.Cell}";
    }

    static string Short(string label)
    {
        int paren = label.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? label[..paren] : label;
    }
}

/// <summary>
/// cancel-editor: the table a configuration plays by, live. Server and client each hold one (the server's changes by a developer-tools request, the client's from the server's sync). Shared by every copy of a
/// <see cref="SimConfig"/> (<c>with</c> copies the reference), so an edit is seen by the next tick of every step that uses the config. Equal to every other holder, so it never makes two configs differ.
/// </summary>
public sealed class CancelLive
{
    volatile CancelData? _data;
    /// <summary>The table in force: the edited one, else the shipped default.</summary>
    public CancelData Active => _data ?? CancelData.Shipped;
    /// <summary>Whether an edited table is set (null means the shipped default).</summary>
    public bool Edited => _data is not null;
    /// <summary>Sets the table (null: the shipped default).</summary>
    public void Set(CancelData? data) => _data = data;
    /// <summary>A holder of its own with this table (a copy of a config made with <c>with</c> shares its original's holder; give it this to play by another table).</summary>
    public static CancelLive Of(CancelData? data) { var live = new CancelLive(); live.Set(data); return live; }
    public override bool Equals(object? obj) => obj is CancelLive;
    public override int GetHashCode() => 0;
}

public sealed partial record SimConfig
{
    /// <summary>cancel-editor (protocol 80, developer tools): the cancel table in force, shared by copies. Not in the config hash: the shipped table is part of the build and an edit is made only on a loopback-only <c>--dev-tools</c> server, which tells every client.</summary>
    public CancelLive Cancel { get; init; } = new();
}
