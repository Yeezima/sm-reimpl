using System;
using System.Collections.Generic;
using Godot;
using SmClient.ImpactRules;
using SmNet;
using SmSim;
using Num = System.Numerics;
using static SmClient.ImpactRules.ClassicHitDirRules;

namespace SmClient;

/// <summary>
/// classic-hit-effects (brief 2026-11-23-series-36-classic-hit-effects rev 1; owner answer 2026-11-23, question card: "Extended only"; client presentation only, no wire, protocol or server
/// change). The directional hit look on players in Classic PvP, driven by the damage the server CONFIRMED (every player's damage mark, protocol 71) so the local, remote and spectating views agree
/// and one damage draws one effect:
/// <list type="bullet">
/// <item>A mark change on a player hit by a drawn shot (own or remote, <see cref="NoteShotContact"/>) is that shot's: gunfire, along the incoming shot (the blood and the fatal-headshot decal are
/// the Extended profile's: <see cref="ExtrasClassicBlood"/>, <see cref="ExtrasClassicFatalHeadshot"/>; the gunfire sparks stay as they were).</item>
/// <item>Otherwise the enemy whose weapon melee attack (not a stomp or blast) just struck in reach is paired (<see cref="ClassicHitDirRules.PickAttacker"/>), and the spray follows his weapon as it is
/// drawn in this view: from its closest approach to the victim's body, along its recorded path and on as it moves (a remote attacker drawn behind the server is waited for up to
/// <see cref="ClassicHitDirRules.MaxWaitSeconds"/>; a late confirmation starts the spray up to <see cref="ClassicHitDirRules.MaxPreAge"/> s aged). Armour sparks (armour only, mixed) are
/// yellow-white directional streaks in both Classic profiles; the blood is Extended's.</item>
/// <item>Nothing else changes: stomps, ground pounds, charges, blasts and executions keep their effects; Musou Mode and Warfront never come here.</item>
/// </list>
/// <c>--old-classic-hit-dir</c> restores the look of d090566a. Aids: <c>--log-hit-dir</c> (a HITDIR line per decision and the cost), <c>--hit-dir-demo &lt;case&gt;</c> (NetworkView.ClassicHitDirDemo.cs).
/// </summary>
public partial class NetworkView
{
    bool OldClassicHitDirFlag => _oldClassicHitDir ??= CmdFlag(OldLookFlag);
    bool? _oldClassicHitDir;
    bool LogHitDir => _logHitDir ??= CmdFlag("log-hit-dir");
    bool? _logHitDir;

    /// <summary>What this match draws of the directional look.</summary>
    DirLook HitDirLook => ClassicHitDirRules.LookFor(MatchExtras, OldClassicHitDirFlag);

    /// <summary>Whether the Extended blood spray replaces today's red health-hit spark fan on players.</summary>
    bool ClassicBloodReplacesRed => HitDirLook.HasFlag(DirLook.BloodSpray);

    ClassicHitSpray? _hitSpray;
    ClassicHitSpray HitSpray()
    {
        if (_hitSpray is null || !GodotObject.IsInstanceValid(_hitSpray)) { _hitSpray = new ClassicHitSpray { Name = "ClassicHitSpray" }; AddChild(_hitSpray); }
        return _hitSpray;
    }

    /// <summary>One contact for a spray: where (world), which way, how wide, how much, how aged, and the weapon's path (world points, seconds from the contact) and live position for melee.</summary>
    public readonly record struct ClassicContact(int Victim, int Attacker, Vector3 Contact, Vector3 Dir, HitVisual Visual, bool Gunfire, bool Head, bool Lethal, bool Heavy,
        float Spread, float Amount, double PreAge, (double T, Vector3 P)[]? History, int HistoryCount, Func<Vector3?>? Weapon);

    /// <summary>Added content (the Extended profile's blood, Extras/Gore): the red directional spray for a contact; <paramref name="drawn"/> set when it was.</summary>
    partial void ExtrasClassicBlood(ClassicContact c, ref bool drawn);
    /// <summary>Added content: a fatal gunfire headshot's contact (world) and shot direction, for the wall decal behind the head.</summary>
    partial void ExtrasClassicFatalHeadshot(int victim, int shooter, Vector3 contact, Vector3 dir);
    /// <summary>Added content: a kill in the feed (the decal pairs a headshot kill with its shot).</summary>
    partial void ExtrasClassicKill(KillView k);

    // ---- weapon paths ----

