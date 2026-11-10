using System.Numerics;
using ArsenalContent = SmContent.Extras.Musou.Arsenal.Arsenal;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Musou Mode's balance pass (musou-balance, owner 2026-11-11; scope "Musou, players only"): "reduce melee attack cleave range by 40%" (<see cref="TfwCombatRules.PlayerCleaveReachScale"/>, x0.6 of the
/// war's whole reach, every weapon's own shape included) and "increase all ranged attack damage by 20%" (<see cref="TfwCombatRules.PlayerRangedDamageScale"/>, x1.2 on every player gun, projectile,
/// splash, grenade and charge hit on the enemies). The 5-wave run, the Orks, the Librarian's spells and Classic PvP keep theirs. The chain lightning's own pass is <see cref="MusouLibrarianReliabilityTests"/>.
/// </summary>
public class MusouBalanceTests
{
    const int Human = 1;
    const string Hammer = "sm_mp_melee_2hand_thunder_hammer";
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly WeaponSpec Rifle = new("sm_mp_ranged_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly ProjectileSpec Rocket = new("ebps\\test\\rocket", 40f, 5f, "raycast", 0f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, Plain with { Damage = 30f },
        new SplashSpec(1.5f, Plain with { Damage = 20f }, [new(0f, 1f), new(1.5f, 1f)], false, false, "target_all"));
    static readonly WeaponSpec Launcher = Rifle with { Name = "sm_mp_ranged_grenade_launcher", Delivery = DeliveryKind.Projectile, Projectile = Rocket, ShotCooldown = 1f };
    static readonly RigidSpec Rigid = new(65f, 0.3f, 0.4f, 0.05f, 0.05f, [new(0f, 1f), new(90f, 0.2f)], [new(0f, 1f), new(90f, 0.2f)], 0.1f, 0.1f);
    static readonly ProjectileSpec FragBall = new("ebps\\test\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null,
        new SplashSpec(10f, new DamageSpec(200f, 0f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(2.5f, 1f), new(5f, 0.5f), new(7f, 0.3f), new(10f, 0.05f)], false, false, "target_all")) { Rigid = Rigid, StopAtMaxBounce = true };
    static readonly ThrownSpec Frag = new("sm_mp_eq_frag_grenade", 2, 1, 1f, 10f, FragBall);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MeleeStep Tracked = Light with { TrackToHit = true };
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    /// <summary>A Space Marine with the rifle (or the launcher), frags, and <paramref name="step"/> as his melee: the class's own steps (<paramref name="wargear"/> null) or one combo of that name.</summary>
    static SimConfig Config(MeleeStep step, string? wargear, int primary = 0, ContentExtras extras = ContentExtras.Musou)
    {
        var c = new ClassLoadout("sm - tactical", primary, -1, [step])
        {
            PrimaryChoices = [primary], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "tactical",
            MeleeChoices = wargear is null ? [] : [new MeleeCombo(wargear, [step])],
        };
        return MusouSimTests.Base with
        {
            Weapons = [Rifle, Launcher], Thrown = [Frag], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], Extras = extras, MusouRules = Still, RespawnTime = 60f,
        };
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    /// <summary>Musou Mode's war on the synthetic arena, the human at (0, -60) facing +Y, the crowd held still. <paramref name="before"/>: the war's rules as they were before this pass (x1 and x1).</summary>
    static World War(SimConfig config, bool before = false)
    {
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        w.Musou!.Frozen = true;
        if (before) w.Musou.War = w.Musou.War! with { PlayerCleaveReachScale = 1f, PlayerRangedDamageScale = 1f };
        return w;
    }

    /// <summary>The 5-wave run on flat ground, its director held.</summary>
    static World Skirmish(SimConfig config)
    {
        var w = new World(config) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Spawn(Human, w.AssignClass(0));
        Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.Musou!.Frozen = true;
        return w;
    }

    static Vector3 Ahead(World w, float metres, float degrees = 0f)
    {
        var p = w.Players[Human];
        float a = p.Yaw + degrees * MathF.PI / 180f;
        return p.Position + new Vector3(-MathF.Sin(a) * metres, MathF.Cos(a) * metres, 0f);
    }

