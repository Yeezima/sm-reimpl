using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>Unit tests for <see cref="Combat"/> and the world's shot resolution, with the original bolter values.</summary>
public class CombatTests
{
    static readonly SimConfig Bolter = new()
    {
        Damage = 22f, ShotCooldown = 0.133f, ClipSize = 30, ReserveSize = 270, ReloadTime = 1.3f, MaxRange = 250f,
        FallOffStart = 45f, FallOffEnd = 70f, FallOffEndMultiplier = 0.8f,
        HeadMultiplier = 2.55f, BodyMultiplier = 1f, ShieldHeadMultiplier = 0.5f, ShieldBodyMultiplier = 0.505f,
    };

    static PlayerState Fresh(int id = 1, Vector3 at = default) => Combat.Respawned(new PlayerState(id, at, Vector3.Zero, 0f, true), Bolter);
    static PlayerInput Hold(uint tick, InputButtons buttons) => new(tick, 0, 0, 0, buttons);

    [Fact]
    public void Fire_rate_is_limited_by_the_shot_cooldown()
    {
        var s = Fresh();
        int shots = 0;
        for (uint t = 1; t <= 30; t++) { s = Combat.StepSelf(s, Hold(t, InputButtons.Fire), Bolter, out bool fired); if (fired) shots++; }
        // 1 s at 30 Hz with a 0.133 s cooldown: a shot every 4 ticks (0.133 s rounds up to the next tick).
        Assert.Equal(8, shots);
        Assert.Equal(30 - 8, s.Clip);
    }

    [Fact]
    public void Empty_clip_reloads_from_reserve_after_the_reload_time()
    {
        var s = Fresh() with { Clip = 0 };
        s = Combat.StepSelf(s, Hold(1, InputButtons.Fire), Bolter, out bool fired);
        Assert.False(fired);
        Assert.Equal(1.3f, s.ReloadLeft, 3);
        int ticks = 0;
        while (s.ReloadLeft > 0f && ticks < 100) { s = Combat.StepSelf(s, Hold((uint)(2 + ticks), InputButtons.Fire), Bolter, out fired); Assert.False(fired); ticks++; }
        Assert.InRange(ticks, 38, 40);   // 1.3 s at 30 Hz
        Assert.Equal((30, 240), (s.Clip, s.Reserve));
    }

    [Fact]
    public void Manual_reload_tops_up_and_is_refused_when_full_or_out_of_reserve()
    {
        var s = Fresh() with { Clip = 25, Reserve = 3 };
        s = Combat.StepSelf(s, Hold(1, InputButtons.Reload), Bolter, out _);
        for (uint t = 2; s.ReloadLeft > 0f; t++) s = Combat.StepSelf(s, Hold(t, InputButtons.None), Bolter, out _);
        Assert.Equal((28, 0), (s.Clip, s.Reserve));
        s = Combat.StepSelf(s, Hold(100, InputButtons.Reload), Bolter, out _);
        Assert.Equal(0f, s.ReloadLeft);
        s = Combat.StepSelf(Fresh(), Hold(1, InputButtons.Reload), Bolter, out _);
        Assert.Equal(0f, s.ReloadLeft);
    }

    [Theory]
    [InlineData(0f, 22f)]
    [InlineData(45f, 22f)]
    [InlineData(57.5f, 19.8f)]
    [InlineData(70f, 17.6f)]
    [InlineData(200f, 17.6f)]
    public void Damage_falls_off_linearly_between_the_file_points(float distance, float damage) =>
        Assert.Equal(damage, Combat.DamageAt(distance, Bolter), 3);

    [Fact]
    public void Shield_absorbs_first_then_health_takes_the_remainder()
    {
        var s = Fresh();
        var (after, shield, health) = Combat.ApplyDamage(s, 22f, HitZone.Body, Bolter);
        Assert.Equal(22f * 0.505f, shield, 3);
        Assert.Equal(0f, health);
        Assert.Equal(100f, after.Health);

        // 1 shield left: it absorbs 1 point at ×0.505 (1/0.505 raw), the rest of the 22 goes to health at ×1.
        (after, shield, health) = Combat.ApplyDamage(s with { Shield = 1f }, 22f, HitZone.Body, Bolter);
        Assert.Equal(1f, shield, 3);
        Assert.Equal(22f - 1f / 0.505f, health, 3);
        Assert.Equal(0f, after.Shield, 3);

        (_, _, health) = Combat.ApplyDamage(s with { Shield = 0f }, 22f, HitZone.Head, Bolter);
        Assert.Equal(22f * 2.55f, health, 3);
        Assert.Equal(0f, after.SinceDamage);
    }

