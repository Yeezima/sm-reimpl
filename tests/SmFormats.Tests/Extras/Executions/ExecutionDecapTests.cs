using System.Numerics;
using SmContent.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;

namespace SmFormats.Tests.Extras.Executions;

/// <summary>
/// Executions: the head cut (2026-11-27; docs/executions-cues.md section 6). Synthetic cue lists (names and times invented here, no original bytes): which rows cut, the cut's time from the
/// row's own tag, the cut firing once on the shared clock at three frame rates, never after an interruption or a late first sight, the gore gate, the bodies that can and cannot be cut, the toss.
/// </summary>
public class ExecutionDecapTests
{
    static ExecutionCue Cue(string row, float at, string tag, ExecutionCueSide side = ExecutionCueSide.Attacker) =>
        new(row, ExecutionWield.Own, side, ExecutionCueKind.Sound, ExecutionCuePlace.Attacker, at, at, tag, "e", "f");

    static IReadOnlyList<ExecutionCue> C1Cues() =>
    [
        Cue("C1.chainsword", 0.7f, "ACTN_CSM_Sync_SwordHit"),
        Cue("C1.chainsword", 4.3f, "ACTN_FX_CS_Sync_Blood_Geyser", ExecutionCueSide.Victim),
        Cue("C1.chainsword", 4.3f, "ACTN_FX_CS_Sync_Blood_Geyser", ExecutionCueSide.Victim),
        Cue("C1.chainsword", 4.367f, "EVT_Right_Foot"),
    ];

