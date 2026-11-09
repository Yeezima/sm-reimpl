using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The owner, 2026-11-06 (stream D): "After the Warboss dies, a giant Ork wave attacks from all angles". When the Warboss of a battle falls (not the final assault's: that ends the war), the
/// sources stop as before, and <see cref="VictoryWaveRules.Size"/> unleashed Orks come in from a ring round the living players, evenly spread over every bearing, a few a tick, and go for
/// them: the free pickup then has to be survived. Every value ours, an estimate (spec "Musou Mode: the wave after the Warboss").
/// </summary>
public static class VictoryWaveRules
{
    /// <summary>Orks in the wave: more than a full crowd (400), less than the pool's room after a battle's garrisons and crowds (ours).</summary>
    public const int Size = 500;
    /// <summary>Orks added a tick: 500 in about 1.4 s at 30 Hz, so no tick builds a crowd at once (ours).</summary>
    public const int PerTick = 12;
    /// <summary>The ring's radius round the players' centre (m), and how far in a spot that falls in a wall or off the walking graph is tried again (ours: out of a frag's 20 m, inside sight).</summary>
    public const float Radius = 45f, RadiusStep = 10f, RadiusLeast = 20f;
    /// <summary>The wave's elites: every this many Orks an 'Ard Boy (ours; the rest the fodder's Boyz and Shootas).</summary>
    public const int EliteEvery = 8;
}

public sealed partial class TfwBattle
{
    int _waveLeft, _waveSent;

    /// <summary>Orks of the victory wave still to come (0: none, or all sent).</summary>
    public int VictoryWaveLeft => _waveLeft;
    /// <summary>Orks of the victory wave sent so far.</summary>
    public int VictoryWaveSent => _waveSent;

    /// <summary>The Warboss fell: the wave is due (none after the final assault's).</summary>
    void BeginVictoryWave()
    {
        if (!_final && VictoryWave) _waveLeft = VictoryWaveRules.Size;
    }

    /// <summary>Whether a won battle sends the wave (false only in tests of the battle's own end).</summary>
    public bool VictoryWave { get; set; } = true;

    /// <summary>A tick of the wave: up to <see cref="VictoryWaveRules.PerTick"/> Orks on the ring round <paramref name="players"/>' centre, while the pool has room above its reserve.</summary>
    void StepVictoryWave(ReadOnlySpan<Vector3> players)
    {
        if (_waveLeft <= 0) return;
        var centre = _layout.LandingZone;
        if (players.Length > 0)
        {
            var sum = Vector3.Zero;
            foreach (var p in players) sum += p;
            centre = sum / players.Length;
        }
        for (int k = 0; k < VictoryWaveRules.PerTick && _waveLeft > 0; k++)
        {
            if (PoolUsed >= TfwBattleRules.CrowdPoolRoom) { _waveLeft = 0; return; }   // the pool is full: the wave is what came
            int n = _waveSent++;
            _waveLeft--;
            // Every bearing in turn: a golden-angle walk round the ring covers all sides evenly at any count, each tick's few far apart.
            float angle = n * 2.399963f;
            var way = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
            if (WaveSpot(centre, way) is not { } at) continue;
            var type = n % VictoryWaveRules.EliteEvery == VictoryWaveRules.EliteEvery - 1 ? MusouEnemyType.ArdBoy : Fodder(_picked++);
            _enemies.Spawn(type, at, FaceCentre(at), Health(type));
        }
    }

    /// <summary>A spot on the walking graph along <paramref name="way"/> from <paramref name="centre"/>: at the ring, else nearer in steps; null when none is on the ground (that Ork does not come).</summary>
    Vector3? WaveSpot(Vector3 centre, Vector3 way)
    {
        for (float r = VictoryWaveRules.Radius; r >= VictoryWaveRules.RadiusLeast; r -= VictoryWaveRules.RadiusStep)
        {
            var spot = centre + way * r;
            if (_enemies.Nav is not { } nav) return spot;
            int node = nav.Nearest(spot, 1.5f, 6f);
            if (node >= 0) return nav.Position(node);
        }
        return null;
    }
}
