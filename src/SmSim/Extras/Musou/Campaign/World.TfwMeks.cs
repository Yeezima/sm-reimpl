using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Expansion S4 in the World (Musou Mode battles only): the Scrap Meks' step with what only the World has, and the contraptions in the players' attacks (<see cref="MusouMekRules"/>, <see cref="MusouMeks"/>).
/// <list type="bullet">
/// <item>The step (<see cref="TfwMeksStep"/>, after S2's, in play): the battle's Meks with the players and Guard squads as targets, the level's sight for the turrets and the Guardsmen for the traps;
/// then each trap set off hurts the Mek's enemies in its radius (a player or AI Marine <see cref="MusouMekRules.TrapShare"/> of his maximum health and armour, a Guardsman the same flat amount of
/// a standard Marine's; Orks spared), and each trap a player destroyed goes off as an Ork blast credited to him (<see cref="MusouMekRules.TrapBlast"/>: it hurts Orks).</item>
/// <item>The players' attacks: a hit-scan shot stops at a dome it enters from outside, and strikes the first contraption on its way (<see cref="TfwMekShot"/>); a blast or a cone from outside a
/// dome does not reach in (<see cref="TfwShielded"/>); blasts, blows and Ork blasts damage the contraptions in their reach.</item>
/// </list>
/// The 5-wave run and the skirmish have no battle, so nothing here runs for them.
/// </summary>
public sealed partial class World
{
    readonly List<MusouTrapTrigger> _mekTriggers = new();
    readonly List<MusouTrapBlast> _mekBlasts = new();
    readonly List<(byte Id, float Distance)> _mekNear = new();
    MusouTarget[] _mekTargets = new MusouTarget[64];
    Func<Vector3, Vector3, bool>? _mekBlocked;
    Func<Vector3, float, bool>? _mekGuardsNear;
    readonly HashSet<ushort> _mekDemoMoved = new();

    /// <summary>The Meks of the battle under way (null: no battle, or not Musou Mode).</summary>
    public MusouMeks? TfwMeks => _tfw?.Battle?.Meks;

    /// <summary>Whether the battles have Scrap Meks (ours: always; the battle tests that count or place the Orks turn it off, as they do the patrols).</summary>
    public bool TfwMeksOn { get; set; } = true;

    /// <summary>Capture aid (ours; the server's <c>SM_MUSOU_S4_DEMO=1</c>): each Mek is moved once to a place 24 to 32 m in front of the first living human, spread across his view, and builds there.</summary>
    public bool TfwMekDemo { get; set; }

    void TfwMeksStep()
    {
        if (!TfwMeksOn || _musou is not { } musou || _state != MatchState.Playing || _tfw?.Battle is not { } battle) return;
        MusouBeginTick();
        int size = _players.Count + GuardSim.MaxSquads;
        if (_mekTargets.Length < size) _mekTargets = new MusouTarget[size * 2];
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        _musouIds.Sort();
        int n = 0;
        foreach (int id in _musouIds) { var p = _players[id]; _mekTargets[n++] = new MusouTarget(id, p.Position, p.Alive && !_tfw.InRange.Contains(id)); }
        if (TfwGuards is { } guards) n += guards.AppendTargets(_mekTargets.AsSpan(n));
        _mekBlocked ??= Blocked;
        _mekGuardsNear ??= (point, radius) => TfwGuards is { } g && g.CountInside(point, radius) > 0;
        if (TfwMekDemo) { musou.MekDemo = true; TfwMekDemoPlaces(musou, battle); }
        musou.RocketDome ??= (origin, direction, max) => TfwMeks is { AnyDome: true } m ? m.DomeEntry(origin, direction, max) : float.PositiveInfinity;   // the dome stops the Orks' rockets from outside
        if (TfwGuards is { } squads && squads.Turrets is null)
        {
            squads.Shielded = TfwShielded;   // the dome stops the Guardsmen's fire from outside
            if (MusouMekRules.GuardsShootTurrets)
            {
                squads.Turrets = (Vector3 from, float range, out Vector3 centre) => { centre = default; return TfwMeks is { Count: > 0 } m ? m.NearestTurret(from, range, out centre) : (byte)0; };
                squads.TurretHit = (id, damage) => TfwMeks?.Damage(id, 0, damage, out _);
            }
        }
        battle.StepMeks(_mekTargets.AsSpan(0, n), _mekBlocked, _mekGuardsNear);
        var meks = battle.Meks;
        _mekTriggers.Clear();
        meks.TakeTriggers(_mekTriggers);
        foreach (var t in _mekTriggers) TfwTrapHurt(t);
        bool blasts = TfwMekBlasts();
        if (_mekTriggers.Count > 0 || blasts) TfwCreditKills();
    }

