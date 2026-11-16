using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmNet.Tests.Extras.Warfront.Battle;

/// <summary>Warfront W3b: the progression library takes effect in a node battle (XP, level-up refill, picker and protection, the five cards, Ork scaling by level), the squad and stuck-log seams, assists.</summary>
public class WarfrontProgressionWiringTests
{
    static World Node(int humans = 1, bool frozen = true)
    {
        var w = WarfrontBattleTests.War(humans);
        TfwRig.Deploy(w);
        Clear(w);
        w.Musou!.Frozen = frozen;
        return w;
    }

    static WarfrontProgressionRuntime Rt(World w) => w.Tfw!.Warfront!.Progression;

    static WarfrontPlayerState Pl(World w, int id) => Rt(w).Campaign.Player((ulong)id)!;

    /// <summary>Every enemy dies by nobody (attacker 0: no XP).</summary>
    static void Clear(World w)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        for (int k = 0; k < n; k++)
            if (all[k].Alive) w.Musou.Damage(all[k].Id, 0, 1e6f, Vector3.Zero, out _);
    }

    static ushort Ork(World w, MusouEnemyType type, Vector3 at, float health = 1000f) => w.Musou!.Spawn(type, at, 0f, health);

    static ushort Leashed(World w, MusouEnemyType type, Vector3 at) => w.Musou!.SpawnLeashed(type, at, 0f, 1000f, 1, 10f);   // a frozen Ork is otherwise taken for stranded after a few seconds

    static void Stand(World w, int ticks)
    {
        for (int k = 0; k < ticks; k++) w.Step(_ => default);
    }

    static Vector3 Near(World w, int id = 1, float dx = 1.2f) => w.Players[id].Position + new Vector3(0f, dx, 0f);

    static void Kill(World w, ushort ork, int by)
    {
        Assert.True(w.Musou!.Damage(ork, by, 1e6f, Vector3.Zero, out bool killed));
        Assert.True(killed);
    }

    /// <summary>Gives the player cards by levelling him (XP straight into the campaign, no ledger event) and taking each pick.</summary>
    static void Cards(World w, int id, params WarfrontCard[] cards)
    {
        var rt = Rt(w);
        ulong key = (ulong)id;
        rt.Campaign.AddXp(key, WarfrontCampaign.XpForLevel(cards.Length + 1) - Pl(w, id).Xp);
        foreach (var c in cards) Assert.True(rt.Campaign.TryTakePick(key, (int)c));
    }

    static void Capture(World w, TfwBattle battle, int k)
    {
        TfwRig.Teleport(w, k == 3 ? battle.Layout.Hq : battle.Layout.Positions[k]);
        for (int t = 0; t < 120 * 30 && (k == 3 ? !battle.Won && w.Tfw!.Battle is not null : battle.Objectives[k].State != TfwPositionState.Secured); t++)
        {
            Clear(w);
            w.Step(_ => default);
        }
    }

    // ---- XP ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_ordinary_kill_pays_only_the_killer_and_a_kill_by_nobody_pays_nothing()
    {
        var w = Node(2);
        int points = w.Musou!.Rules.Of(MusouEnemyType.Boy).Points;
        Kill(w, Ork(w, MusouEnemyType.Boy, Near(w)), 1);
        Stand(w, 1);
        Assert.Equal(points, Pl(w, 1).Xp);
        Assert.Equal(0, Pl(w, 2).Xp);
        Kill(w, Ork(w, MusouEnemyType.Boy, Near(w)), 0);   // a squad's or a Guardsman's kill: the squad's own list
        Kill(w, Ork(w, MusouEnemyType.Nob, Near(w)), 0);
        Stand(w, 1);
        Assert.Equal(points, Pl(w, 1).Xp);
        Assert.Equal(0, Pl(w, 2).Xp);
    }

    [Fact]
    public void An_elite_or_boss_kill_pays_every_connected_player_once_and_the_killer_not_twice()
    {
        var w = Node(2);
        int nob = w.Musou!.Rules.Of(MusouEnemyType.Nob).Points, boss = w.Musou.Rules.Of(MusouEnemyType.BossNob).Points;
        Kill(w, Ork(w, MusouEnemyType.Nob, Near(w)), 1);
        Stand(w, 1);
        Assert.Equal(nob, Pl(w, 1).Xp);
        Assert.Equal(nob, Pl(w, 2).Xp);
        Kill(w, Ork(w, MusouEnemyType.BossNob, Near(w)), 2);
        Stand(w, 1);
        Assert.Equal(nob + boss, Pl(w, 1).Xp);
        Assert.Equal(nob + boss, Pl(w, 2).Xp);
    }

    [Fact]
    public void A_capture_pays_each_player_once_D_pays_800_and_a_repeated_event_pays_nothing()
    {
        var w = Node(2);
        var battle = w.Tfw!.Battle!;
        Capture(w, battle, 0);
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[0].State);
        Assert.Equal(400, Pl(w, 1).Xp);
        Assert.Equal(400, Pl(w, 2).Xp);
        var rt = Rt(w);
        Assert.Empty(rt.OnBattleChange(battle, TfwBattleChange.PositionSecured));   // the same position reported again
        Assert.Equal(400, Pl(w, 2).Xp);
        Capture(w, battle, 1);
        Capture(w, battle, 2);
        Assert.Equal(1200, Pl(w, 1).Xp);
        Capture(w, battle, 3);
        Assert.Null(w.Tfw.Battle);   // won: back in the hub
        Assert.Equal(1200 + 800, Pl(w, 1).Xp);
        Assert.Equal(1200 + 800, Pl(w, 2).Xp);
    }

    [Fact]
    public void A_player_who_hit_an_Ork_inside_five_seconds_and_did_not_kill_it_has_one_assist()
    {
        var w = Node(2);
        var rt = Rt(w);
        ushort ork = Leashed(w, MusouEnemyType.Boy, Near(w));
        Assert.True(w.Musou!.Damage(ork, 1, 10f, Vector3.Zero, out _));
        Stand(w, 30);
        Assert.True(w.Musou.Damage(ork, 2, 10f, Vector3.Zero, out _));
        Kill(w, ork, 2);
        Stand(w, 1);
        Assert.Equal(1, rt.AssistsOf(1));
        Assert.Equal(0, rt.AssistsOf(2));
        // Alone on the kill: none.
        ork = Leashed(w, MusouEnemyType.Boy, Near(w));
        Kill(w, ork, 1);
        Stand(w, 1);
        Assert.Equal(1, rt.AssistsOf(1));
        // A squad's kill still gives the helper the assist.
        ork = Leashed(w, MusouEnemyType.Boy, Near(w));
        Assert.True(w.Musou.Damage(ork, 2, 10f, Vector3.Zero, out _));
        Kill(w, ork, 0);
        Stand(w, 1);
        Assert.Equal(1, rt.AssistsOf(2));
    }

    [Fact]
    public void An_assist_expires_after_five_seconds_keeps_the_last_two_attackers_and_is_cleared_by_a_kill()
    {
        var assists = new WarfrontAssists();
        Span<int> into = stackalloc int[2];
        assists.Hit(7, 1, 100);
        Assert.Equal(0, assists.Take(7, 2, 100 + assists.WindowTicks + 1, into));   // 5 s and a tick later: nothing
        assists.Hit(7, 1, 100);
        assists.Hit(7, 2, 110);
        assists.Hit(7, 3, 120);   // only the last two are kept
        Assert.Equal(1, assists.Take(7, 3, 125, into));   // the killer is 3: the one before him, 2, helped; 1 was pushed out
        Assert.Equal(2, into[0]);
        Assert.Equal(0, assists.Take(7, 3, 126, into));   // cleared by the kill
    }

    // ---- level-up and the picker ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_level_up_refills_health_and_shield_the_same_tick()
    {
        var w = Node(2);
        Ork(w, MusouEnemyType.Boy, Near(w), 100000f);
        w.Musou!.Frozen = false;
        w.Tfw!.Warfront!.OrkLevelScale = () => (1f, 4f);
        Stand(w, 200);
        var hurt = w.Players[1];
        var body = Loadouts.Body(w.Config, hurt);
        Assert.True(hurt.Shield < body.ShieldMax || hurt.Health < body.HealthMax);
        w.Musou.Frozen = true;
        Clear(w);
        // A Nob killed by the hurt player is worth 100 to everybody: from 1450 that crosses 1500 into level 2.
        Rt(w).Campaign.AddXp(1, 1450);
        Kill(w, Ork(w, MusouEnemyType.Nob, Near(w, 1), 10f), 1);
        Stand(w, 1);
        var after = w.Players[1];
        Assert.Equal(2, Pl(w, 1).Level);
        Assert.Equal(body.HealthMax, after.Health);   // refilled in the tick of the kill
        Assert.Equal(body.ShieldMax, after.Shield);
    }

    static void Aboard(World w, int id)
    {
        Rt(w).Campaign.AddXp((ulong)id, WarfrontCampaign.XpForLevel(2));   // one pick pending
    }

    [Fact]
    public void The_picker_opens_aboard_protects_the_player_refuses_a_stale_index_and_takes_card_one_after_twenty_seconds()
    {
        var w = WarfrontBattleTests.War(2);   // in the hub
        Stand(w, 2);
        Aboard(w, 1);
        Stand(w, 2);
        var offer = w.WarfrontOffer(1);
        Assert.NotNull(offer);
        Assert.Null(w.WarfrontOffer(2));
        Assert.False(w.WarfrontPick(1, 0, offer!.PickIndex + 1));   // stale
        Assert.False(w.WarfrontPick(1, 9, offer.PickIndex));        // no such card
        Assert.NotNull(w.WarfrontOffer(1));
        // Protection: no damage and no input for him, the other one is not touched.
        Assert.Equal(0f, w.DamageTakenScale(1));
        Assert.Equal(1f, w.DamageTakenScale(2));
        var at = w.Players[1].Position;
        w.Step(_ => new PlayerInput(w.Tick + 1, 0f, 1f, 0f, InputButtons.None));
        for (int k = 0; k < 20; k++) w.Step(_ => new PlayerInput(w.Tick + 1, 0f, 1f, 0f, InputButtons.None));
        Assert.True(Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(w.Players[1].Position.X, w.Players[1].Position.Y)) < 0.05f);
        Stand(w, 20 * 30);
        Assert.Null(w.WarfrontOffer(1));
        Assert.Equal([(int)offer.Cards[0]], Pl(w, 1).Cards);   // card 1, taken by the server
        Assert.Equal(1f, w.DamageTakenScale(1));
        // A pick by the player: a fresh offer, the right index, protection over at once.
        Rt(w).Campaign.AddXp(1, WarfrontCampaign.XpForLevel(3) - Pl(w, 1).Xp);
        Stand(w, 2);
        var second = w.WarfrontOffer(1);
        Assert.NotNull(second);
        Assert.True(w.WarfrontPick(1, 2, second!.PickIndex));
        Assert.Null(w.WarfrontOffer(1));
        Assert.Equal((int)second.Cards[2], Pl(w, 1).Cards[1]);
        Assert.False(w.WarfrontPick(1, 2, second.PickIndex));   // nothing stands
    }

    [Fact]
    public void In_the_field_the_picker_waits_for_a_lull_and_opens_no_second_offer_inside_45_seconds()
    {
        var w = Node();
        Stand(w, 4 * 30);   // quiet since the landing
        Aboard(w, 1);
        ushort near = w.Musou!.SpawnLeashed(MusouEnemyType.Boy, Near(w, 1, 6f), 0f, 1000f, 1, 10f);
        Stand(w, 60);
        Assert.Null(w.WarfrontOffer(1));   // an Ork within 12 m: no lull
        Kill(w, near, 0);                  // the Ork is gone and he has been quiet: a lull
        Stand(w, 3);
        var first = w.WarfrontOffer(1);
        Assert.NotNull(first);
        Assert.True(w.WarfrontPick(1, 0, first!.PickIndex));
        Rt(w).Campaign.AddXp(1, WarfrontCampaign.XpForLevel(3) - Pl(w, 1).Xp);   // the next one is pending at once
        Stand(w, 40 * 30);
        Assert.Null(w.WarfrontOffer(1));   // under 45 s since the last opening
        Stand(w, 8 * 30);
        Assert.NotNull(w.WarfrontOffer(1));
    }

    // ---- the cards -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_damage_card_scales_the_damage_dealt_with_and_without_a_power_up()
    {
        var w = Node();
        Stand(w, 1);
        Cards(w, 1, WarfrontCard.Damage, WarfrontCard.Damage);   // +20 %
        Stand(w, 1);
        ushort a = Ork(w, MusouEnemyType.Boy, Near(w), 1000f);
        Assert.True(w.Musou!.TryGet(a, out var e));
        float start = e.Health;
        Assert.True(w.Musou.Damage(a, 1, 100f, Vector3.Zero, out _));
        Assert.True(w.Musou.TryGet(a, out e));
        Assert.Equal(start - 120f, e.Health, 2);
        Assert.True(w.MusouGiveBuff(1, MusouPowerup.DoubleDamage));
        Stand(w, 1);
        Assert.True(w.Musou.TryGet(a, out e));
        start = e.Health;
        Assert.True(w.Musou.Damage(a, 1, 100f, Vector3.Zero, out _));
        Assert.True(w.Musou.TryGet(a, out e));
        Assert.Equal(start - 240f, e.Health, 2);   // the card multiplied into the power-up's x2
    }

    [Fact]
    public void The_hp_and_armour_cards_scale_the_damage_taken_by_whether_the_shield_stands()
    {
        var w = Node();
        Stand(w, 1);
        w.Musou!.Frozen = false;
        w.Tfw!.Warfront!.OrkLevelScale = () => (1f, 6f);
        ushort boy = Ork(w, MusouEnemyType.Boy, Near(w), 100000f);
        int steps = 0;
        for (; steps < 2400 && w.Players[1].Shield > 0f && w.Players[1].Alive; steps++) w.Step(_ => default);   // an Ork strips his shield
        Assert.True(w.Players[1].Alive);
        Assert.True(w.Players[1].Shield <= 0f, $"shield {w.Players[1].Shield} after {steps}");
        Kill(w, boy, 0);
        w.Musou.Frozen = true;
        Cards(w, 1, WarfrontCard.MaxHp, WarfrontCard.MaxHp, WarfrontCard.MaxArmor);
        Stand(w, 1);
        Assert.Equal(1f / 1.2f, w.DamageTakenScale(1), 3);   // shield down: the hp cards
        for (int k = 0; k < 900 && w.Players[1].Shield <= 0f; k++) w.Step(_ => default);   // it comes back
        Assert.True(w.Players[1].Shield > 0f);
        Assert.Equal(1f / 1.15f, w.DamageTakenScale(1), 3);   // shield up: the armour card
    }

    [Fact]
    public void The_attack_speed_card_sets_the_players_attack_speed_bonus()
    {
        var w = Node();
        Stand(w, 1);
        Assert.Equal(0f, w.Players[1].AttackSpeedBonus);
        Assert.Equal(1f, Stims.AttackSpeed(w.Config, w.Players[1]));
        Cards(w, 1, WarfrontCard.AttackSpeed, WarfrontCard.AttackSpeed);
        Stand(w, 1);
        Assert.Equal(0.2f, w.Players[1].AttackSpeedBonus, 4);
        Assert.Equal(1.2f, Stims.AttackSpeed(w.Config, w.Players[1]), 4);   // melee and fire timing both read this
        Stand(w, 1);
        Assert.Equal(0.2f, w.Players[1].AttackSpeedBonus, 4);
    }

    [Fact]
    public void The_cooldown_card_shortens_the_orbital_cooldown_and_the_grenade_refill_never_below_forty_percent()
    {
        uint Orbital(int cards)
        {
            var w = Node();
            Stand(w, 1);
            if (cards > 0) Cards(w, 1, Enumerable.Repeat(WarfrontCard.CooldownReduction, cards).ToArray());
            Stand(w, 1);
            var point = w.Players[1].Position + new Vector3(0f, 20f, 0f);
            Assert.True(w.TfwOrbital(1, point));
            return w.Tfw!.Fire.OrbitalTicksLeft(1, w.Tick);
        }
        uint none = Orbital(0), three = Orbital(3), many = Orbital(12);
        Assert.InRange(none, 1790u, 1810u);   // 60 s
        Assert.InRange(three, (uint)(1800 * 0.76) - 5, (uint)(1800 * 0.76) + 5);
        Assert.InRange(many, (uint)(1800 * 0.40) - 5, (uint)(1800 * 0.40) + 5);   // the floor (the grenade refill takes the same scale: Rt(w).CooldownScale; the test rig's class has no grenade to run it)
    }

    [Fact]
    public void The_damage_cards_multiply_on_top_of_the_melee_cap_with_a_power_up_and_without_one()
    {
        var w = Node();
        Stand(w, 1);
        Cards(w, 1, WarfrontCard.Damage, WarfrontCard.Damage, WarfrontCard.Damage);   // x1.3
        Assert.True(w.MusouGiveBuff(1, MusouPowerup.Supercharged));                   // x2 melee: the owner's cap
        Stand(w, 1);
        Assert.Equal(2.6f, w.MusouMeleeMultiplier(1), 3);                              // the card is not eaten by the cap
        var w2 = Node();
        Stand(w2, 1);
        Cards(w2, 1, Enumerable.Repeat(WarfrontCard.Damage, 12).ToArray());           // x2.2: past the old x2
        Stand(w2, 1);
        Assert.Equal(2.2f, w2.MusouMeleeMultiplier(1), 3);                             // not pulled back to the cap
        var plain = Node();
        Stand(plain, 1);
        Assert.True(plain.MusouGiveBuff(1, MusouPowerup.Supercharged));
        Stand(plain, 1);
        Assert.Equal(2f, plain.MusouMeleeMultiplier(1), 3);                            // no cards: Musou's cap as ever
    }

    [Fact]
    public void The_runtime_joins_and_leaves_only_its_own_campaign()
    {
        var own = new WarfrontProgressionRuntime();
        own.Sync([1, 2]);
        Assert.True(own.Campaign.IsConnected(1) && own.Campaign.IsConnected(2));
        own.Sync([2]);
        Assert.False(own.Campaign.IsConnected(1));

        var campaign = new WarfrontCampaign(9);
        var served = new WarfrontProgressionRuntime(campaign) { KeyOf = id => (ulong)(100 + id) };
        served.Sync([1]);
        Assert.True(served.TryKey(1, out ulong key));
        Assert.Equal(101UL, key);
        Assert.False(campaign.IsConnected(key));   // the server joins and leaves, never the sim
        Assert.True(campaign.Join(key, out _));
        served.Sync([]);                           // unmapped, still the server's to leave
        Assert.False(served.TryKey(1, out _));
        Assert.True(campaign.IsConnected(key));
    }

    [Fact]
    public void The_squad_seam_steps_once_a_tick_and_a_tick_without_a_fight_is_made_up_at_the_next()
    {
        var w = Node();
        int steps = 0;
        var rules = w.Tfw!.Warfront!;
        rules.SquadStep = () => steps++;
        Stand(w, 5);
        steps = 0;
        Stand(w, 60);
        Assert.InRange(steps, 60, 60);   // once a tick while the fight runs: not doubled
        // A tick whose fight never ran (TfwGuardsStep returns early on an empty field): its record is stale.
        typeof(WarfrontBattleRules).GetField("LastFought", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(rules, 0u);
        steps = 0;
        Stand(w, 1);
        Assert.Equal(2, steps);   // the missed tick's step at the start, then this tick's own
        Stand(w, 1);
        Assert.Equal(3, steps);
    }

    [Fact]
    public void The_coop_seam_holds_a_player_no_damage_and_no_input_and_others_are_untouched()
    {
        var w = Node(2);
        Stand(w, 1);
        Assert.Equal(1f, w.DamageTakenScale(1));
        w.Tfw!.Warfront!.CoopHeld = id => id == 1;
        Assert.Equal(0f, w.DamageTakenScale(1));
        Assert.Equal(1f, w.DamageTakenScale(2));
        var at = w.Players[1].Position;
        for (int k = 0; k < 20; k++) w.Step(_ => new PlayerInput(w.Tick + 1, 0f, 1f, 0f, InputButtons.None));
        Assert.True(Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(w.Players[1].Position.X, w.Players[1].Position.Y)) < 0.05f);
        w.Tfw.Warfront.CoopHeld = null;
        Assert.Equal(1f, w.DamageTakenScale(1));
    }

    [Fact]
    public void A_human_kill_raises_the_callback_and_a_taken_pick_is_reported_once()
    {
        var w = WarfrontBattleTests.War(2);
        var rt = Rt(w);
        var kills = new List<WarfrontHumanKill>();
        var picked = new List<WarfrontPickResult>();
        rt.HumanKill = kills.Add;
        rt.Picked = picked.Add;
        Stand(w, 2);
        Aboard(w, 1);
        Stand(w, 2);
        var offer = w.WarfrontOffer(1)!;
        Assert.True(w.WarfrontPick(1, 3, offer.PickIndex));   // slot 4: the Retainer card
        var taken = rt.TakePicks();
        Assert.Single(taken);
        Assert.Equal(WarfrontCard.Retainer, taken[0].Card);
        Assert.Equal(1UL, taken[0].Key);
        Assert.Single(picked);
        Assert.Empty(rt.TakePicks());
        TfwRig.Deploy(w);
        Clear(w);
        ushort ork = Leashed(w, MusouEnemyType.Boy, Near(w));
        Kill(w, ork, 1);
        Stand(w, 1);
        var k = Assert.Single(kills);
        Assert.Equal((1UL, 1, ork, MusouEnemyType.Boy), (k.Key, k.Player, k.Enemy, k.Type));
        Kill(w, Leashed(w, MusouEnemyType.Boy, Near(w)), 0);   // a squad's kill: not a human's
        Stand(w, 1);
        Assert.Single(kills);
    }

    [Fact]
    public void A_key_the_eligible_predicate_refuses_gets_no_shared_xp_but_keeps_its_own_kills()
    {
        var w = Node(2);
        Rt(w).Eligible = key => key != 2;   // player 2 spectates
        int nob = w.Musou!.Rules.Of(MusouEnemyType.Nob).Points, boy = w.Musou.Rules.Of(MusouEnemyType.Boy).Points;
        Kill(w, Leashed(w, MusouEnemyType.Nob, Near(w)), 1);
        Stand(w, 1);
        Assert.Equal(nob, Pl(w, 1).Xp);
        Assert.Equal(0, Pl(w, 2).Xp);
        Kill(w, Leashed(w, MusouEnemyType.Boy, Near(w)), 2);
        Stand(w, 1);
        Assert.Equal(boy, Pl(w, 2).Xp);   // his own ordinary kill still pays him
    }

    // ---- Ork scaling -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_Ork_spawned_after_the_highest_level_rises_has_the_new_health_and_damage_and_living_Orks_do_not()
    {
        var w = Node();
        Stand(w, 1);
        var rules = w.Tfw!.Warfront!;
        var spot = w.Tfw.Battle!.Layout.LandingZone + new Vector3(30f, 0f, 0f);
        ushort before = Ork(w, MusouEnemyType.Boy, spot, 100f);
        Assert.Equal(1f, rules.DamageScaleOf(before));
        Rt(w).Campaign.AddXp(1, WarfrontCampaign.XpForLevel(5));   // level 5: x1.32 health, x1.2 damage
        ushort after = Ork(w, MusouEnemyType.Boy, spot + new Vector3(2f, 0f, 0f), 100f);
        Assert.True(w.Musou!.TryGet(before, out var e));
        Assert.Equal(100f, e.Health, 2);
        Assert.Equal(1f, rules.DamageScaleOf(before));
        Assert.True(w.Musou.TryGet(after, out e));
        Assert.Equal(132f, e.Health, 2);
        Assert.Equal(1.2f, rules.DamageScaleOf(after), 3);
    }

    [Fact]
    public void The_hub_spawns_no_level_scale()
    {
        var w = WarfrontBattleTests.War();
        Rt(w).Campaign.AddXp(1, WarfrontCampaign.XpForLevel(5));
        ushort hub = Ork(w, MusouEnemyType.Boy, TfwTestArena.Hub.Spawns[0].Position, 100f);
        Assert.True(w.Musou!.TryGet(hub, out var e));
        Assert.Equal(100f, e.Health, 2);
    }

    // ---- seams -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_squad_target_reaches_the_Orks_an_attack_on_it_reaches_the_seam_and_never_the_Guardsmen()
    {
        float Hit(float damageScale, out int stepped, out int id)
        {
            var w = Node(frozen: false);
            var rules = w.Tfw!.Warfront!;
            rules.OrkLevelScale = () => (1f, damageScale);
            var spot = w.Tfw.Battle!.Layout.LandingZone + new Vector3(30f, 30f, 0f);
            spot.Z = w.Players[1].Position.Z;
            int steps = 0, hitId = 0;
            float damage = 0f;
            rules.SquadTargets = into => { into[0] = new MusouTarget(WarfrontBattleRules.SquadIdFirst, spot, true, 0.4f); return 1; };
            rules.SquadStep = () => steps++;
            rules.SquadHit = (target, d, from) => { hitId = target; damage += d; };
            Ork(w, MusouEnemyType.Boy, spot + new Vector3(0f, 0.9f, 0f), 1000f);
            int guardsHit = w.TfwBestGuardCleave;
            for (int k = 0; k < 240 && damage == 0f; k++) w.Step(_ => default);
            Assert.Equal(guardsHit, w.TfwBestGuardCleave);
            stepped = steps; id = hitId;
            return damage;
        }
        float one = Hit(1f, out int steps, out int id);
        Assert.True(one > 0f);
        Assert.Equal(WarfrontBattleRules.SquadIdFirst, id);
        Assert.True(steps > 0);
        float three = Hit(3f, out _, out _);
        Assert.Equal(one * 3f, three, 2);   // the Ork's remembered multiplier is applied before the seam
    }

    [Fact]
    public void With_the_squad_seams_unset_a_battle_runs_as_before()
    {
        var w = Node(frozen: false);
        Ork(w, MusouEnemyType.Boy, Near(w), 100000f);
        Stand(w, 120);
        Assert.Null(w.Tfw!.Warfront!.SquadTargets);
        Assert.Null(w.Tfw.Warfront.SquadHit);
        Assert.True(w.Musou!.Alive > 0);   // the battle ran on with the hooks in place and no squad
    }

    [Fact]
    public void A_late_human_may_be_held_back_by_the_seam_and_is_landed_by_default()
    {
        var w = Node();
        w.Tfw!.Warfront!.LateJoinAllowed = id => id != 2;
        w.Spawn(2, w.AssignClass(0));
        Stand(w, 30);
        Assert.False(w.Tfw.Battle is null);
        Assert.True(TfwRig.InStormbird(w.Players[2].Position));
        w.Tfw.Warfront.LateJoinAllowed = null;
        Stand(w, 30);
        Assert.False(TfwRig.InStormbird(w.Players[2].Position));
    }

    [Fact]
    public void The_battle_begin_end_and_objective_seams_fire()
    {
        var w = WarfrontBattleTests.War();
        var rules = w.Tfw!.Warfront!;
        var log = new List<string>();
        rules.BattleBegan = (lane, node) => log.Add($"began {lane} {node}");
        rules.BattleEnded = result => log.Add($"ended {result}");
        rules.ObjectiveSecured = k => log.Add($"secured {k}");
        var battle = TfwRig.Deploy(w, TfwLane.B);
        Clear(w);
        Capture(w, battle, 0);
        for (int k = 1; k < 3; k++) Capture(w, battle, k);
        Capture(w, battle, 3);
        Assert.StartsWith("began B", log[0]);
        Assert.Equal(["secured 0", "secured 1", "secured 2", "secured 3"], log.Skip(1).Take(4).ToArray());
        Assert.Equal("ended Won", log[^1]);
    }

    [Fact]
    public void A_stuck_Ork_in_a_position_being_taken_produces_one_record_with_slot_type_and_position()
    {
        var w = WarfrontBattleTests.War();
        var battle = TfwRig.Deploy(w, TfwLane.C);
        var reports = new List<WarfrontStuckReport>();
        w.Tfw!.Warfront!.StuckSink = reports.Add;
        Clear(w);
        w.Musou!.Frozen = true;   // no headway at all
        var at = battle.Layout.Positions[0];
        ushort id = Ork(w, MusouEnemyType.Boy, at, 1000f);
        TfwRig.Teleport(w, at + new Vector3(3f, 0f, 0f));
        for (int k = 0; k < 90 * 30 && reports.Count == 0; k++) w.Step(_ => default);
        Assert.Single(reports);
        Assert.Equal(id, reports[0].Id);
        Assert.Equal(MusouEnemyType.Boy, reports[0].Type);
        Assert.Equal(TfwLane.C, reports[0].Lane);
        Assert.Equal(WarfrontStuckWhy.Cleared, reports[0].Why);
        Assert.True(Vector3.Distance(reports[0].At, at) < 1f);
        Stand(w, 60);
        Assert.Single(reports);
    }
}
