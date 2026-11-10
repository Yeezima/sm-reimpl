using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// The Remote Detonators in the World (W4, docs/new-weapons-design.md section 7; Musou Mode only; the three partial methods of <c>World.Arsenal.cs</c> that are W4's). A thrown charge sticks to the
/// level, or to the Ork it struck and rides on him (a Boss Ork and the Warboss too), and goes off <see cref="DemolitionChargeRules.FuseSeconds"/> after it sticks (after the throw for one that never
/// sticks). The core never applies a blast for it (the item's projectile has none): its detonation, whatever set it off, starts a <see cref="ChargeRun"/> here, a centre blast of 600 and then 16 stacking
/// spots of 180 round a ring, one every 2 ticks (<see cref="DemolitionChargeRules"/>). Bosses take x2, up to 80 kills of one charge fly as corpses, survivors are thrown back up to 10 m once.
/// It sticks to a contraption too and rides on it (the core's stop at a contraption is skipped for it through the hook's <c>stays</c>); one destroyed under it drops it, the fuse running on.
/// </summary>
public sealed partial class World
{
    sealed class ChargeFuse
    {
        public uint StuckTick;          // 0: not stuck yet
        public ushort Enemy;            // the Ork it rides on, or 0
        public Vector3 Offset;          // from his feet
        public byte Mek;                // the contraption it sticks to, or 0
        public Vector3 MekOffset;       // from the contraption's position
        public bool Master;             // the thrower's Master-Crafted Wargear, as thrown
    }

    sealed class ChargeRun
    {
        public int Projectile, Owner, Item;
        public Vector3 Centre;
        public uint Start;              // the centre blast's tick
        public int Next;                // the next spot
        public bool Master;
        public int ThrownLeft = DemolitionChargeRules.ThrownMax;
        public readonly HashSet<ushort> Knocked = new();
    }

    readonly Dictionary<int, ChargeFuse> _chargeFuses = new();
    readonly Dictionary<int, (ushort Enemy, Vector3 Offset)> _chargeRides = new();
    readonly Dictionary<int, (byte Mek, Vector3 Offset)> _chargeMeks = new();
    readonly List<ChargeRun> _chargeRuns = new();
    readonly List<int> _chargeDue = new();
    int _chargeItem = -2;

    /// <summary>Test aid and measurement: the centre blasts and the ring spots that have gone off, and the kills that flew as corpses.</summary>
    public int ChargeCentres { get; private set; }
    public int ChargeSpots { get; private set; }
    public int ChargeFlown { get; private set; }
    /// <summary>The charge runs in progress (test aid).</summary>
    public int ChargeRunsLive => _chargeRuns.Count;

    /// <summary>Index of the Remote Detonators in <see cref="SimConfig.Thrown"/>, or -1 (the config has none).</summary>
    int ChargeItem()
    {
        if (_chargeItem != -2) return _chargeItem;
        _chargeItem = -1;
        for (int i = 0; i < Config.Thrown.Count; i++)
            if (Config.Thrown[i].Name.Equals(ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase)) { _chargeItem = i; break; }
        return _chargeItem;
    }

    partial void ArsenalRefillSeconds(ThrownSpec item, ref float seconds)
    {
        if (item.Name.Equals(ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase)) seconds = DemolitionChargeRules.RefillSeconds;
    }

    partial void ArsenalProjectileMeets(int projectile, ushort enemy, byte contraption, Vector3 point, ref bool stays)
    {
        if (_musou is null || (enemy == 0 && contraption == 0)) return;
        int item = ChargeItem();
        if (item < 0) return;
        int at = IndexOf(projectile);
        if (at < 0 || !_projectiles[at].Thrown || _projectiles[at].Weapon != item) return;
        if (contraption != 0)
        {
            // It sticks to the contraption (the owner's design; the core would set it off there): the core does not stop it, and the next step holds it at the point it struck.
            if (TfwMeks is not { } meks || !meks.TryGet(contraption, out var c)) return;
            _chargeMeks[projectile] = (contraption, point - c.Position);
            stays = true;
            return;
        }
        if (!_musou.TryGet(enemy, out var e)) return;
        _chargeRides[projectile] = (enemy, point - e.Position);   // it stuck to him: it rides from the next step on
    }

