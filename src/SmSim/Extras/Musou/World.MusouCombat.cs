using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the players' attacks on the enemies and the Warboss's on the players (MU3; design section B; rules ours unless the design names
/// them, <see cref="MusouCombatRules"/>, <see cref="MusouBossRules"/>). Everything here runs only from the Musou hooks (<see cref="_musou"/> set), after the
/// core resolved the same attacks against the players and the level, and only in play; the players' results are never changed (a tracer's end and hit kind
/// are, for the effects). No lag compensation (local play; ours).
/// <list type="bullet">
/// <item>Hit-scan rays (<see cref="ShotEvent"/> with an origin, no projectile): tested from the origin to where the core ended them against the enemies
/// (<see cref="MusouSim.Raycast"/>: a body cylinder and a head sphere); the nearest takes the shot's damage at its distance
/// (<see cref="Combat.DamageAt(float, WeaponSpec)"/>, or a charged stage's) times the weapon's head or body multiplier, and the tracer ends on it. The weapon
/// is the one the shooter carries (its perk variant, as the core fired it).</item>
/// <item>Cones: every enemy the cone covers (<see cref="MusouSim.ConeCoverage"/>) with a clear line from the eye to its chest takes the cone's damage times
/// its fall-off and coverage multiplier (<see cref="Cones.Multiplier"/>), on the body, at most <see cref="MusouCombatRules.ConeTargets"/> nearest first;
/// the shot's hit kind becomes a body hit.</item>
/// <item>Projectiles: each flying one's move this tick (from where it was after the last tick) is tested against the enemies; the first it meets takes its
/// direct damage (and its detonation damage); one that detonates on contact goes off there (its blast through <see cref="ExtrasBlast"/>), any other stops
/// there (a rigid body glances off as off a player) and never strikes that enemy again.</item>
/// <item>Blasts (<see cref="ExtrasBlast"/>): every enemy whose chest is within the radius (and in sight with LineOfSight) takes the splash's damage times
/// <see cref="Combat.SplashIntensity"/>, on the body; from <see cref="MusouCombatRules.BlastStaggerIntensity"/> it staggers and is knocked back up to
/// <see cref="MusouCombatRules.BlastKnockback"/> by the intensity. A blind-only blast does nothing to them.</item>
/// <item>The cleave (<see cref="ExtrasMelee"/>): every enemy within the core's melee range plus <see cref="MusouCombatRules.CleaveExtraRange"/> and the cone
/// times <see cref="MusouCombatRules.CleaveConeScale"/> (feet to feet, the core's vertical reach, a clear line from the eye), at most
/// <see cref="MusouCombatRules.CleaveTargets"/> nearest first, takes the step's full damage; a heavy step (<see cref="MusouSim.Heavy"/>) also staggers and
/// knocks back <see cref="MusouCombatRules.HeavyKnockback"/>. With a stomp in the same step an enemy in both takes the larger of the two (lead's rule).</item>
/// <item>Charges: a charge connecting this tick (as the core's: still connecting, or ended this tick by a wall, a landing or a player) tests its move against the enemies (within the two radii plus its contact margin, the core's vertical
/// reach); each it meets takes the charge's primary damage once, staggers and is knocked back, at most <see cref="MusouCombatRules.ChargeTargets"/> a
/// charge. The charge does not stop on them (ours).</item>
/// <item>The Warboss (<see cref="MusouBoss"/>): its slam's hits as an enemy's staggering strike, its charge's with the core's stagger reaction and a knockback.</item>
/// </list>
/// Every KO is the attacking player's (<see cref="MusouKill"/>: the kill on its score and the director's points).
/// </summary>
public sealed partial class World
{
    MusouBoss? _musouBoss;
    readonly List<MusouBossStrike> _musouBossStrikes = new();
    readonly Dictionary<int, Vector3> _musouProjectileFrom = new();       // projectile id: where it was after the last tick
    readonly Dictionary<int, ushort> _musouProjectileStruck = new();      // projectile id: the enemy it last struck without going off
    readonly Dictionary<int, HashSet<ushort>> _musouCharged = new();      // player id: the enemies its charge under way has hit
    readonly Dictionary<ushort, float> _musouStomped = new();             // enemy id: what the stomp of the step under way dealt it
    uint _musouStompTick;                                                  // … that stomp's tick and attacker
    int _musouStompBy;
    readonly List<int> _musouStale = new();
    readonly ushort[] _musouNear = new ushort[MusouSim.Capacity];         // one Query's ids at a time: never Query into it while a loop over it runs

    /// <summary>The Warboss's abilities this round (null before he comes).</summary>
    public MusouBoss? MusouBoss => _musouBoss;

    /// <summary>The Musou events of this world tick: the first Musou hook of a tick starts it (a cleave or a blast comes before <c>ExtrasStep</c>).</summary>
    void MusouBeginTick()
    {
        if (_musou!.Tick != Tick) _musou.BeginTick(Tick);
    }

    /// <summary>The players' shots (rays, cones), projectiles and charges of this tick against the enemies (in play only).</summary>
    void MusouPlayerShots()
    {
        var musou = _musou!;
        for (int k = 0; k < _shots.Count; k++)
        {
            var shot = _shots[k];
            if (shot.Projectile != 0 || shot.Weapon < 0 || shot.Weapon >= Config.Weapons.Count) continue;
            var weapon = MusouWeaponOf(shot);
            var ray = shot.End - shot.Origin;
            float length = ray.Length();
            if (!(length > 1e-3f)) continue;
            var direction = ray / length;
            if (shot.Stage == 0 && weapon.Delivery == DeliveryKind.Cone)
            {
                if (weapon.Cone is { } cone && MusouCone(shot.Shooter, shot.Origin, direction, cone) && shot.Hit is ShotHit.None or ShotHit.World)
                    _shots[k] = shot with { Hit = ShotHit.Body, Surface = -1, Target = 0, Armoured = false };
                continue;
            }
            ushort enemy = musou.Raycast(shot.Origin, direction, length, out float distance, out bool head);
            if (enemy == 0) continue;
            var spec = Shots.ChargeShotOf(weapon, shot.Stage)?.Damage ?? weapon.DirectDamage with { Damage = Combat.DamageAt(distance, weapon) };
            float damage = spec.Damage * (head ? spec.HeadMultiplier : spec.BodyMultiplier);
            musou.Damage(enemy, shot.Shooter, damage, direction, out _, marker: head ? (byte)2 : (byte)1, cause: MusouDeathKind.Shot);
            // The tracer ends on the enemy (the wire carries the end and the hit kind; no player is named).
            _shots[k] = shot with { End = shot.Origin + direction * distance, Hit = head ? ShotHit.Head : ShotHit.Body, Surface = -1, Target = 0, Armoured = false };
        }
        MusouProjectiles();
        MusouCharges();
    }

    /// <summary>The weapon a shot left as its shooter carries it (the perk variant the core fired, <see cref="Loadouts.InHand"/>), else the config's.</summary>
    WeaponSpec MusouWeaponOf(in ShotEvent shot) =>
        _players.TryGetValue(shot.Shooter, out var s) && Loadouts.WeaponIndex(Config, s, s.Slot) == shot.Weapon && Loadouts.InHand(Config, s) is { } held
            ? held : Config.Weapons[shot.Weapon];

    /// <summary>A cone shot on the enemies (see the class summary); whether it hit any.</summary>
    bool MusouCone(int shooter, Vector3 origin, Vector3 direction, ConeSpec cone)
    {
        var musou = _musou!;
        int n = musou.Query(origin, cone.Length + 2f, _musouNear, verticalReach: cone.Length + 2f);
        bool any = false;
        int covered = 0;
        for (int k = 0; k < n && covered < musou.Rules.Combat.ConeTargets; k++)
        {
            if (!musou.TryGet(_musouNear[k], out var e)) continue;
            var (radius, height) = musou.Body(e.Type);
            float coverage = MusouSim.ConeCoverage(cone, origin, direction, e.Position, radius, height);
            if (coverage <= 0f) continue;
            var chest = MusouSim.Chest(e.Position, height);
            if (Blocked(origin, chest)) continue;
            float distance = Vector3.Distance(origin, chest);
            float multiplier = Cones.Multiplier(cone, distance, coverage);
            if (multiplier <= 0f) continue;
            covered++;
            if (musou.Damage(e.Id, shooter, cone.Damage.Damage * multiplier * cone.Damage.BodyMultiplier, chest - origin, out _, marker: 1, cause: MusouDeathKind.Shot)) any = true;
        }
        return any;
    }

    /// <summary>Each flying projectile's move this tick against the enemies (see the class summary).</summary>
    void MusouProjectiles()
    {
        if (_projectiles.Count == 0)
        {
            if (_musouProjectileFrom.Count > 0) _musouProjectileFrom.Clear();
            if (_musouProjectileStruck.Count > 0) _musouProjectileStruck.Clear();
            return;
        }
        var musou = _musou!;
        float dt = Config.TickSeconds;
        // Newest first: a detonation removes only its own entry, which this loop has then passed.
        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];
            if (p.Motion != ProjectileMotion.Flying) continue;
            int id = p.Id;
            var from = _musouProjectileFrom.TryGetValue(id, out var was) ? was : p.Position - p.Velocity * dt;   // launched this tick: back along its flight
            var segment = p.Position - from;
            float length = segment.Length();
            if (!(length > 1e-4f)) continue;
            var direction = segment / length;
            ushort enemy = musou.Raycast(from, direction, length, out float distance, out bool head);
            if (enemy == 0 || (_musouProjectileStruck.TryGetValue(id, out var struck) && struck == enemy)) continue;
            var point = from + direction * distance;
            var spec = SpecOf(p);
            if (!p.Thrown && Shots.DirectDamageOf(WeaponOf(p), p.Stage) is { } direct)
                musou.Damage(enemy, p.Owner, direct.Damage * (head ? direct.HeadMultiplier : direct.BodyMultiplier), direction, out _, marker: head ? (byte)2 : (byte)1, cause: MusouDeathKind.Shot);
            if (spec.DetonateOnCollision)
            {
                if (spec.DetonateDamage is { } blast) musou.Damage(enemy, p.Owner, blast.Damage * blast.BodyMultiplier, direction, out _, cause: MusouDeathKind.Blast);
                Detonate(id, point, -1, HitZone.None);   // its splash reaches the enemies through ExtrasBlast
                continue;
            }
            _musouProjectileStruck[id] = enemy;
            var velocity = Vector3.Zero;
            if (spec.Rigid is { } rigid && !spec.Sticky && musou.TryGet(enemy, out var e))
            {
                // Off the enemy's body as off a player's (entity reductions).
                var away = new Vector3(point.X - e.Position.X, point.Y - e.Position.Y, 0f);
                var normal = away.LengthSquared() > 1e-8f ? Vector3.Normalize(away) : new Vector3(-direction.X, -direction.Y, 0f);
                if (normal.LengthSquared() > 1e-8f)
                    velocity = Keep(p.Velocity - 2f * Vector3.Dot(p.Velocity, normal) * normal, rigid.EntityHorizontalReduction, rigid.EntityVerticalReduction);
            }
            _projectiles[i] = p with { Position = point, Velocity = velocity, Travelled = p.Travelled - (length - distance) };
        }
        foreach (var p in _projectiles) _musouProjectileFrom[p.Id] = p.Position;
        Prune(_musouProjectileFrom);
        Prune(_musouProjectileStruck);

        void Prune<T>(Dictionary<int, T> of)
        {
            if (of.Count <= _projectiles.Count) return;
            _musouStale.Clear();
            foreach (int id in of.Keys) if (IndexOf(id) < 0) _musouStale.Add(id);
            foreach (int id in _musouStale) of.Remove(id);
        }
    }

    /// <summary>
    /// The charges this tick against the enemies (see the class summary): each charger's move this tick in the charging state the core tested its contacts
    /// with: still connecting, or the move that ended in a wall stop, a landing or a stop on a player (as the core's own list in <c>World.Step</c>).
    /// </summary>
    void MusouCharges()
    {
        _history.TryGetValue(Tick - 1, out var start);   // every player as this tick began
        bool any = _rushes.Count > 0 || _musouCharged.Count > 0;
        foreach (var p in _players.Values) any |= p.Rush != RushPhase.None;
        if (!any && start is not null) foreach (var p in start.Values) any |= p.Rush != RushPhase.None;
        if (!any) return;
        var musou = _musou!;
        var rules = musou.Rules.Combat;
        foreach (var (id, p) in _players)
        {
            PlayerState? before = start is not null && start.TryGetValue(id, out var b) ? b : null;
            PlayerState? charging = null;
            // As the core's: a charger killed earlier this tick still deals its contact (RushWorld), so no alive test here.
            if (Rush.Connecting(p, Config)) charging = p;
            else if (before is { } wallFrom && Rush.WallStoppedFrom(wallFrom, p, Config) is { } wall && Rush.Connecting(wall, Config)) charging = wall;
            else if (before is { } landFrom && Rush.LandedFrom(landFrom, p) is { } landed && Rush.Connecting(landed, Config)) charging = landed;
            else if (_rushes.Exists(r => r.Player == id)) charging = p;   // stopped on a player this tick
            if (charging is not { } state || Rush.SpecOf(Config, state) is not { } spec)
            {
                _musouCharged.Remove(id);
                continue;
            }
            if (!_musouCharged.TryGetValue(id, out var hit)) _musouCharged[id] = hit = new HashSet<ushort>();
            var from = before?.Position ?? p.Position;
            var mid = (from + p.Position) * 0.5f;
            float half = Vector3.Distance(from, p.Position) * 0.5f;
            int n = musou.Query(mid, half + 2f, _musouNear, Config.MeleeVerticalReach);
            var line = new Vector2(p.Position.X - from.X, p.Position.Y - from.Y);
            var forward = line.LengthSquared() > 1e-8f ? Vector2.Normalize(line) : new Vector2(-MathF.Sin(p.Yaw), MathF.Cos(p.Yaw));
            for (int k = 0; k < n && hit.Count < rules.ChargeTargets; k++)
            {
                if (hit.Contains(_musouNear[k]) || !musou.TryGet(_musouNear[k], out var e)) continue;
                var (radius, _) = musou.Body(e.Type);
                if (MusouSim.FlatDistanceToSegment(e.Position, from, p.Position) > radius + Config.CapsuleRadius + spec.ContactMargin) continue;
                hit.Add(e.Id);
                var side = new Vector2(e.Position.X - p.Position.X, e.Position.Y - p.Position.Y);
                var push = forward + (side.LengthSquared() > 1e-8f ? Vector2.Normalize(side) * 0.5f : Vector2.Zero);
                float damage = spec.Primary.Damage * spec.Primary.BodyMultiplier * Stims.MeleeDealt(Config, p);
                musou.Damage(e.Id, id, damage, new Vector3(push, 0f), out bool killed, marker: 3, cause: MusouDeathKind.Melee);
                if (!killed) musou.Stagger(e.Id, push, rules.HeavyKnockback);
            }
        }
        if (_musouCharged.Count > 0)
        {
            _musouStale.Clear();
            foreach (int id in _musouCharged.Keys) if (!_players.ContainsKey(id)) _musouStale.Add(id);
            foreach (int id in _musouStale) _musouCharged.Remove(id);
        }
    }

    /// <summary>The cleave of one melee step (see the class summary).</summary>
    void MusouCleave(int attacker, PlayerState state, MeleeStep step)
    {
        if (_state != MatchState.Playing) return;
        MusouBeginTick();
        var musou = _musou!;
        var rules = musou.Rules.Combat;
        bool heavy = MusouSim.Heavy(state, step);
        float damage = step.Damage * step.HealthMultiplier * Stims.MeleeDealt(Config, state);
        if (!(damage > 0f) && !heavy) return;
        float range = Config.MeleeRange + rules.CleaveExtraRange;
        float minCos = MathF.Cos(MathF.Min(180f, Config.MeleeConeDegrees * rules.CleaveConeScale) * MathF.PI / 180f);
        var forward = new Vector2(-MathF.Sin(state.Yaw), MathF.Cos(state.Yaw));
        var eye = state.Position + new Vector3(0, 0, Config.EyeHeight);
        int n = musou.Query(state.Position, range, _musouNear, Config.MeleeVerticalReach);
        int struck = 0;
        for (int k = 0; k < n && struck < rules.CleaveTargets; k++)
        {
            if (!musou.TryGet(_musouNear[k], out var e)) continue;
            var flat = new Vector2(e.Position.X - state.Position.X, e.Position.Y - state.Position.Y);
            float distance = flat.Length();
            if (distance > 1e-4f && Vector2.Dot(flat / distance, forward) < minCos) continue;   // overlapping counts as in front, as the core's
            if (Blocked(eye, MusouSim.Chest(e.Position, musou.Body(e.Type).Height))) continue;
            struck++;
            var push = distance > 1e-4f ? flat / distance : forward;
            bool killed = false;
            // The lead's rule (MU3a review): an enemy inside the step's stomp and its cleave takes the larger of the two, not both.
            float stomped = _musouStompTick == Tick && _musouStompBy == attacker ? _musouStomped.GetValueOrDefault(e.Id) : 0f;
            if (damage > stomped) musou.Damage(e.Id, attacker, damage - stomped, new Vector3(push, 0f), out killed, marker: 3, cause: MusouDeathKind.Melee);
            if (heavy && !killed) musou.Stagger(e.Id, push, rules.HeavyKnockback);
        }
        _musouStomped.Clear();
    }

    /// <summary>A blast on the enemies (see the class summary).</summary>
    void MusouBlast(int owner, Vector3 point, SplashSpec splash, bool melee)
    {
        if (_state != MatchState.Playing || splash.BlindOnly || !(splash.Radius > 0f)) return;
        MusouBeginTick();
        var musou = _musou!;
        var rules = musou.Rules.Combat;
        int n = musou.Query(point, splash.Radius, _musouNear, verticalReach: splash.Radius + 3f);
        for (int k = 0; k < n; k++)
        {
            if (!musou.TryGet(_musouNear[k], out var e)) continue;
            var chest = MusouSim.Chest(e.Position, musou.Body(e.Type).Height);
            float distance = Vector3.Distance(chest, point);
            if (distance > splash.Radius) continue;
            if (splash.LineOfSight && Blocked(point, chest)) continue;
            float intensity = Combat.SplashIntensity(splash.FallOff, distance);
            if (!(intensity > 0f)) continue;
            var away = new Vector2(e.Position.X - point.X, e.Position.Y - point.Y);
            var push = away.LengthSquared() > 1e-8f ? Vector2.Normalize(away) : new Vector2(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw));
            float damage = splash.Damage.Damage * intensity * splash.Damage.BodyMultiplier;
            bool killed = false;
            if (damage > 0f) musou.Damage(e.Id, owner, damage, new Vector3(push, 0.3f), out killed, cause: melee ? MusouDeathKind.Melee : MusouDeathKind.Blast);
            if (melee && damage > 0f)
            {
                // A stomp: the step's cleave follows at once (ResolveMelee) and gives each enemy only what it adds to this.
                if (_musouStompTick != Tick || _musouStompBy != owner) { _musouStomped.Clear(); _musouStompTick = Tick; _musouStompBy = owner; }
                _musouStomped[e.Id] = _musouStomped.GetValueOrDefault(e.Id) + damage;
            }
            if (!killed && intensity >= rules.BlastStaggerIntensity) musou.Stagger(e.Id, push, rules.BlastKnockback * MathF.Min(1f, intensity));
        }
    }

    /// <summary>
    /// The Warboss's abilities this tick, after the enemies' step; their hits on the players. <paramref name="targets"/> are brought up to date first: a
    /// player the horde killed this tick is neither struck nor reported struck.
    /// </summary>
    void MusouBossStep(Span<MusouTarget> targets, bool fight)
    {
        var director = _musouDirector!;
        if (director.BossId == 0) { _musouBoss = null; return; }
        for (int k = 0; k < targets.Length; k++)
            if (targets[k].Alive && !(_players.TryGetValue(targets[k].Id, out var now) && now.Alive)) targets[k] = targets[k] with { Alive = false };
        if (_musouBoss is null || _musouBoss.Id != director.BossId)
            _musouBoss = new MusouBoss(_musou!, director, director.BossId, director.BossMaxHealth, Config.CapsuleRadius);
        _musouBossStrikes.Clear();
        _musouBoss.Step(targets, fight, _musouBossStrikes);
        foreach (var strike in _musouBossStrikes)
        {
            if (!_players.TryGetValue(strike.Player, out var before) || !before.Alive) continue;
            MusouHit(new MusouAttack(_musouBoss.Id, strike.Player, strike.Damage, Melee: true, Staggers: strike.Knockback <= 0f, strike.From));
            // The charge: the core's stagger with a knockback, when the hit landed (an enemy's damage scale of 0 hurts nobody and throws nobody).
            if (strike.Knockback > 0f && _players[strike.Player] is { Alive: true } after && (after.Health < before.Health || after.Shield < before.Shield))
                TryReact(0, strike.Player, new ReactionEntry(0f, ReactionKind.Stagger, Backward: strike.Knockback, Duration: _musou!.Rules.Boss.ChargeKnockbackSeconds),
                    strike.From, strike.Push);
        }
    }
}
