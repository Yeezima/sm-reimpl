using System.Numerics;
using SmContent.Extras.Autocannon;
using ContentArsenal = SmContent.Extras.Musou.Arsenal.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmNet.Tests.Extras.Autocannon;

/// <summary>
/// Classic PvP's autocannon in a match (<see cref="ContentExtras.Autocannon"/>, the extended profile; the owner, 2026-11-11: "add the autocannon to classic pvp mode with everything the same except: no aoe
/// blast radius / simply higher single shot damage to be the same as the stalker - with the original games headshot bonus value"): on a synthetic fixture, a Devastator's autocannon round hurts the
/// Chaos player it strikes exactly as a Stalker Bolter round does, the players close behind and beside him not at all (no blast, no detonation), its clip spends (Musou's endless clip is Musou's), and
/// the Havoc finds it at the Devastator's position. No original data.
/// </summary>
public class ClassicAutocannonSimTests
{
    // The Chaos file as WeaponTuning.Read gives it (as AutocannonTests): hit-scan, a 2-round burst, 120 rounds, head x1.
    static readonly WeaponSpec ChaosFile = new("csm_mp_exotic_auto_cannon", WeaponSlot.Ranged, TriggerKind.SemiAuto, 50f, 0.4f, 120, 0, 2f, 150f, 45f, 70f, 0.75f, 1f, 1f, 1f, 1f)
    {
        Heat = new HeatSpec(1f, 15f, 9f, 5f, 4.5f, 0.41f, 0.41f), BurstLength = 2, BurstInterval = 0.2f,
    };
    static readonly WeaponSpec HeavyBolter = new("sm_mp_exotic_heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 35f, 0.233f, 400, 0, 3f, 250f, 50f, 75f, 0.8f, 1f, 1f, 0.7f, 0.7f) { AlwaysReady = true };
    // The Stalker's shot as the install has it: 50, x2 on the head (x2.5 on armour), x0.8 on an armoured body; a semi-automatic rifle (its own rate does not matter here).
    static readonly WeaponSpec Stalker = new("sm_mp_ranged_sniper_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 50f, 0.3f, 10, 40, 2.5f, 250f, 60f, 100f, 0.7f, 2f, 1f, 2.5f, 0.8f) { AlwaysReady = true };
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);

    static readonly SimConfig Data = new()
    {
        Weapons = [HeavyBolter, Stalker, HeavyBolter with { Name = ClassicAutocannon.HeavyBolterFileChaos }],
        Classes =
        [
            new ClassLoadout("sm - devastator", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "heavy" },
            new ClassLoadout("sm - tactical", 1, -1, [Light]) { PrimaryChoices = [1], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" },
            new ClassLoadout("csm - havoc", 2, -1, [Light]) { PrimaryChoices = [2], EquipmentChoices = [], Side = Team.Chaos, Role = "heavy" },
        ],
        HitVolumes = [HitVolume.Sphere("Head", HitZone.Head, new(0f, 0f, 1.9f), 0.2f), HitVolume.Capsule("Spine", HitZone.Body, new(0f, 0f, 0.4f), new(0f, 0f, 1.5f), 0.35f)],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f, HealthMax = 150f, ShieldMax = 50f, RespawnTime = 2f,
    };

    static SimConfig Extended() => ClassicAutocannon.WithAutocannon(Data with { Extras = ContentExtras.All }, ChaosFile, []);

    /// <summary>The shooter (id 1, class <paramref name="shooterClass"/> with primary choice <paramref name="primary"/>) at the origin, Havocs at <paramref name="ahead"/> metres (y) and the one at 8 m with another 1 m beside him.</summary>
    static World Line(SimConfig config, int shooterClass, int primary, params float[] ahead)
    {
        var spawns = new List<SpawnPoint> { new(Vector3.Zero, 0f) };
        foreach (float y in ahead) spawns.Add(new SpawnPoint(new Vector3(0f, y, 0f), MathF.PI));
        var w = new World(config, null, spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f } };
        w.Spawn(1, shooterClass, primaryChoice: primary);
        for (int k = 0; k < ahead.Length; k++) w.Spawn(2 + k, 2);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.None));
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    /// <summary>Fires player 1 down the line until the nearest Havoc is hit; what every Havoc has left (health + armour), the detonations seen and the shooter's clip after the hit.</summary>
    static (Dictionary<int, float> Left, int Detonations, int Clip, string Weapon) FireOnce(World w)
    {
        var me = w.Players[1].Position;
        float Left(int id) => w.Players[id].Health + w.Players[id].Shield;
        var behind = w.Players.Values.Where(p => p.PlayerId != 1 && p.Position.Y > me.Y).ToArray();
        var before = w.Players.Values.Where(p => p.PlayerId != 1 && p.Position.Y < me.Y).ToArray();
        float yaw = behind.Length >= before.Length ? 0f : MathF.PI;
        var ahead = (behind.Length >= before.Length ? behind : before).OrderBy(p => MathF.Abs(p.Position.Y - me.Y)).Select(p => p.PlayerId).ToArray();
        float full = Left(ahead[0]);
        int detonations = 0;
        for (int k = 0; k < 90 && Left(ahead[0]) == full; k++)
        {
            uint view = Math.Max(1u, w.Tick), body = w.Tick > 1 ? w.Tick - 1 : 0;
            w.Step(id => new PlayerInput(w.Tick, 0f, 0f, id == 1 ? yaw : w.Players[id].Yaw, id == 1 ? InputButtons.Fire : InputButtons.None, ViewTick: view, BodyTick: body));
            detonations += w.LastDetonations.Count;
        }
        Assert.True(Left(ahead[0]) < full, "the nearest Havoc was never hit");
        return (w.Players.Keys.Where(id => id != 1).ToDictionary(id => id, Left), detonations, w.Players[1].Clip, Loadouts.InHand(w.Config, w.Players[1])!.Name);
    }

