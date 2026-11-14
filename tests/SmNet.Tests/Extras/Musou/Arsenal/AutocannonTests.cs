using System.Numerics;
using ContentArsenal = SmContent.Extras.Musou.Arsenal.Arsenal;
using SmNet.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Arsenal;

/// <summary>
/// W1, the Musou autocannon (docs/new-weapons-design.md section 4), in a war on the synthetic arena: the install's Chaos file's numbers (here its values written out as the reader gives them) become the
/// Space Marines' full auto gun, one round every 0.4 s, overheating after 15 rounds, through 5 Orks with a 2.5 m bolt-style blast at each Ork a round strikes; nothing of it outside a Musou
/// config and the bolter's blasts as they were. The autocannon pass (the owner, 2026-11-11): the blast is the same everywhere in its reach, so it kills every fodder Ork at its centre and its edge; the
/// round is the blast's + 30. The autocannon pass 2 (the same day: "Stormboy is not fodder", "No clip in Musou mode - just infinite shooting"): the fodder are the Boyz, Shoota Boyz, Rokkit Boyz
/// (85), Gretchin and Bomb Squigs (30), so the blast is 71 (85.2 after a human's x1.2) and the round 101 (121.2); the autocannon has no clip: no shot spends a round, nothing refills or reloads it.
/// </summary>
public class AutocannonTests
{
    // The Chaos file as WeaponTuning.Read gives it (spec/behavior.md "Autocannon"): hit-scan, a 2-round burst 0.2 s apart, 120 rounds.
    static readonly WeaponSpec ChaosFile = new("csm_mp_exotic_auto_cannon", WeaponSlot.Ranged, TriggerKind.SemiAuto, 50f, 0.4f, 120, 0, 2f, 150f, 45f, 70f, 0.75f, 1f, 1f, 1f, 1f)
    {
        Heat = new HeatSpec(1f, 15f, 9f, 5f, 4.5f, 0.41f, 0.41f), BurstLength = 2, BurstInterval = 0.2f,
    };
    static readonly WeaponSpec HeavyBolter = new("sm_mp_exotic_heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 35f, 0.233f, 400, 0, 3f, 250f, 50f, 75f, 0.8f, 1f, 1f, 0.7f, 0.7f)
    {
        RaiseDelay = 0.333f, AlwaysReady = true,
    };
    static readonly WeaponSpec Bolter = HeavyBolter with { Name = "sm_mp_ranged_bolter", Damage = 1f, ShotCooldown = 0.12f, Heat = null };
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    const int Human = 1;

    /// <summary>The Musou config with the autocannon appended as the content layer does (0 heavy bolter, 1 bolter, 2 the autocannon).</summary>
    static SimConfig Config()
    {
        var devastator = new ClassLoadout("sm - devastator", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "heavy" };
        var tactical = new ClassLoadout("sm - tactical", 1, -1, [Light]) { PrimaryChoices = [1], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        var config = MusouSimTests.Base with
        {
            Weapons = [HeavyBolter, Bolter], Thrown = [], Classes = [devastator, tactical, devastator with { Name = "csm - havoc", Side = Team.Chaos }], Extras = ContentExtras.Musou, MusouRules = Still, RespawnTime = 60f,
        };
        return MusouLoadout.Prepare(ContentArsenal.WithAutocannon(config, ChaosFile, []));   // as the content layer: the arsenal, then the Musou strip
    }

    static World War(Vector3 at)
    {
        var config = Config();
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0, primaryChoice: 1);   // the Devastator, asking for the second of his primaries: the autocannon
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, at, 0f);
        Tick(w);
        w.Musou!.Frozen = true;
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));
    static ushort Boy(World w, Vector3 at, float health = 100000f) { ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, at, 0f, health); Assert.NotEqual(0, id); return id; }
    static float HealthOf(World w, ushort id) => w.Musou!.TryGet(id, out var e) ? e.Health : 0f;

    // ---------------------------------------------------------------- the weapon

    [Fact]
    public void The_twin_is_full_auto_one_round_every_four_tenths_with_the_files_heat_and_the_heavy_bolters_handling()
    {
        var twin = ContentArsenal.Twin(ChaosFile, HeavyBolter);
        Assert.Equal(ArsenalRules.Autocannon, twin.Name);
        Assert.Equal(TriggerKind.FullAuto, twin.Trigger);
        Assert.Equal(0.4f, twin.ShotCooldown);
        Assert.Equal(AutocannonRules.SecondsPerRound, twin.ShotCooldown);
        Assert.Equal(50f, twin.Damage);   // the shared twin keeps the file's round; Musou's own is the blast's + 30 (below)
        var musou = ContentArsenal.MusouTwin(ChaosFile, HeavyBolter);
        Assert.Equal(AutocannonRules.BlastDamage + 30f, musou.Damage);   // the owner, 2026-11-11: "the damage value of the aoe explosion effect + 30"
        Assert.Equal(101f, musou.Damage);   // 71 + 30 (130 before the Stormboy left the fodder)
        Assert.Equal(twin with { Damage = musou.Damage }, musou);   // nothing else differs
        Assert.True(musou.Damage > HeavyBolter.Damage && musou.ShotCooldown > HeavyBolter.ShotCooldown);   // slower and harder than the heavy bolter
        Assert.False(twin.Bursts);
        Assert.Equal((400, 0), (twin.ClipSize, twin.ReserveSize));
        Assert.Equal((150f, 45f, 70f, 0.75f), (twin.MaxRange, twin.FallOffStart, twin.FallOffEnd, twin.FallOffEndMultiplier));
        Assert.Equal(new HeatSpec(1f, 15f, 9f, 5f, 4.5f, 0.41f, 0.41f), twin.Heat);   // 15 rounds, then too hot
        Assert.Null(twin.Brace);   // the owner: no brace
        Assert.Equal((HeavyBolter.RaiseDelay, HeavyBolter.AlwaysReady), (twin.RaiseDelay, twin.AlwaysReady));
    }

    [Fact]
    public void It_joins_the_Space_Marine_Devastators_primary_list_alone_at_the_end_and_no_index_moves()
    {
        var config = Config();
        Assert.Equal(3, config.Weapons.Count);
        Assert.Equal(ArsenalRules.Autocannon, config.Weapons[2].Name);
        Assert.Equal(AutocannonRules.DirectDamage, config.Weapons[2].Damage);   // Musou's round
        Assert.Equal([0, 2], config.Classes[0].PrimaryChoices);   // the Devastator: heavy bolter, then the autocannon
        Assert.Equal([1], config.Classes[1].PrimaryChoices);      // the Tactical's list is as it was
        Assert.Equal([0], config.Classes[2].PrimaryChoices);      // so is the Chaos Havoc's
    }

    [Fact]
    public void Without_a_heavy_bolter_nothing_is_appended_and_a_note_says_so()
    {
        var bare = MusouSimTests.Base with { Extras = ContentExtras.Musou };
        var notes = new List<string>();
        Assert.Same(bare, ContentArsenal.WithAutocannon(bare, ChaosFile, notes));
        Assert.Contains(notes, n => n.Contains("heavy_bolter"));
    }

    [Fact]
    public void The_animations_are_the_heavy_bolters_and_no_other_name_is_touched()
    {
        Assert.Equal("sm_mp_exotic_heavy_bolter", ContentArsenal.AliasOf(ArsenalRules.Autocannon));
        Assert.Equal("sm_mp_exotic_heavy_bolter", SmContent.AnimationAlias.Of(ArsenalRules.Autocannon));
        foreach (var name in new[] { "sm_mp_exotic_heavy_bolter", "csm_mp_exotic_auto_cannon", "sm_mp_ranged_bolter" }) Assert.Equal(name, ContentArsenal.AliasOf(name));
    }

    // ---------------------------------------------------------------- the firing

    [Fact]
    public void It_fires_a_round_every_four_tenths_of_a_second_while_the_trigger_is_held()
    {
        var w = War(new Vector3(0f, -60f, 0f));
        Assert.Equal(ArsenalRules.Autocannon, Loadouts.InHand(w.Config, w.Players[Human])!.Name);
        ushort id = Boy(w, w.Players[Human].Position + new Vector3(0f, 8f, 0f));
        var ticks = new List<uint>();
        float last = HealthOf(w, id);
        var drops = new List<float>();
        for (int k = 0; k < 3 * w.Config.TickRate; k++)
        {
            Tick(w, InputButtons.Fire);
            if (w.LastShots.Count > 0) ticks.Add(w.Tick);
            float now = HealthOf(w, id);
            if (now < last - 1e-3f) drops.Add(last - now);
            last = now;
        }
        Assert.InRange(ticks.Count, 6, 8);   // 2.5 a second over three seconds, give or take the first shot's wait
        for (int k = 1; k < ticks.Count; k++)
            Assert.InRange((ticks[k] - ticks[k - 1]) / (float)w.Config.TickRate, 0.4f - 1.5f / w.Config.TickRate, 0.4f + 1.5f / w.Config.TickRate);
        float ranged = TfwCombatRules.Default.PlayerRangedDamageScale;   // x1.2 in the war (owner 2026-11-11: "increase all ranged attack damage by 20%")
        Assert.Equal(1.2f, ranged);
        // A round is 101 and the blast on the Ork it strikes 71, in the same tick: 206.4 after the x1.2 (121.2 + 85.2).
        Assert.All(drops, d => Assert.Equal((AutocannonRules.DirectDamage + AutocannonRules.BlastDamage) * ranged, d, 0.5f));
        Assert.Equal(206.4f, (AutocannonRules.DirectDamage + AutocannonRules.BlastDamage) * ranged, 0.01f);
    }

    [Fact]
    public void It_overheats_after_fifteen_rounds()
    {
        var w = War(new Vector3(0f, -60f, 0f));
        int shots = 0;
        for (int k = 0; k < 12 * w.Config.TickRate && !w.Players[Human].Overheated; k++)
        {
            Tick(w, InputButtons.Fire);
            shots += w.LastShots.Count;
        }
        Assert.True(w.Players[Human].Overheated);
        Assert.Equal(15, shots);
    }

    // ---------------------------------------------------------------- the blast

    [Fact]
    public void A_round_blasts_2_5_metres_at_every_Ork_it_strikes_through_five_and_the_sixth_that_stops_it()
    {
        var w = War(new Vector3(0f, -60f, 0f));
        var me = w.Players[Human].Position;
        var line = Enumerable.Range(0, 7).Select(k => Boy(w, me + new Vector3(0f, 8f + k * 6f, 0f))).ToArray();
        // Beside the first, 2.0 m off the ray: outside the bolt blast's 1.25 m, inside the autocannon's 2.5 m.
        ushort beside = Boy(w, me + new Vector3(2.0f, 8f, 0f));
        // Beside the second, 4.0 m off: outside both (the burst is 3.5 m since the AoE pass, 2026-11-14).
        ushort outside = Boy(w, me + new Vector3(4.0f, 14f, 0f));
        Tick(w, InputButtons.Fire);
        for (int k = 0; k < 20 && w.LastShots.Count == 0; k++) Tick(w, InputButtons.Fire);
        Assert.NotEmpty(w.LastShots);
        Assert.Equal(6, w.TfwBoltBlastsOut);   // five pierced and the one that stops it: a blast at each of the six struck
        Assert.Equal(6, line.Count(id => HealthOf(w, id) < 100000f));
        Assert.Equal(100000f, HealthOf(w, line[6]));
        var records = w.Tfw!.Fire.Events.Where(f => f.Kind == TfwFireKind.BoltBlast).ToArray();
        Assert.Equal(6, records.Length);
        Assert.All(records, r => Assert.Equal(AutocannonRules.BlastRadius * SmSim.Extras.Musou.MusouCombatRules.AutocannonAoeScale, r.To.Y, 0.06f));   // the clients get the radius (x1.4, AoE pass 2026-11-14)
        float hurt = 100000f - HealthOf(w, beside);
        Assert.Equal(AutocannonRules.BlastDamage * TfwCombatRules.Default.PlayerRangedDamageScale, hurt, 0.01f);   // a blast alone, all of it: the ray passes 2 m off him (no fall-off since 2026-11-11)
        Assert.Equal(100000f, HealthOf(w, outside));
    }

    [Fact]
    public void The_blast_follows_the_bolt_blasts_rules_a_kill_is_torn_apart_where_he_stands()
    {
        var w = War(new Vector3(0f, -60f, 0f));
        var me = w.Players[Human].Position;
        Boy(w, me + new Vector3(0f, 8f, 0f));
        ushort victim = Boy(w, me + new Vector3(2.0f, 8f, 0f), health: 1f);   // dies to the blast alone
        MusouEvent? death = null;
        for (int k = 0; k < 30 && death is null; k++)
        {
            Tick(w, InputButtons.Fire);
            foreach (var e in w.Musou!.Events) if (e.Kind == MusouEventKind.Death && e.Enemy == victim) death = e;
        }
        Assert.NotNull(death);
        Assert.Equal((byte)MusouDeathKind.BoltBlast, death!.Value.Value);
    }

    [Fact]
    public void The_blast_is_twice_the_bolt_blasts_size_and_full_to_its_edge()
    {
        var war = TfwCombatRules.Default;
        Assert.Equal(1.25f, war.BoltBlastRadius);
        Assert.Equal(2f, AutocannonRules.BlastRadius / war.BoltBlastRadius);   // twice the size (owner D12)
        Assert.Equal(71f, AutocannonRules.BlastDamage);
        foreach (float d in new[] { 0f, 0.5f, 1f, 1.8f, 2.4f, 2.5f }) Assert.Equal(1f, Combat.SplashIntensity(AutocannonRules.BlastFallOff, d));   // no fall-off (owner 2026-11-11: a one-shot inside the radius)
        Assert.Equal(0f, Combat.SplashIntensity(AutocannonRules.BlastFallOff, 2.6f));
        Assert.Equal(AutocannonRules.BlastFallOff, AutocannonRules.Blast.FallOff);
        Assert.Equal(AutocannonRules.BlastDamage, AutocannonRules.Blast.Damage.Damage);
        Assert.Equal(1f, AutocannonRules.Blast.Damage.BodyMultiplier);
    }

    // ---------------------------------------------------------------- the autocannon pass (owner 2026-11-11): one blast kills any fodder Ork

    [Fact]
    public void The_fodder_are_every_Ork_that_is_neither_an_elite_nor_a_boss_nor_the_Stormboy_and_the_blast_after_the_x1_2_kills_the_toughest()
    {
        // The owner, 2026-11-11: "Stormboy is not fodder".
        Assert.Equal([MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.Gretchin, MusouEnemyType.RokkitBoy, MusouEnemyType.BombSquig], AutocannonRules.Fodder);
        Assert.DoesNotContain(MusouEnemyType.Stormboy, AutocannonRules.Fodder);
        var rules = MusouRules.Default;
        Assert.Equal([85f, 85f, 30f, 85f, 30f], AutocannonRules.Fodder.Select(t => rules.Of(t).Health));
        Assert.Equal(85f, AutocannonRules.FodderHealthMost(rules));   // the Boy, the Shoota Boy, the Rokkit Boy
        Assert.Equal(120f, rules.Of(MusouEnemyType.Stormboy).Health);   // out of the rule
        foreach (var t in AutocannonRules.Fodder)
        {
            Assert.False(TfwRewardRules.Elite(t), t.ToString());
            Assert.False(MusouPowerupRules.Elite(t), t.ToString());
            Assert.False(MusouBossOrks.Is(t), t.ToString());
        }
        Assert.True(MusouSim.Fodder(MusouEnemyType.Boy) && MusouSim.Fodder(MusouEnemyType.ShootaBoy) && MusouSim.Fodder(MusouEnemyType.Gretchin));   // the war's fodder rule, all in the list
        float ranged = TfwCombatRules.Default.PlayerRangedDamageScale;
        // The blast as the war deals it (World.MusouBlast: damage x intensity x body multiplier x the x1.2, in single precision): 85.2000046, so an 85 Ork's health goes below 0.
        float dealt = AutocannonRules.Blast.Damage.Damage * Combat.SplashIntensity(AutocannonRules.BlastFallOff, AutocannonRules.BlastRadius) * AutocannonRules.Blast.Damage.BodyMultiplier * ranged;
        Assert.True(dealt > AutocannonRules.FodderHealthMost(rules), $"{dealt:R}");
        Assert.True(MathF.Max(0f, 85f - dealt) <= 0f);
        Assert.True(AutocannonRules.BlastDamage < AutocannonRules.FodderHealthMost(rules));   // the base alone would leave a Boy standing: the rule counts the x1.2
        Assert.True((AutocannonRules.BlastDamage - 1f) * ranged < AutocannonRules.FodderHealthMost(rules));   // and 71 is the least that does (70 x 1.2 = 84)
        Assert.Equal(85.2f, AutocannonRules.BlastDamage * ranged, 0.01f);
        Assert.Equal(121.2f, AutocannonRules.DirectDamage * ranged, 0.01f);
    }

    /// <summary>The point the round's first blast goes off at, struck on a Boy 8 m ahead (the war's own geometry: the ray at the shooter's eye, the Ork's near side).</summary>
    static Vector3 FirstBlastPoint()
    {
        var w = War(new Vector3(0f, -60f, 0f));
        Boy(w, w.Players[Human].Position + new Vector3(0f, 8f, 0f));
        for (int k = 0; k < 20 && w.LastShots.Count == 0; k++) Tick(w, InputButtons.Fire);
        return Assert.Single(w.Tfw!.Fire.Events, f => f.Kind == TfwFireKind.BoltBlast).At;
    }

    public static TheoryData<MusouEnemyType> FodderTypes => new(AutocannonRules.Fodder);

    [Theory]
    [MemberData(nameof(FodderTypes))]
    public void One_blast_kills_a_fodder_Ork_at_its_centre_and_at_its_edge(MusouEnemyType type)
    {
        var point = FirstBlastPoint();
        var w = War(new Vector3(0f, -60f, 0f));
        var me = w.Players[Human].Position;
        float health = w.Musou!.Rules.Of(type).Health;
        float height = w.Musou.Body(type).Height;
        // The Ork the round strikes (a tough stand-in, so nothing changes on the ray), one beside the blast's point and one at its edge, both off the ray.
        Boy(w, me + new Vector3(0f, 8f, 0f));
        // On the floor beside the point (off the ray, so the blast alone reaches them): the blast measures to the chest, 0.55 of the height up.
        float dz = point.Z - MusouSim.Chest(me, height).Z;
        Vector3 At(float distance, Vector2 way)
        {
            float flat = MathF.Sqrt(MathF.Max(0f, distance * distance - dz * dz));
            var d = Vector2.Normalize(way) * flat;
            return new Vector3(point.X + d.X, point.Y + d.Y, me.Z);
        }
        ushort centre = w.Musou.Spawn(type, new Vector3(point.X + 0.95f, point.Y + 0.3f, me.Z), 0f, health);
        ushort edge = w.Musou.Spawn(type, At(AutocannonRules.BlastRadius - 0.08f, new Vector2(1f, 0.4f)), 0f, health);
        Assert.NotEqual(0, centre);
        Assert.NotEqual(0, edge);
        Assert.True(w.Musou.TryGet(edge, out var e0));
        Assert.InRange(Vector3.Distance(MusouSim.Chest(e0.Position, height), point), AutocannonRules.BlastRadius - 0.2f, AutocannonRules.BlastRadius);   // at the edge
        Assert.True(w.Musou.TryGet(centre, out var c0));
        Assert.InRange(Vector3.Distance(MusouSim.Chest(c0.Position, height), point), 0f, 1.6f);   // near the centre (not on the ray: a blast alone)
        var deaths = new HashSet<ushort>();
        for (int k = 0; k < 20 && w.LastShots.Count == 0; k++) Tick(w, InputButtons.Fire);
        Assert.Single(w.LastShots);
        foreach (var ev in w.Musou.Events) if (ev.Kind == MusouEventKind.Death) deaths.Add(ev.Enemy);
        Assert.Contains(centre, deaths);
        Assert.Contains(edge, deaths);
    }

    [Fact]
    public void A_Stormboy_beside_the_blast_is_not_fodder_and_lives_through_it()
    {
        // The owner, 2026-11-11: "Stormboy is not fodder": his 120 takes the blast's 85.2 and stands.
        var point = FirstBlastPoint();
        var w = War(new Vector3(0f, -60f, 0f));
        var me = w.Players[Human].Position;
        Boy(w, me + new Vector3(0f, 8f, 0f));
        float health = w.Musou!.Rules.Of(MusouEnemyType.Stormboy).Health;
        ushort storm = w.Musou.Spawn(MusouEnemyType.Stormboy, new Vector3(point.X + 0.95f, point.Y + 0.3f, me.Z), 0f, health);
        Assert.NotEqual(0, storm);
        for (int k = 0; k < 20 && w.LastShots.Count == 0; k++) Tick(w, InputButtons.Fire);
        Assert.Single(w.LastShots);
        Assert.True(w.Musou.TryGet(storm, out var e) && e.Alive);
        Assert.Equal(health - AutocannonRules.BlastDamage * TfwCombatRules.Default.PlayerRangedDamageScale, e.Health, 0.01f);   // 34.8 left
    }

    [Theory]
    [MemberData(nameof(FodderTypes))]
    public void A_round_kills_the_fodder_Ork_it_strikes(MusouEnemyType type)
    {
        var w = War(new Vector3(0f, -60f, 0f));
        var me = w.Players[Human].Position;
        ushort id = w.Musou!.Spawn(type, me + new Vector3(0f, 8f, 0f), 0f, w.Musou.Rules.Of(type).Health);
        Assert.NotEqual(0, id);
        // Aimed at his chest (the Gretchin and the Squig are under the eye's level).
        float pitch = MathF.Atan2(MusouSim.Chest(me, w.Musou.Body(type).Height).Z - (me.Z + w.Config.EyeHeight), 8f);
        bool died = false;
        for (int k = 0; k < 20 && !died; k++)
        {
            w.Step(p => new PlayerInput(w.Tick, 0f, 0f, w.Players[p].Yaw, InputButtons.Fire, Pitch: pitch));
            foreach (var ev in w.Musou.Events) if (ev.Kind == MusouEventKind.Death && ev.Enemy == id) died = true;
        }
        Assert.True(died);
        Assert.Single(w.LastShots);   // the first round
    }

    [Fact]
    public void It_has_no_clip_in_the_war_no_shot_spends_a_round()
    {
        // The owner, 2026-11-11: "No clip in Musou mode - just infinite shooting".
        var w = War(new Vector3(0f, -60f, 0f));
        Assert.Equal(AutocannonRules.Clip, w.Players[Human].Clip);
        int shots = 0;
        for (int k = 0; k < 4 * w.Config.TickRate && !w.Players[Human].Overheated; k++)
        {
            Tick(w, InputButtons.Fire);
            shots += w.LastShots.Count;
            Assert.Equal(AutocannonRules.Clip, w.Players[Human].Clip);   // never moves: no round leaves it (nothing puts one back)
            Assert.Equal(0, w.Players[Human].Reserve);
            Assert.Equal(0f, w.Players[Human].ReloadLeft);
        }
        Assert.InRange(shots, 8, 15);
        Assert.True(AutocannonRules.EndlessClip(w.Config.Weapons[2]));
        Assert.False(AutocannonRules.EndlessClip(w.Config.Weapons[0]));   // the heavy bolter keeps Musou's rule (a fresh clip once empty)
        // The rule is the config's (the shot step's, so the server and the client's prediction agree): Musou's autocannon only.
        Assert.True(w.Config.EndlessClip(w.Config.Weapons[2]));
        Assert.False(w.Config.EndlessClip(w.Config.Weapons[0]));
        Assert.False((w.Config with { Extras = ContentExtras.All }).EndlessClip(w.Config.Weapons[2]));   // Classic PvP's autocannon spends its clip
        Assert.False((w.Config with { Extras = ContentExtras.None }).EndlessClip(w.Config.Weapons[2]));
    }

    [Fact]
    public void The_shot_step_alone_spends_no_round_so_the_clients_prediction_agrees_and_R_reloads_nothing()
    {
        // Combat.StepPlayer is what the client predicts with: no World, no war step after it.
        var w = War(new Vector3(0f, -60f, 0f));
        var s = w.Players[Human];
        int fired = 0;
        for (int k = 0; k < 3 * w.Config.TickRate; k++)
        {
            var buttons = k % 20 == 10 ? InputButtons.Reload : InputButtons.Fire;
            s = Combat.StepPlayer(s, new PlayerInput((uint)k + 1, 0f, 0f, s.Yaw, buttons), w.Config, out var ev);
            if (ev.Fired) fired++;
            Assert.Equal(AutocannonRules.Clip, s.Clip);
            Assert.Equal(0f, s.ReloadLeft);   // R starts no reload: the clip is full and there is no reserve
        }
        Assert.InRange(fired, 4, 8);
        Assert.True(s.Heat > 0f);   // the heat still runs
    }

    [Fact]
    public void Its_heat_still_runs_with_no_clip()
    {
        var w = War(new Vector3(0f, -60f, 0f));
        int shots = 0;
        for (int k = 0; k < 12 * w.Config.TickRate && !w.Players[Human].Overheated; k++)
        {
            Tick(w, InputButtons.Fire);
            shots += w.LastShots.Count;
        }
        Assert.True(w.Players[Human].Overheated);
        Assert.Equal(15, shots);
        Assert.Equal(AutocannonRules.Clip, w.Players[Human].Clip);
    }

    [Fact]
    public void The_blasts_radius_crosses_the_wire_to_a_decimetre_and_is_drawn_at_the_right_size()
    {
        // The wire rounds the radius to a decimetre (banker's rounding): the bolt blast's 1.25 m arrives as 1.2, the autocannon's 2.5 m exactly.
        float Through(float radius)
        {
            var back = TfwWire.ReadFire(TfwWire.WriteFire([new TfwFireEvent(TfwFireKind.BoltBlast, 7, 1, new Vector3(1f, 2f, 3f), new Vector3(0f, radius, 0f))]));
            return Assert.Single(back!).To.Y;
        }
        float bolt = Through(1.25f), auto = Through(2.5f);
        Assert.InRange(bolt, 1.15f, 1.25f);
        Assert.Equal(2.5f, auto, 2);
        Assert.Equal(1f, AutocannonRules.BlastDrawScale(bolt));   // every bolt blast is drawn as it was
        Assert.Equal(1f, AutocannonRules.BlastDrawScale(1.25f));
        Assert.Equal(2f, AutocannonRules.BlastDrawScale(auto));   // the autocannon's at twice the size
        Assert.Equal(1f, AutocannonRules.BlastDrawScale(0f));     // a record with no radius
        Assert.Equal(1f, AutocannonRules.BlastDrawScale(float.NaN));
        Assert.Equal(1f, AutocannonRules.BlastDrawScale(-3f));
    }

    // ---------------------------------------------------------------- Musou only

    [Fact]
    public void A_config_without_Musou_never_gets_the_autocannon()
    {
        var plain = MusouSimTests.Base with { Extras = ContentExtras.None };
        Assert.DoesNotContain(plain.Weapons, x => ArsenalRules.IsArsenal(x.Name));
        // The content layer is reached only through the Musou dispatcher; a Musou config with no install adds nothing and says so.
        var notes = new List<string>();
        var musou = MusouSimTests.Base with { Extras = ContentExtras.Musou };
        var appended = ContentArsenal.Append(musou, "", notes);   // W4 appends the Remote Detonators from the data's stand-in values: no weapon, only that thrown item
        Assert.Equal(musou.Weapons, appended.Weapons);
        Assert.Equal([ArsenalRules.RemoteDetonators], appended.Thrown.Select(t => t.Name).Where(ArsenalRules.IsArsenal));
        Assert.Contains(notes, n => n.Contains("autocannon"));
    }
}
