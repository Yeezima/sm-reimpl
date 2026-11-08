using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode's plasma (the owner, 2026-11-08: every plasma weapon pierces all it passes through and burns it): a shot or ball through five Orks hits all five at full damage, the map still stops it, a burn deals
/// half the hit in six ticks 0.5 s apart, a new hit renews it and never stacks, a boss takes a quarter of a tick, the kill is the shooter's, the clients get a Burn record; the bolter is as it was, and
/// Classic PvP's plasma still stops on the first player.
/// </summary>
public class TfwPlasmaTests
{
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly WeaponSpec Gun = new("sm_mp_ranged_plasma_gun", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 1f, 1f, 0.5f, 0.5f);
    static readonly ProjectileSpec Ball = new("ebps\\test\\plasma", 40f, 5f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, null,
        new SplashSpec(1.5f, Plain with { Damage = 1f }, [new(0f, 1f), new(1.5f, 1f)], false, false, "target_all"));
    static readonly WeaponSpec BallGun = Gun with { Name = "sm_mp_exotic_plasma_cannon", Delivery = DeliveryKind.Projectile, Projectile = Ball, ShotCooldown = 1f };
    static readonly WeaponSpec Bolter = Gun with { Name = "sm_mp_ranged_bolter" };
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    const int Human = 1;

    static World War(int primary, Vector3 at, TfwCombatRules? rules = null)
    {
        var c = new ClassLoadout("sm - tactical", primary, -1, [Light]) { PrimaryChoices = [primary], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        var config = MusouSimTests.Base with
        {
            Weapons = [Gun, BallGun, Bolter], Thrown = [], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], Extras = ContentExtras.Musou, MusouRules = Still, RespawnTime = 60f,
        };
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, at, 0f);
        Tick(w);
        w.Musou!.Frozen = true;
        if (rules is not null) w.Musou.War = rules;
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    static void Shoot(World w)
    {
        Tick(w, InputButtons.Fire);
        for (int k = 0; k < 20 && w.LastShots.Count == 0 && w.Projectiles.Count == 0; k++) Tick(w, InputButtons.Fire);
        Assert.True(w.LastShots.Count > 0 || w.Projectiles.Count > 0);
    }

    static ushort Boy(World w, Vector3 at, MusouEnemyType type = MusouEnemyType.Boy, float health = 1000f)
    {
        ushort id = w.Musou!.Spawn(type, at, 0f, health);
        Assert.NotEqual(0, id);
        return id;
    }

    static float HealthOf(World w, ushort id) => w.Musou!.TryGet(id, out var e) ? e.Health : 0f;
    static Vector3 Me(World w) => w.Players[Human].Position;

    // ---------------------------------------------------------------- the pierce

    [Fact]
    public void A_plasma_shot_goes_through_all_five_enemies_each_at_full_damage()
    {
        var w = War(0, new Vector3(0f, -60f, 0f));
        var line = Enumerable.Range(0, 5).Select(k => Boy(w, Me(w) + new Vector3(0f, 6f + k * 2f, 0f))).ToArray();
        Shoot(w);
        var lost = line.Select(id => 1000f - HealthOf(w, id)).ToArray();
        Assert.All(lost, l => Assert.True(l > 0f, string.Join(", ", lost)));   // the bolter's cap of three would have left the rest alone
        Assert.All(lost, l => Assert.Equal(lost[0], l, 3));                    // no fall-off from one to the next
        Assert.Equal(5, w.TfwBestPierce);
    }

    [Fact]
    public void A_bolter_shot_still_stops_at_its_cap()
    {
        var w = War(2, new Vector3(0f, -60f, 0f));
        var line = Enumerable.Range(0, 5).Select(k => Boy(w, Me(w) + new Vector3(0f, 6f + k * 2f, 0f))).ToArray();
        Shoot(w);
        Assert.Equal(3, line.Count(id => HealthOf(w, id) < 1000f));
        Assert.Equal(0, w.TfwBurning);
    }