    partial void ArsenalStep()
    {
        int item = ChargeItem();
        if (item < 0 || _musou is null) return;
        if (_projectiles.Count == 0 && _chargeRuns.Count == 0 && _chargeFuses.Count == 0) { if (_chargeRides.Count > 0) _chargeRides.Clear(); if (_chargeMeks.Count > 0) _chargeMeks.Clear(); return; }
        var musou = _musou;
        uint fuse = (uint)MathF.Round(DemolitionChargeRules.FuseSeconds * Config.TickRate);
        _chargeDue.Clear();
        for (int i = 0; i < _projectiles.Count; i++)
        {
            var p = _projectiles[i];
            if (!p.Thrown || p.Weapon != item) continue;
            if (!_chargeFuses.TryGetValue(p.Id, out var f))
                _chargeFuses[p.Id] = f = new ChargeFuse { Master = _players.TryGetValue(p.Owner, out var thrower) && Loadouts.Upgrades(Config, thrower).Has(DemolitionChargeRules.MasterCrafted) };
            var q = p;
            if (p.Motion == ProjectileMotion.Attached)   // it passes marines (ours): off the one it struck, it falls from there
                q = p with { Motion = ProjectileMotion.Flying, AttachedTo = -1, Velocity = Vector3.Zero, Struck = p.AttachedTo };
            if (_chargeRides.Remove(p.Id, out var ride)) { f.Enemy = ride.Enemy; f.Offset = ride.Offset; f.Mek = 0; }
            if (_chargeMeks.Remove(p.Id, out var onMek)) { f.Mek = onMek.Mek; f.MekOffset = onMek.Offset; f.Enemy = 0; }
            if (f.Mek != 0)
            {
                if (TfwMeks is { } meks && meks.TryGet(f.Mek, out var mek))
                {
                    q = q with { Position = mek.Position + f.MekOffset, Velocity = Vector3.Zero, Motion = ProjectileMotion.Stuck };   // held on the contraption
                    if (f.StuckTick == 0) f.StuckTick = Tick;
                }
                else { f.Mek = 0; q = q with { Motion = ProjectileMotion.Flying, Velocity = Vector3.Zero }; }   // destroyed under it: it drops, the fuse runs on
            }
            if (f.Enemy != 0)
            {
                if (musou.TryGet(f.Enemy, out var host) && host.Alive)
                {
                    q = q with { Position = host.Position + f.Offset, Velocity = Vector3.Zero, Motion = ProjectileMotion.Stuck };   // rides on him
                    if (f.StuckTick == 0) f.StuckTick = Tick;
                }
                else { f.Enemy = 0; q = q with { Motion = ProjectileMotion.Flying, Velocity = Vector3.Zero }; }   // his carrier fell: it drops where he was, the fuse runs on
            }
            else if (q.Motion == ProjectileMotion.Stuck && f.StuckTick == 0) f.StuckTick = Tick;
            if (q != p) _projectiles[i] = q;
            uint from = f.StuckTick != 0 ? f.StuckTick : p.LaunchTick;
            if (Tick >= from + fuse) _chargeDue.Add(p.Id);
        }
        foreach (int id in _chargeDue)
        {
            int at = IndexOf(id);
            if (at >= 0) Detonate(id, _projectiles[at].Position, -1, HitZone.None);
        }
        // Every detonation of a charge this tick (its fuse, or the core's stop at a contraption) starts a run.
        for (int d = 0; d < _detonations.Count; d++)
        {
            var det = _detonations[d];
            if (!det.Thrown || det.Weapon != item) continue;
            _chargeFuses.Remove(det.Projectile, out var f);
            _chargeRides.Remove(det.Projectile);
            _chargeMeks.Remove(det.Projectile);
            _chargeRuns.Add(new ChargeRun { Projectile = det.Projectile, Owner = det.Owner, Item = item, Centre = det.Position, Start = Tick, Master = f?.Master ?? false });
        }
        if (_chargeFuses.Count > 0)   // a charge gone some other way (off the map): forgotten
            foreach (int id in _chargeFuses.Keys.ToList()) if (IndexOf(id) < 0) _chargeFuses.Remove(id);
        for (int r = _chargeRuns.Count - 1; r >= 0; r--)
        {
            var run = _chargeRuns[r];
            long timer = SimProfile.Begin();
            if (Tick == run.Start) { ChargeBlast(run, run.Centre, DemolitionChargeRules.Centre(run.Master)); ChargeCentres++; }
            while (run.Next < DemolitionChargeRules.Spots && Tick >= run.Start + DemolitionChargeRules.SpotTick(run.Next))
            {
                var at = DemolitionChargeRules.SpotAt(run.Centre, run.Projectile, run.Next, DemolitionChargeRules.Ring(run.Master),
                    Collision is null ? null : (o, dir, len) => RaycastLevel(o, dir, len) is { } hit ? hit.Distance : null);
                ChargeBlast(run, at, DemolitionChargeRules.Spot(run.Master));
                ChargeSpots++;
                run.Next++;
            }
            SimProfile.End("charge: blast", timer);
            if (run.Next >= DemolitionChargeRules.Spots) _chargeRuns.RemoveAt(r);
        }
    }

