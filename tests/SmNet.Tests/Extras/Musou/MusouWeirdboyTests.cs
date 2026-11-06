using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Weirdboy (Musou Mode only; spec "Musou Mode: the Weirdboy"): the data's numbers, the shield that bullets cannot pierce and melee and blasts wear down, the stun when it breaks and the shield's
/// return, the bolt that bursts where it was aimed (a player who moves on is spared), the teleport away from a player who closes in, the summons, and when he comes in a battle.
/// </summary>
public class MusouWeirdboyTests
{
    static int Ticks(float seconds) => (int)MathF.Round(seconds * 30f);

    sealed class Fight
    {
        public readonly MusouSim Sim = new(null);
        public readonly MusouTarget[] Targets;
        public readonly List<MusouAttack> Attacks = new();
        public readonly List<(uint Tick, MusouEvent Event)> Events = new();
        public readonly ushort Id;
        public uint Tick;

        public Fight(Vector3 player)
        {
            Id = Sim.Spawn(MusouEnemyType.Weirdboy, Vector3.Zero, 0f, MusouRules.Default.Of(MusouEnemyType.Weirdboy).Health);
            Assert.NotEqual(0, Id);
            Targets = [new MusouTarget(1, player, true)];
        }

        public void Step(int n = 1)
        {
            for (int k = 0; k < n; k++)
            {
                Sim.BeginTick(++Tick);
                Attacks.Clear();
                Sim.Step(Targets, null, true, Attacks);
                foreach (var e in Sim.Events) Events.Add((Tick, e));
            }
        }

        public MusouEnemy Me => Sim.TryGet(Id, out var e) ? e : default;
        public IEnumerable<(uint Tick, MusouEvent Event)> Cues(byte cue) => Events.Where(e => e.Event.Kind == MusouEventKind.Telegraph && e.Event.Enemy == Id && e.Event.Value == cue);
    }

    [Fact]
    public void His_numbers_are_the_datas_where_it_has_them()
    {
        var s = MusouRules.Default.Of(MusouEnemyType.Weirdboy);
        Assert.Equal(MusouEnemyType.Weirdboy, s.Type);
        Assert.Equal(50f, s.Health);          // health_max
        Assert.Equal(6f, s.Speed);            // speed_standing/run_speed
        Assert.Equal(20f, s.MeleeDamage);     // the staff's damage_general
        Assert.Equal(5f, s.HybridMeleeDistance);   // the staff's max_range
        Assert.Equal(3f, s.RangedBlastRadius);     // the frazzle's area_of_effect radius
        Assert.Equal(40f, s.RangedBoltSpeed);      // projectile_ext speed
        Assert.Equal(35f, s.RangedDamage);         // 70 explosive x 0.5 on heavy armour
        Assert.Equal((20f, 50f), (s.RangedMin, s.RangedMax));   // min/max_ideal_range
        Assert.True(s.ShieldMax > 0f);
        Assert.Equal(8f, MusouWeirdboy.StunSeconds);
        Assert.Equal(75f, MusouWeirdboy.ShieldRestore);
        Assert.False(MusouSim.Fodder(MusouEnemyType.Weirdboy));
        Assert.False(MusouBossOrks.Is(MusouEnemyType.Weirdboy));
        Assert.True((int)MusouEnemyType.Weirdboy < 16 && (int)MusouEnemyState.Vanishing < 16);   // 4 bits each on the wire
    }

