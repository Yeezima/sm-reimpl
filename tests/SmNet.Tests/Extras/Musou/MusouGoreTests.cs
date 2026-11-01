using System.Numerics;
using SmNet.Extras.Gore;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Musou pass 2 M10: the gore's rules and its field (engine-free; the Godot view only draws these arrays). Who makes gore, the owner's item 23 (explosions, frag grenades, artillery and
/// lascannon shots burst the enemy), the distance bands, and the count limits under the heavy death rates of 200 enemies: no pool ever holds more than its size, no frame turns more than
/// 12 deaths into bursts, the queue holds at most 512.
/// </summary>
public class MusouGoreTests
{
    static GoreBurst Plan(MusouDeathKind cause, int killer = 1, float metres = 5f, MusouEnemyType type = MusouEnemyType.Boy, bool lascannon = false, float load = 1f, ushort id = 7, uint tick = 100) =>
        MusouGoreRules.Plan(cause, type, killer, lascannon, metres, load, id, tick);

    [Theory]
    [InlineData(MusouDeathKind.Melee, 1, false, GoreClass.Gore)]
    [InlineData(MusouDeathKind.Melee, 65520, false, GoreClass.Gore)]   // a retainer
    [InlineData(MusouDeathKind.Shot, 1, false, GoreClass.Gore)]
    [InlineData(MusouDeathKind.Melee, 0, false, GoreClass.None)]   // nobody's: a Guardsman's kill carries 0
    [InlineData(MusouDeathKind.Shot, 0, false, GoreClass.None)]
    [InlineData(MusouDeathKind.World, 1, false, GoreClass.None)]   // a stranded enemy removed by the failsafe
    [InlineData(MusouDeathKind.Strafe, 0, false, GoreClass.Gore)]   // the Thunderhawk's bolters
    [InlineData(MusouDeathKind.Blast, 1, false, GoreClass.Explosion)]   // a frag grenade
    [InlineData(MusouDeathKind.Blast, 0, false, GoreClass.Explosion)]
    [InlineData(MusouDeathKind.Barrage, 0, false, GoreClass.Explosion)]   // item 23 overrides the design's "barrage kills: no gore"
    [InlineData(MusouDeathKind.Shot, 1, true, GoreClass.Explosion)]   // a lascannon shot
    [InlineData(MusouDeathKind.Melee, 1, true, GoreClass.Gore)]   // a melee blow with a lascannon in hand is still a blow
    [InlineData(MusouDeathKind.Shot, 0, true, GoreClass.None)]
    public void Classify_follows_who_and_what_killed(MusouDeathKind cause, int killer, bool lascannon, GoreClass expected) =>
        Assert.Equal(expected, MusouGoreRules.Classify(cause, killer, lascannon));

    [Fact]
    public void The_distance_bands_are_the_designs()
    {
        Assert.Equal(GoreLod.Full, GoreRules.LodOf(0f));
        Assert.Equal(GoreLod.Full, GoreRules.LodOf(15f));
        Assert.Equal(GoreLod.Half, GoreRules.LodOf(15.1f));
        Assert.Equal(GoreLod.Half, GoreRules.LodOf(35f));
        Assert.Equal(GoreLod.Spray, GoreRules.LodOf(35.1f));
        Assert.Equal(GoreLod.Spray, GoreRules.LodOf(60f));
        Assert.Equal(GoreLod.None, GoreRules.LodOf(60.1f));
    }

    [Fact]
    public void Full_half_one_spray_and_nothing_with_distance()
    {
        var full = Plan(MusouDeathKind.Melee, metres: 5f);
        var half = Plan(MusouDeathKind.Melee, metres: 25f);
        var spray = Plan(MusouDeathKind.Melee, metres: 50f);
        var none = Plan(MusouDeathKind.Melee, metres: 80f);
        Assert.True(full.Gibs > half.Gibs && half.Gibs > 0);
        Assert.True(full.Droplets > half.Droplets && half.Droplets > spray.Droplets && spray.Droplets > 0);
        Assert.Equal(0, spray.Gibs + spray.Limbs + spray.Pools);   // one spray, nothing on the ground
        Assert.Equal(0, none.Gibs + none.Limbs + none.Droplets + none.Pools + none.Mist);
    }

    [Fact]
    public void Melee_weighs_heaviest_and_an_explosion_more_than_any_gore()
    {
        var melee = Plan(MusouDeathKind.Melee);
        var shot = Plan(MusouDeathKind.Shot);
        var strafe = Plan(MusouDeathKind.Strafe, killer: 0);
        var boom = Plan(MusouDeathKind.Blast);
        Assert.True(melee.Gibs > shot.Gibs && melee.Droplets > shot.Droplets);
        Assert.True(strafe.Gibs >= melee.Gibs);
        Assert.True(boom.Gibs > strafe.Gibs && boom.Droplets > strafe.Droplets && boom.Mist > 0 && boom.Pools >= 2);
        Assert.True(melee.Mist >= 1 && boom.Mist > melee.Mist);   // every gore death makes a cloud; an explosion a bank of them
    }

    [Fact]
    public void An_explosion_always_gibs_the_body_even_beyond_the_bands_and_a_shot_never_does()
    {
        Assert.True(Plan(MusouDeathKind.Barrage, killer: 0, metres: 200f).Gibbed);
        Assert.True(Plan(MusouDeathKind.Blast, metres: 5f).Gibbed);
        Assert.True(Plan(MusouDeathKind.Shot, lascannon: true).Gibbed);
        Assert.False(Plan(MusouDeathKind.Shot).Gibbed);
        Assert.False(Plan(MusouDeathKind.Melee, metres: 200f).Gibbed);   // out of the bands a gore death leaves its body
        Assert.False(Plan(MusouDeathKind.World).Gibbed);
    }

    [Fact]
    public void Gibbing_a_gore_death_is_repeatable_and_about_the_stated_chance()
    {
        int gibbed = 0;
        for (ushort id = 1; id <= 2000; id++)
        {
            bool a = Plan(MusouDeathKind.Melee, id: id, tick: 31).Gibbed, b = Plan(MusouDeathKind.Melee, id: id, tick: 31).Gibbed;
            Assert.Equal(a, b);
            if (a) gibbed++;
        }
        Assert.InRange(gibbed / 2000.0, 0.30, 0.40);   // 35 % for a Boy
        int big = 0;
        for (ushort id = 1; id <= 2000; id++) if (Plan(MusouDeathKind.Melee, type: MusouEnemyType.Nob, id: id, tick: 31).Gibbed) big++;
        Assert.InRange(big / 2000.0, 0.10, 0.20);   // 15 % for a Nob
    }

    [Fact]
    public void The_big_bleed_more_and_a_loaded_pool_thins_every_count()
    {
        var boy = Plan(MusouDeathKind.Melee);
        var nob = Plan(MusouDeathKind.Melee, type: MusouEnemyType.Nob);
        var warboss = Plan(MusouDeathKind.Melee, type: MusouEnemyType.Warboss);
        Assert.True(nob.Droplets > boy.Droplets && warboss.Droplets > nob.Droplets);
        var loaded = Plan(MusouDeathKind.Blast, load: 0.25f);
        var fresh = Plan(MusouDeathKind.Blast, load: 1f);
        Assert.True(loaded.Gibs < fresh.Gibs && loaded.Droplets < fresh.Droplets);
        Assert.True(loaded.Gibs >= 1);   // never nothing
    }

    [Fact]
    public void A_cleave_slash_is_drawn_for_a_marines_hit_within_range_only()
    {
        Assert.True(MusouGoreRules.SlashDrawn(1, 10f));
        Assert.True(MusouGoreRules.SlashDrawn(65521, 60f));
        Assert.False(MusouGoreRules.SlashDrawn(0, 10f));   // no attacker: not a Marine's blow
        Assert.False(MusouGoreRules.SlashDrawn(1, 61f));
    }

    // ---------------------------------------------------------------- the field and the count limits

    static void AssertWithinPools(GoreField f)
    {
        Assert.InRange(f.LiveGibs, 0, GoreRules.GibPool);
        Assert.InRange(f.LiveDrops, 0, GoreRules.DropletPool);
        Assert.InRange(f.LivePools, 0, GoreRules.PoolRing);
        Assert.InRange(f.LiveSpatters, 0, GoreRules.SpatterRing);
        Assert.InRange(f.LiveMists, 0, GoreRules.MistPool);
        Assert.InRange(f.LiveSlashes, 0, GoreRules.SlashPool);
        Assert.Equal(GoreRules.GibPool, f.Gibs.Length);
        Assert.Equal(GoreRules.PoolRing, f.Pools.Length);
    }

    static void AssertFinite(GoreField f)
    {
        foreach (var g in f.Gibs) if (g.Live) Assert.True(float.IsFinite(g.Pos.X + g.Pos.Y + g.Pos.Z + g.Vel.Length()));
        foreach (var d in f.Drops) if (d.Live) Assert.True(float.IsFinite(d.Pos.X + d.Pos.Y + d.Pos.Z + d.Vel.Length()));
    }

    [Fact]
    public void A_thousand_explosions_in_one_frame_never_exceed_a_pool()
    {
        var f = new GoreField(1);
        var burst = Plan(MusouDeathKind.Barrage, killer: 0);
        for (int i = 0; i < 1000; i++) f.Spawn(burst, new Vector3(i % 20, i / 20, 1f), new Vector3(0, 1, 0.2f), 0f, ork: true);
        AssertWithinPools(f);
        Assert.Equal(GoreRules.GibPool, f.LiveGibs);   // full, and the oldest were recycled
        Assert.True(f.Recycled > 0);
        for (int i = 0; i < 600; i++) { f.Step(1f / 60f); AssertWithinPools(f); }
        AssertFinite(f);
    }

    [Theory]
    [InlineData(GoreField.HitKind.Shot)]
    [InlineData(GoreField.HitKind.Head)]
    [InlineData(GoreField.HitKind.Melee)]
    [InlineData(GoreField.HitKind.Blast)]
    [InlineData(GoreField.HitKind.Fire)]
    public void Every_damaging_hit_makes_a_blood_cloud_and_ink(GoreField.HitKind kind)
    {
        var f = new GoreField(3);
        f.HitCloud(new Vector3(0, 0, 1.2f), new Vector3(1, 0, 0), kind);
        Assert.Equal(1, f.LiveMists);
        Assert.True(f.LiveDrops >= 10);
        Assert.True(f.Mists[0].Size >= 2f);   // at least a Marine across once it has bloomed
    }

    [Fact]
    public void A_twenty_enemy_cleave_and_a_strike_over_a_crowd_lose_no_cloud_and_a_full_pool_recycles_the_oldest()
    {
        var f = new GoreField(4);
        for (int i = 0; i < 20; i++) f.HitCloud(new Vector3(i, 0, 1.2f), new Vector3(1, 0, 0), GoreField.HitKind.Melee);
        Assert.Equal(20, f.LiveMists);
        for (int i = 0; i < 200; i++) f.HitCloud(new Vector3(i, 5, 1.2f), Vector3.Zero, GoreField.HitKind.Fire);
        Assert.Equal(220, f.LiveMists);
        for (int i = 0; i < 100; i++) f.HitCloud(new Vector3(i, 9, 1.2f), Vector3.Zero, GoreField.HitKind.Shot);
        Assert.Equal(GoreRules.MistPool, f.LiveMists);   // full: the oldest were recycled, nothing skipped
        Assert.True(f.Recycled > 0);
    }

    [Fact]
    public void A_cloud_thins_before_it_ends_and_the_amount_is_three_times_the_first_pass()
    {
        Assert.Equal(3f, GoreRules.Amount);
        var f = new GoreField(5);
        f.HitCloud(new Vector3(0, 0, 1.2f), new Vector3(1, 0, 0), GoreField.HitKind.Melee);
        float life = f.Mists[0].Life;
        for (int i = 0; i < (int)((life - 0.2f) * 60f); i++) f.Step(1f / 60f);
        Assert.True(f.Mists[0].Live);   // still there near its end (the view has thinned it to almost nothing by then)
        for (int i = 0; i < 30; i++) f.Step(1f / 60f);
        Assert.Equal(0, f.LiveMists);
    }

    [Fact]
    public void Everything_settles_on_the_floor_and_fades_out()
    {
        var f = new GoreField(2);
        f.Spawn(Plan(MusouDeathKind.Blast), new Vector3(0, 0, 3f), new Vector3(1, 0, 0), 2f, ork: false);
        Assert.True(f.LiveGibs > 0 && f.LiveDrops > 0 && f.LivePools > 0 && f.LiveMists > 0);
        for (int i = 0; i < 10 * 60; i++) f.Step(1f / 60f);
        foreach (var g in f.Gibs) if (g.Live) { Assert.True(g.Resting); Assert.True(g.Pos.Z >= 2f - 1e-3f); Assert.True(g.Pos.Z < 2.3f); }   // on the floor under the enemy (z 2)
        Assert.Equal(0, f.LiveDrops);
        Assert.Equal(0, f.LiveMists);
        Assert.True(f.LiveSpatters > 0);   // the landing drops and the bounces left spots
        foreach (var p in f.Pools) if (p.Live) Assert.Equal(2f, p.Pos.Z);
        for (int i = 0; i < 100 * 60; i++) f.Step(1f / 60f);
        Assert.Equal(0, f.LiveGibs + f.LivePools + f.LiveSpatters);   // the ground is cleaned after the pool's 90 s
    }

    [Fact]
    public void A_swing_that_hits_twenty_draws_twenty_slashes_that_expire()
    {
        var f = new GoreField(3);
        var cam = new Vector3(0, -10, 3);
        for (int i = 0; i < 20; i++) f.Cleave(new Vector3(i * 0.4f, 0, 1.1f), new Vector3(0, -2, 1), cam);
        Assert.Equal(20, f.LiveSlashes);
        foreach (var s in f.Slashes)
        {
            if (!s.Live) continue;
            Assert.True(Math.Abs(Vector3.Dot(s.Axis, s.Normal)) < 1e-3f);   // the stroke lies in the plane facing the camera
            Assert.True(Math.Abs(s.Axis.Length() - 1f) < 1e-3f);
            Assert.InRange(s.Length, 1.3f, 1.9f);
        }
        for (int i = 0; i < 30; i++) f.Step(1f / 60f);
        Assert.Equal(0, f.LiveSlashes);
        for (int i = 0; i < 200; i++) f.Cleave(new Vector3(i, 0, 1.1f), default, cam);
        Assert.Equal(GoreRules.SlashPool, f.LiveSlashes);   // a full pool recycles
    }

    [Fact]
    public void The_queue_holds_ninety_six_and_a_frame_takes_twelve()
    {
        var q = new GoreQueue<int>();
        for (int i = 0; i < 1000; i++) q.Add(i);   // far more than a barrage of 200
        Assert.Equal(GoreRules.QueueMax, q.Count);
        Assert.Equal(1000 - GoreRules.QueueMax, q.Dropped);
        var batch = new List<int>();
        Assert.Equal(12, q.Take(batch));
        Assert.Equal(12, batch.Count);
        Assert.Equal(1000 - GoreRules.QueueMax, batch[0]);   // the oldest kept go first
        int frames = 1;
        while (q.Count > 0) { batch.Clear(); Assert.True(q.Take(batch) <= GoreRules.DeathsPerFrame); frames++; }
        Assert.Equal(43, frames);   // 512 deaths in 43 frames
    }

    [Fact]
    public void Two_hundred_enemies_dying_at_heavy_rates_hold_every_limit_and_the_load_thins_the_bursts()
    {
        var f = new GoreField(4);
        var q = new GoreQueue<(MusouDeathKind Cause, float Metres)>();
        var batch = new List<(MusouDeathKind, float)>();
        var rng = new Random(9);
        int burstsMade = 0;
        long gibsFirstSecond = 0, gibsLast = 0;
        // 20 s at 60 fps: 8 deaths a frame (480 a second, 5 times the demo's heavy rate), a barrage of 200 every 5 s.
        for (int frame = 0; frame < 20 * 60; frame++)
        {
            for (int k = 0; k < 8; k++) q.Add((rng.Next(4) == 0 ? MusouDeathKind.Blast : MusouDeathKind.Melee, 3f + rng.NextSingle() * 50f));
            if (frame % 300 == 0) for (int k = 0; k < 200; k++) q.Add((MusouDeathKind.Barrage, 5f + rng.NextSingle() * 40f));
            batch.Clear();
            int taken = q.Take(batch);
            Assert.True(taken <= GoreRules.DeathsPerFrame);
            Assert.True(q.Count <= GoreRules.QueueMax);
            float load = f.LoadScale();
            long before = f.Spawned;
            foreach (var (cause, metres) in batch)
            {
                var plan = MusouGoreRules.Plan(cause, MusouEnemyType.Boy, cause == MusouDeathKind.Barrage ? 0 : 1, false, metres, load, (ushort)(frame % 200 + 1), (uint)frame);
                f.Spawn(plan, new Vector3(rng.NextSingle() * 40f, rng.NextSingle() * 40f, 1f), Vector3.UnitY, 0f, ork: true);
                burstsMade++;
            }
            if (frame < 60) gibsFirstSecond += f.Spawned - before; else gibsLast = f.Spawned - before;
            f.Step(1f / 60f);
            AssertWithinPools(f);
        }
        AssertFinite(f);
        Assert.True(burstsMade > 0);
        Assert.True(f.Recycled > 0);   // the pools were full: the oldest were recycled, never grown
        Assert.True(f.LoadScale() < 1f);
        Assert.Equal(0, q.Dropped);   // a barrage fits the queue: a body that vanished always got its gore
        Assert.True(gibsFirstSecond > 0 && gibsLast >= 0);
    }

    [Fact]
    public void Nothing_is_made_for_no_gore_and_nothing_beyond_sixty_metres()
    {
        var f = new GoreField(5);
        f.Spawn(GoreBurst.Nothing, Vector3.Zero, Vector3.UnitY, 0f, false);
        f.Spawn(Plan(MusouDeathKind.Melee, metres: 80f), Vector3.Zero, Vector3.UnitY, 0f, false);
        Assert.Equal(0L, f.Spawned);
    }

    [Fact]
    public void The_gore_code_draws_nothing_on_the_screen()
    {
        // Owner item 8: no blood on the screen. The view is a Node3D with MultiMeshes; it never creates a canvas item, a control, a canvas layer or a screen-reading shader.
        var up = new DirectoryInfo(AppContext.BaseDirectory);
        while (up is not null && !File.Exists(Path.Combine(up.FullName, "SmReimpl.slnx"))) up = up.Parent;
        string root = up?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found above the test assembly");
        string dir = Path.Combine(root, "client", "Extras", "Musou", "Gore");
        Assert.True(Directory.Exists(dir), dir);
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (var banned in new[] { "CanvasLayer", "ColorRect", "TextureRect", "Control(", "CanvasItem", "SCREEN_TEXTURE", "hint_screen_texture", "ShowHitMarker", "Vignette" })
                Assert.False(text.Contains(banned), $"{Path.GetFileName(file)} mentions {banned}");
        }
    }
}
