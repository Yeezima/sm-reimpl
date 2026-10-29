using SmContent;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// Ork O7b / O7c: the engine-free rules of the Ork's actions on screen (how the boy's clips are laid over the simulation's times) and the reader of the clips' timing
/// from the boy's graphs. Synthetic graphs only: hand-made sequencers and tags, no original bytes.
/// </summary>
public class OrkActionsTests
{
    // ---------------------------------------------------------------- rules

    [Fact]
    public void A_reaction_clip_is_played_up_to_its_end_over_the_simulations_time()
    {
        // The simulation holds the player 0.5 s; the boy's heavy hit ends at 1.77 s of its 2 s clip: the half second shows the 1.77 s.
        Assert.Equal(0f, OrkActionRules.ReactionTime(0f, 0.5f, 1.77f, 2f));
        Assert.Equal(0.885f, OrkActionRules.ReactionTime(0.25f, 0.5f, 1.77f, 2f), 3);
        Assert.Equal(1.77f, OrkActionRules.ReactionTime(0.5f, 0.5f, 1.77f, 2f), 3);
    }

    [Fact]
    public void A_reaction_clip_time_is_clamped_to_the_clip_and_survives_bad_numbers()
    {
        Assert.Equal(2f, OrkActionRules.ReactionTime(5f, 0.5f, 1.77f, 2f));              // past the end of the clip
        Assert.Equal(0f, OrkActionRules.ReactionTime(-1f, 0.5f, 1.77f, 2f));
        Assert.Equal(0f, OrkActionRules.ReactionTime(float.NaN, 0.5f, 1.77f, 2f));
        Assert.Equal(0.3f, OrkActionRules.ReactionTime(0.3f, 0f, 1.77f, 2f), 4);          // no usable simulation end: the clip's own speed
        Assert.Equal(0.3f, OrkActionRules.ReactionTime(0.3f, 0.5f, float.NaN, 2f), 4);
        Assert.Equal(0f, OrkActionRules.ReactionTime(0.3f, 0.5f, 1.77f, -1f));            // a broken clip length never gives a negative time
    }

    [Theory]
    [InlineData(2.567f, 1.167f, 2.2f)]   // the left hop: the clip over the simulation's hop
    [InlineData(1.767f, 1.767f, 1f)]     // the choppa swing: the step is the clip
    [InlineData(1f, 0f, 1f)]             // no simulation duration: the clip's own speed
    [InlineData(0f, 1f, 1f)]
    [InlineData(float.NaN, 1f, 1f)]
    public void A_clip_rate_is_the_clip_over_the_simulations_duration(float clip, float simulation, float expected)
    {
        Assert.Equal(expected, OrkActionRules.ClipRate(clip, simulation), 2);
    }

    [Fact]
    public void The_toss_releases_in_the_clip_when_the_simulation_releases()
    {
        // The simulation throws at 0.2 s and ends at 0.667 s; the boy's clip releases at 0.711 s and a mover leaves it at 1.6 s of 2.133 s.
        const float simRelease = 0.2f, simEnd = 0.667f, clipRelease = 0.711f, clipEnd = 1.6f, length = 2.133f;
        Assert.Equal(0f, OrkActionRules.ThrowClipTime(0f, simRelease, simEnd, clipRelease, clipEnd, length));
        Assert.Equal(clipRelease, OrkActionRules.ThrowClipTime(simRelease, simRelease, simEnd, clipRelease, clipEnd, length), 3);
        Assert.Equal(clipEnd, OrkActionRules.ThrowClipTime(simEnd, simRelease, simEnd, clipRelease, clipEnd, length), 3);
        float before = OrkActionRules.ThrowClipTime(0.1f, simRelease, simEnd, clipRelease, clipEnd, length);
        float after = OrkActionRules.ThrowClipTime(0.4f, simRelease, simEnd, clipRelease, clipEnd, length);
        Assert.InRange(before, 0f, clipRelease);
        Assert.InRange(after, clipRelease, clipEnd);
    }

