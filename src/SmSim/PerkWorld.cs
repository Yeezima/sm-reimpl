using System.Numerics;

namespace SmSim;

/// <summary>A perk's blast waiting in the world (M43): Final Vengeance's after a death, Reactive Blast's after an armour break.</summary>
/// <param name="Due">The tick it goes off in.</param>
/// <param name="Perk">The perk it comes from (index into <see cref="SimConfig.Perks"/>, -1 unknown): the kill feed names it.</param>
public readonly record struct PerkBlast(uint Due, int Owner, Vector3 Point, SplashSpec Splash, int Perk);

/// <summary>
/// The world side of the perks (M43; rules **interpretation** where noted, spec/behavior.md "Perks"). Player-blueprint upgrades
/// (<see cref="PlayerUpgrade"/>): an armour shield that falls from above 0 to 0 sets off the owner's <see cref="PlayerUpgrade.ShieldBreakBlasts"/>
/// at its chest in the same tick (after the hit that broke it; Reactive Blast); a death sets off <see cref="PlayerUpgrade.DeathBlasts"/>
/// <see cref="PlayerUpgrade.DeathBlastDelay"/> seconds later where the player died (Final Vengeance). Melee hits heal the attacker
/// (<see cref="MeleeStep.Heal"/>). A jump pack's <see cref="JumpPackSpec.TakeOffBlasts"/> go off at the lift-off (Blast Off). Blasts
/// apply like a projectile's (<see cref="ApplyBlast"/>; friendly fire and the match's end as any blast) and honour their
/// <see cref="SplashSpec.TargetFilter"/>: <c>target_enemies_and_neutrals</c> spares the owner's allies. Their kills are perk kills
/// (<see cref="KillCause.Perk"/>).
/// </summary>
public sealed partial class World
{
    readonly List<PerkBlast> _perkBlasts = new();

    /// <summary>Perk blasts waiting to go off (server only; not on the wire).</summary>
    public IReadOnlyList<PerkBlast> PendingPerkBlasts => _perkBlasts;

    /// <summary>Perk blasts that went off during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<PerkBlast> LastPerkBlasts => _firedPerkBlasts;
    readonly List<PerkBlast> _firedPerkBlasts = new();

    /// <summary>The filter of a blast that spares the owner's allies (the take-off blast's).</summary>
    public const string EnemiesOnly = "target_enemies_and_neutrals";

    /// <summary>
    /// After damage: when <paramref name="target"/>'s armour shield broke (it was above 0 and the hit took it all:
    /// <paramref name="shieldTaken"/>; a kill zeroes the shield without breaking it), its armour-break blasts are queued for this tick.
    /// </summary>
    void ShieldBroke(int target, PlayerState before, PlayerState after, float shieldTaken)
    {
        if (before.Shield <= 0f || after.Shield > 0f || shieldTaken < before.Shield - 1e-4f || Config.PlayerUpgrades.Count == 0) return;
        var centre = after.Position + new Vector3(0, 0, ChestOf(after));
        foreach (var u in Loadouts.PlayerUpgradesOf(Config, after))
            foreach (var blast in u.ShieldBreakBlasts) _perkBlasts.Add(new PerkBlast(Tick, target, centre, blast, Loadouts.PerkGranting(Config, after, u.Upgrade)));
    }

    /// <summary>A melee hit's heal (M43): the attacker, alive and below its maximum health, gets <paramref name="amount"/> back (up to the maximum).</summary>
    void Heal(int attacker, float amount)
    {
        if (amount <= 0f || !_players.TryGetValue(attacker, out var a) || !a.Alive) return;
        float max = Loadouts.Body(Config, a).HealthMax;
        if (a.Health >= max * 0.9999f) return;   // the data's prerequisite: health at most 0.9999 of the maximum
        _players[attacker] = a with { Health = MathF.Min(max, a.Health + amount) };
    }

    /// <summary>
    /// Sets off every perk blast due by this tick (those queued in it included), in the order queued; a blast that breaks another
    /// shield queues that one's for this tick too (at most <see cref="MaxPerkBlastRounds"/> rounds, so chains end).
    /// </summary>
    void ApplyPerkBlasts()
    {
        _firedPerkBlasts.Clear();
        for (int round = 0; round < MaxPerkBlastRounds && _perkBlasts.Count > 0; round++)
        {
            var due = _perkBlasts.Where(b => b.Due <= Tick).ToList();
            if (due.Count == 0) break;
            _perkBlasts.RemoveAll(b => b.Due <= Tick);
            foreach (var b in due)
            {
                ApplyBlast(b.Owner, 0, b.Point, b.Splash, filtered: true, perk: b.Perk);
                _firedPerkBlasts.Add(b);
            }
        }
    }

    /// <summary>Most rounds of armour-break blasts one tick resolves.</summary>
    public const int MaxPerkBlastRounds = 4;

    /// <summary>After the tick's deaths: every player who died in it with a death blast queues it where it died.</summary>
    void ScheduleDeathBlasts(List<int> aliveBefore)
    {
        if (Config.PlayerUpgrades.Count == 0) return;
        foreach (int id in aliveBefore)
        {
            if (!_players.TryGetValue(id, out var dead) || dead.Alive) continue;
            var centre = dead.Position + new Vector3(0, 0, ChestOf(dead));
            foreach (var u in Loadouts.PlayerUpgradesOf(Config, dead))
            {
                uint delay = (uint)Math.Max(0, (int)MathF.Round(MathF.Max(0f, u.DeathBlastDelay) * Config.TickRate));
                foreach (var blast in u.DeathBlasts) _perkBlasts.Add(new PerkBlast(Tick + Math.Max(1u, delay), id, centre, blast, Loadouts.PerkGranting(Config, dead, u.Upgrade)));
            }
        }
    }

    /// <summary>A jump pack's lift-off blasts (M43, Blast Off), at the player's chest; recorded as the perk's hits.</summary>
    void TakeOffBlasts(int player, PlayerState state)
    {
        if (Loadouts.JumpPack(Config, state) is not { TakeOffBlasts.Count: > 0 } pack) return;
        // The perk: the player's perk whose string a pack variant with take-off blasts needs.
        int perk = -1;
        if (Loadouts.JumpPack(Config, state.Class) is { } basePack)
            foreach (var v in basePack.Variants)
                if (v.Spec.TakeOffBlasts.Count > 0)
                    foreach (var u in v.Upgrades) if (Loadouts.PerkGranting(Config, state, u) is var p and >= 0) { perk = p; break; }
        var centre = state.Position + new Vector3(0, 0, ChestOf(state));
        foreach (var blast in pack.TakeOffBlasts) ApplyBlast(player, 0, centre, blast, filtered: true, perk: perk);
    }

    /// <summary>Whether a blast of <paramref name="owner"/> with <paramref name="splash"/>'s filter may reach <paramref name="target"/> (M43).</summary>
    bool BlastReaches(int owner, int target, SplashSpec splash)
    {
        if (splash.TargetFilter != EnemiesOnly || owner == target) return true;
        return !_players.TryGetValue(owner, out var a) || !_players.TryGetValue(target, out var t) || Hostile(a.Team, t.Team);
    }
}
