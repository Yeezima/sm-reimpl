using System.Numerics;
using System.Runtime.InteropServices;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Musou Mode's melee and firepower in the World (pass 2, M5; items 7, 21, 22, 24; <see cref="TfwCombatRules"/>). All of it runs from <see cref="TfwStep"/> and the Musou combat hooks, and only with
/// <see cref="MusouSim.War"/> set (<see cref="StartTfw"/>): the core's frag, melee and shots are not touched, in Musou Mode either. The bigger frags are <see cref="ProjectileState.Spec"/>, the
/// per-projectile override the core already reads; the piercing and the cleave work on the enemies' pool alone, which no other mode has.
/// </summary>
public sealed partial class World
{
    readonly Dictionary<int, List<ushort>> _tfwPierced = new();   // projectile id: the enemies it has gone through
    readonly HashSet<int> _tfwFragged = new();                    // humans given their grenades this life

    /// <summary>For the fight's report: the most enemies one swing struck, the most one shot went into, and the frags that left a hand.</summary>
    public int TfwBestCleave { get; private set; }
    public int TfwBestPierce { get; private set; }
    public int TfwFragsOut { get; private set; }

    /// <summary>
    /// The Marines' shoves of this tick, each Marine in id order (<see cref="_musouIds"/>, from <see cref="TfwGuardsStep"/>): a Marine moving into a fodder Ork or a Guardsman throws him down. Heavier
    /// contact throws farther and kills oftener; a charge's contact on the enemies is <see cref="MusouCharges"/>'s, and a charge only shoves Guardsmen here.
    /// </summary>
    void TfwShoves()
    {
        var musou = _musou!;
        var guards = TfwGuards!;
        if (musou.War is not { } war) return;
        _history.TryGetValue(Tick - 1, out var start);
        foreach (int id in _musouIds)
        {
            var p = _players[id];
            if (!p.Alive || p.Team != Team.SpaceMarines) continue;
            var velocity = new Vector2(p.Velocity.X, p.Velocity.Y);
            float speed = velocity.Length();
            bool landing = start is not null && start.TryGetValue(id, out var before) && !before.Grounded && p.Grounded && before.Velocity.Z <= -war.LandingFall;
            bool charging = p.Rush != RushPhase.None;
            bool sprinting = speed > (Config.RunSpeed + Config.SprintSpeed) * 0.5f;
            if (!landing && !charging && speed < war.MoveSpeed) continue;
            float push = landing ? war.LandingPush : charging ? war.ChargePush : sprinting ? war.SprintPush : war.WalkPush;
            float death = landing ? war.LandingDeath : charging ? war.ChargeDeath : sprinting ? war.SprintDeath : war.WalkDeath;
            var heading = speed > 1e-3f ? velocity / speed : new Vector2(-MathF.Sin(p.Yaw), MathF.Cos(p.Yaw));
            float contact = musou.Rules.PlayerRing + war.ContactMargin;
            float reach = landing ? war.LandingReach : contact + 0.6f;
            if (!charging || !Rush.Connecting(p, Config) || landing)
            {
                var near = _musouNear.AsSpan(0, Math.Min(_musouNear.Length, war.ShoveTests));
                int n = musou.Query(p.Position, reach, near, 1.5f);
                for (int k = 0; k < n; k++)
                {
                    if (!musou.TryGet(near[k], out var e) || !MusouSim.Fodder(e.Type)) continue;
                    var flat = new Vector2(e.Position.X - p.Position.X, e.Position.Y - p.Position.Y);
                    float distance = flat.Length();
                    var away = distance > 1e-4f ? flat / distance : heading;
                    if (!landing && (distance > contact + musou.Body(e.Type).Radius || Vector2.Dot(away, heading) < 0f)) continue;
                    musou.KnockDown(e.Id, landing ? away : heading + away, push, id, death);
                }
            }
            if (guards.Count > 0) guards.Shove(p.Position, landing ? war.LandingReach : contact + guards.Rules.Radius, landing ? default : heading, push, death, war);
        }
    }

    /// <summary>
    /// The frags of this tick (item 21): a human's frag gets the bigger blast and two more beside it, fanned out from its launch (one grenade spent). A bigger blast that went off tells the clients
    /// (tag 11, <see cref="TfwFireKind.Frag"/>): the snapshot's detonation is the core's and its effect the original size. After the core's launches and detonations of the tick.
    /// </summary>
    void TfwFrags()
    {
        var run = _tfw!;
        if (_musou!.War is not { } war) return;
        int throws = _throws.Count;   // the copies add none: only the one throw left the hand
        for (int k = 0; k < throws; k++)
        {
            var thrown = _throws[k];
            if (TfwContracts.IsPuppet(thrown.Thrower) || thrown.Item < 0 || thrown.Item >= Config.Thrown.Count || !TfwCombatRules.IsFrag(Config.Thrown[thrown.Item])) continue;
            int at = IndexOf(thrown.Projectile);
            if (at < 0) continue;
            var p = _projectiles[at] with { Spec = war.BiggerFrag(SpecOf(_projectiles[at])) };
            _projectiles[at] = p;
            TfwFragsOut++;
            if (p.Motion != ProjectileMotion.Flying) continue;
            var launched = p.Position - p.Velocity * Config.TickSeconds;
            for (int copy = 1; copy < war.FragsPerThrow; copy++)
            {
                if (_projectiles.Count >= Math.Clamp(Config.MaxProjectiles, 1, MaxProjectilesLimit)) break;
                int side = (copy + 1) / 2;
                float angle = (copy % 2 == 1 ? side : -side) * war.FragSpreadDegrees * (MathF.PI / 180f);
                float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
                var v = new Vector3(p.Velocity.X * cos - p.Velocity.Y * sin, p.Velocity.X * sin + p.Velocity.Y * cos, p.Velocity.Z);
                while (_projectiles.Any(q => (q.Id & 0xFFFF) == (_nextProjectileId & 0xFFFF)) || _recentDetonations.Any(d => (d.Projectile & 0xFFFF) == (_nextProjectileId & 0xFFFF)))
                    _nextProjectileId++;
                _projectiles.Add(p with { Id = _nextProjectileId++, Position = launched, Velocity = v });
                TfwFragsOut++;
            }
        }
        foreach (var d in _detonations)
            if (d.Thrown && d.Weapon >= 0 && d.Weapon < Config.Thrown.Count && Config.Thrown[d.Weapon].Projectile.Splash is { } plain && d.Radius > plain.Radius * 1.01f)
                run.AddFire(new TfwFireEvent(TfwFireKind.Frag, Tick, (ushort)d.Projectile, d.Position, new Vector3(d.Weapon, d.Radius, 0f)));
    }

