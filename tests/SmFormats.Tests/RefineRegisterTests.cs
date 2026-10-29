using SmTool;

namespace SmFormats.Tests;

/// <summary>The refine register generator (smtool refine-register): synthetic markdown only, no repository files.</summary>
public class RefineRegisterTests
{
    static RegisterModel Analyse(string md) => RefineRegister.Analyse(md);

    const string Standard = """
        # Spec

        ## Combat

        ### Rifle (M6)

        Some prose.

        | Parameter | Value | Units | Timing basis | Where | Source | Kind | Confidence |
        |---|---|---|---|---|---|---|---|
        | Shot cooldown | 0.133 | s | per shot | `Cfg.Cooldown` | wargear `shot_cooldown` | extracted | high |
        | Eye height | 1.6 | m | per tick | `Cfg.Eye` | placeholder | provisional | none |
        | Spread | 0 | degrees | per shot | `Cfg.Spread` | owner's direction | ours | medium |
        | Reload | 1.3 | s | per reload | `Cfg.Reload` | wargear | extracted | medium. Needs a check |
        """;

    static string OneTable(string rows, string header = "| Parameter | Value | Source | Kind | Confidence |\n|---|---|---|---|---|") =>
        "## S\n\nKind `extracted`.\n\n" + header + "\n" + rows + "\n";

    // ---- table parsing ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Parses_headers_rows_and_the_heading_chain()
    {
        var t = Assert.Single(MarkdownTables.Parse(Standard));
        Assert.Equal(["Spec", "Combat", "Rifle (M6)"], t.HeadingChain);
        Assert.Equal(8, t.Header.Count);
        Assert.Equal(4, t.Rows.Count);
        Assert.Equal("Eye height", t.Rows[1].Cells[0]);
        Assert.Equal("provisional", t.Rows[1].Cells[6]);
        Assert.All(t.Rows, r => Assert.False(r.Malformed));
        Assert.Equal("Some prose.", t.LeadIn);
    }

    [Fact]
    public void Pipes_inside_backticks_and_escaped_pipes_stay_in_their_cell()
    {
        const string md = """
            ## S

            | Name | Value | Kind |
            |---|---|---|
            | Test aid | `--look left|right` and `a\|b` | test aid |
            | Plain | x \| y | ours |
            """;
        var t = Assert.Single(MarkdownTables.Parse(md));
        Assert.Equal(3, t.Rows[0].Cells.Count);
        Assert.Equal("`--look left|right` and `a|b`", t.Rows[0].Cells[1]);
        Assert.Equal("x | y", t.Rows[1].Cells[1]);
        Assert.All(t.Rows, r => Assert.False(r.Malformed));
    }

    [Fact]
    public void Several_tables_with_different_headers_and_case()
    {
        const string md = """
            ## A

            | Camera set | FOV | Used by |
            |---|---|---|
            | `exploration_default_mp` | 40 | rifles |

            ### B

            | PARAMETER (M12) | VALUE | KIND | CONFIDENCE |
            |---|---|---|---|
            | Thing | 3 | EXTRACTED | HIGH |

            | Behavior | Description | Kind | Confidence | Source / validation needed |
            | --- | --- | --- | --- | --- |
            | Fury | meter | public | medium | same |
            """;
        var tables = MarkdownTables.Parse(md);
        Assert.Equal(3, tables.Count);
        Assert.Equal("B", tables[1].Heading);
        var r1 = TableRoles.Detect(tables[1].Header);
        Assert.Equal(1, r1.Value); Assert.Equal(2, r1.Kind); Assert.Equal(3, r1.Confidence);
        var r2 = TableRoles.Detect(tables[2].Header);
        Assert.Equal(1, r2.Value); Assert.Equal(4, r2.Source);
        var r0 = TableRoles.Detect(tables[0].Header);
        Assert.Equal(-1, r0.Kind); Assert.Equal(-1, r0.Value);
    }

    [Fact]
    public void A_cell_that_continues_on_the_next_line_is_joined()
    {
        const string md = "## S\n\n| Name | Value | Kind |\n|---|---|---|\n| Long | first part\ncontinued | ours |\n| Next | 1 | ours |\n";
        var t = Assert.Single(MarkdownTables.Parse(md));
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("first part continued", t.Rows[0].Cells[1]);
        Assert.Equal("ours", t.Rows[0].Cells[2]);
    }

