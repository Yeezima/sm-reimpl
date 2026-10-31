using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU3): the Warboss's abilities tick by tick on the flat ground (a boss that neither walks nor swings, so only its abilities act):
/// the ground slam's telegraph, its landing 0.8 s later on the players within 6 m only, its 10 s cooldown; the charge's telegraph to its end point, the
/// wind-up in place, the 15 m run at 12 m/s that hits a player on its path once with the knockback, its 12 s cooldown; the Waaagh! at 75, 50 and 25 % each
/// once (the summons through the gates, the horde's speed for 8 s); the enrage below 25 % (cooldowns x 0.6); stagger immunity. Then two whole runs in the
/// World: one won (the Warboss killed by a player's shots, the same run twice alike) and one lost (the Warboss kills the last player).
/// </summary>
public class MusouBossTests
{
    /// <summary>A Warboss that stands (speed 0) and has no swing of its own; the abilities' first cooldowns as given.</summary>
    static MusouRules Rules(float slamFirst, float chargeFirst) => MusouRules.Default with
    {
        Types = [.. MusouRules.Default.Types.Select(t => t.Type == MusouEnemyType.Warboss ? t with { Speed = 0f, MeleeDamage = 0f } : t)],
        Boss = MusouBossRules.Default with { SlamFirst = slamFirst, ChargeFirst = chargeFirst },
    };

    sealed class Fight
    {
        public readonly MusouSim Sim;
        public readonly MusouBoss Boss;
        public readonly MusouTarget[] Targets;
        public readonly List<MusouAttack> Attacks = new();
        public readonly List<MusouBossStrike> Strikes = new();
        public readonly List<(uint Tick, MusouEvent Event)> Events = new();
        public uint Tick;

        public Fight(MusouRules rules, params Vector3[] players)
        {
            Sim = new MusouSim(null, rules);
            ushort id = Sim.Spawn(MusouEnemyType.Warboss, Vector3.Zero, 0f, 4000f);
            Boss = new MusouBoss(Sim, null, id, 4000f);
            Targets = [.. players.Select((p, k) => new MusouTarget(k + 1, p, true))];
        }

        /// <summary>One tick; the boss's strikes of this tick in <see cref="Strikes"/>.</summary>
        public void Step()
        {
            Sim.BeginTick(++Tick);
            Attacks.Clear(); Strikes.Clear();
            Sim.Step(Targets, null, true, Attacks);
            Boss.Step(Targets, true, Strikes);
            foreach (var e in Sim.Events) Events.Add((Tick, e));
        }

        public MusouEnemy Me => Sim.TryGet(Boss.Id, out var e) ? e : default;
        public IEnumerable<uint> Telegraphs(MusouBossAbility a) => Events.Where(e => e.Event.Kind == MusouEventKind.Telegraph && e.Event.Value == (byte)a).Select(e => e.Tick);
        public uint StepUntil(MusouBossAbility a, int max = 2000)
        {
            int before = Telegraphs(a).Count();
            for (int k = 0; k < max && Telegraphs(a).Count() == before; k++) Step();
            Assert.True(Telegraphs(a).Count() > before, $"no {a} within {max} ticks");
            return Telegraphs(a).Last();
        }
    }

    [Fact]
    public void The_ground_slam_lands_after_its_telegraph_on_the_players_within_six_metres_with_its_cooldown()
    {
        var f = new Fight(Rules(0f, 1000f), new Vector3(0f, 3f, 0f), new Vector3(6.5f, 0f, 0f));
        uint t0 = f.StepUntil(MusouBossAbility.GroundSlam);
        var telegraph = f.Events.Single(e => e.Event.Kind == MusouEventKind.Telegraph).Event;
        Assert.Equal((f.Boss.Id, (ushort)1, Vector3.Zero), (telegraph.Enemy, telegraph.Player, telegraph.Vector));
        Assert.Equal(MusouBossAction.SlamWindUp, f.Boss.Action);
        // 0.8 s = 24 ticks of wind-up, in place, before it lands.
        for (int k = 1; k < 24; k++) { f.Step(); Assert.Empty(f.Strikes); Assert.Equal(MusouEnemyState.WindUp, f.Me.State); }
        f.Step();
        Assert.Equal(t0 + 24, f.Tick);
        var strike = Assert.Single(f.Strikes);   // the player at 6.5 m is spared
        Assert.Equal((1, 40f, MusouBossAbility.GroundSlam, 0f), (strike.Player, strike.Damage, strike.Ability, strike.Knockback));
        Assert.Equal(MusouEnemyState.Recover, f.Me.State);
        for (int k = 0; k < 18; k++) f.Step();
        Assert.Equal((MusouBossAction.None, MusouEnemyState.Seek), (f.Boss.Action, f.Me.State));
        // The cooldown: 10 s from the telegraph.
        uint t1 = f.StepUntil(MusouBossAbility.GroundSlam);
        Assert.Equal(t0 + 300, t1);
        Assert.Equal(f.Me.Position, Vector3.Zero);
        // Stagger-immune.
        Assert.False(f.Sim.Stagger(f.Boss.Id, Vector2.UnitY, 2f));
    }

