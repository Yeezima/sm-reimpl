using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Progression;

namespace SmSim;

/// <summary>
/// Warfront's progression in the World (slice W3b): XP from the kills, the level-up refill, the picker's openings and timeouts, the protection of a standing offer, and the cards' effects (damage dealt, damage taken, attack speed, cooldowns),
/// the squad seams (W9). Each hook returns at once unless the run's Warfront object is set. Bound to no input: <see cref="WarfrontPick"/> is for the later wire.
/// </summary>
public sealed partial class World
{
    readonly List<int> _warfrontIds = new();
    readonly ushort[] _warfrontOne = new ushort[1];

    /// <summary>The offer standing for player <paramref name="player"/> (null: none), for the later wire.</summary>
    public WarfrontOpenOffer? WarfrontOffer(int player) =>
        _tfw?.Warfront?.Progression is { } rt && rt.TryKey(player, out ulong key) ? rt.Picker.OpenOffer(key) : null;

    /// <summary>A pick of player <paramref name="player"/>: card slot 0 to 3 of the standing offer whose pick index is <paramref name="pickIndex"/> (a stale index or no offer: refused, nothing changes). Protection ends with it.</summary>
    public bool WarfrontPick(int player, int slot, int pickIndex) =>
        _tfw?.Warfront?.Progression is { } rt && rt.TryKey(player, out ulong key) && rt.TryPick(key, slot, pickIndex);

    partial void WarfrontStep()
    {
        if (_tfw?.Warfront is not { } rules) return;
        var rt = rules.Progression;
        rt.Refill ??= () => WarfrontRefill(rt);
        _tfw.Fire.Warfront ??= rules;
        int rate = Math.Max(1, Config.TickRate);
        rt.Assists.WindowTicks = (uint)(5 * rate);
        double now = Tick / (double)rate;
        _warfrontIds.Clear();
        foreach (int id in _players.Keys) if (!TfwContracts.IsPuppet(id)) _warfrontIds.Add(id);
        _warfrontIds.Sort();
        rt.Sync(_warfrontIds);
        WarfrontRefill(rt);
        // The cards' attack speed rides on the player state (Stims.AttackSpeed): set each tick.
        foreach (int id in _warfrontIds)
        {
            if (!_players.TryGetValue(id, out var p)) continue;
            float bonus = rt.BonusesOf(id).AttackSpeed - 1f;
            if (p.AttackSpeedBonus != bonus) _players[id] = p with { AttackSpeedBonus = bonus };
        }
        foreach (int id in _warfrontIds) if (LibrarianOf(id) is { } psyker) psyker.CooldownScale = rt.CooldownScale(id);   // the cooldown cards on the Shockwave and the pull
        if (_tfw.Battle is not null && rules.SquadStep is { } squad && rules.LastFought != Tick - 1) squad();   // the field was empty last tick: TfwGuardsStep returned before the fight, so the squads still take their tick
        rt.TickPicker(now);   // an offer open for 20 s takes card 1; the protection ends with it
        bool aboard = _tfw.Phase is TfwRunPhase.Hub or TfwRunPhase.Travel;   // on the ship: the hub or the way down; every battle (a node, the HQ, the fortress) is the field
        foreach (int id in _warfrontIds)
        {
            if (!rt.TryKey(id, out ulong key) || rt.Campaign.Player(key) is not { PendingPicks: > 0 } || rt.Picker.Protected(key)) continue;
            if (!_players.TryGetValue(id, out var p) || !p.Alive) continue;
            bool lull = aboard;
            if (!aboard)
            {
                bool orks = _musou!.Query(p.Position, WarfrontProgressionRules.LullOrkDistance, _warfrontOne, WarfrontProgressionRules.LullOrkDistance) > 0;
                double quiet = Math.Min(p.SinceDamage, rt.SecondsSinceKill(key, now));
                lull = WarfrontPicker.IsLull(quiet, orks ? 0f : float.PositiveInfinity);
            }
            rt.Picker.TryOpen(key, now, aboard, lull, rt.RosterSizeOf(key), DreadnoughtRules.IsDreadnought(Config, p.Class));
        }
    }

