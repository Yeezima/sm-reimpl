using SmContent;
using SmContent.Customization;
using SmContent.Extras.Autocannon;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>
/// The autocannon pass's content side (the owner, 2026-11-11): Classic PvP's autocannon (<see cref="ContentExtras.Autocannon"/>, the extended profile only) appended to both heavy classes at one position
/// with the Stalker Bolter's shot, its presentation per mode (the same model, effects, reticle and box as Musou's, but its install twin's own sounds and a box that counts its rounds; Musou's box shows the
/// endless sign and keeps the Stalker's sounds), the profiles that have it (extended; never faithful or Musou), and the backpack the autocannon wears (the heavy bolter's) in every mode. Synthetic data only.
/// </summary>
public class ClassicAutocannonContentTests
{
    static WeaponSpec Gun(string name, WeaponSlot slot = WeaponSlot.Ranged) => new(name, slot, TriggerKind.FullAuto, 35f, 0.2f, 100, 0, 1f, 100f, 40f, 60f, 0.8f, 1f, 1f, 1f, 1f);

    static readonly WeaponSpec StalkerGun = Gun(Arsenal.StalkerBolterFile) with { Trigger = TriggerKind.SemiAuto, Damage = 50f, HeadMultiplier = 2f, ShieldHeadMultiplier = 2.5f, ShieldBodyMultiplier = 0.8f };

    static SimConfig Data(ContentExtras extras = ContentExtras.None) => new()
    {
        Weapons = [Gun("sm_mp_exotic_heavy_bolter"), Gun("sm_mp_exotic_las_cannon"), Gun(ClassicAutocannon.HeavyBolterFileChaos), Gun("csm_mp_exotic_las_cannon"), StalkerGun, Gun("sm_mp_ranged_bolter")],
        Classes =
        [
            new ClassLoadout("sm - devastator", 0, -1, []) { Side = Team.SpaceMarines, Role = "heavy", PrimaryChoices = [0, 1] },
            new ClassLoadout("csm - havoc", 2, -1, []) { Side = Team.Chaos, Role = "heavy", PrimaryChoices = [2, 3] },
            new ClassLoadout("sm - tactical", 5, -1, []) { Side = Team.SpaceMarines, Role = "tactical", PrimaryChoices = [5, 4] },
        ],
        Extras = extras,
    };

    static readonly WeaponSpec Source = Gun("csm_mp_exotic_auto_cannon") with { Damage = 50f, ShotCooldown = 0.4f, Trigger = TriggerKind.SemiAuto, BurstLength = 2, BurstInterval = 0.2f, ClipSize = 120 };

    // ---------------------------------------------------------------- the profiles and the config

    [Fact]
    public void Extended_has_the_Classic_autocannon_and_faithful_and_Musou_do_not()
    {
        Assert.True(ContentExtras.All.HasFlag(ContentExtras.Autocannon));
        Assert.True(ContentProfiles.TryParse(ContentProfiles.Extended, out var extended, out _));
        Assert.True(extended.HasFlag(ContentExtras.Autocannon));
        Assert.Equal(ContentProfiles.Extended, ContentProfiles.Name(extended));
        Assert.True(ContentProfiles.TryParse(ContentProfiles.Faithful, out var faithful, out _));
        Assert.Equal(ContentExtras.None, faithful);
        Assert.True(ContentProfiles.TryParse(ContentProfiles.Musou, out var musou, out _));
        Assert.False(musou.HasFlag(ContentExtras.Autocannon));
        Assert.True(ContentProfiles.TryParse("autocannon", out var alone, out _));
        Assert.Equal(ContentExtras.Autocannon, alone);
        Assert.Equal("autocannon", ContentProfiles.Name(alone));
        Assert.False(ContentProfiles.TryParse("musou,autocannon", out _, out _));   // Musou takes no other flag
        Assert.Equal(16u, (uint)ContentExtras.Autocannon);   // the next free bit
        Assert.Equal(2u, ContentExtras.WireVersion);           // unchanged: the flag itself is in the hash
    }