    /// <summary>
    /// Every human life in the war carries its grenades: the strip to the starting weapons (<see cref="MusouLoadout.Stripped"/>) takes them, and item 21 needs them. A fresh life is seen here once it
    /// is armed; the war's own routine for a life (<see cref="TfwRespawnAt"/>) forgets the last one.
    /// </summary>
    void TfwGiveFrags()
    {
        _musouIds.Clear();
        foreach (int id in _players.Keys) _musouIds.Add(id);
        foreach (int id in _musouIds)
        {
            if (TfwContracts.IsPuppet(id)) continue;
            var p = _players[id];
            if (!p.Alive || !_musouArmed.Contains(id)) { _tfwFragged.Remove(id); continue; }
            if (_tfwFragged.Add(id) && Loadouts.Equipment(Config, p) is { Stim: null } item) _players[id] = p with { Grenades = item.Count };
        }
        if (_tfwFragged.Count > _players.Count) _tfwFragged.RemoveWhere(id => !_players.ContainsKey(id));
    }

    /// <summary>A hit-scan shot through the enemies (item 24): the nearest and those behind it along the ray, each hit fully and for less the deeper it goes; the tracer ends on the last.</summary>
    void TfwPierceShot(int k, in ShotEvent shot, WeaponSpec weapon, Vector3 direction, float length, TfwCombatRules war)
    {
        var musou = _musou!;
        Span<ushort> passed = stackalloc ushort[TfwCombatRules.MostPierced];
        int most = Math.Min(1 + TfwCombatRules.Pierced(weapon), passed.Length), struck = 0;
        float keep = 1f, last = 0f;
        bool firstHead = false;
        while (struck < most)
        {
            ushort enemy = musou.Raycast(shot.Origin, direction, length, out float distance, out bool head, passed[..struck]);
            if (enemy == 0) break;
            var spec = Shots.ChargeShotOf(weapon, shot.Stage)?.Damage ?? weapon.DirectDamage with { Damage = Combat.DamageAt(distance, weapon) };
            float damage = spec.Damage * (head ? spec.HeadMultiplier : spec.BodyMultiplier) * keep;
            musou.Damage(enemy, shot.Shooter, damage, direction, out _, marker: head ? (byte)2 : (byte)1, cause: MusouDeathKind.Shot);
            if (struck == 0) firstHead = head;
            passed[struck++] = enemy;
            last = distance;
            keep *= war.PierceKeep;
        }
        if (struck == 0) return;
        TfwBestPierce = Math.Max(TfwBestPierce, struck);
        _shots[k] = shot with { End = shot.Origin + direction * last, Hit = firstHead ? ShotHit.Head : ShotHit.Body, Surface = -1, Target = 0, Armoured = false };
    }

    /// <summary>
    /// A projectile through the enemies on this tick's move (item 24): the first <see cref="TfwCombatRules.Pierced"/> it meets take its direct damage and it flies on. The enemy that stops it, as the
    /// 5-wave run's first (0: none on this move), for <see cref="MusouProjectiles"/> to finish.
    /// </summary>
    ushort TfwPierceProjectile(in ProjectileState p, Vector3 from, Vector3 direction, float length, out float distance, out bool head)
    {
        var musou = _musou!;
        var weapon = WeaponOf(p);
        if (!_tfwPierced.TryGetValue(p.Id, out var passed)) _tfwPierced[p.Id] = passed = new List<ushort>(2);
        int most = Math.Min(TfwCombatRules.Pierced(weapon), TfwCombatRules.MostPierced);
        while (true)
        {
            ushort enemy = musou.Raycast(from, direction, length, out distance, out head, CollectionsMarshal.AsSpan(passed));
            if (enemy == 0 || passed.Count >= most) return enemy;
            if (Shots.DirectDamageOf(weapon, p.Stage) is { } direct)
                musou.Damage(enemy, p.Owner, direct.Damage * (head ? direct.HeadMultiplier : direct.BodyMultiplier), direction, out _, marker: head ? (byte)2 : (byte)1, cause: MusouDeathKind.Shot);
            passed.Add(enemy);
        }
    }

    /// <summary>Forgets the projectiles that are gone.</summary>
    void TfwPrunePierced()
    {
        if (_tfwPierced.Count <= _projectiles.Count) return;
        _musouStale.Clear();
        foreach (int id in _tfwPierced.Keys) if (IndexOf(id) < 0) _musouStale.Add(id);
        foreach (int id in _musouStale) _tfwPierced.Remove(id);
    }
}
