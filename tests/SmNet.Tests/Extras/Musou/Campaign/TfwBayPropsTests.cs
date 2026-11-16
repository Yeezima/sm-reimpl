using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The troop bay's wall assets replace the floor pads (owner, 2026-11-14): each station keeps its zone and Use key, and its model stands on the wall in front of that zone.</summary>
public class TfwBayPropsTests
{
    static readonly TfwHub Hub = TfwTestArena.Hub;

    [Fact]
    public void Every_station_that_was_a_pad_has_a_wall_prop()
    {
        foreach (var station in new[] { TfwStation.Equipment, TfwStation.Range, TfwStation.Destination })
            Assert.Contains(TfwBayProps.All, p => p.Station == station);
        Assert.DoesNotContain(TfwBayProps.All, p => p.Station is TfwStation.None or TfwStation.Return);   // the range's return pad is not part of the troop bay
    }

    [Fact]
    public void The_mapping_is_armory_for_equipment_airlock_for_the_range_and_drop_deployment_for_the_battle()
    {
        Assert.Equal(["Armory.glb", "Mag-Lock-Weapon-Rack.glb"], TfwBayProps.All.Where(p => p.Station == TfwStation.Equipment).Select(p => p.File));
        Assert.Equal(["Firing-Range-Airlock.glb"], TfwBayProps.All.Where(p => p.Station == TfwStation.Range).Select(p => p.File));
        Assert.Equal(["Drop-Deployment.glb"], TfwBayProps.All.Where(p => p.Station == TfwStation.Destination).Select(p => p.File));
    }

    [Fact]
    public void Each_prop_stands_in_front_of_its_zone_so_standing_in_the_zone_is_standing_before_it()
    {
        foreach (var p in TfwBayProps.All)
            Assert.True(TfwBayProps.ZoneGap(p) <= TfwBayProps.MaxZoneGap, $"{p.Name}: {TfwBayProps.ZoneGap(p):F2} m from its zone");
    }

    [Fact]
    public void The_zone_points_still_match_the_hub_so_the_trigger_is_unmoved()
    {
        foreach (var station in new[] { TfwStation.Equipment, TfwStation.Range, TfwStation.Destination })
        {
            var at = Hub.OnFloor(TfwBayProps.StationModel(station));
            var expected = station switch { TfwStation.Equipment => Hub.EquipmentStation, TfwStation.Range => Hub.RangeStation, _ => Hub.DestinationStation };
            Assert.Equal(expected, at);
        }
    }

    [Fact]
    public void A_player_standing_at_the_prop_reaches_the_zone_and_gets_the_prompt()
    {
        // The nearest floor point to each prop inside the zone is a place the Use key works: the station is reported there.
        var range = new TfwRange(Hub.Centre + new Vector3(80f, 0f, 0f));
        foreach (var p in TfwBayProps.All.Where(p => p.Label.Length > 0 || p.Station == TfwStation.Equipment))
        {
            var s = TfwBayProps.StationModel(p.Station);
            var toWall = new Vector2(TfwBayProps.WallX(p) - s.X, p.Y - s.Y);
            var inside = s + Vector2.Normalize(toWall) * MathF.Min(toWall.Length(), TfwMap.StationRadius - 0.05f);
            Assert.Equal(p.Station, TfwHudModel.StationAt(Hub.OnFloor(inside), Hub, range));
        }
    }

    [Fact]
    public void The_prompt_words_name_the_station()
    {
        Assert.Equal("E: EQUIPMENT", TfwHudModel.StationPrompt(TfwStation.Equipment, "E"));
        Assert.Equal("E: FIRING RANGE", TfwHudModel.StationPrompt(TfwStation.Range, "E"));
        Assert.Equal("E: DESTINATION", TfwHudModel.StationPrompt(TfwStation.Destination, "E"));
    }

    [Fact]
    public void The_props_stand_on_the_tech_panels_between_their_ends_and_above_the_floor()
    {
        foreach (var p in TfwBayProps.All)
        {
            Assert.InRange(p.Y, TfwShip.PanelFrom + 1f, TfwShip.PanelTo - 1f);
            Assert.InRange(p.AboveFloor, 0.5f, TfwShip.PanelTop - TfwShip.FloorZ - 0.5f);
            Assert.True(TfwBayProps.WallX(p) == TfwShip.LeftPanel || TfwBayProps.WallX(p) == TfwShip.RightPanel);
        }
    }

    [Fact]
    public void The_kit_files_are_committed_and_the_release_script_expects_exactly_these()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "SmReimpl.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var models = Path.Combine(root!, "client", "assets", "owner-kit", "thunderhawk-classic", "models");
        foreach (var file in TfwBayProps.Files) Assert.True(File.Exists(Path.Combine(models, file)), file);
        Assert.Equal(TfwBayProps.Files.Count(), Directory.GetFiles(models, "*.glb").Length);
        Assert.True(File.Exists(Path.Combine(root!, "client", "assets", "owner-kit", ".gdignore")));
        Assert.Contains($"Count -ne {TfwBayProps.Files.Count()})", File.ReadAllText(Path.Combine(root!, "tools", "release", "release.ps1")));
    }
}