    [Fact]
    public void The_toss_time_never_goes_backwards_and_stays_in_the_clip()
    {
        float last = -1f;
        for (float t = 0f; t <= 2f; t += 0.01f)
        {
            float c = OrkActionRules.ThrowClipTime(t, 0.2f, 0.667f, 0.711f, 1.6f, 2.133f);
            Assert.InRange(c, 0f, 2.133f);
            Assert.True(c >= last - 1e-5f, $"time {t}");
            last = c;
        }
        Assert.Equal(0f, OrkActionRules.ThrowClipTime(-3f, 0.2f, 0.667f, 0.711f, 1.6f, 2.133f));
        Assert.Equal(0f, OrkActionRules.ThrowClipTime(float.NaN, 0.2f, 0.667f, 0.711f, 1.6f, 2.133f));
    }

    [Fact]
    public void A_toss_without_a_simulation_release_plays_on_at_its_own_speed()
    {
        Assert.Equal(0.4f, OrkActionRules.ThrowClipTime(0.4f, 0f, 0f, 0.711f, 1.6f, 2.133f), 4);
    }

    [Theory]
    [InlineData(false, true, -1f, 0f, OrkDeathKind.Backward)]    // shot from the front (the blow runs against the facing): falls backwards
    [InlineData(false, true, 1f, 0f, OrkDeathKind.Forward)]      // shot from behind: falls forward
    [InlineData(false, false, 0f, 0f, OrkDeathKind.Backward)]    // no direction known: as a hit from the front
    public void A_shot_death_falls_away_from_the_blow(bool melee, bool hasDirection, float along, float right, OrkDeathKind expected)
    {
        Assert.Equal(expected, OrkActionRules.DeathKindOf(melee, hasDirection, along, right));
    }

    [Theory]
    [InlineData(1f, -1f, OrkDeathKind.MeleeA)]    // from the left (pushed right), from the front: "Death Left"
    [InlineData(1f, 1f, OrkDeathKind.MeleeB)]     // from the left, from behind: "Death Left Back"
    [InlineData(-1f, -1f, OrkDeathKind.MeleeB)]   // from the right, from the front: "Death Right"
    [InlineData(-1f, 1f, OrkDeathKind.MeleeA)]    // from the right, from behind: "Melee Death Back Right"
    public void A_melee_death_is_chosen_by_the_side_and_direction_of_the_blow(float right, float along, OrkDeathKind expected)
    {
        Assert.Equal(expected, OrkActionRules.DeathKindOf(true, true, along, right));
    }

    [Fact]
    public void A_melee_death_without_a_direction_is_a_hit_from_the_right_front()
    {
        Assert.Equal(OrkDeathKind.MeleeB, OrkActionRules.DeathKindOf(true, false, 0f, 0f));
    }

    [Fact]
    public void The_death_hold_is_the_ragdoll_window_never_past_the_clip()
    {
        Assert.Equal(0.567f, OrkActionRules.DeathHold(new OrkDeathClip(OrkDeathKind.Backward, "a", 1.5f, 0.567f)), 3);
        Assert.Equal(1f, OrkActionRules.DeathHold(new OrkDeathClip(OrkDeathKind.Backward, "a", 1f, 3f)));
        Assert.Equal(0f, OrkActionRules.DeathHold(new OrkDeathClip(OrkDeathKind.Backward, "a", 1f, -1f)));
    }

    [Fact]
    public void A_charge_stop_plays_the_clip_on_from_the_hit()
    {
        Assert.Equal(0.667f, OrkActionRules.RushStopClipTime(0.667f, 0f, 1.667f), 3);
        Assert.Equal(0.9f, OrkActionRules.RushStopClipTime(0.667f, 0.233f, 1.667f), 3);
        Assert.Equal(1.667f, OrkActionRules.RushStopClipTime(0.667f, 5f, 1.667f), 3);   // clamped to the clip
        Assert.Equal(0.667f, OrkActionRules.RushStopClipTime(0.667f, -1f, 1.667f), 3);  // a negative stop time is none
    }

