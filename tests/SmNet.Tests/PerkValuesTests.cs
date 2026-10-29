using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// perks/finish: the values line under each perk's description in the class creator (<see cref="PerkValues"/>). It is generated from the same specs
/// the simulation picks for a player with the perk, so the tests compare it with what <see cref="Loadouts"/> gives such a player; values the simulation
/// does not use, or cannot determine, are never shown; inert perks say "Not active yet.". Synthetic specs (made-up numbers), plus the player's install
/// when present (read-only; skipped without it, never in CI).
/// </summary>
public class PerkValuesTests(ITestOutputHelper output)
{
    const string Mag = "u_mag", Spread = "u_spread", Heal = "u_heal", Charge = "u_charge", Imp = "u_imp", Larr = "u_larr", Halo = "u_halo", Unused = "u_unused";

    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static WeaponSpec Gun(string name, WeaponSlot slot, int clip) =>
        new(name, slot, TriggerKind.FullAuto, 20f, 0.15f, clip, 80, 1.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f) { HitType = DamageType.Ranged };

    static SpreadSpec Sets(float hip, float aimed, float perShot) =>
        new(new SpreadStances([Set(hip, perShot), Set(hip, perShot), Set(hip, perShot), Set(aimed, perShot), Set(aimed, perShot), Set(aimed, perShot)]), []);
    static SpreadSet Set(float initial, float perShot) => new("s", new ScatterSet(initial, initial, perShot, 1.5f, 1.5f, 2.4f, 0.1f), HandlingSet.None);