    sealed class WeaponTrail
    {
        public const int Capacity = 48;
        public readonly (double T, Vector3 P)[] Ring = new (double, Vector3)[Capacity];
        public int Count, Head;
        public double MeleeAt = double.NegativeInfinity;
        public bool Heavy;
        public long Key;
        public void Add(double t, Vector3 p) { Ring[Head] = (t, p); Head = (Head + 1) % Capacity; if (Count < Capacity) Count++; }
        /// <summary>The points from <paramref name="since"/> on, oldest first, into <paramref name="into"/>; their count.</summary>
        public int Copy(double since, (double T, Vector3 P)[] into)
        {
            int n = 0;
            for (int k = 0; k < Count; k++)
            {
                var e = Ring[(Head - Count + k + Capacity) % Capacity];
                if (e.T >= since && n < into.Length) into[n++] = e;
            }
            return n;
        }
    }

    readonly Dictionary<int, WeaponTrail> _weaponTrails = new();
    readonly Dictionary<ulong, Vector3> _bladeLocal = new();
    readonly (double T, Vector3 P)[] _trailScratch = new (double, Vector3)[WeaponTrail.Capacity];
    readonly List<ClassicHitDirRules.PathPoint> _pathScratch = new();
    readonly List<MeleeCandidate> _candidateScratch = new();
    readonly List<int> _trailGone = new();

    /// <summary>A point on the melee weapon's head as drawn (world): 60 % of the way from its mesh's centre to the end of its longest side farther from its grip; the right hand without a weapon.</summary>
    Vector3? BladePoint(int id)
    {
        CharacterViewer? c = _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
        if (c is null || !GodotObject.IsInstanceValid(c) || !c.IsInsideTree()) return null;
        if (c.Weapons?.MeleeMeshInHand is { } held && GodotObject.IsInstanceValid(held.Mesh))
        {
            ulong key = held.Mesh.GetInstanceId();
            if (!_bladeLocal.TryGetValue(key, out var local))
            {
                var box = held.Mesh.GetAabb();
                var centre = box.Position + box.Size * 0.5f;
                int axis = box.Size.X >= box.Size.Y && box.Size.X >= box.Size.Z ? 0 : box.Size.Y >= box.Size.Z ? 1 : 2;
                var half = Vector3.Zero; half[axis] = box.Size[axis] * 0.5f;
                var end = (centre + half).LengthSquared() >= (centre - half).LengthSquared() ? centre + half : centre - half;
                _bladeLocal[key] = local = centre + (end - centre) * 0.6f;
                if (_bladeLocal.Count > 128) _bladeLocal.Clear();
            }
            return held.Mesh.GlobalTransform * local;
        }
        return BodyMarker(id, "RIG_Prop_Rhand")?.Invoke()?.Position;
    }

    /// <summary>A weapon melee attack (not a stomp: its blast keeps its own effects).</summary>
    bool WeaponMeleeActive(PlayerState s) => s.Alive && s.MeleeStep > 0 && _config is not null && Combat.MeleeStepOf(_config, s) is { } step && step.Stomp is null;

    // ---- contacts in flight ----

    sealed class ShotContact { public double At, Delay; public Vector3 Contact; public Vector3 Dir; public bool Head; public int Shooter; public bool ArmourUp; public bool Own; }
    readonly Dictionary<int, ShotContact> _shotContacts = new();

    sealed class MeleePending { public int Victim, Attacker; public bool Shield, Health, ArmourBefore, Lethal, Heavy; public long Key; public double At; public int Frames; }
    readonly List<MeleePending> _meleePending = new();
    readonly ContactLedger _contactLedger = new();
    readonly Dictionary<int, double> _meleePairedAt = new();
    (int Victim, float ShieldAfter, double At) _ownMeleeReport = (0, 0f, double.NegativeInfinity);

    int _hitDirShots, _hitDirMelee, _hitDirUnpaired, _hitDirSparks, _hitDirBlood, _hitDirWaited;
    double _hitDirMs, _hitDirMsPeak;
    int _hitDirFrames;
    readonly System.Diagnostics.Stopwatch _hitDirWatch = new();

    /// <summary>A shot drawn on a player (FxImpact): remembered for the confirmation of its damage. Own shots wait for the server's mark; another player's arrives with it.</summary>
    void NoteShotContact(int victim, int shooter, Vector3 contact, Vector3 dir, bool head, bool? armourUp, bool own, double delay)
    {
        if (victim <= 0 || HitDirLook == DirLook.None) return;
        if (!_shotContacts.TryGetValue(victim, out var c)) _shotContacts[victim] = c = new ShotContact();
        c.At = _connectedFor; c.Delay = delay; c.Contact = contact; c.Dir = dir; c.Head = head; c.Shooter = shooter; c.ArmourUp = armourUp ?? true; c.Own = own;
    }

