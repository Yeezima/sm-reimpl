using System.Numerics;
using SmContent;
using SmNet;
using SmSim;
using Xunit;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// anim/hammer-swing (the owner, 2026-11-01): the thunder hammer's standard swings step forward less (<see cref="MeleeStep.LungeFactor"/>, set for the hammer's light strikes only), and its air
/// slash plays its own clip (<see cref="HammerSlashPose"/>, client presentation). Synthetic clips throughout (no original bytes).
/// </summary>
public class HammerSwingTests(ITestOutputHelper output)
{
    static float T(float ticks) => ticks / 30f;

    static MeleeVariant Variant(float duration, float hit) => new(0f, Enumerable.Range(0, 61).Select(i =>
    {
        float t = duration * i / 60f;
        return 4f * Math.Clamp(t / hit, 0f, 1f) + 1f * Math.Clamp((t - hit) / (duration - hit), 0f, 1f);
    }).ToArray());

    /// <summary>A hammer-like swing: one clip variant (no distance blend), 5 m of root motion, hit at tick 7, moves again at 22, over at 31.</summary>
    static MeleeStep Swing(float factor)
    {
        float duration = T(30.5f), hit = T(6.5f);
        return new MeleeStep(duration, hit, T(12.5f), T(18.5f), T(15.5f), T(21.5f), 0f, 0f, 3.5f)
        {
            Reach = new MeleeReach(0f, T(3f), 0f, hit, 3.5f, 0f, [Variant(duration, hit)]),
            LungeFactor = factor,
        };
    }

    static SimConfig Config(float factor) => new()
    {
        MeleeSteps = [Swing(factor), Swing(factor), Swing(factor)], MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f, MeleeLungeScale = 1.625f,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);

    static List<PlayerState> Run(SimConfig c, Vector3? target, int ticks)
    {
        var self = Fresh(c);
        var view = target is { } t ? new Dictionary<int, PlayerState> { [2] = Combat.Respawned(new PlayerState(2, t, Vector3.Zero, 0f, true), c) } : null;
        var states = new List<PlayerState>();
        int locked = 0;
        for (int i = 0; i < ticks; i++)
        {
            var input = new PlayerInput((uint)i, 0, 0, 0f, i == 0 ? InputButtons.Melee : InputButtons.None);
            if (view is not null) { input = input with { MeleeTarget = MeleeLock.Choose(self, input, view, c, null, locked) }; locked = input.MeleeTarget; }
            self = Combat.StepPlayer(self, input, c, out _, null, view);
            states.Add(self);
        }
        return states;
    }

    [Fact]
    public void A_strike_factor_scales_the_free_step_and_only_that_strike()
    {
        float full = Run(Config(1f), null, 22)[^1].Position.Y, trimmed = Run(Config(0.82f), null, 22)[^1].Position.Y;
        output.WriteLine($"no target, to the move gate: {full:F2} m → {trimmed:F2} m ({full - trimmed:F2} m less)");
        Assert.True(full > 4f);
        Assert.Equal(0.82f * full, trimmed, 3);
        Assert.Equal(1f, new MeleeStep().LungeFactorApplied);   // a step with no factor set: none (the default, a record built without it)
        Assert.Equal(1f, (new MeleeStep() with { LungeFactor = 0f }).LungeFactorApplied);
        Assert.Equal(1f, (new MeleeStep() with { LungeFactor = float.NaN }).LungeFactorApplied);
    }

    [Fact]
    public void The_hash_covers_the_factor_only_when_it_is_not_one()
    {
        Assert.Equal(Config(1f).Hash(), (Config(1f) with { MeleeSteps = [Swing(1f), Swing(1f), Swing(1f)] }).Hash());
        Assert.NotEqual(Config(1f).Hash(), Config(0.82f).Hash());
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(5f)]
    [InlineData(7f)]
    public void With_a_target_the_trimmed_swing_still_closes_to_the_separation_by_the_hit(float distance)
    {
        var target = new Vector3(0f, distance, 0f);
        var full = Run(Config(1f), target, 31);
        var trimmed = Run(Config(0.82f), target, 31);
        float separation = Config(1f).MeleeLock.Separation;
        float gapFull = distance - full[6].Position.Y, gapTrimmed = distance - trimmed[6].Position.Y;   // at the hit (tick 7)
        output.WriteLine($"target {distance} m: gap at the hit {gapFull:F3} (1) / {gapTrimmed:F3} (0.82) m; at the move gate {distance - full[20].Position.Y:F3} / {distance - trimmed[20].Position.Y:F3} m");
        Assert.True(gapTrimmed <= gapFull + 0.05f, "the lock's closing is the burst: the hit still lands from the same gap");
        Assert.All(trimmed, s => Assert.True(distance - s.Position.Y >= separation - 1e-3f, "never within the separation"));
        Assert.All(trimmed, s => Assert.True(s.Position.Y >= -1e-3f, "no step goes backwards"));
    }