    [Fact]
    public void Very_long_lines_are_just_rows()
    {
        var big = new string('x', 20_000);
        var md = $"## S\n\n| Name | Value | Kind | Confidence |\n|---|---|---|---|\n| Big | {big} | extracted | high |\n";
        var t = Assert.Single(MarkdownTables.Parse(md));
        Assert.Equal(20_000, t.Rows[0].Cells[1].Length);
        Assert.Empty(Analyse(md).Tagged);
    }

    [Fact]
    public void Crlf_line_endings_and_a_last_row_without_a_trailing_newline()
    {
        const string md = "## S\r\n\r\n| Name | Value | Kind |\r\n|---|---|---|\r\n| A | 1 | ours |\r\n| B | 2 | ours |";
        var t = Assert.Single(MarkdownTables.Parse(md));
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("ours", t.Rows[1].Cells[2]);
        Assert.Equal(2, Analyse(md).Tagged.Count);
    }

    [Fact]
    public void An_unclosed_backtick_is_plain_text_and_does_not_swallow_the_row()
    {
        const string md = "## S\n\n| Name | Value | Kind |\n|---|---|---|\n| A | the `x is open | ours |\n| B | 2 | ours |\n";
        var t = Assert.Single(MarkdownTables.Parse(md));
        Assert.Equal(["A", "the `x is open", "ours"], t.Rows[0].Cells);
        Assert.All(t.Rows, r => Assert.False(r.Malformed));
    }

    [Fact]
    public void Text_outside_tables_and_code_fences_is_ignored()
    {
        const string md = "## S\n\n```\n| Not | a table |\n|---|---|\n| x | y |\n```\n\n- | bullet | line |\n";
        Assert.Empty(MarkdownTables.Parse(md));
    }

    [Fact]
    public void A_table_directly_after_another_has_no_lead_in_of_its_own()
    {
        const string md = "## S\n\nAbout the first table: provisional.\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n| C | D |\n|---|---|\n| 3 | 4 |\n";
        var t = MarkdownTables.Parse(md);
        Assert.Equal("About the first table: provisional.", t[0].LeadIn);
        Assert.Equal("", t[1].LeadIn);
    }

    // ---- malformed rows ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_row_with_the_wrong_cell_count_is_reported_and_never_classified()
    {
        const string md = "## S\n\n| Name | Value | Kind | Confidence |\n|---|---|---|---|\n| A | 1 | ours | low | extra |\n| B | 2 | ours | low |\n| C | 3 |\n";
        var model = Analyse(md);
        Assert.Equal(["A", "C"], model.Malformed.Select(m => m.Name));
        Assert.Equal("B", Assert.Single(model.Tagged).Name);
        Assert.Equal(0, model.RowsUntagged);
        Assert.Empty(model.Findings); // shifted cells are not read for provenance either
    }

    // ---- classification -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Extracted_high_confidence_rows_are_not_tagged_and_the_others_are_with_their_reasons()
    {
        var m = Analyse(Standard);
        Assert.Equal(1, m.RowsUntagged);
        Assert.Equal(3, m.Tagged.Count);
        var eye = m.Tagged.Single(e => e.Name == "Eye height");
        Assert.Contains("kind: provisional", eye.Reasons);
        Assert.Contains("confidence: none (low)", eye.Reasons);
        Assert.Contains("notes: placeholder", eye.Reasons);
        var spread = m.Tagged.Single(e => e.Name == "Spread");
        Assert.Contains("kind: ours", spread.Reasons);
        Assert.Contains("confidence: medium", spread.Reasons);
        Assert.Equal(["confidence: medium"], m.Tagged.Single(e => e.Name == "Reload").Reasons);
    }

