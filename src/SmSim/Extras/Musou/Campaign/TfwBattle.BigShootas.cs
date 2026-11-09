using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Where the Big Shoota Nobs stand in a battle (expansion S3, docs/musou-expansion.md "Big Shoota Nob"; every value ours, owner brief 2026-11-07, spec "Musou expansion: Big Shoota Nob"). A change raises
/// <see cref="TfwRules.Version"/>.
/// </summary>
public static class TfwBigShootaRules
{
    /// <summary>One Big Shoota Nob holds a spot at each position the Orks hold at the landing (ours), never replaced (as the garrisons), with his own squad (owner brief: 10).</summary>
    public const int Squad = 10;   // 8 (80 %) on 2026-11-09; back to 100 % on 2026-11-10 (the owner: "roll back the 20% less orks change"); the 80 % was (the owner: "reduce the total amount of orks on the map to 80% of current")
    /// <summary>His spot: on the side of the point facing the landing zone, this far beyond its capture ring (ours: he covers the point without standing in it, so a capture never waits on him).</summary>
    public const float PostOut = 3f;
    /// <summary>His squad stands on a ring this far round him (ours), leashed: a target within <see cref="TfwBattleRules.DefenderAggro"/> wakes it.</summary>
    public const float RingMin = 2f, RingMax = 5f;
    /// <summary>The second Big Shoota Nob of a position (since 2026-11-10): his post turned this far (radians, 45 degrees; ours) round the point from the first's.</summary>
    public const float SecondTurn = MathF.PI / 4f;
}

public sealed partial class TfwBattle
{
    const int BigShootaGroupFirst = PatrolGroupFirst + 8;   // after the patrols' columns (13 on): 21 to 23
    readonly List<ushort> _bigShootas = new();

    /// <summary>Whether Big Shoota Nobs hold the positions in this battle (ours: always but in the final assault, with <see cref="Patrols"/>; tests turn them off to count the crowds alone).</summary>
    public bool BigShootasOn { get; set; } = true;
    /// <summary>The Big Shoota Nobs placed at the start, in position order (alive or not).</summary>
    public IReadOnlyList<ushort> BigShootas => _bigShootas;

    /// <summary>At the start of a battle (not the final assault): at each position a Big Shoota Nob on his post and his squad of <see cref="TfwBigShootaRules.Squad"/> round him; since 2026-11-10 a second Nob beside him.</summary>
    void PlaceBigShootas()
    {
        if (!Patrols || !BigShootasOn || _final) return;   // with the moving formations' switch (Patrols), which the crowd-counting tests turn off
        for (int n = 0; n < TfwBattleRules.BigTwins; n++)   // two a position since 2026-11-10 ("double boss and elite enemy spawns"): the second 45 degrees round the point, without a squad of his own
            for (int k = 0; k < Positions; k++)
                PlaceBigShoota(k, n);   // the first of every position first, so BigShootas[k] is still position k's (with every spawn made)
    }

    void PlaceBigShoota(int k, int n)
    {
        var point = _layout.Positions[k];
        var way = new Vector2(_layout.LandingZone.X - point.X, _layout.LandingZone.Y - point.Y);
        way = way.LengthSquared() > 1e-6f ? Vector2.Normalize(way) : Vector2.UnitY;
        if (n > 0) way = Vector2.Transform(way, Matrix3x2.CreateRotation(TfwBigShootaRules.SecondTurn * (n % 2 == 1 ? 1f : -1f) * ((n + 1) / 2)));
        Vector3 post = point;
        foreach (float share in (ReadOnlySpan<float>)[1f, 0.8f, 0.6f, 0.4f])
        {
            var spot = point + new Vector3(way * (_layout.Radius(k) + TfwBigShootaRules.PostOut) * share, 0f);
            if (_enemies.Nav is not { } nav) { post = spot; break; }
            int node = nav.Nearest(spot, 2f, 3f);
            if (node >= 0) { post = nav.Position(node); break; }
        }
        float yaw = FaceCentre(post);
        ushort id = _enemies.Spawn(MusouEnemyType.BigShootaNob, post, yaw, Health(MusouEnemyType.BigShootaNob));
        if (id == 0) return;
        _enemies.Post(id, post);
        _bigShootas.Add(id);
        TrackBig(id);   // off the crowds' cap since 2026-11-10
        if (n > 0) return;
        for (int m = 0; m < TfwBigShootaRules.Squad; m++)
        {
            var type = Fodder(_picked++);
            float angle = m * 2.399963f, radius = TfwBigShootaRules.RingMin + (TfwBigShootaRules.RingMax - TfwBigShootaRules.RingMin) * MusouSim.Unit(MusouSim.Mix((uint)(k * 16 + m), 0x5B16_5400u));
            var at = Ground(post + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f), post);
            _enemies.SpawnLeashed(type, at, yaw, Health(type), BigShootaGroupFirst + k, TfwBattleRules.DefenderAggro);
        }
    }
}
