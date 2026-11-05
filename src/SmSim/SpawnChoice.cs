using System.Numerics;

namespace SmSim;

/// <summary>Where a classic PvP player asks to respawn (fix/pvp-deploy-spawn, protocol 57): the side's spawn room, a control point the side holds, or a Teleport Homer carrier.</summary>
public enum SpawnChoiceKind : byte { SpawnRoom = 0, ControlPoint = 1, Homer = 2 }

/// <summary>A respawn choice: <paramref name="Kind"/> and its target (a control point's index, a carrier's player id; 0 for the spawn room).</summary>
public readonly record struct SpawnChoice(SpawnChoiceKind Kind, int Target = 0)
{
    public static readonly SpawnChoice SpawnRoom = new(SpawnChoiceKind.SpawnRoom);
}

/// <summary>
/// fix/pvp-deploy-spawn (the owner, 2026-11-06; classic PvP only, i.e. a team match that is not Musou Mode): "You should never respawn in classic PvP mode on top of your
/// ally. Always in the main spawn room, or choose to spawn at a captured point. You can also choose to spawn on a player but only if they have the perk teleport homer on
/// and they're alive." And: "When pressing the deploy button in the loadout Customizer (while in game classic PvP mode only), suicide the player so they immediately
/// respawn [with] the new loadout."
/// <list type="bullet">
/// <item>A death respawn goes to the player's choice (<see cref="SetSpawnChoice"/>) when it is still valid at that moment (<see cref="SpawnOptions"/>): a control point
/// its side owns (Seize Ground), a living ally carrying Teleport Homer (the spot <see cref="HomerSpotNear"/> picks), else the side's spawn room. Nothing else puts a
/// player next to an ally: the old automatic Teleport Homer spawn (every respawning ally next to the nearest living carrier, and the Tactical classes carry the perk
/// by default) is kept for Musou Mode only.</item>
/// <item>Every chosen spot is free: on the floor, the capsule fits, not within two capsule radii (+0.1 m) of a living player; none free: the spawn room.</item>
/// <item>Deploy (<see cref="RequestDeploy"/>): the next tick kills the living player (a suicide: a death on the scoreboard, no killer; in Annihilation the other
/// side scores as for any death) with no respawn delay, so the tick after it the player respawns with the waiting loadout. No perk death blast and no drop.</item>
/// </list>
/// </summary>
public sealed partial class World
{
    readonly Dictionary<int, SpawnChoice> _spawnChoices = new();
    readonly HashSet<int> _deployNow = new();
    readonly HashSet<int> _deployDeaths = new();

    /// <summary>A team match that is not Musou Mode (Annihilation or Seize Ground): the classic PvP rules of this file apply.</summary>
    public bool ClassicPvp => Rules is not null && !Config.Extras.HasFlag(ContentExtras.Musou);

    /// <summary>Players killed this tick by a Deploy (no death blast, no drop).</summary>
    public IReadOnlyCollection<int> LastDeployDeaths => _deployDeaths;

    /// <summary>The player's standing respawn choice (the spawn room when none was made).</summary>
    public SpawnChoice SpawnChoiceOf(int playerId) => _spawnChoices.TryGetValue(playerId, out var c) ? c : SpawnChoice.SpawnRoom;

    /// <summary>
    /// Sets the player's respawn choice (kept until changed; checked again at each respawn). False (nothing changed) outside classic PvP, for an unknown player, an
    /// unknown kind or a target out of range (a point index beyond the map's, the player itself as carrier).
    /// </summary>
    public bool SetSpawnChoice(int playerId, SpawnChoice choice)
    {
        if (!ClassicPvp || !_players.ContainsKey(playerId)) return false;
        bool valid = choice.Kind switch
        {
            SpawnChoiceKind.SpawnRoom => true,
            SpawnChoiceKind.ControlPoint => choice.Target >= 0 && choice.Target < _pointSpecs.Length,
            SpawnChoiceKind.Homer => choice.Target > 0 && choice.Target != playerId,
            _ => false,
        };
        if (!valid) return false;
        if (choice.Kind == SpawnChoiceKind.SpawnRoom) _spawnChoices.Remove(playerId);
        else _spawnChoices[playerId] = choice;
        return true;
    }

    /// <summary>
    /// The respawn choices the player has now (classic PvP; empty otherwise): the spawn room first, then in Seize Ground each control point its side owns (in map order),
    /// then each living ally of its side carrying Teleport Homer (by id).
    /// </summary>
    public IReadOnlyList<SpawnChoice> SpawnOptions(int playerId)
    {
        var options = new List<SpawnChoice>();
        if (!ClassicPvp || !_players.TryGetValue(playerId, out var me)) return options;
        options.Add(SpawnChoice.SpawnRoom);
        if (Seizing)
            for (int i = 0; i < _points.Length; i++)
                if (_points[i].Owner == me.Team && me.Team != Team.None) options.Add(new SpawnChoice(SpawnChoiceKind.ControlPoint, i));
        foreach (var p in _players.Values.OrderBy(p => p.PlayerId))
            if (IsHomerCarrierFor(p, me)) options.Add(new SpawnChoice(SpawnChoiceKind.Homer, p.PlayerId));
        return options;
    }

    /// <summary>A living ally (another player of <paramref name="me"/>'s side) carrying Teleport Homer.</summary>
    bool IsHomerCarrierFor(PlayerState carrier, PlayerState me) =>
        carrier.Alive && carrier.PlayerId != me.PlayerId && me.Team is Team.SpaceMarines or Team.Chaos && carrier.Team == me.Team
        && Loadouts.HasPerk(Config, carrier, Loadouts.TeleportHomerPerk);

