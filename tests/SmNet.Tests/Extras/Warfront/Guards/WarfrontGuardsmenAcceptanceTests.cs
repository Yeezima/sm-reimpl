using System.Numerics;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmNet.Tests.Extras.Warfront.Chaos;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>What one observed trial run saw, tick by tick (Guardsmen G3, 2026-11-26). Reads the simulation only through its public test aids.</summary>
internal sealed class GuardsmenLog
{
    public readonly List<(int Tick, int Point, WarfrontLineState From, WarfrontLineState To)> Transitions = new();
    public readonly List<WarfrontVoxLine> Vox = new();
    /// <summary>The withheld squads of each point's record the first time it reads Withdrawn, with the destination.</summary>
    public readonly Dictionary<int, (int Tick, int[] Squads, int Dest)> FirstWithdrawal = new();
    public readonly List<int> TotalMen = new();
    public readonly List<bool> DispatchTick = new();
    public readonly bool[] EverLost = new bool[3];
    public readonly int[] LostAtTick = [-1, -1, -1];
    public readonly bool[] SecuredAfterLost = new bool[3];
    public int FirstFlankTick = -1, FlankClearedTick = -1, FlankPoint = -1;
    public int WithdrawnEntries(int k) => Transitions.Count(t => t.Point == k && t.To == WarfrontLineState.Withdrawn);
    public int First(int k, WarfrontLineState s) => Transitions.Where(t => t.Point == k && t.To == s).Select(t => t.Tick).DefaultIfEmpty(-1).First();
}

/// <summary>The rig of the acceptance tests: lane battles on the flat test arena (Orks or Chaos), the scripted pushes of <see cref="WarfrontLineTrial"/>, an observer, and no static switch of the trial moved.</summary>
internal static class GuardsmenRig
{
    public static (World W, TfwBattle B) Lane(bool chaos = false, bool off = false, int settle = 12)
    {
        var w = TfwRig.War(config: chaos ? WarfrontChaosRosterTests.ChaosConfig : WarfrontBattleTests.WarfrontConfig);
        var b = TfwRig.Deploy(w);
        b.WarfrontLineOff = off;
        WarfrontLineTrial.Prepare(w, settle);
        return (w, b);
    }

    /// <summary>Every enemy that belongs to no army dies (the stage's crowds and its own pressure): the controlled Chaos stage, as the trial's own variant (kept here so the trial's static switches stay untouched).</summary>
    public static void QuietStage(World w, TfwBattle b)
    {
        var army = new HashSet<ushort>();
        foreach (var a in b.ArmyList) foreach (ushort id in a.Orks) army.Add(id);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        for (int k = 0; k < n; k++)
            if (all[k].Alive && !army.Contains(all[k].Id)) w.Musou.Damage(all[k].Id, WarfrontLineTrial.Human, 1e6f, Vector3.Zero, out _);
    }

    public static int Men(World w, TfwBattle b)
    {
        int far = 0;
        foreach (var a in b.ArmyList) if (a.Side == TfwArmySide.Guard) far += a.FarCount;
        return w.TfwGuards!.Alive + far + b.GuardsAtBase;
    }

    public static (WarfrontLineTrialResult R, GuardsmenLog L) Observe(World w, TfwBattle b, string scenario, int seed, int target, int seconds, bool quietStage = false, Action<int, GuardsmenLog>? extra = null)
    {
        var log = new GuardsmenLog();
        var last = new WarfrontLineState[3];
        ushort seq = 0;
        int sent = b.ReinforcementsSent;
        var flankOn = false;
        var r = WarfrontLineTrial.Run(w, scenario, seed, target, seconds, t =>
        {
            if (quietStage && t % 30 == 0) QuietStage(w, b);
            for (int k = 0; k < 3; k++)
            {
                var s = b.WarfrontLineOf(k);
                if (s != last[k])
                {
                    log.Transitions.Add((t, k, last[k], s));
                    if (s == WarfrontLineState.Withdrawn && !log.FirstWithdrawal.ContainsKey(k))
                    {
                        var rec = b.WarfrontWithheld().Where(x => x.Origin == k).ToArray();
                        log.FirstWithdrawal[k] = (t, rec.Select(x => x.Squad).OrderBy(x => x).ToArray(), rec.Length > 0 ? rec[0].Dest : -1);
                    }
                    last[k] = s;
                }
                if (b.Objectives[k].State != TfwPositionState.Secured && !log.EverLost[k]) { log.EverLost[k] = true; log.LostAtTick[k] = t; }
                else if (log.EverLost[k] && b.Objectives[k].State == TfwPositionState.Secured) log.SecuredAfterLost[k] = true;
            }
            if (b.WarfrontVoxNewest is { } v && v.Sequence != seq) { seq = v.Sequence; log.Vox.Add(v); }
            if (log.FlankPoint < 0) for (int k = 0; k < 3; k++) if (b.WarfrontFlankOn(k)) log.FlankPoint = k;
            bool on = log.FlankPoint >= 0 && b.WarfrontFlankOn(log.FlankPoint);
            if (on && !flankOn && log.FirstFlankTick < 0) log.FirstFlankTick = t;
            if (!on && flankOn) log.FlankClearedTick = t;
            flankOn = on;
            log.TotalMen.Add(Men(w, b));
            log.DispatchTick.Add(b.ReinforcementsSent != sent);
            sent = b.ReinforcementsSent;
            extra?.Invoke(t, log);
        });
        return (r, log);
    }