    /// <summary>The trap blasts waiting (a blast may destroy another trap: those go off too, at most 16 rounds a tick); whether any went off.</summary>
    bool TfwMekBlasts()
    {
        if (TfwMeks is not { } meks) return false;
        bool any = false;
        for (int round = 0; round < 16; round++)
        {
            _mekBlasts.Clear();
            meks.TakeBlasts(_mekBlasts);
            if (_mekBlasts.Count == 0) break;
            foreach (var b in _mekBlasts) MusouOrkBlast(b.Point + new Vector3(0f, 0f, 0.3f), MusouMekRules.TrapBlast, b.Credit);
            any = true;
        }
        return any;
    }

    /// <summary>A trap set off: every living player within its radius takes <see cref="MusouMekRules.TrapShare"/> of his maximum (health and armour) as an Ork's hit, an AI Marine dies outright
    /// (<see cref="MusouMekRules.TrapAiMarineDamage"/>); the Guardsmen in it the same share of a standard Marine's.</summary>
    void TfwTrapHurt(MusouTrapTrigger t)
    {
        float r2 = MusouMekRules.TrapRadius * MusouMekRules.TrapRadius;
        foreach (int id in _musouIds)
        {
            if (!_players.TryGetValue(id, out var p) || !p.Alive) continue;
            var d = p.Position - t.Point;
            if (d.X * d.X + d.Y * d.Y > r2 || MathF.Abs(d.Z) > MusouMekRules.TrapVertical) continue;
            var body = Loadouts.Body(Config, p);
            float damage = TfwContracts.IsPuppet(id) ? MusouMekRules.TrapAiMarineDamage : (body.HealthMax + body.ShieldMax) * MusouMekRules.TrapShare;   // an AI Marine dies outright (owner, 2026-11-08)
            MusouHit(new MusouAttack(t.Mek, id, damage, Melee: false, Staggers: false, t.Point));
        }
        TfwGuards?.Blast(t.Point, MusouMekRules.TrapRadius, _ => 1f, TfwTrapFlatDamage, out _);
    }

    /// <summary>A trap's damage to a Guardsman: <see cref="MusouMekRules.TrapShare"/> of a standard (first-class) Marine's health and armour.</summary>
    public float TfwTrapFlatDamage
    {
        get
        {
            var body = Loadouts.Body(Config, new PlayerState(0, Vector3.Zero, Vector3.Zero, 0f, true));
            return (body.HealthMax + body.ShieldMax) * MusouMekRules.TrapShare;
        }
    }

    /// <summary>
    /// A player's hit-scan shot against the domes and the contraptions (from <c>MusouPlayerShots</c>, Musou Mode): it ends where it enters a dome from outside, and the first contraption before that takes
    /// its damage (on the body) and ends it. <paramref name="length"/> comes back shortened for the Orks' pierce; the tracer ends there.
    /// </summary>
    void TfwMekShot(int k, ref ShotEvent shot, WeaponSpec weapon, Vector3 direction, ref float length)
    {
        if (TfwMeks is not { Count: > 0 } meks) return;
        float dome = meks.DomeEntry(shot.Origin, direction, length);
        bool ended = false;
        // The dome lets a shot through to its own generator (owner, 2026-11-09: every contraption can be destroyed); anything else behind its shell it stops.
        byte hit = meks.Raycast(shot.Origin, direction, length, out float distance);
        if (hit != 0 && distance > dome && !meks.IsGenerator(hit)) hit = 0;
        if (hit == 0 && dome < length) { length = dome; ended = true; }
        if (hit != 0)
        {
            var spec = Shots.ChargeShotOf(weapon, shot.Stage)?.Damage ?? weapon.DirectDamage with { Damage = Combat.DamageAt(distance, weapon) };
            meks.Damage(hit, shot.Shooter, spec.Damage * spec.BodyMultiplier, out _);
            length = distance; ended = true;
        }
        if (!ended) return;
        shot = shot with { End = shot.Origin + direction * length, Hit = ShotHit.World, Surface = -1, Target = 0, Armoured = false };
        _shots[k] = shot;
    }