    [Fact]
    public void The_map_still_stops_a_plasma_shot()
    {
        var w = War(0, new Vector3(0f, 100f, 0f));
        var inside = new[] { 106f, 110f, 116f }.Select(y => Boy(w, new Vector3(0f, y, 0f))).ToArray();
        var outside = new[] { 124f, 128f }.Select(y => Boy(w, new Vector3(0f, y, 0f))).ToArray();
        Shoot(w);
        Assert.All(inside, id => Assert.True(HealthOf(w, id) < 1000f));
        Assert.All(outside, id => Assert.Equal(1000f, HealthOf(w, id)));
    }

    [Fact]
    public void A_plasma_ball_goes_through_every_enemy_and_bursts_on_the_wall_not_on_an_Ork()
    {
        var w = War(1, new Vector3(0f, 60f, 0f));
        var line = Enumerable.Range(0, 5).Select(k => Boy(w, Me(w) + new Vector3(0f, 6f + k * 3f, 0f))).ToArray();
        Shoot(w);
        DetonationEvent? burst = null;
        for (int k = 0; k < 200 && burst is null; k++)
        {
            Tick(w);
            if (w.LastDetonations.Count > 0) burst = w.LastDetonations[0];
        }
        Assert.NotNull(burst);
        Assert.InRange(burst!.Value.Position.Y, 115f, 121f);   // the wall at y = 120, not the third Ork's
        var lost = line.Select(id => 1000f - HealthOf(w, id)).ToArray();
        Assert.All(lost, l => Assert.True(l >= 20f, string.Join(", ", lost)));   // the ball's direct hit on each (20), the burn on top
        Assert.Equal(5, w.TfwBurnsSet);   // every one of the five caught fire
        Assert.Equal(5, w.TfwBurning);
    }

    [Fact]
    public void With_the_switch_off_the_ball_bursts_on_the_enemy_after_its_cap()
    {
        var w = War(1, new Vector3(0f, 60f, 0f), TfwCombatRules.Default with { PlasmaPierces = false });
        var me = Me(w);
        for (int k = 0; k < 7; k++) Boy(w, me + new Vector3(0f, 6f + k * 3f, 0f));
        Shoot(w);
        DetonationEvent? burst = null;
        for (int k = 0; k < 100 && burst is null; k++)
        {
            Tick(w);
            if (w.LastDetonations.Count > 0) burst = w.LastDetonations[0];
        }
        Assert.NotNull(burst);
        Assert.InRange(burst!.Value.Position.Y - me.Y, 20f, 22.5f);   // the sixth of the line: the cannon's cap of five, then the one that stops it
        Assert.Equal(0, w.TfwBurning);
    }

    // ---------------------------------------------------------------- the burn

    /// <summary>Ticks the world for <paramref name="seconds"/>; the largest health a single tick took off <paramref name="id"/>.</summary>
    static float Run(World w, ushort id, float seconds)
    {
        float most = 0f;
        for (int k = 0; k < (int)MathF.Round(seconds * w.Config.TickRate); k++)
        {
            float before = HealthOf(w, id);
            Tick(w);
            most = MathF.Max(most, before - HealthOf(w, id));
        }
        return most;
    }

    [Fact]
    public void A_burn_deals_half_the_hit_in_six_ticks_half_a_second_apart_and_ends()
    {
        var w = War(0, new Vector3(0f, -60f, 0f));
        var war = w.Musou!.War!;
        ushort id = Boy(w, Me(w) + new Vector3(0f, 6f, 0f));
        Shoot(w);
        float hit = 1000f - HealthOf(w, id);
        Assert.True(hit > 0f);
        Assert.Equal(1, w.TfwBurning);
        Assert.Equal(6, war.PlasmaBurnTicks);
        // Tick by tick: count the ticks that took health and when.
        float last = HealthOf(w, id);
        var at = new List<int>();
        for (int k = 1; k <= 4 * w.Config.TickRate; k++)
        {
            Tick(w);
            float now = HealthOf(w, id);
            if (now < last) at.Add(k);
            last = now;
        }
        Assert.Equal(6, at.Count);
        Assert.Equal(6, w.TfwBurnTicksDealt);
        Assert.Equal(0, w.TfwBurning);
        Assert.Equal(hit * 0.5f, 1000f - hit - last, 2);   // 50 % of the hit in all
        for (int k = 1; k < at.Count; k++) Assert.InRange(at[k] - at[k - 1], (int)(0.5f * w.Config.TickRate) - 1, (int)(0.5f * w.Config.TickRate) + 1);
    }

