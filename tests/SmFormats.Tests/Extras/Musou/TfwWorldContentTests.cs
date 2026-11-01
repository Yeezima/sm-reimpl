using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmFormats.Tests.Extras.Musou;

/// <summary>Musou Mode's world from a synthetic install (pass 2, M1): the map's points and start areas become the battlefield; a missing or unusable map gives the one clear message. No original bytes.</summary>
public class TfwWorldContentTests
{
    static byte[] Script() => SeizeGroundContentTests.Script("require", "GetDefaultVersusObjectives", "mkr_objective_1", "mkr_objective_2", "mkr_objective_3");

    [Fact]
    public void The_points_and_start_areas_of_the_install_make_the_battlefield()
    {
        string dir = SeizeGroundContentTests.Install(TfwMap.Battlefield, Script(), ("d_mission_objects", SeizeGroundContentTests.Layer(SeizeGroundContentTests.Markers)));
        try
        {
            var data = Assert.IsType<TfwMapData>(TfwWorld.Load(dir, out string? problem));
            Assert.Null(problem);
            Assert.Equal(new System.Numerics.Vector3(110f, -785f, 4f), data.Field.Points[0].Centre);
            Assert.Equal(13.5f, data.Field.Points[1].Radius);
            Assert.Equal(10f, data.Field.Points[2].HalfHeight);
            Assert.Equal(new System.Numerics.Vector3(245f, -937.38f, 5.5f), data.Field.MarineStart);
            Assert.All(data.Spawns, s => Assert.True(data.Field.Hub.Contains(s.Position)));
            Assert.Contains(data.StartAreas, a => a.Side == Team.SpaceMarines && a.Center == data.Field.Hub.Centre);
            Assert.Equal(TfwMap.WithHub(SmContent.MapContent.Load(dir, TfwMap.Battlefield).ToCollisionWorld()).Hash, data.Hash);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_install_without_the_map_or_its_points_is_told_so()
    {
        string other = SeizeGroundContentTests.Install("mp_other", Script(), ("d_mission_objects", SeizeGroundContentTests.Layer(SeizeGroundContentTests.Markers)));
        string bare = SeizeGroundContentTests.Install(TfwMap.Battlefield, null);
        try
        {
            Assert.Null(TfwWorld.Load(other, out string? missing));
            Assert.Contains("Manufactorum", missing);
            Assert.Contains("not in this install", missing);
            Assert.Null(TfwWorld.Load(bare, out string? unusable));
            Assert.Contains("capture points", unusable);
        }
        finally { Directory.Delete(other, recursive: true); Directory.Delete(bare, recursive: true); }
    }
}