    [Fact]
    public void The_charge_winds_up_in_place_runs_fifteen_metres_and_hits_a_player_on_its_path_once()
    {
        var f = new Fight(Rules(1000f, 0f), new Vector3(0f, 10f, 0f));
        uint t0 = f.StepUntil(MusouBossAbility.Charge);
        var telegraph = f.Events.Single(e => e.Event.Kind == MusouEventKind.Telegraph).Event;
        Assert.Equal(15f, telegraph.Vector.Y, 3);   // the end point, 15 m along the line to the player
        Assert.Equal(0f, telegraph.Vector.X, 3);
        // 1 s of wind-up in place.
        for (int k = 1; k < 30; k++) { f.Step(); Assert.Equal(MusouBossAction.ChargeWindUp, f.Boss.Action); Assert.True(f.Me.Position.Length() < 0.05f); }
        f.Step();
        Assert.Equal(MusouBossAction.Charging, f.Boss.Action);
        var strikes = new List<(uint Tick, MusouBossStrike Strike)>();
        float y = f.Me.Position.Y;
        for (int k = 0; k < 60 && f.Boss.Action == MusouBossAction.Charging; k++)
        {
            f.Step();
            float stride = f.Me.Position.Y - y;
            y = f.Me.Position.Y;
            if (f.Me.Position.Y < 8f) Assert.Equal(0.4f, stride, 2);   // 12 m/s, before the player's ring
            strikes.AddRange(f.Strikes.Select(s => (f.Tick, s)));
        }
        var hit = Assert.Single(strikes).Strike;
        Assert.Equal((1, 50f, 3f, MusouBossAbility.Charge), (hit.Player, hit.Damage, hit.Knockback, hit.Ability));
        Assert.True(hit.Push.Y > 0.8f);   // thrown along the charge
        Assert.Equal(MusouBossAction.Recover, f.Boss.Action);
        Assert.InRange(f.Boss.ChargeTravelled, 14f, 15.5f);
        Assert.InRange(f.Me.Position.Y, 14f, 15.5f);
        // The player 5 m behind it now: too close to charge. 10 m ahead again: the next charge 12 s after the first telegraph.
        f.Targets[0] = f.Targets[0] with { Position = f.Me.Position + new Vector3(0f, 10f, 0f) };
        uint t1 = f.StepUntil(MusouBossAbility.Charge);
        Assert.Equal(t0 + 360, t1);
    }