    [Fact]
    public void The_ticks_are_quiet_no_hit_event_and_no_marker_per_tick()
    {
        var w = War(0, new Vector3(0f, -60f, 0f));
        Boy(w, Me(w) + new Vector3(0f, 6f, 0f));
        Shoot(w);
        int hits = 0;
        for (int k = 0; k < 4 * w.Config.TickRate; k++)
        {
            Tick(w);
            hits += w.Musou!.Events.Count(e => e.Kind == MusouEventKind.Hit);
        }
        Assert.True(w.TfwBurnTicksDealt >= 6);
        Assert.Equal(0, hits);
    }

    [Fact]
    public void A_new_hit_renews_the_burn_and_never_stacks_it()
    {
        var w = War(0, new Vector3(0f, -60f, 0f));
        ushort id = Boy(w, Me(w) + new Vector3(0f, 6f, 0f));
        Shoot(w);
        float hit = 1000f - HealthOf(w, id);
        Run(w, id, 1.2f);   // a tick or two in
        Assert.InRange(w.TfwBurnTicksDealt, 1, 2);
        int hits = w.TfwBurnsSet;
        for (int k = 0; k < 2 * w.Config.TickRate && w.LastShots.Count == 0; k++) Tick(w, InputButtons.Fire);   // the second shot
        Assert.Equal(hits + 1, w.TfwBurnsSet);
        Assert.Equal(1, w.TfwBurning);   // still one burn
        int before = w.TfwBurnTicksDealt;
        float oneTick = hit * 0.5f / 6f;
        float most = Run(w, id, 5f);
        Assert.InRange(most, oneTick * 0.95f, oneTick * 1.05f);   // a tick is never doubled
        Assert.Equal(0, w.TfwBurning);
        Assert.Equal(before + 6, w.TfwBurnTicksDealt);   // the renewal gives the full six again, not a sum of two burns
    }

    [Fact]
    public void A_boss_takes_a_quarter_of_each_tick()
    {
        var w = War(0, new Vector3(0f, -60f, 0f));
        ushort boy = Boy(w, Me(w) + new Vector3(0f, 6f, 0f));
        ushort boss = Boy(w, Me(w) + new Vector3(0f, 8f, 0f), MusouEnemyType.BossNob, 100000f);
        Shoot(w);
        float boyHit = 1000f - HealthOf(w, boy), bossHit = 100000f - HealthOf(w, boss);
        Run(w, boy, 4f);
        float boyBurn = 1000f - boyHit - HealthOf(w, boy), bossBurn = 100000f - bossHit - HealthOf(w, boss);
        Assert.Equal(boyHit * 0.5f, boyBurn, 2);
        Assert.InRange(bossBurn, bossHit * 0.5f * w.Musou!.War!.PlasmaBurnBossShare * 0.98f, bossHit * 0.5f * w.Musou.War.PlasmaBurnBossShare * 1.02f);   // a quarter of the Boy's (float noise on 100000 health)
    }

    [Fact]
    public void A_kill_by_the_burn_is_the_shooters()
    {
        var w = War(0, new Vector3(0f, -60f, 0f));
        var spot = Me(w) + new Vector3(0f, 6f, 0f);
        ushort decoy = Boy(w, spot);
        Shoot(w);
        float hit = 1000f - HealthOf(w, decoy);
        w.Musou!.Remove(decoy);
        ushort victim = Boy(w, spot, health: hit * 1.2f);   // the hit leaves a fifth of it, the burn takes it in three ticks
        Shoot(w);
        Assert.InRange(HealthOf(w, victim), 0.01f, hit);
        MusouEvent? death = null;
        for (int k = 0; k < 3 * w.Config.TickRate && death is null; k++)
        {
            Tick(w);
            foreach (var e in w.Musou.Events) if (e.Kind == MusouEventKind.Death && e.Enemy == victim) death = e;
        }
        Assert.NotNull(death);
        Assert.Equal(Human, death!.Value.Player);
        Assert.Equal((byte)MusouDeathKind.Shot, death.Value.Value);
        Assert.Equal(0, w.TfwBurning);
    }

    // ---------------------------------------------------------------- the clients