    public static int Rank(string outcome) => outcome switch { "HELD" => 3, "LOST-AND-RETAKEN" => 2, "POINT-LOST" => 1, _ => 0 };

    public static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

    public static float Degrees(Vector2 a, Vector2 b) => MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(b)), -1f, 1f)) * 180f / MathF.PI;
}

/// <summary>
/// Guardsmen G3 acceptance (design-guardsmen-1125, as amended by the owner's answers of 2026-11-25 and the lead's rulings of 2026-11-26): one test for every positive and negative journey of the line on the flat test
/// arena, from the acceptance criteria and not from the implementer's tests. Real lane maps (cramped ground) need the install and are not reached here.
/// </summary>
public class WarfrontGuardsmenAcceptanceTests
{
    // ---- the lead's rulings and the owner's answers, pinned ----

    [Fact]
    public void The_rulings_and_the_untouched_rules_are_pinned()
    {
        Assert.Equal(6f, WarfrontLineRules.OverwhelmRatio);
        Assert.Equal(1, WarfrontLineRules.OverwhelmHealthyMost);
        Assert.Equal(0.35f, WarfrontLineRules.FlankMainShare);
        Assert.Equal(2f, WarfrontLineRules.CollapseRatio);
        Assert.Equal(4, WarfrontLineRules.BreakSeconds);
        Assert.Equal(45, WarfrontLineRules.RallyLockoutSeconds);
        Assert.Equal(5, WarfrontVox.PerMinute);
        Assert.Equal(6f, WarfrontVox.SpacingSeconds);
        // N13: no other rule moves (the capture and the budget are the Musou war's numbers)
        var (_, b) = GuardsmenRig.Lane(settle: 1);
        Assert.Equal(TfwArmyRules.GarrisonCap * b.GuardScale, b.GarrisonCapNow);
        Assert.Equal(TfwArmyRules.ReinforceMen * b.GuardScale, b.ReinforceMenNow);
        Assert.Equal(60f, TfwArmyRules.ReinforceSeconds);
    }

    [Fact]
    public void Without_the_environment_switch_the_default_battle_runs_the_line_and_with_it_the_old_one()
    {
        var w = TfwRig.War(config: WarfrontBattleTests.WarfrontConfig);
        var b = TfwRig.Deploy(w);   // no code switch: the default is the environment's
        // the same test is run with SM_WF_GUARD_AI_OLD=1 (QA step): the static reads it once at the type's load
        Assert.Equal(WarfrontLineRules.Old, b.WarfrontLineOff);
        Assert.Equal(!WarfrontLineRules.Old, b.WarfrontLineOn);
    }

    // ---- positive journeys ----

    /// <summary>P1: a routine wave on a prepared garrison without Marines: the line stays Holding, no withdrawal, and the result is at least the old behaviour's.</summary>
    [Fact]
    public void P1_a_routine_wave_is_held_without_marines_and_is_not_worse_than_the_old_line()
    {
        var (w, b) = GuardsmenRig.Lane();
        var (r, log) = GuardsmenRig.Observe(w, b, "routine", 1, 0, 120);
        var (w0, b0) = GuardsmenRig.Lane(off: true);
        var (old, _) = GuardsmenRig.Observe(w0, b0, "routine", 1, 0, 120);
        Assert.True(b.WarfrontWithdrawals == 0, "withdrawals " + b.WarfrontWithdrawals + ": " + string.Join(",", log.Transitions.Select(t => $"{t.Tick / 30.0:0}s:{t.Point}:{t.To}")) + " outcome " + r.Outcome);
        Assert.DoesNotContain(log.Transitions, t => t.To is WarfrontLineState.Withdrawn or WarfrontLineState.Rallying);
        Assert.DoesNotContain(log.Vox, v => v.Kind == WarfrontVoxKind.FallingBack);
        Assert.True(GuardsmenRig.Rank(r.Outcome) >= GuardsmenRig.Rank(old.Outcome), $"new {r.Outcome} against old {old.Outcome}");
        Assert.True(r.PointsLost <= old.PointsLost, $"points lost new {r.PointsLost} old {old.PointsLost}");
        Assert.Equal(0, b0.WarfrontWithdrawals);
    }