    [Fact]
    public void Each_waaagh_fires_once_summons_through_the_gates_and_the_enrage_shortens_the_cooldowns()
    {
        var rules = Rules(0f, 1000f) with { Waves = [new MusouWaveSpec(0, 1, [(MusouEnemyType.Boy, 1f)])] };
        var sim = new MusouSim(null, rules);
        var director = new MusouDirector(sim, []);
        director.Start(1);
        MusouTarget[] targets = [new(1, Vector3.Zero, true)];
        uint tick = 0;
        var attacks = new List<MusouAttack>();
        var strikes = new List<MusouBossStrike>();
        var events = new List<(uint Tick, MusouEvent Event)>();
        sim.BeginTick(++tick);
        director.Step(targets);
        Assert.Equal(MusouPhase.Boss, director.Phase);
        var boss = new MusouBoss(sim, director, director.BossId, director.BossMaxHealth);
        void Step(int n)
        {
            for (int k = 0; k < n; k++)
            {
                sim.BeginTick(++tick);
                director.Step(targets);
                sim.Step(targets, null, true, attacks);
                boss.Step(targets, true, strikes);
                foreach (var e in sim.Events) events.Add((tick, e));
            }
        }
        int Waaaghs() => events.Count(e => e.Event.Kind == MusouEventKind.Telegraph && e.Event.Value == (byte)MusouBossAbility.Waaagh);
        int Boyz() { var all = new MusouEnemy[MusouSim.Capacity]; return all[..sim.CopyTo(all)].Count(e => e.Type == MusouEnemyType.Boy); }

        Step(2);
        Assert.Equal(0, Waaaghs());
        sim.Damage(director.BossId, 1, 4000f * 0.26f, Vector3.UnitY, out _);   // 74 %
        Step(1);
        Assert.Equal(1, Waaaghs());
        Assert.Equal((240, 1.3f), (sim.FrenzyTicks, sim.FrenzyScale));
        Step(4);
        Assert.Equal(20, Boyz());   // at most 6 a tick, through the gates (the flat ground's ring of 30 m)
        Assert.Equal((20, 20), (director.WaveTotal, director.Released));
        Step(10);
        Assert.Equal(1, Waaaghs());   // once each
        // Past 50 and 25 % in one blow: both fire (and the enrage).
        sim.Damage(director.BossId, 1, 4000f * 0.5f, Vector3.UnitY, out _);   // 24 %
        Step(1);
        Assert.Equal(3, Waaaghs());
        Assert.True(boss.Enraged);
        Assert.Single(events, e => e.Event.Kind == MusouEventKind.Telegraph && e.Event.Value == (byte)MusouBossAbility.Enrage);
        Step(12);
        Assert.Equal(60, Boyz());
        sim.Damage(director.BossId, 1, 4000f * 0.2f, Vector3.UnitY, out _);   // 4 %
        Step(5);
        Assert.Equal(3, Waaaghs());
        Assert.Single(events, e => e.Event.Kind == MusouEventKind.Telegraph && e.Event.Value == (byte)MusouBossAbility.Enrage);
        Assert.Equal(60, director.WaveTotal);
        Assert.Equal(0, director.Summons);
        // The horde's speed ends 8 s after the last Waaagh!.
        Step(240);
        Assert.Equal((0, 1f), (sim.FrenzyTicks, sim.FrenzyScale));
    }

    [Fact]
    public void Enraged_the_slam_comes_back_after_six_seconds()
    {
        var f = new Fight(Rules(0f, 1000f), new Vector3(0f, 3f, 0f));
        uint t0 = f.StepUntil(MusouBossAbility.GroundSlam);
        f.Sim.Damage(f.Boss.Id, 1, 3100f, Vector3.UnitY, out _);   // 22.5 %: three Waaagh!s and the enrage
        f.Step();
        Assert.True(f.Boss.Enraged);
        uint t1 = f.StepUntil(MusouBossAbility.GroundSlam);
        Assert.Equal(t0 + 300, t1);   // the cooldown started before the enrage keeps its length
        uint t2 = f.StepUntil(MusouBossAbility.GroundSlam);
        Assert.Equal(t1 + 180, t2);   // 10 s x 0.6
    }

    [Fact]
    public void The_waaagh_speed_makes_the_horde_run_faster_for_eight_seconds()
    {
        var sim = new MusouSim(null);
        ushort boy = sim.Spawn(MusouEnemyType.Boy, new Vector3(-150f, 0f, 0f), 0f, 85f);
        MusouTarget[] targets = [new(1, Vector3.Zero, true)];
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        float Speed(int n) { for (int k = 0; k < n; k++) { sim.BeginTick(++tick); sim.Step(targets, null, false, attacks); } return sim.TryGet(boy, out var e) ? e.Velocity.Length() : 0f; }
        Assert.Equal(5.2f, Speed(20), 2);
        sim.Frenzy(1.3f, 8f);
        Assert.Equal(5.2f * 1.3f, Speed(1), 2);
        Assert.Equal(5.2f * 1.3f, Speed(238), 2);
        Assert.Equal(5.2f, Speed(2), 2);
    }