    [Fact]
    public void It_is_appended_to_both_heavy_classes_at_one_position_and_no_index_moves()
    {
        var notes = new List<string>();
        var classic = ClassicAutocannon.WithAutocannon(Data(ContentExtras.All), Source, notes);
        Assert.Equal(7, classic.Weapons.Count);
        Assert.Equal(ArsenalRules.Autocannon, classic.Weapons[6].Name);
        Assert.Equal([0, 1, 6], classic.Classes[0].PrimaryChoices);
        Assert.Equal([2, 3, 6], classic.Classes[1].PrimaryChoices);
        Assert.Equal([5, 4], classic.Classes[2].PrimaryChoices);   // the Tactical as he was (he lists the Stalker, not a heavy bolter)
        int devastator = LoadoutIndices.Class(classic, "sm - devastator"), havoc = LoadoutIndices.Class(classic, "csm - havoc");
        Assert.Equal(2, LoadoutIndices.Choice(classic, devastator, "primary", "mp_exotic_auto_cannon"));   // a custom class's side-neutral name
        Assert.Equal(2, LoadoutIndices.Choice(classic, havoc, "primary", "mp_exotic_auto_cannon"));        // the same position on the Chaos side
        Assert.Equal(0, LoadoutIndices.Choice(classic, devastator, "primary", "mp_exotic_heavy_bolter"));
        Assert.Contains(notes, n => n.StartsWith(ClassicAutocannon.Who));
        // Once in, a second append (the in-match picker's config) changes nothing.
        Assert.Same(classic, ClassicAutocannon.WithAutocannon(classic, Source, []));
        // The data's configuration (the faithful profile's) has no such name: a Join sends the default.
        Assert.Equal(-1, LoadoutIndices.Choice(Data(), LoadoutIndices.Class(Data(), "sm - devastator"), "primary", "mp_exotic_auto_cannon"));
    }

    [Fact]
    public void Its_round_is_the_Stalkers_with_the_datas_head_bonus_and_nothing_else_of_it()
    {
        var gun = ClassicAutocannon.WithAutocannon(Data(ContentExtras.All), Source, []).Weapons[^1];
        Assert.Equal((50f, 2f, 1f, 2.5f, 0.8f), (gun.Damage, gun.HeadMultiplier, gun.BodyMultiplier, gun.ShieldHeadMultiplier, gun.ShieldBodyMultiplier));   // the Stalker's
        Assert.Equal((TriggerKind.FullAuto, AutocannonRules.SecondsPerRound, 120, 0), (gun.Trigger, gun.ShotCooldown, gun.ClipSize, gun.ReserveSize));   // the autocannon's, with the multiplayer file's clip (owner 2026-11-11: "120 clip size")
        Assert.Equal(Source.ClipSize, gun.ClipSize);
        Assert.Equal(AutocannonRules.Clip, Arsenal.ClassicTwin(Source with { ClipSize = 0 }, Gun("sm_mp_exotic_heavy_bolter"), StalkerGun).ClipSize);   // a file without a clip keeps Musou's 400
        Assert.Equal((Source.MaxRange, Source.FallOffStart, Source.FallOffEnd, Source.FallOffEndMultiplier), (gun.MaxRange, gun.FallOffStart, gun.FallOffEnd, gun.FallOffEndMultiplier));
        Assert.Null(gun.Projectile);   // hit-scan: Classic's shot path sets off no blast
        Assert.Null(gun.Brace);
        // Musou's round is the blast's + 30; Classic's is not.
        Assert.Equal(AutocannonRules.DirectDamage, Arsenal.MusouTwin(Source, Gun("sm_mp_exotic_heavy_bolter")).Damage);
        Assert.NotEqual(AutocannonRules.DirectDamage, gun.Damage);
    }

    [Fact]
    public void Without_a_Stalker_or_a_heavy_bolter_nothing_is_appended_and_a_note_says_why()
    {
        var notes = new List<string>();
        var noStalker = Data(ContentExtras.All) with { Weapons = [.. Data().Weapons.Where(w => w.Name != Arsenal.StalkerBolterFile)] };
        Assert.Same(noStalker, ClassicAutocannon.WithAutocannon(noStalker, Source, notes));
        Assert.Contains(notes, n => n.Contains(Arsenal.StalkerBolterFile));
        var bare = new SimConfig { Extras = ContentExtras.All };
        Assert.Same(bare, ClassicAutocannon.WithAutocannon(bare, Source, notes));
        // Without an install the load appends nothing and says so.
        var none = new List<string>();
        Assert.Same(bare, ClassicAutocannon.Append(bare, "", none));
        Assert.Contains(none, n => n.StartsWith(ClassicAutocannon.Who) && n.Contains("autocannon"));
    }

