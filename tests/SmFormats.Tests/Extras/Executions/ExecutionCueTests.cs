using System.Numerics;
using SmContent;
using SmContent.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmFormats.Tests.Extras.Executions;

/// <summary>
/// Executions sound and gore (2026-11-27; docs/executions-cues.md): the cue sheet's build from tags and files (synthetic: names and times invented here, no original bytes), the borrowed blades'
/// motor rule, the blood gate, the once-only clock with its late skip, step-back, interruption and finish, and the crossover rows: every pairing exercised and the knife's added move picked.
/// </summary>
public class ExecutionCueTests
{
    static SoundBinding Snd(string tag, string start, string stop = "") =>
        new($"action_data/triggered_actions/{tag}/commands/post_sound_event", tag, "", "", [], start, stop, false, false, false, 0f, "", new Dictionary<string, float>());

    static SpeechBinding Say(string tag, string type, string character = "autodetect") =>
        new($"action_data/triggered_actions/{tag}/commands/post_speech_exertion_event", tag, "", [], SpeechKind.Exertion, character, type, "", "");

    static FxBinding Fx(string tag, string name, string marker) =>
        new($"action_data/triggered_actions/{tag}/commands/play_fx", "commands", "", [],
            new FxPlay("master_fx", name, 1f, 1f, true, "", marker, "", "", "", "", 0f, "", false, false), null);

    static ExecutionCueFile File(string name, SoundBinding[]? sounds = null, SpeechBinding[]? speech = null, FxBinding[]? fx = null, string? character = null) =>
        new(name, (sounds ?? []).Select(s => s.Trigger).Concat((speech ?? []).Select(s => s.Trigger)).Concat((fx ?? []).Select(f => f.Path.Split('/')[2])).ToHashSet(StringComparer.OrdinalIgnoreCase),
            sounds ?? [], speech ?? [], fx ?? [], character);

    static ExecutionClipEvent A(string name, float start, float end = -1f) => new("Action", name, start, end < 0f ? start : end);

    static ExecutionRow Row(string id) => ExecutionRows.All.First(r => r.Id == id);

    /// <summary>A chainsword row (O1) and a knife row on a chainsword move (C1 borrowed): body foley, a windowed grind with its stop, a swing, an Ork line, blood and a teeth trail.</summary>
    static ExecutionCueSheet Sheet()
    {
        var files = new Dictionary<string, ExecutionCueFile>(StringComparer.OrdinalIgnoreCase)
        {
            ["vanguard"] = File("vanguard", [Snd("TAG_Grab", "play_grab_foley"), Snd("TAG_Punch", "play_fist_hit")], [Say("TAG_Grab", "roar")]),
            ["chainsword_new"] = File("chainsword_new",
                [Snd("TAG_Cut", "play_chainsword_grind_flesh", "stop_chainsword_grind_flesh"), Snd("TAG_Swing", "play_chainsword_swing"), Snd("TAG_Punch", "play_fist_hit")],
                [Say("TAG_Cut", "effort_build"), Say("TAG_Pull", "effort_short_stab", "ork_boy_2"), Say("TAG_End", "damage_small", "")],
                [Fx("TAG_Cut", "chainsword_blood_grind", "fx_chainsword_spray"), Fx("TAG_Cut", "chainsword_armor_swipe", "fx_chainsword_spray"), Fx("TAG_Cut", "blood_off_xx", "fx_swing")]),
            ["combat_knife"] = File("combat_knife", [Snd("TAG_Stab", "play_knife_stab")]),
            ["boy"] = File("boy", [Snd("TAG_Roar", "play_ork_roar")], [Say("TAG_Hurt", "damage_big")], character: "ork_voice"),
            ["chaos_marine_melee"] = File("chaos_marine_melee", [], [Say("TAG_Hurt", "damage_big")], character: "chaos_voice"),
        };
        IReadOnlyList<ExecutionClipEvent> Att(ExecutionRow r) =>
            [A("TAG_Grab", 0.2f), A("TAG_Swing", 0.5f), A("TAG_Cut", 1.0f, 2.0f), A("TAG_Pull", 1.5f), A("TAG_Punch", 2.2f), A("TAG_End", 2.3f), A("ACTN_give_health_50", 2.3f), A("TAG_Stab", 2.4f)];
        IReadOnlyList<ExecutionClipEvent> Vic(ExecutionRow r) => [A("TAG_Roar", 0.3f), A("TAG_Hurt", 1.1f), A("ork_suicide_slam", 2.4f)];
        return ExecutionCueLoader.Build([Row("O1.chainsword"), Row("C1.chainsword"), Row("O4.combat_knife")], Att, Vic, files);
    }

