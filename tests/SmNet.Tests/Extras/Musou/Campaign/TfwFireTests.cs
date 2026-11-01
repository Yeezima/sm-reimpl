using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>Fire support (Musou pass 2, M8): the barrages, the Thunderhawk, their rules and their records, scripted through the real World.</summary>
public class TfwFireTests
{
    const int Human = TfwRig.Human;
    static readonly Vector3 Open = new(60f, -60f, 0f);

    /// <summary>A battle with the field emptied of its own Orks and Guardsmen, the schedule off.</summary>
    static World Battle()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        var fire = w.Tfw!.Fire;
        fire.NoSchedule = true;
        w.Musou!.Clear();
        w.TfwGuards!.Clear();
        w.TfwRespawnAt(Human, new Vector3(0f, -100f, 0f), 0f);
        return w;
    }

    static ushort Enemy(World w, MusouEnemyType type, Vector3 at)
    {
        ushort id = w.Musou!.Spawn(type, at, 0f, w.Musou.Rules.Of(type).Health);
        Assert.NotEqual(0, id);
        return id;
    }

    static (MusouEnemyType, float, bool)[] Fodder => [(MusouEnemyType.Boy, 5.9f, true), (MusouEnemyType.ShootaBoy, 3f, true), (MusouEnemyType.Gretchin, 0f, true), (MusouEnemyType.Boy, 6.5f, false)];

    [Fact]
    public void A_shell_kills_fodder_within_6_m_and_only_there_as_a_barrage_death_of_nobody()
    {
        foreach (var (type, distance, dies) in Fodder)
        {
            var w = Battle();
            ushort id = Enemy(w, type, Open + new Vector3(distance, 0f, 0f));
            w.Musou!.BeginTick(w.Tick);
            w.TfwShellForTests(Open);
            Assert.True(w.Musou.TryGet(id, out var e));
            Assert.Equal(dies, e.State == MusouEnemyState.Dying);
            if (!dies) { Assert.Equal(w.Musou.Rules.Of(type).Health, e.Health); continue; }
            var death = Assert.Single(w.Musou.Events, x => x.Kind == MusouEventKind.Death && x.Enemy == id);
            Assert.Equal((0, (byte)MusouDeathKind.Barrage), (death.Player, death.Value));
        }
    }

    [Theory]
    [InlineData(0f, 150f)]
    [InlineData(6f, 75f)]
    [InlineData(11.9f, 1.25f)]
    public void Elites_and_bosses_take_150_falling_off_to_12_m(float distance, float expected)
    {
        foreach (var type in new[] { MusouEnemyType.Nob, MusouEnemyType.ArdBoy })
        {
            var w = Battle();
            ushort id = Enemy(w, type, Open + new Vector3(distance, 0f, 0f));
            float health = w.Musou!.Rules.Of(type).Health;
            w.TfwShellForTests(Open);
            Assert.True(w.Musou.TryGet(id, out var e));
            if (expected < health) Assert.Equal(health - expected, e.Health, 2);
            else Assert.Equal(MusouEnemyState.Dying, e.State);
        }
    }

    [Fact]
    public void A_shell_on_a_roof_spares_those_under_it()
    {
        var w = Battle();
        ushort id = Enemy(w, MusouEnemyType.Boy, Open);
        w.TfwShellForTests(Open + new Vector3(0f, 0f, TfwFireRules.VerticalReach + 1f));
        Assert.True(w.Musou!.TryGet(id, out var e) && e.State != MusouEnemyState.Dying);
    }

    [Theory]
    [InlineData(0f, 60f)]
    [InlineData(6f, 30f)]
    [InlineData(12.5f, 0f)]
    public void Players_are_never_spared_60_falling_off_to_12_m(float distance, float expected)
    {
        var w = Battle();
        w.TfwRespawnAt(Human, Open + new Vector3(distance, 0f, 0f), 0f);
        var before = w.Players[Human];
        w.TfwShellForTests(Open);
        var after = w.Players[Human];
        Assert.Equal(expected, before.Health + before.Shield - after.Health - after.Shield, 2);
    }

    [Fact]
    public void Guardsmen_die_within_4_m()
    {
        var w = Battle();
        var guards = w.TfwGuards!;
        int squad = guards.SpawnSquad(Open);
        Assert.True(squad >= 0);
        int inside = guards.CountInside(Open, TfwFireRules.GuardRadius);
        int alive = guards.Alive;
        Assert.True(inside > 0);
        w.TfwShellForTests(Open);
        Assert.Equal(0, guards.CountInside(Open, TfwFireRules.GuardRadius));
        Assert.Equal(alive - inside, guards.Alive);
    }

    [Fact]
    public void A_barrage_walks_16_to_24_shells_0_35_s_apart_each_told_1_s_before_it_lands()
    {
        var w = Battle();
        var fire = w.Tfw!.Fire;
        Assert.True(w.TfwFireForTests(true, false, Open, 0.7f));
        int rate = w.Config.TickRate;
        var shells = fire.Shells.OrderBy(s => s.Tick).ToArray();
        Assert.InRange(shells.Length, TfwFireRules.ShellsMin, TfwFireRules.ShellsMax);
        for (int k = 1; k < shells.Length; k++) Assert.InRange((shells[k].Tick - shells[k - 1].Tick) / (float)rate, 0.3f, 0.4f);
        var way = new Vector3(MathF.Cos(0.7f), MathF.Sin(0.7f), 0f);
        float last = float.NegativeInfinity;
        for (int k = 0; k < shells.Length; k += 4)
        {
            float along = Vector3.Dot(shells[k].At - Open, way);
            Assert.True(along > last - 2f * TfwFireRules.Scatter, "the line walks");
            last = along;
        }
        var seen = new List<(TfwFireEvent Event, uint At)>();
        var out1 = new List<TfwFireEvent>();
        int total = shells.Length;
        for (int t = 0; t < 20 * rate; t++)
        {
            TfwRig.Steps(w, 1);
            out1.Clear();
            fire.TakeEvents(out1);
            foreach (var e in out1) seen.Add((e, w.Tick));
        }
        Assert.Equal(total, seen.Count(s => s.Event.Kind == TfwFireKind.Shell));
        foreach (var (e, at) in seen) Assert.InRange((e.Tick - (long)at) / (float)rate, 0.8f, 1.1f);
        Assert.Equal(total, fire.ShellsLanded);
        Assert.Empty(fire.Shells);
    }

    [Fact]
    public void Nothing_falls_or_flies_uncalled_with_the_runs_paused()
    {
        Assert.False(TfwFireRules.RunsEnabled);
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        var fire = w.Tfw!.Fire;
        for (int t = 0; t < 300 * w.Config.TickRate && w.Tfw.Battle is not null; t++)
        {
            TfwRig.Steps(w, 1);
            if (w.Players[Human] is { Alive: false }) w.TfwRespawnAt(Human, new Vector3(0f, -100f, 0f), 0f);
        }
        Assert.Equal((0, 0, 0), (fire.Barrages, fire.Runs, fire.ShellsLanded));
    }

    static TfwCommandRecord Strike(Vector3 at, ushort sequence = 20) => new(sequence, TfwCommand.Orbital, 0, at);

    [Fact]
    public void A_strike_starts_at_once_on_its_point_with_the_first_impact_1_s_after_the_call()
    {
        Assert.True(TfwWire.HasPoint(TfwCommand.Orbital));
        Assert.Equal(Strike(Open), TfwWire.ReadCommand(TfwWire.WriteCommand(Strike(Open))));
        var w = Battle();
        var fire = w.Tfw!.Fire;
        uint called = w.Tick;
        Assert.True(w.Tfw.Enqueue(Human, Strike(Open)));
        TfwRig.Steps(w, 2);
        Assert.Equal(1, fire.Barrages);
        var shells = fire.Shells.OrderBy(s => s.Tick).ToArray();
        Assert.InRange((shells[0].Tick - called) / (float)w.Config.TickRate, TfwFireRules.ShellDelay - 0.1f, TfwFireRules.ShellDelay + 0.1f);
        var centre = shells.Aggregate(Vector3.Zero, (sum, s) => sum + s.At with { Z = 0f }) / shells.Length;
        Assert.True(Vector3.Distance(centre, Open) < TfwFireRules.Scatter, $"{centre}");
        var records = new List<TfwFireEvent>();
        fire.TakeEvents(records);
        Assert.Contains(records, r => r.Kind == TfwFireKind.Shell);
        Assert.Equal((byte)TfwFireRules.StrikeCooldownSeconds, w.TfwOwn(Human).OrbitalSeconds);
    }

    [Fact]
    public void The_server_refuses_a_strike_on_cooldown_off_the_battlefield_from_the_dead_or_outside_a_battle()
    {
        var hub = TfwRig.War();
        Assert.False(hub.TfwOrbital(Human, Open));
        var w = Battle();
        var fire = w.Tfw!.Fire;
        var box = w.Tfw.Field.Bounds;
        Assert.False(w.TfwOrbital(Human, new Vector3(box.Max.X + 5f, 0f, 0f)));
        Assert.False(w.TfwOrbital(Human, new Vector3(float.NaN, 0f, 0f)));
        Assert.False(w.TfwOrbital(Human + 1, Open));
        Assert.Equal(0, fire.StrikeTicksLeft(Human, w.Tick));
        Assert.True(w.TfwOrbital(Human, Open));
        Assert.False(w.TfwOrbital(Human, Open));
        TfwRig.Seconds(w, 12f);
        if (w.Players[Human].Alive) Assert.False(w.TfwOrbital(Human, Open));
        Assert.Equal(1, fire.Barrages);
        var dead = Battle();
        Assert.True(dead.TfwKillForTests(Human));
        Assert.False(dead.TfwOrbital(Human, Open));
    }

    [Fact]
    public void The_cooldown_ends_after_60_s()
    {
        var fire = new TfwFire();
        fire.StrikeCalledForTests(1, 100, 30);
        Assert.Equal(1800, fire.StrikeTicksLeft(1, 100));
        Assert.True(fire.StrikeTicksLeft(1, 1899) > 0);
        Assert.Equal(0, fire.StrikeTicksLeft(1, 1900));
        Assert.Equal(0, fire.StrikeTicksLeft(2, 100));
    }

    [Fact]
    public void The_cooldown_travels_in_the_own_block_and_the_hud_says_it()
    {
        var war = new TfwWarView(TfwRunPhase.Battle, TfwLane.B, 1f, 1f, 1, 1, 0, default, default, default, new TfwOwnView(0, 0, 0, 0, 0, false, TfwRetainerState.None, default, 0, 0, 0, 0, 0, true, 37));
        Assert.Equal(37, TfwWire.ReadWar(TfwWire.WriteWar(war))!.Value.Own.OrbitalSeconds);
        Assert.Equal("ORBITAL STRIKE IN 37 S", TfwHudModel.OrbitalText(war.Own));
        Assert.Equal("B ORBITAL STRIKE READY", TfwHudModel.OrbitalText(default));
    }

    [Fact]
    public void Every_drawn_run_gap_is_at_least_45_s()
    {
        var fire = new TfwFire();
        for (uint tick = 0; tick < 100_000; tick += 37)
        {
            float gap = fire.RunGap(tick);
            Assert.InRange(gap, TfwFireRules.RunGapSeconds, TfwFireRules.RunGapSeconds + TfwFireRules.RunGapSpread);
        }
    }

    [Fact]
    public void A_run_flies_at_35_m_or_more_over_everything_and_its_guns_hurt_enemies_alone_in_its_corridor()
    {
        var w = Battle();
        var fire = w.Tfw!.Fire;
        var near = Enumerable.Range(0, 12).Select(k => Enemy(w, MusouEnemyType.Nob, Open + new Vector3(-30f + k * 5f, 18f, 0f))).ToArray();
        ushort far = Enemy(w, MusouEnemyType.Nob, Open + new Vector3(0f, 120f, 0f));
        var player = w.Players[Human];
        Assert.True(w.TfwFireForTests(false, true, Open, 0f));
        var run = fire.Run!.Value;
        Assert.True(run.From.Z >= Open.Z + TfwFireRules.RunHeight);
        Assert.True(run.From.Z >= TfwTestArena.WallHeight + TfwFireRules.RunClearance);
        Assert.Equal(2f * TfwFireRules.RunReach, run.Length, 1);
        var records = new List<TfwFireEvent>();
        fire.TakeEvents(records);
        var record = Assert.Single(records, r => r.Kind == TfwFireKind.Run);
        Assert.Equal((run.From, run.To), (record.At, record.To));
        for (int t = 0; fire.Run is not null && t < 20 * w.Config.TickRate; t++) { TfwRig.Steps(w, 1); fire.TakeEvents(records); }
        Assert.Null(fire.Run);
        Assert.Equal(2f * TfwFireRules.RunReach / TfwFireRules.RunSpeed, (w.Tick - run.Start) / (float)w.Config.TickRate, 0);
        Assert.True(fire.RoundsHit > 10, $"{fire.RoundsHit} hits");
        Assert.InRange(fire.RoundsHit / (float)fire.RoundsFired, 0.5f, 0.95f);
        float dealt = near.Sum(id => w.Musou!.Rules.Of(MusouEnemyType.Nob).Health - (w.Musou.TryGet(id, out var e) ? e.Health : 0f));
        Assert.True(dealt >= 10f * TfwFireRules.RoundDamage, $"{dealt} dealt by {fire.RoundsHit} hits, {fire.RoundKills} kills");   // not every hit: the battle keeps spawning its own Orks in reach, and a killing round wastes its rest
        Assert.True(w.Musou!.TryGet(far, out var untouched) && untouched.Health == w.Musou.Rules.Of(MusouEnemyType.Nob).Health);
        Assert.Equal((player.Health, player.Shield), (w.Players[Human].Health, w.Players[Human].Shield));
        var rounds = records.Where(r => r.Kind == TfwFireKind.Round).ToArray();
        Assert.Equal(fire.RoundsFired, rounds.Length);
        foreach (var r in rounds) Assert.True(r.At.Z > r.To.Z + 20f, "fired down from the aircraft");
    }

    [Fact]
    public void A_wall_stops_the_guns()
    {
        var w = Battle();
        var fire = w.Tfw!.Fire;
        // Behind the arena's wall, seen from a path outside: nothing in a clear line.
        ushort id = Enemy(w, MusouEnemyType.Nob, new Vector3(0f, TfwTestArena.ArenaHalf - 1f, 0f));
        float start = w.Musou!.Rules.Of(MusouEnemyType.Nob).Health;
        Assert.True(fire.Run is null);
        fire.BeginRunForTests(w.Tick, new Vector3(-300f, TfwTestArena.ArenaHalf + 15f, 12f), new Vector3(300f, TfwTestArena.ArenaHalf + 15f, 12f), w.Config.TickRate);
        for (int t = 0; fire.Run is not null && t < 15 * w.Config.TickRate; t++) TfwRig.Steps(w, 1);
        Assert.Equal(0, fire.RoundsFired);
        Assert.True(w.Musou.TryGet(id, out var e) && e.Health == start);
    }

    [Fact]
    public void Fire_KOs_credit_nobody_and_a_new_battle_clears_what_is_in_the_air()
    {
        var w = Battle();
        var fire = w.Tfw!.Fire;
        ushort id = Enemy(w, MusouEnemyType.Boy, Open);
        w.TfwShellForTests(Open);
        TfwRig.Steps(w, 2);
        Assert.Equal(0, w.Scores.GetValueOrDefault(Human).Kills);
        Assert.True(id != 0);
        w.TfwFireForTests(true, true, Open, 0f);
        Assert.NotEmpty(fire.Shells);
        w.Tfw!.PickupForTests(w);
        TfwRig.Steps(w, 2);
        Assert.Empty(fire.Shells);
        Assert.Null(fire.Run);
    }

    [Fact]
    public void The_same_tick_draws_the_same_barrage_and_a_salt_another()
    {
        static TfwShell[] Draw(ulong salt)
        {
            var w = Battle();
            w.Tfw!.Fire.Salt = salt;
            w.TfwFireForTests(true, false, Open, 1f);
            return w.Tfw.Fire.Shells.ToArray();
        }
        Assert.Equal(Draw(0), Draw(0));
        Assert.NotEqual(Draw(0), Draw(99));
    }

    [Fact]
    public void FireTest_is_a_point_command_ignored_without_the_servers_switch()
    {
        var c = new TfwCommandRecord(9, TfwCommand.FireTest, 3, new Vector3(60f, -60f, 0f));
        Assert.True(TfwWire.HasPoint(TfwCommand.FireTest));
        Assert.Equal(c, TfwWire.ReadCommand(TfwWire.WriteCommand(c)));
        var w = Battle();
        var fire = w.Tfw!.Fire;
        Assert.True(w.Tfw.Enqueue(Human, c));
        TfwRig.Steps(w, 2);
        Assert.Equal((0, 0), (fire.Barrages, fire.Runs));
        fire.TestCommands = true;
        Assert.True(w.Tfw.Enqueue(Human, c));
        TfwRig.Steps(w, 2);
        Assert.Equal((1, 0), (fire.Barrages, fire.Runs));   // the runs are paused
    }

    [Theory]
    [InlineData(TfwFireKind.Shell, 3)]
    [InlineData(TfwFireKind.Run, 3)]
    [InlineData(TfwFireKind.Round, 1)]
    public void A_shell_and_a_run_go_in_three_messages_a_round_in_one(TfwFireKind kind, int copies) => Assert.Equal(copies, TfwSendRules.FireCopies(kind));
}