    [Fact]
    public void Musous_append_stays_the_Space_Marine_Devastators_alone_with_Musous_numbers()
    {
        var musou = Arsenal.WithAutocannon(Data(ContentExtras.Musou), Source, []);
        Assert.Equal([0, 1, 6], musou.Classes[0].PrimaryChoices);
        Assert.Equal([2, 3], musou.Classes[1].PrimaryChoices);   // no Havoc in Musou
        Assert.Equal(AutocannonRules.DirectDamage, musou.Weapons[6].Damage);
        Assert.Equal((1f, 1f), (musou.Weapons[6].HeadMultiplier, musou.Weapons[6].ShieldHeadMultiplier));   // the file's, not the Stalker's
    }

    // ---------------------------------------------------------------- the presentation per mode

    [Fact]
    public void Classic_keeps_the_autocannons_own_sounds_and_Musou_the_Stalkers()
    {
        // The owner, 2026-11-11: "the sound of the autocannon in classic pvp mode should simply it's original sound - no stalker bolter sound change".
        Assert.Null(Arsenal.SoundSources(ContentExtras.All, ArsenalRules.Autocannon));
        Assert.Null(Arsenal.SoundSources(ContentExtras.Autocannon, ArsenalRules.Autocannon));
        Assert.Equal([Arsenal.StalkerBolterFile, Arsenal.StalkerBolterFileChaos], Arsenal.SoundSources(ContentExtras.Musou, ArsenalRules.Autocannon));
        // So Classic's sounds are its presentation source's, the install's own autocannon (the client's SoundSources falls back to it).
        Assert.Equal([Arsenal.AutocannonTwinFile, Arsenal.HeavyBolterFile], Arsenal.PresentationSources(ContentExtras.All, ArsenalRules.Autocannon));
        Assert.Equal(Arsenal.AutocannonTwinFile, Arsenal.PresentationSources(ContentExtras.All, ArsenalRules.Autocannon)![0]);
        Assert.False(StalkerHitConfirm.Is(Arsenal.AutocannonTwinFile));   // no Stalker hit confirm for it in Classic
    }

    [Fact]
    public void Its_look_is_Musous_in_Classic_and_nothing_shows_without_the_content()
    {
        foreach (var extras in new[] { ContentExtras.All, ContentExtras.Autocannon, ContentExtras.Musou })
        {
            Assert.True(ArsenalRules.Shown(extras, ArsenalRules.Autocannon));
            Assert.Equal(Arsenal.EffectNames(ArsenalRules.Autocannon), Arsenal.EffectNames(extras, ArsenalRules.Autocannon));   // the loyalist flash, tracer and impact
            Assert.Equal(Arsenal.Reticle(ArsenalRules.Autocannon), Arsenal.Reticle(extras, ArsenalRules.Autocannon));
        }
        foreach (var extras in new[] { ContentExtras.None, ContentExtras.Orks, ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks })
        {
            Assert.False(ArsenalRules.Shown(extras, ArsenalRules.Autocannon));
            Assert.Null(Arsenal.PresentationSources(extras, ArsenalRules.Autocannon));
            Assert.Null(Arsenal.EffectNames(extras, ArsenalRules.Autocannon));
            Assert.Null(Arsenal.Reticle(extras, ArsenalRules.Autocannon));
            Assert.Null(Arsenal.WeaponBox(extras, ArsenalRules.Autocannon, chaos: false));
        }
        // Classic carries the autocannon alone of the arsenal.
        foreach (var other in ArsenalRules.Names.Where(n => n != ArsenalRules.Autocannon))
        {
            Assert.False(ArsenalRules.Shown(ContentExtras.All, other));
            Assert.True(ArsenalRules.Shown(ContentExtras.Musou, other));
        }
        Assert.False(ArsenalRules.Shown(ContentExtras.All, Arsenal.AutocannonTwinFile));
    }

