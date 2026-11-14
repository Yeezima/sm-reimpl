using System.Numerics;
using System.Runtime.InteropServices;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Musou Mode's melee and firepower in the World (pass 2, M5; items 7, 21, 22, 24; <see cref="TfwCombatRules"/>). All of it runs from <see cref="TfwStep"/> and the Musou combat hooks, and only with
/// <see cref="MusouSim.War"/> set (<see cref="StartTfw"/>): the core's frag, melee and shots are not touched, in Musou Mode either. The piercing and the cleave work on the enemies' pool alone, which no other mode has.
/// </summary>
public sealed partial class World
{
    readonly Dictionary<int, List<ushort>> _tfwPierced = new();   // projectile id: the enemies it has gone through
    readonly HashSet<int> _tfwFragged = new();                    // humans given their grenades this life

    /// <summary>For the fight's report: the most enemies one swing struck and the most one shot went into.</summary>
    public int TfwBestCleave { get; private set; }
    public int TfwBestPierce { get; private set; }

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
    /// Every human life in the war carries its grenades, the vanilla count (the item's <c>SpawnCount</c>, as Classic PvP spawns with): the strip to the starting weapons
    /// (<see cref="MusouLoadout.Stripped"/>) takes them. The throw itself is the core's (one frag, the data's blast; owner 2026-11-08: "make them function as they do in vanilla space marine").
    /// A fresh life is seen here once it is armed; the war's own routine for a life (<see cref="TfwRespawnAt"/>) forgets the last one.
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
            if (_tfwFragged.Add(id) && Loadouts.Equipment(Config, p) is { Stim: null } item) _players[id] = p with { Grenades = item.SpawnCount };
        }
        if (_tfwFragged.Count > _players.Count) _tfwFragged.RemoveWhere(id => !_players.ContainsKey(id));
    }

    /// <summary>A hit-scan shot through the enemies (item 24): the nearest and those behind it along the ray, each hit fully and for less the deeper it goes; the tracer ends on the last.</summary>
    void TfwPierceShot(int k, in ShotEvent shot, WeaponSpec weapon, Vector3 direction, float length, TfwCombatRules war)
    {
        var musou = _musou!;
        Span<ushort> passed = stackalloc ushort[TfwCombatRules.MostPlasmaPierced];
        Span<float> at = stackalloc float[TfwCombatRules.MostPlasmaPierced];
        bool plasma = war.PlasmaShot(weapon);   // World.TfwPlasma.cs: through every enemy, each at full damage, and each burns
        int most = plasma ? passed.Length : Math.Min(1 + TfwCombatRules.Pierced(weapon), TfwCombatRules.MostPierced), struck = 0;
        float keep = 1f, last = 0f;
        bool firstHead = false;
        while (struck < most)
        {
            ushort enemy = musou.Raycast(shot.Origin, direction, length, out float distance, out bool head, passed[..struck]);
            if (enemy == 0) break;
            var spec = MusouShotDamage(weapon, shot.Stage, distance, shot.Shooter);   // full damage at any range in the war (owner 2026-11-11)
            float damage = spec.Damage * (head ? spec.HeadMultiplier : spec.BodyMultiplier) * keep * MusouRangedDealt(shot.Shooter);   // x1.2 (owner 2026-11-11); a plasma burn follows the hit
            musou.Damage(enemy, shot.Shooter, damage, direction, out _, marker: head ? (byte)2 : (byte)1, cause: MusouDeathKind.Shot);
            if (plasma) TfwPlasmaBurn(enemy, shot.Shooter, damage, war);
            if (struck == 0) firstHead = head;
            at[struck] = distance;
            passed[struck++] = enemy;
            last = distance;
            if (!plasma && !TfwCombatRules.ScalesPlayer(shot.Shooter)) keep *= war.PierceKeep;   // owner 2026-11-11: a human's shot loses nothing per Ork; an AI Marine's keeps the 15 %
        }
        if (struck == 0) return;
        SplashSpec? own = null;
        ArsenalShotBlast(weapon, ref own);   // Arsenal/World.Arsenal.cs: a weapon's own blast (W1, the autocannon); none until then
        if (war.BoltBlasts && (own is not null || TfwCombatRules.IsBolt(weapon))) TfwBoltBlasts(shot.Shooter, shot.Origin, direction, at[..struck], war, own);
        TfwBestPierce = Math.Max(TfwBestPierce, struck);
        _shots[k] = shot with { End = shot.Origin + direction * last, Hit = firstHead ? ShotHit.Head : ShotHit.Body, Surface = -1, Target = 0, Armoured = false };
    }

    /// <summary>For the tests and the fight's report: the bolt blasts set off this war.</summary>
    public int TfwBoltBlastsOut { get; private set; }

    /// <summary>
    /// A bolt weapon's blasts (the owner, 2026-11-07): one at every enemy the shot struck, at the point it struck him (<paramref name="distances"/> along the ray), each on the enemies around it
    /// (<see cref="MusouBlast"/>: enemies only, so no player, Guardsman or AI Marine is hurt), and a record on tag 11 (<see cref="TfwFireKind.BoltBlast"/>) for the clients' effect and sound.
    /// After the pierce has dealt its hits, so a blast does not change which enemies the shot went through.
    /// </summary>
    /// <param name="own">The weapon's own blast (Musou's arsenal: <c>ArsenalShotBlast</c>), else null: the bolt blast.</param>
    void TfwBoltBlasts(int shooter, Vector3 origin, Vector3 direction, ReadOnlySpan<float> distances, TfwCombatRules war, SplashSpec? own = null)
    {
        // musou/aoe-pass (owner 2026-11-14): the bolt blast's reach x1.25, the autocannon's burst x1.4 (MusouCombatRules.AutocannonAoeScale), fall-off distances with it; damage unchanged.
        var splash = own is null ? Combat.ScaledReach(war.BoltBlast(), MusouCombatRules.WeaponAoeScale) : Combat.ScaledReach(own, MusouCombatRules.AutocannonAoeScale);
        int frag = -1;
        for (int i = 0; i < Config.Thrown.Count && frag < 0; i++) if (TfwCombatRules.IsFrag(Config.Thrown[i])) frag = i;
        foreach (float distance in distances)
        {
            var point = origin + direction * distance;
            MusouBlast(shooter, point, splash, melee: false, bolt: war.BoltBlastKnocksDown);
            TfwBoltBlastsOut++;
            _tfw?.Fire.BoltBlast(Tick, point, frag, splash.Radius);
        }
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
        var war = musou.War!;
        bool plasma = war.PlasmaShot(weapon);   // World.TfwPlasma.cs: a plasma ball never stops on an enemy (only on the map, where its splash goes off) and burns each it goes through
        int most = plasma ? int.MaxValue : Math.Min(TfwCombatRules.Pierced(weapon), TfwCombatRules.MostPierced);
        while (true)
        {
            ushort enemy = musou.Raycast(from, direction, length, out distance, out head, CollectionsMarshal.AsSpan(passed));
            if (enemy == 0 || passed.Count >= most) return enemy;
            // A charged plasma cannon ball carries its damage in the burst (DetonateDamage), not as a direct hit: each enemy it goes through takes that once.
            var direct = Shots.DirectDamageOf(weapon, p.Stage) ?? (plasma ? SpecOf(p).DetonateDamage : null);
            if (direct is { } hit)
            {
                float damage = hit.Damage * (head ? hit.HeadMultiplier : hit.BodyMultiplier) * MusouRangedDealt(p.Owner);   // x1.2 (owner 2026-11-11)
                musou.Damage(enemy, p.Owner, damage, direction, out _, marker: head ? (byte)2 : (byte)1, cause: MusouDeathKind.Shot);
                if (plasma) TfwPlasmaBurn(enemy, p.Owner, damage, war);
            }
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
