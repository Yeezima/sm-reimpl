using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The Firing Range's place and its walls (2026-11-02): clear of the level and the Stormbird, closed on every side, and a button in the ship whose pad the other stations do not share.</summary>
public class TfwRangeTests
{
    static readonly TfwMapData Data = TfwTestArena.Build();

    [Fact]
    public void The_range_stands_east_of_the_level_clear_of_the_ship_and_is_in_the_hash()
    {
        var range = Assert.IsType<TfwRange>(Data.Field.Range);
        var hi = TfwTestArena.Level().Character.Max;   // the level alone: the composed world holds the range too
        Assert.True(range.Centre.X - TfwRange.Size / 2f > hi.X, "east of the level");
        Assert.False(range.Contains(Data.Field.Hub.Centre));
        Assert.False(Data.Field.Hub.Contains(range.Centre));
        Assert.True(range.Contains(range.Spawn.Position));
        Assert.True(range.Contains(range.ReturnStation));
        Assert.Equal(range, TfwRange.For(TfwTestArena.Level()));   // the server and every client find the same place
    }

    [Fact]
    public void The_floor_holds_a_player_and_the_walls_close_the_square()
    {
        var range = TfwRange.For(TfwTestArena.Level());
        var world = Data.Collision;
        var spawn = range.Spawn.Position + new Vector3(0f, 0f, 2f);
        Assert.NotNull(world.Character.Raycast(spawn, -Vector3.UnitZ, 5f));   // the floor
        foreach (var way in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY })
            Assert.NotNull(world.Character.Raycast(range.Centre + new Vector3(0f, 0f, 2f), way, 80f));   // a wall on every side, 50 m off
    }

    [Fact]
    public void The_range_button_is_a_station_apart_from_the_others_and_the_pad_back_is_at_the_range()
    {
        var hub = Data.Field.Hub;
        var range = Assert.IsType<TfwRange>(Data.Field.Range);
        Assert.Equal(SmNet.Extras.Musou.Campaign.TfwStation.Range, SmNet.Extras.Musou.Campaign.TfwHudModel.StationAt(hub.RangeStation, hub, range));
        Assert.Equal(SmNet.Extras.Musou.Campaign.TfwStation.Destination, SmNet.Extras.Musou.Campaign.TfwHudModel.StationAt(hub.DestinationStation, hub, range));
        Assert.Equal(SmNet.Extras.Musou.Campaign.TfwStation.Equipment, SmNet.Extras.Musou.Campaign.TfwHudModel.StationAt(hub.EquipmentStation, hub, range));
        Assert.Equal(SmNet.Extras.Musou.Campaign.TfwStation.Return, SmNet.Extras.Musou.Campaign.TfwHudModel.StationAt(range.ReturnStation, hub, range));
        Assert.True(hub.Contains(hub.RangeStation));
    }
}
