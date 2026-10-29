using System.Numerics;

namespace SmSim;

/// <summary>A charge that connected in the last tick (diagnostics, tests, client effects).</summary>
/// <param name="Harmed">The connected player could be harmed (an enemy, or anyone with friendly fire): it took the hit.</param>
public readonly record struct RushEvent(uint Tick, int Player, int Target, bool Harmed);

/// <summary>
/// The charge in the world (M29; rules **interpretation**, spec/behavior.md "Charge"): each tick a rush still connecting
/// (<see cref="Rush.Connecting"/>) tests its move this tick against the other living players as its client saw them (rewound
/// like melee) and visible from its eye (a clear line to the chest through weapon collision, as melee): the first one it reaches
/// (<see cref="Rush.Contact"/>) stops it (<see cref="Rush.Stopped"/>: the collision stop). As for melee and shots, the charge acts
/// with its state from this tick's step: a charger hit, stunned or killed by an earlier resolution in the same tick still deals
/// its contact (two chargers meeting head on both hit), and only the stop is skipped when its phase already changed. The stops
/// are applied once every charge of the tick has resolved (M32, <see cref="StopRushes"/>), so a charger is still in its rush
/// (and its hyper armour, <see cref="Rush.HyperArmour"/>) for every contact of that tick whatever the player order.
/// If the attacker may harm that player it takes the rush's primary hit (shield part, then the damage) and reaction (the stun,
/// unless hyper armour or flight; <see cref="TryReact"/>), and every other living player it may harm within the ability's reach
/// in front (the melee cone, a clear line from the eye) the secondary hit; an ally (without friendly fire) only stops it.
/// </summary>
public sealed partial class World
{
    readonly List<RushEvent> _rushes = new();

    /// <summary>Charges that connected during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<RushEvent> LastRushes => _rushes;

    /// <summary>One charge's contact this tick (see the class summary); returns whether it connected (its stop is <see cref="StopRushes"/>'s).</summary>
    bool ResolveRush(int attacker, PlayerInput input, PlayerState before, PlayerState state)
    {
        if (Rush.SpecOf(Config, state) is not { } spec) return false;   // protocol 30: the dash attack's lunge too
        var seen = SeenBy(input);
        var eye = state.Position + new Vector3(0, 0, Config.EyeHeight);
        int target = -1; float first = float.MaxValue;
        foreach (var (id, then) in seen)
        {
            if (id == attacker || !then.Alive || !_players.TryGetValue(id, out var current) || !current.Alive) continue;
            if (Rush.Contact(before.Position, state.Position, state.Yaw, then.Position, spec, Config) is not float t || t > first || (t == first && id > target)) continue;
            if (Collision is not null)
            {
                var toChest = then.Position + new Vector3(0, 0, ChestOf(then)) - eye;
                float length = toChest.Length();
                if (length > 1e-4f && Collision.Weapons.Raycast(eye, toChest / length, length) is { } wall && wall.Distance < length) continue;   // behind a wall
            }
            first = t; target = id;
        }
        if (target < 0) return false;
        bool harmed = Harms(attacker, target);
        _rushes.Add(new RushEvent(Tick, attacker, target, harmed));
        if (!harmed) return true;
        RushHit(attacker, target, state, spec.Primary, spec.PrimaryReaction);
        // Others in reach in front: the secondary hit.
        var forward = new Vector2(-MathF.Sin(state.Yaw), MathF.Cos(state.Yaw));
        float minCos = MathF.Cos(Config.MeleeConeDegrees * MathF.PI / 180f);
        var origin = state.Position + new Vector3(0, 0, Config.EyeHeight);
        foreach (var (id, then) in seen.OrderBy(kv => kv.Key))
        {
            if (id == attacker || id == target || !then.Alive || !_players.TryGetValue(id, out var current) || !current.Alive || !Harms(attacker, id)) continue;
            var offset = then.Position - state.Position;
            var flat = new Vector2(offset.X, offset.Y);
            float distance = flat.Length();
            if (distance > spec.Range || MathF.Abs(offset.Z) > Config.MeleeVerticalReach) continue;
            if (distance > 1e-4f && Vector2.Dot(flat / distance, forward) < minCos) continue;
            if (Collision is not null)
            {
                var toChest = then.Position + new Vector3(0, 0, ChestOf(then)) - origin;
                float length = toChest.Length();
                if (length > 1e-4f && Collision.Weapons.Raycast(origin, toChest / length, length) is { } wall && wall.Distance < length) continue;
            }
            RushHit(attacker, id, state, spec.Secondary, spec.SecondaryReaction);
        }
        return true;
    }

    /// <summary>
    /// The collision stop of every charger that connected this tick, after all contacts resolved (M32), unless a resolution
    /// already changed its phase (a reaction, death).
    /// </summary>
    void StopRushes(IEnumerable<int> chargers)
    {
        // feel/jump-pack-2 (the review): a charger a wall stopped this tick (0 ticks into its wall stop) that also connected takes the hit's stop, not the wall's.
        foreach (int id in chargers)
            if (_players.TryGetValue(id, out var now) && now.Alive && Rush.Charging(Rush.ChargingPhase(now)))
                _players[id] = Rush.Stopped(now with { Rush = Rush.ChargingPhase(now) });   // the dash attack: its hit state
    }

    /// <summary>One charge hit: the shield part, then the damage on the body (as a blast's), recorded as melee; then the reaction.</summary>
    void RushHit(int attacker, int target, PlayerState state, DamageSpec damage, ReactionEntry reaction)
    {
        var victim = _players[target];
        var before = victim;
        bool armoured = victim.Shield > 0f;   // before this hit (hit feedback, protocol 22)
        // M43: the target's received-damage perks (the charge is melee unless its file says otherwise).
        var type = damage.Type == DamageType.Unknown ? DamageType.Melee : damage.Type;
        float received = Loadouts.Body(Config, victim).Received(type) * DamageScaleOf(attacker)   // test aid: --bot-damage-scale
            * Stims.MeleeDealt(Config, state);   // perks/inert-2: the charger's combat stims
        float shieldOnly = MathF.Min(victim.Shield, MathF.Max(0f, damage.ShieldDamage * received));
        if (shieldOnly > 0f) victim = victim with { Shield = victim.Shield - shieldOnly, SinceDamage = 0f };
        var (after, shieldDamage, healthDamage) = Combat.ApplyDamage(victim, damage.Damage * received, damage.ShieldBodyMultiplier,
            damage.BodyMultiplier * Stims.HealthTaken(Config, before, type), Config);   // perks/inert-2: the target's stims, health part only
        _players[target] = after;
        ShieldBroke(target, before, after, shieldOnly + shieldDamage);
        var push = after.Position - state.Position;
        var flat = new Vector3(push.X, push.Y, 0f);
        var direction = flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : new Vector3(-MathF.Sin(state.Yaw), MathF.Cos(state.Yaw), 0f);
        _hits.Add(new HitEvent(Tick, attacker, target, HitZone.Body, Vector3.Distance(after.Position, state.Position), shieldOnly + shieldDamage, healthDamage, !after.Alive,
            Melee: true, Direction: direction, Armoured: armoured));
        if (after.Alive) TryReact(attacker, target, reaction, state.Position, new Vector2(direction.X, direction.Y));
    }
}