    [Fact]
    public void The_Havocs_autocannon_plays_its_Chaos_twins_effects_and_the_Devastators_the_loyalist_ones()
    {
        // The owner, 2026-11-11: "Give the chaos autocannon the chaos visual effects all the way through".
        foreach (var extras in new[] { ContentExtras.All, ContentExtras.Autocannon, ContentExtras.Musou })
        {
            Assert.Equal(Arsenal.AutocannonTwinFile, Arsenal.EffectsFor(extras, ArsenalRules.Autocannon, chaos: true));   // the install's own Chaos entry
            Assert.Equal(ArsenalRules.Autocannon, Arsenal.EffectsFor(extras, ArsenalRules.Autocannon, chaos: false));     // the loyalist renames of the autocannon's own
            Assert.Equal(Arsenal.AutocannonTwinFile, Arsenal.EffectsFor(extras, "SM_MP_EXOTIC_AUTO_CANNON", chaos: true));
        }
        // The twin's entry is the one the loyalist renames start from: Chaos names in, loyalist names out.
        var names = Arsenal.EffectNames(ArsenalRules.Autocannon)!;
        Assert.Equal(Arsenal.LoyalistMuzzleEffect, names[Arsenal.ChaosMuzzleEffect]);
        Assert.Equal(Arsenal.LoyalistTracerEffect, names[Arsenal.ChaosTracerEffect]);
        Assert.Equal(Arsenal.LoyalistImpactEffect, names[Arsenal.ChaosImpactEffect]);
        Assert.Equal([Arsenal.AutocannonTwinFile, Arsenal.HeavyBolterFile], Arsenal.PresentationSources(ContentExtras.All, ArsenalRules.Autocannon));   // the twin is read beside the classes' files
        // Every other weapon and every profile without the item: the name itself, whoever fires.
        foreach (var name in new[] { "sm_mp_exotic_heavy_bolter", ClassicAutocannon.HeavyBolterFileChaos, Arsenal.AutocannonTwinFile, "sm_mp_ranged_bolter", ArsenalRules.LightningClaw })
            Assert.Equal(name, Arsenal.EffectsFor(ContentExtras.All, name, chaos: true));
        foreach (var extras in new[] { ContentExtras.None, ContentExtras.Orks, ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks })
            Assert.Equal(ArsenalRules.Autocannon, Arsenal.EffectsFor(extras, ArsenalRules.Autocannon, chaos: true));
    }

    [Fact]
    public void The_drops_give_the_Classic_autocannon_the_heavy_bolters_ammunition()
    {
        var box = new PickupType("pickup_mp_drop_ammo", PickupKind.Ammo, 3, 4f, 25f, [40, 0, 40, 0, 12, 30], []);
        var classic = ClassicAutocannon.WithAutocannon(Data(ContentExtras.All) with { Pickups = new PickupSpec([box]) }, Source, []);
        Assert.Equal([40, 0, 40, 0, 12, 30, 40], classic.Pickups!.Types[0].WeaponAmounts);   // index 6: the Space Marine heavy bolter's 40
        Assert.Equal(6, classic.Classes[0].PrimaryChoices[^1]);   // the Devastator's autocannon
        Assert.Equal(40, classic.Pickups.Types[0].WeaponAmount(classic.Classes[1].PrimaryChoices[^1]));   // the Havoc's is the same weapon
    }

    [Fact]
    public void Musous_box_shows_the_endless_sign_and_Classics_the_rounds_left()
    {
        var musou = Arsenal.WeaponBox(ContentExtras.Musou, ArsenalRules.Autocannon, chaos: false)!;
        Assert.Same(Arsenal.EndlessAutocannonBox, musou);
        Assert.Contains("infinity_mc", musou.Show);
        Assert.Null(musou.Count);   // no count: the clip is never spent
        Assert.Equal(("ammo_autocannon_mc/ammo_bar", WeaponBoxBar.Heat), (musou.Bar, musou.Value));   // the heat still shows
        Assert.All(musou.Show, path => Assert.Contains(path, WeaponBoxRules.Optional));
        var classic = Arsenal.WeaponBox(ContentExtras.All, ArsenalRules.Autocannon, chaos: true)!;
        Assert.Same(Arsenal.WeaponBox(ArsenalRules.Autocannon, chaos: true), classic);
        Assert.DoesNotContain("infinity_mc", classic.Show);
        Assert.NotNull(classic.Count);
    }
}

