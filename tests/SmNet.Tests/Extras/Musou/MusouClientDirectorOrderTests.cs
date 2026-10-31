using System.Numerics;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU3b): the client keeps the newest director. Datagrams can arrive late or twice (GameClient.Envelope.cs), so a director older than
/// the one shown is ignored (a late wave-2 director after wave 3, or a stale boss one after the win, must not turn the run back); a copy of the same tick
/// changes nothing; a restarted server clock takes its first director whatever its tick; forgetting the enemies keeps the director.
/// </summary>
public class MusouClientDirectorOrderTests
{
    static readonly MusouBox Box = new(new Vector3(-100f, -100f, -10f), new Vector3(100f, 100f, 30f));

    static ExtrasSection DirectorAt(uint tick, byte wave, MusouPhase phase) =>
        new(tick, MusouWire.DirectorTag, MusouWire.WriteDirector(new MusouDirectorView(wave, 5, phase, 0, 80, 80, 0, 0f, 0f, 3, Box)));

    [Fact]
    public void A_reordered_older_director_is_ignored()
    {
        var s = new MusouClientState();
        Assert.True(s.Apply(DirectorAt(1000, 2, MusouPhase.Wave), 0.0));
        Assert.True(s.Apply(DirectorAt(1100, 3, MusouPhase.Wave), 0.1));
        Assert.True(s.Apply(DirectorAt(1050, 2, MusouPhase.Break), 0.2));   // late: from before wave 3
        Assert.Equal(3, s.Director!.Value.Wave);
        Assert.Equal(MusouPhase.Wave, s.Director!.Value.Phase);
        Assert.Equal(1, s.StaleDirectors);

        Assert.True(s.Apply(DirectorAt(1200, 5, MusouPhase.Won), 0.3));
        Assert.True(s.Apply(DirectorAt(1150, 5, MusouPhase.Boss), 0.4));   // a stale boss director after the win
        Assert.Equal(MusouPhase.Won, s.Director!.Value.Phase);
        Assert.Equal(2, s.StaleDirectors);
    }

    [Fact]
    public void A_duplicated_director_changes_nothing()
    {
        var s = new MusouClientState();
        var section = DirectorAt(500, 4, MusouPhase.Wave);
        Assert.True(s.Apply(section, 0.0));
        var shown = s.Director;
        Assert.True(s.Apply(section, 0.1));   // the same datagram twice
        Assert.Equal(shown, s.Director);
        Assert.Equal(0, s.StaleDirectors);
        Assert.True(s.Apply(DirectorAt(510, 4, MusouPhase.Break), 0.2));   // a newer one still applies
        Assert.Equal(MusouPhase.Break, s.Director!.Value.Phase);
    }

    [Fact]
    public void A_restarted_server_clock_takes_its_first_director()
    {
        var s = new MusouClientState();
        Assert.True(s.Apply(DirectorAt(5000, 3, MusouPhase.Wave), 0.0));
        Assert.True(s.Apply(DirectorAt(20, 0, MusouPhase.Waiting), 1.0));   // far older: the restart path, not a stale director
        Assert.Equal(1, s.Restarts);
        Assert.Equal(MusouPhase.Waiting, s.Director!.Value.Phase);
        Assert.True(s.Apply(DirectorAt(30, 1, MusouPhase.Wave), 1.1));
        Assert.Equal(1, s.Director!.Value.Wave);
    }

    [Fact]
    public void Forgetting_the_enemies_keeps_the_director()
    {
        var s = new MusouClientState();
        s.Apply(DirectorAt(100, 1, MusouPhase.Wave), 0.0);
        var entity = new MusouEntity(7, new Vector3(1f, 2f, 0f), 0f, MusouEnemyType.Boy, MusouEnemyState.Seek);
        s.Apply(new ExtrasSection(101, MusouWire.EntitiesTag, MusouWire.WriteEntities([entity], Box)), 0.0);
        Assert.Equal(1, s.Count);
        s.ForgetEnemies();
        Assert.Equal(0, s.Count);
        Assert.NotNull(s.Director);
        Assert.True(s.Apply(DirectorAt(99, 1, MusouPhase.Waiting), 0.1));   // still older than the kept one
        Assert.Equal(MusouPhase.Wave, s.Director!.Value.Phase);
    }
}
