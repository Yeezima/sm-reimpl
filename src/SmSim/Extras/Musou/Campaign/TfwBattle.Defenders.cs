using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The six Defenders' posts (expansion S6; <see cref="DefenderRules"/>): they act on their own, with no command, beside the Guardsmen. Each holds the Imperial position the Orks press hardest (an Ork army on its way
/// to it or attacking it counts all its men, the Orks near it count as they stand), shared out as the squads are; with none Imperial they hold the landing zone.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>The posts of <paramref name="count"/> Defenders, in their order: each a place round the position he holds.</summary>
    public Vector3[] DefenderPosts(int count)
    {
        var posts = new Vector3[Math.Max(0, count)];
        Span<float> threat = stackalloc float[Positions];
        Span<int> want = stackalloc int[Positions];
        Span<ushort> near = stackalloc ushort[DefenderRules.ThreatMost];
        int owned = 0;
        for (int k = 0; k < Positions; k++)
        {
            want[k] = 0;
            if (!Imperial(k)) { threat[k] = -1f; continue; }
            owned++;
            threat[k] = _enemies.Query(_layout.Positions[k], DefenderRules.ThreatRadius, near, DefenderRules.ThreatRadius) + DefenderRules.ThreatFloor;
            foreach (var a in _armies)
                if (a.Side == TfwArmySide.Ork && a.Goal == k && a.Order is TfwArmyOrder.March or TfwArmyOrder.Attack or TfwArmyOrder.Hold) threat[k] += a.FarCount;   // its real Orks are already in the Query
        }
        for (int i = 0; i < posts.Length; i++)
        {
            float angle = i * 2.399963f;
            var spread = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * DefenderRules.PostSpread;
            if (owned == 0) { posts[i] = Ground(_layout.LandingZone + spread, _layout.LandingZone); continue; }
            int best = -1;
            float bestScore = float.NegativeInfinity;
            for (int k = 0; k < Positions; k++)
            {
                if (threat[k] < 0f) continue;
                float score = threat[k] / (want[k] + 1);
                if (score > bestScore) { bestScore = score; best = k; }
            }
            want[best]++;
            posts[i] = Ground(_layout.Positions[best] + spread, _layout.Positions[best]);
        }
        return posts;
    }
}
