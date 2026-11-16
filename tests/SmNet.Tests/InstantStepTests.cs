using System.Numerics;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// The owner's melee changes of 2026-11-06 (sim/instant-step): the step forward begins on the first tick of every strike (<see cref="MeleeLockSpec.StepFront"/>), and every melee
/// weapon's strikes step 20 % further (<see cref="MeleeTuning.OwnerStepBoost"/>). Distances measured in the simulation; synthetic clips.
/// </summary>
public class InstantStepTests
{
    static float T(float ticks) => ticks / 30f;

    /// <summary>A clip whose root motion builds up slowly and mostly lands after the hit (like the install's free swings): 0.15 m by the hit, 2 m by the move gate.</summary>
    static MeleeVariant LateVariant(float duration, float hit, float moveAt) => new(1f, Enumerable.Range(0, 61).Select(i =>
    {
        float t = duration * i / 60f;
        return t <= hit ? 0.15f * t / hit : t <= moveAt ? 0.15f + 1.85f * (t - hit) / (moveAt - hit) : 2f + 0.2f * (t - moveAt) / (duration - moveAt);
    }).ToArray());

    static MeleeStep Strike(float factor = 1f, bool inPlace = false)
    {
        float duration = T(45f), hit = T(9f), moveAt = T(30f);
        return new MeleeStep(duration, hit, T(13f), T(30f), T(20f), moveAt, 0f, 0f, 3.5f)
        {
            Reach = new MeleeReach(0f, T(3f), 0f, hit, 3.5f, 1f, [LateVariant(duration, hit, moveAt)]),
            LungeFactor = factor, InPlace = inPlace,
        };
    }

    static SimConfig Config(float front, float factor = 1f, bool inPlace = false) => new()
    {
        MeleeSteps = [Strike(factor, inPlace)], MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f, MeleeLungeScale = 1.625f,
        MeleeLock = MeleeLockSpec.Default with { StepFront = front },
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    static List<float> Steps(SimConfig c)
    {
        var m = c.MeleeSteps[0];
        var list = new List<float>();
        float dt = c.TickSeconds;
        for (float t = 0f; t < m.Duration - 1e-5f; t += dt) list.Add(MeleeLock.LungeStep(m, t, MathF.Min(t + dt, m.Duration), 0f, null, c));
        return list;
    }

    [Fact]
    public void The_step_moves_on_the_first_tick_where_the_clip_stood_still()
    {
        var old = Steps(Config(0f));
        var now = Steps(Config(1f));
        Assert.True(old[0] < 0.05f);     // the clip's own profile: next to nothing in the first tick
        Assert.True(now[0] > 0.15f);     // the instant step: a stride at once
        Assert.True(now.Take(3).Sum() > old.Take(3).Sum() * 3f);
    }

    [Fact]
    public void The_total_and_the_tail_are_unchanged_and_more_is_done_by_the_hit()
    {
        var old = Steps(Config(0f));
        var now = Steps(Config(1f));
        int hitTick = (int)MathF.Round(9f);
        int moveTick = (int)MathF.Round(30f);
        Assert.Equal(old.Sum(), now.Sum(), 2);                                   // the same distance
        Assert.Equal(old.Take(moveTick).Sum(), now.Take(moveTick).Sum(), 2);     // …delivered by the move gate
        Assert.True(now.Take(hitTick).Sum() > old.Take(hitTick).Sum() + 0.5f);   // …but sooner
        Assert.All(now, s => Assert.True(s >= -1e-4f));                          // never backwards
    }

    [Fact]
    public void The_step_never_gets_slower_than_the_clip_asked_after_the_first_tick_and_stays_smooth()
    {
        var now = Steps(Config(1f));
        // An ease-out: each tick up to the move gate steps no more than the one before (no jerk).
        for (int i = 1; i < 30; i++) Assert.True(now[i] <= now[i - 1] + 1e-3f, $"tick {i}");
    }

    [Fact]
    public void An_in_place_strike_has_no_instant_step()
    {
        var c = Config(1f, inPlace: true);
        Assert.Equal(Steps(Config(0f, inPlace: true)).Sum(), Steps(c).Sum(), 3);
        Assert.Equal(Steps(Config(0f, inPlace: true))[0], Steps(c)[0], 4);
    }

    [Fact]
    public void The_lunge_factor_scales_the_instant_step_too()
    {
        Assert.Equal(1.2f * Steps(Config(1f)).Sum(), Steps(Config(1f, 1.2f)).Sum(), 2);
    }

    [Fact]
    public void Front_is_hashed_only_when_set()
    {
        Assert.NotEqual(Config(0f).Hash(), Config(1f).Hash());
    }

    [Fact]
    public void Every_melee_weapon_map_boosts_all_its_strikes_twenty_percent_except_the_heavy_classes_kick()
    {
        Assert.Equal(1.2f, MeleeTuning.OwnerStepBoost);
        foreach (var map in new[] { MeleeTuning.CombatKnife, MeleeTuning.PowerSword, MeleeTuning.PowerAxe, MeleeTuning.ThunderHammer, MeleeTuning.OrkChoppa, MeleeTuning.OrkChoppaSet2 })
            Assert.Equal(MeleeTuning.OwnerStepBoost, map.StepBoost);
        foreach (string wargear in new[] { "sm_mp_melee_chainsword", "csm_mp_melee_chainsword", "sm_mp_melee_2hand_thunder_hammer", "csm_mp_melee_2hand_power_mace", "sm_mp_melee_power_axe" })
            Assert.Equal(MeleeTuning.OwnerStepBoost, MpClasses.MeleeMapFor(wargear)!.StepBoost);
        Assert.Equal(1f, MeleeTuning.Displace.StepBoost);
        Assert.Equal(1f, MpClasses.MeleeMapFor("sm_mp_core_abil_heavy_evade")!.StepBoost);
    }
}
