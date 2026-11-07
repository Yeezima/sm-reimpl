using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

public sealed partial class World
{
    readonly List<MusouCrash> _musouCrashes = new();
    readonly ushort[] _musouScatter = new ushort[256];

    /// <summary>The crashes of shot-down Stormboys this tick (expansion S3).</summary>
    public int MusouCrashesSetOff { get; private set; }

    /// <summary>
    /// Expansion S3: each shot-down Stormboy that struck the ground this tick sets off the original frag blast as an Ork blast (<see cref="MusouStormboy.CrashBlast"/>: players, AI Marines, Guards and Orks,
    /// credited to whoever shot him down), and the fodder that lives through it is thrown down away from it (<see cref="MusouStormboy.CrashScatter"/> at the centre, by the blast's intensity). From the war's fight
    /// (<see cref="TfwGuardsStep"/>), after the enemies' step.
    /// </summary>
    void MusouStormboyCrashes()
    {
        if (_musou is not { } musou) return;
        _musouCrashes.Clear();
        if (musou.TakeCrashes(_musouCrashes) == 0) return;
        foreach (var crash in _musouCrashes)
        {
            MusouCrashesSetOff++;
            var blast = MusouStormboy.CrashBlast;
            MusouOrkBlast(crash.Point, blast, crash.Credit);
            int n = musou.Query(crash.Point, blast.Radius, _musouScatter, verticalReach: blast.Radius);
            for (int k = 0; k < n; k++)
            {
                if (!musou.TryGet(_musouScatter[k], out var e) || !e.Alive) continue;
                var away = new Vector2(e.Position.X - crash.Point.X, e.Position.Y - crash.Point.Y);
                if (away.LengthSquared() < 1e-6f) away = new Vector2(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw));
                float distance = MusouStormboy.CrashScatter * blast.IntensityAt(away.Length());
                if (distance > 0.05f) musou.KnockDown(e.Id, Vector2.Normalize(away), distance, crash.Credit, deathChance: 0f);
            }
        }
    }
}