    [Fact]
    public void Lethal_damage_kills_and_the_dead_cannot_act()
    {
        var s = Fresh() with { Shield = 0f, Health = 10f };
        var (dead, _, _) = Combat.ApplyDamage(s, 22f, HitZone.Body, Bolter);
        Assert.False(dead.Alive);
        Assert.Equal(0f, dead.Health);
        Assert.Equal(Bolter.RespawnTime, dead.RespawnLeft);
        var next = Combat.StepSelf(dead, new PlayerInput(1, 0, 1, 0, InputButtons.Fire | InputButtons.Sprint), Bolter, out bool fired);
        Assert.False(fired);
        Assert.Equal(dead.Position, next.Position);
        Assert.Equal(dead.Clip, next.Clip);
        Assert.Equal(Bolter.RespawnTime - Bolter.TickSeconds, next.RespawnLeft, 4);
        Assert.Equal((dead, 0f, 0f), Combat.ApplyDamage(dead, 22f, HitZone.Head, Bolter));
    }

    [Fact]
    public void Shield_and_health_regenerate_after_their_delays()
    {
        var s = Fresh() with { Shield = 0f, Health = 50f, SinceDamage = 0f };
        uint t = 0;
        PlayerState Run(PlayerState x, float seconds)
        {
            for (int i = 0; i < (int)MathF.Round(seconds * Bolter.TickRate); i++) x = Combat.StepSelf(x, Hold(++t, InputButtons.None), Bolter, out _);
            return x;
        }
        s = Run(s, 4.9f);
        Assert.Equal((0f, 50f), (s.Shield, s.Health));
        s = Run(s, 1.1f);   // 6 s: health regenerating for ~1 s, shield not yet
        Assert.Equal(0f, s.Shield);
        Assert.InRange(s.Health, 54f, 56f);
        s = Run(s, 2f);     // 8 s: shield ~1 s of regeneration at 33/s
        Assert.InRange(s.Shield, 31f, 36f);
        s = Run(s, 20f);
        Assert.Equal((100f, 100f), (s.Shield, s.Health));
    }

    [Fact]
    public void Hit_test_distinguishes_head_body_and_miss()
    {
        var feet = new Vector3(0, 20, 0);
        var eye = new Vector3(0, 0, Bolter.EyeHeight);
        Vector3 Toward(float z) => Vector3.Normalize(feet + new Vector3(0, 0, z) - eye);
        Assert.Equal(HitZone.Head, Combat.HitTest(eye, Toward(Bolter.HeadHeight), feet, Bolter).Zone);
        Assert.Equal(HitZone.Body, Combat.HitTest(eye, Toward(1.0f), feet, Bolter).Zone);
        Assert.Equal(HitZone.None, Combat.HitTest(eye, Toward(3.5f), feet, Bolter).Zone);
        Assert.Equal(HitZone.None, Combat.HitTest(eye, -Toward(1.0f), feet, Bolter).Zone);
        var (_, distance) = Combat.HitTest(eye, Toward(1.0f), feet, Bolter);
        Assert.InRange(distance, 19f, 20f);
        Assert.Equal(HitZone.None, Combat.HitTest(eye, Toward(1.0f), new Vector3(0, 300, 0), Bolter).Zone);   // beyond max range
        // A shooter standing inside a target hits nothing of it, whichever way it aims.
        Assert.Equal(HitZone.None, Combat.HitTest(eye, new Vector3(0, 1, 0), new Vector3(0.1f, 0, 0), Bolter).Zone);
        Assert.Equal(HitZone.None, Combat.HitTest(eye, new Vector3(0, -1, 0), new Vector3(0.1f, 0, 0), Bolter).Zone);
    }

