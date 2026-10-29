using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmTool;

// smtool refine-register: the human-playtest checklist (owner's instruction, 2026-10-28: "tag everything that was an
// estimation or guess so that later we can return and playtest with humans to refine the tagged feature to a sm1 faithful
// recreation"). Reads the markdown tables of spec/behavior.md and docs/changes-from-original.md (read-only) and writes
// docs/refine-register.md. Pure text processing: no game install, no network. The rules are in RefineRules and are
// printed in the register's own introduction.

/// <summary>Regex helpers: every pattern is case-insensitive and culture-invariant.</summary>
static class Rx
{
    public const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    public static Regex New(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, Opts | extra);
    public static bool Is(string input, string pattern) => Regex.IsMatch(input, pattern, Opts);
    public static string Replace(string input, string pattern, string replacement) => Regex.Replace(input, pattern, replacement, Opts);
}

/// <summary>One table row of a markdown document.</summary>
/// <param name="Line">1-based line of the row's first physical line.</param>
/// <param name="Cells">Trimmed cell texts (pipes inside code spans and escaped pipes kept as text).</param>
/// <param name="Malformed">The cell count differs from the header's (even after a strict GFM re-split).</param>
public sealed record MdRow(int Line, IReadOnlyList<string> Cells, bool Malformed);

/// <summary>A markdown table with the headings above it and the prose directly above it.</summary>
/// <param name="HeadingChain">Headings from the outermost to the nearest; each as text without the leading #s.</param>
/// <param name="Prose">Non-table lines since the nearest heading or the previous table.</param>
public sealed record MdTable(IReadOnlyList<string> HeadingChain, int HeadingLevel, int HeaderLine, IReadOnlyList<string> Header, IReadOnlyList<MdRow> Rows, string Prose)
{
    public string Heading => HeadingChain.Count > 0 ? HeadingChain[^1] : "(no heading)";

    /// <summary>The last paragraph of prose directly above the table (what the section says about this table).</summary>
    public string LeadIn
    {
        get
        {
            var paragraphs = Regex.Split(Prose.Replace("\r", ""), @"\n\s*\n", RegexOptions.CultureInvariant).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            return paragraphs.Count == 0 ? "" : paragraphs[^1];
        }
    }
}

public static class MarkdownTables
{
    static readonly Regex HeadingRx = Rx.New(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    static readonly Regex SeparatorRx = Rx.New(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$");

    public static List<MdTable> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var tables = new List<MdTable>();
        var stack = new List<(int Level, string Text)>();
        var prose = new StringBuilder();
        bool fenced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")) { fenced = !fenced; prose.AppendLine(line); continue; }
            if (fenced) { prose.AppendLine(line); continue; }
            var h = HeadingRx.Match(line);
            if (h.Success)
            {
                int level = h.Groups[1].Length;
                stack.RemoveAll(s => s.Level >= level);
                stack.Add((level, h.Groups[2].Value.Trim()));
                prose.Clear();
                continue;
            }
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && lines[i + 1].Contains('|') && lines[i + 1].Contains('-') && SeparatorRx.IsMatch(lines[i + 1]))
            {
                int headerLine = i + 1;
                var header = SplitRow(Logical(lines, ref i), lenient: true);
                i++; // the separator line
                var rows = new List<MdRow>();
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('|'))
                {
                    i++;
                    int first = i + 1;
                    var logical = Logical(lines, ref i);
                    var cells = SplitRow(logical, lenient: true);
                    bool malformed = false;
                    if (cells.Count != header.Count)
                    {
                        var strict = SplitRow(logical, lenient: false);
                        if (strict.Count == header.Count) cells = strict; else malformed = true;
                    }
                    rows.Add(new MdRow(first, cells, malformed));
                }
                tables.Add(new MdTable(stack.Select(s => s.Text).ToList(), stack.Count > 0 ? stack[^1].Level : 0, headerLine, header, rows, prose.ToString()));
                prose.Clear();
                continue;
            }
            prose.AppendLine(line);
        }
        return tables;
    }

    /// <summary>The logical row starting at <paramref name="i"/>: physical lines are joined while the row has no closing pipe and the next line is plain text (a cell that continues on the next line).</summary>
    static string Logical(string[] lines, ref int i)
    {
        var sb = new StringBuilder(lines[i].Trim());
        int physical = 1;
        while (!EndsWithPipe(sb.ToString()) && i + 1 < lines.Length && physical < 8)
        {
            var next = lines[i + 1].Trim();
            if (next.Length == 0 || next.StartsWith('|') || next.StartsWith('#')) break;
            sb.Append(' ').Append(next);
            i++; physical++;
        }
        return sb.ToString();
    }

    static bool EndsWithPipe(string s)
    {
        s = s.TrimEnd();
        return s.EndsWith('|') && !s.EndsWith("\\|");
    }

    /// <summary>
    /// Splits one table row. Lenient: a pipe inside a closed code span is part of the text (the spec writes `a|b` unescaped); strict: GFM, where only
    /// <c>\|</c> escapes a pipe. In both, <c>\|</c> yields a literal pipe, <c>&lt;br&gt;</c> a space. A backtick without a closing one on the line is plain text.
    /// </summary>
    public static List<string> SplitRow(string row, bool lenient)
    {
        row = row.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (EndsWithPipe(row)) row = row[..^1];
        var cells = new List<string>();
        var cur = new StringBuilder();
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (c == '\\' && i + 1 < row.Length && row[i + 1] == '|') { cur.Append('|'); i++; continue; }
            if (c == '`' && lenient)
            {
                int n = 0; while (i + n < row.Length && row[i + n] == '`') n++;
                int close = FindClosingRun(row, i + n, n);
                if (close >= 0)
                {
                    cur.Append(row[i..(close + n)].Replace("\\|", "|"));
                    i = close + n - 1;
                    continue;
                }
                cur.Append(row, i, n); i += n - 1; continue;
            }
            if (c == '|') { cells.Add(Clean(cur.ToString())); cur.Clear(); continue; }
            cur.Append(c);
        }
        cells.Add(Clean(cur.ToString()));
        return cells;
    }

    static int FindClosingRun(string s, int from, int n)
    {
        for (int j = from; j < s.Length; j++)
        {
            if (s[j] != '`') continue;
            int m = 0; while (j + m < s.Length && s[j + m] == '`') m++;
            if (m == n) return j;
            j += m - 1;
        }
        return -1;
    }

    static string Clean(string s) => Rx.Replace(s, @"<br\s*/?>", " ").Trim();
}

/// <summary>The roles of a table's columns, found from the header text (case-insensitive; parenthesised and backticked parts ignored).</summary>
public sealed record TableRoles(int Name, int Value, int Units, int Kind, int Confidence, int Source, int Where, IReadOnlyList<int> Other, IReadOnlyList<int> Skip)
{
    public bool HasProvenance => Kind >= 0 || Confidence >= 0 || Source >= 0 || Other.Count > 0 && StatusLike;
    public bool StatusLike { get; init; }

