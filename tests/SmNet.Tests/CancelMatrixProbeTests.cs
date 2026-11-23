using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// cancel-editor: the table-driven check of the default table against the code. For every (action in progress, input) cell that can be reached with the synthetic classes of the melee, hop and
/// charge tests, the simulation is put in the action, the input is pressed at several moments of it, and what the code did (the action ended or a new one started: yes; the press kept in the input
/// buffer: queued; nothing: no) is compared with the baseline's cell. Cells that cannot be reached here (no weapon, throw or brace in the synthetic classes), cells the matrix marks not applicable
/// and the held inputs whose effect is not a state change (fire, aim, sprint, the stick outside a charge) are reported as not probed, never as passed.
/// </summary>
public class CancelMatrixProbeTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    static int Col(string id) => CancelLayout.ColIndex(id);
    static int Row(string id) => CancelLayout.RowIndex(id);

    /// <summary>One way into an action: a config, a class and the state at the action's start.</summary>
    sealed record Entry(string Row, SimConfig Config, int Class, Func<SimConfig, PlayerState> Enter);

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float mx = 0f, float my = 0f) =>
        Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b), c, out _);

    static PlayerState StepOut(PlayerState s, SimConfig c, InputButtons b, float mx, float my, out SelfEvents e) =>
        Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b), c, out e);

    static readonly SimConfig Pack = CancelGateTests.Plays(MeleeCancelTests.Pack, null);
    static readonly SimConfig Heavy = CancelGateTests.Plays(MeleeCancelTests.Heavy with { InputBufferWindow = 0.3f }, null);

    static List<Entry> Entries() =>
    [
        new("melee_light", Heavy, 0, c => Step(MeleeCancelTests.Fresh(c, 0), c, InputButtons.Melee)),
        new("melee_light", Pack, 0, c => Step(MeleeCancelTests.Fresh(c, 0), c, InputButtons.Melee)),
        new("melee_heavy", Heavy, 0, c => Step(MeleeCancelTests.Fresh(c, 0), c, InputButtons.MeleeHeavy)),
        new("charge", Heavy, 0, c => MeleeCancelTests.Charging(c, 0, 0)),
        new("charge", Pack, 0, c => MeleeCancelTests.Charging(c, 0, 0)),
        new("evade_roll", Heavy, 0, c => Step(MeleeCancelTests.Fresh(c, 0), c, InputButtons.Jump)),
        new("evade_hop", Heavy, 1, c => Step(MeleeCancelTests.Fresh(c, 1), c, InputButtons.Jump)),
        new("jp_hop", Pack, 0, c => Step(Step(MeleeCancelTests.Fresh(c, 0), c, InputButtons.Jump), c, InputButtons.None)),
        new("jp_takeoff", Pack, 0, c => Step(MeleeCancelTests.Fresh(c, 0), c, InputButtons.Jump)),
        new("reload", Heavy, 0, c => Step(MeleeCancelTests.Fresh(c, 0) with { Clip = 5 }, c, InputButtons.Reload)),
        new("reload", Pack, 0, c => Step(MeleeCancelTests.Fresh(c, 0) with { Clip = 5 }, c, InputButtons.Reload)),
    ];

    /// <summary>The press of an input column (the way it is made), or null for a column that is not a press.</summary>
    static (InputButtons Buttons, float Mx, float My)? Press(string col, SimConfig c, PlayerState s) => col switch
    {
        "light" => (InputButtons.Melee, 0f, 0f),
        "heavy" => (InputButtons.MeleeHeavy, 0f, 0f),
        "charge" => (InputButtons.Sprint | InputButtons.MeleeHeavy, 0f, 1f),
        "roll" => Loadouts.JumpPack(c, s) is null ? (InputButtons.Jump, 0f, 0f) : null,
        "pack" => Loadouts.JumpPack(c, s) is not null ? (InputButtons.Jump, 0f, 0f) : null,
        "reload" => (InputButtons.Reload, 0f, 0f),
        "switch" => Loadouts.Holstered(c, s) is not null ? (InputButtons.Switch, 0f, 0f) : null,
        "grenade" => Loadouts.Equipment(c, s) is not null ? (InputButtons.Grenade, 0f, 0f) : null,
        "back" => (InputButtons.None, 0f, -1f),
        _ => null,
    };

    /// <summary>What the code did with a press: ended the action in progress; kept the press (the input buffer, or the combo's queue); started its own action beside the one in progress; nothing.</summary>
    enum Seen { Yes, No, Queued, Parallel }

    /// <summary>Whether the action of <paramref name="row"/> is still going in <paramref name="s"/> (its own state, not "the first row that applies").</summary>
    static bool Live(string row, PlayerState s) => row switch
    {
        "melee_light" or "melee_light_last" or "melee_heavy" or "melee_stomp" => s.MeleeStep > 0,
        "charge" or "charge_air" => s.Rush == RushPhase.Rush,
        "evade_roll" or "evade_hop" or "jp_hop" => s.EvadeTicks > 0,
        "jp_takeoff" => s.Jump == JumpPhase.TakeOff,
        "reload" => s.ReloadLeft > 0f,
        _ => false,
    };

    static bool Started(PlayerState before, PlayerState after) =>
        (after.MeleeStep > 0 && before.MeleeStep == 0) || (after.EvadeTicks > 0 && before.EvadeTicks == 0) || (after.Throwing && !before.Throwing) || (after.SwitchLeft > 0f && before.SwitchLeft <= 0f)
        || (after.ReloadLeft > 0f && before.ReloadLeft <= 0f) || (after.Rush != RushPhase.None && before.Rush == RushPhase.None) || (after.Jump != JumpPhase.None && before.Jump == JumpPhase.None);

    static Seen? Observe(Entry e, int ticks, string col)
    {
        var c = e.Config;
        var s = e.Enter(c);
        for (int k = 0; k < ticks; k++) s = Step(s, c, InputButtons.None);
        if (!Live(e.Row, s)) return null;                                                    // the action is over (or never began) at this moment
        if (Press(CancelLayout.Cols[Col(col)], c, s) is not { } p) return null;
        var control = Step(s, c, InputButtons.None);
        if (!Live(e.Row, control)) return null;                                              // it ends by itself on this tick: nothing to learn
        var pressed = StepOut(s, c, p.Buttons, p.Mx, p.My, out _);
        if (!Live(e.Row, pressed) || pressed.Rush != control.Rush) return Seen.Yes;
        if ((pressed.Buffered != BufferedAction.None && control.Buffered == BufferedAction.None) || (pressed.MeleeQueued && !control.MeleeQueued)) return Seen.Queued;
        return Started(control, pressed) ? Seen.Parallel : Seen.No;
    }

    static string Short(CancelState s) => s.ToString();

    [Fact]
    public void Every_reachable_default_cell_matches_what_the_simulation_does()
    {
        var times = new[] { 1, 2, 3, 4, 6, 8, 10, 13, 16, 20, 24, 28, 32, 36 };
        var table = CancelData.Baseline;
        var mismatches = new List<string>();
        int probed = 0, unprobed = 0, na = 0;
        var seenCells = new HashSet<(int, int)>();
        foreach (var entry in Entries())
            for (int col = 0; col < CancelLayout.ColCount; col++)
            {
                string colId = CancelLayout.Cols[col];
                int row = Row(entry.Row);
                var cell = table.Base(row, col);
                if (cell.State == CancelState.NotApplicable) { na++; continue; }
                var seen = new HashSet<Seen>();
                foreach (int t in times) if (Observe(entry, t, colId) is { } o) seen.Add(o);
                if (seen.Count == 0) { unprobed++; continue; }
                seenCells.Add((row, col)); probed++;
                bool ok = cell.State switch
                {
                    CancelState.Yes => seen.SetEquals([Seen.Yes]),
                    CancelState.No => seen.IsSubsetOf([Seen.No, Seen.Parallel]),
                    CancelState.Queued => seen.Contains(Seen.Queued),
                    CancelState.Window => (seen.Contains(Seen.Yes) && seen.Count > 1) || !seen.Contains(Seen.Yes),   // the synthetic clips carry no window times: a window never seen to open (only a queued or ignored press) is not a mismatch
                    _ => true,
                };
                if (!ok) mismatches.Add($"{CancelLayout.Number(row, col)} {entry.Row} x {colId} ({(entry.Config == Pack ? "Assault" : "Marine")}, class {entry.Class}): matrix {Short(cell.State)}, code {string.Join("/", seen.OrderBy(x => x))}");
            }
        output.WriteLine($"probed {probed} (entry, input) pairs over {seenCells.Count} distinct cells; not reachable {unprobed}; not applicable {na}; mismatches {mismatches.Count}");
        foreach (var m in mismatches) output.WriteLine("MISMATCH " + m);
        Assert.True(seenCells.Count >= 30, $"only {seenCells.Count} cells probed");
        Assert.Empty(mismatches);
    }

    /// <summary>
    /// The matrix the owner reviewed (Y yes, . no, W window, Q queued, - not applicable; 37 rows of 16 inputs, in matrix order), with the seven cells <see cref="Every_reachable_default_cell_matches_what_the_simulation_does"/>
    /// found the code to disagree with (the charge rows' heavy and charge inputs: queued, not no; a charge press ends a reload) corrected. Written out here so a change to the data file is a visible change to a test.
    /// </summary>
    static readonly string[] Reviewed =
    [
        "QQWWYW.QQQ...-Y.",   // melee_light
        "WWWWYW.QQQ...-Y.",   // melee_light_last
        "QWWWYW.QQQ...-Y.",   // melee_heavy
        "WWWW-WWWWWWWW-W.",   // melee_stomp
        "YQQWY..QQQ..Y-W.",   // charge
        "YQQWY..QQQ..Y-W.",   // charge_sprint
        "YQQ.Y..QQQ...-W.",   // charge_air
        "WWWWY..QQQ.WW-Y.",   // charge_stop
        "WW.-Y..QQQ.WW-Y.",   // dash_attack
        "WWQW-.WQWW.WW-W.",   // evade_roll
        "WWQW-WWQQW.WW-W.",   // evade_hop
        "QQQ-W........-Y.",   // jp_takeoff
        "YQQ-WWWQQW.WW-W.",   // jp_hop
        "YQQ-W........-..",   // jp_flight
        "QQQ-Q..QQQ...-..",   // jp_dive
        "QQQ-Y..QQQ...-Y.",   // jp_recover
        "YYQ-Y.....WWW-Y.",   // jp_landed
        "YYYYW-.YYYY..-Y.",   // hip_fire
        "YYYYW--YYY...-Y.",   // aim_fire
        "YYYYWY.YYYY..-Y.",   // charged_shot
        "..Y.....Y....-..",   // reload
        ".......QYQ...-..",   // switch
        "YYYYY-.QYYY..-Y.",   // raise
        "QQQWY..QQQ...-Y.",   // grenade
        "QQQWY..QQQ...-..",   // stim
        "YYYYY.Y...-YY-Y.",   // sprint
        "YYYYYYY....YY-Y.",   // sprint_stop
        "QQQQ-..QQQ...-..",   // brace_enter
        "....-..Y.....-..",   // braced
        "QQQY-..QQQ...-..",   // brace_exit
        "YYYYYY..YY...-Y.",   // vent
        "----------------",   // exec_doing
        "----------------",   // exec_victim
        "YYYYYYYYYYYYY-W.",   // react_interrupt
        "...WY..QQQ...-..",   // react_stun
        "....Y..QQQ...-..",   // react_stagger
        "--------------..",   // flinch
    ];

    [Fact]
    public void The_shipped_default_and_the_baseline_equal_the_reviewed_matrix_in_every_cell()
    {
        Assert.Equal(Reviewed.Length, CancelLayout.RowCount);
        for (int r = 0; r < Reviewed.Length; r++)
            for (int c = 0; c < CancelLayout.ColCount; c++)
            {
                var want = Reviewed[r][c] switch { 'Y' => CancelState.Yes, '.' => CancelState.No, 'W' => CancelState.Window, 'Q' => CancelState.Queued, _ => CancelState.NotApplicable };
                Assert.True(want == CancelData.Baseline.Base(r, c).State, $"baseline {CancelLayout.Number(r, c)} {CancelLayout.Rows[r]} x {CancelLayout.Cols[c]}");
                if (!CancelDefaultTests.IsExportCell(r, c)) Assert.True(want == CancelData.Shipped.Base(r, c).State, $"shipped {CancelLayout.Number(r, c)} {CancelLayout.Rows[r]} x {CancelLayout.Cols[c]}");   // the exported cells (4 since the 11-23 export) are checked in CancelDefaultTests
                if (!CancelDefaultTests.IsExportCell(r, c)) Assert.False(CancelData.Shipped.Base(r, c).HasTimes);
            }
        Assert.Equal(37 * 16, CancelLayout.RowCount * CancelLayout.ColCount);
    }
}
