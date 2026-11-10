using System.Numerics;
using System.Reflection;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Arsenal;

/// <summary>
/// W3, the power spear (docs/new-weapons-design.md section 6): the hammer's combo slowed to 0.85 and lightened, sweeps that reach 15 m over 300 degrees and strike at most 12 Orks, the hammer's jump-pack
/// rules, and the impale on the pound's landing (600 to the nearest enemy within 2 m). Musou only: a player without the spear, and every non-Musou match, keep the hammer's cleave and landing.
/// </summary>
public class SpearTests
{
    const string Hammer = "sm_mp_melee_2hand_thunder_hammer";
    static readonly MeleeStep HammerLight = new(2.33f, 0.57f, 0.9f, 1.4f, 0.8f, 0.9f, 200f, 10f, 3.5f) { StringResetAt = 1.9f, EvadeAt = 1.1f };
    static readonly MeleeStep HammerHeavy = new(2.0f, 0.6f, 0f, 0f, 0.8f, 0.9f, 300f, 10f, 3.5f) { YNext = 4, YComboOpen = 0.3f, YComboClose = 0.9f, PrimaryReaction = new(0f, ReactionKind.Stun) };
    static readonly SplashSpec Blast = new(7f, new DamageSpec(100f, 125f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(7f, 1f)], false, false, "target_all");
    static readonly MeleeStep HammerSlam = new(1.1f, 0.6f, 0f, 0f, 0.8f, 0.9f, 250f, 10f, 3.5f) { Stomp = Blast, SteerFrom = 0.1f, SteerTo = 0.6f, SteerRate = 300f };
    static readonly MeleeCombo HammerCombo = new(Hammer, [HammerLight]) { Heavy = [HammerHeavy, HammerSlam] };
    static readonly MeleeCombo SpearCombo = SpearRules.FromHammer(HammerCombo, ArsenalRules.PowerSpear);

    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    const int Human = 1;

    static SimConfig Config(ContentExtras extras = ContentExtras.Musou)
    {
        var c = new ClassLoadout("sm - assault", 0, -1, [HammerLight]) { PrimaryChoices = [0], MeleeChoices = [HammerCombo, SpearCombo], Side = Team.SpaceMarines, Role = "assault" };
        return MusouSimTests.Base with { Weapons = [Rifle], Classes = [c, c with { Name = "csm - assault", Side = Team.Chaos }], Extras = extras, MusouRules = Still, RespawnTime = 60f };
    }