    [Fact]
    public void Aim_direction_matches_the_movement_yaw_convention()
    {
        Assert.True(Vector3.Distance(new Vector3(0, 1, 0), Combat.AimDirection(0, 0)) < 1e-5f);
        Assert.True(Vector3.Distance(new Vector3(-1, 0, 0), Combat.AimDirection(MathF.PI / 2, 0)) < 1e-5f);
        Assert.True(Combat.AimDirection(0, 0.5f).Z > 0);
    }

    [Fact]
    public void World_resolves_hits_against_the_rewound_target_position()
    {
        var config = Bolter with { MaxRewindTicks = 8 };
        var world = new World(config);
        world.Spawn(1); world.Spawn(2);
        // Target strafes sideways (+X relative to the shooter's line of sight is irrelevant; it just moves) for a while.
        PlayerInput Idle(int id) => new(0, 0, 0, 0, InputButtons.None);
        for (int i = 0; i < 10; i++) world.Step(id => id == 2 ? new PlayerInput(0, 1, 0, 0, InputButtons.None) : Idle(id));
        uint seenTick = world.Tick - 5;
        // Aim at where the target was 5 ticks ago (what a client 5 ticks behind sees).
        var targetThen = HistoryPosition(world, config, seenTick);
        var eye = world.Players[1].Position + new Vector3(0, 0, config.EyeHeight);
        var aim = Vector3.Normalize(targetThen + new Vector3(0, 0, 1.0f) - eye);
        float yaw = MathF.Atan2(-aim.X, aim.Y), pitch = MathF.Asin(aim.Z);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, yaw, InputButtons.Fire, pitch, seenTick) : Idle(id));
        var hit = Assert.Single(world.LastHits);
        Assert.Equal((1, 2, HitZone.Body), (hit.Shooter, hit.Target, hit.Zone));
        Assert.True(world.Players[2].Shield < config.ShieldMax);

        // The same aim with the current view tick misses: the target has moved on.
        float yaw2 = yaw, pitch2 = pitch;
        for (int i = 0; i < 6; i++) world.Step(id => id == 2 ? new PlayerInput(0, 1, 0, 0, InputButtons.None) : Idle(id));
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, yaw2, InputButtons.Fire, pitch2, world.Tick) : id == 2 ? new PlayerInput(0, 1, 0, 0, InputButtons.None) : Idle(id));
        Assert.Empty(world.LastHits);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0f)]
    public void Dead_players_respawn_with_full_health_shield_and_ammo(float respawnTime)
    {
        var config = Bolter with { RespawnTime = respawnTime };
        var world = new World(config);
        world.Spawn(1);
        // Kill player 1 directly through repeated head shots from a second player standing in front.
        world.Spawn(2);
        var a = world.Players[1].Position; var b = world.Players[2].Position;
        var aim = Vector3.Normalize(a + new Vector3(0, 0, 1.0f) - (b + new Vector3(0, 0, config.EyeHeight)));
        float yaw = MathF.Atan2(-aim.X, aim.Y), pitch = MathF.Asin(aim.Z);
        int ticks = 0;
        while (world.Players[1].Alive && ticks++ < 300)
            world.Step(id => id == 2 ? new PlayerInput(0, 0, 0, yaw, InputButtons.Fire, pitch, world.Tick) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
        Assert.False(world.Players[1].Alive);   // killed this tick; with RespawnTime 0 it is back on the next one
        for (int i = 0; i < config.TickRate + 1; i++) world.Step(_ => new PlayerInput(0, 0, 0, 0, InputButtons.None));
        var p = world.Players[1];
        Assert.True(p.Alive);
        Assert.Equal((config.HealthMax, config.ShieldMax, config.ClipSize, config.ReserveSize), (p.Health, p.Shield, p.Clip, p.Reserve));
    }

    /// <summary>Re-simulates the target's movement to find where it stood at a past tick (the world keeps no public history).</summary>
    static Vector3 HistoryPosition(World world, SimConfig config, uint tick)
    {
        var replay = new World(config);
        replay.Spawn(1); replay.Spawn(2);
        while (replay.Tick < tick) replay.Step(id => id == 2 ? new PlayerInput(0, 1, 0, 0, InputButtons.None) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
        return replay.Players[2].Position;
    }
}