public partial class CustomizationProfileTests
{
    [Fact]
    public void Classics_catalogue_lists_the_autocannon_for_both_heavy_classes_and_faithfuls_does_not()
    {
        var classic = Catalog.For(ContentExtras.All);
        Assert.Equal(ArsenalRules.Autocannon, classic.Perks.Class("sm - devastator")!.Primary.Items[^1].Name);
        Assert.Equal(ArsenalRules.Autocannon, classic.Perks.Class("csm - havoc")!.Primary.Items[^1].Name);
        Assert.Equal("Autocannon", classic.Perks.Class("csm - havoc")!.Primary.Items[^1].DisplayName);
        Assert.DoesNotContain(classic.Perks.Classes.Where(c => c.Role != "heavy"), c => c.Primary.Items.Any(i => i.Name == ArsenalRules.Autocannon));
        Assert.Same(Catalog, Catalog.For(ContentExtras.None));
        Assert.Same(Catalog, Catalog.For(ContentExtras.Orks));
        // Musou's own catalogue keeps its Space Marine row alone (its players are Space Marines).
        var musou = Catalog.For(ContentExtras.Musou);
        Assert.Contains(musou.Perks.Class("sm - devastator")!.Primary.Items, i => i.Name == ArsenalRules.Autocannon);
        Assert.DoesNotContain(musou.Perks.Class("csm - havoc")!.Primary.Items, i => i.Name == ArsenalRules.Autocannon);
    }

    [Fact]
    public void A_saved_autocannon_slot_survives_the_read_with_the_classic_items_and_is_repaired_away_without_them()
    {
        const string json = """
            { "schema": 1, "selected": { "kind": "custom", "slot": 1 },
              "classes": [ { "slot": 1, "role": "heavy", "name": "Cannon", "primary": "mp_exotic_auto_cannon" } ] }
            """;
        var kept = LoadWith(json, Catalog.For(ContentExtras.Autocannon));   // the customizer's read catalogue (CustomizerServices.Load)
        Assert.Equal("mp_exotic_auto_cannon", Assert.Single(kept.Profile.Classes).Primary);
        Assert.DoesNotContain(kept.Repairs, r => r.Path.Contains("primary") && !r.Informational);
        var data = LoadWith(json, Catalog);   // the data's alone: not offered, the class default
        Assert.Null(Assert.Single(data.Profile.Classes).Primary);
    }

    ProfileLoadResult LoadWith(string json, CustomizationCatalog catalog)
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(Path.Combine(_profileDir, "customization.json"), json);
        return NewStore().Load(catalog);
    }

    [Fact]
    public void The_autocannon_wears_the_heavy_bolters_backpack_on_the_Devastator_and_the_Havoc_keeps_his()
    {
        // The owner, 2026-11-11: "equipping the autocannon should also change the devastator/havoc's backpack to be the same one as the heavy bolter".
        foreach (var carried in new[] { ArsenalRules.Autocannon, "mp_exotic_auto_cannon", "SM_MP_EXOTIC_AUTO_CANNON" })   // the match's name, the customizer's side-neutral one
        {
            var look = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - devastator", Catalog, new ResolveOptions { Carried = [carried, "mp_perk_a"] });
            var withHeavy = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - devastator", Catalog, new ResolveOptions { Carried = ["sm_mp_exotic_heavy_bolter", "mp_perk_a"] });
            Assert.Equal(("sm_backpack_heavy_bolter", true), (look.Piece("backpack")!.Name, look.Piece("backpack")!.Forced));
            Assert.Equal(withHeavy.Piece("backpack"), look.Piece("backpack"));
            Assert.Equal(["pkg_hb_bp"], look.Piece("backpack")!.Packages);
        }
        // The Havoc's pack is the same whatever he carries (the data: one unconditional override).
        var havoc = AppearanceResolver.Resolve(AppearanceCode.Default, "csm - havoc", Catalog, new ResolveOptions { Carried = [ArsenalRules.Autocannon] });
        var havocHeavy = AppearanceResolver.Resolve(AppearanceCode.Default, "csm - havoc", Catalog, new ResolveOptions { Carried = [ClassicAutocannon.HeavyBolterFileChaos] });
        Assert.Equal("csm_backpack_havoc", havoc.Piece("backpack")!.Name);
        Assert.Equal(havocHeavy.Piece("backpack"), havoc.Piece("backpack"));
        // The alias is the autocannon's alone.
        Assert.Equal("sm_mp_exotic_heavy_bolter", AppearanceResolver.WornAs(ArsenalRules.Autocannon));
        foreach (var name in new[] { "sm_mp_exotic_las_cannon", "sm_mp_exotic_heavy_bolter", "csm_mp_exotic_heavy_bolter", "sm_mp_melee_lightning_claw", "mp_perk_a" })
            Assert.Equal(name, AppearanceResolver.WornAs(name));
        var las = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - devastator", Catalog, new ResolveOptions { Carried = ["sm_mp_exotic_las_cannon"] });
        Assert.NotEqual("sm_backpack_heavy_bolter", las.Piece("backpack")!.Name);   // the others as before
    }
}