    [Theory]
    [InlineData("extracted", "high", "wargear", "", false)]
    [InlineData("extracted", "high", "wargear", "the value is an interpretation of the units", true)]
    [InlineData("extracted", "high", "ours", "", true)]
    [InlineData("extracted", "high", "rough estimate by the owner", "", true)]
    [InlineData("extracted", "high", "user's knowledge, to verify", "", true)]
    [InlineData("extracted", "high", "unverified", "", true)]
    [InlineData("extracted", "high", "an approximation", "", true)]
    [InlineData("extracted", "medium", "wargear", "", true)]
    [InlineData("extracted", "low", "wargear", "", true)]
    [InlineData("extracted", "high", "wargear", "hours of play", false)]
    [InlineData("choice", "high", "x", "", true)]
    [InlineData("interpretation", "high", "x", "", true)]
    [InlineData("estimate", "high", "x", "", true)]
    [InlineData("guess", "high", "x", "", true)]
    [InlineData("public (user)", "high", "x", "", true)]
    [InlineData("user's knowledge", "high", "x", "", true)]
    [InlineData("user", "high", "x", "", true)]
    [InlineData("user / interpretation", "high", "x", "", true)]
    [InlineData("extracted times; meaning user-supplied", "medium", "x", "", true)]
    [InlineData("extracted", "high", "from the user", "", true)]
    [InlineData("extracted", "high", "user-supplied", "", true)]
    [InlineData("owner's decision", "high", "x", "", false)]
    [InlineData("user-facing", "high", "x", "", false)]
    [InlineData("measured", "high", "x", "", false)]
    [InlineData("test aid", "n/a", "—", "", false)]
    public void Rules(string kind, string confidence, string source, string notes, bool tagged)
    {
        var md = $"## S\n\n| Parameter | Value | Source | Kind | Confidence | Notes |\n|---|---|---|---|---|---|\n| P | 1 | {source} | {kind} | {confidence} | {notes} |\n";
        var m = Analyse(md);
        Assert.Equal(tagged, m.Tagged.Count == 1);
    }

    [Fact]
    public void Public_user_counts_once_as_the_users_knowledge()
    {
        var e = Assert.Single(Analyse(OneTable("| P | 1 | x | public (user) | high |")).Tagged);
        Assert.Equal(["kind: user's knowledge"], e.Reasons);
    }

    [Theory]
    [InlineData("No guessing", false)]
    [InlineData("there is no guess in it", false)]
    [InlineData("not a placeholder", false)]
    [InlineData("the values were placeholder demonstrations", false)]
    [InlineData("it wasn't an estimate", false)]
    [InlineData("never ours", false)]
    [InlineData("a placeholder", true)]
    [InlineData("guess by the owner", true)]
    [InlineData("Fine. A guess", true)]
    [InlineData("plain data", false)]
    [InlineData("was a rough estimate", true)]
    [InlineData("the value was unknown", true)]
    [InlineData("was not verified", true)]
    [InlineData("not measured, provisional", true)]
    [InlineData("no data, ours", true)]
    public void Negated_and_historical_mentions_do_not_tag(string notes, bool tagged)
    {
        var md = $"## S\n\n| Parameter | Value | Kind | Confidence | Notes |\n|---|---|---|---|---|\n| P | 1 | extracted | high | {notes} |\n";
        Assert.Equal(tagged, Analyse(md).Tagged.Count == 1);
    }

    [Fact]
    public void The_value_cell_is_not_scanned_for_wording()
    {
        var m = Analyse(OneTable("| P | a placeholder, ours, an interpretation | wargear | extracted | high |"));
        Assert.Empty(m.Tagged);
        Assert.Equal(1, m.RowsUntagged);
    }

    [Fact]
    public void A_table_without_kind_or_confidence_columns_is_judged_by_its_notes()
    {
        const string md = """
            ## Weapons

            Kind `extracted` for the numbers.

            | Weapon | Damage | Cooldown | Notes |
            |---|---|---|---|
            | Bolter | 22 | 0.133 | |
            | Plasma | 40 | 0.1 | provisional approximation |
            """;
        var m = Analyse(md);
        Assert.Equal("Plasma", Assert.Single(m.Tagged).Name);
        Assert.Equal(1, m.RowsUntagged);
        Assert.Empty(m.Findings);
    }

    [Fact]
    public void Test_aid_rows_go_to_the_appendix_not_the_gameplay_areas()
    {
        const string md = "## S\n\n| Parameter | Value | Kind | Confidence |\n|---|---|---|---|\n| Test aids | `--x` | test aid | n/a |\n| Probe (`--y-probe`) | measures | ours | medium |\n| Real | 1 | ours | medium |\n";
        var m = Analyse(md);
        Assert.Equal(2, m.Appendix.Count);
        Assert.Equal("Real", Assert.Single(m.Tagged).Name);
    }