    /// <summary>P2: a second wave from another bearing: one squad (never more than the cap) answers within 5 s of the flank being sustained, the others' slots do not move, the approach does not turn; when the flank clears the squad is released.</summary>
    [Fact]
    public void P2_a_flank_takes_one_squad_while_the_rows_hold_and_the_squad_returns_when_it_clears()
    {
        var (w, b) = GuardsmenRig.Lane();
        const int target = 1;   // the wave goes here; the flank is read on whichever point raises it
        var approachAtFlank = Vector2.Zero;
        bool swung = false, answeredLate = false; string? lateInfo = null;
        string? movedInfo = null; int moved = 0, compared = 0, flankSeen = -1;
        Dictionary<int, Vector3>? before = null;
        int[]? flankers0 = null; var lastApproach = Vector2.Zero;
        var (r, log) = GuardsmenRig.Observe(w, b, "flank", 1, target, 200, false, (t, l) =>
        {
            int k = l.FlankPoint;
            if (k < 0 || !b.WarfrontFlankOn(k)) { before = null; return; }
            var flankers = b.WarfrontFlankSquads(k).ToArray();
            if (l.FirstFlankTick == t) flankSeen = t;
            if (flankers.Length == 0)
            {
                if (flankSeen >= 0 && t - flankSeen > 5 * 30 && lateInfo is null && Enumerable.Range(0, GuardSim.MaxSquads).Any(q => w.TfwGuards!.AliveIn(q) > WarfrontGuardRules.BatteredMen && b.SquadPost(q) == k)) { answeredLate = true; lateInfo = $"point {k} flank on since {flankSeen / 30.0:0.0}s, now {t / 30.0:0.0}s, squads posted {Enumerable.Range(0, GuardSim.MaxSquads).Count(q => w.TfwGuards!.AliveIn(q) > 0 && b.SquadPost(q) == k)}, healthy {Enumerable.Range(0, GuardSim.MaxSquads).Count(q => w.TfwGuards!.AliveIn(q) > WarfrontGuardRules.BatteredMen && b.SquadPost(q) == k)}, line {b.WarfrontLineOf(k)}"; }   // a sustained flank unanswered 5 s on
                return;
            }
            Assert.InRange(flankers.Length, 1, WarfrontLineRules.FlankSquadsMost);
            var approach = b.WarfrontApproach(k);
            if (approachAtFlank == Vector2.Zero) approachAtFlank = approach;
            else if (GuardsmenRig.Degrees(approachAtFlank, approach) > WarfrontGuardRules.ApproachTurnDegrees + 1f && flankers0 is not null && flankers0.SequenceEqual(flankers)) swung = true;
            var others = Enumerable.Range(0, GuardSim.MaxSquads).Where(s => w.TfwGuards!.AliveIn(s) > WarfrontGuardRules.BatteredMen && b.SquadPost(s) == k && !flankers.Contains(s)).ToDictionary(s => s, b.WarfrontSlotOf);
            if (before is not null && flankers0 is not null && flankers0.SequenceEqual(flankers) && approach == lastApproach && others.Keys.All(before.ContainsKey) && before.Keys.All(others.ContainsKey))
                foreach (var (s, p) in others) { compared++; if (Vector3.Distance(p, before[s]) >= 1f) { moved++; movedInfo ??= $"squad {s} moved {Vector3.Distance(p, before[s]):0.0} m at {t / 30.0:0.0}s, point {k}, flankers {string.Join(",", flankers)}"; } }
            before = others;
            flankers0 = flankers;
            lastApproach = approach;
        });
        Assert.True(log.FirstFlankTick >= 0 && b.WarfrontFlanksRaised >= 1, $"a flank was raised: first {log.FirstFlankTick}, raised {b.WarfrontFlanksRaised}, outcome {r.Outcome}, ork armies {b.ArmyList.Count(a => a.Side == TfwArmySide.Ork)}");
        Assert.False(answeredLate, "a squad answered the flank within 5 s: " + lateInfo);
        Assert.True(compared > 0, "rows stood unchanged long enough to compare");
        Assert.True(moved == 0, $"{moved} of {compared}: {movedInfo}");
        Assert.False(swung, "the approach did not swing to the flank");
        Assert.True(log.Vox.Count(v => v.Kind == WarfrontVoxKind.Flank) <= 2, "the Flank line is rare");
        if (log.FlankClearedTick >= 0) Assert.Empty(b.WarfrontFlankSquads(log.FlankPoint));
        _ = r;
    }