    [Fact]
    public void Bullets_never_get_through_the_shield_melee_and_blasts_wear_it_down_and_its_break_stuns_him_open()
    {
        var f = new Fight(new Vector3(0f, 30f, 0f));
        f.Sim.BeginTick(1);
        float shield = MusouRules.Default.Of(MusouEnemyType.Weirdboy).ShieldMax;
        Assert.Equal(shield, f.Sim.ShieldOf(f.Id));
        Assert.True(f.Sim.Damage(f.Id, 1, 500f, Vector3.Zero, out bool killed, cause: MusouDeathKind.Shot));
        Assert.False(killed);
        Assert.Equal(shield, f.Sim.ShieldOf(f.Id));
        Assert.Equal(50f, f.Me.Health);
        Assert.Contains(f.Sim.Events, e => e.Kind == MusouEventKind.Hit && e.Value == MusouWeirdboy.ShieldMarker);
        Assert.True(f.Sim.Damage(f.Id, 1, 500f, Vector3.Zero, out _, cause: MusouDeathKind.Strafe));
        Assert.Equal(shield, f.Sim.ShieldOf(f.Id));

        Assert.True(f.Sim.Damage(f.Id, 1, shield - 100f, Vector3.Zero, out _, cause: MusouDeathKind.Melee));
        Assert.Equal(100f, f.Sim.ShieldOf(f.Id), 3);
        Assert.Equal(50f, f.Me.Health);
        Assert.True(f.Sim.Damage(f.Id, 1, 150f, Vector3.Zero, out killed, cause: MusouDeathKind.Blast));   // the rest of the blow is lost with the shield
        Assert.False(killed);
        Assert.Equal(0f, f.Sim.ShieldOf(f.Id));
        Assert.Equal(50f, f.Me.Health);
        Assert.Equal(MusouEnemyState.Stunned, f.Me.State);
        Assert.Contains(f.Sim.Events, e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouWeirdboy.ShieldBreakCue);

        Assert.True(f.Sim.Damage(f.Id, 1, 30f, Vector3.Zero, out killed, cause: MusouDeathKind.Shot));   // open now: a bolter kills him
        Assert.Equal(20f, f.Me.Health);
        Assert.True(f.Sim.Damage(f.Id, 1, 30f, Vector3.Zero, out killed, cause: MusouDeathKind.Shot));
        Assert.True(killed);
    }

    [Fact]
    public void The_stun_lasts_eight_seconds_through_a_stagger_and_the_shield_comes_back_with_75()
    {
        var f = new Fight(new Vector3(0f, 30f, 0f));
        f.Step(Ticks(0.6f));
        f.Sim.Damage(f.Id, 1, 1000f, Vector3.Zero, out _, cause: MusouDeathKind.Melee);
        Assert.Equal(MusouEnemyState.Stunned, f.Me.State);
        f.Step(Ticks(4f));
        Assert.True(f.Sim.Stagger(f.Id, Vector2.UnitX, 1f));
        Assert.Equal(MusouEnemyState.Stunned, f.Me.State);   // a stagger never ends the stun
        f.Step(Ticks(8f) - Ticks(4f) - 2);
        Assert.Equal(MusouEnemyState.Stunned, f.Me.State);
        f.Step(3);
        Assert.NotEqual(MusouEnemyState.Stunned, f.Me.State);
        Assert.Equal(MusouWeirdboy.ShieldRestore, f.Sim.ShieldOf(f.Id));
        Assert.Single(f.Cues(MusouWeirdboy.ShieldBackCue));
    }

    [Fact]
    public void A_bolt_bursts_where_it_was_aimed_after_its_flight_and_spares_a_player_who_moved_on()
    {
        var player = new Vector3(0f, 30f, 0f);
        var f = new Fight(player);
        for (int k = 0; k < Ticks(8f) && !f.Cues(MusouWeirdboy.BoltCue).Any(); k++) f.Step();
        var (thrown, cue) = f.Cues(MusouWeirdboy.BoltCue).First();
        Assert.Equal(player, cue.Vector);
        Assert.Equal(1, cue.Player);
        int flight = (int)MathF.Round(Vector3.Distance(new Vector3(0f, 0f, 1.5f), player) / 40f * 30f);
        uint landed = 0;
        for (int k = 0; k < flight + 5 && landed == 0; k++)
        {
            f.Step();
            if (f.Attacks.Any(a => a.Enemy == f.Id && !a.Melee)) landed = f.Tick;
        }
        Assert.Equal(thrown + (uint)flight, landed);
        var hit = f.Attacks.Single(a => a.Enemy == f.Id);
        Assert.Equal((1, 35f, false, false), (hit.Player, hit.Damage, hit.Melee, hit.Staggers));

        // The next one: the player steps 4 m aside as it is thrown, out of its 3 m burst.
        int before = f.Cues(MusouWeirdboy.BoltCue).Count();
        for (int k = 0; k < Ticks(6f) && f.Cues(MusouWeirdboy.BoltCue).Count() == before; k++) f.Step();
        Assert.True(f.Cues(MusouWeirdboy.BoltCue).Count() > before);
        f.Targets[0] = new MusouTarget(1, player + new Vector3(4f, 0f, 0f), true);
        bool any = false;
        for (int k = 0; k < flight + 5; k++) { f.Step(); any |= f.Attacks.Any(a => a.Enemy == f.Id); }
        Assert.False(any);
        Assert.Equal(0, f.Sim.BoltsInFlight);
    }