    [Fact]
    public void Only_C1_and_O5_cut_a_head()
    {
        Assert.NotNull(ExecutionDecapRules.RowFor("C1.chainsword"));
        Assert.NotNull(ExecutionDecapRules.RowFor("c1.chainsword"));
        Assert.NotNull(ExecutionDecapRules.RowFor("O5.power_axe"));
        foreach (var id in new[] { "B1.chainsword", "O7.thunder_hammer", "O4.power_axe", "R1.chainsword", "C2.power_axe", "O8.chainsword" })
            Assert.Null(ExecutionDecapRules.RowFor(id));
        // Every catalogue row that cuts exists, and its victim kind matches the row's.
        foreach (var r in ExecutionDecapRules.Rows)
            Assert.Contains(ExecutionRows.All, row => row.Id.StartsWith(r.RowBase + ".", StringComparison.Ordinal) && string.Equals(row.Target, r.Target, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cut_takes_the_tag_time_and_falls_back()
    {
        var row = ExecutionDecapRules.RowFor("C1.chainsword")!;
        var moved = C1Cues().Select(c => c.Tag == row.CutTag ? c with { At = 4.1f, Until = 4.1f } : c).ToList();
        Assert.Equal(4.1f, ExecutionDecapRules.CutSeconds(row, moved));
        Assert.Equal(row.FallbackSeconds, ExecutionDecapRules.CutSeconds(row, [Cue("C1.chainsword", 1f, "OTHER")]));
        Assert.Equal(1.867f, ExecutionDecapRules.RowFor("O5.power_axe")!.FallbackSeconds);
    }

    [Fact]
    public void WithCut_adds_one_cue_in_time_order_and_leaves_other_rows_alone()
    {
        var list = ExecutionDecapRules.WithCut("C1.chainsword", ExecutionWield.CombatKnife, C1Cues());
        Assert.Equal(C1Cues().Count + 1, list.Count);
        var cut = Assert.Single(list, c => c.Kind == ExecutionCueKind.Decap);
        Assert.Equal(4.3f, cut.At);
        Assert.True(cut.Ours && cut.Blood);
        Assert.Equal(ExecutionWield.CombatKnife, cut.Wield);
        Assert.Equal(3, list.ToList().IndexOf(cut));   // after both geyser cues of the same instant, before the 4.367 s footstep
        for (int i = 1; i < list.Count; i++) Assert.True(list[i - 1].At <= list[i].At);
        var other = C1Cues().Select(c => c with { RowId = "C2.power_axe" }).ToList();
        Assert.Same(other, ExecutionDecapRules.WithCut("C2.power_axe", ExecutionWield.Own, other));
        // A cut later than every cue goes last.
        Assert.Equal(ExecutionCueKind.Decap, ExecutionDecapRules.WithCut("O5.power_axe", ExecutionWield.Own, [Cue("O5.power_axe", 0.5f, "X")])[^1].Kind);
    }

    static int Cuts(IEnumerable<ExecutionCueTimeline.Step> steps) => steps.Count(s => s.Kind == ExecutionCueTimeline.StepKind.Fire && s.Cue!.Kind == ExecutionCueKind.Decap);

    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(144f)]
    public void Cut_fires_once_at_its_clock_time(float fps)
    {
        var cues = ExecutionDecapRules.WithCut("O5.power_axe", ExecutionWield.Own, [Cue("O5.power_axe", 0.5f, "ACTN_Ork_Decap_AxBackHit"), Cue("O5.power_axe", 1.867f, "ACTN_OrkBoy_Sync_HeadDecapHit")]);
        var t = new ExecutionCueTimeline();
        var key = new ExecutionCueTimeline.RunKey(4, 50);
        var steps = new List<ExecutionCueTimeline.Step>();
        float firedAt = -1f;
        int total = 0;
        for (float clock = 0f; clock < 2.833f; clock += 1f / fps)
        {
            steps.Clear();
            t.Advance(key, cues, clock, steps);
            if (clock > 1.2f && clock < 1.3f) t.Advance(key, cues, clock - 0.1f, steps);   // a prediction step back fires nothing again
            int n = Cuts(steps);
            if (n > 0 && firedAt < 0f) firedAt = clock;
            total += n;
        }
        Assert.Equal(1, total);
        Assert.InRange(firedAt, 1.867f, 1.867f + 1f / fps + 1e-4f);
    }

    [Fact]
    public void An_interrupted_run_never_cuts()
    {
        var cues = ExecutionDecapRules.WithCut("C1.chainsword", ExecutionWield.Own, C1Cues());
        var t = new ExecutionCueTimeline();
        var key = new ExecutionCueTimeline.RunKey(2, 9);
        var steps = new List<ExecutionCueTimeline.Step>();
        for (float clock = 0f; clock < 4.0f; clock += 1f / 60f) t.Advance(key, cues, clock, steps);
        t.Interrupt(key, steps);   // the executor killed at 4.0 s, before the 4.3 s cut
        for (float clock = 4.0f; clock < 6.4f; clock += 1f / 60f) t.Advance(key, cues, clock, steps);
        Assert.Equal(0, Cuts(steps));
        Assert.True(t.IsClosed(key));
    }

    [Fact]
    public void A_run_first_seen_past_the_cut_skips_it()
    {
        var cues = ExecutionDecapRules.WithCut("C1.chainsword", ExecutionWield.Own, C1Cues());
        var steps = new List<ExecutionCueTimeline.Step>();
        var late = new ExecutionCueTimeline();
        late.Advance(new(1, 1), cues, 4.3f + ExecutionCueTimeline.LateSkipSeconds + 0.05f, steps);
        Assert.Equal(0, Cuts(steps));
        steps.Clear();
        var inTime = new ExecutionCueTimeline();
        inTime.Advance(new(1, 2), cues, 4.3f + ExecutionCueTimeline.LateSkipSeconds - 0.05f, steps);   // within the late window: it still cuts
        Assert.Equal(1, Cuts(steps));
    }

    [Fact]
    public void A_clock_jump_over_the_cut_still_cuts_once_the_run_is_seen()
    {
        var cues = ExecutionDecapRules.WithCut("C1.chainsword", ExecutionWield.Own, C1Cues());
        var t = new ExecutionCueTimeline();
        var key = new ExecutionCueTimeline.RunKey(5, 77);
        var steps = new List<ExecutionCueTimeline.Step>();
        t.Advance(key, cues, 4.0f, steps);   // seen at 4.0 s
        steps.Clear();
        t.Advance(key, cues, 4.9f, steps);   // a 0.9 s frame gap (about 1 fps) passes over the 4.3 s cut
        Assert.Equal(1, Cuts(steps));
        Assert.DoesNotContain(steps, s => s.Cue is { Kind: ExecutionCueKind.Sound, Tag: "ACTN_FX_CS_Sync_Blood_Geyser" });   // the geyser's sounds keep the jump skip
        steps.Clear();
        t.Advance(key, cues, 5.5f, steps);
        Assert.Equal(0, Cuts(steps));   // and only once
    }

    [Fact]
    public void Neck_wound_follows_the_weapon_and_head_bones_go_with_the_head()
    {
        Assert.Equal("power_head", ExecutionDecapRules.NeckWoundFor(ExecutionWeapons.PowerAxe).Point);
        Assert.Equal([("body_dismemberment", 0.6f), ("body_dismemberment_drip", 1.0f)], ExecutionDecapRules.NeckWoundFor(ExecutionWeapons.PowerAxe).Effects);
        Assert.Equal("explode_head", ExecutionDecapRules.NeckWoundFor(ExecutionWeapons.Chainsword).Point);
        Assert.Equal([("body_dismemberment", 0.4f), ("body_dismemberment_drip", 0.8f)], ExecutionDecapRules.NeckWoundFor(ExecutionWeapons.Chainsword).Effects);
        foreach (var w in ExecutionDecapRules.Rows.Select(r => ExecutionRows.All.First(x => x.Id.StartsWith(r.RowBase + ".", StringComparison.Ordinal)).Weapon))
            Assert.All(ExecutionDecapRules.NeckWoundFor(w).Effects, e => Assert.Contains(e.Effect, ExecutionDecapRules.NeckWoundEffects));
        Assert.True(ExecutionDecapRules.IsHeadBoneName("RIG_Head"));
        Assert.True(ExecutionDecapRules.IsHeadBoneName("RIG_Face_Jaw"));
        Assert.False(ExecutionDecapRules.IsHeadBoneName("RIG_Spine3"));   // the Rokkit Boy's mortar and rocket back stay on the stump
        Assert.False(ExecutionDecapRules.IsHeadBoneName("RIG_Neck"));
    }

    [Fact]
    public void Gore_gate_is_the_execution_gore()
    {
        Assert.True(ExecutionDecapRules.Allowed(ContentExtras.Executions | ContentExtras.Warfront));
        Assert.True(ExecutionDecapRules.Allowed(ContentExtras.Executions | ContentExtras.Gore));
        Assert.False(ExecutionDecapRules.Allowed(ContentExtras.Executions));                                  // Classic Extended without its gore
        Assert.False(ExecutionDecapRules.Allowed(ContentExtras.None));                                        // Classic Faithful
        Assert.False(ExecutionDecapRules.Allowed(ContentExtras.Musou));                                       // Musou alone
        Assert.False(ExecutionDecapRules.Allowed(ContentExtras.Executions | ContentExtras.Warfront, off: true));   // --no-execution-gore
    }

    [Fact]
    public void Bodies_that_can_be_cut()
    {
        var o5 = ExecutionDecapRules.RowFor("O5.power_axe")!;
        var c1 = ExecutionDecapRules.RowFor("C1.chainsword")!;
        foreach (var mesh in new[] { "boy_melee", "boy_ranged", "boy_melee_pro", "boy_ranged_pro" })
            Assert.Equal(ExecutionDecapBody.OrkStump, ExecutionDecapRules.BodyFor(o5, mesh, 0));
        Assert.Equal(ExecutionDecapBody.None, ExecutionDecapRules.BodyFor(o5, "boy_ardboy", 0));      // no headless body in the install
        Assert.Equal(ExecutionDecapBody.None, ExecutionDecapRules.BodyFor(o5, "boy_stormboy", 0));
        Assert.Equal(ExecutionDecapBody.None, ExecutionDecapRules.BodyFor(o5, null, 2));
        Assert.Equal(ExecutionDecapBody.HeadPieces, ExecutionDecapRules.BodyFor(c1, null, 1));
        Assert.Equal(ExecutionDecapBody.None, ExecutionDecapRules.BodyFor(c1, null, 0));
        Assert.Equal("boy_melee", ExecutionDecapRules.OrkMeshOf("musou_boy_melee"));
        Assert.Equal("boy_ranged_pro", ExecutionDecapRules.OrkMeshOf("ork_boy_ranged_pro"));
        Assert.Null(ExecutionDecapRules.OrkMeshOf("csm_armour_mp_basic_helmet"));
        Assert.True(ExecutionDecapRules.HeadTriangle(true, true, false));
        Assert.False(ExecutionDecapRules.HeadTriangle(true, false, false));
    }

    [Fact]
    public void Toss_goes_along_the_exit_and_up()
    {
        var (lin, ang) = ExecutionDecapRules.Toss(new Vector3(0f, 5f, 3f), 7u);
        Assert.Equal(ExecutionDecapRules.TossUp, lin.Y, 4);
        Assert.Equal(ExecutionDecapRules.TossAlong, lin.Z, 4);   // the vertical part of the exit is dropped
        Assert.Equal(0f, lin.X, 4);
        Assert.Equal(ExecutionDecapRules.TossSpin, ang.Length(), 3);
        Assert.Equal(ExecutionDecapRules.Toss(new Vector3(0f, 5f, 3f), 7u), ExecutionDecapRules.Toss(new Vector3(0f, 5f, 3f), 7u));   // the same run spins the same on every client
        var (still, _) = ExecutionDecapRules.Toss(Vector3.Zero, 1u);
        Assert.True(float.IsFinite(still.X) && still.Y > 0f);
    }
}