    /// <summary>Whether a player carries Teleport Homer and is alive (the scoreboard's flag, protocol 57).</summary>
    public bool CarriesHomer(int playerId) => _players.TryGetValue(playerId, out var p) && p.Alive && Loadouts.HasPerk(Config, p, Loadouts.TeleportHomerPerk);

    /// <summary>
    /// The in-match customizer's Deploy (classic PvP): the living player dies next tick and respawns the tick after with its waiting loadout. False (nothing queued)
    /// outside classic PvP, after the match ended, for a dead or unknown player, or without a waiting loadout (<see cref="SetNextLoadout"/> first).
    /// </summary>
    public bool RequestDeploy(int playerId)
    {
        if (!ClassicPvp || State == MatchState.Ended || !_players.TryGetValue(playerId, out var p) || !p.Alive || !_nextLoadouts.ContainsKey(playerId)) return false;
        _deployNow.Add(playerId);
        return true;
    }

    /// <summary>Takes the player's queued Deploy (see <see cref="RequestDeploy"/>): true when it still applies (alive, match not ended).</summary>
    bool TakeDeploy(int playerId)
    {
        if (_deployNow.Count == 0 || !_deployNow.Remove(playerId)) return false;
        return State != MatchState.Ended && _players.TryGetValue(playerId, out var p) && p.Alive;
    }

    void ForgetSpawnChoice(int playerId)
    {
        _spawnChoices.Remove(playerId);
        _deployNow.Remove(playerId);
    }

    /// <summary>
    /// Classic PvP's death respawn at the player's choice (see the class summary): a held control point's free spot, or the chosen living carrier's; false for the
    /// spawn room (or a choice no longer valid, or no free spot), which the caller then uses.
    /// </summary>
    bool TryChosenSpawn(PlayerState dead, out Vector3 position, out float yaw)
    {
        position = default; yaw = 0f;
        if (!ClassicPvp || !_spawnChoices.TryGetValue(dead.PlayerId, out var choice)) return false;
        switch (choice.Kind)
        {
            case SpawnChoiceKind.ControlPoint:
                if (!Seizing || choice.Target < 0 || choice.Target >= _points.Length || _points[choice.Target].Owner != dead.Team || dead.Team == Team.None) return false;
                if (PointSpot(choice.Target, dead.PlayerId) is not { } spot) return false;
                position = spot;
                yaw = FacingMiddle(spot, dead.Yaw);
                return true;
            case SpawnChoiceKind.Homer:
                if (!_players.TryGetValue(choice.Target, out var carrier) || !IsHomerCarrierFor(carrier, dead)) return false;
                if (HomerSpotNear(carrier, dead.PlayerId) is not { } near) return false;
                position = near;
                yaw = carrier.Yaw;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Radii of the rings searched for a free spot at a control point (metres, from its centre; ours), kept inside the point's cylinder.</summary>
    static readonly float[] PointRadii = [0f, 1.5f, 3f, 4.5f];

    /// <summary>
    /// A free spot on control point <paramref name="point"/> (ours): the centre, then rings of 1.5, 3 and 4.5 m (12 directions; only those inside the point's radius less
    /// a capsule radius), each dropped onto the floor within the point's height, the capsule fitting, outside kill volumes and clear of every living player. Null when none.
    /// </summary>
    Vector3? PointSpot(int point, int self)
    {
        var cp = _pointSpecs[point];
        float inner = MathF.Max(0f, cp.Radius - MathF.Max(Config.CapsuleRadius, 0.05f));
        foreach (float radius in PointRadii)
        {
            if (radius > inner && radius > 0f) break;
            int directions = radius == 0f ? 1 : 12;
            for (int k = 0; k < directions; k++)
            {
                float angle = k * MathF.Tau / directions;
                var at = cp.Center + new Vector3(-MathF.Sin(angle) * radius, MathF.Cos(angle) * radius, 0f);
                if (Collision is not null)
                {
                    if (LowestStandable(at.X, at.Y, cp.Center.Z + MathF.Max(cp.HalfHeight, 1f), cp.Center.Z - MathF.Max(cp.HalfHeight, 1f) - 1f) is not { } floor) continue;
                    at = floor;
                    if (Collision.KillsPlayer(at, at, Config.CapsuleHeight)) continue;
                    if (Collision.Character.Raycast(at + new Vector3(0f, 0f, 0.05f), Vector3.UnitZ, Config.CapsuleHeight - 0.1f) is not null) continue;
                }
                if (!Crowded(at, self)) return at;
            }
        }
        return null;
    }

    /// <summary>A living player other than <paramref name="self"/> stands within two capsule radii plus 0.1 m of <paramref name="at"/> (in plan, within a capsule's height).</summary>
    bool Crowded(Vector3 at, int self)
    {
        float gap = 2f * MathF.Max(Config.CapsuleRadius, 0.05f) + 0.1f;
        foreach (var (id, p) in _players)
            if (id != self && p.Alive && Vector2.DistanceSquared(new Vector2(p.Position.X, p.Position.Y), new Vector2(at.X, at.Y)) < gap * gap
                && MathF.Abs(p.Position.Z - at.Z) < Config.CapsuleHeight) return true;
        return false;
    }

    /// <summary>The facing toward the map's middle from <paramref name="at"/> (ours); <paramref name="fallback"/> within 1 m of it or without one.</summary>
    float FacingMiddle(Vector3 at, float fallback)
    {
        if (MapMiddle is not { } middle) return fallback;
        var d = new Vector2(middle.X - at.X, middle.Y - at.Y);
        if (d.LengthSquared() < 1f) return fallback;
        return MathF.Atan2(-d.X, d.Y);   // forward is (-sin yaw, cos yaw)
    }
}
