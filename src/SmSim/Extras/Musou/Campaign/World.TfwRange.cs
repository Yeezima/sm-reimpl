using System.Numerics;
using SmSim.Extras.Musou;

using SmSim.Extras.Musou.Campaign;

namespace SmSim;

public sealed partial class World
{
    int _rangeSpawned;

    /// <summary>
    /// The Firing Range's spawning (the owner: "continuously 200 orks in the way the musou mode spawns them"): free hunters of Musou's mix (55 % Boyz, 30 % Shootas, the rest 'Ard Boyz, as
    /// <see cref="TfwBattleRules"/>) on the graph, in the half of the square away from the humans (at least 25 m from each), at most <paramref name="releases"/> a tick until <paramref name="orks"/> live.
    /// </summary>
    internal void TfwRangeTopUp(TfwRange range, IReadOnlyList<int> humans, int orks, int releases)
    {
        if (_tfw is null || _musou is not { } enemies) return;
        var nav = enemies.Nav;
        for (int made = 0; made < releases && enemies.Alive < orks; made++)
        {
            Vector3 at = default;
            bool placed = false;
            for (int attempt = 0; attempt < 6 && !placed; attempt++)
            {
                ulong index = (ulong)(++_rangeSpawned);
                float x = (_tfw.Fire.Roll(Tick, 910, index) - 0.5f) * (TfwRange.Size - 8f), y = (_tfw.Fire.Roll(Tick, 911, index) - 0.5f) * (TfwRange.Size - 8f);
                at = range.Centre + new Vector3(x, y, 0f);
                placed = true;
                foreach (int id in humans) if (_players.TryGetValue(id, out var p) && Vector2.Distance(new(p.Position.X, p.Position.Y), new(at.X, at.Y)) < 25f) placed = false;
            }
            if (!placed) return;
            if (nav is not null)
            {
                int node = nav.Nearest(at, 6f, 8f);
                if (node < 0) continue;
                at = nav.Position(node);
            }
            float roll = _tfw.Fire.Roll(Tick, 912, (ulong)_rangeSpawned);
            var type = roll < TfwBattleRules.FodderBoyz ? MusouEnemyType.Boy : roll < TfwBattleRules.FodderBoyz + TfwBattleRules.FodderShootas ? MusouEnemyType.ShootaBoy : MusouEnemyType.ArdBoy;
            float yaw = _tfw.Fire.Roll(Tick, 913, (ulong)_rangeSpawned) * MathF.Tau;
            if (enemies.Spawn(type, at, yaw, enemies.Rules.Of(type).Health) == 0) return;
        }
    }
}