    [Fact]
    public void The_hammer_and_the_daemon_maul_trim_their_standard_swings_and_no_other_weapon_does()
    {
        Assert.Equal(MeleeTuning.HammerLightLungeFactor, MeleeTuning.ThunderHammer.LightLungeFactor);
        Assert.InRange(MeleeTuning.HammerLightLungeFactor, 0.5f, 0.95f);
        foreach (var other in new[] { MeleeTuning.CombatKnife, MeleeTuning.PowerSword, MeleeTuning.PowerAxe, MeleeTuning.Displace })
            Assert.Equal(1f, other.LightLungeFactor);
        // The daemon maul is the hammer's twin (MpClasses.MeleeMapFor): same map, so the same factor.
        Assert.Equal(MeleeTuning.HammerLightLungeFactor, MpClasses.MeleeMapFor("csm_mp_melee_2hand_power_mace")!.LightLungeFactor);
        Assert.Equal(MeleeTuning.HammerLightLungeFactor, MpClasses.MeleeMapFor("sm_mp_melee_2hand_thunder_hammer")!.LightLungeFactor);
        Assert.Equal(1f, MpClasses.MeleeMapFor("sm_mp_melee_chainsword")!.LightLungeFactor);
    }

    // ---------------------------------------------------------------- the hammer's air slash clip

    [Theory]
    [InlineData("sm_mp_melee_2hand_thunder_hammer", true)]
    [InlineData("csm_mp_melee_2hand_power_mace", true)]
    [InlineData("sm_mp_melee_chainsword", false)]
    [InlineData("sm_mp_melee_powersword", false)]
    [InlineData(null, false)]
    public void Only_the_hammer_and_the_maul_take_the_hammer_dash_state(string? wargear, bool hammer) => Assert.Equal(hammer, HammerSlashPose.IsHammer(wargear));

    [Fact]
    public void The_dive_plays_the_flight_crop_at_the_graphs_rate_and_holds_its_end()
    {
        Assert.Equal(HammerSlashPose.FlightStart, HammerSlashPose.DiveTime(0f), 4);
        Assert.Equal(HammerSlashPose.FlightStart + 0.2f * 0.1f, HammerSlashPose.DiveTime(0.1f), 4);
        Assert.Equal(HammerSlashPose.FlightEnd, HammerSlashPose.DiveTime(0.5f), 4);
        Assert.Equal(HammerSlashPose.FlightEnd, HammerSlashPose.DiveTime(3f), 4);   // a long dive holds the raised hammer
        Assert.Equal(HammerSlashPose.FlightStart, HammerSlashPose.DiveTime(-1f), 4);
    }

    [Fact]
    public void The_impact_plays_from_its_crop_start_at_eight_tenths_of_the_clip_rate_without_a_step()
    {
        const float length = 2.333f;
        // A dive that held the wind-up's end (0.5333) lands: the strike starts a tick of clip later (0.0333 s), eased over the catch-up.
        var times = Enumerable.Range(0, 20).Select(i => HammerSlashPose.ImpactTime(i / 60f, HammerSlashPose.FlightEnd, length)).ToArray();
        output.WriteLine(string.Join(" ", times.Select(t => t.ToString("F3"))));
        Assert.Equal(HammerSlashPose.FlightEnd, times[0], 4);
        Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second >= p.First - 1e-5f, "never backwards"));
        Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second - p.First < 0.05f, "no jump between frames"));
        Assert.Equal(HammerSlashPose.ImpactStart + 0.8f * 0.5f, HammerSlashPose.ImpactTime(0.5f, float.NaN, length), 4);
        // A dive that landed early (clip 0.4733, the dive of 0.2 s) catches up without a pop.
        float landed = HammerSlashPose.DiveTime(0.2f);
        Assert.Equal(landed, HammerSlashPose.ImpactTime(0f, landed, length), 4);
        Assert.Equal(HammerSlashPose.ImpactStart + 0.8f * SlashPose.EasedCatchUp, HammerSlashPose.ImpactTime(SlashPose.EasedCatchUp, landed, length), 4);
        // The clip's end bounds it, and the time left after the landing is the rest at the clip's rate.
        Assert.Equal(length, HammerSlashPose.ImpactTime(10f, float.NaN, length), 4);
        Assert.Equal((length - HammerSlashPose.ImpactStart) / 0.8f, HammerSlashPose.AfterImpact(length), 4);
    }
}
