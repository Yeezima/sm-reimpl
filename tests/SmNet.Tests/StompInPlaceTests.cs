using System.Numerics;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// sim/stomp-in-place (protocol 49; owner, 2026-10-31): the Devastator's and the Havoc's stomp has no step forward (no root-motion lunge, no locked burst, no
/// coasting on the speed the body began with), a rapid F / click sequence cannot slide the body (the kick-to-stomp chain used to restart a lunge), and the
/// stomp's hit lands on the tick the data's foot-stomp tag passes (0.46 of the 1.667 s clip = 0.767 s = tick 23), never earlier. Synthetic attacks only.
/// </summary>
public class StompInPlaceTests
{
    static float T(float ticks) => ticks / 30f;
    const float Strike = 1.5f;
    const int StompHitTick = 23;

    static MeleeVariant Variant(float distance, float duration, float hit) => new(distance, Enumerable.Range(0, 61).Select(i =>
    {
        float t = duration * i / 60f;
        return MathF.Max(0f, distance - Strike) * Math.Clamp(t / hit, 0f, 1f) + 0.6f * Math.Clamp((t - hit) / (duration - hit), 0f, 1f);
    }).ToArray());

    static MeleeStep WithReach(MeleeStep step) => step with
    {
        Reach = new MeleeReach(0f, T(3f), 0f, step.HitTime, 3.5f, 1f, [Variant(1f, step.Duration, step.HitTime), Variant(3.5f, step.Duration, step.HitTime), Variant(7f, step.Duration, step.HitTime)]),
    };

    /// <summary>The kick: hit at tick 11 (0.367 s), combo window 0.567..1.0 s, a lunge; chains to the stomp.</summary>
    static readonly MeleeStep Kick = WithReach(new MeleeStep(1.667f, 0.367f, 0.567f, 1.0f, 0.9f, 1.33f, 30f, 10f, 2f) { StringEnd = true, HeavyNext = 1 });

    /// <summary>The stomp as the data reads it: 1.667 s, the blast at 0.46 of the clip (0.767 s), movement back at 0.8 (1.333 s); in place, with the lunge the clip would have given it.</summary>
    static readonly MeleeStep Stomp = new(1.6666666f, 0.46f * 1.6666666f, 0f, 0f, 0.9f, 0.8f * 1.6666666f, 0f, 0f, 3.5f)
    {
        InPlace = true,
        Stomp = new SplashSpec(7f, new DamageSpec(25f, 50f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(7f, 0.15f)], true, true, "target_all"),
    };

    static readonly SimConfig Config = new()
    {
        Classes = [new ClassLoadout("devastator", -1, -1, [Kick]) { MeleeHeavy = [Stomp], Side = Team.SpaceMarines }],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        MeleeLungeScale = 1.625f,
    };

    static PlayerState Devastator(Vector3 velocity) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, velocity, 0f, true) { Team = Team.SpaceMarines, Class = 0 }, Config);

    static PlayerInput In(uint tick, InputButtons buttons, float forward = 0f) => new(tick, 0, forward, 0f, buttons);

    [Fact]
    public void The_stomp_keeps_the_body_exactly_in_place_even_from_a_run()
    {
        var s = Devastator(new Vector3(0f, 6.5f, 0f));
        var start = s.Position;
        for (uint i = 0; i < 60; i++)
        {
            s = Combat.StepPlayer(s, In(i, i == 0 ? InputButtons.MeleeHeavy : InputButtons.None, forward: 1f), Config, out _, null, null);
            if (s.MeleeStep == 1 && s.MeleeHeavy && s.MeleeTime < Stomp.MoveAt - 1e-3f)
                Assert.True(Vector2.Distance(new Vector2(s.Position.X, s.Position.Y), new Vector2(start.X, start.Y)) < 1e-4f, $"tick {i}: moved to {s.Position}");
        }
    }

    [Fact]
    public void A_rapid_kick_and_stomp_sequence_never_slides_during_a_stomp_and_lunges_only_once_per_kick()
    {
        var s = Devastator(Vector3.Zero);
        var previous = s.Position;
        float inStomp = 0f, total = 0f;
        int stompTicks = 0;
        for (uint i = 0; i < 240; i++)
        {
            var buttons = InputButtons.None;
            if (i % 2 == 0) buttons |= InputButtons.MeleeHeavy;     // F spammed
            if (i % 3 == 0) buttons |= InputButtons.Melee;          // and the click
            s = Combat.StepPlayer(s, In(i, buttons), Config, out _, null, null);
            float step = Vector2.Distance(new Vector2(s.Position.X, s.Position.Y), new Vector2(previous.X, previous.Y));
            total += step;
            if (s.MeleeStep > 0 && s.MeleeHeavy && s.MeleeTime < Stomp.MoveAt - 1e-3f) { inStomp += step; stompTicks++; }
            previous = s.Position;
        }
        Assert.True(stompTicks > 30, "the sequence stomped");
        Assert.True(inStomp < 1e-3f, $"slid {inStomp} m during stomps");
        // Nothing but the kicks' own lunges moves the body: a kick lunge is a few metres at most, and one kick fits in every 1.667 s only if it starts from rest.
        Assert.True(total < 4f * (240f / 30f / 1.667f) , $"total travel {total} m");
    }

    [Fact]
    public void The_stomp_hits_on_the_tick_the_foot_stomp_tag_passes_and_not_before()
    {
        var s = Devastator(Vector3.Zero);
        int hitTick = -1;
        for (uint i = 0; i < 50; i++)
        {
            s = Combat.StepPlayer(s, In(i, i == 0 ? InputButtons.MeleeHeavy : InputButtons.None), Config, out var events, null, null);
            if (events.MeleeHitStep > 0) { Assert.True(events.MeleeHeavy); Assert.Equal(-1, hitTick); hitTick = (int)i; }
        }
        Assert.Equal(StompHitTick, hitTick);
        Assert.True(Stomp.HitTime > 0.7667f - 1e-3f && Stomp.HitTime <= StompHitTick / 30f + 1e-3f);
    }

    [Fact]
    public void In_place_is_part_of_the_config_hash_and_other_steps_hash_as_before()
    {
        var plain = new SimConfig { MeleeSteps = [Stomp with { InPlace = false }] };
        Assert.NotEqual(plain.Hash(), new SimConfig { MeleeSteps = [Stomp] }.Hash());
        Assert.Equal(plain.Hash(), new SimConfig { MeleeSteps = [Stomp with { InPlace = false }] }.Hash());
    }
}