    public static string NormalizeHeader(string h)
    {
        h = Regex.Replace(h, @"`[^`]*`", " ", RegexOptions.CultureInvariant);
        h = Regex.Replace(h, @"\([^)]*\)", " ", RegexOptions.CultureInvariant);
        h = h.Replace("*", "");
        return Regex.Replace(h.ToLowerInvariant(), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    public static TableRoles Detect(IReadOnlyList<string> header)
    {
        int name = 0, value = -1, units = -1, kind = -1, conf = -1, source = -1, where = -1;
        bool status = false;
        var other = new List<int>();
        var skip = new List<int>();
        for (int i = 1; i < header.Count; i++)
        {
            var h = NormalizeHeader(header[i]);
            if (kind < 0 && (h == "kind" || h.StartsWith("kind "))) kind = i;
            else if (conf < 0 && h.StartsWith("confidence")) conf = i;
            else if (source < 0 && h.Contains("source")) source = i;
            else if (units < 0 && h.StartsWith("unit")) units = i;
            else if (h == "timing basis") skip.Add(i); // not scanned
            else if (where < 0 && (h.StartsWith("where") || h.StartsWith("field"))) where = i;
            else if (value < 0 && (h.StartsWith("value") || h == "description" || h == "what we do" || h == "here")) value = i;
            else
            {
                if (h is "status" or "use" or "notes" or "note" or "decision / record") status = true;
                other.Add(i);
            }
        }
        return new TableRoles(name, value, units, kind, conf, source, where, other, skip) { StatusLike = status };
    }
}

/// <summary>The classification rules. Every rule that fires is recorded as a reason, so the register says why a row is tagged.</summary>
public static class RefineRules
{
    // Kind cell words that mark an estimate, a guess or a choice of ours (the owner's list, plus the spec's own kind legend: public, inferred).
    static readonly (string Label, Regex Rx)[] KindWords =
    [
        ("ours", Rx.New(@"\bours\b|\bour (choice|rule)\b")),
        ("choice", Rx.New(@"\bchoice\b")),
        ("interpretation", Rx.New(@"\binterpret")),
        ("provisional", Rx.New(@"\bprovisional\b")),
        ("placeholder", Rx.New(@"\bplaceholder")),
        ("estimate", Rx.New(@"\bestimat")),
        ("guess", Rx.New(@"\bguess")),
        ("approximation", Rx.New(@"\bapproximat")),
        ("inferred", Rx.New(@"\binferred\b")),
        ("public (unverified)", Rx.New(@"\bpublic\b(?!\s*\(user\))")),
        ("gap (not reproduced)", Rx.New(@"\bgap\b|\bnot (implemented|simulated)\b")),
    ];

    // Only the owner's recollection of the original counts as user's knowledge; "owner's decision" is a deliberate change (Part C).
    static readonly Regex UserKnowledge = Rx.New(@"\b(user|owner)['’]?s? knowledge\b|\bpublic\s*\((user|owner)\)|^\s*user\b(?!-)|\buser-supplied\b|\bfrom the user\b");

    // Words in the source, notes or status cells (never the value). Each match is skipped when negated or historical (see TextReasons).
    static readonly (string Label, Regex Rx)[] TextWords =
    [
        ("ours", Rx.New(@"\bours\b|\bour (choice|rule|reading|own)\b|\bour guess\b")),
        ("interpretation", Rx.New(@"\binterpret")),
        ("estimate", Rx.New(@"\bestimat|\brough(ly)?\b")),
        ("guess", Rx.New(@"\bguess")),
        ("approximation", Rx.New(@"\bapproximat")),
        ("placeholder", Rx.New(@"\bplaceholder")),
        ("provisional", Rx.New(@"\bprovisional\b")),
        ("unverified", Rx.New(@"\bunverified\b|\bnot (yet )?verified\b|\bto verify\b|\bto check in the original\b|\bto be verified\b")),
        ("user's knowledge", Rx.New(@"\b(user|owner)['’]?s? (knowledge|recollection|memory)\b|\bfrom (the )?(user|owner)['’]?s? knowledge\b|\buser-supplied\b|\bfrom the user\b")),
        ("open question", Rx.New(@"\bowner question\b|\bopen question\b|\bunknown\b|\bnot established\b")),
        ("gap (not reproduced)", Rx.New(@"\binert\b|\bnot active yet\b|\bnot implemented\b")),
    ];

    // A match is ignored when a negation comes shortly before it in the same clause ("no guessing", "isn't an estimate") or it follows "were" ("were placeholder demonstrations": history).
    // "was a rough estimate", "was unknown", "not measured, provisional" and "no data, ours" stay tagged.
    static readonly Regex NegationBefore = Rx.New(@"(\b(no|not|never|without)|n't)\s+(\w+\s+){0,2}$|\bwere\s+(\w+\s+)?$");
    static readonly char[] ClauseBreaks = ['.', ',', ';', ':', '(', ')', '\n'];

    // A lead-in paragraph states a kind about its table when it says the table's contents "are/is (a) provisional ..." or "Kind provisional ...".
    static readonly Regex KindStatement = Rx.New(@"\bkind\b[^.]{0,40}\b(provisional|ours|placeholder|interpretation|estimate|choice|approximation)\b|\b(is|are)\s+(a\s+|an\s+|all\s+|only\s+)?(provisional|placeholders?|ours|interpretations?|estimates?|choices?|approximations?)\b");

    static readonly Regex NonGameplayName = Rx.New(@"^\W*(test aids?\b|probes?\b|--[a-z])|\b(demo file|replay|movie maker|capture aid)\b");
    static readonly Regex Dash = Rx.New(@"^[\s—–\-/.]*$|^n/?a$");

    // Engineering subjects: wire formats, budgets, uploads, caches, old clients. Decided by the row's subject (its name), not its source alone.
    static readonly Regex EngineeringSubject = Rx.New(@"^(protocol \d+\b|map hash\b)|\b(wire|budgets?|uploads?|blob|bytes?|appearance code|model key|caches?|hash|handshake|packets?|blob chunks?|versions|old clients?|prefetch|loading)\b");

    public static string Plain(string s) => s.Replace("**", "").Replace("’", "'").Replace("`", "");

    public static bool IsBlank(string cell) => Dash.IsMatch(Plain(cell).Trim());

    /// <summary>"low", "medium", "none" (weakest word found), "high", "blank" or "other".</summary>
    public static string ConfidenceLevel(string cell)
    {
        var c = Plain(cell).ToLowerInvariant();
        if (IsBlank(c)) return "blank";
        if (Rx.Is(c, @"\blow\b")) return "low";
        if (Rx.Is(c, @"\bnone\b")) return "none";
        if (Rx.Is(c, @"\bmedium\b")) return "medium";
        if (Rx.Is(c, @"\bhigh\b")) return "high";
        return "other";
    }

    /// <summary>"Not simulated", "Not drawn" and similar rows list what is missing; they carry no kind, confidence or source on purpose.</summary>
    public static bool IsGapRow(string name) => Rx.Is(Plain(name), @"^\s*not (simulated|drawn|implemented|applied|used|reproduced)");

    /// <summary>
    /// Wire formats, caches and similar engineering decisions: nothing to compare with SM1. The protocol and map-hash rows always; others when their subject
    /// (name) is an engineering one and the row is ours or sourced from the architecture document. A visual or gameplay value with an architecture source
    /// (an emblem's placement) stays in the playtest list.
    /// </summary>
    public static bool IsEngineering(string name, string source, string kind)
    {
        var n = Plain(name).Trim();
        if (Rx.Is(n, @"^(protocol \d+\b|map hash\b)")) return true;
        if (!EngineeringSubject.IsMatch(n)) return false;
        return Rx.Is(Plain(source).Trim(), @"^architecture") || Rx.Is(Plain(kind), @"\bours\b") || Rx.Is(Plain(source), @"\bours\b");
    }

    public static bool IsTestAid(string kind) => Rx.Is(Plain(kind), @"\btest aid\b");

    public static bool LooksNonGameplayName(string name) => NonGameplayName.IsMatch(Plain(name));

    /// <summary>The reasons a row needs human-playtest refinement (empty: not tagged). Source, notes and status cells are scanned for wording; the value is not.</summary>
    public static List<string> Reasons(TableRoles r, IReadOnlyList<string> cells)
    {
        string Cell(int i) => i >= 0 && i < cells.Count ? cells[i] : "";
        var reasons = new List<string>();
        if (IsGapRow(cells.Count > 0 ? cells[0] : "")) reasons.Add("notes: not reproduced (gap)");
        var kind = Plain(Cell(r.Kind));
        if (kind.Length > 0 && !IsTestAid(kind))
        {
            foreach (var (label, rx) in KindWords) if (rx.IsMatch(kind)) reasons.Add("kind: " + label);
            if (UserKnowledge.IsMatch(kind)) reasons.Add("kind: user's knowledge");
        }
        var conf = ConfidenceLevel(Cell(r.Confidence));
        if (conf is "low" or "medium" or "none") reasons.Add("confidence: " + (conf == "none" ? "none (low)" : conf));
        var text = new StringBuilder();
        void Add(string s) { text.Append(Plain(s)).Append(" \n "); }
        Add(Cell(r.Source));
        foreach (var i in r.Other) Add(Cell(i));
        if (r.Confidence >= 0) Add(Cell(r.Confidence));
        foreach (var reason in TextReasons(text.ToString(), "notes: "))
            if (!reasons.Contains("kind: " + reason["notes: ".Length..])) reasons.Add(reason);
        return reasons;
    }

    /// <summary>The wording rules applied to free text; each reason is prefixed ("notes: ", "section: "). Negated or historical mentions do not count.</summary>
    public static List<string> TextReasons(string text, string prefix)
    {
        var found = new List<string>();
        var t = Plain(text);
        foreach (var (label, rx) in TextWords)
        {
            foreach (Match m in rx.Matches(t))
            {
                var before = t[Math.Max(0, m.Index - 40)..m.Index];
                int cut = before.LastIndexOfAny(ClauseBreaks);
                if (cut >= 0) before = before[(cut + 1)..];
                if (NegationBefore.IsMatch(before)) continue;
                found.Add(prefix + label);
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// The reasons a table's lead-in paragraph gives to its rows: only when the paragraph states a kind about the table ("the choice of default loadout is a
    /// provisional approximation", "all of these are provisional"). Empty otherwise.
    /// </summary>
    public static List<string> LeadInReasons(string leadIn)
    {
        var t = Plain(leadIn);
        return KindStatement.IsMatch(t) ? TextReasons(t, "section: ") : new List<string>();
    }

    /// <summary>--check: true when the row states none of kind, confidence or source.</summary>
    public static bool LacksProvenance(TableRoles r, IReadOnlyList<string> cells)
    {
        string Cell(int i) => i >= 0 && i < cells.Count ? cells[i] : "";
        if (!IsBlank(Cell(r.Kind)) || !IsBlank(Cell(r.Confidence)) || !IsBlank(Cell(r.Source))) return false;
        if (IsGapRow(cells.Count > 0 ? cells[0] : "")) return false;
        if (r.StatusLike) foreach (var i in r.Other) if (!IsBlank(Cell(i))) return false;
        return true;
    }
}

public sealed record RegisterEntry(string Id, string Area, string Section, string Name, string Value, string Units, IReadOnlyList<string> Reasons, string Source, string Prompt, int Line);

public sealed record CheckFinding(string Section, int Line, string Name, string Problem);

public sealed class RegisterModel
{
    public List<RegisterEntry> Tagged { get; } = new();
    public List<RegisterEntry> Appendix { get; } = new();
    public int RowsScanned { get; set; }
    public int RowsUntagged { get; set; }
    public List<CheckFinding> Findings { get; } = new();
    /// <summary>Rows with the wrong cell count: reported only, never classified (their cells are shifted).</summary>
    public List<CheckFinding> Malformed { get; } = new();
    /// <summary>Rows no rule fired on (section, name, line, kind cell, confidence cell): what <c>--list-untagged</c> prints for review.</summary>
    public List<(string Section, string Name, int Line, string Kind, string Confidence)> Untagged { get; } = new();
    /// <summary>Rows that share their name with another row of the same section (every parsed row counted): section, name, how many.</summary>
    public List<(string Section, string Name, int Count)> Namesakes { get; } = new();
    /// <summary>Tagged, per area, in display order.</summary>
    public IEnumerable<IGrouping<string, RegisterEntry>> ByArea() => Tagged.GroupBy(e => e.Area).OrderBy(g => RefineAreas.Order(g.Key));
}

public static class RefineAreas
{
    public const string Orks = "Ork faction";
    public const string Appendix = "Appendix: not gameplay";

    // Gameplay-critical first (movement ... effects), then the rest. Matched against the nearest heading first, then its parents.
    static readonly (Regex Rx, string Area)[] Map =
    [
        (Rx.New(@"\bork"), Orks),
        (Rx.New(@"time-faithful capture|capture aid|movie maker|test aid"), "Networking and render smoothing"),
        (Rx.New(@"extracted parameters|evade|sprint, backpedal|movement"), "Movement"),
        (Rx.New(@"charge:|the charge|bull rush"), "Charge"),
        (Rx.New(@"jump pack|ground pound(?! impact)"), "Jump pack"),
        (Rx.New(@"hit reactions|knockback"), "Reactions"),
        (Rx.New(@"combat knife|melee"), "Melee"),
        (Rx.New(@"hip fire|reload animations|charged fire|thrown equipment|blind grenade|multiplayer weapons|classes and default loadouts|weapon models"), "Weapons"),
        (Rx.New(@"hit feedback|hud"), "HUD and hit feedback"),
        (Rx.New(@"camera"), "Camera"),
        (Rx.New(@"sound"), "Sound mix"),
        (Rx.New(@"effect|weapon effects|impact and landing"), "Effects"),
        (Rx.New(@"death and ragdoll"), "Death and ragdoll"),
        (Rx.New(@"teams and annihilation|seize ground"), "Game modes"),
        (Rx.New(@"perks"), "Perks"),
        (Rx.New(@"custom classes|armour paint|armour rendering|customiz"), "Customizer and paint"),
        (Rx.New(@"clip entry|cross-fade"), "Animation blending"),
        (Rx.New(@"lighting|anti-aliasing|cameras that never clip"), "Lighting and rendering"),
        (Rx.New(@"provisional values in use"), "Core simulation (provisional values)"),
        (Rx.New(@"known behavior|leads found|unknown, needs"), "General game design"),
    ];

    // Row-level overrides: the "Provisional values in use" table mixes areas; some camera rows are HUD.
    static readonly (Regex Rx, string Area)[] RowMap =
    [
        (Rx.New(@"\bork"), Orks),
        (Rx.New(@"\b(tick|snapshot|interpolation|lag|rewind|silent-player|simultaneous shots|network)\b"), "Networking and render smoothing"),
        (Rx.New(@"\bmelee\b|\blunge\b"), "Melee"),
        (Rx.New(@"\b(hud|crosshair|reticle|hit marker|hit confirmation|scoreboard|kill feed)\b"), "HUD and hit feedback"),
        (Rx.New(@"\b(gravity|movement|capsule|step height|fall speed|fall-off|out-of-world|kill region|level collision|controller|aim pitch|colliding|sprint|back|speed|jump)\b"), "Movement"),
        (Rx.New(@"\b(spawn)\b"), "Game modes"),
        (Rx.New(@"\b(damage|hit volumes|aim origin|respawn|regeneration|time to kill|shots and geometry|penetration|shield|health)\b"), "Combat core"),
    ];

    // Display order: gameplay-critical areas first.
    static readonly string[] Order_ =
    [
        "Movement", "Combat core", "Weapons", "Melee", "Jump pack", "Charge", "Reactions", "Camera", "HUD and hit feedback", "Sound mix", "Effects",
        "Game modes", "Perks", "Death and ragdoll", "Customizer and paint", "Animation blending", "Lighting and rendering",
        "Networking and render smoothing", "Core simulation (provisional values)", "General game design", Orks, Appendix,
    ];

    public static int Order(string area) { int i = Array.IndexOf(Order_, area); return i < 0 ? Order_.Length : i; }

    public static readonly IReadOnlyList<string> GameplayCritical = new[] { "Movement", "Combat core", "Weapons", "Melee", "Jump pack", "Charge", "Reactions", "Camera", "HUD and hit feedback", "Sound mix", "Effects" };

    public static string AreaOf(MdTable table, string rowName)
    {
        foreach (var heading in Enumerable.Reverse(table.HeadingChain))
            foreach (var (rx, area) in Map) if (rx.IsMatch(heading)) return RowOverride(table, area, rowName);
        return RowOverride(table, "General game design", rowName);
    }

    static string RowOverride(MdTable table, string area, string rowName)
    {
        var name = RefineRules.Plain(rowName);
        if (area == Orks) return area;
        // Rows of the mixed tables get the area of their own name.
        bool mixed = area is "Core simulation (provisional values)" or "Camera" or "General game design";
        if (area == "Camera") { if (Rx.Is(name, @"\b(hud|crosshair|reticle|hit marker|hit confirmation)\b")) return "HUD and hit feedback"; return area; }
        if (mixed || area == "Movement" && Rx.Is(table.Heading, "provisional"))
            foreach (var (rx, a) in RowMap) if (rx.IsMatch(name)) return a;
        if (Rx.Is(name, @"\bork")) return Orks;
        return area;
    }
}

public sealed record ChangeGroup(string Part, string Group, MdTable Table);

public static class RefineRegister
{
    const string Notice = "<!-- Generated by `smtool refine-register`; do not edit by hand. Regenerate after any change to spec/behavior.md or docs/changes-from-original.md. -->";

    // ---- stable ids ----------------------------------------------------------------------------------------------------------------------------------

    static string NormSection(string s) => Regex.Replace(Regex.Replace(RefineRules.Plain(s).ToLowerInvariant(), @"\([^)]*\)", " ", RegexOptions.CultureInvariant), @"[^a-z0-9]+", " ", RegexOptions.CultureInvariant).Trim();

    // Only milestone tags such as "(M22)" or "(T7)" are dropped from a name; "(relaxed)" and "(sprint)" stay and tell rows apart.
    static string NormName(string s) => Regex.Replace(Regex.Replace(RefineRules.Plain(s).ToLowerInvariant(), @"\((m|t)\d+[^)]*\)", " ", RegexOptions.CultureInvariant), @"[^a-z0-9]+", " ", RegexOptions.CultureInvariant).Trim();

    static string NormAnchor(string s)
    {
        var t = Regex.Replace(RefineRules.Plain(s).ToLowerInvariant(), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return t.Length > 200 ? t[..200] : t;
    }

    static string Hash8(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];

    /// <summary>
    /// Stable ID of a row: prefix plus 8 hex of SHA-256 over the section heading (parentheses dropped) and the parameter name (only milestone tags dropped).
    /// Other rows' edits and the order of rows do not change it.
    /// </summary>
    public static string StableId(string prefix, string section, string name) => prefix + Hash8(NormSection(section) + "|" + NormName(name));

    /// <summary>
    /// IDs for a list of rows (section, name, anchor), over every row of the document. A name that is unique in its section keeps <see cref="StableId"/>. Rows
    /// sharing section and name are told apart by their anchor (the Where / code-symbol cell, which a tuning edit does not change; never the value); rows with the
    /// same or no anchor get a numeric suffix by their order among the namesakes. Two different keys hashing to one ID throw.
    /// </summary>
    public static string[] AssignIds(string prefix, IReadOnlyList<(string Section, string Name, string Anchor)> items)
    {
        var keys = items.Select(i => NormSection(i.Section) + "|" + NormName(i.Name)).ToArray();
        var count = keys.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var ids = new string[items.Count];
        var owner = new Dictionary<string, string>(); // id -> full key
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < items.Count; i++)
        {
            var full = count[keys[i]] == 1 ? keys[i] : keys[i] + "|" + NormAnchor(items[i].Anchor);
            var id = prefix + Hash8(full);
            if (owner.TryGetValue(id, out var o) && o != full)
                throw new InvalidOperationException($"refine-register: ID {id} collides for \"{o}\" and \"{full}\"");
            if (seen.TryGetValue(full, out var n)) { seen[full] = n + 1; id += "-" + (n + 1); }
            else seen[full] = 1;
            owner[id] = full;
            ids[i] = id;
        }
        return ids;
    }

    /// <summary>The IDs that occur more than once (must be empty).</summary>
    public static List<string> DuplicateIds(IEnumerable<string> ids) => ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

    // ---- building the model -------------------------------------------------------------------------------------------------------------------------

    public static RegisterModel Analyse(string specText)
    {
        var model = new RegisterModel();
        var tables = MarkdownTables.Parse(specText);
        // IDs first, over every parsed row (tagged, untagged, malformed), so a row's ID does not depend on how the rules classify its namesakes.
        // Namesakes are told apart by the Where / code-symbol cell, which a tuning edit leaves alone; never by the value.
        var flat = new List<(string Section, string Name, string Anchor)>();
        foreach (var t in tables)
        {
            var r = TableRoles.Detect(t.Header);
            foreach (var row in t.Rows) flat.Add((t.Heading, row.Cells.Count > 0 ? row.Cells[0] : "", r.Where >= 0 && r.Where < row.Cells.Count ? row.Cells[r.Where] : ""));
        }
        var allIds = AssignIds("R-", flat);
        foreach (var g in flat.GroupBy(f => NormSection(f.Section) + "|" + NormName(f.Name)).Where(g => g.Count() > 1))
            model.Namesakes.Add((g.First().Section, g.First().Name, g.Count()));
        int flatIndex = 0;
        foreach (var table in tables)
        {
            var roles = TableRoles.Detect(table.Header);
            var leadIn = table.LeadIn;
            bool leadInDeclares = Rx.Is(leadIn, @"\b(kind|confidence|source|extracted|observed|measured|inferred|provisional|interpretation)\b");
            if (!roles.HasProvenance && !leadInDeclares && table.Rows.Count > 0)
                model.Findings.Add(new CheckFinding(table.Heading, table.HeaderLine, string.Join(" | ", table.Header), $"table has no Kind/Confidence/Source column and the paragraph above it states none ({table.Rows.Count} rows)"));
            // A table without a Kind column takes the kind its lead-in paragraph states ("the choice of default loadout is a provisional approximation").
            var leadInReasons = roles.Kind < 0 ? RefineRules.LeadInReasons(leadIn) : new List<string>();
            foreach (var row in table.Rows)
            {
                model.RowsScanned++;
                var id = allIds[flatIndex++];
                var cells = row.Cells;
                string Cell(int i) => i >= 0 && i < cells.Count ? cells[i] : "";
                var name = Cell(roles.Name);
                var section = table.Heading;
                if (row.Malformed)
                {
                    model.Malformed.Add(new CheckFinding(section, row.Line, name, $"{cells.Count} cells, header has {table.Header.Count}"));
                    continue; // shifted cells: never classified
                }
                if (roles.HasProvenance && RefineRules.LacksProvenance(roles, cells))
                    model.Findings.Add(new CheckFinding(section, row.Line, name, "row states no kind, confidence or source"));

                var area = RefineAreas.AreaOf(table, name);
                var reasons = RefineRules.Reasons(roles, cells);
                if (RefineRules.ConfidenceLevel(Cell(roles.Confidence)) != "high")
                    foreach (var sr in leadInReasons) if (!reasons.Contains(sr)) reasons.Add(sr);
                bool testAid = RefineRules.IsTestAid(Cell(roles.Kind));
                if (testAid || area == RefineAreas.Appendix || RefineRules.LooksNonGameplayName(name) || RefineRules.IsEngineering(name, Cell(roles.Source), Cell(roles.Kind)))
                {
                    model.Appendix.Add(Entry(id, table, roles, row, RefineAreas.Appendix, reasons.Count == 0 ? ["test aid"] : reasons));
                    continue;
                }
                if (reasons.Count == 0) { model.RowsUntagged++; model.Untagged.Add((section, name, row.Line, Cell(roles.Kind), Cell(roles.Confidence))); continue; }
                model.Tagged.Add(Entry(id, table, roles, row, area, reasons));
            }
        }
        return model;
    }

    static RegisterEntry Entry(string id, MdTable table, TableRoles roles, MdRow row, string area, IReadOnlyList<string> reasons)
    {
        string Cell(int i) => i >= 0 && i < row.Cells.Count ? row.Cells[i] : "";
        var name = Cell(roles.Name);
        string value;
        if (roles.Value >= 0) value = Cell(roles.Value);
        else
        {
            var parts = new List<string>();
            for (int i = 1; i < row.Cells.Count && i < table.Header.Count; i++)
                if (i != roles.Units && i != roles.Kind && i != roles.Confidence && i != roles.Source && i != roles.Where && !roles.Skip.Contains(i) && !RefineRules.IsBlank(row.Cells[i]))
                    parts.Add(RefineRules.Plain(table.Header[i]).Split('(')[0].Trim() + ": " + row.Cells[i]);
            value = string.Join("; ", parts);
        }
        var source = roles.Source >= 0 ? Cell(roles.Source) : roles.Where >= 0 ? Cell(roles.Where) : "";
        var units = roles.Units >= 0 ? Cell(roles.Units) : "";
        return new RegisterEntry(id, area, table.Heading, name, value, units, reasons, source, PlaytestPrompt(area, name, value, units), row.Line);
    }

    // ---- the playtest prompt ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The row's name as a short subject for a prompt: milestone tags dropped, at most about 60 characters.</summary>
    public static string Subject(string name)
    {
        var s = RefineRules.Plain(name);
        s = Regex.Replace(s, @"\s*\((M\d+[^)]*|T\d+[^)]*)\)", "", RegexOptions.CultureInvariant);
        s = Regex.Replace(s, @"\s+", " ", RegexOptions.CultureInvariant).Trim().TrimEnd(':');
        if (s.Length == 0) return "This value";
        if (s.Length > 60)
        {
            var cut = s[..60];
            int sp = cut.LastIndexOf(' ');
            if (sp > 30) cut = cut[..sp];
            s = cut.TrimEnd(' ', ',', ';', '(', '/') + "…";
        }
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    public static string PlaytestPrompt(string area, string name, string value, string units)
    {
        var n = RefineRules.Plain(name).ToLowerInvariant();
        var u = RefineRules.Plain(units).ToLowerInvariant();
        string how;
        if (Rx.Is(n, @"\b(sound|volume|mix|bark|voice|loudness|reverb|filter|audio)\b") || area == "Sound mix") how = "by listening A/B at the same distance and state";
        else if (Rx.Is(n, @"out-of-world|out of world")) how = "by falling off the level edge and noting how far down the player dies";
        else if (Rx.Is(n, @"\bkill region\b|\bkillregion\b")) how = "by walking into the region on the map and noting where the player dies";
        else if (Rx.Is(n, @"\bhit volumes?\b|\bhitbox|head sphere|body capsule|aim origin|\beye\b")) how = "by shooting at the edges of the head and body and noting where shots register";
        else if (Rx.Is(n, @"turning during"))  how = "by rolling while holding a turn and measuring how far the heading changes";
        else if (Rx.Is(n, @"\bmouse\b|sensitivity|\bturn speed\b|\brotation\b|\brelax|aim speed|look speed|\binput\b")) how = "by turning the view at full speed with the same input settings and timing a half turn";
        else if (Rx.Is(n, @"\b(reaction|stagger|stun|knockback|flinch|interrupt)")) how = "by watching the reaction and timing it frame by frame";
        else if (Rx.Is(n, @"penetrat|pierce")) how = "by shooting through a crate or a line of players and counting who is hit";
        else if (Rx.Is(n, @"\b(cooldown|delay|time|duration|window|hold|timer|fuse|blend|fade|latency|interval|lifetime|recharge|regeneration|cool|reload|raise|respawn|capture)\b") || Rx.Is(u, @"^(s|ms|seconds?|s,)\b")) how = "by feel and frame-counted video (a stopwatch for long timers)";
        else if (Rx.Is(n, @"\b(run|walk|sprint|strafe|move|movement|back|acceleration|velocity|fall)\b") && !Rx.Is(n, @"\bmouse\b") || u.Contains("m/s") || u.Contains("units/s")) how = "by timing a run over a known distance";
        else if (Rx.Is(n, @"\b(damage|health|shield|armour|armor|resistance|time to kill|hits to kill)\b")) how = "by counting hits to kill and by video of the health bar";
        else if (Rx.Is(n, @"\b(radius|range|reach|distance|height|width|size|cone|capsule|blast|fall-off|offset)\b") || u == "m") how = "by measuring on a flat spot of the same map";
        else if (Rx.Is(n, @"\b(fov|angle|pitch|yaw|degrees|camera|aim)\b") || u.Contains('°') || u.Contains("degrees")) how = "with an overlaid screenshot taken from the same position";
        else if (area == "Camera" || area == "HUD and hit feedback" || area == "Lighting and rendering" || area == "Customizer and paint") how = "with side-by-side screenshots in the same spot";
        else if (area == "Effects" || area == "Animation blending" || area == "Death and ragdoll") how = "with side-by-side video of the same action";
        else how = "by feel or video";
        var subject = Subject(name);
        var ours = Short(value, 70);
        return ours.Length > 0 ? $"{subject}: compare with SM1 {how}. Ours: {ours}{(ours.EndsWith("…") ? "" : ".")}" : $"{subject}: compare with SM1 {how}.";
    }

    public static string Short(string s, int max)
    {
        s = Regex.Replace(RefineRules.Plain(s), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (s.Length <= max) return s;
        var cut = s[..max];
        int sp = cut.LastIndexOf(' ');
        if (sp > max / 2) cut = cut[..sp];
        return cut.TrimEnd(' ', ',', ';', '(') + "…";
    }

    // ---- the changes document ------------------------------------------------------------------------------------------------------------------

    public static List<ChangeGroup> ChangeTables(string changesText)
    {
        var groups = new List<ChangeGroup>();
        foreach (var t in MarkdownTables.Parse(changesText))
        {
            // chain: "# Changes ..." > "## 1. Deliberate changes ..." [> "### Networking"]
            var part = t.HeadingChain.Count > 1 ? Regex.Replace(t.HeadingChain[1], @"^\d+\.\s*", "", RegexOptions.CultureInvariant) : t.Heading;
            var group = t.HeadingChain.Count > 2 ? t.HeadingChain[2] : "";
            groups.Add(new ChangeGroup(part, group, t));
        }
        return groups;
    }

    /// <summary>IDs of every row of the changes tables, in document order (one array per table).</summary>
    public static List<string[]> ChangeIds(IReadOnlyList<ChangeGroup> changes)
    {
        var flat = new List<(string Section, string Name, string Anchor)>();
        foreach (var cg in changes)
            foreach (var row in cg.Table.Rows)
                flat.Add((cg.Part + " " + cg.Group, row.Cells.Count > 0 ? row.Cells[0] : "", "")); // namesakes: by order
        var ids = AssignIds("C-", flat);
        var result = new List<string[]>();
        int k = 0;
        foreach (var cg in changes) { result.Add(ids.Skip(k).Take(cg.Table.Rows.Count).ToArray()); k += cg.Table.Rows.Count; }
        return result;
    }

    /// <summary>--check for the changes document: malformed rows and empty cells (every row must say what, the original, ours and the record).</summary>
    public static List<CheckFinding> CheckChanges(IReadOnlyList<ChangeGroup> changes)
    {
        var findings = new List<CheckFinding>();
        foreach (var cg in changes)
            foreach (var row in cg.Table.Rows)
            {
                var name = row.Cells.Count > 0 ? row.Cells[0] : "";
                var section = (cg.Part + " " + cg.Group).Trim();
                if (row.Malformed) { findings.Add(new CheckFinding(section, row.Line, name, $"malformed row: {row.Cells.Count} cells, header has {cg.Table.Header.Count}")); continue; }
                for (int i = 0; i < row.Cells.Count; i++)
                    if (RefineRules.IsBlank(row.Cells[i])) findings.Add(new CheckFinding(section, row.Line, name, $"empty cell in column \"{(i < cg.Table.Header.Count ? cg.Table.Header[i] : i.ToString())}\""));
            }
        return findings;
    }

    // ---- rendering ----------------------------------------------------------------------------------------------------------------------------------

    static string Esc(string s, int max = 0)
    {
        s = Regex.Replace(s ?? "", @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (max > 0 && s.Length > max)
        {
            var cut = s[..max];
            int sp = cut.LastIndexOf(' ');
            if (sp > max * 2 / 3) cut = cut[..sp];
            // do not cut inside a code span
            if (cut.Count(c => c == '`') % 2 == 1) cut += "`";
            s = cut.TrimEnd(' ', ',', ';', '(') + "…";
        }
        return s.Replace("|", "\\|");
    }

    public static string Render(RegisterModel model, IReadOnlyList<ChangeGroup> changes, string specName, string changesName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Refine register: what to playtest against Space Marine 1").AppendLine();
        sb.AppendLine(Notice).AppendLine();
        sb.AppendLine($"Owner's instruction (2026-10-28): tag everything that was an estimation or guess, so that later we can return and playtest with humans to refine the tagged feature to a faithful recreation of Space Marine 1. This register is that list: every gameplay value recorded in `{specName}` that is not both read from the install and trusted, plus every deliberate change and our choice from `{changesName}`.").AppendLine();

        sb.AppendLine("## How to use this in a playtest").AppendLine();
        sb.AppendLine("1. Pick an area (gameplay-critical areas come first: movement, combat, weapons, melee, jump pack, charge, reactions, camera, HUD, sound mix, effects).");
        sb.AppendLine("2. Play the same situation in SM1 and in this build: same class, loadout and, where the value is spatial, the same spot. Use the *Compare in a playtest* prompt of each row: it says what to compare and the usual way (feel, frame-counted video, stopwatch, screenshot overlay, A/B listening).");
        sb.AppendLine("3. Record the verdict in the *Result* column or in your notes, by ID: `same`, `close` (keep), or `differs` with SM1's value.");
        sb.AppendLine("4. Put the finding in `spec/behavior.md` (value, units, source `observed`, confidence high) and drop the row's tag by changing its kind; regenerate this file with `smtool refine-register`. The row then disappears from the register. A row stays in the register for as long as it is an estimate, a guess or an interpretation.");
        sb.AppendLine("5. Part C lists the deliberate changes: after the playtest, keep each one (the owner's decision stands) or revert it. It is not a defect list.").AppendLine();
        sb.AppendLine("IDs (`R-xxxxxxxx`, `C-xxxxxxxx`) are 8 hex digits of a hash of the section heading and the parameter name (only milestone tags such as `(M22)` are dropped from a name). They survive edits of other rows and reordering; they change only when that row or its section is renamed. Rows that share a section and a name are told apart by their Where / code-symbol cell (never by the value, which playtests tune), or by their order among the namesakes when the table has none.").AppendLine();

        sb.AppendLine("## How a row gets tagged").AppendLine();
        sb.AppendLine("The command reads every markdown table of the spec. A row needs human-playtest refinement when any rule fires (the *Why tagged* column names each one):").AppendLine();
        sb.AppendLine("- **kind**: the Kind cell says ours, choice, interpretation, provisional, placeholder, estimate, guess, approximation, inferred, public (a community description, not yet checked), the user's knowledge of the original (not verified against it) or a gap. Test aids are not tagged by kind. An owner's decision is a deliberate change, not a guess: it is in Part C.");
        sb.AppendLine("- **confidence**: the Confidence cell says low, medium or none.");
        sb.AppendLine("- **notes**: the source, notes or status cells (not the value) say ours, interpretation, estimate, rough, approximation, placeholder, provisional, unverified, \"to verify\", unknown, not established, an owner question, or the user's knowledge. A mention that is negated or historical (\"no guessing\", \"were placeholder demonstrations\") does not count.");
        sb.AppendLine("- **section**: a table without a Kind column takes the wording of the paragraph just above it, but only when that paragraph states a kind about the table (\"the choice of default loadout is a provisional approximation\"; \"all of these are provisional\"). Rows with high confidence are not tagged this way.");
        sb.AppendLine("- **gap**: rows that say what is not simulated, not drawn or inert are tagged as not reproduced.");
        sb.AppendLine().AppendLine("Rows read from the install with high confidence and no such wording are not tagged. Rows whose kind is `test aid`, rows about demos, replays, probes and capture, and engineering rows (protocol, map hash, wire formats, budgets, uploads, caches and old clients, when the row is ours or sourced from the architecture document) are listed in the appendix only. A visual or gameplay value is never moved there because of its source. Rows with the wrong number of cells are reported as malformed and never classified. Prose paragraphs outside tables are not read, so a human reviewer should treat the register as a floor, not a ceiling.").AppendLine();

        // summary
        sb.AppendLine("## Summary").AppendLine();
        int total = model.RowsScanned;
        sb.AppendLine($"Scanned {total} spec table rows: **{model.Tagged.Count} tagged**, {model.RowsUntagged} not tagged (read from data with high confidence), {model.Appendix.Count} in the appendix (test aids, capture tooling, engineering), {model.Malformed.Count} malformed (not classified). Part C lists {changes.Sum(g => g.Table.Rows.Count)} rows of the changes document.").AppendLine();
        sb.AppendLine("| Area | Tagged rows | by kind | by confidence | by notes | by section text |").AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var g in model.ByArea())
        {
            int k = g.Count(e => e.Reasons.Any(r => r.StartsWith("kind:")));
            int c = g.Count(e => e.Reasons.Any(r => r.StartsWith("confidence:")));
            int n = g.Count(e => e.Reasons.Any(r => r.StartsWith("notes:")));
            int sct = g.Count(e => e.Reasons.Any(r => r.StartsWith("section:")));
            sb.AppendLine($"| {g.Key}{(RefineAreas.GameplayCritical.Contains(g.Key) ? " (critical)" : "")} | {g.Count()} | {k} | {c} | {n} | {sct} |");
        }
        sb.AppendLine($"| **All areas** | **{model.Tagged.Count}** | {model.Tagged.Count(e => e.Reasons.Any(r => r.StartsWith("kind:")))} | {model.Tagged.Count(e => e.Reasons.Any(r => r.StartsWith("confidence:")))} | {model.Tagged.Count(e => e.Reasons.Any(r => r.StartsWith("notes:")))} | {model.Tagged.Count(e => e.Reasons.Any(r => r.StartsWith("section:")))} |").AppendLine();
        sb.AppendLine("A row can fire several rules, so the right-hand columns add up to more than the tagged count.").AppendLine();
        sb.AppendLine("| Reason | Rows |").AppendLine("|---|---:|");
        foreach (var grp in model.Tagged.SelectMany(e => e.Reasons).GroupBy(r => r).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {grp.Key} | {grp.Count()} |");
        sb.AppendLine();

        // Part A and B
        sb.AppendLine("## Part A: gameplay areas, critical first").AppendLine();
        foreach (var g in model.ByArea().Where(g => g.Key != RefineAreas.Orks))
            RenderArea(sb, g.Key, g.ToList(), "###");
        var ork = model.Tagged.Where(e => e.Area == RefineAreas.Orks).ToList();
        sb.AppendLine("## Part B: Ork faction").AppendLine();
        sb.AppendLine("The Orks are an addition the owner keeps (the original multiplayer had Space Marines and Chaos; Orks come with Exterminatus). Rows here cannot be compared with SM1 multiplayer; judge them by feel and by the singleplayer or Exterminatus Orks.").AppendLine();
        if (ork.Count == 0) sb.AppendLine("No tagged rows yet: the spec has no Ork section on this branch.").AppendLine();
        else RenderArea(sb, RefineAreas.Orks, ork, "###", heading: false);

        // Part C
        sb.AppendLine("## Part C: Deliberate changes (keep or revert after playtest)").AppendLine();
        sb.AppendLine($"Rows of `{changesName}`: the owner's deliberate changes, our choices where the original is unknown, and what is not reproduced yet. Nothing is classified here; every row is listed. The prompt says what to ask after the playtest.").AppendLine();
        var seenPart = "";
        var cids = ChangeIds(changes);
        for (int t = 0; t < changes.Count; t++)
        {
            var cg = changes[t];
            if (cg.Part != seenPart) { sb.AppendLine($"### {cg.Part}").AppendLine(); seenPart = cg.Part; }
            if (cg.Group.Length > 0) sb.AppendLine($"#### {cg.Group}").AppendLine();
            var header = cg.Table.Header;
            bool deliberate = Rx.Is(cg.Part, "deliberate");
            bool missing = Rx.Is(cg.Part, "not yet reproduced");
            sb.AppendLine("| ID | " + string.Join(" | ", header.Select(h => Esc(h))) + " | Compare in a playtest | Result |");
            sb.AppendLine("|---|" + string.Concat(header.Select(_ => "---|")) + "---|---|");
            for (int r = 0; r < cg.Table.Rows.Count; r++)
            {
                var row = cg.Table.Rows[r];
                var name = row.Cells.Count > 0 ? row.Cells[0] : "";
                var cells = Enumerable.Range(0, header.Count).Select(i => Esc(i < row.Cells.Count ? row.Cells[i] : "", 320));
                string prompt = deliberate ? $"{Subject(name)}: after playing SM1 and this build, keep this change or revert it; compare the original behaviour (the Original column) with ours."
                    : missing ? $"{Subject(name)}: not reproduced; note how much the missing feature matters in SM1 play."
                    : PlaytestPrompt("Changes", name, row.Cells.Count > 1 ? row.Cells[1] : "", "");
                sb.AppendLine($"| `{cids[t][r]}` | {string.Join(" | ", cells)} | {Esc(prompt)} | ☐ |");
            }
            sb.AppendLine();
        }

        // Appendix
        sb.AppendLine("## Appendix: not gameplay").AppendLine();
        sb.AppendLine("Test aids, probes, demo and capture tooling, and engineering decisions (protocol, wire formats, budgets, uploads, caches, load order). Listed so nothing is lost; no SM1 comparison needed.").AppendLine();
        if (model.Appendix.Count == 0) sb.AppendLine("None.").AppendLine();
        else
        {
            sb.AppendLine("| ID | Parameter | Section |").AppendLine("|---|---|---|");
            foreach (var e in model.Appendix) sb.AppendLine($"| `{e.Id}` | {Esc(e.Name, 120)} | {Esc(e.Section)} |");
            sb.AppendLine();
        }
        if (model.Malformed.Count > 0)
        {
            sb.AppendLine("### Malformed spec rows").AppendLine();
            sb.AppendLine("Rows whose cell count differs from their table's header. Their cells are shifted, so they are not classified. Fix them in the spec.").AppendLine();
            sb.AppendLine("| Line | Section | Parameter | Problem |").AppendLine("|---:|---|---|---|");
            foreach (var m in model.Malformed) sb.AppendLine($"| {m.Line} | {Esc(m.Section)} | {Esc(m.Name, 80)} | {Esc(m.Problem)} |");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static void RenderArea(StringBuilder sb, string area, List<RegisterEntry> entries, string level, bool heading = true)
    {
        if (heading) sb.AppendLine($"{level} {area}{(RefineAreas.GameplayCritical.Contains(area) ? " (gameplay-critical)" : "")} ({entries.Count})").AppendLine();
        foreach (var sec in entries.GroupBy(e => e.Section))
        {
            sb.AppendLine($"*Spec section: {Esc(sec.Key)}*").AppendLine();
            sb.AppendLine("| ID | Parameter | Current value (units) | Why tagged | Source | Compare in a playtest | Result |").AppendLine("|---|---|---|---|---|---|---|");
            foreach (var e in sec)
            {
                var value = Esc(e.Value, 260) + (RefineRules.IsBlank(e.Units) ? "" : " (" + Esc(e.Units, 60) + ")");
                sb.AppendLine($"| `{e.Id}` | {Esc(e.Name, 100)} | {value} | {Esc(string.Join("; ", e.Reasons))} | {Esc(e.Source, 160)} | {Esc(e.Prompt)} | ☐ |");
            }
            sb.AppendLine();
        }
    }

    // ---- command -------------------------------------------------------------------------------------------------------------------------------------

    static readonly string[] ValueFlags = ["--spec", "--changes", "--out"];
    static readonly string[] SwitchFlags = ["--check", "--list-untagged"];

    public static int Run(string[] args)
    {
        // Unknown flags and stray arguments are errors: a typo must not silently regenerate the wrong file.
        for (int i = 0; i < args.Length; i++)
        {
            if (ValueFlags.Contains(args[i])) { if (i + 1 >= args.Length) { Console.Error.WriteLine($"refine-register: {args[i]} needs a value"); return 2; } i++; }
            else if (!SwitchFlags.Contains(args[i])) { Console.Error.WriteLine($"refine-register: unknown argument \"{args[i]}\" (usage: refine-register [--spec f] [--changes f] [--out f] [--check])"); return 2; }
        }
        string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool check = args.Contains("--check");
        bool listUntagged = args.Contains("--list-untagged");
        var root = FindRoot();
        string Resolve(string? given, string fallback) => Path.IsPathRooted(given ?? fallback) ? (given ?? fallback) : Path.Combine(root, given ?? fallback);
        string Display(string? given, string fallback) => given == null ? fallback : Path.IsPathRooted(given) ? Path.GetFileName(given) : given.Replace('\\', '/');
        var specPath = Resolve(Opt("--spec"), "spec/behavior.md");
        var specName = Display(Opt("--spec"), "spec/behavior.md");
        var changesName = Display(Opt("--changes"), "docs/changes-from-original.md");
        var changesPath = Resolve(Opt("--changes"), "docs/changes-from-original.md");
        var outPath = Resolve(Opt("--out"), "docs/refine-register.md");
        if (!File.Exists(specPath)) { Console.Error.WriteLine("spec not found: " + Path.GetFileName(specPath)); return 2; }
        var specText = File.ReadAllText(specPath);
        var changesText = File.Exists(changesPath) ? File.ReadAllText(changesPath) : "";

        RegisterModel model;
        List<ChangeGroup> changes;
        string rendered;
        try
        {
            model = Analyse(specText);
            changes = ChangeTables(changesText);
            rendered = Render(model, changes, specName, changesName);
            var dups = DuplicateIds(model.Tagged.Concat(model.Appendix).Select(e => e.Id).Concat(ChangeIds(changes).SelectMany(a => a)));
            if (dups.Count > 0) throw new InvalidOperationException("refine-register: duplicate IDs: " + string.Join(", ", dups));
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }

        int code = 0;
        if (check)
        {
            int problems = 0;
            if (!File.Exists(changesPath)) { Console.Error.WriteLine($"error: {changesName} is missing"); problems++; }
            foreach (var f in model.Malformed) { Console.Error.WriteLine($"error: {specName}:{f.Line} [{f.Section}] {f.Name}: malformed row ({f.Problem}); its cells are shifted and it is not classified"); problems++; }
            foreach (var f in model.Findings) { Console.Error.WriteLine($"error: {specName}:{f.Line} [{f.Section}] {f.Name}: {f.Problem}"); problems++; }
            foreach (var f in CheckChanges(changes)) { Console.Error.WriteLine($"error: {changesName}:{f.Line} [{f.Section}] {f.Name}: {f.Problem}"); problems++; }
            var outName = Path.GetFileName(outPath);
            if (!File.Exists(outPath)) { Console.Error.WriteLine($"error: {outName} is missing; run `smtool refine-register`"); problems++; }
            else if (!string.Equals(File.ReadAllText(outPath).Replace("\r\n", "\n"), rendered.Replace("\r\n", "\n"), StringComparison.Ordinal)) { Console.Error.WriteLine($"error: {outName} is out of date; run `smtool refine-register`"); problems++; }
            Console.WriteLine(problems == 0
                ? $"refine-register check: ok ({model.RowsScanned} rows, {model.Tagged.Count} tagged)"
                : $"refine-register check: {problems} problem(s) in {model.RowsScanned} rows");
            code = problems == 0 ? 0 : 1;
        }
        if (listUntagged)
        {
            foreach (var u in model.Untagged) Console.WriteLine($"{u.Line}\t[{RefineRules.Plain(u.Section)}]\t{RefineRules.Plain(u.Name)}\t{RefineRules.Plain(u.Kind)}\t{RefineRules.Plain(u.Confidence)}");
            return code;
        }
        if (check) return code;

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, rendered.Replace("\r\n", "\n"), new UTF8Encoding(false));
        Console.WriteLine($"{model.RowsScanned} spec rows scanned: {model.Tagged.Count} tagged, {model.RowsUntagged} not tagged, {model.Appendix.Count} appendix, {model.Malformed.Count} malformed; wrote {Path.GetFileName(outPath)}");
        foreach (var g in model.ByArea()) Console.WriteLine($"  {g.Key}: {g.Count()}");
        if (model.Malformed.Count > 0) Console.WriteLine($"  {model.Malformed.Count} malformed row(s) (listed in the register)");
        Console.WriteLine($"  {model.Namesakes.Sum(n => n.Count)} rows share their name with another row of their section ({model.Namesakes.Count} names); told apart by the Where cell or their order");
        return 0;
    }

    static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "spec", "behavior.md"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }
}