    [Fact]
    public void Engineering_rows_go_to_the_appendix_but_visual_and_gameplay_values_keep_their_place()
    {
        var m = Analyse(OneTable(string.Join("\n",
            "| Model key | cache | architecture section 5 | ours | medium |",
            "| Emblem bytes | 16384 | architecture section 4 | ours | medium |",
            "| Protocol 18 | bits | ours | ours | medium |",
            "| Map hash (blockers) | sha | ours | ours | medium |",
            "| Custom emblem | the pad's second UV set, 128 x 128 | architecture section 2 | ours | medium |",
            "| Ground chunks (ours) | 6 to 12 physics chunks | ours | ours | medium |",
            "| Tick | 30 | docs/architecture.md starting value | provisional | none |")));
        Assert.Equal(["Model key", "Emblem bytes", "Protocol 18", "Map hash (blockers)"], m.Appendix.Select(e => e.Name));
        Assert.Equal(["Custom emblem", "Ground chunks (ours)", "Tick"], m.Tagged.Select(e => e.Name));
        Assert.True(RefineRules.IsEngineering("Old clients", "architecture section 3", "ours"));
        Assert.False(RefineRules.IsEngineering("Pound impact", "ours", "ours"));
    }

    [Fact]
    public void A_lead_in_tags_its_rows_only_when_it_states_a_kind_about_the_table()
    {
        const string states = "## S\n\nThe default loadout is a provisional approximation.\n\n| Class | Primary |\n|---|---|\n| Tactical | bolter |\n";
        var e = Assert.Single(Analyse(states).Tagged);
        Assert.Contains("section: provisional", e.Reasons);
        Assert.Contains("section: approximation", e.Reasons);
        // mentions the word but states no kind about the table
        const string mentions = "## S\n\nField of view: interpretation: vertical. Units unknown.\n\n| Camera set | FOV |\n|---|---|\n| aim | 40 |\n";
        var m = Analyse(mentions);
        Assert.Empty(m.Tagged);
        Assert.Equal(1, m.RowsUntagged);
        // a paragraph further up does not count
        const string farther = "## S\n\nThis is provisional.\n\nKind `extracted`.\n\n| Class | Primary |\n|---|---|\n| Tactical | bolter |\n";
        Assert.Empty(Analyse(farther).Tagged);
        // high-confidence rows are not tagged by it
        const string high = "## S\n\nAll of these are provisional.\n\n| Parameter | Value | Confidence |\n|---|---|---|\n| A | 1 | high |\n| B | 2 | none |\n";
        Assert.Equal("B", Assert.Single(Analyse(high).Tagged).Name);
    }

    [Fact]
    public void Not_simulated_rows_are_gaps_tagged_and_exempt_from_the_check()
    {
        const string md = "## S\n\n| Parameter | Value | Kind | Confidence |\n|---|---|---|---|\n| Not simulated | the lunge | — | — |\n";
        var m = Analyse(md);
        Assert.True(RefineRules.IsGapRow("Not simulated"));
        Assert.Equal("Not simulated", Assert.Single(m.Tagged).Name);
        Assert.Empty(m.Findings);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData("medium. The cooldown is an entry", "medium")]
    [InlineData("low for meaning", "low")]
    [InlineData("none (original rate unknown)", "none")]
    [InlineData("medium (rule) / low (mapping)", "low")]
    [InlineData("—", "blank")]
    [InlineData("", "blank")]
    [InlineData("n/a", "blank")]
    [InlineData("implemented", "other")]
    public void Confidence_level(string cell, string expected) => Assert.Equal(expected, RefineRules.ConfidenceLevel(cell));

    // ---- areas ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("The charge: the bull rush (M29)", "Row", "Charge")]
    [InlineData("Jump pack and ground pound (M14)", "Row", "Jump pack")]
    [InlineData("Sound playback (M26b)", "Row", "Sound mix")]
    [InlineData("Orks and their weapons", "Row", RefineAreas.Orks)]
    [InlineData("Camera, aim mode, shot convergence", "Crosshair spread", "HUD and hit feedback")]
    [InlineData("Camera, aim mode, shot convergence", "FOV", "Camera")]
    [InlineData("Provisional values in use", "Gravity", "Movement")]
    [InlineData("Provisional values in use", "Snapshot rate", "Networking and render smoothing")]
    [InlineData("Something new", "Row", "General game design")]
    public void Areas(string heading, string row, string expected)
    {
        var t = new MdTable(["Doc", heading], 2, 1, ["Parameter", "Value"], [], "");
        Assert.Equal(expected, RefineAreas.AreaOf(t, row));
    }

    [Fact]
    public void Ork_rows_get_their_own_area_by_name()
    {
        var t = new MdTable(["Doc", "Misc"], 2, 1, ["Parameter", "Value"], [], "");
        Assert.Equal(RefineAreas.Orks, RefineAreas.AreaOf(t, "Ork shoota rate of fire"));
    }

