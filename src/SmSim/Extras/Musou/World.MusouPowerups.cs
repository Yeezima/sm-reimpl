using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the powerups in the World (owner, 2026-11-10; <see cref="MusouPowerupRules"/>). A boss's KO, and one elite KO in ten, makes a drop spot where it fell; every living player (not the
/// AI Marines) gets a powerup drop of its own there (<see cref="World.MusouSpawnDrop"/>, the same per-player drops as the weapons had: only that player sees it). The player takes it by holding the use key
/// for <see cref="MusouPowerupRules.HoldSeconds"/> within reach (the owner's "Hold the use key"; <see cref="MusouHoldsStep"/>, checked here on the server: a release, a death or leaving the drop's reach
/// ends the hold). Taking it starts its buff for its full time (one already running starts again: no stacking, ours). The buffs end at death and with the round. Once a tick (<see cref="MusouPowerupsStep"/>)
/// the World hands the enemy simulation the feared players and the damage scales (the melee one with any stim held to <see cref="MusouPowerupRules.Cap"/>, x2: <see cref="MusouMeleeDealt"/>), and keeps
/// <see cref="PlayerState.FireBoostLeft"/> in step with Rapid Fire. Clients learn of their own buffs from wire tag 6.
/// </summary>
public sealed partial class World
{
    readonly Dictionary<int, MusouBuffs> _musouBuffs = new();
    readonly Dictionary<int, uint> _musouBuffVersions = new();
    readonly List<int> _musouBuffIds = new();
    readonly List<int> _musouFeared = new();
    readonly Dictionary<int, (ushort Drop, uint Start)> _musouHolds = new();

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

    /// <summary>The powerup pickup <paramref name="playerId"/> holds the use key for now: the drop and the tick the hold began (the press's); null without one.</summary>
    public (ushort Drop, uint Start)? MusouHoldOf(int playerId) => _musouHolds.TryGetValue(playerId, out var h) ? h : null;

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

    /// <summary>
    /// The whole multiplier on <paramref name="playerId"/>'s melee hits on the enemies now: a stim's melee (<see cref="MusouMeleeDealt"/>) times the powerups' scale in the pool, never above
    /// <see cref="MusouPowerupRules.Cap"/>; 1 outside Musou or for an unknown player (the tests and the log).
    /// </summary>
    public float MusouMeleeMultiplier(int playerId) =>
        _musou is null || !_players.TryGetValue(playerId, out var p) ? 1f : MusouMeleeDealt(playerId, p) * _musou.MeleeScaleOf(playerId);

    /// <summary>
    /// The stim's melee multiplier for <paramref name="attacker"/> (as <paramref name="state"/>) on a hit of the enemies (<see cref="Stims.MeleeDealt"/>), held so that with the powerups' scale the
    /// pool puts on the same hit (Double Damage, Supercharged Melee: <see cref="MusouSim.MeleeScaleOf"/>, itself at most the cap) the whole never passes <see cref="MusouPowerupRules.Cap"/> (owner,
    /// 2026-11-10: "x2 cap"; Musou's stim is x2 alone, so while one works the powerups add nothing to melee). Without a powerup it is the stim's, as before. Used by the charge, the cleave and the
    /// spear's impale; the stomp (a melee blast) carries no stim and only the pool's scale.
    /// </summary>
    float MusouMeleeDealt(int attacker, PlayerState state)
    {
        float stim = Stims.MeleeDealt(Config, state);
        float powerups = _musou!.MeleeScaleOf(attacker);
        return powerups > 1f ? MathF.Min(stim, MusouPowerupRules.Cap / powerups) : stim;
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
            // The owner's "x2 cap" (2026-11-10): Supercharged Melee and Double Damage together are x2 on melee, not x4 (a stim is held under the same cap: MusouMeleeDealt).
            float melee = MathF.Min(MusouPowerupRules.Cap, other * (b.Active(MusouPowerup.Supercharged, Tick) ? MusouPowerupRules.MeleeScale : 1f));
            musou.SetAttackerScale(id, melee, other);
            float fire = b.Active(MusouPowerup.RapidFire, Tick) ? (b.RapidFire - Tick) / (float)Config.TickRate : 0f;
            if (MathF.Abs(p.FireBoostLeft - fire) > 0.5f / Config.TickRate) _players[id] = p with { FireBoostLeft = fire };
        }
        musou.SetFear(_musouFeared);
    }

    /// <summary>A press of <paramref name="playerId"/> whose nearest own drop in reach is the powerup <paramref name="drop"/>: its hold begins this tick (<see cref="MusouHoldsStep"/>).</summary>
    void MusouStartHold(int playerId, ushort drop) => _musouHolds[playerId] = (drop, Tick);

    /// <summary>
    /// Once a tick (from <see cref="MusouDropsStep"/>, after this tick's presses): each hold goes on while its player is alive in play, holds the use key (the input's own flag: the server's check) and stays in
    /// reach of the live drop it began on; one that has run <see cref="MusouPowerupRules.HoldTicks"/> ticks (the press's tick the first) takes the drop and starts its buff; anything else ends it.
    /// </summary>
    void MusouHoldsStep()
    {
        if (_musouHolds.Count == 0) return;
        int need = MusouPowerupRules.HoldTicks(Config.TickRate);
        _musouBuffIds.Clear();
        foreach (int id in _musouHolds.Keys) _musouBuffIds.Add(id);
        _musouBuffIds.Sort();
        foreach (int id in _musouBuffIds)
        {
            var (dropId, start) = _musouHolds[id];
            int index = -1;
            for (int i = 0; i < _musouDrops.Count; i++) if (_musouDrops[i].Id == dropId) { index = i; break; }
            bool holding = _state == MatchState.Playing && _interactHeld.Contains(id) && _players.TryGetValue(id, out var p) && p.Alive && index >= 0
                && _musouDrops[index].Player == id && _musouDrops[index].State == MusouDropState.Live && MusouInReach(_musouDrops[index].Position, p.Position);
            if (!holding) { _musouHolds.Remove(id); continue; }
            if (Tick - start + 1 < need) continue;
            _musouHolds.Remove(id);
            if (MusouGiveBuff(id, MusouPowerupRules.KindOf(_musouDrops[index].Item))) MusouEndDrop(index, MusouDropState.Picked);
        }
    }

    /// <summary>The cleave of a Supercharged player (from <c>MusouCleave</c>): reach and cone half-angle times <see cref="MusouPowerupRules.CleaveScale"/> (the angle at most 180 degrees).</summary>
    void MusouSuperchargedCleave(int attacker, ref float range, ref float minCos)
    {
        if (!MusouBuffActive(attacker, MusouPowerup.Supercharged)) return;
        range *= MusouPowerupRules.CleaveScale;
        float half = MathF.Acos(Math.Clamp(minCos, -1f, 1f)) * MusouPowerupRules.CleaveScale;
        minCos = MathF.Cos(MathF.Min(MathF.PI, half));
    }

    /// <summary>A new round: every buff ends, and every hold.</summary>
    void MusouClearBuffs()
    {
        foreach (int id in _musouBuffs.Keys) _musouBuffVersions[id] = _musouBuffVersions.GetValueOrDefault(id) + 1;
        foreach (int id in _musouBuffs.Keys) if (_players.TryGetValue(id, out var p) && p.FireBoostLeft > 0f) _players[id] = p with { FireBoostLeft = 0f };
        _musouBuffs.Clear();
        _musouHolds.Clear();
    }
}