    // Both races' twins carry the same perk variant (as the install after race parity): one line.
    static readonly WeaponSpec PistolSm = Gun("sm_mp_pistol_bolt_pistol", WeaponSlot.Pistol, 10) is var p ? p with { Variants = [new([Mag], p with { ClipSize = 16 })] } : null!;
    static readonly WeaponSpec PistolCsm = PistolSm with { Name = "csm_mp_pistol_bolt_pistol", Variants = [new([Mag], PistolSm with { Name = "csm_mp_pistol_bolt_pistol", ClipSize = 16, Variants = [] })] };
    static readonly WeaponSpec BolterBase = Gun("sm_mp_ranged_bolter", WeaponSlot.Ranged, 30) with { Spread = Sets(0.25f, 0.075f, 0.35f) };
    static readonly WeaponSpec Bolter = BolterBase with
    {
        Variants =
        [
            new([Spread], BolterBase with { Spread = Sets(0.125f, 0.03f, 0.21f) }),
            // A field the simulation does not use (the charge's max_charge_time): nothing may be shown for it.
            new([Unused], BolterBase with { Charge = new ChargeSpec("a", [], 0.8f, 1f, true, 1) }),
        ],
    };
    static readonly ChargeSpec PlasmaCharge = new("ability_ranged_secondary", [new ChargeStage("1.none", 0f, 0f, 1f, null),
        new ChargeStage("2.heavy", 1.1f, 5f, 1f, new ChargeShot(DeliveryKind.Projectile, null, 100f, 1f, 25, 30f, 0f, null))], 0f, 1f, true, 25);
    static readonly WeaponSpec PlasmaBase = Gun("sm_mp_ranged_plasma_gun", WeaponSlot.Ranged, 125) with { Charge = PlasmaCharge };
    static readonly WeaponSpec Plasma = PlasmaBase with { Variants = [new([Charge], PlasmaBase with { Charge = PlasmaCharge with { MinTimeMultiplier = 0.65f } })] };

    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f);
    static MeleeCombo Combo(string name) => new(name, [Light]) { Variants = [new([Heal], new MeleeCombo(name, [Light with { Heal = 30f }]))] };

    static readonly PerkSpec[] PerkList =
    [
        new("mp_perk_weap_bolt_pistol_larger_mag", Mag, PerkKind.Weapon, 1),
        new("mp_perk_weap_bolter_accuracy", Spread, PerkKind.Weapon, 1),
        new("mp_perk_weap_chainsword_heal_on_hit", Heal, PerkKind.Weapon, 1),
        new("mp_perk_weap_plasma_gun_less_heat", Charge, PerkKind.Weapon, 1),
        new("mp_perk_ranged_damage_resistance", Imp, PerkKind.Class, 1),
        new("mp_perk_shield_recharge", Larr, PerkKind.Class, 1),
        new("mp_perk_iron_halo", Halo, PerkKind.Class, 1),
        new("mp_perk_unused", Unused, PerkKind.Class, 1),
        new("mp_perk_respawn_timer", "mp_perk_respawn_timer", PerkKind.Class, 1),   // nothing reads its string: inert
    ];

    static readonly SimConfig Config = new()
    {
        Weapons = [Bolter, PistolSm, PistolCsm, Plasma],
        Perks = PerkList,
        PlayerUpgrades = [new(Imp) { ReceivedRanged = 0.7f }, new(Larr) { HealthRegenRate = 45f, HealthRegenDelay = -1.5f }, new(Halo) { Shield = new ShieldInfo(150f, 7f, 33f) }],
        Classes =
        [
            new ClassLoadout("sm - tactical", 0, 1, [Light]) { PrimaryChoices = [0, 3], PistolChoices = [1], MeleeChoices = [Combo("sm_mp_melee_chainsword")], Side = Team.SpaceMarines,
                PerkChoices = Enumerable.Range(0, PerkList.Length).ToArray() },
            new ClassLoadout("csm - tactical", 0, 2, [Light]) { PrimaryChoices = [0], PistolChoices = [2], MeleeChoices = [Combo("csm_mp_melee_chain_axe")], Side = Team.Chaos,
                PerkChoices = Enumerable.Range(0, PerkList.Length).ToArray() },
        ],
    };

    static PerkSpec Perk(string upgrade) => PerkList.Single(p => p.UpgradeString == upgrade);

    /// <summary>A tactical player holding <paramref name="upgrade"/>'s perk in slot 1.</summary>
    static PlayerState With(string upgrade) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0, Perk1 = Array.IndexOf(PerkList, Perk(upgrade)) }, Config);

    [Fact]
    public void A_clip_perk_shows_the_clip_the_simulation_gives_and_the_twins_once()
    {
        var lines = PerkValues.Lines(Config, Perk(Mag));
        output.WriteLine(string.Join("\n", lines));
        Assert.Equal(["Bolt Pistol: clip 10 → 16 (+60%)"], lines);
        // Never drifts: the number is the one the simulation picks for a player with the perk.
        Assert.Equal(16, Loadouts.Weapon(Config, With(Mag), WeaponSlot.Pistol)!.ClipSize);
    }

    [Fact]
    public void A_spread_perk_shows_the_reductions_in_percent()
    {
        string text = PerkValues.Text(Config, Perk(Spread));
        output.WriteLine(text);
        Assert.Equal("Bolter: hip spread 0.25° → 0.125° (−50%), hip spread growth per shot 0.35° → 0.21° (−40%), aimed spread 0.075° → 0.03° (−60%), "
            + "aimed spread growth per shot 0.35° → 0.21° (−40%)", text);
        var spread = Loadouts.Weapon(Config, With(Spread), WeaponSlot.Ranged)!.Spread!.Basic.SetFor(SpreadStance.Standing, SpreadMovement.Idle)!;
        Assert.Equal(0.125f, spread.Scatter.InitialHorizontal);
    }

    [Fact]
    public void Heal_on_hit_shows_the_heal_and_twins_with_equal_changes_share_a_line()
    {
        Assert.Equal(["Chainsword, Power Axe / Chain Axe: heals 30 health per hit while hurt"], PerkValues.Lines(Config, Perk(Heal)));
        Assert.Equal(30f, Loadouts.Melee(Config, With(Heal))[0].Heal);
    }

    [Fact]
    public void A_charge_perk_shows_the_charged_shot_time()
    {
        Assert.Equal("Plasma Gun: charged shot ready after 1.1 s → 0.715 s (−35%)", PerkValues.Text(Config, Perk(Charge)));
    }

    [Fact]
    public void Player_blueprint_perks_compare_with_the_bodys_own_values()
    {
        Assert.Equal("Ranged damage taken −30% (x0.7)", PerkValues.Text(Config, Perk(Imp)));
        Assert.Equal("Health regeneration 5/s → 50/s (+900%), health regeneration starts after 5 s → 3.5 s (−30%)", PerkValues.Text(Config, Perk(Larr)));
        Assert.Equal("Armour 100 → 150 (+50%)", PerkValues.Text(Config, Perk(Halo)));
        var body = Loadouts.Body(Config, With(Larr));
        Assert.Equal((50f, 3.5f), (body.HealthRegenRate, body.HealthRegenDelay));
        Assert.Equal(150f, Loadouts.Body(Config, With(Halo)).ShieldMax);
    }

    [Fact]
    public void Values_the_simulation_does_not_use_are_never_shown()
    {
        Assert.Equal(PerkActivity.Active, Perks.StatusOf(Config, Perk(Unused)).Activity);   // a variant reads its string ...
        Assert.Empty(PerkValues.Lines(Config, Perk(Unused)));                              // ... but only an unused field differs: no line, no guess
        Assert.Equal("", PerkValues.Text(Config, Perk(Unused)));
    }

    [Fact]
    public void Inert_perks_say_not_active_yet()
    {
        var inert = PerkList.Single(p => p.Name == "mp_perk_respawn_timer");
        Assert.Equal(PerkActivity.Inert, Perks.StatusOf(Config, inert).Activity);
        Assert.Empty(PerkValues.Lines(Config, inert));
        Assert.Equal("Not active yet.", PerkValues.Text(Config, inert));
    }

    [Fact]
    public void The_description_keeps_the_locale_text_and_adds_the_values_under_it()
    {
        Assert.Equal("Carries more shots.\nBolt Pistol: clip 10 → 16 (+60%)", PerkValues.Describe("Carries more shots.", "Bolt Pistol: clip 10 → 16 (+60%)"));
        Assert.Equal("Carries more shots.", PerkValues.Describe("Carries more shots.", ""));
        Assert.Equal("Not active yet.", PerkValues.Describe("", "Not active yet."));
    }

    [Theory]
    [InlineData(10f, 16f, "+60%")]
    [InlineData(1f, 0.7f, "−30%")]
    [InlineData(0.133f, 0.155f, "+17%")]
    [InlineData(2f, 2f, "±0%")]
    public void Percentages_are_relative_to_the_base_and_rounded(float from, float to, string expected) => Assert.Equal(expected, PerkValues.Pct(from, to));

    [Fact]
    public void Numbers_keep_at_most_three_decimals() =>
        Assert.Equal(("0.715", "25.4", "1.433", "0", "800"), (PerkValues.N(0.715f), PerkValues.N(25.4f), PerkValues.N(1.4333334f), PerkValues.N(0f), PerkValues.N(800f)));

    [Fact]
    public void Item_names_are_neutral_for_both_races() =>
        Assert.Equal(("Power Axe / Chain Axe", "Power Axe / Chain Axe", "Bolter", "Bolter (pistol slot)", "Blind Grenades", "x_unknown"),
            (PerkValues.ItemName("sm_mp_melee_power_axe"), PerkValues.ItemName("csm_mp_melee_chain_axe"), PerkValues.ItemName("sm_mp_ranged_bolter_gold"),
             PerkValues.ItemName("csm_mp_pistol_bolter"), PerkValues.ItemName("sm_mp_eq_krak_grenade"), PerkValues.ItemName("x_unknown")));

    // ---- the install ----

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);
    static readonly Lazy<SimConfig?> Tuned = new(() => Game.Value is { } g ? GameplayTuning.LoadForMatch(g, ContentExtras.None).Config : null);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void On_the_install_every_active_perk_has_values_and_every_inert_one_says_so()
    {
        var c = Tuned.Value!;
        foreach (var perk in c.Perks)
        {
            var status = Perks.StatusOf(c, perk);
            string text = PerkValues.Text(c, perk);
            output.WriteLine($"{Perks.NameOf(perk)} [{status.Activity}]: {text.Replace("\n", " | ")}");
            if (status.Activity == PerkActivity.Inert) Assert.Equal(PerkValues.NotActive, text);
            else Assert.False(string.IsNullOrWhiteSpace(text), $"{perk.Name} is active but has no values line");
        }
        string Of(string name) => PerkValues.Text(c, c.Perks.Single(p => p.Name == name));
        Assert.Equal("Bolt Pistol: clip 10 → 16 (+60%)", Of("mp_perk_weap_bolt_pistol_larger_mag"));
        Assert.Equal("Armour 100 → 150 (+50%)", Of("mp_perk_iron_halo"));
        Assert.Contains("hip spread 0.25° → 0.125° (−50%)", Of("mp_perk_weap_bolter_accuracy"));
        Assert.Contains("Storm Bolter / Twin Bolter: reload 2 s → 1 s (−50%)", Of("mp_perk_weap_storm_bolter_reload"));
        Assert.Contains("heals 30 health per hit while hurt", Of("mp_perk_weap_chainsword_heal_on_hit"));
        Assert.Contains("charge damage 75 + 25 armour → 100 + 50 (+50%)", Of("mp_perk_improved_bullrush"));
        Assert.Equal(5, c.Perks.Count(p => Perks.StatusOf(c, p).Activity == PerkActivity.Inert));
    }
}
