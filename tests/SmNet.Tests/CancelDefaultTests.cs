using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The owner's 11-23 export as the Classic PvP default (spec "Cancel table"; the 11-18 export with 1.1 back to the baseline's queued): what it differs from the baseline in (4 cells), and what each cell does in play with the shipped table.
/// Cells are (action in progress) x (input); the seconds are into the action in progress. Synthetic clips only.
/// </summary>
public class CancelDefaultTests(ITestOutputHelper output)
{
    /// <summary>The four cells the shipped default differs from the baseline in: (row id, column id, the shipped cell). 1.1 (a light press in a light swing) is the baseline's queued again since the 11-23 export.</summary>
    internal static readonly (string Row, string Col, CancelCell Cell)[] ExportCells =
    [
        ("melee_light", "charge", new(CancelState.Window, 500)),
        ("charge", "light", new(CancelState.Window, 1000)),
        ("charge_sprint", "light", new(CancelState.Window, 1000)),
        ("dash_attack", "light", new(CancelState.Yes)),
    ];

    internal static bool IsExportCell(int r, int c) => ExportCells.Any(e => CancelLayout.RowIndex(e.Row) == r && CancelLayout.ColIndex(e.Col) == c);

    const float Dt = 1f / 30f;
    const InputButtons F = InputButtons.Sprint | InputButtons.MeleeHeavy, Rmb = InputButtons.Sprint | InputButtons.Melee;

    /// <summary>A charge that lasts 3 s (the synthetic clip of RushTests ends the rush at 1 s, so a window opening at 1 s could never be seen in it).</summary>
    static readonly RushSpec LongRush = RushTests.Spec with
    {
        Rush = new("SM_BUL_Hit_M_long", [.. Enumerable.Range(0, 31).Select(k => new Vector2(0f, 30f * k / 30))], 3f, 3f, 0.6f, 0.5f, 1.0f),
    };

    static SimConfig Long(CancelData? table)
    {
        var c = RushTests.Config;
        return CancelGateTests.Plays(c with { Classes = [.. c.Classes.Select(k => k with { MeleeRush = LongRush })] }, table);
    }