    /// <summary>
    /// One blast of a charge at <paramref name="point"/>: every living player (the thrower too) in reach and in sight, every Ork (Boss Orks and the Warboss x2; the first
    /// <see cref="DemolitionChargeRules.ThrownMax"/> kills of the whole charge fly as corpses, later ones are torn apart; a survivor is thrown back once, scaled by what he took) and every contraption.
    /// </summary>
    void ChargeBlast(ChargeRun run, Vector3 point, SplashSpec splash)
    {
        if (_state != MatchState.Playing) return;
        MusouBeginTick();
        var musou = _musou!;
        var rules = musou.Rules.Combat;
        foreach (var target in _players.Keys.ToList())
        {
            var player = _players[target];
            if (!player.Alive) continue;
            var centre = player.Position + new Vector3(0, 0, ChestOf(player));
            float distance = Vector3.Distance(centre, point);
            if (distance > splash.Radius || Blocked(point, centre)) continue;
            float intensity = Combat.SplashIntensity(splash.FallOff, distance);
            ApplyHit(run.Owner, target, splash.Damage, HitZone.Body, distance, intensity, run.Projectile, true, weapon: run.Item, thrownItem: true, direction: BlastPush(point, target, Vector3.Zero));
        }
        int n = musou.Query(point, splash.Radius, _musouNear, verticalReach: splash.Radius + 3f);
        SortNearest(musou, point, n);
        for (int k = 0; k < n; k++)
        {
            if (!musou.TryGet(_musouNear[k], out var e) || !e.Alive) continue;
            var chest = MusouSim.Chest(e.Position, musou.Body(e.Type).Height);
            float distance = Vector3.Distance(chest, point);
            if (distance > splash.Radius || Blocked(point, chest) || TfwShielded(point, chest)) continue;
            float intensity = Combat.SplashIntensity(splash.FallOff, distance);
            if (!(intensity > 0f)) continue;
            var away = new Vector2(e.Position.X - point.X, e.Position.Y - point.Y);
            var push = away.LengthSquared() > 1e-8f ? Vector2.Normalize(away) : new Vector2(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw));
            float damage = splash.Damage.Damage * intensity * (TfwCombatRules.IsBoss(e.Type) ? DemolitionChargeRules.BossFactor : 1f);
            bool flies = run.ThrownLeft > 0;
            musou.Damage(e.Id, run.Owner, damage, new Vector3(push, 0.3f), out bool killed, cause: flies ? MusouDeathKind.Blast : MusouDeathKind.BoltBlast);
            if (killed) { if (flies) { run.ThrownLeft--; ChargeFlown++; } continue; }
            if (intensity < rules.BlastStaggerIntensity || !run.Knocked.Add(e.Id)) continue;
            float knock = DemolitionChargeRules.KnockbackMax * MathF.Min(1f, intensity);
            if (!musou.KnockDown(e.Id, push, knock, run.Owner, 0f)) musou.Stagger(e.Id, push, knock);
        }
        TfwMekBlast(run.Owner, point, splash);   // the contraptions in reach (Campaign/World.TfwMeks.cs)
    }
}
