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
/// Space Marines' full auto gun, one round every 0.4 s for 50, overheating after 15 rounds, through 5 Orks with a 2.5 m bolt-style blast at each Ork a round strikes; nothing of it outside a Musou
/// config and the bolter's blasts as they were.
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
    public void The_twin_is_full_auto_one_round_every_four_tenths_for_fifty_with_the_files_heat_and_the_heavy_bolters_handling()
    {
        var twin = ContentArsenal.Twin(ChaosFile, HeavyBolter);
        Assert.Equal(ArsenalRules.Autocannon, twin.Name);
        Assert.Equal(TriggerKind.FullAuto, twin.Trigger);
        Assert.Equal(0.4f, twin.ShotCooldown);
        Assert.Equal(AutocannonRules.SecondsPerRound, twin.ShotCooldown);
        Assert.Equal(50f, twin.Damage);
        Assert.True(twin.Damage > HeavyBolter.Damage && twin.ShotCooldown > HeavyBolter.ShotCooldown);   // slower and harder than the heavy bolter
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
    public void It_fires_a_round_every_four_tenths_of_a_second_for_fifty_while_the_trigger_is_held()
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
        Assert.Contains(drops, d => MathF.Abs(d - 50f) < 0.5f || MathF.Abs(d - 65f) < 0.5f);   // a round is 50 (plus the blast's 15 on the Ork it strikes, in the same tick)
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
        // Beside the second, 3.0 m off: outside both.
        ushort outside = Boy(w, me + new Vector3(3.0f, 14f, 0f));
        Tick(w, InputButtons.Fire);
        for (int k = 0; k < 20 && w.LastShots.Count == 0; k++) Tick(w, InputButtons.Fire);
        Assert.NotEmpty(w.LastShots);
        Assert.Equal(6, w.TfwBoltBlastsOut);   // five pierced and the one that stops it: a blast at each of the six struck
        Assert.Equal(6, line.Count(id => HealthOf(w, id) < 100000f));
        Assert.Equal(100000f, HealthOf(w, line[6]));
        var records = w.Tfw!.Fire.Events.Where(f => f.Kind == TfwFireKind.BoltBlast).ToArray();
        Assert.Equal(6, records.Length);
        Assert.All(records, r => Assert.Equal(AutocannonRules.BlastRadius, r.To.Y));   // the clients get the radius
        float hurt = 100000f - HealthOf(w, beside);
        Assert.InRange(hurt, AutocannonRules.BlastDamage * 0.3f, AutocannonRules.BlastDamage);   // a blast alone: the ray passes 2 m off him
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
    public void The_blast_is_twice_the_bolt_blasts_size_with_its_damage_and_shape()
    {
        var war = TfwCombatRules.Default;
        Assert.Equal(1.25f, war.BoltBlastRadius);
        Assert.Equal(2f, AutocannonRules.BlastRadius / war.BoltBlastRadius);   // twice the size (owner D12)
        Assert.Equal(war.BoltBlastDamage, AutocannonRules.BlastDamage);
        Assert.Equal(war.BoltBlastFallOff[1].Value, AutocannonRules.BlastFallOff[1].Value);
        Assert.Equal(1.0f, AutocannonRules.BlastFallOff[0].Distance);
        Assert.Equal(0.3f, Combat.SplashIntensity(AutocannonRules.BlastFallOff, 2.5f), 3);
        Assert.Equal(0f, Combat.SplashIntensity(AutocannonRules.BlastFallOff, 2.6f));
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
        Assert.Same(musou, ContentArsenal.Append(musou, "", notes));
        Assert.Contains(notes, n => n.Contains("autocannon"));
    }
}