    /// <summary>Each level-up since the last call refills that living player's health and shield to full (owner: "On level-up, refill HP and armor").</summary>
    void WarfrontRefill(WarfrontProgressionRuntime rt)
    {
        if (rt.Xp.LevelUps.Count == 0) return;
        foreach (var up in rt.Xp.Drain())
        {
            if (!rt.TryId(up.Key, out int id) || !_players.TryGetValue(id, out var p) || !p.Alive) continue;
            var body = Loadouts.Body(Config, p);
            _players[id] = p with { Health = body.HealthMax, Shield = body.ShieldMax };
        }
    }

    partial void WarfrontKill(in MusouKill kill)
    {
        if (_tfw is not { Battle: not null, Warfront: { } rules }) return;   // any battle of a Warfront run (a node, the HQ defence, the fortress) pays kills
        int points = _musou!.Rules.Of(kill.Type).Points;
        rules.Progression.Kill(kill.Enemy, kill.Player, kill.Type, points, ((long)Tick << 16) | kill.Enemy, _musou.Tick, Tick / (double)Math.Max(1, Config.TickRate), (uint)Tick);
    }

    partial void WarfrontDamageTaken(int target, ref float scale)
    {
        if (_tfw?.Warfront is not { Progression: { } rt } rules) return;
        if (rt.IsProtected(target) || rules.CoopHeld?.Invoke(target) == true) { scale = 0f; return; }
        var b = rt.BonusesOf(target);
        if (b.MaxHp == 1f && b.MaxArmor == 1f || !_players.TryGetValue(target, out var p)) return;
        scale *= WarfrontStatBonuses.DamageTaken(p.Shield, b);   // provisional approximation of max HP / max armour (spec)
    }

    partial void WarfrontInputLocked(int id, ref bool locked)
    {
        if (_tfw?.Warfront is { } rules && (rules.Progression.IsProtected(id) || rules.CoopHeld?.Invoke(id) == true)) locked = true;
    }

    partial void WarfrontMeleeCap(int attacker, ref float cap)
    {
        if (_tfw?.Warfront?.Progression is not { } rt) return;
        float d = rt.BonusesOf(attacker).Damage;
        if (d != 1f) cap *= d;   // the cards multiply on top of the x2 cap (interpretation)
    }

    partial void WarfrontAttackerScales(MusouSim musou)
    {
        if (_tfw?.Warfront?.Progression is not { } rt) return;
        foreach (int id in rt.Ids)
        {
            float d = rt.BonusesOf(id).Damage;
            if (d != 1f) musou.SetAttackerScale(id, d, d);
        }
    }

    partial void WarfrontAttackerScale(int id, ref float melee, ref float other)
    {
        if (_tfw?.Warfront?.Progression is not { } rt) return;
        float d = rt.BonusesOf(id).Damage;
        if (d == 1f) return;
        melee *= d;
        other *= d;
    }

    partial void WarfrontCooldown(int player, ref float seconds)
    {
        if (_tfw?.Warfront?.Progression is not { } rt) return;
        float s = rt.CooldownScale(player);
        if (s != 1f) seconds *= s;
    }

    // ---- the squad seams (W9) ---------------------------------------------------------------------------------------------------------------

    partial void WarfrontTargets(ref MusouTarget[] targets, ref int n)
    {
        if (_tfw?.Warfront?.SquadTargets is not { } source) return;
        int most = WarfrontBattleRules.MaxSquadTargets;
        if (targets.Length < n + most) Array.Resize(ref targets, (n + most) * 2);
        n += Math.Clamp(source(targets.AsSpan(n, most)), 0, most);
    }

    partial void WarfrontSquadHit(in MusouAttack attack, ref bool handled)
    {
        if (attack.Player > WarfrontBattleRules.SquadIdFirst || attack.Player < WarfrontBattleRules.SquadIdLast || _tfw?.Warfront is not { } rules) return;
        handled = true;   // never the Guardsmen's
        if (rules.SquadHit is not { } hit) return;
        float damage = attack.Damage * rules.DamageScaleOf(attack.Enemy);
        hit(attack.Player, damage, attack.From);
    }

    partial void WarfrontFought()
    {
        if (_tfw?.Warfront is not { } rules) return;
        rules.LastFought = Tick;
        rules.SquadStep?.Invoke();
    }
}