    /// <summary>The own hit report (UpdateHits): the victim's armour after the own melee strike, exact for the own view's mixed-or-blood choice.</summary>
    void NoteOwnMeleeReport(HitReport hits)
    {
        if (hits.LastMelee && hits.LastVictim > 0) _ownMeleeReport = (hits.LastVictim, hits.VictimShield, _connectedFor);
    }

    /// <summary>Whether a melee damage on <paramref name="victim"/> was just paired with an attacker (its fan and red sparks give way to the directional look).</summary>
    bool MeleeContactPaired(int victim) => _meleePairedAt.TryGetValue(victim, out double at) && _connectedFor - at < 0.6;

    /// <summary>
    /// A confirmed damage on another player (his mark changed since the last record; <paramref name="nowAlive"/> false: it killed him). Returns whether a melee attacker was paired (the caller then
    /// leaves out the old fan and, with the blood, the red sparks).
    /// </summary>
    bool ClassicHitDirMark(PlayerState o, bool shield, bool health, bool nowAlive)
    {
        var look = HitDirLook;
        if (look == DirLook.None || _client is null || _config is null || (!shield && !health)) return false;
        _hitDirWatch.Restart();
        try
        {
            bool lethal = !nowAlive;
            if (_shotContacts.TryGetValue(o.PlayerId, out var shot) && _connectedFor - shot.At <= (shot.Own ? OwnWindow : RemoteWindow))
            {
                _shotContacts.Remove(o.PlayerId);
                var visual = Classify(shield, health, ShieldLeftAfterMark(shield, health, shot.ArmourUp));
                _hitDirShots++;
                bool drawn = false;
                if (HasBlood(visual))
                {
                    var c = new ClassicContact(o.PlayerId, shot.Shooter, shot.Contact, shot.Dir, visual, true, shot.Head, lethal, false, 0.3f, BloodAmount(false, lethal), -Math.Max(0.0, shot.At + shot.Delay - _connectedFor), null, 0, null);   // the spray waits for the tracer's arrival
                    ExtrasClassicBlood(c, ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
                    if (drawn) _hitDirBlood++;
                }
                if (lethal && shot.Head) ExtrasClassicFatalHeadshot(o.PlayerId, shot.Shooter, shot.Contact, shot.Dir);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
                if (LogHitDir) GD.Print($"HITDIR shot P{shot.Shooter}->P{o.PlayerId} {(shot.Head ? "head" : "body")} {visual}{(lethal ? " lethal" : "")} shield {shield} health {health} armour-before {shot.ArmourUp} blood {drawn} (t {_connectedFor:F2})");
                return false;
            }
            // melee: the attacker who just struck in reach
            var cands = _candidateScratch;
            cands.Clear();
            AddCandidate(cands, _client.Predicted, own: true);
            foreach (var p in _lastOthers) AddCandidate(cands, p, own: false);
            int attacker = PickAttacker(o.PlayerId, o.Team, o.Position, cands);
            if (attacker == 0)
            {
                _hitDirUnpaired++;
                if (LogHitDir) GD.Print($"HITDIR mark P{o.PlayerId} shield {shield} health {health}: no shot and no weapon melee attacker in reach (left to the existing look; t {_connectedFor:F2})");
                return false;
            }
            var trail = _weaponTrails[attacker];
            _meleePairedAt[o.PlayerId] = _connectedFor;
            if (!_contactLedger.First(o.PlayerId, attacker, trail.Key, _connectedFor)) return true;
            bool before = _armourFlash.ArmourUp(o.PlayerId, _connectedFor) ?? true;
            _meleePending.Add(new MeleePending { Victim = o.PlayerId, Attacker = attacker, Shield = shield, Health = health, ArmourBefore = before, Lethal = lethal, Heavy = trail.Heavy, Key = trail.Key, At = _connectedFor });
            if (LogHitDir) GD.Print($"HITDIR mark P{o.PlayerId} paired with P{attacker} (key {trail.Key}) shield {shield} health {health} armour-before {before}{(lethal ? " lethal" : "")} (t {_connectedFor:F2})");
            return true;
        }
        finally { NoteHitDirCost(); }
    }

    void AddCandidate(List<MeleeCandidate> into, PlayerState s, bool own)
    {
        if (!_weaponTrails.TryGetValue(s.PlayerId, out var t)) return;
        into.Add(new MeleeCandidate(s.PlayerId, s.Team, s.Position, s.Yaw, _connectedFor - t.MeleeAt, s.Alive, t.Heavy, own));
    }

    /// <summary>Per frame, before the shots and marks (UpdateFx): records every attacking player's weapon path and resolves the melee contacts that wait for the drawn weapon.</summary>
    void ClassicHitDirFrame(PlayerState me)
    {
        if (_client is null || _config is null) return;
        if (HitDirLook == DirLook.None) { _meleePending.Clear(); _shotContacts.Clear(); return; }
        // the spray pool (32 meshes) and its shader are made on the first effects frame after the join, not on the first hit mid-fight (live 8v8 check 2026-11-24: made lazily
        // on the first hit, it cost one frame of about 176 ms); left out of the per-frame cost below
        if (_hitSpray is null) HitSpray().Prewarm(ToGodot(me.Position));
        _hitDirWatch.Restart();
        Record(me, own: true);
        foreach (var p in _lastOthers) Record(p, own: false);
        if (_weaponTrails.Count > _lastOthers.Count + 1)
        {
            _trailGone.Clear();
            foreach (var id in _weaponTrails.Keys) { if (id == _client.PlayerId) continue; bool present = false; foreach (var p in _lastOthers) if (p.PlayerId == id) { present = true; break; } if (!present) _trailGone.Add(id); }
            foreach (var id in _trailGone) _weaponTrails.Remove(id);
        }
        for (int i = _meleePending.Count - 1; i >= 0; i--)
            if (ResolveMelee(_meleePending[i])) _meleePending.RemoveAt(i);
        NoteHitDirCost();
        _hitDirFrames++;
    }

    void Record(PlayerState s, bool own)
    {
        if (!_weaponTrails.TryGetValue(s.PlayerId, out var t)) _weaponTrails[s.PlayerId] = t = new WeaponTrail();
        if (WeaponMeleeActive(s))
        {
            long tick = own ? _client!.Tick : _client!.LatestSnapshotTick;
            long start = MeleeHitGate.StartTick(tick, s.MeleeTime, _config!.TickSeconds);
            t.Key = start * 64 + s.MeleeStep * 2 + (s.MeleeHeavy ? 1 : 0);
            t.MeleeAt = _connectedFor; t.Heavy = s.MeleeHeavy;
        }
        if (_connectedFor - t.MeleeAt > 0.9) return;   // only around an attack
        if (BladePoint(s.PlayerId) is { } p) t.Add(_connectedFor, p);
    }

    /// <summary>A waiting melee contact: true when it was drawn (or given up).</summary>
    bool ResolveMelee(MeleePending m)
    {
        m.Frames++;
        bool ownAttacker = m.Attacker == _client!.PlayerId;
        if (ownAttacker && m.Frames < 2) return false;   // the own hit report of the same snapshot comes after the marks (UpdateHits): wait one frame for its exact armour
        PlayerState? victim = null;
        foreach (var p in _lastOthers) if (p.PlayerId == m.Victim) { victim = p; break; }
        if (victim is not { } v || !_weaponTrails.TryGetValue(m.Attacker, out var trail)) return true;
        PlayerState attacker = ownAttacker ? _client.Predicted : default;
        if (!ownAttacker) { bool found = false; foreach (var p in _lastOthers) if (p.PlayerId == m.Attacker) { attacker = p; found = true; break; } if (!found) return true; }
        var low = ToGodot(v.Position + new Num.Vector3(0f, 0f, 0.9f));
        var high = ToGodot(v.Position + new Num.Vector3(0f, 0f, 1.65f));
        int n = trail.Copy(m.At - 0.6, _trailScratch);
        var path = _pathScratch; path.Clear();
        for (int k = 0; k < n; k++) path.Add(new ClassicHitDirRules.PathPoint(_trailScratch[k].T, HdN(_trailScratch[k].P)));
        var (idx, dist) = ClosestApproach(path, HdN(low), HdN(high));
        double waited = _connectedFor - m.At;
        if (idx >= 0 && WaitForContact(idx, path.Count, dist, waited)) { if (m.Frames == 1) _hitDirWaited++; return false; }

        var towardAttacker = ToGodot(attacker.Position) - ToGodot(v.Position); towardAttacker.Y = 0f;
        var facing = DirToGodot(new Num.Vector3(-MathF.Sin(attacker.Yaw), MathF.Cos(attacker.Yaw), 0f)).Normalized();
        Vector3 contact, dir;
        double contactAt;
        int histCount = 0;
        if (idx >= 0 && dist < 1.6f)
        {
            contactAt = path[idx].Time;
            contact = HdG(ContactOnBody(path[idx].Position, HdN(low), HdN(high)));
            dir = WeaponDirection(path, contactAt) is { } wd ? HdG(wd) : facing;
            for (int k = idx + 1; k < path.Count && histCount < _trailScratch.Length; k++) _trailScratch[histCount++] = (path[k].Time - contactAt, HdG(path[k].Position));
        }
        else
        {
            // no drawn weapon near him (no model, a pose far off): the contact on his chest toward the attacker, thrown along the attacker's facing
            contactAt = _connectedFor;
            var outward = towardAttacker.LengthSquared() > 1e-6f ? towardAttacker.Normalized() : -facing;
            contact = (low + high) * 0.5f + outward * BodySurface;
            dir = facing;
        }
        float thrust = MathF.Abs(dir.Normalized().Dot(facing));
        float spread = SpreadFor(thrust);
        bool shieldAfter = ownAttacker && _ownMeleeReport.Victim == m.Victim && _ownMeleeReport.At >= m.At - 0.05
            ? _ownMeleeReport.ShieldAfter > 0f : ShieldLeftAfterMark(m.Shield, m.Health, m.ArmourBefore);
        var visual = Classify(m.Shield, m.Health, shieldAfter);
        double preAge = PreAge(_connectedFor, contactAt);
        int attackerId = m.Attacker;
        Func<Vector3?> weapon = () => BladePoint(attackerId);
        var hist = histCount > 0 ? _trailScratch : null;
        var look = HitDirLook;
        if (HasSparks(visual) && look.HasFlag(DirLook.MeleeSparks))
        {
            HitSpray().SpawnMelee(contact, new ReadOnlySpan<(double, Vector3)>(_trailScratch, 0, histCount), weapon, dir, new ClassicHitSpray.Look(true, 1f, spread * 0.8f), preAge);
            _hitDirSparks++;
        }
        bool drawn = false;
        if (HasBlood(visual))
            ExtrasClassicBlood(new ClassicContact(m.Victim, m.Attacker, contact, dir, visual, false, false, m.Lethal, m.Heavy, spread, BloodAmount(m.Heavy, m.Lethal), preAge, hist, histCount, weapon), ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        if (drawn) _hitDirBlood++;
        _hitDirMelee++;
        if (LogHitDir) GD.Print($"HITDIR melee P{m.Attacker}->P{m.Victim} {visual}{(m.Heavy ? " heavy" : "")}{(m.Lethal ? " lethal" : "")} contact {contact.X:F2},{contact.Y:F2},{contact.Z:F2} dir {dir.X:F2},{dir.Y:F2},{dir.Z:F2} " +
                                $"approach {dist:F2} m, path {path.Count} pts, waited {waited * 1000:F0} ms, pre-age {preAge * 1000:F0} ms, thrust {thrust:F2} spread {spread:F2}, blood {drawn} (t {_connectedFor:F2})");
        return true;
    }

    static Num.Vector3 HdN(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 HdG(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    void NoteHitDirCost()
    {
        double ms = _hitDirWatch.Elapsed.TotalMilliseconds;
        _hitDirMs += ms;
        if (ms > _hitDirMsPeak) _hitDirMsPeak = ms;
    }

    /// <summary>For the effects log: counts and the CPU cost of the directional hit look (the sprays' GPU cost is their draw calls: one per live spray).</summary>
    string HitDirLog() => $"hit dir: look {HitDirLook}, shots {_hitDirShots}, melee {_hitDirMelee} (waited {_hitDirWaited}), unpaired marks {_hitDirUnpaired}, sparks {_hitDirSparks}, blood {_hitDirBlood}, " +
                          $"live sprays {(_hitSpray is null ? 0 : _hitSpray.Live)}, cpu {(_hitDirFrames > 0 ? _hitDirMs / _hitDirFrames : 0):F4} ms/frame mean, {_hitDirMsPeak:F3} ms peak";

    /// <summary>A kill in the feed (NetworkView.Match.cs).</summary>
    void ClassicHitDirKill(KillView k)
    {
        if (HitDirLook.HasFlag(DirLook.HeadshotDecal)) ExtrasClassicKill(k);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
    }
}
