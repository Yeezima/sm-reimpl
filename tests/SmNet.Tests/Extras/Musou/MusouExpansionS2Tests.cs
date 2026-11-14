using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>Expansion stage S2 (docs/musou-expansion.md): the Bomb Squig (drop, fuse, shot, kick, chain, credit) and the Rokkit Boy (the collision rocket, its blast, the client's one warning).</summary>
public class MusouExpansionS2Tests
{
    const int Human = 1;
    static readonly MusouBox Box = new(new Vector3(-150f, -150f, -10f), new Vector3(150f, 150f, 40f));

    // ---------------------------------------------------------------- helpers

    /// <summary>A war on the test arena without a battle (the S1 rig): the human at (0, -60), an AI Marine 30 m east of him; the run idle, the drop director off.</summary>
    static World War(out int marine)
    {
        var config = MusouSimTests.Base with { RespawnTime = 60f };
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.TfwSquigDropsOn = false;
        w.Spawn(Human, 0);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        marine = w.TfwSpawnAlly(new Vector3(30f, -60f, 0f)) ?? 0;
        Assert.NotEqual(0, marine);
        Tick(w);
        return w;
    }

    static void Tick(World w) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.None));

    /// <summary>The Telegraph events of one cue in the world's last tick.</summary>
    static List<MusouEvent> Cues(World w, byte cue) => w.Musou!.Events.Where(e => e.Kind == MusouEventKind.Telegraph && e.Value == cue).ToList();

    static float Total(PlayerState p) => p.Health + p.Shield;

    /// <summary>A bare pool in a war (no graph, no world): Squigs walk straight.</summary>
    static MusouSim Pool() => new(null) { War = TfwCombatRules.Default };

    static void Step(MusouSim sim, uint tick, params MusouTarget[] targets)
    {
        sim.BeginTick(tick);
        sim.Step(targets, null, true, new List<MusouAttack>());
    }

    // ---------------------------------------------------------------- the drop director

    [Fact]
    public void Squigs_drop_only_in_a_battle_near_a_player_and_behind_him()
    {
        var w = TfwRig.War();
        w.Tfw!.Guardsmen = false;
        TfwRig.Seconds(w, MusouSquigRules.DropMaxSeconds + 10f);   // in the Stormbird: no battle
        Assert.Equal(0, w.TfwSquigsDropped);
        Assert.Equal(0, w.Musou!.CountAlive(MusouEnemyType.BombSquig));

        var battle = TfwRig.Deploy(w);
        battle.WeirdboyzOn = false;
        int drops = 0;
        for (int t = 0; t < (int)((MusouSquigRules.DropMaxSeconds + 2f) * 30f) && drops == 0; t++)
        {
            w.Step(_ => default);
            foreach (var e in Cues(w, MusouSquigRules.DropCue))
            {
                drops++;
                var p = w.Players[e.Player];
                var flat = new Vector2(e.Vector.X - p.Position.X, e.Vector.Y - p.Position.Y);
                Assert.InRange(flat.Length(), MusouSquigRules.DropMin - 0.5f, MusouSquigRules.DropMax + 0.5f);
                var view = new Vector2(-MathF.Sin(p.Yaw), MathF.Cos(p.Yaw));
                Assert.True(Vector2.Dot(Vector2.Normalize(flat), view) < MathF.Cos(MusouSquigRules.OffScreenDegrees * MathF.PI / 180f), "off-screen: behind his view");
                Assert.True(w.Musou.TryGet(e.Enemy, out var squig) && squig.Type == MusouEnemyType.BombSquig && squig.State == MusouEnemyState.Spawning, "it falls first");
            }
        }
        Assert.Equal(1, drops);
        Assert.True(w.TfwSquigsDropped >= 1);
    }

    // ---------------------------------------------------------------- the fuse

    [Fact]
    public void A_Squig_falls_then_lights_its_fuse_within_ten_metres_and_bursts_after_its_fuse()
    {
        var sim = Pool();
        ushort squig = sim.Spawn(MusouEnemyType.BombSquig, Vector3.Zero, 0f, 30f);
        var player = new MusouTarget(Human, new Vector3(0f, 30f, 0f), true);
        uint tick = 0;
        int fall = (int)MathF.Round(MusouSquigRules.FallSeconds * 30f);
        for (int k = 0; k < fall - 2; k++) Step(sim, ++tick, player);
        Assert.True(sim.TryGet(squig, out var falling) && falling.State == MusouEnemyState.Spawning && falling.Position == Vector3.Zero, "it stands still while it falls");
        uint lit = 0; float litAt = 0f;
        for (int k = 0; k < 200 && lit == 0; k++)
        {
            Step(sim, ++tick, player);
            if (sim.FuseTicks(squig) > 0) { lit = tick; sim.TryGet(squig, out var e); litAt = Vector3.Distance(e.Position, player.Position); }
        }
        Assert.NotEqual(0u, lit);
        Assert.InRange(litAt, MusouSquigRules.FuseRadius - 0.6f, MusouSquigRules.FuseRadius);
        Assert.Equal(MusouSquigRules.FuseSeconds * 30f, sim.FuseTicks(squig), 0.5f);
        var queued = new List<(ushort Id, int Credit)>();
        int fuse = sim.FuseTicks(squig);
        for (int k = 0; k < fuse - 1; k++) { Step(sim, ++tick, player); sim.TakeDetonations(queued); Assert.Empty(queued); }
        Step(sim, ++tick, player);
        sim.TakeDetonations(queued);
        Assert.Equal([(squig, 0)], queued);   // nobody to credit: no shot, no kick
        Assert.True(sim.TryGet(squig, out var dead) && dead.State == MusouEnemyState.Dying);
    }

    [Fact]
    public void A_Squig_runs_at_a_Guard_squad_and_an_AI_Marine_too()
    {
        var sim = Pool();
        ushort a = sim.Spawn(MusouEnemyType.BombSquig, Vector3.Zero, 0f, 30f);
        var squad = new MusouTarget(TfwContracts.GuardTargetId(0), new Vector3(25f, 0f, 0f), true, 3f);
        var far = new MusouTarget(Human, new Vector3(-60f, 0f, 0f), true);
        for (uint t = 1; t <= 90; t++) Step(sim, t, far, squad);
        Assert.True(sim.TryGet(a, out var e) && e.Position.X > 5f, $"it went for the squad: {e.Position}");
        Assert.True(sim.FuseTicks(a) > 0, "lit near the squad's men");
    }

    // ---------------------------------------------------------------- shot, kick, chain, credit

    [Fact]
    public void A_shot_sets_a_Squig_off_at_once_and_its_frag_blast_hits_all_four_kinds_with_the_shooters_credit()
    {
        var w = War(out int marine);
        var musou = w.Musou!;
        var guards = w.TfwGuards!;
        var centre = new Vector3(0f, -60f, 0f);
        ushort squig = musou.Spawn(MusouEnemyType.BombSquig, centre + new Vector3(0f, 3f, 0f), 0f, 30f);
        ushort boy = musou.Spawn(MusouEnemyType.Boy, centre + new Vector3(1.5f, 3f, 0f), 0f, 85f);
        ushort nob = musou.Spawn(MusouEnemyType.Nob, centre + new Vector3(-2f, 3f, 0f), 0f, 600f);
        Assert.True(squig != 0 && boy != 0 && nob != 0);
        Assert.True(guards.SpawnSquad(centre + new Vector3(0f, 6f, 0f)) >= 0);
        w.TfwRespawnAt(marine, centre + new Vector3(2f, 0f, 0f), 0f);
        float playerBefore = Total(w.Players[Human]), marineBefore = Total(w.Players[marine]);
        int killsBefore = w.Scores.GetValueOrDefault(Human).Kills;
        Assert.True(musou.Damage(squig, Human, 1f, Vector3.UnitY, out bool killed, cause: MusouDeathKind.Shot) && killed, "one round sets it off");
        Tick(w);
        var blast = Assert.Single(Cues(w, MusouSquigRules.BlastCue));
        Assert.Equal(squig, blast.Enemy);
        Assert.Equal(Human, blast.Player);
        Assert.True(Total(w.Players[Human]) < playerBefore, "the player is hurt");
        Assert.True(Total(w.Players[marine]) < marineBefore, "the AI Marine is hurt");
        Assert.True(musou.TryGet(nob, out var n) && n.Health < 600f, "the elite is hurt");
        Assert.False(musou.TryGet(boy, out var b) && b.Alive, "the Boy beside it dies");
        // The frag's data: 200 within 2.5 m, half to 5 m, 0.3 to 7 m, 0.05 at 10 m, nothing beyond.
        Assert.Equal(200f, MusouSquigRules.Blast.Damage);
        Assert.Equal(1f, MusouSquigRules.Blast.IntensityAt(2.4f));
        Assert.Equal(0.5f, MusouSquigRules.Blast.IntensityAt(4f));
        Assert.Equal(0.3f, MusouSquigRules.Blast.IntensityAt(6f));
        Assert.Equal(0.05f, MusouSquigRules.Blast.IntensityAt(10f), 4);
        Assert.Equal(0f, MusouSquigRules.Blast.IntensityAt(10.5f));
        Assert.True(w.Scores.GetValueOrDefault(Human).Kills >= killsBefore + 2, "the Squig's and the Boy's KOs are the shooter's");
    }

    [Fact]
    public void The_blast_damages_the_Guard_squad_men()
    {
        var w = War(out _);
        var guards = w.TfwGuards!;
        var at = new Vector3(-50f, 40f, 0f);
        Assert.True(guards.SpawnSquad(at) >= 0);
        int before = guards.Alive;
        var r = w.MusouOrkBlast(at, MusouSquigRules.Blast, Human);
        Assert.True(r.GuardMen > 0, $"men struck {r.GuardMen}, killed {r.GuardKills}");
        Assert.True(r.GuardKills + w.MusouOrkBlast(at, MusouSquigRules.Blast with { Damage = 1e5f }, Human).GuardKills > 0 && guards.Alive < before, "a big enough burst kills them");   // at GuardRules.Strength 0.875 the Squig's own may already
    }

    [Fact]
    public void A_kick_flings_a_Squig_twelve_metres_along_the_run_and_sets_an_unlit_fuse_to_one_second()
    {
        var sim = Pool();
        ushort squig = sim.Spawn(MusouEnemyType.BombSquig, Vector3.Zero, 0f, 30f);
        var far = new MusouTarget(Human, new Vector3(-80f, 0f, 0f), true);
        uint tick = 0;
        Assert.False(sim.KickSquig(squig, Human, Vector2.UnitY), "not while it falls");
        for (int k = 0; k < 40; k++) Step(sim, ++tick, far);
        sim.TryGet(squig, out var before);
        Assert.Equal(0, sim.FuseTicks(squig));
        Assert.True(sim.KickSquig(squig, Human, new Vector2(0f, 5f)));
        Assert.Equal(30, sim.FuseTicks(squig));
        Assert.True(sim.TryGet(squig, out var flying) && flying.State == MusouEnemyState.Knocked);
        var queued = new List<(ushort Id, int Credit)>();
        for (int k = 0; k < 18; k++) Step(sim, ++tick, far);
        Assert.True(sim.TryGet(squig, out var landed));
        Assert.InRange(landed.Position.Y - before.Position.Y, MusouSquigRules.KickDistance - 1f, MusouSquigRules.KickDistance + 0.5f);
        for (int k = 0; k < 13; k++) Step(sim, ++tick, far);
        sim.TakeDetonations(queued);
        Assert.Equal([(squig, Human)], queued);   // the kicker's within 10 s
        // A lit fuse is kept as it burns.
        ushort other = sim.Spawn(MusouEnemyType.BombSquig, new Vector3(0f, -30f, 0f), 0f, 30f);
        var near = new MusouTarget(Human, new Vector3(0f, -21f, 0f), true);
        for (int k = 0; k < 40; k++) Step(sim, ++tick, near);
        int left = sim.FuseTicks(other);
        Assert.True(left > 0 && left < 54);
        Assert.True(sim.KickSquig(other, Human, Vector2.UnitX));
        Assert.Equal(left, sim.FuseTicks(other));
    }

    [Fact]
    public void A_player_running_into_a_Squig_kicks_it_along_his_run()
    {
        var w = War(out _);
        var musou = w.Musou!;
        ushort squig = musou.Spawn(MusouEnemyType.BombSquig, new Vector3(-80f, 60f, 0f), 0f, 30f);   // far from the player: it lands and runs, unlit
        for (int k = 0; k < 45; k++) Tick(w);
        Assert.Equal(0, musou.FuseTicks(squig));
        musou.Frozen = true;   // now it waits where it is
        Assert.True(musou.TryGet(squig, out var s0) && s0.State == MusouEnemyState.Seek);
        w.TfwRespawnAt(Human, s0.Position - new Vector3(0f, 4f, 0f), 0f);
        bool kicked = false;
        for (int k = 0; k < 60 && !kicked; k++)
        {
            w.Step(id => new PlayerInput(w.Tick, 0f, id == Human ? 1f : 0f, 0f, id == Human ? InputButtons.Sprint : InputButtons.None));
            kicked = Cues(w, MusouSquigRules.KickCue).Any(e => e.Enemy == squig && e.Player == Human);
        }
        Assert.True(kicked, $"the run kicked it (speed {new Vector2(w.Players[Human].Velocity.X, w.Players[Human].Velocity.Y).Length():F1} m/s)");
        Assert.Equal(30, musou.FuseTicks(squig), 1f);
    }

    [Fact]
    public void Squigs_set_each_other_off_in_a_chain_one_a_tick()
    {
        var w = War(out _);
        var musou = w.Musou!;
        var line = new Vector3(-80f, 60f, 0f);   // far from the player
        ushort a = musou.Spawn(MusouEnemyType.BombSquig, line, 0f, 30f);
        ushort b = musou.Spawn(MusouEnemyType.BombSquig, line + new Vector3(8f, 0f, 0f), 0f, 30f);
        ushort c = musou.Spawn(MusouEnemyType.BombSquig, line + new Vector3(16f, 0f, 0f), 0f, 30f);
        ushort far = musou.Spawn(MusouEnemyType.BombSquig, line + new Vector3(40f, 0f, 0f), 0f, 30f);
        Assert.True(musou.Damage(a, Human, 5f, Vector3.Zero, out _, cause: MusouDeathKind.Shot));
        var order = new List<ushort>();
        for (int k = 0; k < 5; k++)
        {
            Tick(w);
            var blasts = Cues(w, MusouSquigRules.BlastCue);
            Assert.True(blasts.Count <= 1, "at most one Squig bursts a tick in a chain");
            order.AddRange(blasts.Select(e => e.Enemy));
            Assert.All(blasts, e => Assert.Equal(Human, e.Player));   // the chain keeps the shooter's credit
        }
        Assert.Equal([a, b, c], order);
        Assert.True(musou.TryGet(far, out var f) && f.Alive, "the one out of reach stays");
    }

    [Fact]
    public void A_Squig_blast_kill_credits_the_kicker_within_ten_seconds_and_no_one_after()
    {
        var sim = Pool();
        var far = new MusouTarget(Human, new Vector3(-80f, 0f, 0f), true);
        ushort squig = sim.Spawn(MusouEnemyType.BombSquig, Vector3.Zero, 0f, 30f);
        uint tick = 0;
        for (int k = 0; k < 40; k++) Step(sim, ++tick, far);
        Assert.True(sim.KickSquig(squig, 7, Vector2.UnitY));
        Step(sim, ++tick, far);
        Assert.True(sim.Damage(squig, 0, 1f, Vector3.Zero, out _, cause: MusouDeathKind.Blast));   // a Guardsman's round, say: no player of its own
        var queued = new List<(ushort Id, int Credit)>();
        sim.TakeDetonations(queued);
        Assert.Equal([(squig, 7)], queued);
    }

    // ---------------------------------------------------------------- the rocket

    [Fact]
    public void A_rocket_flies_at_sixteen_metres_a_second_and_stops_at_the_first_Ork_in_its_path()
    {
        var sim = Pool();
        sim.Frozen = true;
        ushort rokkit = sim.Spawn(MusouEnemyType.RokkitBoy, Vector3.Zero, 0f, 85f);
        ushort first = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 10f, 0f), 0f, 85f);
        ushort second = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 14f, 0f), 0f, 85f);
        Assert.True(rokkit != 0 && first != 0 && second != 0);
        ushort serial = sim.LaunchRocket(rokkit, new Vector3(0f, 40f, 1.0f));
        Assert.NotEqual(0, serial);
        Step(sim, 1);
        Assert.Equal(MusouRokkitRules.RocketSpeed / 30f, Vector3.Distance(sim.Rockets[0].Position, new Vector3(0f, MusouRokkitRules.MuzzleForward, MusouRokkitRules.MuzzleHeight)), 3);
        var blasts = new List<MusouRocketBlast>();
        for (uint t = 2; t < 60 && blasts.Count == 0; t++) { Step(sim, t); sim.TakeRocketBlasts(blasts); }
        var blast = Assert.Single(blasts);
        Assert.Equal(serial, blast.Serial);
        Assert.InRange(blast.Point.Y, 10f - sim.ShotBody(MusouEnemyType.Boy).Radius - 0.6f, 10f);   // at the first Boy's body (the shots' body), not the second
        Assert.Empty(sim.Rockets);
    }

    [Fact]
    public void A_rocket_stops_at_a_wall()
    {
        var sim = Pool();
        sim.Frozen = true;
        var level = TfwTestArena.Level();
        ushort rokkit = sim.Spawn(MusouEnemyType.RokkitBoy, new Vector3(0f, 90f, 0f), 0f, 85f);
        sim.LaunchRocket(rokkit, new Vector3(0f, 140f, 1.5f));   // the arena's north wall stands at y = 120
        var blasts = new List<MusouRocketBlast>();
        for (uint t = 1; t < 150 && blasts.Count == 0; t++) { sim.BeginTick(t); sim.Step([], level, true, new List<MusouAttack>()); sim.TakeRocketBlasts(blasts); }
        var blast = Assert.Single(blasts);
        Assert.InRange(blast.Point.Y, TfwTestArena.ArenaHalf - 0.3f, TfwTestArena.ArenaHalf);
    }

    [Fact]
    public void A_rocket_bursts_on_a_player_and_its_Ork_blast_sets_off_a_Squig()
    {
        var w = War(out _);
        var musou = w.Musou!;
        musou.Frozen = true;
        var at = new Vector3(-60f, 40f, 0f);
        ushort rokkit = musou.Spawn(MusouEnemyType.RokkitBoy, at, 0f, 85f);
        ushort boy = musou.Spawn(MusouEnemyType.Boy, at + new Vector3(0f, 20f, 0f), 0f, 85f);
        ushort squig = musou.Spawn(MusouEnemyType.BombSquig, at + new Vector3(2f, 20f, 0f), 0f, 30f);
        ushort serial = musou.LaunchRocket(rokkit, at + new Vector3(0f, 20f, 1f));
        bool rocket = false, squigBurst = false;
        for (int k = 0; k < 90 && !squigBurst; k++)
        {
            Tick(w);
            rocket |= Cues(w, MusouRokkitRules.BlastCue).Any(e => e.Player == serial);
            squigBurst |= Cues(w, MusouSquigRules.BlastCue).Any(e => e.Enemy == squig);
        }
        Assert.True(rocket, "the rocket burst");
        Assert.True(squigBurst, "its blast set the Squig off");
        Assert.False(musou.TryGet(boy, out var b) && b.Alive, "the Boy it struck (an Ork) is dead: an Ork explosion hurts Orks");
        Assert.True(musou.TryGet(rokkit, out var r) && r.Alive, "the Rokkit Boy 20 m off is untouched");

        // On a player: the rocket stops at his chest.
        w.TfwRespawnAt(Human, at + new Vector3(0f, -20f, 0f), 0f);
        Tick(w);
        ushort serial2 = musou.LaunchRocket(rokkit, w.Players[Human].Position + new Vector3(0f, 0f, 1f));
        Assert.NotEqual(0, serial2);
        float before = Total(w.Players[Human]);
        bool hit = false;
        for (int k = 0; k < 200 && !hit; k++) { Tick(w); hit = Cues(w, MusouRokkitRules.BlastCue).Any(e => e.Player == serial2); }
        Assert.True(hit);
        Assert.True(Total(w.Players[Human]) < before, "the player is hurt");
    }

    [Fact]
    public void A_Rokkit_Boy_holds_his_band_and_fires_every_five_seconds()
    {
        var sim = Pool();
        ushort rokkit = sim.Spawn(MusouEnemyType.RokkitBoy, Vector3.Zero, 0f, 85f);
        var player = new MusouTarget(Human, new Vector3(0f, 35f, 0f), true);
        int fired = 0;
        for (uint t = 1; t <= 30 * 16; t++)
        {
            Step(sim, t, player);
            fired += sim.Events.Count(e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouRokkitRules.FireCue && e.Enemy == rokkit);
        }
        Assert.InRange(fired, 3, 4);   // 16 s at one every 5 s
        Assert.True(sim.TryGet(rokkit, out var e) && Vector3.Distance(e.Position, player.Position) is >= 24f and <= 46f, "in his band");
        var spec = sim.Rules.Of(MusouEnemyType.RokkitBoy);
        Assert.Equal((25f, 45f, 5f), (spec.RangedMin, spec.RangedMax, spec.RangedPeriod));
        Assert.Equal((3.5f, 120f), (MusouRokkitRules.Blast.Radius, MusouRokkitRules.Blast.Damage));
        Assert.Equal(0.25f, MusouRokkitRules.Blast.IntensityAt(3.5f), 4);
    }

    [Fact]
    public void Rokkit_Boyz_join_a_battles_crowds_at_their_share()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        TfwRig.Seconds(w, 20f);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        int rokkits = all.Take(n).Count(e => e.Type == MusouEnemyType.RokkitBoy);
        Assert.True(rokkits > 0, "some Rokkit Boyz came");
        Assert.True(rokkits < n / 5, $"a small share: {rokkits} of {n}");
        battle.RokkitsOn = false;
        Assert.False(battle.RokkitsOn);
    }

    // ---------------------------------------------------------------- the client's warning

    [Fact]
    public void Two_incoming_rockets_give_exactly_one_warning()
    {
        var warner = new MusouRocketWarner();
        var me = new Vector3(0f, 0f, 1f);
        // Two rockets from left and right, both 2 s out, a little apart in time.
        var a = new MusouRocketTrack(1, new Vector3(-32f, 0f, 1f), new Vector3(16f, 0f, 0f));
        var b = new MusouRocketTrack(2, new Vector3(0f, 33f, 1f), new Vector3(0f, -16f, 0f));
        int started = 0; double now = 0;
        for (int f = 0; f < 150; f++, now += 1.0 / 60.0)
        {
            var pa = a with { Position = a.Position + a.Velocity * (float)now };
            var pb = b with { Position = b.Position + b.Velocity * (float)now };
            ushort s = warner.Update(now, me, [pa, pb]);
            if (s != 0) { started++; Assert.Equal(1, s); Assert.InRange(now, 0.95, 1.05); }   // the nearest, about 1 s before it strikes
        }
        Assert.Equal(1, started);
        Assert.Equal(1, warner.Started);
        // A rocket that misses by far never warns; a later one warns again (one at a time, not muted for good).
        Assert.Null(MusouRocketWarner.Threat(me, new MusouRocketTrack(3, new Vector3(-30f, 20f, 1f), new Vector3(16f, 0f, 0f))));
        var c = new MusouRocketTrack(4, new Vector3(0f, -20f, 1f), new Vector3(0f, 16f, 0f));
        int again = 0;
        for (int f = 0; f < 90; f++, now += 1.0 / 60.0) if (warner.Update(now, me, [c with { Position = c.Position + c.Velocity * (f / 60f) }]) != 0) again++;
        Assert.Equal(1, again);
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void The_Squig_and_rocket_cues_and_entities_round_trip()
    {
        var events = new List<MusouEvent>
        {
            new(MusouEventKind.Telegraph, 100, 5, 1, MusouSquigRules.DropCue, new Vector3(10f, -20f, 0.5f)),
            new(MusouEventKind.Telegraph, 101, 5, 54, MusouSquigRules.FuseCue, new Vector3(11f, -20f, 0.5f)),
            new(MusouEventKind.Telegraph, 102, 5, 1, MusouSquigRules.KickCue, new Vector3(23f, -20f, 0.5f)),
            new(MusouEventKind.Telegraph, 103, 5, 1, MusouSquigRules.BlastCue, new Vector3(23f, -20f, 0.9f)),
            new(MusouEventKind.Telegraph, 104, 9, 777, MusouRokkitRules.FireCue, new Vector3(0f, 35f, 1f)),
            new(MusouEventKind.Telegraph, 110, 9, 777, MusouRokkitRules.BlastCue, new Vector3(0f, 30f, 1f)),
        };
        var back = MusouWire.ReadEvents(MusouWire.WriteEvents(events, Box), Box);
        Assert.NotNull(back);
        Assert.Equal(events.Select(e => (e.Kind, e.Tick, e.Enemy, e.Player, e.Value)), back.Select(e => (e.Kind, e.Tick, e.Enemy, e.Player, e.Value)));
        for (int k = 0; k < events.Count; k++) Assert.True(Vector3.Distance(events[k].Vector, back[k].Vector) < 0.05f);
        var entities = new List<MusouEntity>
        {
            new(1, new Vector3(3f, 4f, 0f), 1f, MusouEnemyType.BombSquig, MusouEnemyState.WindUp),
            new(2, new Vector3(3f, 4f, 0f), 1f, MusouEnemyType.BombSquig, MusouEnemyState.Knocked),
            new(3, new Vector3(-3f, 4f, 0f), 2f, MusouEnemyType.RokkitBoy, MusouEnemyState.Seek),
        };
        var read = MusouWire.ReadEntities(MusouWire.WriteEntities(entities, Box), Box);
        Assert.NotNull(read);
        Assert.Equal(entities.Select(e => (e.Id, e.Type, e.State)), read.Select(e => (e.Id, e.Type, e.State)));
        Assert.True(MusouSim.Version >= 14u);   // S2 raised it; later stages raise it further
        Assert.Equal(74u, Protocol.Version);   // main's protocol (the damage mark raised it to 71, headshots raised it to 64 at integration, net/responsive to 65, the charge cancel to 66, the ally slide to 67, the Classic stims to 68); the Musou expansion does not raise it
    }

    // ---------------------------------------------------------------- isolation

    [Fact]
    public void The_skirmish_never_drops_Squigs_or_sends_Rokkit_Boyz()
    {
        var w = new World(MusouSimTests.Base) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.Spawn(Human, 0);
        for (int k = 0; k < 30 * 70; k++) w.Step(id => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None));
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        Assert.DoesNotContain(all.Take(n), e => e.Type is MusouEnemyType.BombSquig or MusouEnemyType.RokkitBoy);
        Assert.Empty(w.Musou.Rockets);
    }
}