    // ---- stable, unique ids ---------------------------------------------------------------------------------------------------------------

    static string[] Ids(string md) => Analyse(md).Tagged.Select(e => e.Id).ToArray();

    [Fact]
    public void Ids_are_stable_across_edits_of_other_rows_and_of_the_value()
    {
        var a = Analyse(Standard).Tagged.Single(e => e.Name == "Eye height").Id;
        var edited = Standard.Replace("| Spread | 0 |", "| Spread | 2 |").Replace("0.133", "0.2").Replace("| Eye height | 1.6 |", "| Eye height | 1.7 |");
        var extra = Standard + "\n| New | 1 | m | x | y | z | ours | low |\n";
        Assert.Equal(a, Analyse(edited).Tagged.Single(e => e.Name == "Eye height").Id);
        Assert.Equal(a, Analyse(extra).Tagged.Single(e => e.Name == "Eye height").Id);
        Assert.Matches(@"^R-[0-9A-F]{8}$", a);
    }

    [Fact]
    public void Ids_follow_the_section_and_the_name_and_ignore_milestone_tags_and_case()
    {
        Assert.Equal(RefineRegister.StableId("R-", "Rifle (M6)", "Eye  height (M22)"), RefineRegister.StableId("R-", "Rifle (M7, renamed)", "eye height"));
        Assert.NotEqual(RefineRegister.StableId("R-", "Rifle", "Eye height"), RefineRegister.StableId("R-", "Pistol", "Eye height"));
        Assert.NotEqual(RefineRegister.StableId("R-", "Rifle", "Eye height"), RefineRegister.StableId("R-", "Rifle", "Eye heights"));
        // other parenthesised words are part of the name
        Assert.NotEqual(RefineRegister.StableId("R-", "S", "Run clips (relaxed)"), RefineRegister.StableId("R-", "S", "Run clips (sprint)"));
    }

    [Fact]
    public void Near_duplicates_differing_in_a_parenthesis_get_distinct_ids_and_survive_a_deletion_and_a_reorder()
    {
        var rows = new[]
        {
            "| Clips (relaxed) | a | x | ours | low |",
            "| Clips (sprint) | b | x | ours | low |",
            "| Clips (aim) | c | x | ours | low |",
        };
        var all = Analyse(OneTable(string.Join("\n", rows))).Tagged.ToDictionary(e => e.Name, e => e.Id);
        Assert.Equal(3, all.Values.Distinct().Count());
        var reordered = Analyse(OneTable(string.Join("\n", rows.Reverse()))).Tagged.ToDictionary(e => e.Name, e => e.Id);
        Assert.Equal(all, reordered);
        var withoutFirst = Analyse(OneTable(string.Join("\n", rows.Skip(1)))).Tagged.ToDictionary(e => e.Name, e => e.Id);
        Assert.Equal(all["Clips (sprint)"], withoutFirst["Clips (sprint)"]);
        Assert.Equal(all["Clips (aim)"], withoutFirst["Clips (aim)"]);
    }

    const string WhereHeader = "| Parameter | Value | Where | Source | Kind | Confidence |\n|---|---|---|---|---|---|";

    [Fact]
    public void Namesakes_are_told_apart_by_the_where_cell_not_by_the_value_or_the_position()
    {
        var rows = new[]
        {
            "| Dup | 1 | `Cfg.A` | x | ours | low |",
            "| Dup | 2 | `Cfg.B` | x | ours | low |",
            "| Dup | 3 | `Cfg.C` | x | ours | low |",
        };
        var a = Analyse(OneTable(string.Join("\n", rows), WhereHeader)).Tagged;
        Assert.Equal(3, a.Select(e => e.Id).Distinct().Count());
        var ids = a.Select(e => e.Id).ToArray();
        // reordered: same ID for the same row
        var reordered = Analyse(OneTable(string.Join("\n", rows.Reverse()), WhereHeader)).Tagged;
        Assert.Equal(ids.OrderBy(x => x), reordered.Select(e => e.Id).OrderBy(x => x));
        Assert.Equal(a.Single(e => e.Value == "2").Id, reordered.Single(e => e.Value == "2").Id);
        // a tuning edit of one value does not change its ID
        var tuned = Analyse(OneTable(string.Join("\n", rows).Replace("| Dup | 2 |", "| Dup | 2.5 |"), WhereHeader)).Tagged;
        Assert.Equal(a.Single(e => e.Value == "2").Id, tuned.Single(e => e.Value == "2.5").Id);
        Assert.Equal(ids.OrderBy(x => x), tuned.Select(e => e.Id).OrderBy(x => x));
    }