    [Fact]
    public void Summons_wait_for_the_hard_cap_with_several_players_and_count_as_enemies_left()
    {
        var rules = Rules(1000f, 1000f) with { HardCap = 25, Waves = [new MusouWaveSpec(0, 1, [(MusouEnemyType.Boy, 1f)])] };
        var sim = new MusouSim(null, rules);
        var director = new MusouDirector(sim, []);
        director.Start(3);
        MusouTarget[] targets = [new(1, Vector3.Zero, true), new(2, new Vector3(1.5f, 0f, 0f), true), new(3, new Vector3(-1.5f, 0f, 0f), true)];
        uint tick = 0;
        void Step(int n) { for (int k = 0; k < n; k++) { sim.BeginTick(++tick); director.Step(targets); } }
        Step(1);
        Assert.Equal(MusouPhase.Boss, director.Phase);
        director.Summon(MusouEnemyType.Boy, 40);
        Assert.Equal(41, director.View().EnemiesLeft);   // the boss and the 40 to come
        Step(20);
        Assert.Equal(25, sim.Alive);   // 24 Boyz and the Warboss: the cap holds
        Assert.Equal((16, 24), (director.Summons, director.Released));
        Assert.Equal(41, director.View().EnemiesLeft);
        // Ten KOs free room: ten more come (at most 6 a tick).
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = sim.CopyTo(all);
        foreach (var e in all[..n].Where(e => e.Type == MusouEnemyType.Boy).Take(10)) { sim.Damage(e.Id, 1, 1000f, Vector3.UnitY, out _); director.OnKill(new MusouKill(e.Id, e.Type, 1, MusouDeathKind.Shot)); }
        Step(1);
        Assert.Equal(21, sim.Alive);
        Step(5);
        Assert.Equal((25, 6), (sim.Alive, director.Summons));
        Assert.Equal(31, director.View().EnemiesLeft);
    }

    /// <summary>A Boy and a Warboss (no swing, no charge) come for one standing player; every Musou event with its tick, and when the player died.</summary>
    static (List<(uint Tick, MusouEvent Event)> Events, uint Died, ushort Boss) BoyAndBoss(float scale, float boyWindUp, float slamWindUp)
    {
        var rules = MusouRules.Default with
        {
            PlayerDamageScale = scale, Lives = 0, LivesPerExtraPlayer = 0,
            Waves = [new MusouWaveSpec(1, 1, [(MusouEnemyType.Boy, 1f)])],
            Types = [.. MusouRules.Default.Types.Select(t => t.Type switch
            {
                MusouEnemyType.Warboss => t with { MeleeDamage = 0f },
                MusouEnemyType.Boy => t with { MeleeWindUp = boyWindUp },
                _ => t,
            })],
            Boss = MusouBossRules.Default with { ChargeFirst = 1000f, SlamWindUp = slamWindUp },
        };
        var w = NewWorld(rules);
        var events = new List<(uint, MusouEvent)>();
        uint died = 0;
        for (int t = 0; t < 30 * 40 && w.State != MatchState.Ended; t++)
        {
            w.Step(_ => default);
            foreach (var e in w.Musou!.Events) events.Add((w.Tick, e));
            if (died == 0 && !w.Players[1].Alive) died = w.Tick;
        }
        return (events, died, w.MusouDirector!.BossId);
    }

    [Fact]
    public void A_player_the_horde_killed_this_tick_is_neither_slammed_nor_reported_slammed()
    {
        // Run A (nobody hurts): when the Boy's first wind-up and the Warboss's first slam begin.
        var (a, _, boss) = BoyAndBoss(0f, 0.5f, 0.8f);
        uint W = a.First(e => e.Event.Kind == MusouEventKind.Telegraph && e.Event.Enemy != boss).Tick;
        uint S = a.First(e => e.Event.Kind == MusouEventKind.Telegraph && e.Event.Value == (byte)MusouBossAbility.GroundSlam).Tick;
        // Run B: both land on the same tick L (the later one's own wind-up lengthened), and the Boy's strike kills.
        float boyWindUp = 0.5f, slamWindUp = 0.8f;
        uint L = Math.Max(W + 15, S + 24);
        if (L > W + 15) boyWindUp = (L - W) / 30f; else slamWindUp = (L - S) / 30f;
        var (b, died, bossB) = BoyAndBoss(1000f, boyWindUp, slamWindUp);
        Assert.Equal(boss, bossB);
        Assert.Equal(S, b.First(e => e.Event.Kind == MusouEventKind.Telegraph && e.Event.Value == (byte)MusouBossAbility.GroundSlam).Tick);
        Assert.Contains(b, e => e.Tick == L && e.Event.Kind == MusouEventKind.Strike && e.Event.Enemy != boss && e.Event.Player == 1);
        Assert.Equal(L, died);
        Assert.DoesNotContain(b, e => e.Event.Kind == MusouEventKind.Strike && e.Event.Enemy == boss);
        // In run A the same slam landed on the living player.
        Assert.Contains(a, e => e.Tick == S + 24 && e.Event.Kind == MusouEventKind.Strike && e.Event.Enemy == boss && e.Event.Player == 1);
    }