    static void Choose(World w, int melee)
    {
        var field = typeof(World).GetField("_players", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var players = (Dictionary<int, PlayerState>)field.GetValue(w)!;
        players[Human] = players[Human] with { MeleeChoice = melee };
    }

    static World War(int melee, SimConfig? config = null)
    {
        var map = TfwTestArena.Build();
        var w = new World(config ?? Config(), map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        Choose(w, melee);
        w.Musou!.Frozen = true;
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    static ushort Ork(World w, Vector3 at, float health = 1000f, MusouEnemyType type = MusouEnemyType.Boy)
    {
        ushort id = w.Musou!.Spawn(type, at, 0f, health);
        Assert.NotEqual(0, id);
        return id;
    }

    static Vector3 Polar(World w, float degrees, float metres, float up = 0f) =>
        w.Players[Human].Position + new Vector3(-MathF.Sin(degrees * MathF.PI / 180f) * metres, MathF.Cos(degrees * MathF.PI / 180f) * metres, up);

    /// <summary>The enemies a light swing strikes (a slash event with marker 3 each).</summary>
    static HashSet<int> StruckBySwing(World w)
    {
        var hit = new HashSet<int>();
        for (int t = 0; t < 140; t++)   // the spear's first swing hits at 0.57 / 0.85 s
        {
            Tick(w, t < 3 ? InputButtons.Melee : InputButtons.None);
            foreach (var e in w.Musou!.Events) if (e.Kind == MusouEventKind.Hit && e.Value == 3) hit.Add(e.Enemy);
        }
        return hit;
    }

    // ---------------------------------------------------------------- the combo

    [Fact]
    public void The_spears_combo_is_the_hammers_at_085_with_the_swings_at_three_quarters()
    {
        var s = SpearCombo.Steps[0];
        Assert.Equal(2.33f / 0.85f, s.Duration, 3);
        Assert.Equal(0.57f / 0.85f, s.HitTime, 3);
        Assert.Equal(0.9f / 0.85f, s.ComboOpen, 3);
        Assert.Equal(1.9f / 0.85f, s.StringResetAt, 3);
        Assert.Equal(1.1f / 0.85f, s.EvadeAt, 3);
        Assert.Equal(150f, s.Damage, 3);   // 200 x 0.75 (design 6.1)
        Assert.Equal(7.5f, s.SecondaryDamage, 3);
        Assert.Equal(225f, SpearCombo.Heavy[0].Damage, 3);
        Assert.Equal(0.3f / 0.85f, SpearCombo.Heavy[0].YComboOpen, 3);
        Assert.Equal(4, SpearCombo.Heavy[0].YNext);
        Assert.Equal(ReactionKind.Stun, SpearCombo.Heavy[0].PrimaryReaction.Kind);
        Assert.Equal(ArsenalRules.PowerSpear, SpearCombo.Wargear);
        Assert.Equal(HammerCombo.Steps.Count, SpearCombo.Steps.Count);
        Assert.Equal(HammerCombo.Heavy.Count, SpearCombo.Heavy.Count);
    }

    [Fact]
    public void The_slam_keeps_the_hammers_damage_and_blast_and_turns_the_same_185_degrees()
    {
        var slam = SpearCombo.Heavy[1];
        Assert.Equal(250f, slam.Damage);
        Assert.Same(Blast, slam.Stomp);
        Assert.Equal(1.1f / 0.85f, slam.Duration, 3);
        Assert.Equal((HammerSlam.SteerTo - HammerSlam.SteerFrom) * HammerSlam.SteerRate, (slam.SteerTo - slam.SteerFrom) * slam.SteerRate, 2);
    }

    [Fact]
    public void An_unset_timing_stays_unset()
    {
        var s = SpearCombo.Steps[0];
        Assert.Equal(float.PositiveInfinity, SpearRules.Slow(new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 100f, 0f, 0f), light: true).StringResetAt);
        Assert.True(s.Duration > HammerLight.Duration);
    }

    // ---------------------------------------------------------------- the sweep

    [Fact]
    public void A_spear_sweep_reaches_15_m_over_300_degrees_and_a_hammer_swing_does_not()
    {
        var spear = War(melee: 1);
        ushort near = Ork(spear, Polar(spear, 0f, 14.5f));      // hammer reach: 8.8 m (10.7 with a track-to-hit step)
        ushort far = Ork(spear, Polar(spear, 0f, 16.5f));
        ushort wide = Ork(spear, Polar(spear, 145f, 5f));       // past the war hammer's 120 each side
        ushort behind = Ork(spear, Polar(spear, 160f, 5f));     // outside 150
        ushort high = Ork(spear, Polar(spear, 20f, 4f, 2.0f));  // the flat cone's 1.5 m height band
        var hit = StruckBySwing(spear);
        Assert.Contains((int)near, hit);
        Assert.Contains((int)wide, hit);
        Assert.DoesNotContain((int)far, hit);
        Assert.DoesNotContain((int)behind, hit);
        Assert.DoesNotContain((int)high, hit);
        Assert.Equal(15f, SpearRules.ReachMetres);
        Assert.Equal(150f, SpearRules.HalfAngleDegrees);

        var hammer = War(melee: 0);
        ushort farForHammer = Ork(hammer, Polar(hammer, 0f, 14.5f));
        ushort wideForHammer = Ork(hammer, Polar(hammer, 145f, 5f));
        ushort sideForHammer = Ork(hammer, Polar(hammer, 115f, 5f));
        var hammerHit = StruckBySwing(hammer);
        Assert.DoesNotContain((int)farForHammer, hammerHit);    // past the hammer's 8.8 m (10.7 m with a track-to-hit step)
        Assert.DoesNotContain((int)wideForHammer, hammerHit);   // past the hammer's 120 degrees each side
        Assert.Contains((int)sideForHammer, hammerHit);
    }

    [Fact]
    public void A_spear_sweep_strikes_at_most_twelve_orks_nearest_first()
    {
        var w = War(melee: 1);
        var ids = new List<ushort>();
        for (int k = 0; k < 30; k++) ids.Add(Ork(w, Polar(w, (k - 15) * 6f, 2f + k * 0.3f)));
        var hit = StruckBySwing(w);
        Assert.Equal(SpearRules.MostOrks, hit.Count);
        Assert.Equal(12, SpearRules.MostOrks);
        // Nearest first: the twelve struck are the twelve closest.
        var nearest = ids.OrderBy(id => { w.Musou!.TryGet(id, out var e); return Vector3.Distance(e.Position, w.Players[Human].Position); }).Take(12).Select(i => (int)i).ToHashSet();
        Assert.Equal(nearest, hit);
    }

    [Fact]
    public void A_light_spear_swing_does_150_to_each()
    {
        var w = War(melee: 1);
        ushort ork = Ork(w, Polar(w, 0f, 3f));
        StruckBySwing(w);
        w.Musou!.TryGet(ork, out var e);
        Assert.Equal(1000f - 150f, e.Health, 1);
    }

    // ---------------------------------------------------------------- the hammer's jump-pack rules

    [Fact]
    public void The_spear_takes_the_hammers_jump_pack_rules_and_nothing_else_does()
    {
        var w = War(melee: 1);
        Assert.True(Loadouts.WieldsHammer(w.Config, w.Players[Human]));
        Choose(w, 0);
        Assert.True(Loadouts.WieldsHammer(w.Config, w.Players[Human]));   // the hammer itself
        Assert.True(ArsenalRules.WieldsHammer(ArsenalRules.PowerSpear));
        Assert.False(ArsenalRules.WieldsHammer(ArsenalRules.LightningClaw));
        Assert.False(ArsenalRules.WieldsHammer("sm_mp_melee_powersword"));
    }

    [Fact]
    public void Classic_pvp_never_gives_the_spear_the_hammers_rules()
    {
        var classic = Config(ContentExtras.None);
        var state = new PlayerState { Class = 0, MeleeChoice = 1 };
        Assert.False(Loadouts.WieldsHammer(classic, state));
    }

    // ---------------------------------------------------------------- the impale

    static void Impact(World w) => typeof(World).GetMethod("ArsenalJump", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, [Human, JumpEventKind.Impact]);

    [Fact]
    public void The_dives_landing_impales_the_nearest_enemy_within_2_m_for_600()
    {
        var w = War(melee: 1);
        ushort nearest = Ork(w, Polar(w, 0f, 1.2f));
        ushort second = Ork(w, Polar(w, 90f, 1.8f));
        ushort out2 = Ork(w, Polar(w, 0f, 2.6f));
        Impact(w);
        w.Musou!.TryGet(nearest, out var a); w.Musou.TryGet(second, out var b); w.Musou.TryGet(out2, out var c);
        Assert.Equal(400f, a.Health, 1);
        Assert.Equal(1000f, b.Health, 1);   // one enemy only
        Assert.Equal(1000f, c.Health, 1);
    }

    [Fact]
    public void A_nob_dies_to_the_impale_and_nothing_within_2_m_means_no_impale()
    {
        var w = War(melee: 1);
        ushort nob = Ork(w, Polar(w, 0f, 1f), health: 600f, type: MusouEnemyType.Nob);
        Impact(w);
        w.Musou!.TryGet(nob, out var dead);
        Assert.Equal(MusouEnemyState.Dying, dead.State);

        var alone = War(melee: 1);
        ushort far = Ork(alone, Polar(alone, 0f, 2.6f));
        Impact(alone);
        alone.Musou!.TryGet(far, out var e);
        Assert.Equal(1000f, e.Health, 1);
    }

    [Fact]
    public void Only_the_spear_impales()
    {
        var w = War(melee: 0);
        ushort ork = Ork(w, Polar(w, 0f, 1.2f));
        Impact(w);
        w.Musou!.TryGet(ork, out var e);
        Assert.Equal(1000f, e.Health, 1);
    }

    // ---------------------------------------------------------------- a real pound

    /// <summary>The Assault with the M14 pack (a pound that lands where it began, straight down) and both melee weapons.</summary>
    static SimConfig PackConfig()
    {
        var c = new ClassLoadout("sm - assault", 0, -1, [HammerLight]) { PrimaryChoices = [0], MeleeChoices = [HammerCombo, SpearCombo], Side = Team.SpaceMarines, Role = "assault", JumpPack = 0 };
        return Config() with { Classes = [c, c with { Name = "csm - assault", Side = Team.Chaos }], JumpPacks = [JumpPackTests.Pack] };
    }

    /// <summary>The player jumps and pounds straight down at the hang until the landing's Impact; the Musou events of every step are collected (what a client would be sent). Returns where it landed.</summary>
    static Vector3 Pound(World w, List<MusouEvent> events) => PoundImpact(w, events).Position;

    /// <summary><see cref="Pound"/>, returning the landing's Impact event (its tick and place).</summary>
    static JumpEvent PoundImpact(World w, List<MusouEvent> events)
    {
        bool pounded = false;
        for (int k = 0; k < 300; k++)
        {
            var p = w.Players[Human];
            var b = k == 0 ? InputButtons.Jump : !pounded && p.Jump == JumpPhase.Hang ? InputButtons.Melee : InputButtons.None;
            if (b == InputButtons.Melee) pounded = true;
            w.Step(id => new PlayerInput(w.Tick, 0f, 0f, 0f, b, -1.4f));
            events.AddRange(w.Musou!.Events);
            if (w.LastJumps.FirstOrDefault(j => j.Kind == JumpEventKind.Impact) is { Player: Human } impact) { events.AddRange(AfterLanding(w)); return impact; }
        }
        throw new Xunit.Sdk.XunitException("no impact");
    }

    /// <summary>A few more ticks after the landing (the recovery), their events: what else a client hears within the impale's window.</summary>
    static List<MusouEvent> AfterLanding(World w)
    {
        var more = new List<MusouEvent>();
        for (int k = 0; k < (int)SpearRules.ImpaleWindowTicks + 2; k++) { w.Step(id => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None, -1.4f)); more.AddRange(w.Musou!.Events); }
        return more;
    }

    /// <summary>What a client makes of a landing from the server's events (<see cref="SpearRules.ShowsImpale"/>), once the snapshots are past the confirmation: the impale pose or not.</summary>
    static bool ClientShowsImpale(List<MusouEvent> events, JumpEvent impact, bool predicted = false) =>
        SpearRules.ShowsImpale(events.Any(e => SpearRules.ImpaleEvidence(e, Human) && SpearRules.NearLanding(e.Tick, impact.Tick)), predicted, impact.Tick + SpearRules.ImpaleConfirmTicks, impact.Tick);

    // ---------------------------------------------------------------- the impale on the clients (owner via the lead, 2026-11-11: the point-down pose only on a hit)

    [Fact]
    public void A_landing_that_impales_shows_the_impale_and_one_that_hits_nothing_does_not()
    {
        var probe = War(1, PackConfig());
        var landing = Pound(probe, []);

        // A hit: a sturdy Boy beside the landing survives the pack's blast and takes the impale; the client hears the melee-marker hit at the landing's tick.
        var hit = War(1, PackConfig());
        ushort boy = Ork(hit, landing + new Vector3(0.6f, 0f, 0f), health: 5000f);
        var heard = new List<MusouEvent>();
        var impact = PoundImpact(hit, heard);
        Assert.Contains(heard, e => e.Kind == MusouEventKind.Hit && e.Enemy == boy && e.Value == SpearRules.ImpaleMarker && e.Tick == impact.Tick);
        Assert.True(ClientShowsImpale(heard, impact));

        // A kill: the Nob's death (a melee kill) is the evidence too, even with the hit itself left out (the send budget drops other players' hits before deaths).
        var kill = War(1, PackConfig());
        ushort nob = Ork(kill, landing + new Vector3(0.6f, 0f, 0f), health: 600f, type: MusouEnemyType.Nob);
        var killed = new List<MusouEvent>();
        var killImpact = PoundImpact(kill, killed);
        Assert.Contains(killed, e => e.Kind == MusouEventKind.Death && e.Enemy == nob);
        Assert.True(ClientShowsImpale(killed.Where(e => e.Kind != MusouEventKind.Hit).ToList(), killImpact));

        // A miss: nothing within 2 m, only a Boy 4 m off whom the pack's blast strikes (a body-marker hit): no impale.
        var miss = War(1, PackConfig());
        ushort far = Ork(miss, landing + new Vector3(4f, 0f, 0f), health: 5000f);
        var missed = new List<MusouEvent>();
        var missImpact = PoundImpact(miss, missed);
        Assert.Contains(missed, e => e.Kind == MusouEventKind.Hit && e.Enemy == far && e.Value != SpearRules.ImpaleMarker);   // the blast reached him
        Assert.False(ClientShowsImpale(missed, missImpact));

        // The blast kills the only Ork within 2 m before the impale looks: nothing is left to impale, and the client follows the server (its guess said an Ork was there).
        var blasted = War(1, PackConfig());
        ushort weak = Ork(blasted, landing + new Vector3(0.6f, 0f, 0f), health: 10f);
        var gone = new List<MusouEvent>();
        var blastImpact = PoundImpact(blasted, gone);
        Assert.Contains(gone, e => e.Kind == MusouEventKind.Death && e.Enemy == weak && e.Value != (byte)MusouDeathKind.Melee);
        Assert.False(ClientShowsImpale(gone, blastImpact, predicted: true));

        // The hammer's pound beside the same sturdy Boy: no impale.
        var hammer = War(0, PackConfig());
        Ork(hammer, landing + new Vector3(0.6f, 0f, 0f), health: 5000f);
        var hammered = new List<MusouEvent>();
        var hammerImpact = PoundImpact(hammer, hammered);
        Assert.False(ClientShowsImpale(hammered, hammerImpact));
    }

    [Fact]
    public void The_client_guesses_its_own_landing_until_the_servers_events_must_have_come()
    {
        const uint landed = 1000;
        // Its guess stands while the snapshots have not reached the confirmation; then only the server's evidence counts.
        Assert.True(SpearRules.ShowsImpale(seen: false, predicted: true, newestTick: landed - 3, landingTick: landed));
        Assert.True(SpearRules.ShowsImpale(false, true, landed + SpearRules.ImpaleConfirmTicks - 1, landed));
        Assert.False(SpearRules.ShowsImpale(false, true, landed + SpearRules.ImpaleConfirmTicks, landed));
        Assert.True(SpearRules.ShowsImpale(true, false, landed, landed));
        Assert.False(SpearRules.ShowsImpale(false, false, landed, landed));
        // The window: the landing's own ticks either side; a strike before the jump is far outside it.
        Assert.True(SpearRules.NearLanding(landed + 2, landed) && SpearRules.NearLanding(landed - SpearRules.ImpaleWindowTicks, landed));
        Assert.False(SpearRules.NearLanding(landed - 15, landed) || SpearRules.NearLanding(landed + SpearRules.ImpaleWindowTicks + 1, landed));
        // The guess: an enemy within the impale's reach, flat, and the melee's vertical reach.
        var at = new Vector3(10f, 20f, 0f);
        Assert.True(SpearRules.ImpaleLikely(at, [at + new Vector3(1.9f, 0f, 0.5f)], 2.5f));
        Assert.False(SpearRules.ImpaleLikely(at, [at + new Vector3(2.2f, 0f, 0f), at + new Vector3(0.5f, 0f, 3f)], 2.5f));
        Assert.False(SpearRules.ImpaleLikely(at, [], 2.5f));
        // Only the player's own melee hits and melee kills are evidence; a shot, a blast kill or another player's hit is not.
        Assert.True(SpearRules.ImpaleEvidence(new MusouEvent(MusouEventKind.Hit, landed, 5, Human, SpearRules.ImpaleMarker, at), Human));
        Assert.True(SpearRules.ImpaleEvidence(new MusouEvent(MusouEventKind.Death, landed, 5, Human, (byte)MusouDeathKind.Melee, at), Human));
        Assert.False(SpearRules.ImpaleEvidence(new MusouEvent(MusouEventKind.Hit, landed, 5, Human, 1, at), Human));
        Assert.False(SpearRules.ImpaleEvidence(new MusouEvent(MusouEventKind.Death, landed, 5, Human, (byte)MusouDeathKind.Blast, at), Human));
        Assert.False(SpearRules.ImpaleEvidence(new MusouEvent(MusouEventKind.Hit, landed, 5, Human + 1, SpearRules.ImpaleMarker, at), Human));
    }

    [Fact]
    public void A_real_pound_with_the_spear_impales_and_its_event_reaches_the_client_the_hammer_does_not()
    {
        var probe = War(1, PackConfig());
        var landing = Pound(probe, []);

        var spear = War(1, PackConfig());
        ushort nob = Ork(spear, landing + new Vector3(0.6f, 0f, 0f), health: 600f, type: MusouEnemyType.Nob);
        var events = new List<MusouEvent>();
        Pound(spear, events);
        spear.Musou!.TryGet(nob, out var dead);
        Assert.Equal(MusouEnemyState.Dying, dead.State);
        Assert.Contains(events, e => e.Kind == MusouEventKind.Death && e.Enemy == nob);   // the kill is in the tick's buffer the server sends

        var hammer = War(0, PackConfig());
        ushort other = Ork(hammer, landing + new Vector3(0.6f, 0f, 0f), health: 600f, type: MusouEnemyType.Nob);
        Pound(hammer, []);
        hammer.Musou!.TryGet(other, out var alive);
        Assert.True(alive.Health > 450f);   // the pack's own blast only
    }

    // ---------------------------------------------------------------- not dropped, and the cost

    [Fact]
    public void The_arsenals_items_are_not_dropped_to_a_player()
    {
        var w = War(1);
        var pool = MusouDropRules.Pool(w.Config, w.Players[Human]);
        Assert.Equal(ArsenalRules.PowerSpear, w.Config.Classes[0].MeleeChoices[1].Wargear);
        Assert.DoesNotContain(MusouDropItem.Melee(1), pool);   // the spear is this class's second melee choice (the hammer 0)
        var plain = Config() with { Classes = [Config().Classes[0] with { MeleeChoices = [HammerCombo, new MeleeCombo("sm_mp_melee_powersword", [HammerLight]), SpearCombo] }, Config().Classes[1]] };
        var pool2 = MusouDropRules.Pool(plain, new PlayerState { Class = 0 });
        Assert.Contains(MusouDropItem.Melee(1), pool2);          // a data weapon is
        Assert.DoesNotContain(MusouDropItem.Melee(2), pool2);    // the spear is not
    }

    [Fact]
    public void A_sweep_among_a_thousand_orks_costs_little()
    {
        var w = War(1);
        for (int k = 0; k < 1000; k++) Ork(w, Polar(w, (k % 300) - 150f, 1.5f + (k % 40) * 0.33f));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hit = StruckBySwing(w);
        watch.Stop();
        Assert.Equal(SpearRules.MostOrks, hit.Count);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"140 ticks with 1000 Orks in reach took {watch.ElapsedMilliseconds} ms");
    }
}