    [Fact]
    public void Namesakes_without_a_where_cell_are_told_apart_by_their_order_and_a_value_edit_keeps_every_id()
    {
        var rows = new[] { "| Dup | one | x | ours | low |", "| Dup | two | x | ours | low |", "| Dup | three | x | ours | low |" };
        var a = Analyse(OneTable(string.Join("\n", rows))).Tagged;
        Assert.Equal(3, a.Select(e => e.Id).Distinct().Count());
        var tuned = Analyse(OneTable(string.Join("\n", rows).Replace("| Dup | two |", "| Dup | 2.0 |"))).Tagged;
        Assert.Equal(a.Select(e => e.Id), tuned.Select(e => e.Id));
        Assert.Equal("2.0", tuned[1].Value);
    }

    [Fact]
    public void Namesakes_are_counted_over_every_row_whatever_the_rules_make_of_them()
    {
        // the second Dup is plain extracted data (untagged) in one document and tagged in the other; the first keeps its ID either way
        var untagged = Analyse(OneTable("| Dup | 1 | `Cfg.A` | x | ours | low |\n| Dup | 2 | `Cfg.B` | wargear | extracted | high |", WhereHeader));
        var tagged = Analyse(OneTable("| Dup | 1 | `Cfg.A` | x | ours | low |\n| Dup | 2 | `Cfg.B` | ours | ours | low |", WhereHeader));
        Assert.Single(untagged.Tagged);
        Assert.Equal(2, tagged.Tagged.Count);
        Assert.Equal(untagged.Tagged[0].Id, tagged.Tagged[0].Id);
        // a malformed namesake counts as well
        var withMalformed = Analyse(OneTable("| Dup | 1 | `Cfg.A` | x | ours | low |\n| Dup | 2 |", WhereHeader));
        Assert.Equal(untagged.Tagged[0].Id, withMalformed.Tagged[0].Id);
        // and the namesakes are reported
        var n = Assert.Single(untagged.Namesakes);
        Assert.Equal(("S", "Dup", 2), n);
    }

    [Fact]
    public void Identical_rows_still_get_distinct_ids()
    {
        var ids = Ids(OneTable("| Same | 1 | x | ours | low |\n| Same | 1 | x | ours | low |\n| Same | 1 | x | ours | low |"));
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Empty(RefineRegister.DuplicateIds(ids));
        Assert.Equal(["a", "b"], RefineRegister.DuplicateIds(["a", "b", "a", "c", "b"]).OrderBy(x => x));
    }

    [Fact]
    public void Ids_are_unique_across_the_spec_the_appendix_and_the_changes()
    {
        var model = Analyse(OneTable("| A | 1 | x | ours | low |\n| A | 2 | x | ours | low |\n| Protocol 3 | 1 | ours | ours | low |\n| B | 1 | x | ours | low |"));
        const string changes = "# C\n\n## 1. Deliberate changes\n\n| Change | Original | Here | Decision |\n|---|---|---|---|\n| X | a | b | c |\n| X | d | e | f |\n";
        var cids = RefineRegister.ChangeIds(RefineRegister.ChangeTables(changes)).SelectMany(x => x);
        var all = model.Tagged.Concat(model.Appendix).Select(e => e.Id).Concat(cids).ToList();
        Assert.Equal(6, all.Count);
        Assert.Empty(RefineRegister.DuplicateIds(all));
        Assert.All(cids, id => Assert.StartsWith("C-", id));
    }

    // ---- prompts ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Combat", "Shot cooldown", "0.133", "s", "stopwatch")]
    [InlineData("Movement", "Sprint speed", "10", "m/s", "known distance")]
    [InlineData("Weapons", "Pound damage", "25", "", "hits to kill")]
    [InlineData("Weapons", "Blast radius", "7", "m", "measuring")]
    [InlineData("Sound mix", "Master bus", "-7", "dB", "listening")]
    [InlineData("Camera", "FOV", "40", "degrees", "screenshot")]
    [InlineData("Movement", "Kill region", "boxes", "", "walking into the region")]
    [InlineData("Combat core", "Hit volumes (placeholders)", "head sphere", "m", "edges of the head and body")]
    [InlineData("Camera", "Aim turn speed horizontal / vertical", "3", "", "half turn")]
    [InlineData("Camera", "Mouse speed", "100", "", "half turn")]
    [InlineData("Reactions", "Stun threshold", "1e6", "", "reaction")]
    [InlineData("Movement", "Turning during the roll", "40", "°/s", "heading changes")]
    public void The_playtest_prompt_says_what_to_compare_and_how(string area, string name, string value, string units, string expected)
    {
        var p = RefineRegister.PlaytestPrompt(area, name, value, units);
        Assert.Contains("compare with SM1", p);
        Assert.Contains(expected, p);
        Assert.StartsWith(char.ToUpperInvariant(name[0]) + name[1..], p);
    }