    // ---------------------------------------------------------------- whole runs in the World

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 5000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);

    static World NewWorld(MusouRules rules)
    {
        var w = new World(MusouSimTests.Base with { Weapons = [Rifle], MusouRules = rules }) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.Spawn(1, w.AssignClass(0));
        return w;
    }

    /// <summary>The player aims at the Warboss (else the nearest enemy) and fires; the run's summary.</summary>
    static (Team Winner, uint Ticks, int Kos, int Points, int Waaaghs, string Log) WinRun()
    {
        var rules = MusouRules.Default with { PlayerDamageScale = 0f, BossHealth = 300f, Waves = [new MusouWaveSpec(3, 3, [(MusouEnemyType.Boy, 1f)])] };
        var w = NewWorld(rules);
        var d = w.MusouDirector!;
        var buffer = new MusouEnemy[MusouSim.Capacity];
        var log = new System.Text.StringBuilder();
        int waaaghs = 0;
        bool bossShot = false;
        for (int t = 0; t < 30 * 120 && w.State != MatchState.Ended; t++)
        {
            var me = w.Players[1].Position;
            float yaw = 0f;
            if (d.BossId != 0 && w.Musou!.TryGet(d.BossId, out var boss) && boss.Alive) { var v = boss.Position - me; yaw = MathF.Atan2(-v.X, v.Y); }
            else
            {
                int n = w.Musou!.CopyTo(buffer);
                var near = buffer[..n].Where(e => e.Alive).OrderBy(e => Vector3.DistanceSquared(e.Position, me)).FirstOrDefault();
                if (near.Id != 0) { var v = near.Position - me; yaw = MathF.Atan2(-v.X, v.Y); }
            }
            w.Step(_ => new PlayerInput(0, 0f, 0f, yaw, InputButtons.Fire));
            foreach (var e in w.Musou!.Events)
            {
                log.Append($"{e.Tick}:{e.Kind}:{e.Enemy}:{e.Player}:{e.Value};");
                if (e.Kind == MusouEventKind.Telegraph && e.Value == (byte)MusouBossAbility.Waaagh) waaaghs++;
                if (e.Kind == MusouEventKind.Death && e.Enemy == d.BossId && d.BossId != 0) bossShot = e.Player == 1 && e.Value == (byte)MusouDeathKind.Shot;
            }
        }
        Assert.True(bossShot, "the Warboss died to the player's shots");
        return (w.Winner, w.Tick, d.Kos, d.Scores[1].Points, waaaghs, log.ToString());
    }

    [Fact]
    public void A_whole_run_is_won_when_a_players_shots_kill_the_warboss_and_replays_alike()
    {
        var a = WinRun();
        Assert.Equal(Team.SpaceMarines, a.Winner);
        Assert.True(a.Waaaghs >= 1);
        Assert.True(a.Points >= 2000);
        Assert.True(a.Kos >= 1);   // the Warboss (he comes once the last wave is all out) and any Boyz shot on the way
        var b = WinRun();
        Assert.Equal(a, b);   // deterministic
    }

    [Fact]
    public void A_whole_run_is_lost_when_the_warboss_kills_the_last_player()
    {
        var rules = MusouRules.Default with
        {
            PlayerDamageScale = 10f, Lives = 0, LivesPerExtraPlayer = 0,
            Waves = [new MusouWaveSpec(1, 1, [(MusouEnemyType.Boy, 1f)])],
            Types = [.. MusouRules.Default.Types.Select(t => t.Type == MusouEnemyType.Boy ? t with { MeleeDamage = 0f } : t)],
        };
        var w = NewWorld(rules);
        var d = w.MusouDirector!;
        var buffer = new MusouEnemy[MusouSim.Capacity];
        int abilities = 0;
        for (int t = 0; t < 30 * 120 && w.State != MatchState.Ended; t++)
        {
            // The wave's Boy (and any summoned) is KO'd by script; only the Warboss is left to fight.
            int n = w.Musou!.CopyTo(buffer);
            foreach (var e in buffer[..n]) if (e.Alive && e.Type == MusouEnemyType.Boy) w.Musou.Damage(e.Id, 1, 1000f, Vector3.UnitY, out _);
            w.Step(_ => default);
            abilities += w.Musou.Events.Count(e => e.Kind == MusouEventKind.Telegraph && e.Enemy == d.BossId && e.Value is (byte)MusouBossAbility.GroundSlam or (byte)MusouBossAbility.Charge);
        }
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.Chaos, w.Winner);
        Assert.Equal(MusouPhase.Lost, d.Phase);
        Assert.NotEqual(0, d.BossId);
        Assert.True(abilities >= 1);
        Assert.False(w.Players[1].Alive);
    }
}
