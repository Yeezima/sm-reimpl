using System.Text;
using SmContent;
using SmFormats.Baf;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// M15 reading: the teams table of the global file, sides from class races and team blueprints, the rule names checked in the
/// objectives script, and class sides and roles in the built loadouts. Synthetic data only.
/// </summary>
public class GameModesTests
{
    [Fact]
    public void Teams_are_read_from_the_global_file_with_their_sides()
    {
        var global = BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[] { ("multiplayer_settings", new (string, object)[] {
            ("teams", new (string, object)[]
            {
                ("space marine team", new (string, object)[] { ("ebp", "ebps\\multiplayer\\race_marine\\sm_space_marine_player") }),
                ("chaos team", new (string, object)[] { ("ebp", "ebps\\multiplayer\\race_chaos\\csm_chaos_marine_player") }),
                ("odd team", new (string, object)[] { ("ebp", "ebps\\multiplayer\\race_ork\\ork_player") }),
            }) }) })));
        var teams = GameModes.ReadTeams(global);
        Assert.Equal([("space marine team", Team.SpaceMarines), ("chaos team", Team.Chaos), ("odd team", Team.None)], teams.Select(t => (t.Key, t.Side)));
        Assert.Equal("ebps\\multiplayer\\race_chaos\\csm_chaos_marine_player", teams[1].Blueprint);
        Assert.Throws<InvalidDataException>(() => GameModes.ReadTeams(BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[] { ("x", 1f) })))));
    }

    [Fact]
    public void Sides_come_from_races_and_blueprint_folders()
    {
        Assert.Equal((Team.SpaceMarines, Team.Chaos, Team.None, Team.None), (GameModes.SideOfRace("space_marine"), GameModes.SideOfRace("CHAOS"), GameModes.SideOfRace("ork"), GameModes.SideOfRace("")));
        Assert.Equal(Team.SpaceMarines, GameModes.SideOfBlueprint("ebps/multiplayer/race_marine/x"));
        Assert.Equal(Team.None, GameModes.SideOfBlueprint("ebps\\multiplayer\\not_race_marine\\x"));   // a whole folder name only
    }

    [Fact]
    public void The_rule_names_are_found_in_script_bytes()
    {
        var script = Encoding.ASCII.GetBytes("\x1bLuaQ ... TeamDeathmatchMarine\0 Deaths\0 isDeathMatch\0 tdm_mkr_spawn_01\0");
        Assert.Equal(["TeamDeathmatchMarine", "Deaths", "isDeathMatch", "tdm_mkr_spawn_01"], GameModes.FindMarkers(script));
        Assert.Empty(GameModes.FindMarkers([]));
        Assert.Equal(8, GameModes.ScriptMarkers.Count);
    }

    [Fact]
    public void Built_loadouts_carry_the_class_side_and_role()
    {
        var classes = new[]
        {
            new MpClass("sm - tactical", "tactical", "space_marine", [], [], [], null),
            new MpClass("csm - havoc", "heavy", "chaos", [], [], [], null),
            new MpClass("ork - boy", "assault", "ork", [], [], [], null),
        };
        var notes = new List<string>();
        var built = MpClasses.Build(classes, new Dictionary<string, BafDocument>(), [], _ => 1f, notes);
        Assert.Equal([(Team.SpaceMarines, "tactical"), (Team.Chaos, "heavy"), (Team.None, "assault")], built.Loadouts.Select(l => (l.Side, l.Role)));
        Assert.Contains(notes, n => n.Contains("ork - boy") && n.Contains("neither side"));
    }
}