    /// <summary>
    /// A player's projectile (grenade, rocket, plasma ball) against the domes and the contraptions: where along its step it enters a dome from outside or strikes a contraption (Projectiles.cs; it detonates
    /// there). A contraption struck takes the projectile's direct damage first (owner, 2026-11-09: hitboxes for grenades and projectiles).
    /// </summary>
    partial void ExtrasProjectileStop(in ProjectileState p, Vector3 direction, float max, ref float stop)
    {
        if (TfwMeks is not { Count: > 0 } meks || _musou is null) return;
        MusouBeginTick();   // the projectiles step before the Musou step: begin the tick so a destroyed cue raised here reaches this tick's events (review 2026-11-09)
        float dome = meks.AnyDome ? meks.DomeEntry(p.Position, direction, max) : float.PositiveInfinity;
        if (float.IsFinite(dome) && meks.GeneratorOnRay(p.Position, direction)) dome = float.PositiveInfinity;   // aimed at the generator: its dome lets it in (it strikes on a later step)
        byte hit = meks.Raycast(p.Position, direction, max, out float distance);
        if (hit != 0 && (distance <= dome || meks.IsGenerator(hit)))
        {
            stop = MathF.Min(stop, distance);
            ArsenalProjectileMeets(p.Id, 0, hit, p.Position + direction * distance);   // Arsenal/World.Arsenal.cs: a projectile of the arsenal meeting a contraption (W4); a no-op until then
            if (!p.Thrown && Shots.DirectDamageOf(WeaponOf(p), p.Stage) is { } direct) meks.Damage(hit, p.Owner, direct.Damage * direct.BodyMultiplier, out _);
            return;
        }
        stop = MathF.Min(stop, dome);
    }

    /// <summary>Whether a dome stops what goes from <paramref name="from"/> (outside it) to <paramref name="to"/> (inside it): a blast or a cone from outside.</summary>
    bool TfwShielded(Vector3 from, Vector3 to) => TfwMeks is { AnyDome: true } meks && meks.Shielded(from, to);

    /// <summary>A player's blast on the contraptions in its radius (not through a dome from outside, nor through a wall when it needs a line).</summary>
    void TfwMekBlast(int owner, Vector3 point, SplashSpec splash)
    {
        if (_state != MatchState.Playing || splash.BlindOnly || !(splash.Radius > 0f) || TfwMeks is not { Count: > 0 } meks || _musou is null) return;
        MusouBeginTick();   // as the projectiles: the cues of this tick
        meks.Near(point, splash.Radius, _mekNear);
        foreach (var (id, distance) in _mekNear)
        {
            if (!meks.TryGet(id, out var c)) continue;
            var centre = MusouMeks.Centre(c);
            if ((c.Kind != MusouContraptionKind.ShieldGenerator && TfwShielded(point, centre)) || (splash.LineOfSight && Blocked(point, centre))) continue;   // the dome never keeps it off its own generator
            float damage = splash.Damage.Damage * Combat.SplashIntensity(splash.FallOff, distance) * splash.Damage.BodyMultiplier;
            if (damage > 0f) meks.Damage(id, owner, damage, out _);
        }
        TfwMekBlasts();
    }

