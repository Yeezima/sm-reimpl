using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmSim;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// fix/customizer-weapons-perks (the owner, 2026-10-29: "i can't use the new weapons in the customizer. i'm not able to select the power sword as an example, or perks"):
/// what the creator offers for every class of the player's install (read-only) is exactly what the server accepts, through the creator's own edits and the join's wire
/// indices. The catalogue, the rows, <see cref="LoadoutEdits"/>, <see cref="ProfileWire"/> and <see cref="Loadouts.Resolve"/> are the real ones; only the clicks are not here
/// (the front end's <c>--ui-selftest</c> presses the rows). Skipped when no install is found (CI); no original bytes are kept.
/// </summary>
public class CustomizerServerAgreementTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    /// <summary>A fact that is skipped unless the game install is found on this machine.</summary>
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    static readonly Lazy<(CustomizationCatalog Catalog, SimConfig Config)> Loaded = new(() =>
    {
        string game = Game.Value!;
        return (CustomizationCatalog.Load(game, []), GameplayTuning.Load(game).Config);
    });

    static string N(string name) => ProfileWire.Neutral(name);

    [InstallFact]
    public void Every_class_lists_exactly_the_choices_the_server_has()
    {
        var (catalog, config) = Loaded.Value;
        foreach (var options in catalog.Perks.Classes)
        {
            int cls = LoadoutIndices.Class(config, options.Key);
            Assert.True(cls >= 0, options.Key);
            foreach (var key in LoadoutRows.SlotKeys)
            {
                var listed = options.For(key).Items.Where(i => i.Simulated).Select(i => i.Name).ToList();
                var served = LoadoutIndices.Names(config, cls, key).Where(n => !(key == "melee" && n.Contains("core_abil", StringComparison.OrdinalIgnoreCase))).ToList();   // the heavy evade is a core ability
                Assert.Equal(served, listed);
            }
            Assert.Equal(LoadoutIndices.Names(config, cls, "perk"), options.Perks.Select(p => p.Name));
        }
    }

    [InstallFact]
    public void Every_weapon_and_grenade_the_creator_offers_is_chosen_and_kept_by_the_server_on_both_sides()
    {
        var (catalog, config) = Loaded.Value;
        int chosen = 0;
        foreach (string race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
            foreach (string role in ProfileStore.Roles(catalog))
            {
                var options = catalog.Perks.ForRole(role, race)!;
                var side = ProfileWire.SideOf(race);
                int cls = ProfileWire.ClassIndex(config, side, role);
                foreach (var key in LoadoutRows.SlotKeys.Where(k => options.For(k).Selectable))
                    foreach (var item in options.For(key).Items.Where(i => i.Simulated))
                    {
                        // The creator previewing this side stores this side's name; True Grit first for the pistol-slot bolters.
                        var profile = LoadoutEdits.Create(CustomizationProfile.Default(catalog), 1, role, catalog).Profile;
                        if (item.RequiredPerk is { } need) profile = LoadoutEdits.SetPerk(profile, 1, 1, need, catalog).Profile;
                        var edit = LoadoutEdits.SetItem(profile, 1, key, N(item.Name), catalog);
                        Assert.True(edit.Changed, $"{race}/{role}: {item.Name}: {edit.Notice}");
                        var custom = edit.Profile.ClassAt(1)!;
                        var wire = ProfileWire.ToLoadout(custom, config, side);
                        var resolved = Loadouts.Resolve(config, cls, wire);
                        int index = key switch { "primary" => resolved.Primary, "pistol" => resolved.Pistol, "melee" => resolved.Melee, _ => resolved.Equipment };
                        var names = LoadoutIndices.Names(config, cls, key);
                        Assert.True(index >= 0 && index < names.Count, $"{race}/{role}: {item.Name} was dropped (index {index})");
                        Assert.Equal(N(item.Name), N(names[index]));
                        // The creator shows it selected on this side, whichever side stored it.
                        var shown = LoadoutRows.Effective(catalog, options, key, custom);
                        Assert.Equal(item.Name, shown?.Name);
                        chosen++;
                    }
            }
        Assert.Equal(56, chosen);   // perks/inert-2: combat stims for the six classes
    }

    [InstallFact]
    public void The_power_sword_and_the_other_new_melee_weapons_are_chosen_for_both_assault_classes()
    {
        var (catalog, config) = Loaded.Value;
        var profile = LoadoutEdits.Create(CustomizationProfile.Default(catalog), 1, "assault", catalog).Profile;
        foreach (var (race, wargears) in new[]
        {
            (CustomizationCatalog.SpaceMarine, new[] { "mp_melee_power_axe", "mp_melee_2hand_thunder_hammer", "mp_melee_powersword" }),
            (CustomizationCatalog.Chaos, new[] { "mp_melee_chain_axe", "mp_melee_2hand_power_mace", "mp_melee_powersword" }),
        })
            foreach (string wargear in wargears)
            {
                var edit = LoadoutEdits.SetItem(profile, 1, "melee", wargear, catalog);
                Assert.True(edit.Changed, $"{race}: {wargear}: {edit.Notice}");
                var side = ProfileWire.SideOf(race);
                var wire = ProfileWire.ToLoadout(edit.Profile.ClassAt(1)!, config, side);
                var chosen = Loadouts.ClassOf(config, wire.Class)!.MeleeChoices[Loadouts.Resolve(config, wire.Class, wire).Melee].Wargear;
                Assert.EndsWith(wargear, chosen);
            }
        // The same power sword name serves both sides; the axe and the hammer follow the side's own list by position.
        var sword = LoadoutEdits.SetItem(profile, 1, "melee", "mp_melee_powersword", catalog).Profile.ClassAt(1)!;
        Assert.Equal("sm_mp_melee_powersword", Loadouts.ClassOf(config, ProfileWire.ToLoadout(sword, config, Team.SpaceMarines).Class)!.MeleeChoices[ProfileWire.ToLoadout(sword, config, Team.SpaceMarines).Melee].Wargear);
        var chaosAxe = LoadoutEdits.SetItem(profile, 1, "melee", "mp_melee_chain_axe", catalog).Profile.ClassAt(1)!;
        var onMarines = ProfileWire.ToLoadout(chaosAxe, config, Team.SpaceMarines);
        Assert.Equal("sm_mp_melee_power_axe", Loadouts.ClassOf(config, onMarines.Class)!.MeleeChoices[onMarines.Melee].Wargear);
    }

    [InstallFact]
    public void The_creators_counterpart_of_an_item_is_the_wires()
    {
        var (catalog, config) = Loaded.Value;
        int checkedItems = 0;
        foreach (string role in ProfileStore.Roles(catalog))
            foreach (var (from, to) in new[] { (CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos), (CustomizationCatalog.Chaos, CustomizationCatalog.SpaceMarine) })
            {
                var source = catalog.Perks.ForRole(role, from)!;
                var target = catalog.Perks.ForRole(role, to)!;
                var side = ProfileWire.SideOf(to);
                int cls = ProfileWire.ClassIndex(config, side, role);
                foreach (var key in LoadoutRows.SlotKeys)
                    foreach (var item in source.For(key).Items.Where(i => i.Simulated))
                    {
                        // A custom class that stored this side's name, played on the other side: the creator's Resolve and the Join's wire must name the same item.
                        var custom = LoadoutEdits.New(1, role, catalog) with
                        {
                            Primary = key == "primary" ? N(item.Name) : null, Pistol = key == "pistol" ? N(item.Name) : null,
                            Melee = key == "melee" ? N(item.Name) : null, Equipment = key == "equipment" ? N(item.Name) : null,
                        };
                        var wire = ProfileWire.ToLoadout(custom, config, side);
                        int index = key switch { "primary" => wire.Primary, "pistol" => wire.Pistol, "melee" => wire.Melee, _ => wire.Equipment };
                        var shown = catalog.Perks.Resolve(target, key, N(item.Name));
                        var names = LoadoutIndices.Names(config, cls, key);
                        Assert.True(index >= 0 && index < names.Count, $"{from}->{to} {role} {item.Name}: the wire dropped it");
                        Assert.True(shown is not null, $"{from}->{to} {role} {item.Name}: the creator shows nothing");
                        Assert.Equal(N(names[index]), N(shown!.Name));
                        checkedItems++;
                    }
            }
        Assert.Equal(62, checkedItems);   // perks/inert-2: combat stims for the six classes
    }

    [InstallFact]
    public void A_power_sword_and_two_perks_join_and_wait_for_the_respawn_as_chosen()
    {
        var (catalog, config) = Loaded.Value;
        var profile = LoadoutEdits.Create(CustomizationProfile.Default(catalog), 1, "assault", catalog).Profile;
        profile = LoadoutEdits.SetItem(profile, 1, "melee", "mp_melee_powersword", catalog).Profile;
        profile = LoadoutEdits.SetPerk(profile, 1, 0, "mp_perk_jump_pack_take_off_damage", catalog).Profile;
        profile = LoadoutEdits.SetPerk(profile, 1, 1, "mp_perk_weap_power_sword_heal_on_hit", catalog).Profile;
        var plan = JoinPlan.Build(profile, Selection.CustomSlot(1), config, catalog, SideChoice.SpaceMarines);   // what a Join sends
        // The Join: the Hello's indices through World.Spawn, the server's own line.
        var world = new World(config, null, [new SpawnPoint(System.Numerics.Vector3.Zero, 0f)]);
        var c = plan.Choice;
        var joined = world.Spawn(1, c.Class, c.Primary, c.Pistol, c.Melee, c.Equipment, c.Perk1, c.Perk2);
        string line = Loadouts.Describe(config, joined);
        Assert.Contains("melee sm_mp_melee_powersword", line);
        Assert.Contains("perks mp_perk_jump_pack_take_off_damage + mp_perk_weap_power_sword_heal_on_hit", line);
        Assert.Equal("sm_mp_melee_powersword", Loadouts.Melee(config, joined).Count > 0 ? Loadouts.ClassOf(config, joined.Class)!.MeleeChoices[Loadouts.MeleeChoiceOf(config, joined)].Wargear : "");
        // The in-match picker: the same plan as a loadout change; the server resolves it unchanged and keeps it for the next life.
        var other = new World(config, null, [new SpawnPoint(System.Numerics.Vector3.Zero, 0f)]);
        other.Spawn(1, c.Class);
        Assert.Equal(c, other.SetNextLoadout(1, c));
        Assert.Equal(c, other.NextLoadout(1));
        // The Chaos Raptor the same way (the chain axe is its counterpart of the power axe; the power sword is shared).
        var chaos = JoinPlan.Build(profile, Selection.CustomSlot(1), config, catalog, SideChoice.Chaos).Choice;
        var raptor = world.Spawn(2, chaos.Class, chaos.Primary, chaos.Pistol, chaos.Melee, chaos.Equipment, chaos.Perk1, chaos.Perk2);
        Assert.Contains("melee csm_mp_melee_powersword", Loadouts.Describe(config, raptor));
    }

    [InstallFact]
    public void Every_perk_of_every_class_goes_into_both_slots_and_reaches_the_server()
    {
        var (catalog, config) = Loaded.Value;
        int placed = 0;
        foreach (string race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
            foreach (string role in ProfileStore.Roles(catalog))
            {
                var options = catalog.Perks.ForRole(role, race)!;
                var side = ProfileWire.SideOf(race);
                int cls = ProfileWire.ClassIndex(config, side, role);
                var served = LoadoutIndices.Names(config, cls, "perk");
                string[] names = options.Perks.Select(p => p.Name).ToArray();
                foreach (string perk in names)
                    for (int slot = 0; slot < 2; slot++)
                    {
                        var profile = LoadoutEdits.Create(CustomizationProfile.Default(catalog), 1, role, catalog).Profile;
                        // The class default perk sits in slot 1 until another is chosen; slot 2 cannot repeat it (the creator refuses it: not a choice).
                        bool isDefault = options.DefaultPerk?.Name == perk;
                        if (isDefault && slot == 1) continue;
                        var edit = LoadoutEdits.SetPerk(profile, 1, slot, perk, catalog);
                        Assert.True(edit.Changed || isDefault, $"{race}/{role}: {perk} in slot {slot + 1}: {edit.Notice}");
                        var custom = edit.Profile.ClassAt(1)!;
                        var resolved = Loadouts.Resolve(config, cls, ProfileWire.ToLoadout(custom, config, side));
                        int index = slot == 0 ? resolved.Perk1 : resolved.Perk2;
                        if (isDefault && index < 0) { placed++; continue; }   // the class default is the wire default
                        Assert.True(index >= 0 && index < served.Count && served[index] == perk, $"{race}/{role}: {perk} in slot {slot + 1} reached the server as {index}");
                        placed++;
                    }
                // Two perks together, neither the default.
                var other = names.Where(n => n != options.DefaultPerk?.Name).ToArray();
                var pair = LoadoutEdits.SetPerk(LoadoutEdits.SetPerk(LoadoutEdits.Create(CustomizationProfile.Default(catalog), 1, role, catalog).Profile, 1, 0, other[0], catalog).Profile, 1, 1, other[^1], catalog).Profile.ClassAt(1)!;
                var both = Loadouts.Resolve(config, cls, ProfileWire.ToLoadout(pair, config, side));
                Assert.Equal((other[0], other[^1]), (served[both.Perk1], served[both.Perk2]));
            }
        Assert.True(placed > 100, placed.ToString());
    }

    [InstallFact]
    public void The_true_grit_rule_holds_from_the_creator_to_the_server()
    {
        var (catalog, config) = Loaded.Value;
        foreach (var (race, pistols) in new[]
        {
            (CustomizationCatalog.SpaceMarine, new[] { "mp_pistol_bolter", "mp_pistol_bolter_gold" }),
            (CustomizationCatalog.Chaos, new[] { "mp_pistol_bolter", "mp_pistol_bolter_gold" }),
        })
            foreach (string pistol in pistols)
            {
                var side = ProfileWire.SideOf(race);
                int cls = ProfileWire.ClassIndex(config, side, "assault");
                var plain = LoadoutEdits.Create(CustomizationProfile.Default(catalog), 1, "assault", catalog).Profile;
                // The creator refuses the pistol Bolter without True Grit ...
                var refused = LoadoutEdits.SetItem(plain, 1, "pistol", pistol, catalog);
                Assert.False(refused.Changed);
                // ... takes it in either perk slot ...
                foreach (int slot in new[] { 0, 1 })
                {
                    var withPerk = LoadoutEdits.SetPerk(plain, 1, slot, Loadouts.TrueGritPerk, catalog).Profile;
                    var chosen = LoadoutEdits.SetItem(withPerk, 1, "pistol", pistol, catalog);
                    Assert.True(chosen.Changed, $"{race}: {pistol} with True Grit in slot {slot + 1}");
                    var resolved = Loadouts.Resolve(config, cls, ProfileWire.ToLoadout(chosen.Profile.ClassAt(1)!, config, side));
                    Assert.True(resolved.Pistol >= 0, "the server dropped the pistol Bolter that has True Grit");
                    Assert.True(Loadouts.IsPistolBolterName(LoadoutIndices.Names(config, cls, "pistol")[resolved.Pistol]));
                    // ... and removing the perk puts the pistol back (the creator resets it).
                    var removed = LoadoutEdits.SetPerk(chosen.Profile, 1, slot, null, catalog).Profile.ClassAt(1)!;
                    Assert.Null(removed.Pistol);
                }
                // The server drops a pistol Bolter that arrives without the perk, whatever a client sends.
                var forced = new LoadoutChoice(cls, -1, LoadoutIndices.Choice(config, cls, "pistol", pistol), -1, -1, -1, -1);
                Assert.Equal(-1, Loadouts.Resolve(config, cls, forced).Pistol);
            }
    }

    [InstallFact]
    public void The_perks_say_what_the_simulation_does_not_blanket_not_active_yet()
    {
        var (catalog, config) = Loaded.Value;
        var status = PerkStatuses.Of(config);
        Assert.Equal(catalog.Perks.All.Count, status.Count);
        int inert = status.Values.Count(s => s.Activity == PerkActivity.Inert), partial = status.Values.Count(s => s.Activity == PerkActivity.Partial);
        Assert.True(status.Count - inert >= 43, $"{status.Count - inert} of {status.Count} perks act");   // M43: 34 implemented; perks/inert-2: 43 (the stims made the last two partial ones whole)
        Assert.True(partial >= 0 && inert >= 0 && inert < status.Count / 2, $"{inert} inert, {partial} partial");
        foreach (var options in catalog.Perks.Classes)
        {
            var rows = LoadoutRows.Perks(catalog, options.Role, options.Race, null, UnlockPolicy.All, status);
            Assert.Equal(options.Perks.Count, rows.Count);
            Assert.All(rows, r => Assert.Equal(status[r.Name], r.Status));
            Assert.Contains(rows, r => r.Active);
        }
    }
}