    // The charge with the data's numbers: the clip 1.667 s, the hit at 0.667 s, the stop 0.333 s (the stop move's own clip length), the rush move the clip's length.
    const float RushClip = 1.667f, RushHit = 0.667f, StopLength = 0.333f;

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1.667f, 1.667f)]
    [InlineData(5f, 1.667f)]
    public void A_charge_in_the_rush_plays_the_clip_at_the_simulations_time(float simTime, float expected)
    {
        Assert.Equal(expected, OrkActionRules.RushClipTime(RushPhase.Rush, RushHit, simTime, RushClip, RushClip), 3);
    }

    [Theory]
    [InlineData(0f, 0.667f)]
    [InlineData(0.1f, 0.767f)]
    [InlineData(0.333f, 1.0f)]   // the stop's whole 0.333 s ends where the attack gives control back, not at the clip's end
    public void A_charge_stop_is_not_scaled_by_the_stop_moves_short_length(float simTime, float expected)
    {
        float time = OrkActionRules.RushClipTime(RushPhase.Collision, RushHit, simTime, StopLength, RushClip);
        Assert.Equal(expected, time, 3);
        Assert.True(time < RushClip - 0.5f);   // the regression: scaling by 1.667 / 0.333 sent the stop to the clip's end at once
    }

    [Theory]
    [InlineData(1.0f, 2.0f, 1.0f, 0.5f)]    // a move twice as long as the clip: the clip plays at half rate
    [InlineData(1.0f, 0.5f, 1.0f, 1.0f)]    // a move half as long: double rate, clamped to the clip's end
    [InlineData(0.25f, 0.5f, 1.0f, 0.5f)]
    public void A_charge_in_the_rush_scales_by_the_clips_rate_over_the_move(float simTime, float moveLength, float clipLength, float expected)
    {
        Assert.Equal(expected, OrkActionRules.RushClipTime(RushPhase.Rush, RushHit, simTime, moveLength, clipLength), 3);
    }

    [Fact]
    public void A_charge_clip_time_is_zero_outside_the_charge_and_survives_bad_times()
    {
        Assert.Equal(0f, OrkActionRules.RushClipTime(RushPhase.None, RushHit, 0.5f, RushClip, RushClip));
        Assert.Equal(0f, OrkActionRules.RushClipTime(RushPhase.Rush, RushHit, float.NaN, RushClip, RushClip));
        Assert.Equal(RushHit, OrkActionRules.RushClipTime(RushPhase.Collision, RushHit, float.NaN, StopLength, RushClip), 3);
    }

    // ---------------------------------------------------------------- the reader

    static BehaviorSequencer Seq(string name, string clip, float speed, params BehaviorTag[] tags) => new(name, [clip], tags, [speed]);
    static BehaviorTag Tag(string kind, string name, float start, float end = -1f) => new(kind, start, end < 0f ? start : end, name, null);

    static BehaviorGraph Reactions()
    {
        var clips = new[]
        {
            "BOY_1HM_REC_Lt_Front_Hi_L_01", "BOY_1HM_REC_Lt_Back_Hi_R_01", "BOY_1HM_REC_Hvy_Front_Hi_L_01", "BOY_1HM_REC_Hvy_Back_Hi_L_01",
            "BOY_1HM_REC_Hvy_Front_Low_R_01", "BOY_1HM_REC_Hvy_Back_Low_R_01",
        };
        return new BehaviorGraph("BOY_REACTIONS_Coop", [], clips.Select(c => Seq("seq " + c, c, 1f, Tag("Branch", "BRANCH_Reaction_End", 0.9f))).ToList());
    }

    static float Length(string clip) => clip.Contains("Hvy") ? 2f : clip.Contains("Lt_") ? 1f : clip.Contains("Toss") ? 2.133f : clip.EndsWith("DIE_B") ? 1.5f : clip.Contains("Fall_Forward") ? 2.067f : clip.Contains("Basic_02") ? 1.167f : 0.833f;

    [Fact]
    public void The_plan_reads_the_boys_reactions_with_their_end_times()
    {
        var plan = OrkActions.From([Reactions()], Length, RigProfiles.OrkBoy, null, null);
        Assert.Equal(3, plan.Reactions.Count);
        var stun = plan.ReactionOf(ReactionKind.Stun)!;
        Assert.Equal("BOY_1HM_REC_Hvy_Front_Hi_L_01", stun.Front.Animation);
        Assert.Equal(0.9f * 2f, stun.Front.EndAt, 3);
        Assert.Equal("BOY_1HM_REC_Lt_Back_Hi_R_01", plan.ReactionOf(ReactionKind.Interrupt)!.Back.Animation);
        Assert.Null(plan.ReactionOf(ReactionKind.None));
    }

    [Fact]
    public void A_missing_reaction_graph_gives_no_reactions_and_a_note_instead_of_failing()
    {
        var plan = OrkActions.From([], Length, RigProfiles.OrkBoy, null, null);
        Assert.Empty(plan.Reactions);
        Assert.Contains(plan.Notes, n => n.Contains("BOY_REACTIONS_Coop"));
        Assert.Null(plan.GunRecoil);
        Assert.Null(plan.RecoilFor(pistol: true));
        Assert.Null(plan.UpperBodyMask);
        Assert.Contains(plan.Notes, n => n.Contains("recoil"));
        Assert.Contains(plan.Notes, n => n.Contains("bone mask"));
    }

    [Fact]
    public void The_toss_release_is_the_ranged_fire_event_and_the_end_the_run_branch()
    {
        var graph = new BehaviorGraph("Base_RANGED_Coop", [],
        [
            Seq("toss", "BOY_2HR_RNG_GrenadeToss", 1f, Tag("Branch", "BRANCH_GrenadeToss_End", 0.9667f), Tag("Branch", "BRANCH_Run", 0.75f), Tag("Event", "Ability_Ranged_Fire", 0.3333f)),
        ]);
        var notes = new List<string>();
        var toss = OrkActions.Throw([graph], "BOY_2HR_RNG_GrenadeToss", Length, notes)!;
        Assert.Equal(0.3333f * 2.133f, toss.ReleaseAt, 3);
        Assert.Equal(0.75f * 2.133f, toss.EndAt, 3);
        Assert.Equal(2.133f, toss.Length, 3);
    }

    [Fact]
    public void Without_a_run_branch_the_toss_ends_at_its_toss_end_branch_and_without_both_at_the_clips_end()
    {
        var withEnd = new BehaviorGraph("g", [], [Seq("toss", "BOY_2HR_RNG_GrenadeToss", 1f, Tag("Branch", "BRANCH_GrenadeToss_End", 0.9667f), Tag("Event", "Ability_Ranged_Fire", 0.3333f))]);
        Assert.Equal(0.9667f * 2.133f, OrkActions.Throw([withEnd], "BOY_2HR_RNG_GrenadeToss", Length, [])!.EndAt, 3);
        var bare = new BehaviorGraph("g", [], [Seq("toss", "BOY_2HR_RNG_GrenadeToss", 1f, Tag("Event", "Ability_Ranged_Fire", 0.3333f))]);
        Assert.Equal(2.133f, OrkActions.Throw([bare], "BOY_2HR_RNG_GrenadeToss", Length, [])!.EndAt, 3);
    }

    [Fact]
    public void A_toss_without_a_release_event_or_a_clip_name_is_none_with_a_note()
    {
        var notes = new List<string>();
        var none = new BehaviorGraph("g", [], [Seq("toss", "BOY_2HR_RNG_GrenadeToss", 1f)]);
        Assert.Null(OrkActions.Throw([none], "BOY_2HR_RNG_GrenadeToss", Length, notes));
        Assert.Null(OrkActions.Throw([none], null, Length, notes));
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void A_death_clip_hands_over_to_the_ragdoll_where_its_pre_powered_ragdoll_window_ends()
    {
        var ranged = new BehaviorGraph("Base_RANGED_Coop", [],
        [
            Seq("back", "BOY_2HR_DIE_B", 1f, Tag("Modifier", "Default Death Controls05", 0.0222f, 1f), Tag("Modifier", "Pre-Powered Ragdoll00", 0f, 0.3778f)),
            Seq("melee a", "BOY_1HM_DIE_Basic_02", 1f, Tag("Modifier", "Pre-Powered Ragdoll", 0f, 0.3429f)),
        ]);
        // The melee graph plays the same clip with another window: the ranged graph's is the one used.
        var melee = new BehaviorGraph("Base_MELEE_Coop", [], [Seq("melee a", "BOY_1HM_DIE_Basic_02", 1f, Tag("Modifier", "Pre-Powered Ragdoll", 0f, 0.4571f))]);
        var notes = new List<string>();
        var deaths = OrkActions.Deaths([melee, ranged], Length, notes, "Base_RANGED_Coop");
        Assert.Equal(0.3778f * 1.5f, deaths.Single(d => d.Kind == OrkDeathKind.Backward).RagdollAt, 3);
        Assert.Equal(0.3429f * 1.167f, deaths.Single(d => d.Kind == OrkDeathKind.MeleeA).RagdollAt, 3);
    }

    [Fact]
    public void A_death_clip_without_a_window_hands_over_after_0_4_seconds_and_says_so()
    {
        var notes = new List<string>();
        var deaths = OrkActions.Deaths([new BehaviorGraph("g", [], [Seq("fwd", "BOY_2HR_DIE_Fall_Forward_01", 1f)])], Length, notes, "g");
        Assert.Equal(0.4f, deaths.Single(d => d.Kind == OrkDeathKind.Forward).RagdollAt, 3);
        Assert.Contains(notes, n => n.Contains("Pre-Powered Ragdoll") && n.Contains("0.4"));
    }

    [Fact]
    public void A_death_clip_the_rig_cannot_read_is_left_out_with_a_note()
    {
        var notes = new List<string>();
        var deaths = OrkActions.Deaths([], name => name == "BOY_2HR_DIE_B" ? throw new KeyNotFoundException("no such clip") : 1f, notes, "g");
        Assert.DoesNotContain(deaths, d => d.Kind == OrkDeathKind.Backward);
        Assert.Contains(notes, n => n.Contains("BOY_2HR_DIE_B"));
    }

    [Fact]
    public void The_plan_picks_a_death_of_the_kind_or_the_first_there_is()
    {
        var clips = new List<OrkDeathClip> { new(OrkDeathKind.Forward, "f", 2f, 0.9f), new(OrkDeathKind.MeleeB, "b", 0.8f, 0.4f) };
        var plan = new OrkActionPlan([], null, null, null, null, clips, null, []);
        Assert.Equal("b", plan.DeathOf(OrkDeathKind.MeleeB)!.Clip);
        Assert.Equal("f", plan.DeathOf(OrkDeathKind.Backward)!.Clip);   // no backward death: the first one
        Assert.Null(new OrkActionPlan([], null, null, null, null, [], null, []).DeathOf(OrkDeathKind.Backward));
    }

    [Fact]
    public void The_recoil_of_a_pistol_falls_back_to_the_guns_and_back()
    {
        var gun = new RecoilClip("seq", "BOY_2HR_RNG_Fire_S_01", 1f, 1f, true, 0.2f, 0.05f);
        var plan = new OrkActionPlan([], null, gun, null, null, [], null, []);
        Assert.Same(gun, plan.RecoilFor(pistol: true));
        Assert.Same(gun, plan.RecoilFor(pistol: false));
        var pistolOnly = new OrkActionPlan([], null, null, gun, null, [], null, []);
        Assert.Same(gun, pistolOnly.RecoilFor(pistol: false));
    }

    [Fact]
    public void Every_clip_of_the_plan_is_listed_for_the_rig_check()
    {
        var gun = new RecoilClip("seq", "BOY_2HR_RNG_Fire_S_01", 1f, 1f, true, 0.2f, 0.05f);
        var plan = OrkActions.From([Reactions()], Length, RigProfiles.OrkBoy, null, null) with { GunRecoil = gun, Throw = new OrkThrowClip("toss", 2f, 0.7f, 1.6f), Deaths = [new(OrkDeathKind.Backward, "die", 1f, 0.5f)] };
        var clips = plan.Clips().ToList();
        Assert.Contains("BOY_1HM_REC_Hvy_Front_Low_R_01", clips);
        Assert.Contains("BOY_2HR_RNG_Fire_S_01", clips);
        Assert.Contains("toss", clips);
        Assert.Contains("die", clips);
    }

    [Fact]
    public void The_clips_the_simulation_names_are_in_the_preload_hint()
    {
        Assert.Contains(OrkClass.RushClip, OrkActions.SimClips);
        Assert.Contains("BOY_2HR_NAV_Evade4m_L_01", OrkActions.SimClips);
        Assert.Contains("BOY_2HR_NAV_Evade4m_R_01", OrkActions.SimClips);
        Assert.Equal(OrkActions.SimClips.Count, OrkActions.SimClips.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_boys_profile_names_the_clips_the_actions_use()
    {
        Assert.Equal("BOY_2HR_RNG_GrenadeToss", RigProfiles.OrkBoy.Clips.TossClip);
        Assert.Equal("BOY_REACTIONS_Coop", RigProfiles.OrkBoy.Reactions.Graph);
        Assert.Contains(OrkActions.DeathClips, d => d.Kind == OrkDeathKind.Backward && d.Clip == "BOY_2HR_DIE_B");
        Assert.Equal(Enum.GetValues<OrkDeathKind>().Length, OrkActions.DeathClips.Select(d => d.Kind).Distinct().Count());   // one clip for every kind
    }
}