    [Fact]
    public void The_clients_get_a_burn_record_for_the_enemy_and_it_survives_the_wire()
    {
        var w = War(0, new Vector3(0f, -60f, 0f));
        ushort id = Boy(w, Me(w) + new Vector3(0f, 6f, 0f));
        Shoot(w);
        var record = Assert.Single(w.Tfw!.Fire.Events, f => f.Kind == TfwFireKind.Burn);
        Assert.Equal(id, record.Subject);
        Assert.True(MathF.Abs(record.At.Y - Me(w).Y - 6f) < 0.5f);
        var back = TfwWire.ReadFire(TfwWire.WriteFire([record]));
        Assert.NotNull(back);
        Assert.Equal((record.Kind, record.Tick, record.Subject), (back![0].Kind, back[0].Tick, back[0].Subject));
        Assert.True(Vector3.Distance(record.At, back[0].At) < 0.05f);
        Assert.Equal(1, TfwSendRules.FireCopies(TfwFireKind.Burn));
    }

    [Theory]
    [InlineData("sm_mp_ranged_plasma_gun", true)]
    [InlineData("csm_mp_ranged_plasma_gun", true)]
    [InlineData("sm_mp_pistol_plasma_pistol", true)]
    [InlineData("csm_mp_pistol_plasma_pistol", true)]
    [InlineData("sm_mp_exotic_plasma_cannon", true)]
    [InlineData("sm_mp_ranged_bolter", false)]
    [InlineData("sm_mp_ranged_melta", false)]
    [InlineData("sm_mp_exotic_las_cannon", false)]
    public void Every_plasma_weapon_and_only_those_pierce_and_burn(string name, bool plasma) =>
        Assert.Equal(plasma, TfwCombatRules.Default.PlasmaShot(Gun with { Name = name }));

    // ---------------------------------------------------------------- Classic stays

    [Fact]
    public void Classic_PvP_plasma_still_stops_on_the_first_player()
    {
        var spawns = new List<SpawnPoint> { new(Vector3.Zero, 0f), new(new Vector3(0f, 8f, 0f), MathF.PI), new(new Vector3(0f, 11f, 0f), MathF.PI), new(new Vector3(0f, 14f, 0f), MathF.PI) };
        var c = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        var config = new SimConfig
        {
            Weapons = [BallGun with { Damage = 5f }], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }],
            HitVolumes = [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.9f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.4f), new(0f, 0f, 1.5f), 0.35f)],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, HealthMax = 150f, ShieldMax = 50f, RespawnTime = 2f,
        };
        var w = new World(config, null, spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f } };
        w.Spawn(1, 0);
        for (int k = 2; k <= 4; k++) w.Spawn(k, 1);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.None));
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Null(w.Musou);
        var me = w.Players[1].Position;
        var others = w.Players.Values.Where(p => p.PlayerId != 1).ToArray();
        float yaw = others.Count(p => p.Position.Y > me.Y) >= others.Count(p => p.Position.Y < me.Y) ? 0f : MathF.PI;
        var ahead = others.Where(p => (p.Position.Y > me.Y) == (yaw == 0f)).OrderBy(p => MathF.Abs(p.Position.Y - me.Y)).Select(p => p.PlayerId).ToArray();
        Assert.True(ahead.Length >= 2);
        float Left(int id) => w.Players[id].Health + w.Players[id].Shield;
        float full = Left(ahead[0]);
        DetonationEvent? burst = null;
        for (int k = 0; k < 200 && burst is null; k++)
        {
            uint view = Math.Max(1u, w.Tick), body = w.Tick > 1 ? w.Tick - 1 : 0;
            w.Step(id => new PlayerInput(w.Tick, 0f, 0f, id == 1 ? yaw : w.Players[id].Yaw, id == 1 ? InputButtons.Fire : InputButtons.None, ViewTick: view, BodyTick: body));
            if (w.LastDetonations.Count > 0) burst = w.LastDetonations[0];
        }
        Assert.NotNull(burst);
        Assert.True(Left(ahead[0]) < full);                                     // the first player is hit
        Assert.Equal(config.HealthMax + config.ShieldMax, Left(ahead[1]), 1);   // the one behind him is not (the ball burst on the first)
        Assert.InRange(MathF.Abs(burst!.Value.Position.Y - w.Players[ahead[0]].Position.Y), 0f, 2f);
    }
}
