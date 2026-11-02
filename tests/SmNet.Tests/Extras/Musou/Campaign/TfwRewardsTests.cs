using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode's rewards (pass 2's M6; the owner's items 3, 9, 11, 25, 26): the full loadout from the start, the enemies' boxes, the crates, the cards, and the extra slots that never touch the primary. Synthetic fixture.
/// </summary>
public class TfwRewardsTests
{
    const int Human = TfwRig.Human;
    static WeaponSpec Gun(string name, WeaponSlot slot, int clip, int reserve) => new(name, slot, TriggerKind.FullAuto, 9f, 0.12f, clip, reserve, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly ThrownSpec Frag = new("frag", 2, 1, 1f, 20f, ThrownSpec.NoProjectile), Blind = new("blind", 2, 1, 1f, 20f, ThrownSpec.NoProjectile);

    // Weapons: 0 bolter, 1 plasma gun, 2 melta, 3 lascannon, 4 heavy bolter, 5 bolt pistol, 6 plasma pistol.
    // Perks: 0 mag, 1 grit, 2 the second weapon.
    static readonly SimConfig Raw = new()
    {
        Weapons = [Gun("bolter", WeaponSlot.Ranged, 30, 270), Gun("plasma", WeaponSlot.Ranged, 20, 80), Gun("melta", WeaponSlot.Ranged, 5, 20), Gun("las", WeaponSlot.Ranged, 3, 12), Gun("heavy", WeaponSlot.Ranged, 100, 0),
            Gun("bolt_pistol", WeaponSlot.Pistol, 8, 64), Gun("plasma_pistol", WeaponSlot.Pistol, 10, 40)],
        Thrown = [Frag, Blind],
        Perks = [new PerkSpec("mp_perk_mag", null, PerkKind.Class, 1), new PerkSpec("mp_perk_grit", null, PerkKind.Class, 1), new PerkSpec(Loadouts.SecondWeaponPerk, null, PerkKind.Class, 1)],
        Classes =
        [
            new("sm - tactical", 0, 5, [Light]) { PrimaryChoices = [0, 1, 2, 3, 4], PistolChoices = [5, 6], EquipmentChoices = [0, 1], PerkChoices = [0, 1, 2], DefaultPerk = 0, Side = Team.SpaceMarines, Role = "tactical" },
            new("sm - assault marine", -1, 5, [Light]) { PistolChoices = [5, 6], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "assault" },
        ],
        Pickups = new PickupSpec([new PickupType("ammo", PickupKind.Ammo, 3, 4f, 25f, [60, 20, 5, 3, 40, 20, 10], [0, 0]), new PickupType("nades", PickupKind.Grenades, 1, 4f, 25f, [0, 0, 0, 0, 0, 0, 0], [1, 1])]),
        Extras = ContentExtras.Musou, RespawnTime = 2f,
    };

    static readonly SimConfig Config = MusouLoadout.Prepare(Raw with { MusouRules = MusouRules.Default with { PlayerDamageScale = 0f } });

    static World War()
    {
        var w = TfwRig.War(null, Config);
        w.MusouWeaponDrops = true; w.Tfw!.Rewards.Offering = true;   // the cards are switched off by default (TfwRewardRules.Enabled); these tests are of the cards
        return w;
    }

    static void Keep(World w, params int[] weapons)
    {
        foreach (int weapon in weapons) Assert.True(w.MusouGiveWeapon(Human, weapon));
    }

    static void Key(World w, byte key)
    {
        Assert.True(w.Tfw!.Enqueue(Human, new TfwCommandRecord(77, TfwCommand.SelectSlot, key)));
        w.Step(_ => default);
    }

    static PlayerState Me(World w) => w.Players[Human];

    // ---------------------------------------------------------------- item 25

    [Fact]
    public void A_run_starts_with_the_full_loadout_not_the_pistol_alone()
    {
        var w = War();
        var p = Me(w);
        Assert.Equal((0, 5, WeaponSlot.Ranged), (p.Primary, p.Pistol, p.Slot));   // the class default, which the match config took away for the 5-wave run
        Assert.Equal((30, 270), (p.Clip, p.Reserve));
        Assert.Equal(1, p.Grenades);
    }

    [Fact]
    public void A_chosen_loadout_stands_and_a_landing_keeps_it()
    {
        var w = War();
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 0, Primary = 3, Pistol = 1, Equipment = 1 }));
        TfwRig.Deploy(w);
        TfwRig.Steps(w, 2);
        var p = Me(w);
        Assert.Equal((3, 6, 1), (p.Primary, p.Pistol, p.Equipment));
        Assert.Equal((3, 12), (p.Clip, p.Reserve));
    }

    [Fact]
    public void The_five_wave_run_still_strips_to_the_pistol()
    {
        var w = new World(Config) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = 0 } };
        Assert.True(w.StartMusou(null));
        w.Spawn(1, 0);
        w.MusouJoined(1);
        Assert.Equal((-1, WeaponSlot.Pistol, 0), (w.Players[1].Primary, w.Players[1].Slot, w.Players[1].Grenades));
    }

    // ---------------------------------------------------------------- item 26

    [Fact]
    public void A_found_weapon_goes_to_the_first_slot_and_leaves_the_hand_alone()
    {
        var w = War();
        var before = Me(w);
        Keep(w, 1);
        var after = Me(w);
        Assert.Equal((before.Primary, before.Slot, before.Clip), (after.Primary, after.Slot, after.Clip));
        Assert.Equal(new TfwArsenalView(1, -1, -1, 0, 0), w.TfwArsenalOf(Human));
        Keep(w, 2, 3);
        Assert.Equal(new TfwArsenalView(1, 2, 3, 0, 0), w.TfwArsenalOf(Human));
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public void A_key_puts_its_slot_in_hand_and_key_2_brings_the_primary_back(byte key, int weapon)
    {
        var w = War();
        Keep(w, 1, 2, 3);
        Key(w, key);
        var held = Me(w);
        Assert.Equal((weapon, WeaponSlot.Ranged), (held.Primary, held.Slot));
        Assert.Equal(key, w.TfwArsenalOf(Human).LoadedPrimary);
        Assert.Equal(Raw.Weapons[weapon].ClipSize, held.Clip);
        Key(w, 2);
        Assert.Equal((0, WeaponSlot.Ranged, 30), (Me(w).Primary, Me(w).Slot, Me(w).Clip));
        Assert.Equal((byte)0, w.TfwArsenalOf(Human).LoadedPrimary);
    }

    [Fact]
    public void An_empty_key_does_nothing_and_every_weapon_keeps_its_ammunition()
    {
        var w = War();
        Keep(w, 1);
        Key(w, 4);
        Assert.Equal(0, Me(w).Primary);
        Key(w, 3);
        Assert.Equal(1, Me(w).Primary);
        for (int k = 0; k < 20; k++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
        int spent = Me(w).Clip;
        Assert.True(spent < 20);
        Key(w, 2);
        Assert.Equal(30, Me(w).Clip);
        Key(w, 3);
        Assert.Equal(spent, Me(w).Clip);
    }

    [Fact]
    public void With_all_three_full_a_pickup_replaces_the_slot_in_hand_and_the_primary_is_never_lost()
    {
        var w = War();
        Keep(w, 1, 2, 3);
        Key(w, 4);
        Keep(w, 4);
        Assert.Equal(new TfwArsenalView(1, 4, 3, 4, 0), w.TfwArsenalOf(Human));
        Assert.Equal((4, WeaponSlot.Ranged, 100), (Me(w).Primary, Me(w).Slot, Me(w).Clip));   // the new one stays in hand
        Key(w, 2);
        Assert.Equal(0, Me(w).Primary);
    }

    [Fact]
    public void With_all_three_full_and_the_primary_in_hand_the_slot_last_in_hand_goes()
    {
        var w = War();
        Keep(w, 1, 2, 3);
        Key(w, 5);
        Key(w, 2);
        Keep(w, 4);
        Assert.Equal(new TfwArsenalView(1, 2, 4, 0, 0), w.TfwArsenalOf(Human));
        Assert.Equal(0, Me(w).Primary);
    }

    [Fact]
    public void A_weapon_owned_refills_and_takes_no_slot()
    {
        var w = War();
        Keep(w, 1);
        Key(w, 3);
        for (int k = 0; k < 20; k++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
        Assert.True(Me(w).Clip < 20);
        Keep(w, 1);
        Assert.Equal((20, 80), (Me(w).Clip, Me(w).Reserve));
        Keep(w, 0);   // the own primary, waiting in its bank
        Assert.Equal(new TfwArsenalView(1, -1, -1, 3, 0), w.TfwArsenalOf(Human));
    }

    [Fact]
    public void A_pistol_only_class_keeps_its_finds_in_the_slots_through_the_pistol_field()
    {
        var w = TfwRig.War(null, Config);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 1 }));
        Assert.True(w.TfwRespawnAt(Human, Me(w).Position, 0f));
        TfwRig.Steps(w, 2);
        Assert.Equal((1, -1, WeaponSlot.Pistol), (Me(w).Class, Me(w).Primary, Me(w).Slot));
        Keep(w, 6);
        Key(w, 3);
        Assert.Equal((6, WeaponSlot.Pistol, 10), (Me(w).Pistol, Me(w).Slot, Me(w).Clip));
        Assert.Equal((byte)3, w.TfwArsenalOf(Human).LoadedPistol);
        Key(w, 1);
        Assert.Equal((5, WeaponSlot.Pistol, 8), (Me(w).Pistol, Me(w).Slot, Me(w).Clip));
    }

    [Fact]
    public void A_second_primary_keeps_key_3_and_finds_go_to_4_and_5()
    {
        var w = War();
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 0, Perk1 = 2, Second = 1 }));
        Assert.True(w.TfwRespawnAt(Human, Me(w).Position, 0f));
        TfwRig.Steps(w, 2);
        Assert.Equal(1, Loadouts.SecondIndex(w.Config, Me(w)));
        Keep(w, 2, 3);
        Assert.Equal(new TfwArsenalView(-1, 2, 3, 0, 0), w.TfwArsenalOf(Human));
        Keep(w, 4);
        Assert.Equal(1, Loadouts.SecondIndex(w.Config, Me(w)));
    }

    [Fact]
    public void The_slots_stay_for_the_run_and_a_new_life_refills_them()
    {
        var w = War();
        Keep(w, 1);
        Key(w, 3);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, 3f);
        Assert.True(Me(w).Alive);
        Assert.Equal(0, Me(w).Primary);   // the own primary back
        Assert.Equal(new TfwArsenalView(1, -1, -1, 0, 0), w.TfwArsenalOf(Human));
        Key(w, 3);
        Assert.Equal((1, 20), (Me(w).Primary, Me(w).Clip));
    }

    [Fact]
    public void A_customizer_change_made_with_an_extra_in_hand_stands_at_the_next_life()
    {
        var w = War();
        Keep(w, 1);
        Key(w, 3);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 0, Primary = 2 }));
        TfwRig.Deploy(w);
        TfwRig.Steps(w, 2);
        Assert.Equal((2, WeaponSlot.Ranged, 5), (Me(w).Primary, Me(w).Slot, Me(w).Clip));
        Assert.Equal(new TfwArsenalView(1, -1, -1, 0, 0), w.TfwArsenalOf(Human));
    }

    static void Swap(World w, LoadoutChoice choice)
    {
        Assert.NotNull(w.SetNextLoadout(Human, choice));
        Assert.True(w.Tfw!.Enqueue(Human, new TfwCommandRecord(90, TfwCommand.ChangeClass, 0)));
        TfwRig.Steps(w, 3);
    }

    [Fact]
    public void The_slots_follow_a_class_change_in_the_stormbird_and_come_back_with_the_class()
    {
        var w = War();
        Keep(w, 1, 2);
        Key(w, 3);                                                        // an extra in hand as the class changes
        Assert.True(TfwTestArena.Hub.Contains(Me(w).Position));
        Swap(w, LoadoutChoice.Default with { Class = 1 });
        Assert.Equal((1, -1, 5, WeaponSlot.Pistol), (Me(w).Class, Me(w).Primary, Me(w).Pistol, Me(w).Slot));   // none of the old class's weapons
        Assert.Equal(TfwArsenalView.Empty, w.TfwArsenalOf(Human));
        Key(w, 3);
        Assert.Equal(-1, Me(w).Primary);                                  // the empty slot does nothing
        Keep(w, 6);                                                       // the new class's own kit
        Assert.Equal(new TfwArsenalView(6, -1, -1, 0, 0), w.TfwArsenalOf(Human));
        Swap(w, LoadoutChoice.Default with { Class = 0 });
        Assert.Equal((0, 0, WeaponSlot.Ranged, 30), (Me(w).Class, Me(w).Primary, Me(w).Slot, Me(w).Clip));     // the default primary, not the extra it left in hand
        Assert.Equal(new TfwArsenalView(1, 2, -1, 0, 0), w.TfwArsenalOf(Human));
        Key(w, 4);
        Assert.Equal((2, 5), (Me(w).Primary, Me(w).Clip));                // full, and it works
        Key(w, 2);
        Assert.Equal(0, Me(w).Primary);
    }

    [Fact]
    public void A_setup_of_the_same_class_with_an_extra_in_hand_stands_even_on_that_weapon()
    {
        var w = War();
        Keep(w, 1);
        Key(w, 3);
        Swap(w, LoadoutChoice.Default with { Class = 0, Primary = 1 });   // chooses as its primary the very weapon in the slot
        Assert.Equal((1, (byte)0), (Me(w).Primary, w.TfwArsenalOf(Human).LoadedPrimary));
        Key(w, 2);
        Assert.Equal(1, Me(w).Primary);
    }

    [Fact]
    public void The_kits_equipment_and_perk_follow_their_class()
    {
        var w = War();
        var kit = w.Tfw!.Rewards.Kit(Human, 0);
        kit.Equipment = 1;
        kit.Perk = 1;
        Swap(w, LoadoutChoice.Default with { Class = 0 });
        Assert.Equal((1, 1), (Me(w).Equipment, Me(w).Perk2));
        Swap(w, LoadoutChoice.Default with { Class = 1 });
        Assert.Equal((0, -1), (Me(w).Equipment, Me(w).Perk2));
        Swap(w, LoadoutChoice.Default with { Class = 0 });
        Assert.Equal((1, 1), (Me(w).Equipment, Me(w).Perk2));
    }

    [Fact]
    public void A_card_picked_after_a_class_change_stays_with_the_class_it_was_offered_to()
    {
        var w = War();
        var offer = Won(w);
        Assert.Equal(TfwRewardKind.Weapon, offer.First.Kind);
        Swap(w, LoadoutChoice.Default with { Class = 1 });
        Pick(w, offer.Sequence, 0);
        Assert.Equal(TfwArsenalView.Empty, w.TfwArsenalOf(Human));        // nothing of the Tactical's in the Assault's slots
        Assert.Equal(offer.First.Item, (ushort)w.Tfw!.Rewards.Kit(Human, 0).Weapons[0]);
    }

    [Fact]
    public void A_class_change_outside_the_stormbird_changes_nothing()
    {
        var w = War();
        Keep(w, 1);
        TfwRig.Deploy(w);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 1 }));
        Assert.True(w.Tfw!.Enqueue(Human, new TfwCommandRecord(90, TfwCommand.ChangeClass, 0)));
        TfwRig.Steps(w, 3);
        Assert.Equal(0, Me(w).Class);
        Assert.Equal(new TfwArsenalView(1, -1, -1, 0, 0), w.TfwArsenalOf(Human));
    }

    [Fact]
    public void A_key_during_a_strike_waits_for_its_end()
    {
        var w = War();
        Keep(w, 1);
        for (int k = 0; k < 2; k++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee));
        Assert.True(MusouLoadout.Busy(Me(w)));
        Key(w, 3);
        Assert.Equal(0, Me(w).Primary);
        TfwRig.Seconds(w, 2f);
        Assert.Equal(1, Me(w).Primary);
    }

    [Fact]
    public void A_new_run_gives_back_the_primary_and_empties_the_slots()
    {
        var w = War();
        Keep(w, 1);
        Key(w, 3);
        Assert.Equal(1, Me(w).Primary);
        w.TfwNewRunForTests();
        TfwRig.Steps(w, 2);
        Assert.Equal(0, Me(w).Primary);
        Assert.Equal(TfwArsenalView.Empty, w.TfwArsenalOf(Human));
    }

    // ---------------------------------------------------------------- item 3

    [Fact]
    public void The_boxes_chance_is_about_a_twelfth_for_fodder_and_two_fifths_for_elites()
    {
        int fodder = 0, elites = 0;
        const int n = 20000;
        for (int k = 0; k < n; k++)
        {
            if (TfwRewardRules.Boxes(MusouEnemyType.Boy, (uint)(k * 7), (ushort)(k % 512 + 1))) fodder++;
            if (TfwRewardRules.Boxes(MusouEnemyType.Nob, (uint)(k * 7), (ushort)(k % 512 + 1))) elites++;
        }
        Assert.InRange(fodder / (float)n, 0.07f, 0.09f);
        Assert.InRange(elites / (float)n, 0.38f, 0.42f);
    }

    [Fact]
    public void Kos_leave_the_cores_own_boxes_in_a_battle()
    {
        var w = War();
        TfwRig.Deploy(w);
        var enemies = w.Musou!;
        var at = Me(w).Position;
        for (int round = 0; round < 40 && w.LivePickups.Count == 0; round++)
        {
            for (int k = 0; k < 40; k++) enemies.Spawn(MusouEnemyType.ArdBoy, at + new Vector3(k, 3f, 0f), 0f, 170f);
            TfwRig.KillAll(w);
            TfwRig.Steps(w, 2);
        }
        Assert.NotEmpty(w.LivePickups);
        var box = w.LivePickups[0];
        Assert.True(box.Dropper > TfwRewardRules.BoxDropperBase);
    }

    [Fact]
    public void An_ammunition_box_reaches_the_weapons_in_the_bank()
    {
        var w = War();
        Keep(w, 1);
        Key(w, 3);
        for (int k = 0; k < 20; k++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire | InputButtons.Reload));
        Key(w, 2);
        for (int k = 0; k < 20; k++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
        Key(w, 3);
        for (int k = 0; k < 90; k++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));   // the plasma gun in hand, the bolter in the bank
        var inHand = Me(w);
        Assert.NotNull(w.SpawnPickup(0, inHand.Position));
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Interact));
        TfwRig.Steps(w, 2);
        Assert.True(Me(w).Reserve >= inHand.Reserve);
        Key(w, 2);
        Assert.Equal(270, Me(w).Reserve);   // 60 more, capped
    }

    // ---------------------------------------------------------------- item 9

    [Fact]
    public void Each_landing_places_six_crates_of_the_class_that_last_the_battle_and_outlive_a_death()
    {
        var w = War();
        TfwRig.Deploy(w);
        var crates = w.MusouDrops.Where(d => d.Crate).ToArray();
        Assert.Equal(TfwSites.CrateCount, crates.Length);
        Assert.All(crates, c => Assert.Equal(Human, c.Player));
        Assert.All(crates, c => Assert.True(MusouDropItem.Index(c.Item) is 1 or 2 or 3 or 4 or 6, $"{c.Item}"));   // the class's list, without what it carries
        TfwRig.Seconds(w, MusouDropRules.LifetimeSeconds + 1f);
        Assert.Equal(TfwSites.CrateCount, w.MusouDrops.Count(d => d.Crate && d.State == MusouDropState.Live));
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 3);
        Assert.Equal(TfwSites.CrateCount, w.MusouDrops.Count(d => d.Crate && d.State == MusouDropState.Live));
    }

    [Fact]
    public void A_crate_opened_goes_to_a_slot_and_a_battles_end_takes_the_rest()
    {
        var w = War();
        TfwRig.Deploy(w);
        var crate = w.MusouDrops.First(d => d.Crate);
        Assert.True(w.TfwRespawnAt(Human, crate.Position, 0f));
        TfwRig.Steps(w, 2);
        var before = Me(w);
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Interact));
        Assert.Equal(before.Primary, Me(w).Primary);
        Assert.Equal(MusouDropItem.Index(crate.Item), w.TfwArsenalOf(Human).Slot3);
        Assert.Equal(TfwSites.CrateCount - 1, w.MusouDrops.Count(d => d.Crate && d.State == MusouDropState.Live));
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Seconds(w, TfwBattleRules.PickupSeconds + 0.2f);
        Assert.DoesNotContain(w.MusouDrops, d => d.Crate);
    }

    [Fact]
    public void The_crate_sites_are_apart_and_a_seed_gives_the_same_places()
    {
        var sites = new TfwSites(TfwTestArena.Field, null, null);
        var layout = TfwTestArena.Layout();
        var one = sites.Crates(layout, 5);
        Assert.Equal(TfwSites.CrateCount, one.Count);
        for (int a = 0; a < one.Count; a++)
            for (int b = a + 1; b < one.Count; b++) Assert.True(Vector3.Distance(one[a], one[b]) >= TfwSites.CrateSpacing - 0.5f);
        Assert.Equal(one, sites.Crates(layout, 5));
    }

    // ---------------------------------------------------------------- item 11

    static TfwRewardOffer Won(World w)
    {
        TfwRig.WinBattle(w, TfwLane.A);
        return Assert.IsType<TfwRewardOffer>(w.Tfw!.Rewards.Offer(Human));
    }

    static void Pick(World w, ushort offer, byte card)
    {
        Assert.True(w.Tfw!.Enqueue(Human, new TfwCommandRecord(88, TfwCommand.ChooseReward, card, Offer: offer)));
        TfwRig.Steps(w, 3);
    }

    [Fact]
    public void A_won_map_offers_one_of_each_kind_for_the_class_and_a_left_one_none()
    {
        var w = War();
        TfwRig.Deploy(w);
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Seconds(w, TfwBattleRules.PickupSeconds + 0.2f);
        Assert.Null(w.Tfw!.Rewards.Offer(Human));
        var offer = Won(w);
        Assert.Equal(TfwRewardKind.Weapon, offer.First.Kind);
        Assert.Contains(MusouDropItem.Index(offer.First.Item), new[] { 1, 2, 3, 4, 6 });
        Assert.Equal(new TfwRewardCard(TfwRewardKind.Equipment, 1), offer.Second);   // the one it does not carry
        Assert.Equal(new TfwRewardCard(TfwRewardKind.Perk, 1), offer.Third);         // grit: not its own, not the second weapon's perk
    }

    [Fact]
    public void A_delayed_pick_of_an_older_offer_does_nothing()
    {
        var w = War();
        var first = Won(w);
        var second = w.Tfw!.Rewards.Roll(w.Config, Me(w), w.Tfw.Rewards.Kit(Human, 0), w.Tick, Human)!.Value;
        Assert.NotEqual(first.Sequence, second.Sequence);
        Pick(w, first.Sequence, 0);
        Assert.Equal(TfwArsenalView.Empty, w.TfwArsenalOf(Human));
        Assert.NotNull(w.Tfw.Rewards.Offer(Human));
        Pick(w, second.Sequence, 0);
        Assert.Null(w.Tfw.Rewards.Offer(Human));
        Assert.Equal(MusouDropItem.Index(second.First.Item) == 6 ? 6 : second.First.Item, w.TfwArsenalOf(Human).Slot3);
        Pick(w, second.Sequence, 1);   // once
        Assert.Equal(0, Me(w).Equipment);
    }

    [Fact]
    public void A_pick_in_the_field_is_no_free_refill_and_the_offer_waits_for_the_stormbird()
    {
        var w = War();
        var offer = Won(w);
        TfwRig.Deploy(w);
        for (int k = 0; k < 20; k++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
        int spent = Me(w).Clip;
        Assert.True(spent < 30);
        Pick(w, offer.Sequence, 0);
        Assert.True(Me(w).Clip <= spent);                                  // no fresh arming
        Assert.Equal(TfwArsenalView.Empty, w.TfwArsenalOf(Human));
        Assert.Equal(offer, w.Tfw!.Rewards.Offer(Human));                  // still standing
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Seconds(w, TfwBattleRules.PickupSeconds + 0.2f);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
        Pick(w, offer.Sequence, 0);
        Assert.Null(w.Tfw.Rewards.Offer(Human));
        Assert.Equal(offer.First.Item, (ushort)w.TfwArsenalOf(Human).Slot3);
    }

    [Fact]
    public void A_weapon_card_over_a_loaded_slot_brings_the_own_weapon_home()
    {
        var w = War();
        Won(w);
        Keep(w, 1, 2, 3);
        Key(w, 4);                                                         // in hand, in the primary's field
        Assert.Equal((byte)4, w.TfwArsenalOf(Human).LoadedPrimary);
        var offer = w.Tfw!.Rewards.Roll(w.Config, Me(w), w.Tfw.Rewards.Kit(Human, 0), w.Tick, Human)!.Value;
        Assert.Equal(TfwRewardKind.Weapon, offer.First.Kind);
        Pick(w, offer.Sequence, 0);
        Assert.Equal(new TfwArsenalView(1, (short)offer.First.Item, 3, 0, 0), w.TfwArsenalOf(Human));
        Assert.Equal((0, 30), (Me(w).Primary, Me(w).Clip));                // the own primary, never lost
    }

    [Fact]
    public void The_equipment_and_perk_cards_lie_on_the_loadout_for_the_run()
    {
        var w = War();
        var offer = Won(w);
        Pick(w, offer.Sequence, 1);
        Assert.Equal((1, 1), (Me(w).Equipment, Me(w).Grenades));
        var second = w.Tfw!.Rewards.Roll(w.Config, Me(w), w.Tfw.Rewards.Kit(Human, 0), w.Tick, Human)!.Value;
        Pick(w, second.Sequence, 2);
        Assert.Equal((0, 1), (Me(w).Perk1, Me(w).Perk2));
        Assert.Equal(WeaponSlot.Ranged, Me(w).Slot);
        TfwRig.Deploy(w);
        TfwRig.Steps(w, 2);
        Assert.Equal((1, 1), (Me(w).Equipment, Me(w).Perk2));   // it lasts through the landing
    }

    [Fact]
    public void A_perk_card_never_takes_the_slot_of_weapon_versatility()
    {
        var c = Config;
        var s = new PlayerState(1, default, default, 0f, true) { Class = 0, Perk1 = 0, Perk2 = 2 };
        Assert.Equal((1, 2), (TfwRewards.WithPerk(c, s, 1).Perk1, TfwRewards.WithPerk(c, s, 1).Perk2));
        Assert.Equal((0, 1), (TfwRewards.WithPerk(c, s with { Perk2 = -1 }, 1).Perk1, TfwRewards.WithPerk(c, s with { Perk2 = -1 }, 1).Perk2));
    }

    [Fact]
    public void Kits_are_per_class()
    {
        var rewards = new TfwRewards();
        rewards.Kit(1, 0).Weapons[0] = 3;
        Assert.False(rewards.Kit(1, 1).Holds(3));
        Assert.False(rewards.Kit(2, 0).Holds(3));
        Assert.True(rewards.Kit(1, 0).Holds(3));
        rewards.NewRun();
        Assert.False(rewards.Kit(1, 0).Holds(3));
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void Tag_15_and_a_crates_bit_round_trip()
    {
        var a = new TfwArsenalView(4, -1, 6, 3, 5);
        Assert.Equal(a, TfwWire.ReadArsenal(TfwWire.WriteArsenal(a)));
        Assert.Null(TfwWire.ReadArsenal(new byte[7]));
        var bad = TfwWire.WriteArsenal(a);
        bad[6] = 2;
        Assert.Null(TfwWire.ReadArsenal(bad));
        var box = new MusouBox(new Vector3(-10f), new Vector3(10f));
        var list = MusouWire.ReadDrops(MusouWire.WriteDrops([new MusouDropRecord(1, Vector3.Zero, 2, MusouDropState.Live, true), new MusouDropRecord(2, Vector3.Zero, 2, MusouDropState.Picked)], box), box)!;
        Assert.True(list[0].Crate);
        Assert.False(list[1].Crate);
        Assert.Equal(MusouDropState.Picked, list[1].State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void A_weapon_key_that_does_not_exist_is_refused(byte key) =>
        Assert.Null(TfwWire.ReadCommand(TfwWire.WriteCommand(new TfwCommandRecord(1, TfwCommand.SelectSlot, key))));
}