    [Fact]
    public void The_sheet_takes_each_tags_commands_from_the_sides_files()
    {
        var own = Sheet().For("O1.chainsword");
        // The body's and the weapon's commands, the victim's from its own blueprint; rewards and kills are no cue.
        Assert.Contains(own, c => c is { Side: ExecutionCueSide.Attacker, Kind: ExecutionCueKind.Sound, Event: "play_grab_foley", File: "vanguard", At: 0.2f });
        Assert.Contains(own, c => c is { Side: ExecutionCueSide.Victim, Kind: ExecutionCueKind.Sound, Event: "play_ork_roar", Place: ExecutionCuePlace.Victim });
        Assert.DoesNotContain(own, c => c.Tag.StartsWith("ACTN_give", StringComparison.Ordinal) || c.Tag.Contains("suicide"));
        // A windowed grind: its sound at the start (at the wound: ours), its stop at the window's end on the same place.
        var grind = Assert.Single(own, c => c.Event == "play_chainsword_grind_flesh");
        Assert.Equal((1.0f, 2.0f, ExecutionCuePlace.Wound, ExecutionCueKind.Sound), (grind.At, grind.Until, grind.Place, grind.Kind));
        var stop = Assert.Single(own, c => c.Event == "stop_chainsword_grind_flesh");
        Assert.Equal((2.0f, ExecutionCuePlace.Wound, ExecutionCueKind.Stop), (stop.At, stop.Place, stop.Kind));
        // The same hit from the blueprint and the weapon plays once.
        Assert.Single(own, c => c.Event == "play_fist_hit");
        // A named character's line and a characterless damage line in an attacker file are the victim's (ours).
        Assert.Equal(ExecutionCuePlace.Victim, Assert.Single(own, c => c.Tag == "TAG_Pull").Place);
        Assert.Equal(ExecutionCuePlace.Victim, Assert.Single(own, c => c.Tag == "TAG_End").Place);
        Assert.Equal(ExecutionCuePlace.Attacker, Assert.Single(own, c => c.Tag == "TAG_Cut" && c.Kind == ExecutionCueKind.Speech).Place);
        // Blood is flagged, an "xx" effect is not drawn, the chainsword's own trail is.
        Assert.True(Assert.Single(own, c => c.Event == "chainsword_blood_grind").Blood);
        Assert.Contains(own, c => c.Event == "chainsword_armor_swipe" && !c.Blood);
        Assert.DoesNotContain(own, c => c.Event.EndsWith("xx", StringComparison.Ordinal));
        // Time order, and the file's voices are known.
        Assert.Equal(own.OrderBy(c => c.At).Select(c => c.At), own.Select(c => c.At));
        Assert.Equal("ork_voice", Sheet().Characters["boy"]);
    }

