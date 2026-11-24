using SmContent;
using SmContent.Customization;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Arsenal;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Arsenal;

/// <summary>
/// remove-spear-claw (owner 2026-11-23: "turn off the power spear and power claw - remove them from the game for now"): the lightning claw and the power spear are in no list, catalogue, bot or
/// retainer loadout by default; saved picks of either load as the class default; <c>--old-arsenal-spear-claw</c> (<see cref="ArsenalRules.SpearClawSwitch"/>) brings both back to the
/// configuration d090566a built; every other weapon and Classic are unchanged. Synthetic data only; the install is read where a test says so, never the owner's save or settings (the profile
/// test writes its own file under a private temporary folder).
/// </summary>
public class SpearClawRemovalTests(ITestOutputHelper output) : IDisposable
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);
    static readonly string[] Removed = [ArsenalRules.LightningClaw, ArsenalRules.PowerSpear];

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-reimpl-tests", "spear-claw-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { } }

    static SimConfig Load(string profile, bool spearClaw)
    {
        Assert.True(ContentProfiles.TryParse(profile, out var flags, out _));
        using var scope = ArsenalRules.SpearClawScoped(spearClaw);
        return GameplayTuning.LoadForMatch(Game.Value!, flags).Config;
    }

    static IEnumerable<string> Melees(SimConfig c) => c.Classes.SelectMany(k => k.MeleeChoices.Select(m => m.Wargear));

    [Fact]
    public void The_switch_is_off_by_default_and_reads_its_scope()
    {
        Assert.Equal("--old-arsenal-spear-claw", ArsenalRules.SpearClawSwitch);
        Assert.Equal("SM_OLD_ARSENAL_SPEAR_CLAW", ArsenalRules.SpearClawEnvironmentVariable);
        Assert.Null(Environment.GetEnvironmentVariable(ArsenalRules.SpearClawEnvironmentVariable));   // a test run never sets it (it would restore both everywhere)
        Assert.False(ArsenalRules.SpearClawOffered);
        using (ArsenalRules.SpearClawScoped(true)) Assert.True(ArsenalRules.SpearClawOffered);
        Assert.False(ArsenalRules.SpearClawOffered);
        Assert.True(ArsenalRules.IsSpearOrClaw("SM_MP_MELEE_POWER_SPEAR"));
        Assert.True(ArsenalRules.IsSpearOrClaw(ArsenalRules.LightningClaw));
        Assert.False(ArsenalRules.IsSpearOrClaw(ArsenalRules.Autocannon));
        Assert.False(ArsenalRules.IsSpearOrClaw(ArsenalRules.RemoteDetonators));
        Assert.False(ArsenalRules.IsSpearOrClaw(null));
    }

    [Fact]
    public void The_server_takes_the_restore_switch()
    {
        Assert.True(ServerCommandLine.TryParse(["--old-arsenal-spear-claw", "--bind", "127.0.0.1"], out var on, out var error), error);
        Assert.True(on.OldArsenalSpearClaw);
        Assert.False(on.Help);
        Assert.True(ServerCommandLine.TryParse(["--bind", "127.0.0.1"], out var off, out _));
        Assert.False(off.OldArsenalSpearClaw);
        Assert.Contains("--old-arsenal-spear-claw", ServerCommandLine.Usage);
    }

    [ContentProfileTests.InstallFact]
    public void Musou_and_Warfront_configs_carry_neither_and_the_switch_restores_the_rest_exactly()
    {
        // Musou hashes are not pinned (they move with every Musou change). On this branch the restored hashes equalled d090566a's (0xBFCB0B7F87F6F3D6, 0x36AB4C416735B459), captured there with
        // the same LoadForMatch call; what stays checked is structural: the restored config less the two hashes as the default one.
        foreach (var profile in new[] { ContentProfiles.Musou, ContentProfiles.Warfront })
        {
            var off = Load(profile, false);
            var on = Load(profile, true);
            output.WriteLine($"{profile}: removed 0x{off.Hash():X16}UL, restored 0x{on.Hash():X16}UL");
            Assert.NotEqual(on.Hash(), off.Hash());
            foreach (var name in Removed)
            {
                Assert.DoesNotContain(name, Melees(off), StringComparer.OrdinalIgnoreCase);
                Assert.Contains(name, Melees(on), StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(off.Weapons, w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            // Every other weapon is unchanged: the same weapons, thrown items, perks and classes; each class's melee list is the restored one without the two, in the same order.
            // (Compared by the config hash, which writes every value: the records hold lists, which compare by reference.)
            Assert.Equal(on.Classes.Count, off.Classes.Count);
            var stripped = on with
            {
                Classes = [.. on.Classes.Select(c => c with { MeleeChoices = [.. c.MeleeChoices.Where(m => !ArsenalRules.IsSpearOrClaw(m.Wargear))] })],
            };
            for (int i = 0; i < on.Classes.Count; i++)
                Assert.Equal(stripped.Classes[i].MeleeChoices.Select(m => m.Wargear), off.Classes[i].MeleeChoices.Select(m => m.Wargear));
            Assert.Equal(stripped.Hash(), off.Hash());   // weapons, thrown items, perks, bodies, every class and every other melee combo as restored
            // The two were the last entries of the Space Marine Assault's list, so every other index is where it was.
            var assault = on.Classes.First(c => c.Role == "assault" && c.Side == Team.SpaceMarines);
            Assert.Equal(Removed.OrderBy(n => n), assault.MeleeChoices.TakeLast(2).Select(m => m.Wargear).OrderBy(n => n));
        }
    }

    [ContentProfileTests.InstallFact]
    public void Classic_is_unchanged_by_the_switch_and_never_offered_either()
    {
        foreach (var profile in new[] { ContentProfiles.Faithful, ContentProfiles.Extended })
        {
            var off = Load(profile, false);
            var on = Load(profile, true);
            Assert.Equal(on.Hash(), off.Hash());
            foreach (var name in Removed) Assert.DoesNotContain(name, Melees(off), StringComparer.OrdinalIgnoreCase);
        }
        var catalog = CustomizationCatalog.Load(Game.Value!);
        foreach (var spearClaw in new[] { false, true })
        {
            using var scope = ArsenalRules.SpearClawScoped(spearClaw);
            Assert.True(ContentProfiles.TryParse(ContentProfiles.Extended, out var extended, out _));
            foreach (var c in new[] { catalog.For(ContentExtras.None), catalog.For(extended) })
                foreach (var name in Removed) Assert.DoesNotContain(c.Perks.Classes.SelectMany(k => k.Melee.Items), i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [ContentProfileTests.InstallFact]
    public void Musous_catalogue_has_no_row_for_either_unless_restored_and_every_other_row_stays()
    {
        var catalog = CustomizationCatalog.Load(Game.Value!);
        IReadOnlyList<(string Role, string Race, string Slot, string Name)> Rows(bool spearClaw)
        {
            using var scope = ArsenalRules.SpearClawScoped(spearClaw);
            var c = catalog.For(ContentExtras.Musou);
            return [.. c.Perks.Classes.SelectMany(k => new[] { "primary", "pistol", "melee", "equipment" }.SelectMany(s => k.For(s).Items.Select(i => (k.Role, k.Race, s, i.Name))))];
        }
        var off = Rows(false);
        var on = Rows(true);
        foreach (var name in Removed)
        {
            Assert.DoesNotContain(off, r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(on, r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        Assert.Equal(on.Where(r => !ArsenalRules.IsSpearOrClaw(r.Name)), off);   // the autocannon, the Remote Detonators and every data item kept, in order
        Assert.Contains(off, r => r.Name == ArsenalRules.Autocannon);
        Assert.Contains(off, r => r.Name == ArsenalRules.RemoteDetonators);
    }

    [ContentProfileTests.InstallFact]
    public void No_bot_preset_or_squad_roll_gives_either()
    {
        foreach (var profile in new[] { ContentProfiles.Musou, ContentProfiles.Warfront })
        {
            var c = Load(profile, false);
            for (int k = 0; k < c.Classes.Count; k++)
                foreach (var preset in BotLoadouts.For(c.Classes[k].Role))
                {
                    var pick = BotLoadouts.Resolve(c, k, preset);
                    if (pick.Melee >= 0) Assert.False(ArsenalRules.IsSpearOrClaw(c.Classes[k].MeleeChoices[pick.Melee].Wargear));
                }
        }
    }

    [ContentProfileTests.InstallFact]
    public void A_saved_pick_of_either_resolves_to_the_class_default()
    {
        var off = Load(ContentProfiles.Warfront, false);
        var on = Load(ContentProfiles.Warfront, true);
        int assault = off.Classes.ToList().FindIndex(c => c.Role == "assault" && c.Side == Team.SpaceMarines);
        foreach (var name in Removed)
        {
            // A custom class or Hello by name: unknown to the class, so the wire's -1, the class default.
            Assert.Equal(-1, LoadoutIndices.Choice(off, assault, "melee", LoadoutIndices.Neutral(name)));
            Assert.True(LoadoutIndices.Choice(on, assault, "melee", LoadoutIndices.Neutral(name)) >= 0);
            // A choice by index (a Hello, a Warfront class book): the restored list's position is past the end now, so the class default.
            int index = on.Classes[assault].MeleeChoices.ToList().FindIndex(m => m.Wargear == name);
            Assert.True(index >= off.Classes[assault].MeleeChoices.Count);
            var chosen = Loadouts.Choose(off, new PlayerState(0, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, 0f, true) { Class = assault }, -1, -1, index);
            var standard = Loadouts.Choose(off, new PlayerState(0, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, 0f, true) { Class = assault }, -1, -1, -1);
            Assert.Equal(standard.MeleeChoice, chosen.MeleeChoice);
            Assert.Equal(Loadouts.Melee(off, standard), Loadouts.Melee(off, chosen));
        }
    }

    [ContentProfileTests.InstallFact]
    public void A_Musou_loadout_slot_holding_either_loads_as_the_class_default()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ProfileStore.FileName), """
            { "schema": 1, "selected": { "kind": "default", "role": "tactical" }, "classes": [], "appearances": [],
              "sets": { "musou": { "selected": { "kind": "custom", "slot": 1 },
                "classes": [
                  { "slot": 1, "name": "Spear", "role": "assault", "melee": "mp_melee_power_spear", "pistol": "mp_pistol_bolt_pistol", "perks": [] },
                  { "slot": 2, "name": "Claw", "role": "assault", "melee": "mp_melee_lightning_claw", "pistol": "mp_pistol_bolt_pistol", "perks": [] },
                  { "slot": 3, "name": "Sword", "role": "assault", "melee": "mp_melee_powersword", "pistol": "mp_pistol_bolt_pistol", "perks": [] } ] } } }
            """);
        var catalog = CustomizationCatalog.Load(Game.Value!);
        LoadoutSet Read(bool spearClaw, out IReadOnlyList<ProfileRepair> repairs)
        {
            using var scope = ArsenalRules.SpearClawScoped(spearClaw);
            var result = new ProfileStore(_dir).Load(catalog, readsAddedSets: true);
            repairs = result.Repairs;
            return result.Profile.Set("musou")!;
        }
        var off = Read(false, out var offRepairs);
        Assert.Null(off.ClassAt(1)!.Melee);
        Assert.Null(off.ClassAt(2)!.Melee);
        Assert.Equal("mp_melee_powersword", off.ClassAt(3)!.Melee);
        Assert.Equal(["Spear", "Claw", "Sword"], off.Classes.Select(c => c.Name));
        Assert.Equal([off.ClassAt(1)!.Pistol, off.ClassAt(2)!.Pistol], [off.ClassAt(3)!.Pistol, off.ClassAt(3)!.Pistol]);
        Assert.Contains(offRepairs, r => r.Path == "sets.musou.classes[0].melee");
        Assert.Contains(offRepairs, r => r.Path == "sets.musou.classes[1].melee");
        var on = Read(true, out _);
        Assert.Equal("mp_melee_power_spear", on.ClassAt(1)!.Melee);
        Assert.Equal("mp_melee_lightning_claw", on.ClassAt(2)!.Melee);
    }
}
