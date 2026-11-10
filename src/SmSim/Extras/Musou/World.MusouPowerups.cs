using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the powerups in the World (owner, 2026-11-10; <see cref="MusouPowerupRules"/>). A boss's KO, and one elite KO in ten, makes a drop spot where it fell; every living player (not the
/// AI Marines) gets a powerup drop of its own there (<see cref="World.MusouSpawnDrop"/>, the same per-player drops as the weapons had: only that player sees it and takes it with the use key). Taking it
/// starts its buff for its full time (one already running starts again: no stacking, ours). The buffs end at death and with the round. Once a tick (<see cref="MusouPowerupsStep"/>) the World hands the
/// enemy simulation the feared players and the damage scales, and keeps <see cref="PlayerState.FireBoostLeft"/> in step with Rapid Fire. Clients learn of their own buffs from wire tag 6.
/// </summary>
public sealed partial class World
{
    readonly Dictionary<int, MusouBuffs> _musouBuffs = new();
    readonly Dictionary<int, uint> _musouBuffVersions = new();
    readonly List<int> _musouBuffIds = new();
    readonly List<int> _musouFeared = new();

    /// <summary>Whether enemies drop powerups (on in every Musou match; tests of the old weapon drops turn it off).</summary>
    public bool MusouPowerupDrops { get; set; } = true;

    /// <summary>Powerups taken since the match began (the log and the tests).</summary>
    public int MusouPowerupsTaken { get; private set; }

    /// <summary>Player <paramref name="playerId"/>'s buffs (the ticks they end on; all 0 without any).</summary>
    public MusouBuffs MusouBuffsOf(int playerId) => _musouBuffs.GetValueOrDefault(playerId);

    /// <summary>A number that changes whenever <paramref name="playerId"/>'s buffs change (taken, or ended early by a death or the round).</summary>
    public uint MusouBuffVersion(int playerId) => _musouBuffVersions.GetValueOrDefault(playerId);

    /// <summary>Whether <paramref name="kind"/> works for <paramref name="playerId"/> now.</summary>
    public bool MusouBuffActive(int playerId, MusouPowerup kind) => _musouBuffs.TryGetValue(playerId, out var b) && b.Active(kind, Tick);

    /// <summary>
    /// Starts <paramref name="kind"/> for a living player for its full time (the pickup, the tests and the capture aid); false outside Musou, for an unknown or dead player or no kind.
    /// Rapid Fire also sets <see cref="PlayerState.FireBoostLeft"/>, which the player's own step counts down (so the client predicts the fire rate and the free rounds).
    /// </summary>
    public bool MusouGiveBuff(int playerId, MusouPowerup kind)
    {
        if (_musou is null || kind == MusouPowerup.None || !_players.TryGetValue(playerId, out var p) || !p.Alive) return false;
        float seconds = MusouPowerupRules.Seconds(kind);
        uint ticks = (uint)MathF.Max(1f, MathF.Round(seconds * Config.TickRate));
        var b = _musouBuffs.GetValueOrDefault(playerId);
        b.Set(kind, Tick + ticks);
        _musouBuffs[playerId] = b;
        _musouBuffVersions[playerId] = _musouBuffVersions.GetValueOrDefault(playerId) + 1;
        if (kind == MusouPowerup.RapidFire) _players[playerId] = p with { FireBoostLeft = seconds };
        MusouPowerupsTaken++;
        return true;
    }

    /// <summary>A KO (from <see cref="MusouDropFor"/>): a powerup drop for every living player when the enemy is a boss, or an elite whose roll comes up.</summary>
    void MusouPowerupFor(MusouKill kill, System.Numerics.Vector3 at)
    {
        if (!MusouPowerupDrops || !MusouPowerupRules.Drops(kill.Type, Tick, kill.Enemy)) return;
        _musouDropPlayers.Clear();
        foreach (int id in _players.Keys) _musouDropPlayers.Add(id);
        _musouDropPlayers.Sort();
        foreach (int id in _musouDropPlayers)
        {
            if (!_players[id].Alive) continue;
            if (_tfw is not null && SmSim.Extras.Musou.Campaign.TfwContracts.IsPuppet(id)) continue;   // the AI Marines pick nothing up
            MusouSpawnDrop(id, at, MusouPowerupRules.Item(MusouPowerupRules.Roll(Tick, kill.Enemy, id)), kill.Enemy);
        }
    }

    /// <summary>
    /// Once a tick (from <see cref="MusouDropsStep"/>, before the pickups): a dead player's buffs end, an ended buff is forgotten, and the enemy simulation gets the feared players and every player's
    /// damage scales; a living player's <see cref="PlayerState.FireBoostLeft"/> is held to Rapid Fire's time left.
    /// </summary>
    void MusouPowerupsStep()
    {
        var musou = _musou!;
        _musouFeared.Clear();
        musou.ClearAttackerScales();
        if (_musouBuffs.Count == 0) { musou.SetFear(_musouFeared); return; }
        _musouBuffIds.Clear();
        foreach (int id in _musouBuffs.Keys) _musouBuffIds.Add(id);
        _musouBuffIds.Sort();
        foreach (int id in _musouBuffIds)
        {
            var b = _musouBuffs[id];
            if (!_players.TryGetValue(id, out var p) || !p.Alive || !b.Any(Tick))
            {
                // Buffs are tied to a life (ours, as the drops): a death or a leave ends them; one that ran out is forgotten.
                bool early = b.Any(Tick);
                _musouBuffs.Remove(id);
                if (early) _musouBuffVersions[id] = _musouBuffVersions.GetValueOrDefault(id) + 1;
                if (p.Alive && p.FireBoostLeft > 0f) _players[id] = p with { FireBoostLeft = 0f };
                continue;
            }
            if (b.Active(MusouPowerup.Fear, Tick)) _musouFeared.Add(id);
            float other = b.Active(MusouPowerup.DoubleDamage, Tick) ? MusouPowerupRules.DamageScale : 1f;
            float melee = other * (b.Active(MusouPowerup.Supercharged, Tick) ? MusouPowerupRules.MeleeScale : 1f);
            musou.SetAttackerScale(id, melee, other);
            float fire = b.Active(MusouPowerup.RapidFire, Tick) ? (b.RapidFire - Tick) / (float)Config.TickRate : 0f;
            if (MathF.Abs(p.FireBoostLeft - fire) > 0.5f / Config.TickRate) _players[id] = p with { FireBoostLeft = fire };
        }
        musou.SetFear(_musouFeared);
    }

    /// <summary>The cleave of a Supercharged player (from <c>MusouCleave</c>): reach and cone half-angle times <see cref="MusouPowerupRules.CleaveScale"/> (the angle at most 180 degrees).</summary>
    void MusouSuperchargedCleave(int attacker, ref float range, ref float minCos)
    {
        if (!MusouBuffActive(attacker, MusouPowerup.Supercharged)) return;
        range *= MusouPowerupRules.CleaveScale;
        float half = MathF.Acos(Math.Clamp(minCos, -1f, 1f)) * MusouPowerupRules.CleaveScale;
        minCos = MathF.Cos(MathF.Min(MathF.PI, half));
    }

    /// <summary>A new round: every buff ends.</summary>
    void MusouClearBuffs()
    {
        foreach (int id in _musouBuffs.Keys) _musouBuffVersions[id] = _musouBuffVersions.GetValueOrDefault(id) + 1;
        foreach (int id in _musouBuffs.Keys) if (_players.TryGetValue(id, out var p) && p.FireBoostLeft > 0f) _players[id] = p with { FireBoostLeft = 0f };
        _musouBuffs.Clear();
    }
}
