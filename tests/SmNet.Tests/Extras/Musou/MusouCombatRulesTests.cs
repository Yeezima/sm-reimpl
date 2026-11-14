using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// musou/combat-rules (owner, 2026-11-09, Musou Mode only): every melee strike gives back 5 % armour; the air slash's landing 25 % health and 10 % armour; combat stims last 3x, hit 2x, swing and
/// fire 20 % faster, spend no ammunition and give 25 % health + 20 % armour per strike (instead of 5 % and 5 %); the frag's blast reaches 2x; the slash strokes live a second longer and fade out eased.
/// Classic PvP (no Musou flag) keeps every old value.
/// </summary>
public class MusouCombatRulesTests
{
    static readonly StimSpec StimData = new(5f, 0.3f, 0.9f) { MeleeDamage = 1.35f };
    static readonly ThrownSpec Stim = new("sm_mp_eq_stimpack", 1, -1, 6f, 0f, ThrownSpec.NoProjectile) { Stim = StimData };
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly ThrownSpec FragItem = new("sm_mp_eq_frag_grenade", 2, 1, 1f, 30f, new ProjectileSpec("ebps\\test\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f)], false, true, false, 1, 1f, 1.5f, 0, null,
        new SplashSpec(10f, Plain with { Damage = 200f }, [new(0f, 1f), new(5f, 0.5f), new(10f, 0.05f)], true, false, "target_all")));

    static MusouRules Still => MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    static SimConfig WithStim(SimConfig c) => c with { Thrown = [Stim, FragItem], Classes = [.. c.Classes.Select(k => k with { EquipmentChoices = [0] })] };

    static void Set(World w, Func<PlayerState, PlayerState> change)
    {
        var players = (Dictionary<int, PlayerState>)typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        players[1] = change(players[1]);
    }