    [Fact]
    public void The_prompt_uses_a_short_name()
    {
        var longName = "Probe results (bots on mp_tdm_09, port 27853, headless 60 to 150 ms hitch frames on a loaded PC; raw = frames with a pose jump)";
        var s = RefineRegister.Subject(longName);
        Assert.True(s.Length <= 62, s);
        Assert.EndsWith("…", s);
        Assert.Equal("Eye height", RefineRegister.Subject("Eye height (M22)"));
    }

    // ---- --check and the command --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_reports_rows_that_state_no_kind_confidence_or_source()
    {
        const string md = "## S\n\n| Parameter | Value | Source | Kind | Confidence |\n|---|---|---|---|---|\n| Good | 1 | wargear | extracted | high |\n| Bad | 2 | — | | n/a |\n";
        var m = Analyse(md);
        var f = Assert.Single(m.Findings);
        Assert.Equal("Bad", f.Name);
    }

    [Fact]
    public void Check_reports_a_table_without_any_provenance_column_or_statement()
    {
        const string md = "## S\n\nJust numbers.\n\n| Parameter | Value |\n|---|---|\n| A | 1 |\n| B | 2 |\n";
        var f = Assert.Single(Analyse(md).Findings);
        Assert.Contains("no Kind/Confidence/Source", f.Problem);
    }

    [Fact]
    public void Check_accepts_a_table_whose_paragraph_directly_above_states_its_kind()
    {
        const string md = "## S\n\nKind `extracted`, confidence high.\n\n| Parameter | Value |\n|---|---|\n| A | 1 |\n";
        Assert.Empty(Analyse(md).Findings);
    }

    [Fact]
    public void Check_does_not_accept_a_kind_statement_further_up_the_section()
    {
        const string md = "## S\n\nKind `extracted`, confidence high.\n\nThese are the numbers.\n\n| Parameter | Value |\n|---|---|\n| A | 1 |\n";
        Assert.Single(Analyse(md).Findings);
    }

    [Fact]
    public void Check_reports_empty_cells_and_malformed_rows_of_the_changes_document()
    {
        const string ok = "# C\n\n## 1. Deliberate changes\n\n| Change | Original | Here | Decision |\n|---|---|---|---|\n| X | a | b | c |\n";
        Assert.Empty(RefineRegister.CheckChanges(RefineRegister.ChangeTables(ok)));
        const string bad = "# C\n\n## 1. Deliberate changes\n\n| Change | Original | Here | Decision |\n|---|---|---|---|\n| X | a | — | c |\n| Y | a | b |\n";
        var f = RefineRegister.CheckChanges(RefineRegister.ChangeTables(bad));
        Assert.Equal(2, f.Count);
        Assert.Contains("empty cell", f[0].Problem);
        Assert.Contains("malformed", f[1].Problem);
    }

    [Fact]
    public void The_repository_register_is_current_and_every_spec_row_has_provenance()
    {
        // The gate without CI: dotnet test fails when spec/behavior.md or docs/changes-from-original.md changed without `smtool refine-register`,
        // or when a spec row states no kind, confidence or source.
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        int code;
        try { code = RefineRegister.Run(["--check"]); }
        finally { Console.SetError(original); }
        Assert.True(code == 0, "refine-register --check failed (run `smtool refine-register`, fix the rows listed): " + captured);
    }

    sealed class Workdir : IDisposable
    {
        public string Dir = Path.Combine(Path.GetTempPath(), "refine-register-" + Guid.NewGuid().ToString("N"));
        public string Spec => Path.Combine(Dir, "behavior.md");
        public string Changes => Path.Combine(Dir, "changes.md");
        public string Out => Path.Combine(Dir, "register.md");
        public Workdir() { Directory.CreateDirectory(Dir); }
        public int Run(params string[] extra) => RefineRegister.Run(["--spec", Spec, "--changes", Changes, "--out", Out, .. extra]);
        public void Dispose() => Directory.Delete(Dir, true);
    }