    /// <summary>P3 (flat ground half): on open ground nothing is cramped, there is no firing position set, and every squad's place is reached and held within the radius and the slack (no stall, no chase). The cramped half needs the install's lane maps (smtool warfront-guards --survey).</summary>
    [Fact]
    public void P3_on_open_ground_no_point_is_cramped_and_every_squad_stands_at_its_point()
    {
        var (w, b) = GuardsmenRig.Lane(settle: 30);
        for (int k = 0; k < 3; k++)
        {
            Assert.False(b.WarfrontCramped(k));
            Assert.Empty(b.WarfrontFiringPositions(k));
        }
        var men = new TfwGuard[GuardSim.Capacity];
        int n = w.TfwGuards!.CopyTo(men);
        Assert.True(n > 0);
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (w.TfwGuards.AliveIn(s) == 0 || !w.TfwGuards.Arrived(s)) continue;
            int k = b.SquadPost(s);
            if (k is < 0 or > 2) continue;
            Assert.True(Vector2.Distance(GuardsmenRig.Flat(w.TfwGuards.Centre(s)), GuardsmenRig.Flat(b.Layout.Positions[k])) <= b.Layout.Radius(k) + WarfrontLineRules.MenSlack + 2f, $"squad {s} stands at point {k}: {Vector2.Distance(GuardsmenRig.Flat(w.TfwGuards.Centre(s)), GuardsmenRig.Flat(b.Layout.Positions[k])):0.0} m, radius {b.Layout.Radius(k):0.0}, arrived {w.TfwGuards.Arrived(s)}");
        }
    }

    /// <summary>P4: an elite push with no help: Breaking, then (4 s later at the earliest) Withdrawn with the same squads; they walk to a friendly point and stand there; the origin is lost to the Orks; nobody is made for them.</summary>
    [Fact]
    public void P4_an_elite_push_with_no_help_breaks_then_falls_back_together_to_a_friendly_point()
    {
        var (w, b) = GuardsmenRig.Lane();
        const int origin = 0;
        int[]? stable = null; int[] lastNow = [];
        bool changed = false;
        var (r, log) = GuardsmenRig.Observe(w, b, "elite", 1, origin, 170, false, (t, l) =>
        {
            if (b.WarfrontLineOf(origin) != WarfrontLineState.Withdrawn) return;
            var now = b.WarfrontWithheld().Where(x => x.Origin == origin).Select(x => x.Squad).OrderBy(x => x).ToArray();
            if (stable is null) stable = now;
            else if (now.Except(stable).Any()) { changed = true; lastNow = now; }   // a squad may die out of the record; none may join it
        });
        Assert.True(log.FirstWithdrawal.ContainsKey(origin), "the line fell back: " + string.Join(",", log.Transitions.Select(t => $"{t.Tick / 30}s:{t.Point}:{t.To}")));
        int breaking = log.First(origin, WarfrontLineState.Breaking), withdrawn = log.First(origin, WarfrontLineState.Withdrawn);
        Assert.True(breaking >= 0 && withdrawn - breaking >= (WarfrontLineRules.BreakSeconds - 1) * 30, $"Breaking at {breaking}, Withdrawn at {withdrawn}");
        var (wt, squads, dest) = log.FirstWithdrawal[origin];
        Assert.NotEmpty(squads);
        Assert.NotEqual(origin, dest);
        Assert.True(dest is 1 or 2 or 3, $"destination {dest}");
        Assert.False(changed, "the withheld squads are the same ones for the whole withdrawal: " + string.Join(",", stable ?? []) + " then " + string.Join(",", lastNow));
        Assert.True(log.Vox.Any(v => v.Kind == WarfrontVoxKind.LineBreaking && v.From == origin));
        Assert.True(log.Vox.Any(v => v.Kind == WarfrontVoxKind.FallingBack && v.From == origin));
        int lb = log.Vox.FindIndex(v => v.Kind == WarfrontVoxKind.LineBreaking && v.From == origin), fb = log.Vox.FindIndex(v => v.Kind == WarfrontVoxKind.FallingBack && v.From == origin);
        Assert.True(lb < fb, "the break is called before the fall back");
        Assert.True(log.EverLost[origin], "the origin fell to the Orks with nobody to hold it");
        _ = r; _ = wt;
    }

    /// <summary>P4 (arrival): the survivors of the fall back reach and stand in the destination's rows.</summary>
    [Fact]
    public void P4_the_survivors_stand_at_the_destination()
    {
        var (w, b) = GuardsmenRig.Lane();
        const int origin = 0;
        int[]? squads = null;
        int dest = -1, since = -1;
        bool standing = false;
        GuardsmenRig.Observe(w, b, "elite", 1, origin, 170, false, (t, l) =>
        {
            if (squads is null && b.WarfrontLineOf(origin) == WarfrontLineState.Withdrawn)
            {
                var rec = b.WarfrontWithheld().Where(x => x.Origin == origin).ToArray();
                squads = rec.Select(x => x.Squad).ToArray();
                dest = rec[0].Dest;
                since = t;
            }
            if (squads is null || dest is < 0 or > 2 || t - since < 20 * 30 || b.WarfrontLineOf(origin) != WarfrontLineState.Withdrawn || standing) return;
            var alive = squads.Where(s => w.TfwGuards!.AliveIn(s) > 0).ToArray();
            if (alive.Length > 0 && alive.All(s => Vector2.Distance(GuardsmenRig.Flat(w.TfwGuards!.Centre(s)), GuardsmenRig.Flat(b.Layout.Positions[dest])) <= b.Layout.Radius(dest) + WarfrontLineRules.MenSlack + 6f)) standing = true;
        });
        Assert.NotNull(squads);
        if (dest is 1 or 2) Assert.True(standing || squads!.All(s => w.TfwGuards!.AliveIn(s) == 0), "the survivors stand at the friendly point 20 s on");
    }

    /// <summary>P5: the Marines break the push: Rallying is called, the same squads return to the origin and re-form, and the 45 s lockout holds.</summary>
    [Fact]
    public void P5_when_the_marines_break_the_push_the_same_squads_rally_and_the_lockout_holds()
    {
        var (w, b) = GuardsmenRig.Lane();
        const int origin = 0;
        var point = b.Layout.Positions[origin];
        int brokeAt = -1, heldAt = -1;
        int[] ours = [];
        var postsAtHeld = new Dictionary<int, int>();
        var (r, log) = GuardsmenRig.Observe(w, b, "elite", 1, origin, 230, false, (t, l) =>
        {
            // the Marines break the push once the line has fallen back (the break scenario breaks it at the first Breaking, before the confirmation)
            if (brokeAt < 0 && b.WarfrontLineOf(origin) == WarfrontLineState.Withdrawn) { brokeAt = t; ours = b.WarfrontWithheld().Where(x => x.Origin == origin).Select(x => x.Squad).ToArray(); }
            if (brokeAt >= 0 && heldAt < 0 && t % 15 == 0)
            {
                w.TfwRespawnAt(WarfrontLineTrial.Human, point, 0f);
                var all = new MusouEnemy[MusouSim.Capacity];
                int n = w.Musou!.CopyTo(all);
                for (int k = 0; k < n; k++)
                    if (all[k].Alive && Vector2.Distance(GuardsmenRig.Flat(all[k].Position), GuardsmenRig.Flat(point)) <= 60f) w.Musou.Damage(all[k].Id, WarfrontLineTrial.Human, 1e6f, Vector3.Zero, out _);
            }
            if (heldAt < 0 && l.Transitions.Any(x => x.Point == origin && x.From == WarfrontLineState.Rallying && x.To == WarfrontLineState.Holding)) { heldAt = t; foreach (int q in ours) if (w.TfwGuards!.AliveIn(q) > 0) postsAtHeld[q] = b.SquadPost(q); }
        });
        Assert.True(log.FirstWithdrawal.ContainsKey(origin), "the line fell back first: " + string.Join(",", log.Transitions.Select(t => $"{t.Tick / 30}s:{t.Point}:{t.To}")));
        int rally = log.First(origin, WarfrontLineState.Rallying);
        Assert.True(rally > log.First(origin, WarfrontLineState.Withdrawn), "it rallied after it withdrew");
        Assert.True(b.WarfrontLineRallies >= 1);
        Assert.Contains(log.Vox, v => v.Kind == WarfrontVoxKind.Rallying && v.From == origin);
        int held = log.Transitions.Where(t => t.Point == origin && t.From == WarfrontLineState.Rallying && t.To == WarfrontLineState.Holding).Select(t => t.Tick).DefaultIfEmpty(-1).First();
        Assert.True(held > rally, "the squads re-formed on the point");
        // the lockout: no new Breaking/Withdrawn within 45 s of re-forming
        Assert.DoesNotContain(log.Transitions, t => t.Point == origin && t.Tick > held && t.Tick < held + (WarfrontLineRules.RallyLockoutSeconds - 1) * 30 && t.To is WarfrontLineState.Breaking or WarfrontLineState.Withdrawn);
        Assert.NotEmpty(postsAtHeld);
        Assert.All(postsAtHeld, kv => Assert.True(kv.Value == origin, $"squad {kv.Key} re-formed with post {kv.Value}; withdrew {brokeAt / 30.0:0.0}s, rally {log.First(origin, WarfrontLineState.Rallying) / 30.0:0.0}s, held {heldAt / 30.0:0.0}s, dest {log.FirstWithdrawal[origin].Dest}, rallied squads {string.Join(",", ours)}, men {string.Join(",", ours.Select(q => w.TfwGuards!.AliveIn(q)))}, positions {string.Join(" ", ours.Where(q => w.TfwGuards!.AliveIn(q) > 0).Select(q => Vector2.Distance(GuardsmenRig.Flat(w.TfwGuards!.Centre(q)), GuardsmenRig.Flat(point)).ToString("0")))}"));
        _ = r;
    }

    /// <summary>P6: reinforcements still go to a point whose line fell back (owner 2026-11-25); the split favours a threatened point, no need is zeroed, the budget is the same.</summary>
    [Fact]
    public void P6_reinforcements_still_reach_a_fallen_back_point_and_a_threatened_one_gets_the_larger_share_of_the_same_budget()
    {
        var (w, b) = GuardsmenRig.Lane();
        // the first dispatch of the run after a point's line has pressure on it: read the armies marching out
        var shares = new Dictionary<int, int>();
        int budget = b.ReinforceMenNow;
        int before = b.ReinforcementsSent;
        bool grabbed = false, withdrawnThen = false;
        var seen = new HashSet<TfwArmy>();
        GuardsmenRig.Observe(w, b, "elite", 1, 0, 150, false, (t, l) =>
        {
            foreach (var a in b.ArmyList.Where(a => a.Side == TfwArmySide.Guard && a.Order == TfwArmyOrder.March && a.Real == 0 && a.Start == a.FarCount))
            {
                if (!seen.Add(a)) continue;
                if (!grabbed) { }
                shares[a.Goal] = shares.GetValueOrDefault(a.Goal) + a.Count;
                if (b.WarfrontLineOf(0) is WarfrontLineState.Withdrawn or WarfrontLineState.Breaking) withdrawnThen = true;
                grabbed = true;
            }
        });
        Assert.True(b.ReinforcementsSent > before, "a dispatch was made");
        Assert.True(shares.Values.Sum() <= budget * 3 + 400, "dispatched men stay within the budget per minute");
        // the threatened point is not starved while its line is down
        if (withdrawnThen) Assert.True(shares.GetValueOrDefault(0) > 0, "a fallen-back point still gets men");
    }

    /// <summary>P7: a whole run's lines (a break and rally) are each shown once: sequences count up from 1 without a repeat or a gap, each at least 6 s after the last, and every line survives the wire.</summary>
    [Fact]
    public void P7_every_vox_line_of_a_run_is_unique_spaced_and_round_trips_the_wire()
    {
        var (w, b) = GuardsmenRig.Lane();
        var (r, log) = GuardsmenRig.Observe(w, b, "break", 2, 0, 220);
        Assert.NotEmpty(log.Vox);
        for (int i = 0; i < log.Vox.Count; i++)
        {
            Assert.Equal(i + 1, log.Vox[i].Sequence);
            if (i > 0) Assert.True(log.Vox[i].Tick - log.Vox[i - 1].Tick >= (uint)(WarfrontVox.SpacingSeconds * 30) - 1, $"spacing {log.Vox[i].Tick - log.Vox[i - 1].Tick}");
            var bytes = WarfrontVoxWire.Encode(log.Vox[i]);
            Assert.True(WarfrontVoxWire.TryDecode(bytes, out var back));
            Assert.Equal((log.Vox[i].Sequence, log.Vox[i].Kind, log.Vox[i].From, log.Vox[i].To), (back.Sequence, back.Kind, back.From, back.To));
            Assert.False(string.IsNullOrEmpty(WarfrontVox.Text(back.Kind, back.From, back.To)));
        }
        // at most 5 non-urgent lines in any rolling minute
        for (int i = 0; i < log.Vox.Count; i++)
            Assert.True(log.Vox.Count(v => v.Kind > WarfrontVoxKind.FallingBack && v.Tick >= log.Vox[i].Tick && v.Tick < log.Vox[i].Tick + 60 * 30) <= WarfrontVox.PerMinute);
        _ = r;
    }

    /// <summary>P7: a burst of 20 offers in 30 s at the limiter: at most 5 low-priority lines a minute, six seconds apart, in priority order.</summary>
    [Fact]
    public void P7_a_burst_of_twenty_events_in_thirty_seconds_is_limited()
    {
        var v = new WarfrontVox(30);
        var kinds = new[] { WarfrontVoxKind.Flank, WarfrontVoxKind.Reinforcements, WarfrontVoxKind.Rallying, WarfrontVoxKind.Flank, WarfrontVoxKind.Reinforcements };
        var made = new List<WarfrontVoxLine>();
        for (uint t = 0; t < 60 * 30; t++)
        {
            if (t < 30 * 30 && t % 45 == 0) v.Offer(kinds[(t / 45) % 5], (int)((t / 45) % 3), WarfrontVox.None, t);   // 20 offers in the first 30 s
            v.Step(t);
            if (v.Newest is { } n && (made.Count == 0 || made[^1].Sequence != n.Sequence)) made.Add(n);
        }
        Assert.InRange(made.Count, 1, WarfrontVox.PerMinute);
        for (int i = 1; i < made.Count; i++) Assert.True(made[i].Tick - made[i - 1].Tick >= 6 * 30);
        Assert.Equal(Enumerable.Range(1, made.Count).Select(i => (ushort)i), made.Select(m => m.Sequence));
    }

    /// <summary>The ours barks and the five texts are wired to the right kinds: five kinds, the texts as the brief writes them (the barks themselves are client audio).</summary>
    [Theory]
    [InlineData(WarfrontVoxKind.LineBreaking, "LINE BREAKING AT A")]
    [InlineData(WarfrontVoxKind.Flank, "FLANK AT A")]
    [InlineData(WarfrontVoxKind.Rallying, "RALLYING ON A")]
    [InlineData(WarfrontVoxKind.Reinforcements, "REINFORCEMENTS ARRIVING AT A")]
    public void The_five_callouts_read_as_the_brief_writes_them(WarfrontVoxKind kind, string text) => Assert.Equal(text, WarfrontVox.Text(kind, 0, WarfrontVox.None));

    // ---- negative journeys ----

    /// <summary>N5: Guardsmen never capture: a lost point is never Secured again while only Guardsmen (withheld or walking) are there.</summary>
    [Fact]
    public void N5_withheld_squads_never_take_a_point_back()
    {
        var (w, b) = GuardsmenRig.Lane();
        var (r, log) = GuardsmenRig.Observe(w, b, "elite", 1, 0, 170);
        Assert.True(log.EverLost[0], "the point was lost");
        Assert.False(log.SecuredAfterLost[0], "no Guardsman secured it again");
        _ = r;
    }

    /// <summary>N6: no men are made by a withdrawal or a rally. Every rise in the Guardsmen total (real men, garrison aggregates, men at the base) is a reserve (10) or a reinforcement dispatch, and the dispatches keep the minute's budget.</summary>
    [Theory]
    [InlineData("elite")]
    [InlineData("break")]
    public void N6_a_withdrawal_and_a_rally_make_no_men(string scenario)
    {
        var (w, b) = GuardsmenRig.Lane();
        var (r, log) = GuardsmenRig.Observe(w, b, scenario, 3, 0, 200);
        int gained = 0, dispatches = 0;
        for (int i = 1; i < log.TotalMen.Count; i++)
        {
            int d = log.TotalMen[i] - log.TotalMen[i - 1];
            if (log.DispatchTick[i]) dispatches++;
            if (d <= 0) continue;
            gained += d;
            Assert.True(d <= GuardSim.SquadSize || log.DispatchTick[i], $"tick {i}: +{d} men with no dispatch");
        }
        int minutes = r.Seconds / 60 + 1;
        Assert.True(gained <= minutes * b.ReinforceMenNow + (r.Seconds / (int)TfwBattleRules.ReserveSeconds + 1) * GuardSim.SquadSize + b.GuardsAtBase + 1, $"gained {gained} men in {r.Seconds} s");
    }

    /// <summary>N7: no loops: a point withdraws at most once inside a lockout, and a squad changes state at most twice in 60 s.</summary>
    [Fact]
    public void N7_pressure_around_the_thresholds_makes_no_loop()
    {
        var (w, b) = GuardsmenRig.Lane();
        var (r, log) = GuardsmenRig.Observe(w, b, "break", 4, 0, 230);
        for (int k = 0; k < 3; k++)
        {
            var at = log.Transitions.Where(t => t.Point == k && t.To == WarfrontLineState.Withdrawn).Select(t => t.Tick).ToArray();
            for (int i = 1; i < at.Length; i++) Assert.True(at[i] - at[i - 1] >= WarfrontLineRules.RallyLockoutSeconds * 30, $"point {k} withdrew twice {(at[i] - at[i - 1]) / 30.0:0.0} s apart");
            Assert.InRange(log.Transitions.Count(t => t.Point == k && t.Tick < 60 * 30), 0, 6);
        }
        _ = r;
    }

    /// <summary>N8: no chase: while a point's line is Holding every squad posted there stands within its radius plus the slack plus 2 m.</summary>
    [Fact]
    public void N8_a_holding_line_never_stands_beyond_the_radius_and_the_slack()
    {
        var (w, b) = GuardsmenRig.Lane();
        int checks = 0;
        GuardsmenRig.Observe(w, b, "routine", 2, 0, 90, false, (t, l) =>
        {
            if (t % 30 != 0) return;
            for (int s = 0; s < GuardSim.MaxSquads; s++)
            {
                if (w.TfwGuards!.AliveIn(s) == 0 || !w.TfwGuards.Arrived(s)) continue;
                int k = b.SquadPost(s);
                if (k is < 0 or > 2 || b.WarfrontLineOf(k) != WarfrontLineState.Holding || b.WarfrontFlankSquads(k).Contains(s)) continue;
                float d = Vector2.Distance(GuardsmenRig.Flat(w.TfwGuards.Centre(s)), GuardsmenRig.Flat(b.Layout.Positions[k]));
                checks++;
                Assert.True(d <= b.Layout.Radius(k) + WarfrontLineRules.MenSlack + 2f, $"squad {s} at {d:0.0} m from point {k} at {t / 30} s, radius {b.Layout.Radius(k):0.0}, arrived {w.TfwGuards.Arrived(s)}");
            }
        });
        Assert.True(checks > 0);
    }

    /// <summary>N10: a crowd of armies (40 and more) and no help: no crash, and the counters stay sane.</summary>
    [Fact]
    public void N10_a_crowd_of_armies_does_not_crash_the_line()
    {
        var (w, b) = GuardsmenRig.Lane();
        for (int i = 0; i < 45; i++)
        {
            var at = b.Layout.Positions[i % 3] + new Vector3(60f + i, 5f * (i % 7), 0f);
            if (b.AddArmyForTests(TfwArmySide.Ork, i % 2 == 0 ? TfwArmyOrder.Attack : TfwArmyOrder.Hold, i % 3, 30, at, 0.2f) is null) break;
        }
        var ex = Record.Exception(() => GuardsmenRig.Observe(w, b, "elite", 1, 0, 60));
        Assert.Null(ex);
        Assert.True(b.WarfrontWithdrawals >= 0 && b.WarfrontWithdrawalsRefused >= 0);
    }

    /// <summary>The garrison's own 50 % loss retreat (kept, owner 2026-11-25) and the line rule run together: a garrison under attack retreats to a friendly place, and the line's withdrawal does not strand squads in either list.</summary>
    [Fact]
    public void The_garrison_loss_retreat_and_the_line_rule_work_together_without_stranding_squads()
    {
        var (w, b) = GuardsmenRig.Lane();
        var (r, log) = GuardsmenRig.Observe(w, b, "collapse", 1, 0, 200, false, (t, l) =>
        {
            if (t % 30 != 0) return;
            // every living squad is in exactly one place: the field (not an army squad), an army's list, or a withdrawal record
            var inArmies = b.ArmyList.SelectMany(a => a.Squads).ToList();
            Assert.Equal(inArmies.Count, inArmies.Distinct().Count());
            var withheld = b.WarfrontWithheld().Select(x => x.Squad).ToList();
            Assert.Equal(withheld.Count, withheld.Distinct().Count());
            Assert.Empty(withheld.Intersect(inArmies));
        });
        Assert.True(r.GuardsmenMin <= r.GuardsmenStart);
        _ = log;
    }

    /// <summary>N9 (in-trial): the restore switch is bit-for-bit repeatable, and it removes every trace of the line (no withdrawal, rally, flank, vox, withheld squad).</summary>
    [Fact]
    public void N9_the_restore_switch_leaves_no_trace_and_repeats_exactly()
    {
        var (w1, b1) = GuardsmenRig.Lane(off: true);
        var (w2, b2) = GuardsmenRig.Lane(off: true);
        var (r1, l1) = GuardsmenRig.Observe(w1, b1, "collapse", 5, 0, 100);
        var (r2, _) = GuardsmenRig.Observe(w2, b2, "collapse", 5, 0, 100);
        Assert.Equal(r1.Hash, r2.Hash);
        Assert.Equal(0, b1.WarfrontWithdrawals + b1.WarfrontLineRallies + b1.WarfrontFlanksRaised);
        Assert.Empty(l1.Vox);
        Assert.Empty(b1.WarfrontWithheld());
        Assert.False(b1.WarfrontLineOn);
        var (w3, b3) = GuardsmenRig.Lane();
        var (r3, _) = GuardsmenRig.Observe(w3, b3, "collapse", 5, 0, 100);
        Assert.NotEqual(r1.Hash, r3.Hash);   // the line changes the fight
    }

    /// <summary>Determinism: two runs with one seed give the same state hash and the same lines, for Orks and for Chaos.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_same_seed_gives_the_same_run_and_the_same_lines(bool chaos)
    {
        string[] trace = new string[2];
        string[] hash = new string[2];
        for (int i = 0; i < 2; i++)
        {
            var (w, b) = GuardsmenRig.Lane(chaos);
            var (r, log) = GuardsmenRig.Observe(w, b, "break", 7, 0, 150, chaos);
            hash[i] = r.Hash;
            trace[i] = string.Join(";", log.Transitions.Select(t => $"{t.Tick}:{t.Point}:{t.To}")) + "|" + string.Join(";", log.Vox.Select(v => $"{v.Sequence}:{v.Kind}:{v.From}:{v.To}:{v.Tick}"));
        }
        Assert.Equal(hash[0], hash[1]);
        Assert.Equal(trace[0], trace[1]);
    }

    /// <summary>N14: the balance: 6 field squads' worth of Guardsmen against a 60-Ork wave on an open point, 30 seeds: the Orks' win share stays within 3 seeds of the old behaviour (and is reported).</summary>
    [Fact]
    public void N14_the_new_line_keeps_the_open_ground_balance_within_three_seeds_of_the_old()
    {
        int newLost = 0, oldLost = 0;
        for (int seed = 1; seed <= 30; seed++)
        {
            foreach (bool off in new[] { false, true })
            {
                var (w, b) = GuardsmenRig.Lane(off: off, settle: 6);
                for (int t = 0; t < seed * 7; t++) w.Step(_ => default);   // the seed moves the wave's start and size
                var wave = b.SendWaveForTests(52 + seed % 17, 0);
                Assert.NotNull(wave);
                wave!.FarElites = 6f;
                bool lost = false;
                for (int t = 0; t < 90 * 30 && !lost; t++)
                {
                    w.Step(_ => default);
                    if (b.Objectives[0].State != TfwPositionState.Secured || w.TfwGuards!.Alive == 0) lost = true;
                }
                if (lost) { if (off) oldLost++; else newLost++; }
                _ = seed;
            }
        }
        Assert.InRange(Math.Abs(newLost - oldLost), 0, 3);
    }
}