    [Fact]
    public void A_round_hurts_the_player_it_strikes_as_a_Stalker_round_and_nobody_else()
    {
        var config = Extended();
        // The Devastator's second primary is the autocannon; the Tactical's only one the Stalker. The Havocs stand at 8, 9 and 10 m: the two behind the first are inside Musou's 2.5 m blast.
        var auto = FireOnce(Line(config, 0, 1, 8f, 9f, 10f));
        var stalker = FireOnce(Line(config, 1, 0, 8f, 9f, 10f));
        Assert.Equal(ArsenalRules.Autocannon, auto.Weapon);
        Assert.Equal(Stalker.Name, stalker.Weapon);
        Assert.Equal(stalker.Left.OrderBy(p => p.Key).Select(p => p.Value), auto.Left.OrderBy(p => p.Key).Select(p => p.Value));   // the same hurt, the same untouched
        float full = config.HealthMax + config.ShieldMax;
        Assert.Equal(2, auto.Left.Values.Count(v => v == full));   // no splash: the two behind him untouched
        Assert.Equal(0, auto.Detonations);
    }

    [Fact]
    public void Its_clip_spends_in_Classic_PvP()
    {
        var w = Line(Extended(), 0, 1, 8f);
        Assert.Equal(400, w.Players[1].Clip);
        var shot = FireOnce(w);
        Assert.Equal(ArsenalRules.Autocannon, shot.Weapon);
        Assert.Equal(399, shot.Clip);   // one round gone; only Musou Mode keeps the autocannon's clip full
    }

    [Fact]
    public void The_Havoc_carries_it_at_the_Devastators_position_and_a_class_carried_over_keeps_it()
    {
        var config = Extended();
        Assert.Equal([0, 3], config.Classes[0].PrimaryChoices);   // the Devastator: heavy bolter, then the autocannon
        Assert.Equal([2, 3], config.Classes[2].PrimaryChoices);   // the Havoc: his heavy bolter, then the same autocannon
        Assert.Equal([1], config.Classes[1].PrimaryChoices);      // the Tactical as he was
        var w = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f } };
        w.Spawn(1, 2, primaryChoice: 1);
        Assert.Equal(ArsenalRules.Autocannon, Loadouts.InHand(w.Config, w.Players[1])!.Name);
    }

    [Fact]
    public void Without_the_flag_the_world_has_no_autocannon_and_a_choice_beyond_the_list_is_the_default()
    {
        // The faithful profile's config never runs the append (the tuning's dispatcher, under ContentExtras.Autocannon only): its Devastator's out-of-range choice is his default heavy bolter.
        var w = new World(Data, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f } };
        w.Spawn(1, 0, primaryChoice: 1);
        Assert.Equal(HeavyBolter.Name, Loadouts.InHand(w.Config, w.Players[1])!.Name);
        Assert.DoesNotContain(Data.Weapons, x => ArsenalRules.IsAutocannon(x.Name));
    }

    [Fact]
    public void The_Classic_round_is_the_Stalkers_and_Musous_is_the_blasts_plus_thirty()
    {
        var classic = Extended().Weapons[^1];
        var musou = ContentArsenal.MusouTwin(ChaosFile, HeavyBolter);
        Assert.Equal((Stalker.Damage, Stalker.HeadMultiplier, Stalker.BodyMultiplier, Stalker.ShieldHeadMultiplier, Stalker.ShieldBodyMultiplier),
            (classic.Damage, classic.HeadMultiplier, classic.BodyMultiplier, classic.ShieldHeadMultiplier, classic.ShieldBodyMultiplier));
        Assert.Equal(AutocannonRules.BlastDamage + 30f, musou.Damage);
        // Everything else is the same gun in both modes: the name, trigger, rate, clip, range, fall-off, heat and handling.
        static object Gun(WeaponSpec g) => (g.Name, g.Trigger, g.ShotCooldown, g.ClipSize, g.ReserveSize, g.MaxRange, g.FallOffStart, g.FallOffEnd, g.FallOffEndMultiplier, g.Heat, g.AlwaysReady, g.RaiseDelay, g.Bursts, g.Brace);
        Assert.Equal(Gun(musou), Gun(classic));
        Assert.Equal((AutocannonRules.Clip, 0), (classic.ClipSize, classic.ReserveSize));
    }
}