    static World NewWorld(SimConfig? config = null)
    {
        var w = new World(config ?? MusouSimTests.Base with { MusouRules = Still }) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));
        return w;
    }

    static void Swing(World w, int ticks = 20)
    {
        for (int t = 0; t < ticks; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.Melee : 0));
    }

    [Fact]
    public void Every_melee_strike_gives_back_five_percent_of_the_armour_for_each_enemy_and_never_over_the_most()
    {
        var w = NewWorld();
        var body = Loadouts.Body(w.Config, w.Players[1]);
        Assert.True(body.ShieldMax > 0f);
        Set(w, p => p with { Shield = 0f, Health = body.HealthMax });
        for (int k = 0; k < 3; k++) w.Musou!.Spawn(MusouEnemyType.Boy, w.Players[1].Position + new Vector3(k * 0.4f - 0.4f, 1.5f, 0f), 0f, 85f);
        Swing(w);
        Assert.Equal(body.ShieldMax * 3 * MusouCombatRules.MeleeArmourShare, w.Players[1].Shield, 2);   // three struck: 3 x 5 %, multi-target hits stack
        Assert.Equal(0.05f, MusouCombatRules.MeleeArmourShare);

        var alone = NewWorld();
        Set(alone, p => p with { Shield = 0f });
        Swing(alone);
        Assert.Equal(0f, alone.Players[1].Shield);   // a swing at nobody gives nothing

        var nearlyFull = NewWorld();
        Set(nearlyFull, p => p with { Shield = body.ShieldMax * 0.98f });
        nearlyFull.Musou!.Spawn(MusouEnemyType.Boy, nearlyFull.Players[1].Position + new Vector3(0f, 1.5f, 0f), 0f, 85f);
        Swing(nearlyFull);
        Assert.Equal(body.ShieldMax, nearlyFull.Players[1].Shield, 2);
    }

    [Fact]
    public void The_air_slash_landing_gives_back_25_percent_health_and_10_percent_armour()
    {
        var w = NewWorld();
        var body = Loadouts.Body(w.Config, w.Players[1]);
        Set(w, p => p with { Health = body.HealthMax * 0.2f, Shield = 0f });
        typeof(World).GetMethod("MusouSlashLandHeal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, [1]);
        Assert.Equal(body.HealthMax * 0.45f, w.Players[1].Health, 2);
        Assert.Equal(body.ShieldMax * 0.10f, w.Players[1].Shield, 2);
        Assert.Equal(0.25f, MusouCombatRules.SlashLandHealthShare);
        Assert.Equal(0.10f, MusouCombatRules.SlashLandArmourShare);
    }

    [Fact]
    public void With_a_stim_working_a_strike_gives_25_percent_health_and_20_percent_armour_instead_of_the_basic_shares()
    {
        var w = NewWorld(WithStim(MusouSimTests.Base with { MusouRules = Still }));
        var body = Loadouts.Body(w.Config, w.Players[1]);
        Set(w, p => p with { Health = body.HealthMax * 0.3f, Shield = 0f, StimLeft = 10f, Equipment = 0 });
        Assert.NotNull(Stims.Active(w.Config, w.Players[1]));
        w.Musou!.Spawn(MusouEnemyType.Boy, w.Players[1].Position + new Vector3(0f, 1.5f, 0f), 0f, 85f);
        Swing(w, 6);
        Assert.Equal(body.HealthMax * (0.3f + 0.25f), w.Players[1].Health, 2);
        Assert.Equal(body.ShieldMax * 0.20f, w.Players[1].Shield, 2);
    }

    [Fact]
    public void Stims_in_Musou_last_three_times_hit_twice_swing_and_fire_faster_and_spend_no_ammunition()
    {
        var cfg = WithStim(MusouSimTests.Base);
        var boost = cfg.StimBoost;
        Assert.Equal(3f, boost.Lifetime);
        Assert.Equal(2f, boost.Melee);
        Assert.Equal(1.2f, boost.Speed);
        Assert.True(boost.FreeAmmo);
        Assert.Equal(15f, Stims.LifetimeOf(cfg, StimData));
        var state = new PlayerState { Alive = true, StimLeft = 3f, Class = 0, Equipment = 0 };
        Assert.Equal(2f, Stims.MeleeDealt(cfg, state));   // 2x in all, replacing the data's x1.35
        Assert.Equal(1.2f, Stims.AttackSpeed(cfg, state));
        Assert.True(Stims.FreeAmmo(cfg, state));
        Assert.Equal(1f, Stims.AttackSpeed(cfg, state with { StimLeft = 0f }));
        Assert.False(Stims.FreeAmmo(cfg, state with { StimLeft = 0f }));
    }

    [Fact]
    public void A_stimmed_musou_player_fires_without_draining_the_magazine_and_the_stim_lasts_fifteen_seconds()
    {
        var w = new World(WithStim(MusouSimTests.Base), null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = MusouSimTests.Endless };
        w.Spawn(1, 0, equipmentChoice: 0);
        for (int t = 0; t < 12; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.Grenade : 0));
        Assert.InRange(w.Players[1].StimLeft, 14.0f, 15f);
        int clip = w.Players[1].Clip;
        int shots = 0;
        for (int t = 0; t < 40; t++) { w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire)); shots += w.LastShots.Count; }
        Assert.True(shots > 3);
        Assert.Equal(clip, w.Players[1].Clip);   // fired, nothing spent
        for (int t = 0; t < 30 * 14; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));
        Assert.Equal(0f, w.Players[1].StimLeft);
    }

    [Fact]
    public void Classic_PvP_keeps_the_stims_and_the_frag_it_always_had()
    {
        var classic = WithStim(MusouSimTests.Base with { Extras = ContentExtras.None });
        Assert.Equal(StimBoost.None, classic.StimBoost);
        Assert.Equal(5f, Stims.LifetimeOf(classic, StimData));
        var state = new PlayerState { Alive = true, StimLeft = 3f, Class = 0, Equipment = 0 };
        Assert.Equal(1.35f, Stims.MeleeDealt(classic, state));
        Assert.Equal(1f, Stims.AttackSpeed(classic, state));
        Assert.False(Stims.FreeAmmo(classic, state));
        Assert.Equal(1f, classic.ThrownBlastScale(FragItem));
        var w = new World(classic, null, [new SpawnPoint(Vector3.Zero, 0f)]) { Rules = MusouSimTests.Endless };
        w.Spawn(1, 0, equipmentChoice: 0);
        for (int t = 0; t < 12; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.Grenade : 0));
        Assert.InRange(w.Players[1].StimLeft, 4.0f, 5f);
        int clip = w.Players[1].Clip;
        for (int t = 0; t < 40; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));   // past the use's 0.9 s
        Assert.True(w.Players[1].Clip < clip);
    }

    [Fact]
    public void The_frag_blast_reaches_twice_as_far_in_Musou_and_nothing_else_changes()
    {
        var musou = WithStim(MusouSimTests.Base);
        Assert.Equal(2.5f, musou.ThrownBlastScale(FragItem));   // x2, then the AoE pass's x1.25 (owner 2026-11-14)
        Assert.Equal(1.25f, musou.ThrownBlastScale(Stim));   // any other item: the AoE pass's x1.25 (a stim has no blast)
        var scaled = Combat.ScaledReach(FragItem.Projectile.Splash!, 2f);
        Assert.Equal(20f, scaled.Radius);
        Assert.Equal(200f, scaled.Damage.Damage);
        Assert.Equal([new DistancePoint(0f, 1f), new DistancePoint(10f, 0.5f), new DistancePoint(20f, 0.05f)], scaled.FallOff);
        Assert.Equal(1f, (MusouSimTests.Base with { Extras = ContentExtras.None }).ThrownBlastScale(FragItem));
        Assert.Equal(1f, (MusouSimTests.Base with { Extras = ContentExtras.Orks | ContentExtras.Gore }).ThrownBlastScale(FragItem));
    }

    [Fact]
    public void The_slash_strokes_live_a_second_longer_and_open_and_close_like_a_tear_without_popping()
    {
        foreach (float old in new[] { MusouSlashRules.Seconds, 0.18f })
        {
            float life = MusouSlashRules.Lifetime(old);
            Assert.Equal(old + 1f, life, 4);
            Assert.Equal(0f, MusouSlashRules.TearWidth(0f, old), 4);          // starts as a line
            Assert.Equal(0f, MusouSlashRules.Fade(0f, old), 4);           // and unseen
            Assert.Equal(1f, MusouSlashRules.TearWidth(MusouSlashRules.OpenSeconds, old), 4);   // fully open after the opening
            Assert.Equal(1f, MusouSlashRules.Fade(MusouSlashRules.OpenSeconds, old), 4);
            Assert.Equal(1f, MusouSlashRules.TearWidth(life - MusouSlashRules.CloseSeconds, old), 4);   // holds until the closing begins
            Assert.Equal(0f, MusouSlashRules.TearWidth(life, old), 4);        // shut and gone exactly at the end
            Assert.Equal(0f, MusouSlashRules.Fade(life, old), 4);
            Assert.True(MusouSlashRules.Fade(life - MusouSlashRules.CloseSeconds * 0.5f, old) > 0.9f);   // it narrows before it fades
            float lastW = 0f, lastF = 0f, step = 0f;
            for (float age = 0f; age <= life; age += 0.01f)
            {
                float w = MusouSlashRules.TearWidth(age, old), f = MusouSlashRules.Fade(age, old);
                step = MathF.Max(step, MathF.Max(MathF.Abs(w - lastW), MathF.Abs(f - lastF)));
                lastW = w; lastF = f;
            }
            Assert.True(step < 0.2f, $"nothing jumps (largest 10 ms step {step})");
            Assert.Equal(1.15f, MusouSlashRules.Reveal(life * 0.5f, old), 4);   // the head has swept the whole cut by the hold
        }
    }

    [Fact]
    public void The_five_wave_skirmish_keeps_the_old_cleave_volume()
    {
        var w = NewWorld();   // not the war: no TfwCombatRules volume
        var me = w.Players[1].Position;
        ushort far = w.Musou!.Spawn(MusouEnemyType.Boy, me + new Vector3(0f, 8f, 0f), 0f, 1000f);   // old reach 4.5 m: out
        ushort wide = w.Musou.Spawn(MusouEnemyType.Boy, me + new Vector3(-3f * MathF.Sin(1.83f), 3f * MathF.Cos(1.83f), 0f), 0f, 1000f);   // 105 degrees off: out of the old 90
        var hit = new HashSet<int>();
        for (int t = 0; t < 12; t++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.Melee : 0));
            foreach (var e in w.Musou.Events) if (e.Kind == MusouEventKind.Hit && e.Value == 3) hit.Add(e.Enemy);
        }
        Assert.DoesNotContain((int)far, hit);
        Assert.DoesNotContain((int)wide, hit);
    }

    // ---- musou/aoe-pass (owner 2026-11-14)

    static readonly SplashSpec Pound = new(6f, Plain with { Damage = 100f, ShieldDamage = 80f }, [new(0f, 1f), new(3f, 0.6f), new(6f, 0.1f)], true, false, "target_all");

    static SplashSpec? JumpBlast(World w, JumpEventKind kind, SplashSpec? blast)
    {
        var args = new object?[] { kind, blast };
        typeof(World).GetMethod("ExtrasJumpBlast", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, args);
        return (SplashSpec?)args[1];
    }

    [Fact]
    public void The_ground_pound_and_the_air_slash_landing_reach_one_and_a_half_times_as_far_and_deal_one_and_a_half_times_at_the_centre()
    {
        var w = NewWorld();
        foreach (var kind in new[] { JumpEventKind.Impact, JumpEventKind.SlashLand })
        {
            var big = JumpBlast(w, kind, Pound)!;
            Assert.Equal(9f, big.Radius);
            Assert.Equal([new DistancePoint(0f, 1f), new DistancePoint(4.5f, 0.6f), new DistancePoint(9f, 0.1f)], big.FallOff);   // the same shape as a fraction of the new radius
            Assert.Equal(150f, big.Damage.Damage);   // the centre
            Assert.Equal(120f, big.Damage.ShieldDamage);
            Assert.Equal(Combat.SplashIntensity(Pound.FallOff, 4.5f), Combat.SplashIntensity(big.FallOff, 6.75f), 4);   // half way out: the same share
            Assert.Equal(Combat.SplashIntensity(Pound.FallOff, 6f), Combat.SplashIntensity(big.FallOff, 9f), 4);          // the rim keeps its share
        }
        Assert.Equal(Pound, JumpBlast(w, JumpEventKind.Land, Pound));   // an ordinary landing and a launch are as they were
        Assert.Equal(Pound, JumpBlast(w, JumpEventKind.Launch, Pound));
        Assert.Null(JumpBlast(w, JumpEventKind.Impact, null));
    }

    [Fact]
    public void Classic_PvP_keeps_its_pound_and_landing_blasts_and_every_weapon_blast_size()
    {
        var classic = new World(MusouSimTests.Base with { Extras = ContentExtras.None }, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        foreach (var kind in new[] { JumpEventKind.Impact, JumpEventKind.SlashLand, JumpEventKind.Land }) Assert.Same(Pound, JumpBlast(classic, kind, Pound));
        var c = WithStim(MusouSimTests.Base with { Extras = ContentExtras.None });
        Assert.Equal(1f, c.ThrownBlastScale(null));
        Assert.Equal(1f, c.ThrownBlastScale(FragItem));
        Assert.Equal(1f, c.ThrownBlastScale(Stim));
        // Orks, gore and the Ork faction alone are not Musou Mode.
        Assert.Equal(1f, (MusouSimTests.Base with { Extras = ContentExtras.Orks | ContentExtras.Gore }).ThrownBlastScale(null));
    }

    [Fact]
    public void Every_player_weapon_blast_reaches_a_quarter_further_in_Musou_and_the_autocannon_burst_two_fifths_further()
    {
        var musou = WithStim(MusouSimTests.Base);
        Assert.Equal(1.25f, musou.ThrownBlastScale(null));   // a launched projectile's blast (launchers, plasma, melta)
        Assert.Equal(2.5f, musou.ThrownBlastScale(FragItem));
        Assert.Equal(1.25f, MusouCombatRules.WeaponAoeScale);
        Assert.Equal(1.4f, MusouCombatRules.AutocannonAoeScale);   // the lead's reading: 1.75 is the one-line alternative
        var bolt = Combat.ScaledReach(SmSim.Extras.Musou.Campaign.TfwCombatRules.Default.BoltBlast(), MusouCombatRules.WeaponAoeScale);
        Assert.Equal(1.5625f, bolt.Radius, 4);
        Assert.Equal(15f, bolt.Damage.Damage);   // the damage is as it was
        var auto = Combat.ScaledReach(AutocannonRules.Blast, MusouCombatRules.AutocannonAoeScale);
        Assert.Equal(3.5f, auto.Radius, 4);
        Assert.Equal(AutocannonRules.BlastDamage, auto.Damage.Damage);
        Assert.Equal(1f, Combat.SplashIntensity(auto.FallOff, 3.4f));   // still full to its edge
        Assert.Equal(0f, Combat.SplashIntensity(auto.FallOff, 3.6f));
        Assert.True(AutocannonRules.LosesDust(auto.Radius));    // the client still draws the autocannon's blast without dust
        Assert.False(AutocannonRules.LosesDust(bolt.Radius));   // and the bolt blast with it
    }

    [Fact]
    public void The_clients_pound_ring_follows_the_blast_in_Musou_and_stays_the_datas_in_Classic()
    {
        Assert.Equal(1.5f, MusouCombatRules.PoundReach(ContentExtras.Musou));
        Assert.Equal(9f, Pound.Radius * MusouCombatRules.PoundReach(ContentExtras.Musou));   // the ring marks what the server's blast reaches
        Assert.Equal(JumpBlast(NewWorld(), JumpEventKind.Impact, Pound)!.Radius, Pound.Radius * MusouCombatRules.PoundReach(ContentExtras.Musou));
        Assert.Equal(1f, MusouCombatRules.PoundReach(ContentExtras.None));
        Assert.Equal(1f, MusouCombatRules.PoundReach(ContentExtras.Orks | ContentExtras.Gore));
        string root = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        foreach (var (file, call) in new[] { ("NetworkView.PoundImpact.cs", "PoundRing.Request(pound.Radius * PoundRingScale)"), ("NetworkView.PoundImpact.cs", "(pack.Pound?.Radius ?? 0f) * PoundRingScale"), ("NetworkView.Jump.cs", "pound.Radius * PoundRingScale") })
            Assert.Contains(call, File.ReadAllText(Path.Combine(root, "client", file)));
    }
}