    [Fact]
    public void He_teleports_away_from_a_player_who_closes_in_to_25_to_35_metres_from_him()
    {
        var player = new Vector3(0f, 4f, 0f);
        var f = new Fight(player);
        for (int k = 0; k < Ticks(2f) && f.Me.State != MusouEnemyState.Vanishing; k++) f.Step();
        Assert.Equal(MusouEnemyState.Vanishing, f.Me.State);
        var (_, cue) = f.Cues(MusouWeirdboy.TeleportCue).Single();
        float away = new Vector2(cue.Vector.X - player.X, cue.Vector.Y - player.Y).Length();
        Assert.InRange(away, MusouWeirdboy.TeleportMin - 1e-3f, MusouWeirdboy.TeleportMax + 1e-3f);
        Assert.True(cue.Vector.Y < player.Y);   // first tried on his own side of the player
        f.Step(Ticks(MusouWeirdboy.VanishSeconds));
        Assert.Equal(MusouEnemyState.Spawning, f.Me.State);
        Assert.Equal(cue.Vector, f.Me.Position);
        Assert.False(f.Attacks.Any(a => a.Melee));   // he never swung
    }

    [Fact]
    public void He_calls_four_Boyz_round_him_once_his_summons_is_due()
    {
        var f = new Fight(new Vector3(0f, 30f, 0f));
        f.Step(Ticks(0.5f + MusouWeirdboy.SummonFirst) + 2);
        Assert.Single(f.Cues(MusouWeirdboy.SummonCue));
        Assert.Equal(MusouEnemyState.Casting, f.Me.State);
        Assert.Equal(1, f.Sim.Alive);
        f.Step(Ticks(MusouWeirdboy.SummonSeconds));
        Assert.Equal(1 + MusouWeirdboy.SummonCount, f.Sim.Alive);
        var all = new MusouEnemy[8];
        int n = f.Sim.CopyTo(all);
        foreach (var boy in all.Take(n).Where(e => e.Id != f.Id))
        {
            Assert.Equal(MusouEnemyType.Boy, boy.Type);
            Assert.InRange(new Vector2(boy.Position.X - f.Me.Position.X, boy.Position.Y - f.Me.Position.Y).Length(), MusouWeirdboy.SummonRingMin - 0.5f, MusouWeirdboy.SummonRingMax + 0.5f);
        }
    }

    [Fact]
    public void The_failsafe_gets_through_the_shield_and_a_move_mid_stun_brings_the_shield_back()
    {
        var f = new Fight(new Vector3(0f, 30f, 0f));
        f.Step(Ticks(0.6f));
        f.Sim.Damage(f.Id, 1, 1000f, Vector3.Zero, out _, cause: MusouDeathKind.Melee);
        Assert.Equal(MusouEnemyState.Stunned, f.Me.State);
        Assert.True(f.Sim.Relocate(f.Id, new Vector3(10f, 10f, 0f)));
        Assert.Equal(MusouEnemyState.Seek, f.Me.State);
        Assert.Equal(MusouWeirdboy.ShieldRestore, f.Sim.ShieldOf(f.Id));

        var g = new Fight(new Vector3(0f, 30f, 0f));
        g.Sim.BeginTick(1);
        Assert.True(g.Sim.Damage(g.Id, 0, 50f, Vector3.Zero, out bool killed, marker: 0, cause: MusouDeathKind.World));   // the stranded failsafe's removal
        Assert.True(killed);
    }