    [Fact]
    public void A_borrowed_blade_drops_the_chainsword_motor_and_keeps_the_blood()
    {
        var sheet = Sheet();
        foreach (var (row, wield) in new[] { ("O1.chainsword", ExecutionWield.PowerSword), ("C1.chainsword", ExecutionWield.CombatKnife), ("O4.combat_knife", ExecutionWield.Own) })
        {
            var cues = sheet.For(row, wield);
            Assert.DoesNotContain(cues, c => c.Kind is ExecutionCueKind.Sound or ExecutionCueKind.Stop && c.Event.Contains("chainsword"));   // no motor, no motor stop
            Assert.Equal(ExecutionMotorRole.Grind, Assert.Single(cues, c => c.Kind == ExecutionCueKind.Motor && c.Tag == "TAG_Cut").Motor);
            Assert.Equal(ExecutionMotorRole.Swing, Assert.Single(cues, c => c.Kind == ExecutionCueKind.Motor && c.Tag == "TAG_Swing").Motor);
            Assert.All(cues.Where(c => c.Kind == ExecutionCueKind.Motor), c => Assert.True(c.Ours));
            Assert.Contains(cues, c => c.Event == "chainsword_blood_grind");          // the blood stays
            Assert.DoesNotContain(cues, c => c.Event == "chainsword_armor_swipe");     // the teeth's trail does not
            Assert.Contains(cues, c => c.Event == "play_grab_foley");                 // the body's cues are the same
        }
        // The knife reads its own file first: its stab, which the chainsword's file does not have.
        Assert.Contains(sheet.For("O4.combat_knife"), c => c.Event == "play_knife_stab");
        Assert.DoesNotContain(sheet.For("O1.chainsword"), c => c.Event == "play_knife_stab");
        // The knife on an Ork chainsword row is never a wield (its Ork rows are its own): the row's own list.
        Assert.Same(sheet.For("O1.chainsword"), sheet.For("O1.chainsword", ExecutionWield.CombatKnife));
    }

    [Fact]
    public void The_wield_follows_the_weapon_and_blood_the_gore()
    {
        Assert.Equal(ExecutionWield.PowerSword, ExecutionCueRules.WieldOf(Row("C1.chainsword"), "sm_mp_melee_powersword"));
        Assert.Equal(ExecutionWield.CombatKnife, ExecutionCueRules.WieldOf(Row("C1.chainsword"), "sm_mp_melee_combat_knife"));
        Assert.Equal(ExecutionWield.Own, ExecutionCueRules.WieldOf(Row("C1.chainsword"), "csm_mp_melee_chainsword_gold"));
        Assert.Equal(ExecutionWield.Own, ExecutionCueRules.WieldOf(Row("C2.power_axe"), "sm_mp_melee_powersword"));
        Assert.Equal(ExecutionWield.Own, ExecutionCueRules.WieldOf(Row("O4.combat_knife"), "sm_mp_melee_combat_knife"));
        Assert.True(ExecutionCueRules.BloodAllowed(ContentExtras.Executions | ContentExtras.Warfront));
        Assert.True(ExecutionCueRules.BloodAllowed(ContentExtras.Executions | ContentExtras.Gore));
        Assert.False(ExecutionCueRules.BloodAllowed(ContentExtras.Executions));
        Assert.False(ExecutionCueRules.BloodAllowed(ContentExtras.Executions | ContentExtras.Warfront, off: true));
        // Every row has a weapon file; every wield of a chainsword row is listed.
        Assert.All(ExecutionRows.All, r => Assert.NotEmpty(ExecutionCueRules.GraphFile(r)));
        Assert.Equal([ExecutionWield.Own, ExecutionWield.PowerSword, ExecutionWield.CombatKnife], ExecutionCueRules.Wields(Row("B1.chainsword")));
        Assert.Equal([ExecutionWield.Own, ExecutionWield.PowerSword], ExecutionCueRules.Wields(Row("O8.chainsword")));
    }

    static ExecutionCue Cue(float at, string evt = "e") => new("T1.x", ExecutionWield.Own, ExecutionCueSide.Attacker, ExecutionCueKind.Sound, ExecutionCuePlace.Attacker, at, at, "TAG", evt, "f");

    [Fact]
    public void Each_cue_fires_once_on_the_clock_for_every_client_alike()
    {
        IReadOnlyList<ExecutionCue> cues = [Cue(0.1f, "a"), Cue(0.5f, "b"), Cue(1.0f, "c"), Cue(2.0f, "d")];
        var key = new ExecutionCueTimeline.RunKey(3, 100);
        // Attacker, victim and observer: three clients, each its own frame rate, the same cues in the same order, each once.
        foreach (float step in new[] { 1f / 30f, 1f / 60f, 0.25f })
        {
            var t = new ExecutionCueTimeline();
            var fired = new List<string>();
            var steps = new List<ExecutionCueTimeline.Step>();
            for (float clock = 0f; clock <= 2.6f; clock += step)
            {
                steps.Clear();
                t.Advance(key, cues, clock, steps);
                if (clock > 1.2f && clock < 1.3f) t.Advance(key, cues, 0.2f, steps);   // a prediction correction steps the clock back: nothing fires again
                fired.AddRange(steps.Where(s => s.Kind == ExecutionCueTimeline.StepKind.Fire).Select(s => s.Cue!.Event));
            }
            Assert.Equal(["a", "b", "c", "d"], fired);
            Assert.Equal(4, t.Fired);
        }
    }

