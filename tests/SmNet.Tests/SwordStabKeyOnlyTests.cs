using System.Numerics;
using SmClient;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// sim/stab-key-only (owner, 2026-11-23; brief series-36 power sword stab): in Classic PvP the power sword's F + F is the stomp again, and the heavy stab comes only from the Sword Stab action
/// (default T, rebindable, no controller default). Synthetic heavy list shaped like the content read: Y1 and the X,F strike (both chaining into the finisher), the finisher stomp, then the stab,
/// which no chain names. Both pack classes (the Assault Marine and the Raptor) carry the same sword. The cancel matrix's Power Sword Stab section drives play without touching stomp or generic melee.
/// </summary>
public class SwordStabKeyOnlyTests
{
    static float T(float ticks) => ticks / 30f;

    static readonly ReactionEntry Stagger = new(0f, ReactionKind.Stagger, Backward: 1f, Duration: 0.2f);
    static readonly SplashSpec Blast = new(7f, new DamageSpec(25f, 50f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(7f, 1f)], false, true, "target_all");

    /// <summary>Light attack 1: hit at tick 7, combo window ticks 13..19 (a heavy press there chains to heavy strike 2, the X,F strike).</summary>
    static readonly MeleeStep X1 = new(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 65f, 30f, 3.5f);
    /// <summary>Heavy strikes 1 and 2: hit at tick 10, Y window ticks 15..25 into the finisher (3).</summary>
    static MeleeStep Y(float damage) => new MeleeStep(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), damage, 0f, 3.5f) { YNext = 3, YComboOpen = T(14.5f), YComboClose = T(24.5f) };
    static readonly MeleeStep Stomp = new(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), 0f, 0f, 3.5f) { Stomp = Blast };
    static readonly MeleeStep StabStep = new(T(54.5f), T(19.5f), T(20.5f), T(30.5f), T(13.5f), T(27.5f), 100f, 0f, 3.5f) { PrimaryReaction = Stagger, ShieldDamage = 50f, Stab = true };
    const int StabAt = 4;

    static ClassLoadout Sword(string name, Team side) => new(name, -1, -1, [X1]) { MeleeHeavy = [Y(5f), Y(35f), Stomp, StabStep], Side = side };

    /// <summary>Class 0 the Assault Marine, 1 the Raptor, 2 and 3 plain targets of each side.</summary>
    static SimConfig Config(CancelData? table = null) => new()
    {
        Classes =
        [
            Sword("sm - assault marine", Team.SpaceMarines), Sword("csm - raptor", Team.Chaos),
            new ClassLoadout("chaos target", -1, -1, [X1]) { Side = Team.Chaos }, new ClassLoadout("marine target", -1, -1, [X1]) { Side = Team.SpaceMarines },
        ],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        HealthMax = 200f, ShieldMax = 300f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
        Cancel = CancelLive.Of(table ?? CancelData.Baseline),
    };

    /// <summary>A sword user (1) facing a target in front (2) and a second target beside it (3, outside the cone: only a blast reaches it).</summary>
    static World Make(int swordClass, CancelData? table = null)
    {
        int target = swordClass == 0 ? 2 : 3;
        var w = new World(Config(table), null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f), new SpawnPoint(new Vector3(3, -1, 0), 0f)]);
        w.Spawn(1, swordClass); w.Spawn(2, target); w.Spawn(3, target);
        return w;
    }

    static List<HitEvent> Run(World w, int ticks, Func<int, InputButtons> buttons, List<(int Step, bool Heavy)>? steps = null)
    {
        var hits = new List<HitEvent>();
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            w.Step(id => new PlayerInput(0, 0f, 0f, id == 1 ? 0f : MathF.PI, id == 1 ? buttons(tick) : InputButtons.None));
            hits.AddRange(w.LastHits.Where(h => h.Shooter == 1));
            steps?.Add((w.Players[1].MeleeStep, w.Players[1].MeleeHeavy));
        }
        return hits;
    }

    static bool Stabbed(List<(int Step, bool Heavy)> steps) => steps.Any(s => s.Heavy && s.Step == StabAt);

    // ---- the stomp is back on F + F; no combo reaches the stab ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void F_then_F_is_the_normal_stomp_with_its_blast_on_both_classes(int cls)
    {
        var w = Make(cls);
        var steps = new List<(int, bool)>();
        var hits = Run(w, 70, k => k == 0 || k == 20 ? InputButtons.MeleeHeavy : InputButtons.None, steps);
        Assert.Contains((3, true), steps);                                           // the finisher stomp
        Assert.False(Stabbed(steps));
        Assert.True(w.Players[3].Shield < 300f || w.Players[3].Health < 200f, "the stomp's blast reaches the target beside");
        Assert.DoesNotContain(hits, h => h.Melee && h.ShieldDamage == 150f);         // no stab damage anywhere
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Light_then_F_is_the_X_F_strike_and_light_F_F_is_the_stomp_never_the_stab(int cls)
    {
        var w = Make(cls);
        var steps = new List<(int, bool)>();
        Run(w, 90, k => k == 0 ? InputButtons.Melee : k == 14 || k == 14 + 20 ? InputButtons.MeleeHeavy : InputButtons.None, steps);
        Assert.Contains((2, true), steps);    // RMB -> F: the data's X,F strike (heavy strike 2), not the stab
        Assert.Contains((3, true), steps);    // -> F: the stomp
        Assert.False(Stabbed(steps));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Mashing_every_melee_input_but_the_stab_key_never_stabs(int cls)
    {
        var w = Make(cls);
        var steps = new List<(int, bool)>();
        Run(w, 300, k => (k % 7) switch { 0 => InputButtons.Melee, 3 => InputButtons.MeleeHeavy, 5 => InputButtons.Melee | InputButtons.MeleeHeavy, _ => InputButtons.None }, steps);
        Assert.False(Stabbed(steps));
        Assert.NotEqual(StabAt, MeleeChain.HeavyTarget(Y(0f), 1, true, 4));   // no Y chain names the stab
        Assert.NotEqual(StabAt, MeleeChain.HeavyTarget(X1, 1, false, 4));     // nor the light attack's heavy follow-up
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_stab_bit_alone_starts_the_stab_with_its_hit_on_both_classes(int cls)
    {
        var w = Make(cls);
        var steps = new List<(int, bool)>();
        var hits = Run(w, 60, k => k == 0 ? InputButtons.Stab : InputButtons.None, steps);
        Assert.Equal((StabAt, true), steps[0]);
        var hit = Assert.Single(hits, h => h.Melee);
        Assert.Equal((2, 150f), (hit.Target, hit.ShieldDamage));
        Assert.Equal((300f, 200f), (w.Players[3].Shield, w.Players[3].Health));   // no blast
    }

    // ---- the binding: default T, rebind, persistence, the old key ----

    static InputButtons Keys(BindingSet set, params string[] down) =>
        BindingSet.Sample(a => set.Inputs(BindDevice.Keyboard, a).Any(down.Contains), captured: true, includeMove: false).Buttons;

    [Fact]
    public void T_is_the_default_stab_key_with_no_controller_default_and_F_is_not_a_stab()
    {
        var d = BindingSet.Defaults();
        Assert.Equal(InputButtons.Stab, Keys(d, "Key:T") & InputButtons.Stab);
        Assert.Equal(InputButtons.None, Keys(d, "Key:F") & InputButtons.Stab);
        Assert.Equal(InputButtons.None, Keys(d, "Mouse:Right") & InputButtons.Stab);
        Assert.Null(d.Get(BindDevice.Controller, GameAction.Stab, 0));
    }

    [Fact]
    public void A_rebound_stab_key_persists_across_a_restart_and_the_old_key_no_longer_stabs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-stab-bind-" + Guid.NewGuid().ToString("N"));
        try
        {
            var set = BindingSet.Defaults();
            Assert.True(set.Assign(BindDevice.Keyboard, GameAction.Stab, 0, "Key:Y"));
            Assert.Null(set.Save(dir));
            var back = BindingSet.Load(dir, out _);                     // a restart reads the file again
            Assert.Equal("Key:Y", back.Get(BindDevice.Keyboard, GameAction.Stab, 0));
            Assert.Equal(InputButtons.Stab, Keys(back, "Key:Y") & InputButtons.Stab);
            Assert.Equal(InputButtons.None, Keys(back, "Key:T") & InputButtons.Stab);   // T no longer stabs
            // deliberately kept: T in the second slot still stabs, beside Y
            Assert.True(back.Assign(BindDevice.Keyboard, GameAction.Stab, 1, "Key:T"));
            Assert.Null(back.Save(dir));
            var again = BindingSet.Load(dir, out _);
            Assert.Equal(InputButtons.Stab, Keys(again, "Key:T") & InputButtons.Stab);
            Assert.Equal(InputButtons.Stab, Keys(again, "Key:Y") & InputButtons.Stab);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void Rebinding_the_stun_strike_moves_the_stomp_input_and_leaves_the_stab_on_its_own_key()
    {
        var set = BindingSet.Defaults();
        Assert.True(set.Assign(BindDevice.Keyboard, GameAction.StunStrike, 0, "Key:Q"));
        Assert.Equal(InputButtons.MeleeHeavy, Keys(set, "Key:Q") & (InputButtons.MeleeHeavy | InputButtons.Stab));
        Assert.Equal(InputButtons.None, Keys(set, "Key:F") & (InputButtons.MeleeHeavy | InputButtons.Stab));
        Assert.Equal(InputButtons.Stab, Keys(set, "Key:T") & (InputButtons.MeleeHeavy | InputButtons.Stab));
    }

    // ---- the cancel matrix's Power Sword Stab section drives play ----

    static int Row(string id) => CancelLayout.RowIndex(id);
    static int Col(string id) => CancelLayout.ColIndex(id);
    static CancelData Edit(string row, string col, CancelCell cell) => CancelData.Baseline.With(CancelScope.Any, Row(row), Col(col), cell);

    [Fact]
    public void The_section_is_the_last_row_and_column_and_its_defaults_are_the_code()
    {
        Assert.Equal(CancelData.StabRowId, CancelLayout.Rows[^1]);
        Assert.Equal(CancelData.StabColId, CancelLayout.Cols[^1]);
        for (int c = 0; c < CancelLayout.ColCount; c++) Assert.Equal(CancelData.Baseline.Base(Row("melee_stab"), c), CancelData.Shipped.Base(Row("melee_stab"), c));
        for (int r = 0; r < CancelLayout.RowCount; r++) Assert.Equal(CancelData.Baseline.Base(r, Col("stab")), CancelData.Shipped.Base(r, Col("stab")));
        Assert.Equal(CancelState.NotApplicable, CancelData.Shipped.Base(Row("exec_doing"), Col("stab")).State);   // shown as inapplicable
        Assert.Equal(CancelState.NotApplicable, CancelData.Shipped.Base(Row("melee_stab"), Col("exec")).State);
    }

    [Fact]
    public void The_stab_is_its_own_row_while_it_plays_and_an_F_strike_and_the_stomp_keep_theirs()
    {
        var c = Config();
        var s = MeleeCancelTests.Fresh(c, 0);
        var stab = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Stab), c, out _);
        Assert.Equal(Row("melee_stab"), CancelGate.ActiveRow(c, stab, default));
        var f = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.MeleeHeavy), c, out _);
        Assert.Equal(Row("melee_heavy"), CancelGate.ActiveRow(c, f, default));
    }

    [Fact]
    public void A_light_press_ends_the_stab_when_its_row_says_yes_and_an_F_strike_is_untouched()
    {
        var edited = Edit("melee_stab", "light", new(CancelState.Yes));
        PlayerState After(CancelData table, InputButtons start)
        {
            var c = Config(table);
            var s = Combat.StepPlayer(MeleeCancelTests.Fresh(c, 0), new PlayerInput(0, 0f, 0f, 0f, start), c, out _);
            for (int k = 0; k < 3; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
            return Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), c, out _);
        }
        var stabDefault = After(CancelData.Baseline, InputButtons.Stab);
        Assert.Equal((StabAt, true), (stabDefault.MeleeStep, stabDefault.MeleeHeavy));      // default: the press waits (queued)
        var stabEdited = After(edited, InputButtons.Stab);
        Assert.Equal((1, false), (stabEdited.MeleeStep, stabEdited.MeleeHeavy));            // edited: the stab ends, light attack 1 starts at once
        var heavyDefault = After(CancelData.Baseline, InputButtons.MeleeHeavy);
        var heavyEdited = After(edited, InputButtons.MeleeHeavy);
        Assert.Equal((heavyDefault.MeleeStep, heavyDefault.MeleeHeavy, heavyDefault.MeleeTime), (heavyEdited.MeleeStep, heavyEdited.MeleeHeavy, heavyEdited.MeleeTime));   // generic heavy melee unchanged
        Assert.Equal((1, true), (heavyEdited.MeleeStep, heavyEdited.MeleeHeavy));
    }

    [Fact]
    public void The_stab_key_ends_an_F_strike_when_the_stab_column_says_yes_and_by_default_it_is_refused()
    {
        var edited = Edit("melee_heavy", "stab", new(CancelState.Yes));
        PlayerState After(CancelData table, InputButtons start, int wait)
        {
            var c = Config(table);
            var s = Combat.StepPlayer(MeleeCancelTests.Fresh(c, 0), new PlayerInput(0, 0f, 0f, 0f, start), c, out _);
            for (int k = 0; k < wait; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
            return Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Stab), c, out _);
        }
        Assert.Equal((1, true), (After(CancelData.Baseline, InputButtons.MeleeHeavy, 3).MeleeStep, true));
        var e = After(edited, InputButtons.MeleeHeavy, 3);
        Assert.Equal((StabAt, true), (e.MeleeStep, e.MeleeHeavy));
        // the stomp (in its own row) and a stomp edit are untouched by the stab column: F, F, then T in the stomp
        var c0 = Config(edited);
        var s0 = Combat.StepPlayer(MeleeCancelTests.Fresh(c0, 0), new PlayerInput(0, 0f, 0f, 0f, InputButtons.MeleeHeavy), c0, out _);
        for (int k = 0; k < 19; k++) s0 = Combat.StepPlayer(s0, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c0, out _);
        s0 = Combat.StepPlayer(s0, new PlayerInput(0, 0f, 0f, 0f, InputButtons.MeleeHeavy), c0, out _);
        for (int k = 0; k < 10 && s0.MeleeStep != 3; k++) s0 = Combat.StepPlayer(s0, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c0, out _);
        Assert.Equal(3, s0.MeleeStep);
        Assert.Equal(Row("melee_heavy"), CancelGate.ActiveRow(c0, s0, default));   // the sword's finisher stomp is a heavy strike (not in place): the generic heavy row, whose stab cell is edited here
    }

    // ---- networked: the server plays what the client predicts ----

    [Fact]
    public void Over_the_network_the_stab_key_stabs_F_F_stomps_and_the_prediction_needs_no_correction()
    {
        var config = Config() with { Classes = [Sword("sm - assault marine", Team.SpaceMarines), Sword("csm - raptor", Team.Chaos)] };
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(30), TimeSpan.Zero, 0), seed: 11, 0, config);
        var client = h.AddClient(config with { Cancel = CancelLive.Of(CancelData.Baseline) }, nonce: 77);
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.True(client.PlayerId > 0);
        uint start = 0;
        PlayerInput Input(int who, uint tick)
        {
            if (start == 0) start = tick + 5;
            long k = tick - (long)start;
            var b = k == 0 ? InputButtons.Stab : k == 90 || k == 110 ? InputButtons.MeleeHeavy : InputButtons.None;   // the stab, then F, F
            return new(tick, 0f, 0f, 0f, b);
        }
        var server = new List<(int, bool)>();
        var predicted = new List<(int, bool)>();
        int before = client.CorrectionErrors.Count;
        for (int i = 0; i < 7000; i++)
        {
            h.Step(Input);
            if (h.Server.World.Players.TryGetValue(client.PlayerId, out var s)) server.Add((s.MeleeStep, s.MeleeHeavy));
            predicted.Add((client.Predicted.MeleeStep, client.Predicted.MeleeHeavy));
        }
        Assert.Contains((StabAt, true), server);      // the key's stab
        Assert.Contains((3, true), server);           // F, F: the stomp
        Assert.Contains((StabAt, true), predicted);
        Assert.Contains((3, true), predicted);
        Assert.Equal(server.Count(x => x == (StabAt, true)) > 0, predicted.Count(x => x == (StabAt, true)) > 0);
        var errors = client.CorrectionErrors.Skip(before).ToList();
        Assert.True(errors.Count == 0 || errors.Max() < 0.01f, $"corrections {errors.DefaultIfEmpty().Max()}");
    }

    // ---- an export from before the section still imports ----

    [Fact]
    public void An_export_without_the_stab_section_imports_with_the_code_defaults_there()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(CancelData.Shipped.With(CancelScope.Any, Row("jp_hop"), Col("light"), new(CancelState.No)).ToJson("old"))!.AsObject();
        var rows = node["rows"]!.AsArray(); rows.RemoveAt(rows.Count - 1);
        var cols = node["cols"]!.AsArray(); cols.RemoveAt(cols.Count - 1);
        var cells = node["cells"]!.AsObject();
        foreach (var key in cells.Select(kv => kv.Key).Where(k => k.StartsWith("melee_stab|", StringComparison.Ordinal) || k.EndsWith("|stab", StringComparison.Ordinal)).ToList()) cells.Remove(key);
        var back = CancelData.Parse(node.ToJsonString(), out string error);
        Assert.True(back is not null, error);
        Assert.Equal(CancelState.No, back!.Base(Row("jp_hop"), Col("light")).State);
        Assert.Equal(CancelData.Shipped.Base(Row("melee_stab"), Col("light")), back.Base(Row("melee_stab"), Col("light")));
        Assert.Equal(CancelData.Shipped.Base(Row("hip_fire"), Col("stab")), back.Base(Row("hip_fire"), Col("stab")));
        Assert.Single(back.DiffFromShipped());
    }
}
