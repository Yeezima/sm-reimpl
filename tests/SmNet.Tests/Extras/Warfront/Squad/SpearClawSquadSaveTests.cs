using System.Text.Json;
using System.Text.Json.Serialization;
using SmContent;
using SmSim;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Squad;

/// <summary>
/// remove-spear-claw (owner 2026-11-23: "turn off the power spear and power claw - remove them from the game for now"), Warfront's saved squads: a synthetic <c>squads.json</c> (built here, never
/// the owner's save) whose retainers carry the lightning claw or the power spear loads with the class default melee and the rest of every record byte for byte as saved; a squad roll never gives
/// either. The removal itself: <c>SpearClawRemovalTests</c> (Extras/Musou/Arsenal).
/// </summary>
public class SpearClawSquadSaveTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    static SimConfig Load(bool spearClaw)
    {
        using var scope = ArsenalRules.SpearClawScoped(spearClaw);
        return GameplayTuning.LoadForMatch(Game.Value!, ContentProfiles.WarfrontProfile).Config;
    }

    [Fact]
    public void A_saved_Marine_of_a_class_the_config_lacks_is_kept_as_saved()
    {
        var saved = new SquadLoadout { ClassId = "sm - no such class", Melee = ArsenalRules.PowerSpear, Weapon = SquadWeapon.Blade };
        Assert.Same(saved, SquadLoadoutBuilder.WithOfferedMelee(new SimConfig(), saved));   // no throw, nothing changed (the review's F3)
    }

    [ContentProfileTests.InstallFact]
    public void No_squad_roll_gives_either()
    {
        var c = Load(false);
        foreach (var squad in Enum.GetValues<SquadClass>())
            for (int selection = 0; selection < 8; selection++)
                if (SquadLoadoutBuilder.Roll(c, squad, 0x5EEDUL, 7UL, 0, selection, default) is { } roll) Assert.False(ArsenalRules.IsSpearOrClaw(roll.Melee));
    }

    [ContentProfileTests.InstallFact]
    public void A_class_book_choice_by_the_old_index_mirrors_the_class_default()
    {
        var off = Load(false);
        var on = Load(true);
        int assault = off.Classes.ToList().FindIndex(c => c.Role == "assault" && c.Side == Team.SpaceMarines);
        var standard = SquadLoadoutBuilder.FromChoice(off, new LoadoutChoice(assault), default)!;
        foreach (var name in new[] { ArsenalRules.LightningClaw, ArsenalRules.PowerSpear })
        {
            int index = on.Classes[assault].MeleeChoices.ToList().FindIndex(m => m.Wargear == name);
            var mirror = SquadLoadoutBuilder.FromChoice(off, new LoadoutChoice(assault, -1, -1, index, -1, -1, -1), default)!;
            Assert.Equal(standard.Melee, mirror.Melee);
            Assert.False(ArsenalRules.IsSpearOrClaw(mirror.Melee));
            Assert.Equal(standard.Weapon, mirror.Weapon);
        }
    }

    static readonly JsonSerializerOptions SquadJson = new() { Converters = { new JsonStringEnumConverter() }, IncludeFields = true };   // GameServer.WarfrontWiring's squads.json options

    [ContentProfileTests.InstallFact]
    public void A_squads_json_holding_either_loads_with_the_default_melee_and_the_rest_byte_unchanged()
    {
        var off = Load(false);
        var on = Load(true);
        int assault = on.Classes.ToList().FindIndex(c => c.Role == "assault" && c.Side == Team.SpaceMarines);
        int tactical = on.Classes.ToList().FindIndex(c => c.Role == "tactical" && c.Side == Team.SpaceMarines);
        var look = AppearanceCode.Default;
        SquadLoadout Mirror(SimConfig c, int cls, int melee) => SquadLoadoutBuilder.FromChoice(c, new LoadoutChoice(cls, -1, -1, melee, -1, -1, -1), look)! with { Colours = [1u, 2u, 3u] };
        int Index(string name) => on.Classes[assault].MeleeChoices.ToList().FindIndex(m => m.Wargear == name);
        int sword = on.Classes[assault].MeleeChoices.ToList().FindIndex(m => m.Wargear == "sm_mp_melee_powersword");
        Assert.True(sword >= 0);

        // A synthetic squads.json as the restored build (d090566a) wrote it: a claw Marine, a spear Marine, a power sword Marine, a Tactical Marine, and a second owner untouched.
        var record = new ServiceRecord { Kills = 12, EliteKills = 3, Revives = 2, BattlesSurvived = 4 };
        var save = new WarfrontSquadSave(
            new SquadRosterSave(9, [
                new SquadOwnerSave(11UL, false, [
                    new SquadMarineSave("Brother Claw", Mirror(on, assault, Index(ArsenalRules.LightningClaw)), record),
                    new SquadMarineSave("Brother Spear", Mirror(on, assault, Index(ArsenalRules.PowerSpear)), record),
                    new SquadMarineSave("Brother Sword", Mirror(on, assault, sword), record)]),
                new SquadOwnerSave(22UL, true, [new SquadMarineSave("Brother Bolter", Mirror(on, tactical, -1), record)])]),
            new SquadClassSave([new SquadClassKeySave(11UL, SquadOwnerKind.Standard, false, SquadOwnerLoadout.Standard(new LoadoutChoice(assault, -1, -1, Index(ArsenalRules.PowerSpear), -1, -1, -1)))]),
            []);
        string json = JsonSerializer.Serialize(save, SquadJson);
        Assert.Contains(ArsenalRules.LightningClaw, json);
        Assert.Contains(ArsenalRules.PowerSpear, json);
        var read = JsonSerializer.Deserialize<WarfrontSquadSave>(json, SquadJson)!;

        var loaded = WarfrontSquadWiring.OfferedMelee(off, read.Rosters);   // what WarfrontSquadWiring.Import hands the rosters
        var marines = loaded.Owners.SelectMany(o => o.Marines).ToList();
        var defaultMelee = Mirror(off, assault, -1);
        foreach (var m in marines.Take(2))
        {
            Assert.Equal(defaultMelee.Melee, m.Loadout.Melee);   // the class default, as a new mirror of a default choice writes it
            Assert.False(ArsenalRules.IsSpearOrClaw(m.Loadout.Melee));
            Assert.Equal(defaultMelee.Weapon, m.Loadout.Weapon);
        }
        // The rest of every Marine, and the Marines without either, serialize byte for byte as they were saved.
        var savedMarines = read.Rosters.Owners.SelectMany(o => o.Marines).ToList();
        for (int i = 0; i < marines.Count; i++)
        {
            var was = savedMarines[i];
            var now = marines[i];
            bool converted = i < 2;
            var a = converted ? was with { Loadout = was.Loadout with { Melee = "", Weapon = new SquadWeapon() } } : was;
            var b = converted ? now with { Loadout = now.Loadout with { Melee = "", Weapon = new SquadWeapon() } } : now;
            Assert.Equal(JsonSerializer.Serialize(a, SquadJson), JsonSerializer.Serialize(b, SquadJson));
        }
        Assert.Equal(JsonSerializer.Serialize(read.Rosters with { Owners = [read.Rosters.Owners[1]] }, SquadJson), JsonSerializer.Serialize(loaded with { Owners = [loaded.Owners[1]] }, SquadJson));
        Assert.Equal(read.Rosters.NextName, loaded.NextName);

        // Under the restore switch the restored config still lists both: nothing is converted.
        var kept = WarfrontSquadWiring.OfferedMelee(on, read.Rosters);
        Assert.Equal(JsonSerializer.Serialize(read.Rosters, SquadJson), JsonSerializer.Serialize(kept, SquadJson));
        // A save without either is the same value.
        var plain = read.Rosters with { Owners = [read.Rosters.Owners[1]] };
        Assert.Same(plain, WarfrontSquadWiring.OfferedMelee(off, plain));
    }
}
