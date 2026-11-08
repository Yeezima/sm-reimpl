using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// One Ork explosion (expansion S1, docs/musou-expansion.md "Ork blast"; every number ours, owner brief 2026-11-07). <see cref="Radius"/> metres, <see cref="Damage"/> at the centre; the damage falls off from the
/// full value at the centre to <see cref="EdgeFraction"/> of it at the edge, linearly, or by <see cref="FallOff"/> (a data fall-off, as <see cref="Combat.SplashIntensity"/> reads it) when given.
/// <see cref="HurtBosses"/> (default true): the Warboss and the Boss Orks take it too, as every other Ork does; false spares them.
/// </summary>
public sealed record OrkBlastSpec(float Radius, float Damage)
{
    public float EdgeFraction { get; init; } = 0.25f;
    public IReadOnlyList<DistancePoint>? FallOff { get; init; }
    public bool HurtBosses { get; init; } = true;

    /// <summary>The share of <see cref="Damage"/> at <paramref name="distance"/> (0 beyond the radius).</summary>
    public float IntensityAt(float distance) =>
        distance > Radius ? 0f : FallOff is { Count: > 0 } points ? Combat.SplashIntensity(points, distance)
        : Radius <= 0f ? 1f : 1f - (1f - EdgeFraction) * (distance / Radius);
}

/// <summary>What an Ork blast did (counts of those struck and of those it killed).</summary>
public readonly record struct OrkBlastResult(int Players, int PlayerKills, int GuardMen, int GuardKills, int Orks, int OrkKills, int SquigsQueued);

public sealed partial class World
{
    /// <summary>
    /// Friendly-fire table (expansion S1; ours, owner brief 2026-11-07). Ork bullets and Ork blows never hurt Orks: a shooter's shot is a rolled hit on a player or a Guard squad with no projectile, and
    /// a melee blow lands on its own target (so nothing needs a rule). Ork explosions (this blast: Bomb Squig, Rokkit rocket, Stormboy crash, scrap trap shot by a player) hurt everyone in the radius:
    /// players incl. AI Marines, Guard squads' men, and Orks (elites and bosses too, unless the spec spares bosses). The Weirdboy's own burst and bolts are a separate older rule and unchanged.
    /// </summary>
    public const string OrkFriendlyFireTable =
        "Ork bullets: players yes, AI Marines yes, Guards yes, Orks no. Ork melee: Orks no. Ork explosions: players yes, AI Marines yes, Guards yes, Orks yes (elites yes, bosses yes by default).";

    /// <summary>
    /// A neutral Ork explosion at <paramref name="point"/>: damages every living player (AI Marines included) through the enemies' attack rules (<see cref="MusouHit"/>: received-damage multipliers, no stagger),
    /// the men of every Guard squad, and every Ork (<see cref="MusouDeathKind.OrkBlast"/>), then queues the Bomb Squigs in the radius to detonate (<see cref="MusouSim.QueueSquigDetonations"/>).
    /// <paramref name="credit"/> is the player whose action caused it (0: no one); it is the credited killer of the Orks it kills. Nothing outside a Musou Mode battle in play.
    /// </summary>
    public OrkBlastResult MusouOrkBlast(Vector3 point, OrkBlastSpec blast, int credit = 0)
    {
        if (_musou is not { } musou || _state != MatchState.Playing || !(blast.Radius > 0f) || !(blast.Damage > 0f)) return default;
        MusouBeginTick();
        int pending = musou.PendingDetonations;   // expansion S2: a Squig this blast kills queues its own burst (MusouSim.Squig.cs), counted with the ones queued below
        int players = 0, playerKills = 0, guardMen = 0, guardKills = 0, orks = 0, orkKills = 0;
        // Players (and AI Marines): a snapshot of the ids, since MusouHit writes the player table.
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        _musouIds.Sort();
        foreach (int id in _musouIds)
        {
            var p = _players[id];
            if (!p.Alive) continue;
            var d = p.Position + new Vector3(0f, 0f, Config.CapsuleHeight * 0.5f) - point;
            float distance = d.Length();
            float amount = blast.Damage * blast.IntensityAt(distance);
            if (!(amount > 0f)) continue;
            players++;
            MusouHit(new MusouAttack(0, id, amount, Melee: false, Staggers: false, point));
            if (_players.TryGetValue(id, out var after) && !after.Alive) playerKills++;
        }
        if (TfwGuards is { } guards)
        {
            guardMen = guards.Blast(point, blast.Radius, blast.IntensityAt, blast.Damage, out guardKills);
        }
        int n = musou.Query(point, blast.Radius, _musouNear, verticalReach: blast.Radius + 3f);
        for (int k = 0; k < n; k++)
        {
            if (!musou.TryGet(_musouNear[k], out var e) || !e.Alive) continue;   // !Alive: one an earlier Weirdboy burst killed in this loop
            if (!blast.HurtBosses && (e.Type == MusouEnemyType.Warboss || MusouBossOrks.Is(e.Type))) continue;
            var chest = MusouSim.Chest(e.Position, musou.Body(e.Type).Height);
            float distance = Vector3.Distance(chest, point);
            float amount = blast.Damage * blast.IntensityAt(distance);
            if (!(amount > 0f)) continue;
            var away = new Vector2(e.Position.X - point.X, e.Position.Y - point.Y);
            var push = away.LengthSquared() > 1e-8f ? Vector2.Normalize(away) : new Vector2(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw));
            orks++;
            if (musou.Damage(e.Id, credit, amount, new Vector3(push, 0.3f), out bool killed, cause: MusouDeathKind.OrkBlast) && killed) orkKills++;
        }
        TfwMekOrkBlast(point, blast, credit);   // Campaign/World.TfwMeks.cs: the contraptions (expansion S4)
        musou.QueueSquigDetonations(point, blast.Radius, credit, _musouNear);
        int queued = musou.PendingDetonations - pending;
        return new OrkBlastResult(players, playerKills, guardMen, guardKills, orks, orkKills, queued);
    }
}
