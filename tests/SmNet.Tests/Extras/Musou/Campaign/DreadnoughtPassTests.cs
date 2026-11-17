using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The Dreadnought pass (owner, 2026-11-17; claude/musou-dreadnought-pass): his cannon's rounds go through the crosshair's point of aim (a shot's origin may lie 8 m from his eye, his camera being about 3 m from it),
/// his melee repeats by the marines' rule with a 0.2 s lead-in on a chained swing, his cleave and his stomp spheres are 1.5 times as long, and his gun cancels a swing 0.15 s after its hit.
/// </summary>
public class DreadnoughtPassTests
{
    static readonly WeaponSpec Cannon = new(DreadnoughtRules.CannonWargear, WeaponSlot.Ranged, TriggerKind.FullAuto, 101f, 0.1f, 140, 2_000_000, 2.5f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f) { AlwaysReady = true };
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };
    static readonly SimConfig Config = MusouLoadout.Prepare(TfwRig.Config with { Weapons = [.. TfwRig.Config.Weapons, Cannon], MusouRules = Still });
    static int Dread(SimConfig c) => c.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
    const int Human = TfwRig.Human;

    static World Landed(SimConfig? config = null)
    {
        config ??= Config;
        var w = TfwRig.War(null, config);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = Dread(config) }));
        TfwRig.Command(w, TfwCommand.ChangeClass);
        TfwRig.Steps(w, 3);
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, DreadnoughtRules.EntranceSeconds + 0.3f);
        w.Musou!.Frozen = true;
        return w;
    }

    static PlayerState Me(World w) => w.Players[Human];
    static void Step(World w, InputButtons b = InputButtons.None) => w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, b));
    static ushort Boy(World w, Vector3 at) { ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, at, 0f, 100000f); Assert.NotEqual(0, id); return id; }
    static float Health(World w, ushort id) => w.Musou!.TryGet(id, out var e) ? e.Health : 0f;

    // ---------------------------------------------------------------- where the cannon shoots

    /// <summary>His camera as the client builds it: the marine rig's offsets (about 0.6 m right, 2.5 m back, 2.0 m up) times 2.2, in his frame facing +Y; the film measured the camera ray 3.6 to 4.0 m from his eye, this one 3.1 m (level) to 4 m.</summary>
    static readonly Vector3 CameraBehind = new(1.3f, -5.5f, 4.4f);

    static (PlayerInput Input, Vector3 CameraPoint, Vector3 Direction) CrosshairShot(PlayerState me, SimConfig c, Vector3 aimAt)
    {
        var camera = me.Position + CameraBehind;
        var direction = Vector3.Normalize(aimAt - camera);
        var eye = Combat.EyePosition(me, c);
        var start = camera + direction * MathF.Max(0f, Vector3.Dot(eye - camera, direction));   // the camera ray's point nearest the eye (NetworkView.Converged)
        float yaw = MathF.Atan2(-direction.X, direction.Y), pitch = MathF.Asin(direction.Z);
        var input = new PlayerInput(0, 0f, 0f, yaw, InputButtons.Fire, pitch).WithShot(start, direction, eye);
        return (input, camera, direction);
    }

    /// <summary>The same config with the Dreadnought's body back on the core's 1.5 m cut (the before).</summary>
    static SimConfig OldCut(SimConfig c) => c with { Bodies = [.. c.Bodies.Select(b => b with { MaxShotOrigin = null })] };

    static float DistanceFromRay(Vector3 point, Vector3 origin, Vector3 direction) => Vector3.Cross(point - origin, direction).Length();

    [Fact]
    public void A_shot_of_his_leaves_the_camera_ray_and_goes_through_the_crosshairs_point_at_any_range_where_the_old_cut_ran_1_4_m_below()
    {
        var w = Landed();
        var me = Me(w);
        Assert.Equal(DreadnoughtRules.MaxShotOriginOffset, w.Config.For(me.Class).MaxShotOriginOffset);
        Assert.Equal(1.5f, w.Config.MaxShotOriginOffset);   // only his body carries the long cut
        var oldConfig = OldCut(w.Config);   // the core's cut
        foreach (float range in new[] { 6f, 12f, 40f })
        {
            var aim = me.Position + new Vector3(0.4f, range, 1.2f);
            var (input, camera, direction) = CrosshairShot(me, w.Config, aim);
            var origin = Combat.ShotOrigin(me, input, w.Config);
            var ray = Combat.ShotDirection(input, w.Config);
            float miss = DistanceFromRay(aim, origin, ray);
            Assert.True(miss < 0.02f, $"range {range}: the shot passes {miss:F3} m from the point of aim");
            Assert.True(DistanceFromRay(origin, camera, direction) < 0.02f, "the origin is on the camera ray");
            var (oldInput, _, _) = CrosshairShot(me, oldConfig, aim);
            float oldMiss = DistanceFromRay(aim, Combat.ShotOrigin(me, oldInput, oldConfig), Combat.ShotDirection(oldInput, oldConfig));
            if (range >= 12f) Assert.InRange(oldMiss, 0.5f, 2.0f);   // before: parallel to the crosshairs' ray, half a metre to 1.4 m off (a near, steep ray passes within the old cut)
        }
    }

    [Fact]
    public void A_marines_shot_is_unchanged_his_camera_passes_within_the_core_cut()
    {
        var c = MusouSimTests.Base;
        var me = new PlayerState(1, new Vector3(0f, 0f, 0f), Vector3.Zero, 0f, true);
        var camera = new Vector3(0.6f, -2.0f, 1.9f);   // a marine's: 0.3 m from his eye's height
        var aim = new Vector3(0f, 20f, 1.2f);
        var direction = Vector3.Normalize(aim - camera);
        var eye = Combat.EyePosition(me, c);
        var start = camera + direction * MathF.Max(0f, Vector3.Dot(eye - camera, direction));
        Assert.True((start - eye).Length() < c.MaxShotOriginOffset);
        var input = new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire).WithShot(start, direction, eye);
        Assert.True((Combat.ShotOrigin(me, input, c) - start).Length() < 1e-3f);
        Assert.Equal(1.5f, c.For(0).MaxShotOriginOffset);   // a marine's body has no cut of its own
        Assert.Equal(1.5f, MusouLoadout.Prepare(c).For(0).MaxShotOriginOffset);   // and Musou's Prepare leaves every class but his on the core's
    }

    [Fact]
    public void The_round_the_server_fires_goes_through_the_point_of_aim_the_old_cut_round_passed_below_it()
    {
        float Miss(SimConfig config, out float below)
        {
            var w = Landed(config);
            var me = Me(w);
            var aim = me.Position + new Vector3(0f, 25f, 1.1f);   // the crosshair on an Ork's chest 25 m ahead
            Boy(w, me.Position + new Vector3(0f, 25f, 0f));
            var (input, _, _) = CrosshairShot(me, w.Config, aim);
            w.Step(_ => input);
            var shot = w.LastShots.First(x => x.Shooter == Human);
            var ray = Vector3.Normalize(shot.End - shot.Origin);
            below = aim.Z - (shot.Origin + ray * Vector3.Dot(aim - shot.Origin, ray)).Z;   // how far under the aim point the round passes
            return DistanceFromRay(aim, shot.Origin, ray);
        }
        Assert.True(Miss(Config, out float below) < 0.05f, $"the round passes {below:F3} m under the point of aim");
        Assert.InRange(Miss(OldCut(Config), out float oldBelow), 0.4f, 2f);   // before: the cut origin, parallel to the crosshair's ray, a metre or so under it
        Assert.True(oldBelow > 0.4f, $"{oldBelow:F2} m under");
    }

    // ---------------------------------------------------------------- melee

    /// <summary>Ticks at which each swing begins while Melee is pressed at 10 Hz (a press every third tick), from the first press.</summary>
    static List<(float At, int Step)> SwingStarts(World w, int seconds)
    {
        var starts = new List<(float, int)>();
        int last = 0; float lastTime = 0f;
        for (int k = 0; k < 30 * seconds; k++)
        {
            bool press = k % 3 == 0;
            Step(w, press ? InputButtons.Melee : InputButtons.None);
            var me = Me(w);
            if (me.MeleeStep != 0 && (last == 0 || me.MeleeTime < lastTime)) starts.Add((k / 30f, me.MeleeStep));
            last = me.MeleeStep; lastTime = me.MeleeTime;
        }
        return starts;
    }

    [Fact]
    public void Mashing_Melee_starts_each_swing_on_the_tick_the_one_before_hits_and_a_chained_swing_skips_0_2_s_of_its_wind_up()
    {
        var w = Landed();
        var starts = SwingStarts(w, 8);
        Assert.True(starts.Count >= 9, $"{starts.Count} swings in 8 s");
        var light = DreadnoughtMelee.Light();
        for (int i = 1; i < starts.Count; i++)
        {
            float hit = light[starts[i - 1].Step - 1].HitTime - (i >= 2 ? DreadnoughtRules.ChainLeadIn : 0f);   // a chained swing starts 0.2 s into its clip
            float gap = starts[i].At - starts[i - 1].At;
            Assert.InRange(gap, hit - 0.001f, hit + 2.1f / 30f);   // the next swing begins within two ticks of the last one's hit (the press rhythm and the tick)
            Assert.NotEqual(starts[i - 1].Step, starts[i].Step);   // the other swing of the arm, as every Musou class's chain
        }
    }

    [Fact]
    public void His_gun_cancels_a_light_swing_a_sixth_of_a_second_after_its_hit_not_a_second_after_it()
    {
        foreach (var step in DreadnoughtMelee.Light())
        {
            Assert.Equal(step.HitTime + DreadnoughtRules.RangedAfterHit, step.RangedAt, 3);
            Assert.True(step.RangedAt < step.MoveAt + 0.35f);
        }
        foreach (var (data, fast) in DreadnoughtMelee.DataLight().Zip(DreadnoughtMelee.Light())) Assert.True(fast.RangedAt < data.RangedAt / DreadnoughtRules.MeleeSpeed);   // earlier than the data's tag at his speed
        var w = Landed();
        Step(w, InputButtons.Melee);
        Assert.True(Me(w).MeleeStep > 0);
        int fired = 0;
        for (int k = 0; k < 30; k++) { Step(w, InputButtons.Fire); if (Me(w).MeleeStep == 0) { fired = k; break; } }
        float cancelAt = (fired + 1) / 30f;
        Assert.InRange(cancelAt, 0.6f, 1.0f);   // the hit at 0.46 or 0.67 s plus 0.15 s, to a tick
        Assert.True(Me(w).Cooldown > 0f, "the round that cancelled the swing left");
    }

    // ---------------------------------------------------------------- cleave

    [Fact]
    public void His_cleave_is_1_5_times_the_ordinary_and_the_server_strikes_what_the_new_reach_covers()
    {
        var w = Landed();
        var step = DreadnoughtMelee.Light()[0];
        var ordinary = MusouCleaveShape.Of(w.Config, step, war: true);
        var his = MusouCleaveShape.Of(w.Config, step, war: true, dreadnought: true);
        Assert.Equal(ordinary.Reach * 1.5f, his.Reach, 3);
        Assert.Equal(ordinary.HalfAngleDegrees, his.HalfAngleDegrees);   // the reach only: the angle is the war's
        // an Ork 1.2 times the old reach ahead is struck, one 2.5 m past the new reach is not (the same world without the swing is the baseline: allies' fire and the level are in both)
        float Taken(bool swing, float ahead)
        {
            var world = Landed();
            var at = Me(world).Position;
            ushort id = Boy(world, at + new Vector3(0f, ahead, 0f));
            Step(world, swing ? InputButtons.Melee : InputButtons.None);
            TfwRig.Seconds(world, 1.2f);
            return 100000f - Health(world, id);
        }
        float near = ordinary.Reach * 1.2f, far = his.Reach + 2.5f;
        Assert.True(Taken(true, near) - Taken(false, near) >= 90f, $"the Ork at {near:F1} m (old reach {ordinary.Reach:F1}, new {his.Reach:F1})");
        Assert.True(Taken(true, far) - Taken(false, far) < 1f, $"the Ork at {far:F1} m");
    }

    [Fact]
    public void A_chained_swings_hit_comes_0_2_s_sooner_so_his_hits_follow_each_0_36_s_apart_against_0_56_before()
    {
        var w = Landed();
        var starts = SwingStarts(w, 8);
        var light = DreadnoughtMelee.Light();
        Assert.All(light, m => Assert.Equal(DreadnoughtRules.ChainLeadIn, m.ChainLeadIn));
        float before = light.Average(m => m.HitTime), after = light.Average(m => m.HitTime - DreadnoughtRules.ChainLeadIn);
        Assert.InRange(before, 0.5f, 0.6f);   // 0.56 s between hits in a chain without the lead-in
        Assert.InRange(after, 0.33f, 0.39f);   // the knife's: 0.33 s
        var chained = starts.Skip(2).Zip(starts.Skip(3), (a, b) => b.At - a.At).ToList();
        Assert.InRange(chained.Average(), 0.30f, 0.42f);
        Assert.All(light, m => Assert.True(m.ChainLeadIn < m.HitTime));   // the hit still lands
    }

    [Fact]
    public void The_stomp_and_the_slam_reach_15_m_and_a_footfall_stays_at_10_m()
    {
        Assert.Equal(15f, DreadnoughtMelee.Stomp.Radius);
        Assert.Equal(15f, DreadnoughtMelee.Slam.Radius);
        Assert.Equal(10f, DreadnoughtMelee.Footfall.Radius);
        float Taken(bool stomp)
        {
            var world = Landed();
            var at = Me(world).Position;
            ushort id = Boy(world, at + new Vector3(0f, 12.5f, 0f));   // past the data's 10 m, inside 15
            Step(world, stomp ? InputButtons.MeleeHeavy : InputButtons.None);
            TfwRig.Seconds(world, 1.6f);
            return 100000f - Health(world, id);
        }
        Assert.True(Taken(true) - Taken(false) >= 40f, "the stomp's 50 reaches an Ork 12.5 m away");
    }

    static CollisionWorld Wall(params Vector3[] corners)
    {
        var wall = new StaticMesh(corners, [0, 1, 2, 0, 2, 3]);
        return new CollisionWorld(wall, wall);
    }

    [Fact]
    public void A_wall_3_m_behind_him_does_not_move_the_origin_and_a_pillar_beside_him_slides_it_along_the_ray_not_back_to_the_eye()
    {
        var w = Landed();
        var me = Me(w);
        var aim = me.Position + new Vector3(0.4f, 20f, 1.2f);
        var (input, camera, direction) = CrosshairShot(me, w.Config, aim);
        var eye = Combat.EyePosition(me, w.Config);
        float y = me.Position.Y, x = me.Position.X;
        // a wall 3 m behind him (the game's camera is pulled in against it): not between the eye and the ray's nearest point
        var behind = Wall(new(x - 20, y - 3f, -1), new(x + 20, y - 3f, -1), new(x + 20, y - 3f, 10), new(x - 20, y - 3f, 10));
        var origin = Combat.ShotOrigin(me, input, w.Config, behind);
        Assert.True(DistanceFromRay(origin, camera, direction) < 0.02f && (origin - eye).Length() > 2f, "the origin stays on the camera ray");
        Assert.True(DistanceFromRay(aim, origin, Combat.ShotDirection(input, w.Config)) < 0.02f);
        // a pillar between the eye and the ray's nearest point (0.7 m to his right, 2 m long): the fallback was the eye, a round about 2 m under the crosshair
        var pillar = Wall(new(x + 0.7f, y - 1f, -1), new(x + 0.7f, y + 1f, -1), new(x + 0.7f, y + 1f, 10), new(x + 0.7f, y - 1f, 10));
        var slid = Combat.ShotOrigin(me, input, w.Config, pillar);
        Assert.True(DistanceFromRay(slid, camera, direction) < 0.02f, "the origin slid along the camera ray");
        Assert.True((slid - eye).Length() > 2f && DistanceFromRay(aim, slid, Combat.ShotDirection(input, w.Config)) < 0.02f, "the round still passes the point of aim");
        // a marine keeps the core's fallback to the eye
        var c = MusouSimTests.Base;
        var marine = new PlayerState(1, me.Position, Vector3.Zero, 0f, true);
        var marineEye = Combat.EyePosition(marine, c);
        var marineInput = new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire).WithShot(marineEye + new Vector3(1.0f, 0f, 0.3f), Vector3.UnitY, marineEye);
        Assert.Equal(marineEye, Combat.ShotOrigin(marine, marineInput, c, pillar));
    }


    [Fact]
    public void The_pass_raises_the_versions_and_leaves_the_protocol()
    {
        Assert.True(MusouSim.Version >= 63u);
        Assert.True(TfwRules.Version >= 88u);
    }
}