    [Fact]
    public void A_late_run_skips_old_cues_and_an_interruption_cancels_the_rest()
    {
        IReadOnlyList<ExecutionCue> cues = [Cue(0.1f, "a"), Cue(0.9f, "b"), Cue(1.6f, "c"), Cue(2.5f, "d")];
        var key = new ExecutionCueTimeline.RunKey(1, 7);
        var t = new ExecutionCueTimeline();
        var steps = new List<ExecutionCueTimeline.Step>();
        t.Advance(key, cues, 1.0f, steps);   // first seen at 1.0 s: "a" is 0.9 s behind (skipped), "b" 0.1 s (plays)
        Assert.Equal(["b"], steps.Select(s => s.Cue!.Event));
        steps.Clear();
        t.Advance(key, cues, 1.7f, steps);
        Assert.Equal(["c"], steps.Select(s => s.Cue!.Event));
        steps.Clear();
        t.Interrupt(key, steps);             // the executor killed: one stop step, "d" never fires
        Assert.Equal([ExecutionCueTimeline.StepKind.Interrupt], steps.Select(s => s.Kind));
        steps.Clear();
        t.Advance(key, cues, 3f, steps);     // a stale frame after the end starts the run again only as a new track, which skips everything behind it
        Assert.DoesNotContain(steps, s => s.Cue?.Event == "d");
        Assert.Equal(1, t.Interrupted);
        var all = new List<ExecutionCueTimeline.Step>();
        t.InterruptAll(all);                 // the match's exit
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void A_completed_run_plays_its_last_cues_then_finishes()
    {
        IReadOnlyList<ExecutionCue> cues = [Cue(0.2f, "a"), Cue(3.0f, "victim late")];
        var key = new ExecutionCueTimeline.RunKey(2, 9);
        var t = new ExecutionCueTimeline();
        var steps = new List<ExecutionCueTimeline.Step>();
        t.Advance(key, cues, 2.8f - 2.7f, steps);
        t.Finish(key);                       // Done at the attacker's end (2.8 s): the victim's later cue still plays
        for (float clock = 2.8f; clock < 4f; clock += 0.1f) t.Advance(key, cues, clock, steps);
        Assert.Equal(["a", "victim late"], steps.Where(s => s.Kind == ExecutionCueTimeline.StepKind.Fire).Select(s => s.Cue!.Event));
        Assert.Equal(ExecutionCueTimeline.StepKind.Finish, steps[^1].Kind);
        Assert.Single(steps, s => s.Kind == ExecutionCueTimeline.StepKind.Finish);
        Assert.False(t.Has(key));
    }

    // ---- the crossover rows ----

    static readonly SimConfig Config = new();

    static IReadOnlyList<Vector4> Straight(float metres) => Enumerable.Range(0, 11).Select(i => new Vector4(0f, metres * i / 10f, 0f, 0f)).ToArray();

    /// <summary>The rows with straight root paths (the install fills the real ones): every variant travels its gap less 1 m, the victim stays.</summary>
    static IReadOnlyList<ExecutionRow> Playable() => ExecutionRows.All.Select(r => r with
    {
        AttackerRoot = Straight(MathF.Max(0f, r.Offset.Y - 1f)), VictimRoot = Straight(0f),
        AttackerVariants = r.AttackerVariants.Select(v => v with { Root = Straight(MathF.Max(0f, v.Distance - 1f)) }).ToArray(),
    }).ToArray();

    [Fact]
    public void The_crossovers_join_their_matchups_and_repeated_presses_pick_them()
    {
        var rows = Playable();
        var set = ExecutionCatalogue.Create(rows);
        // Distinct moves per matchup (docs/executions-cues.md 3): the knife and the chainsword (and so the power sword) on the Boy kinds.
        Assert.Equal(["O3.combat_knife", "O8.combat_knife", "O4.combat_knife"], set.For("*", "sm_mp_melee_combat_knife", ExecutionTargets.OrkBoy).Select(r => r.Id));
        Assert.Equal(["O1.chainsword", "O2.chainsword", "O3.chainsword", "O4.chainsword", "O8.chainsword", "O6.chainsword"], set.For("*", "sm_mp_melee_chainsword", ExecutionTargets.OrkBoy).Select(r => r.Id));
        Assert.Equal(set.For("*", "sm_mp_melee_chainsword", ExecutionTargets.OrkBoy).Select(r => r.Id), set.For("*", "sm_mp_melee_powersword", ExecutionTargets.OrkBoy).Select(r => r.Id));
        // Unchanged elsewhere: the Gretchin, the axe and the hammer, and the knife on the Chaos kinds.
        Assert.Equal(["O9.combat_knife"], set.For("*", "sm_mp_melee_combat_knife", ExecutionTargets.Gretchin).Select(r => r.Id));
        Assert.Equal(4, set.For("*", "sm_mp_melee_power_axe", ExecutionTargets.OrkBoy).Count);
        Assert.Equal(2, set.For("*", "thunder_hammer", ExecutionTargets.OrkBoy).Count);
        Assert.Equal(["C1.chainsword"], set.For("*", "sm_mp_melee_combat_knife", ExecutionTargets.ChaosMarine).Select(r => r.Id));
        // Repeated eligible presses on a Boy 2.4 m away (inside every knife row's distance band): the seeded pick reaches every move, the new one included.
        var e = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines };
        var boy = new MusouEnemy(3, MusouEnemyType.Boy, MusouEnemyState.Seek, new Vector3(0f, 2.4f, 0f), Vector2.Zero, MathF.PI, 100f, -1, 0, 0, 0);
        foreach (var (weapon, expected) in new[] { ("sm_mp_melee_combat_knife", new[] { "O3.combat_knife", "O4.combat_knife", "O8.combat_knife" }) })
        {
            var picked = Enumerable.Range(0, 96).Select(k => ExecutionChoice.ChooseWarfront(e, "tactical", weapon, boy, rows, Config, null, ExecutionChoice.Seed((uint)k, 1, 3)))
                .Where(p => p.Ok).Select(p => p.Row!.Id).Distinct().Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Order(StringComparer.Ordinal), picked);
        }
        // The prompt's first fit (unseeded) is still the knife's own highest-priority move.
        Assert.Equal("O3.combat_knife", ExecutionChoice.ChooseWarfront(e, "tactical", "sm_mp_melee_combat_knife", boy, rows, Config, null, null).Row!.Id);
    }

    [Fact]
    public void Every_pairing_has_a_cue_list_and_the_crossovers_borrow_with_their_own_blade()
    {
        foreach (var r in ExecutionRows.All)
        {
            Assert.NotEmpty(ExecutionCueRules.Wields(r));
            Assert.NotEmpty(ExecutionCueRules.WieldFile(r, ExecutionWield.Own));
        }
        // The crossovers' blades differ from their graphs' weapons: their motors are replaced.
        Assert.NotEqual(ExecutionCueRules.GraphFile(Row("O4.combat_knife")), ExecutionCueRules.WieldFile(Row("O4.combat_knife"), ExecutionWield.Own));
        Assert.NotEqual(ExecutionCueRules.GraphFile(Row("O8.chainsword")), ExecutionCueRules.WieldFile(Row("O8.chainsword"), ExecutionWield.Own));
        Assert.NotEqual(ExecutionCueRules.GraphFile(Row("O6.chainsword")), ExecutionCueRules.WieldFile(Row("O6.chainsword"), ExecutionWield.Own));
        Assert.Equal(ExecutionCueRules.GraphFile(Row("O1.chainsword")), ExecutionCueRules.WieldFile(Row("O1.chainsword"), ExecutionWield.Own));
    }
}