    const string GoodChanges = "# Changes\n\n## 1. Deliberate changes\n\n| Change | Original | Here | Decision |\n|---|---|---|---|\n| X | a | b | owner |\n";
    const string GoodSpec = "## S\n\n| Parameter | Value | Kind | Confidence |\n|---|---|---|---|\n| A | 1 | extracted | high |\n";

    [Fact]
    public void Check_command_exit_code_follows_the_findings_and_a_stale_or_missing_register_is_an_error()
    {
        using var w = new Workdir();
        File.WriteAllText(w.Changes, GoodChanges);
        File.WriteAllText(w.Spec, GoodSpec);
        Assert.Equal(1, w.Run("--check")); // no register yet
        Assert.False(File.Exists(w.Out)); // --check writes nothing
        Assert.Equal(0, w.Run());
        Assert.True(File.Exists(w.Out));
        Assert.Equal(0, w.Run("--check"));
        // a spec edit without regenerating: stale
        File.WriteAllText(w.Spec, GoodSpec.Replace("| A | 1 | extracted | high |", "| A | 1 | ours | low |"));
        Assert.Equal(1, w.Run("--check"));
        Assert.Equal(0, w.Run());
        Assert.Equal(0, w.Run("--check"));
        // a row without kind, confidence or source
        File.WriteAllText(w.Spec, GoodSpec.Replace("| A | 1 | extracted | high |", "| A | 1 | — | — |"));
        Assert.Equal(0, w.Run());
        Assert.Equal(1, w.Run("--check"));
        // --list-untagged does not hide the check's result
        File.WriteAllText(w.Spec, GoodSpec.Replace("| A | 1 | extracted | high |", "| A | 1 | ours | low |"));
        Assert.Equal(1, w.Run("--check", "--list-untagged"));
        Assert.Equal(0, w.Run());
        Assert.Equal(0, w.Run("--check", "--list-untagged"));
        // a missing changes file is an error
        File.Delete(w.Changes);
        Assert.Equal(1, w.Run("--check"));
        File.WriteAllText(w.Changes, GoodChanges);
        // a changes row with an empty cell
        File.WriteAllText(w.Spec, GoodSpec);
        File.WriteAllText(w.Changes, GoodChanges.Replace("| X | a | b | owner |", "| X | a |  | owner |"));
        Assert.Equal(0, w.Run());
        Assert.Equal(1, w.Run("--check"));
    }

    [Fact]
    public void Unknown_flags_and_stray_arguments_are_rejected_without_writing()
    {
        using var w = new Workdir();
        File.WriteAllText(w.Changes, GoodChanges);
        File.WriteAllText(w.Spec, GoodSpec);
        Assert.Equal(2, w.Run("--chek"));
        Assert.Equal(2, w.Run("stray"));
        Assert.Equal(2, RefineRegister.Run(["--spec"]));
        Assert.False(File.Exists(w.Out));
    }

    // ---- output -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_register_lists_tagged_rows_the_changes_and_the_summary_deterministically()
    {
        var model = Analyse(Standard);
        const string changesMd = "# Changes\n\n## 1. Deliberate changes (owner's decisions)\n\n| Change | Original | Here | Decision |\n|---|---|---|---|\n| Hip fire | scatter | none | owner |\n\n## 2. Our choices\n\n### Networking\n\n| Rule | What we do | What is unknown | Spec section |\n|---|---|---|---|\n| Tick | 30 Hz | original | Provisional |\n";
        var changes = RefineRegister.ChangeTables(changesMd);
        var a = RefineRegister.Render(model, changes, "behavior.md", "changes.md");
        Assert.Equal(a, RefineRegister.Render(model, changes, "behavior.md", "changes.md"));
        Assert.Contains("## How to use this in a playtest", a);
        Assert.Contains("## Summary", a);
        Assert.Contains("Part C: Deliberate changes (keep or revert after playtest)", a);
        Assert.Contains("| Eye height |", a);
        Assert.DoesNotContain("| Shot cooldown |", a);
        Assert.Contains("Hip fire", a);
        Assert.Contains("#### Networking", a);
        Assert.Contains(model.Tagged.Single(e => e.Name == "Eye height").Id, a);
        Assert.DoesNotContain(@":\", a); // no absolute paths
    }
}