    static PlayerState Fresh(SimConfig c, int cls = 0) => MeleeCancelTests.Fresh(c, cls);
    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b = InputButtons.None, float my = 0f) => Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out _);
    static PlayerState Idle(PlayerState s, SimConfig c, int ticks) { for (int i = 0; i < ticks; i++) s = Step(s, c); return s; }

    [Fact]
    public void The_shipped_default_is_the_baseline_plus_exactly_the_four_exported_cells()
    {
        Assert.Null(CancelData.ShippedError);
        Assert.True(CancelData.Shipped.DiffersFromBaseline);
        Assert.NotEqual(CancelData.Baseline.Hash, CancelData.Shipped.Hash);
        var diff = CancelData.Shipped.DiffAgainst(CancelData.Baseline);
        Assert.Equal(ExportCells.Length, diff.Count);
        foreach (var (row, col, cell) in ExportCells)
        {
            int r = CancelLayout.RowIndex(row), c = CancelLayout.ColIndex(col);
            Assert.True(r >= 0 && c >= 0, $"{row} x {col}");
            Assert.Equal(cell, CancelData.Shipped.Base(r, c));
            Assert.Contains(diff, d => d.Scope.IsBase && d.Row == r && d.Col == c && d.Cell == cell);
            Assert.NotEqual(CancelData.Baseline.Base(r, c), CancelData.Shipped.Base(r, c));
        }
        Assert.Equal("1.3 5.1 6.1 9.1", string.Join(" ", ExportCells.Select(e => CancelLayout.Number(CancelLayout.RowIndex(e.Row), CancelLayout.ColIndex(e.Col)))));
        Assert.True(CancelGate.Active(new SimConfig()));                                          // a default config plays the table
        Assert.False(CancelGate.Active(new SimConfig() with { Extras = ContentExtras.Musou }));   // never Musou Mode
    }

    // ---- 1.1: a light press during a light swing (the baseline's queued again, owner's 11-23 export) ----

    [Fact]
    public void A_light_press_in_a_swing_queues_the_next_step_as_the_baseline_does()
    {
        var def = CancelGateTests.Plays(RushTests.Config, CancelData.Shipped);
        var old = CancelGateTests.Plays(RushTests.Config, null);
        int r = CancelLayout.RowIndex("melee_light"), c = CancelLayout.ColIndex("light");
        Assert.Equal(CancelData.Baseline.Base(r, c), CancelData.Shipped.Base(r, c));
        Assert.Equal(CancelState.Queued, CancelData.Shipped.Base(r, c).State);
        int probed = 0, queued = 0;
        for (int k = 0; k < 24; k++)
        {
            PlayerState Run(SimConfig cfg, out PlayerState before) { var s = Idle(Step(Fresh(cfg), cfg, InputButtons.Melee), cfg, k); before = s; return Step(s, cfg, InputButtons.Melee); }
            var after = Run(def, out var b);
            var oldAfter = Run(old, out _);
            if (b.MeleeStep == 0) break;
            probed++;
            Assert.Equal(oldAfter, after);                      // the shipped table plays this cell exactly as the code's own rule
            if (after.MeleeQueued) queued++;
            output.WriteLine($"t {b.MeleeTime:F3}: queued {after.MeleeQueued}");
        }
        Assert.True(probed >= 12, $"only {probed} ticks of the swing probed");
        Assert.True(queued >= 5, "a press during the swing queues the next step");
    }

    // ---- 1.3: a charge press during a light swing ----

    [Fact]
    public void A_charge_press_in_a_swing_is_refused_before_half_a_second_and_allowed_after()
    {
        var def = CancelGateTests.Plays(RushTests.Config, CancelData.Shipped);
        var old = CancelGateTests.Plays(RushTests.Config, null);
        int refused = 0, allowed = 0, oldAllowedEarly = 0;
        for (int k = 0; k < 24; k++)
        {
            PlayerState Run(SimConfig c, out PlayerState before) { var s = Idle(Step(Fresh(c), c, InputButtons.Melee), c, k); before = s; return Step(s, c, F, 1f); }
            var after = Run(def, out var b);
            var oldAfter = Run(old, out _);
            if (b.MeleeStep == 0) break;
            float t = b.MeleeTime;
            if (t < 0.5f - 1e-3f) { refused++; Assert.NotEqual(RushPhase.Rush, after.Rush); if (oldAfter.Rush == RushPhase.Rush) oldAllowedEarly++; }
            else { allowed++; Assert.Equal(RushPhase.Rush, after.Rush); Assert.Equal(0, after.MeleeStep); }
        }
        Assert.True(refused >= 10 && allowed >= 3, $"refused {refused}, allowed {allowed}");
        Assert.True(oldAllowedEarly >= 5, "the baseline lets the charge in as soon as the hit has landed (0.2 s)");
    }

    // ---- 5.1 and 6.1: a light press during a charge ----

    [Theory]
    [InlineData(false)]   // started with F (heavy) + Shift + W
    [InlineData(true)]    // started with the sprint-RMB charge (cell 6.1's row is "charge_sprint")
    public void A_light_press_in_a_charge_is_refused_before_one_second_and_ends_it_into_a_swing_after(bool rmbStart)
    {
        var def = Long(CancelData.Shipped);
        var old = Long(null);
        int refused = 0, allowed = 0, oldAllowedEarly = 0;
        for (int k = 0; k < 60; k += 2)
        {
            PlayerState Run(SimConfig c, out PlayerState before) { var s = Idle(Step(Fresh(c), c, rmbStart ? Rmb : F, 1f), c, k); before = s; return Step(s, c, InputButtons.Melee); }
            var after = Run(def, out var b);
            var oldAfter = Run(old, out _);
            Assert.Equal(RushPhase.Rush, b.Rush);
            float t = b.RushTicks * Dt;
            if (t < 1f - 1e-3f)
            {
                refused++;
                Assert.Equal(RushPhase.Rush, after.Rush);          // the charge runs on
                Assert.Equal(0, after.MeleeStep);                  // no swing
                Assert.False(after.MeleeQueued);
                if (oldAfter.Rush != RushPhase.Rush && oldAfter.MeleeStep == 1) oldAllowedEarly++;
            }
            else
            {
                allowed++;
                Assert.NotEqual(RushPhase.Rush, after.Rush);       // the charge ends...
                Assert.Equal(1, after.MeleeStep);                  // ...and the swing starts on the same tick
            }
        }
        output.WriteLine($"{(rmbStart ? "RMB" : "F")} start: refused {refused}, allowed {allowed}, baseline allowed early {oldAllowedEarly}");
        Assert.True(refused >= 10 && allowed >= 5, $"refused {refused}, allowed {allowed}");
        Assert.True(oldAllowedEarly >= 10, "the baseline ends a charge for a light press at any tick");
    }

    // ---- 9.1: a light press during an aerial tackle ----

    static (PlayerState InTackle, PlayerState After) TackleThenLight(SimConfig c, int idle)
    {
        var s = Step(Fresh(c), c, InputButtons.Jump, 1f);
        s = Step(s, c, InputButtons.None, 1f);
        while (s.EvadeTicks < 10) s = Step(s, c, InputButtons.None);
        s = Step(s, c, InputButtons.Melee, 1f);
        Assert.Equal(RushPhase.DashAttack, s.Rush);
        s = Step(s, c);                                            // release: the next press is a fresh one
        for (int i = 0; i < idle; i++) s = Step(s, c);
        var inTackle = s;
        return (inTackle, Step(s, c, InputButtons.Melee));
    }

    [Fact]
    public void A_light_press_in_an_aerial_tackle_ends_it_into_a_swing_at_once()
    {
        var def = CancelGateTests.Plays(JumpPackAttackTests.Config, CancelData.Shipped);
        var old = CancelGateTests.Plays(JumpPackAttackTests.Config, null);
        int cut = 0, oldHeld = 0;
        for (int idle = 0; idle < 6; idle++)
        {
            var (inTackle, after) = TackleThenLight(def, idle);
            if (inTackle.Rush != RushPhase.DashAttack) break;      // the tackle is over
            cut++;
            Assert.NotEqual(RushPhase.DashAttack, after.Rush);
            Assert.Equal(1, after.MeleeStep);
            var (_, oldAfter) = TackleThenLight(old, idle);
            if (oldAfter.Rush == RushPhase.DashAttack && oldAfter.MeleeStep == 0) oldHeld++;
        }
        Assert.True(cut >= 2, $"only {cut} ticks of the tackle probed");
        output.WriteLine($"cut {cut} ticks; the baseline held the press for {oldHeld} of them");
    }

    // ---- prediction ----

    [Fact]
    public void Prediction_parity_the_self_step_and_the_server_step_agree_through_swing_then_charge_with_the_shipped_table()
    {
        // The same sequence through the server's step (StepPlayer) and the client's prediction (StepSelf) must give identical states every tick:
        // a swing, a charge press at 0.2 s (refused) and at 0.57 s (starts; since the 11-23 export no light press in the swing, which would queue the next step as the baseline does), then a light press 0.27 s into the charge (refused) and 1.07 s in (ends it and swings).
        var c = Long(CancelData.Shipped);
        PlayerInput In(int t) => t switch
        {
            0 => new(0, 0f, 0f, 0f, InputButtons.Melee),
            6 => new(0, 0f, 1f, 0f, F),
            17 => new(0, 0f, 1f, 0f, F),
            25 => new(0, 0f, 0f, 0f, InputButtons.Melee),
            55 => new(0, 0f, 0f, 0f, InputButtons.Melee),
            _ => new(0, 0f, 0f, 0f, InputButtons.None),
        };
        var a = Fresh(c); var b = Fresh(c);
        bool charged = false, swungAfter = false;
        for (int t = 0; t < 110; t++)
        {
            var input = In(t) with { Tick = (uint)t };
            a = Combat.StepPlayer(a, input, c, out _);
            b = Combat.StepSelf(b, input, c, out _, out _);
            Assert.Equal(a, b);
            if (t is >= 6 and <= 16) Assert.NotEqual(RushPhase.Rush, a.Rush);
            if (t is >= 18 and < 55) charged |= a.Rush == RushPhase.Rush;
            if (t == 25) Assert.Equal(RushPhase.Rush, a.Rush);
            if (t == 55) swungAfter = a.MeleeStep == 1 && a.Rush != RushPhase.Rush;
        }
        Assert.True(charged);
        Assert.True(swungAfter);
    }
}