    [Fact]
    public void A_stagger_cuts_his_summons_short()
    {
        var f = new Fight(new Vector3(0f, 30f, 0f));
        f.Step(Ticks(0.5f + MusouWeirdboy.SummonFirst) + 2);
        Assert.Equal(MusouEnemyState.Casting, f.Me.State);
        Assert.True(f.Sim.Stagger(f.Id, Vector2.UnitX, 1f));
        f.Step(Ticks(MusouWeirdboy.SummonSeconds) + 2);
        Assert.Equal(1, f.Sim.Alive);   // no Boyz came
        Assert.NotEqual(MusouEnemyState.Casting, f.Me.State);
    }

    [Fact]
    public void His_shield_breaking_throws_down_every_Ork_within_20_metres_but_the_big_ones()
    {
        var f = new Fight(new Vector3(0f, 30f, 0f));
        f.Sim.War = TfwCombatRules.Default;
        ushort near = f.Sim.Spawn(MusouEnemyType.Nob, new Vector3(5f, 0f, 0f), 0f, 600f);
        ushort edge = f.Sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, -19f, 0f), 0f, 85f);
        ushort far = f.Sim.Spawn(MusouEnemyType.Boy, new Vector3(25f, 0f, 0f), 0f, 85f);
        ushort boss = f.Sim.Spawn(MusouEnemyType.BossNob, new Vector3(-5f, 0f, 0f), 0f, 6000f);
        ushort chaos = f.Sim.Spawn(MusouEnemyType.ChaosMarine, new Vector3(0f, 5f, 0f), 0f, 250f);
        f.Step(Ticks(0.6f));
        f.Sim.Damage(f.Id, 1, 1000f, Vector3.Zero, out _, cause: MusouDeathKind.Melee);
        MusouEnemyState State(ushort id) => f.Sim.TryGet(id, out var e) ? e.State : MusouEnemyState.Dying;
        Assert.Equal(MusouEnemyState.Knocked, State(near));
        Assert.Equal(MusouEnemyState.Knocked, State(edge));
        Assert.NotEqual(MusouEnemyState.Knocked, State(far));
        Assert.NotEqual(MusouEnemyState.Knocked, State(boss));
        Assert.NotEqual(MusouEnemyState.Knocked, State(chaos));
        Assert.Equal(1 + 5, f.Sim.Alive);   // nobody died
        f.Step(Ticks(0.5f));
        Assert.True(f.Sim.TryGet(near, out var thrown) && thrown.Position.X > 5.5f);   // away from him
    }

    [Fact]
    public void His_death_bursts_killing_every_Ork_within_20_metres_but_the_Warboss_credited_to_his_killer()
    {
        var f = new Fight(new Vector3(0f, 30f, 0f));
        ushort boy = f.Sim.Spawn(MusouEnemyType.Boy, new Vector3(5f, 0f, 0f), 0f, 85f);
        ushort nob = f.Sim.Spawn(MusouEnemyType.BossNob, new Vector3(0f, -19f, 0f), 0f, 6000f);
        ushort other = f.Sim.Spawn(MusouEnemyType.Weirdboy, new Vector3(-10f, 0f, 0f), 0f, 50f);
        ushort far = f.Sim.Spawn(MusouEnemyType.Boy, new Vector3(25f, 0f, 0f), 0f, 85f);
        ushort warboss = f.Sim.Spawn(MusouEnemyType.Warboss, new Vector3(0f, 8f, 0f), 0f, 15000f);
        ushort chaos = f.Sim.Spawn(MusouEnemyType.ChaosMarine, new Vector3(0f, 5f, 0f), 0f, 250f);
        f.Sim.BeginTick(1);
        f.Sim.Damage(f.Id, 7, 1000f, Vector3.Zero, out _, cause: MusouDeathKind.Melee);   // the shield breaks
        Assert.True(f.Sim.Damage(f.Id, 7, 50f, Vector3.Zero, out bool killed, cause: MusouDeathKind.Shot));
        Assert.True(killed);
        bool Alive(ushort id) => f.Sim.TryGet(id, out var e) && e.Alive;
        Assert.False(Alive(boy)); Assert.False(Alive(nob)); Assert.False(Alive(other));   // the other Weirdboy's shield does not stop it
        Assert.True(Alive(far)); Assert.True(Alive(warboss)); Assert.True(Alive(chaos));
        var kills = new List<MusouKill>();
        f.Sim.TakeKills(kills);
        Assert.Equal(4, kills.Count);
        Assert.All(kills, k => Assert.Equal(7, k.Player));
        Assert.All(kills.Where(k => k.Enemy != f.Id), k => Assert.Equal(MusouDeathKind.Blast, k.Cause));
        Assert.Equal(2, f.Sim.Events.Count(e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouWeirdboy.BurstCue));   // he and the other one

        var g = new Fight(new Vector3(0f, 30f, 0f));
        ushort spared = g.Sim.Spawn(MusouEnemyType.Boy, new Vector3(5f, 0f, 0f), 0f, 85f);
        g.Sim.BeginTick(1);
        g.Sim.Damage(g.Id, 0, 50f, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.World);   // the failsafe's removal: no burst
        Assert.True(g.Sim.TryGet(spared, out var s) && s.Alive);
    }

    [Fact]
    public void In_a_battle_one_appears_near_a_player_after_45_to_90_seconds_never_more_than_two_and_none_in_the_final_assault()
    {
        Vector3[] player = [new(0f, -230f, 0f)];
        var come = new List<(int Tick, ushort Id, Vector3 At)>();
        var sim = new MusouSim(null) { Frozen = true };
        var battle = new TfwBattle(sim, new GuardSim(sim), TfwTestArena.Layout(), 100.0, 1, false, default, TfwBattleRules.EliteBase, guardsmen: false) { Trickle = false, Patrols = false, BossOrksOn = false };
        battle.OnSwarm = s => { foreach (ushort id in s.Ids) sim.Remove(id); };
        int tick = 0;
        battle.OnWeirdboy = (id, at) => come.Add((tick, id, at));
        battle.Start();
        for (; tick < Ticks(8f * 60f); tick++)
        {
            battle.Step(player);
            Assert.True(battle.Weirdboyz.Count <= TfwWeirdboyRules.MaxAlive);
        }
        Assert.NotEmpty(come);
        Assert.InRange(come[0].Tick, Ticks(TfwWeirdboyRules.FirstSeconds) - 1, Ticks(TfwWeirdboyRules.FirstSeconds + TfwWeirdboyRules.FirstSpread) + 1);
        foreach (var (_, id, at) in come)
        {
            Assert.InRange(Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(player[0].X, player[0].Y)), TfwWeirdboyRules.ArriveMin - 1e-3f, TfwWeirdboyRules.ArriveMax + 1e-3f);
            Assert.True(sim.TryGet(id, out var e) && e.Type == MusouEnemyType.Weirdboy);
        }
        Assert.Equal(TfwWeirdboyRules.MaxAlive, battle.Weirdboyz.Count);   // a frozen pool: nobody kills them, so the cap holds the rest back

        var assault = new TfwBattle(sim, new GuardSim(sim), TfwTestArena.Layout(), 100.0, 1, true, default, TfwBattleRules.EliteBase, guardsmen: false) { Trickle = false, Patrols = false };
        assault.Start();
        for (int t = 0; t < Ticks(150f); t++) assault.Step(player);
        Assert.Equal(0, assault.WeirdboyzCome);
    }
}