    /// <summary>A player's melee step on the contraptions in reach (the cleave's range and cone, feet to their edge).</summary>
    void TfwMekMelee(int attacker, PlayerState state, MeleeStep step)
    {
        if (_state != MatchState.Playing || TfwMeks is not { Count: > 0 } meks || _musou is not { } musou) return;
        MusouBeginTick();   // as the projectiles: the cues of this tick
        float damage = step.Damage * step.HealthMultiplier * Stims.MeleeDealt(Config, state);
        if (!(damage > 0f)) return;
        float range = musou.Rules.Combat.CleaveRange(Config.MeleeRange);
        float minCos = MathF.Cos(MathF.Min(180f, Config.MeleeConeDegrees * musou.Rules.Combat.CleaveConeScale) * MathF.PI / 180f);
        if (musou.War is { } volume) { range *= volume.CleaveReachScale; minCos = volume.CleaveMinCos; }   // as the Orks' cleave (owner 2026-11-09)
        var forward = new Vector2(-MathF.Sin(state.Yaw), MathF.Cos(state.Yaw));
        meks.Near(state.Position, range + 2f, _mekNear);
        foreach (var (id, _) in _mekNear)
        {
            if (!meks.TryGet(id, out var c)) continue;
            var flat = new Vector2(c.Position.X - state.Position.X, c.Position.Y - state.Position.Y);
            float distance = flat.Length(), reach = range + MusouMekRules.Body(c.Kind).Radius;
            if (distance > reach || MathF.Abs(c.Position.Z - state.Position.Z) > Config.MeleeVerticalReach) continue;
            if (distance > 1e-4f && distance > MusouMekRules.Body(c.Kind).Radius && Vector2.Dot(flat / distance, forward) < minCos) continue;   // standing on it counts as facing it
            meks.Damage(id, attacker, MusouMekRules.MeleeDestroys ? MathF.Max(damage, c.Health + 1f) : damage, out _);   // one blow destroys it (owner, 2026-11-09)
        }
        TfwMekBlasts();
    }

    /// <summary>An Ork blast on the contraptions in its radius (it hurts every side); a trap it destroys goes off only when the blast is credited to a player.</summary>
    void TfwMekOrkBlast(Vector3 point, OrkBlastSpec blast, int credit)
    {
        if (TfwMeks is not { Count: > 0 } meks) return;
        meks.Near(point, blast.Radius, _mekNear);
        foreach (var (id, distance) in _mekNear)
        {
            if (meks.TryGet(id, out var c) && c.Kind != MusouContraptionKind.ShieldGenerator && TfwShielded(point, MusouMeks.Centre(c))) continue;   // a dome keeps an Ork blast from outside off what stands in it, never off its generator
            float damage = blast.Damage * blast.IntensityAt(distance);
            if (damage > 0f) meks.Damage(id, credit, damage, out _);
        }
    }

    /// <summary>The capture aid: each Mek's place is fixed in front of the first living human, and a Mek far from it is moved there once.</summary>
    void TfwMekDemoPlaces(MusouSim musou, TfwBattle battle)
    {
        PlayerState? human = null;
        foreach (int id in _musouIds) if (!TfwContracts.IsPuppet(id) && _players[id].Alive) { human = _players[id]; break; }
        if (human is not { } h) return;
        var ahead = new Vector3(-MathF.Sin(h.Yaw), MathF.Cos(h.Yaw), 0f);
        var side = new Vector3(ahead.Y, -ahead.X, 0f);
        var origin = h.Position;
        battle.Meks.DemoHide ??= slot =>
        {
            float across = slot switch { 0 => -12f, 1 => -4f, 2 => 4f, 3 => 12f, _ => 0f };
            var spot = origin + ahead * (slot == 4 ? 32f : 24f) + side * across;
            int node = musou.Walk?.Nearest(spot, 4f, 6f) ?? -1;
            return node >= 0 ? musou.Walk!.Position(node) : spot;
        };
        for (int slot = 0; slot < MusouMekRules.MaxMeks; slot++)
        {
            ushort id = battle.Meks.MekId(slot);
            if (id == 0 || _mekDemoMoved.Contains(id)) continue;
            var to = battle.Meks.DemoHide(slot)!.Value;
            musou.Relocate(id, to);
            _mekDemoMoved.Add(id);
        }
    }
}