    static ushort Boy(World w, Vector3 at, float health = 1000f) { ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, at, 0f, health); Assert.NotEqual(0, id); return id; }

    static float Lost(World w, ushort id, float health = 1000f) => health - (w.Musou!.TryGet(id, out var e) ? (e.Alive ? e.Health : 0f) : 0f);

    static HashSet<int> Swing(World w)
    {
        var struck = new HashSet<int>();
        for (int t = 0; t < 14; t++)
        {
            Tick(w, t < 3 ? InputButtons.Melee : InputButtons.None);
            foreach (var e in w.Musou!.Events) if (e.Kind == MusouEventKind.Hit && e.Value == 3) struck.Add(e.Enemy);
        }
        return struck;
    }

    // ---------------------------------------------------------------- the cleave: x0.6

    public static TheoryData<string?, bool> Weapons => new()
    {
        { null, false }, { null, true }, { "sm_mp_melee_powersword", false }, { "sm_mp_melee_chainsword", false }, { "sm_mp_melee_combat_knife", true }, { Hammer, false }, { Hammer, true },
        { ArsenalRules.LightningClaw, false }, { ArsenalRules.PowerSpear, false }, { ArsenalRules.PowerSpear, true },
    };

    /// <summary>The drawn line's shape (the client's arc and predicted pick): in the war 0.6 of the old reach for every weapon, the spear's 15 m sweep too; angles and count as before; the 5-wave run as before.</summary>
    [Theory]
    [MemberData(nameof(Weapons))]
    public void The_war_cleave_reaches_six_tenths_of_its_old_reach_for_every_player_melee_weapon(string? wargear, bool tracked)
    {
        var step = tracked ? Tracked : Light;
        var config = Config(step, wargear);
        var rules = MusouCombatRules.Default;
        var war = TfwCombatRules.Default;
        bool spear = wargear == ArsenalRules.PowerSpear;
        float oldWar = spear ? SpearRules.ReachMetres : rules.CleaveRange(config.MeleeRange, tracked, war: true) * war.CleaveReachScale;
        float oldRun = spear ? SpearRules.ReachMetres : rules.CleaveRange(config.MeleeRange, tracked, war: false);
        var inWar = MusouCleaveShape.Of(config, step, war: true, wargear);
        Assert.Equal(oldWar * 0.6f, inWar.Reach, 3);
        Assert.Equal(spear ? SpearRules.HalfAngleDegrees : war.CleaveHalfAngleDegrees, inWar.HalfAngleDegrees, 3);
        Assert.Equal(spear ? SpearRules.MostOrks : war.CleaveTargets, inWar.Most);
        var inRun = MusouCleaveShape.Of(config, step, war: false, wargear);
        Assert.Equal(oldRun, inRun.Reach, 3);
        Assert.Equal(0.6f, war.PlayerCleaveReachScale);
        if (spear) Assert.Equal(9f, inWar.Reach, 3);
        if (!spear && !tracked) Assert.Equal(4.5f * 1.5f * 1.3f * 0.6f, inWar.Reach, 3);   // the synthetic config's 4 m core range + 0.5: 8.78 m before, 5.27 m now
    }

    public static TheoryData<string?> Swung => new() { null, "sm_mp_melee_powersword", ArsenalRules.LightningClaw, ArsenalRules.PowerSpear };

    /// <summary>
    /// The server's own cleave in a real war: a Boy just inside 0.6 of the old reach is struck, one just past it is not, nor one just inside the old reach (struck before this pass, as the same
    /// world with the scale at 1 shows).
    /// </summary>
    [Theory]
    [MemberData(nameof(Swung))]
    public void The_servers_cleave_strikes_within_six_tenths_of_the_old_reach(string? wargear)
    {
        var config = Config(Light, wargear);
        float oldReach = wargear == ArsenalRules.PowerSpear ? SpearRules.ReachMetres : MusouCombatRules.Default.CleaveRange(config.MeleeRange, false, war: true) * TfwCombatRules.Default.CleaveReachScale;
        float reach = oldReach * 0.6f;
        foreach (bool before in new[] { false, true })
        {
            var w = War(config, before);
            ushort inside = Boy(w, Ahead(w, reach - 0.4f, 10f)), past = Boy(w, Ahead(w, reach + 0.4f, -10f)), oldInside = Boy(w, Ahead(w, oldReach - 0.4f, 5f));
            var struck = Swing(w);
            Assert.Contains((int)inside, struck);
            Assert.Equal(before, struck.Contains(past));
            Assert.Equal(before, struck.Contains(oldInside));
        }
    }

    [Fact]
    public void The_five_wave_run_keeps_its_reach()
    {
        var config = Config(Light, null);
        float reach = MusouCombatRules.Default.CleaveRange(config.MeleeRange, false, war: false);
        var w = Skirmish(config);
        ushort inside = Boy(w, Ahead(w, reach - 0.3f));
        Assert.Contains((int)inside, Swing(w));
    }

    // ---------------------------------------------------------------- ranged damage: x1.2

    /// <summary>Each Boy's loss in a war after <paramref name="act"/>, with this pass's rules and with the scales at 1 (as before it).</summary>
    static (float[] Now, float[] Before) Twin(SimConfig config, Func<World, ushort[]> place, Action<World> act)
    {
        var result = new float[2][];
        foreach (bool before in new[] { false, true })
        {
            var w = War(config, before);
            var ids = place(w);
            act(w);
            result[before ? 1 : 0] = ids.Select(id => Lost(w, id)).ToArray();
        }
        return (result[0], result[1]);
    }

    static void TwelveTenths(float[] now, float[] before)
    {
        Assert.Equal(before.Length, now.Length);
        Assert.True(before.Any(b => b > 0f), "something was hit");
        for (int k = 0; k < now.Length; k++) Assert.Equal(before[k] * 1.2f, now[k], 2);
    }

    static void Fire(World w)
    {
        Tick(w, InputButtons.Fire);
        for (int k = 0; k < 10 && w.LastShots.Count == 0; k++) Tick(w, InputButtons.Fire);
        Assert.NotEmpty(w.LastShots);
        Tick(w);
    }

    [Fact]
    public void A_gun_shot_through_the_crowd_deals_twelve_tenths_to_each_and_its_bolt_blasts_too()
    {
        // The bolter: through two, stopping on the third; a bolt blast at each, which also catches the Boys beside them.
        var (now, before) = Twin(Config(Light, null), w => [Boy(w, Ahead(w, 6f)), Boy(w, Ahead(w, 10f)), Boy(w, Ahead(w, 14f)), Boy(w, Ahead(w, 6f) + new Vector3(0.75f, -0.6f, 0f))], Fire);
        TwelveTenths(now, before);
        Assert.Equal(1.2f, TfwCombatRules.Default.PlayerRangedDamageScale);
    }

    [Fact]
    public void A_launchers_round_deals_twelve_tenths_through_and_in_its_burst()
    {
        var config = Config(Light, null, primary: 1);
        var (now, before) = Twin(config, w => [Boy(w, Ahead(w, 5f)), Boy(w, Ahead(w, 8f)), Boy(w, Ahead(w, 11f)), Boy(w, Ahead(w, 11f) + new Vector3(0.8f, 0.3f, 0f))], w =>
        {
            for (int k = 0; k < 60 && w.LastDetonations.Count == 0; k++) Tick(w, k < 2 ? InputButtons.Fire : InputButtons.None);
            Assert.NotEmpty(w.LastDetonations);
            Tick(w);
        });
        TwelveTenths(now, before);
    }

    [Fact]
    public void A_frags_blast_deals_twelve_tenths()
    {
        var lost = new float[2];
        foreach (bool old in new[] { false, true })
        {
            var w = War(Config(Light, null), old);
            Tick(w, InputButtons.Grenade);
            for (int k = 0; k < 60 && w.Projectiles.Count == 0; k++) Tick(w);
            ushort near = 0;   // placed in flight, where it will land
            for (int k = 0; k < 300 && w.Projectiles.Count > 0; k++)
            {
                if (near == 0 && w.Projectiles[0].Position.Z < 1.5f) near = Boy(w, w.Projectiles[0].Position with { Z = 0f } + new Vector3(2f, 0f, 0f), 10000f);   // inside the full-damage ring
                Tick(w);
            }
            Assert.NotEqual(0, near);
            lost[old ? 1 : 0] = Lost(w, near, 10000f);
        }
        Assert.True(lost[1] > 0f);
        Assert.Equal(lost[1] * 1.2f, lost[0], 1);
    }

    [Fact]
    public void The_remote_detonators_blasts_deal_twelve_tenths()
    {
        var c = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        var config = ArsenalContent.Append(MusouSimTests.Base with { Weapons = [Rifle], Thrown = [], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], MusouRules = Still, RespawnTime = 60f }, "", []);
        var lost = new float[2][];
        foreach (bool old in new[] { false, true })
        {
            var w = War(config, old);
            Tick(w, InputButtons.Grenade);
            for (int k = 0; k < 120 && (w.Projectiles.Count == 0 || w.Projectiles[0].Motion != ProjectileMotion.Stuck); k++) Tick(w);
            Assert.Equal(ProjectileMotion.Stuck, w.Projectiles[0].Motion);
            var at = w.Projectiles[0].Position with { Z = 0f };
            var ids = new[] { Boy(w, at + new Vector3(3f, 0f, 0f), 100000f), Boy(w, at + new Vector3(-12f, 4f, 0f), 100000f), Boy(w, at + new Vector3(0f, 25f, 0f), 100000f) };
            for (int k = 0; k < 400; k++) Tick(w);   // the fuse, the centre blast and the ring
            lost[old ? 1 : 0] = ids.Select(id => Lost(w, id, 100000f)).ToArray();
        }
        Assert.True(lost[1].All(l => l > 0f), string.Join(", ", lost[1]));
        for (int k = 0; k < 3; k++) Assert.Equal(lost[1][k] * 1.2f, lost[0][k], 0);
    }

    // ---------------------------------------------------------------- AI Marines keep theirs (the lead, 2026-11-11: "Musou, players only")

    const int Marine = TfwContracts.PuppetIdFirst;

    /// <summary>An AI Marine at (20, -60) facing +Y in the war <paramref name="w"/>, pressing what <paramref name="press"/> says for the tick (the human idles).</summary>
    static void AddMarine(World w, Func<uint, InputButtons> press)
    {
        w.Tfw!.PuppetBrain = (_, p) => new PlayerInput(w.Tick, 0f, 0f, 0f, press(w.Tick));
        w.Spawn(Marine, 0);
        Tick(w);
        w.TfwRespawnAt(Marine, new Vector3(20f, -60f, 0f), 0f);
        Tick(w);
        Assert.True(w.Players[Marine].Alive);
        Assert.False(TfwCombatRules.ScalesPlayer(Marine));
        Assert.True(TfwCombatRules.ScalesPlayer(Human));
    }

    static Vector3 AheadOf(World w, int id, float metres, float degrees = 0f)
    {
        var p = w.Players[id];
        float a = p.Yaw + degrees * MathF.PI / 180f;
        return p.Position + new Vector3(-MathF.Sin(a) * metres, MathF.Cos(a) * metres, 0f);
    }

    [Theory]
    [MemberData(nameof(Swung))]
    public void An_AI_Marines_cleave_keeps_the_reach_before_this_pass(string? wargear)
    {
        var config = Config(Light, wargear);
        float oldReach = wargear == ArsenalRules.PowerSpear ? SpearRules.ReachMetres : MusouCombatRules.Default.CleaveRange(config.MeleeRange, false, war: true) * TfwCombatRules.Default.CleaveReachScale;
        // The drawn arc's shape for him: the old reach; a human's: x0.6.
        Assert.Equal(oldReach, MusouCleaveShape.Of(config, Light, war: true, wargear, human: false).Reach, 3);
        Assert.Equal(oldReach * 0.6f, MusouCleaveShape.Of(config, Light, war: true, wargear).Reach, 3);

        var w = War(config);
        uint swingFrom = 0;
        AddMarine(w, t => swingFrom != 0 && t < swingFrom + 3 ? InputButtons.Melee : InputButtons.None);
        if (wargear is not null) Choose(w, Marine);
        ushort near = Boy(w, AheadOf(w, Marine, oldReach * 0.6f - 0.4f, 10f)), oldInside = Boy(w, AheadOf(w, Marine, oldReach - 0.4f, -5f)), past = Boy(w, AheadOf(w, Marine, oldReach + 0.4f));
        swingFrom = w.Tick + 1;
        var struck = new HashSet<int>();
        for (int t = 0; t < 14; t++)
        {
            Tick(w);   // the human idles
            foreach (var e in w.Musou!.Events) if (e.Kind == MusouEventKind.Hit && e.Value == 3 && e.Player == Marine) struck.Add(e.Enemy);
        }
        Assert.Contains((int)near, struck);
        Assert.Contains((int)oldInside, struck);   // inside his old reach, past a human's x0.6
        Assert.DoesNotContain((int)past, struck);
    }

    /// <summary>The AI Marine's melee choice: the class's first (the one combo of these tests' configs).</summary>
    static void Choose(World w, int id)
    {
        var field = typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var players = (Dictionary<int, PlayerState>)field.GetValue(w)!;
        players[id] = players[id] with { MeleeChoice = 0 };
    }

    [Fact]
    public void An_AI_Marines_shot_keeps_its_damage()
    {
        var lost = new float[2][];
        foreach (bool old in new[] { false, true })
        {
            var w = War(Config(Light, null), old);
            bool fire = false;
            AddMarine(w, _ => fire ? InputButtons.Fire : InputButtons.None);
            var ids = new[] { Boy(w, AheadOf(w, Marine, 6f)), Boy(w, AheadOf(w, Marine, 10f)), Boy(w, AheadOf(w, Marine, 6f) + new Vector3(0.75f, -0.6f, 0f)) };
            fire = true;
            for (int k = 0; k < 12 && w.LastShots.Count == 0; k++) Tick(w);
            Assert.NotEmpty(w.LastShots);
            fire = false;
            Tick(w);
            lost[old ? 1 : 0] = ids.Select(id => Lost(w, id)).ToArray();
        }
        Assert.True(lost[1][0] > 0f && lost[1][2] > 0f, string.Join(", ", lost[1]));   // the shot and its bolt blast
        for (int k = 0; k < 3; k++) Assert.Equal(lost[1][k], lost[0][k], 3);   // the same with this pass's rules: x1 for him
    }

    [Fact]
    public void Melee_the_orks_and_the_five_wave_run_keep_their_damage()
    {
        // A cleave's damage: the same with this pass's rules and without.
        var (now, before) = Twin(Config(Light, null), w => [Boy(w, Ahead(w, 2f))], w => Swing(w));
        Assert.True(before[0] > 0f);
        Assert.Equal(before[0], now[0], 3);

        // An Ork's blow on a player: the same.
        var hurt = new float[2];
        foreach (bool old in new[] { false, true })
        {
            var config = Config(Light, null) with { MusouRules = Still with { PlayerDamageScale = 1f } };
            var w = War(config, old);
            float was = w.Players[Human].Health + w.Players[Human].Shield;
            w.MusouLandAttack(new MusouAttack(1, Human, 40f, Melee: true, Staggers: false, Ahead(w, 1f)));
            hurt[old ? 1 : 0] = was - (w.Players[Human].Health + w.Players[Human].Shield);
        }
        Assert.True(hurt[1] > 0f);
        Assert.Equal(hurt[1], hurt[0], 3);

        // The 5-wave run's gun: no war, no scale.
        var run = Skirmish(Config(Light, null));
        ushort boy = Boy(run, Ahead(run, 6f));
        Fire(run);
        Assert.True(Lost(run, boy) > 0f);
        var war = War(Config(Light, null));
        war.Musou!.War = war.Musou.War! with { BoltBlasts = false };
        ushort warBoy = Boy(war, Ahead(war, 6f));
        Fire(war);
        Assert.Equal(Lost(run, boy) * 1.2f, Lost(war, warBoy), 2);   // the same shot in the war: x1.2
    }
}
