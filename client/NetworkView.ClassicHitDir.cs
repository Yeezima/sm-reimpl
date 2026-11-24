using System;
using System.Linq;
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
    static readonly bool LogHitDirPaths = CmdFlag("log-hit-dir-paths");
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
        public readonly Vector3[] Roots = new Vector3[Capacity];   // the attacker's drawn feet at each sample: the weapon's own motion is the path less the body's
        public int Count, Head;
        public string? NoBlade;   // why the last frame recorded no weapon point (diagnostics)
        public double MeleeAt = double.NegativeInfinity;
        public bool Heavy;
        public bool SlashWas;   // the air slash was on at the last record
        public int Slashes;     // air slashes seen (their attack keys)
        public long Key;
        public void Add(double t, Vector3 p, Vector3 root) { Ring[Head] = (t, p); Roots[Head] = root; Head = (Head + 1) % Capacity; if (Count < Capacity) Count++; }
        /// <summary>The feet at the points <see cref="Copy"/> returns for the same <paramref name="since"/>.</summary>
        public int CopyRoots(double since, Vector3[] into)
        {
            int n = 0;
            for (int k = 0; k < Count; k++)
            {
                int i = (Head - Count + k + Capacity) % Capacity;
                if (Ring[i].T >= since && n < into.Length) into[n++] = Roots[i];
            }
            return n;
        }
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
    readonly Vector3[] _rootScratch = new Vector3[WeaponTrail.Capacity];
    readonly List<ClassicHitDirRules.PathPoint> _relScratch = new();
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
            return HeldPose(held.Mesh) * local;
        }
        return BodyMarker(id, "RIG_Prop_Rhand")?.Invoke()?.Position;
    }

    /// <summary>
    /// The held weapon mesh's world transform from its hand bone's current pose (as the weapon rig's muzzle and magazine are read): a bone attachment's own transform follows the bone only
    /// after the skeleton's update, so read straight from it the path lagged or, before the update, stood still with the body (tester round 2, 2026-11-24: real sweeps read as thrusts).
    /// </summary>
    static Transform3D HeldPose(MeshInstance3D mesh)
    {
        Node? n = mesh.GetParent();
        while (n is not null and not BoneAttachment3D) n = n.GetParent();
        if (n is BoneAttachment3D att && att.GetParent() is Skeleton3D sk && att.BoneIdx >= 0 && att.BoneIdx < sk.GetBoneCount())
        {
            var fromAttachment = att.GlobalTransform.AffineInverse() * mesh.GlobalTransform;   // the wield offset: fixed while held
            return sk.GlobalTransform * sk.GetBoneGlobalPose(att.BoneIdx) * fromAttachment;
        }
        return mesh.GlobalTransform;
    }

    /// <summary>Why no weapon point could be read for a player (the unpaired and path-less melee log).</summary>
    string BladeMissing(int id)
    {
        CharacterViewer? c = _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
        if (c is null) return "no model for him";
        if (!GodotObject.IsInstanceValid(c)) return "his model freed";
        if (!c.IsInsideTree()) return "his model not in the scene";
        if (c.Weapons is null) return "no weapon rig";
        return "no melee weapon in hand and no right-hand marker";
    }

    /// <summary>A weapon melee attack (not a stomp: its blast keeps its own effects).</summary>
    bool WeaponMeleeActive(PlayerState s) =>
        _config is not null && ClassicHitDirRules.IsWeaponMelee(s.Alive, s.MeleeStep, s.MeleeStep > 0 && Combat.MeleeStepOf(_config, s) is { Stomp: not null }, s.JumpSlash);

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
        if (HitDirLook == DirLook.None) return;
        if (victim > 0)
        {
            var c = new ShotContact { At = _connectedFor, Delay = delay, Contact = contact, Dir = dir, Head = head, Shooter = shooter, ArmourUp = armourUp ?? true, Own = own };
            _shotContacts[victim] = c;
            NoteServerHit(shooter, contact, ToGame(contact), dir, head, armourUp, own, delay, c);
        }
        else NoteServerHit(shooter, contact, ToGame(contact), dir, head, armourUp, own, delay, null);
    }

    /// <summary>
    /// Every body or head hit the server sent in a shot record (FxImpact), whichever player this view resolved it to (or none): a mark that finds no shot of its own takes the nearest
    /// unused one ending within <see cref="ClassicHitDirRules.LooseShotReach"/> of his drawn chest (live check 2026-11-24: of 64 unpaired marks in a 3v3, 40 were such hits, resolved to
    /// nobody because this view draws the victim about a tenth of a second behind the shot, up to 2.5 m away from where the bullet struck him).
    /// </summary>
    sealed class ServerHit { public ShotContact Contact = null!; public Num.Vector3 End, DirGame; public bool Used; }
    readonly List<ServerHit> _looseScratch = new();
    readonly List<LooseHit> _looseHitScratch = new();
    double _drawLagSum, _drawLagMax; int _drawLagCount;
    readonly List<ServerHit> _serverHits = new();
    int _hitDirLooseShots;

    void NoteServerHit(int shooter, Vector3 contact, Num.Vector3 end, Vector3 dir, bool head, bool? armourUp, bool own, double delay, ShotContact? resolved)
    {
        if (HitDirLook == DirLook.None) return;
        for (int i = _serverHits.Count - 1; i >= 0; i--) if (_connectedFor - _serverHits[i].Contact.At > OwnWindow) _serverHits.RemoveAt(i);
        if (_serverHits.Count >= 64) _serverHits.RemoveAt(0);
        var c = resolved ?? new ShotContact { At = _connectedFor, Delay = delay, Contact = contact, Dir = dir, Head = head, Shooter = shooter, ArmourUp = armourUp ?? true, Own = own };
        var gd = _gameSpace is null ? new Vector3(dir.X, -dir.Z, dir.Y) : _gameSpace.Basis.Inverse() * dir;
        _serverHits.Add(new ServerHit { Contact = c, End = end, DirGame = new Num.Vector3(gd.X, gd.Y, gd.Z) });
    }

    ShotContact? ServerHitNear(PlayerState o)
    {
        var chest = o.Position + new Num.Vector3(0f, 0f, 1.25f);
        // the reach grows with how far he moves in the lag this view draws the others with (lead decision 2026-11-24)
        float lag = _client is null ? 0.1f : (float)_client.CurrentInterpolationDelay.TotalSeconds;
        _drawLagSum += lag; _drawLagCount++; if (lag > _drawLagMax) _drawLagMax = lag;
        float speed = new Num.Vector2(o.Velocity.X, o.Velocity.Y).Length();
        float reach = LooseReach(speed, lag);
        _looseScratch.Clear(); _looseHitScratch.Clear();
        foreach (var h in _serverHits)
        {
            if (h.Used || h.Contact.Shooter == o.PlayerId || _connectedFor - h.Contact.At > (h.Contact.Own ? OwnWindow : RemoteWindow)) continue;
            if (TeamOf(h.Contact.Shooter) is var t && t != Team.None && t == o.Team) continue;
            _looseScratch.Add(h); _looseHitScratch.Add(new LooseHit(h.End, h.DirGame));
        }
        int pick = PickLooseHit(chest, reach, _looseHitScratch);
        if (pick < 0) return null;
        var best = _looseScratch[pick];
        if (LogHitDir) HitDirPrint($"HITDIR loose hit for P{o.PlayerId}: P{best.Contact.Shooter}'s, end {Num.Vector3.Distance(best.End, chest):F2} m from his chest, line {LineDistance(chest, best.End, best.DirGame):F2} m; reach {reach:F2} m (speed {speed:F1} m/s, lag {lag * 1000:F0} ms; {_looseHitScratch.Count} candidates)");
        best.Used = true;
        int wrong = 0;   // resolved to another player: no longer his
        foreach (var kv in _shotContacts) if (kv.Value == best.Contact) { wrong = kv.Key; break; }
        if (wrong != 0) _shotContacts.Remove(wrong);
        // drawn where this view shows him: his body's surface toward the shot, at the struck height
        var c = best.Contact;
        var feet = ToGodot(o.Position);
        float height = Math.Clamp(best.End.Z - o.Position.Z, 0.9f, 1.8f);
        var back = -c.Dir; back.Y = 0f;
        back = back.LengthSquared() > 1e-6f ? back.Normalized() : Vector3.Forward;
        return new ShotContact { At = c.At, Delay = c.Delay, Contact = feet + Vector3.Up * height + back * BodySurface, Dir = c.Dir, Head = c.Head, Shooter = c.Shooter, ArmourUp = c.ArmourUp, Own = c.Own };
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
            bool fresh = _shotContacts.TryGetValue(o.PlayerId, out var shot) && _connectedFor - shot.At <= (shot.Own ? OwnWindow : RemoteWindow);
            // the server's body or head hit that this view resolved to nobody or to another player (it draws the others interpolated, a step behind the shot): the nearest one on him
            if (!fresh && ServerHitNear(o) is { } loose) { shot = loose; fresh = true; _hitDirLooseShots++; }
            if (fresh)
            {
                _shotContacts.Remove(o.PlayerId);
                foreach (var h in _serverHits) if (h.Contact == shot) h.Used = true;
                var visual = Classify(shield, health, ShieldLeftAfterMark(shield, health, shot.ArmourUp));
                _hitDirShots++;
                NoteHitDirFocus(shot.Contact, shot.Dir);
                bool drawn = false;
                if (HasBlood(visual))
                {
                    var c = new ClassicContact(o.PlayerId, shot.Shooter, shot.Contact, shot.Dir, visual, true, shot.Head, lethal, false, 0.3f, BloodAmount(false, lethal), -Math.Max(0.0, shot.At + shot.Delay - _connectedFor), null, 0, null);   // the spray waits for the tracer's arrival
                    ExtrasClassicBlood(c, ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
                    if (drawn) _hitDirBlood++;
                }
                if (lethal && shot.Head) ExtrasClassicFatalHeadshot(o.PlayerId, shot.Shooter, shot.Contact, shot.Dir);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
                if (LogHitDir) HitDirPrint($"HITDIR shot P{shot.Shooter}->P{o.PlayerId} {(shot.Head ? "head" : "body")} {visual}{(lethal ? " lethal" : "")} shield {shield} health {health} armour-before {shot.ArmourUp} blood {drawn} (t {_connectedFor:F2})");
                return false;
            }
            // a bull-rush charge's connect (owner 2026-11-24; NetworkView.ClassicHitDirCharge.cs)
            if (ClassicChargeMark(o, shield, health, lethal)) return true;
            // melee: the attacker who just struck in reach
            var cands = _candidateScratch;
            cands.Clear();
            AddCandidate(cands, _client.Predicted, own: true);
            foreach (var p in _lastOthers) AddCandidate(cands, p, own: false);
            int attacker = PickAttacker(o.PlayerId, o.Team, o.Position, cands);
            if (attacker == 0)
            {
                _hitDirUnpaired++;
                if (LogHitDir) HitDirPrint($"HITDIR mark P{o.PlayerId} shield {shield} health {health}: no shot and no weapon melee attacker in reach (left to the existing look; t {_connectedFor:F2}); why: {UnpairedReason(o)}");
                return false;
            }
            var trail = _weaponTrails[attacker];
            _meleePairedAt[o.PlayerId] = _connectedFor;
            if (!_contactLedger.First(o.PlayerId, attacker, trail.Key, _connectedFor)) return true;
            bool before = _armourFlash.ArmourUp(o.PlayerId, _connectedFor) ?? true;
            _meleePending.Add(new MeleePending { Victim = o.PlayerId, Attacker = attacker, Shield = shield, Health = health, ArmourBefore = before, Lethal = lethal, Heavy = trail.Heavy, Key = trail.Key, At = _connectedFor });
            if (LogHitDir) HitDirPrint($"HITDIR mark P{o.PlayerId} paired with P{attacker} (key {trail.Key}) shield {shield} health {health} armour-before {before}{(lethal ? " lethal" : "")} (t {_connectedFor:F2})");
            return true;
        }
        finally { NoteHitDirCost(); }
    }

    void AddCandidate(List<MeleeCandidate> into, PlayerState s, bool own)
    {
        if (!_weaponTrails.TryGetValue(s.PlayerId, out var t)) return;
        into.Add(new MeleeCandidate(s.PlayerId, s.Team, s.Position, s.Yaw, _connectedFor - t.MeleeAt, s.Alive, t.Heavy, own, Slash: t.Key < 0));
    }

    /// <summary>Per frame, before the shots and marks (UpdateFx): records every attacking player's weapon path and resolves the melee contacts that wait for the drawn weapon.</summary>
    void ClassicHitDirFrame(PlayerState me)
    {
        if (_client is null || _config is null) return;
        if (HitDirLook == DirLook.None) { _meleePending.Clear(); _shotContacts.Clear(); _serverHits.Clear(); return; }
        // the spray pool (32 meshes) and its shader are made on the first effects frame after the join, not on the first hit mid-fight (live 8v8 check 2026-11-24: made lazily
        // on the first hit, it cost one frame of about 176 ms); left out of the per-frame cost below
        if (_hitSpray is null) HitSpray().Prewarm(ToGodot(me.Position));
        _hitDirWatch.Restart();
        Record(me, own: true);
        foreach (var p in _lastOthers) Record(p, own: false);
        TrackRush(me);
        foreach (var p in _lastOthers) TrackRush(p);
        ChargeFxFrame();
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
        bool slash = s.Alive && s.JumpSlash;
        if (slash && !t.SlashWas) t.Slashes++;
        t.SlashWas = slash;
        if (WeaponMeleeActive(s))
        {
            if (slash) { t.Key = ClassicHitDirRules.SlashKey(t.Slashes); t.Heavy = false; }   // owner 2026-11-24: the air slash is weapon melee
            else
            {
                long tick = own ? _client!.Tick : _client!.LatestSnapshotTick;
                long start = MeleeHitGate.StartTick(tick, s.MeleeTime, _config!.TickSeconds);
                t.Key = ClassicHitDirRules.AttackKey(start, s.MeleeStep, s.MeleeHeavy);
                t.Heavy = s.MeleeHeavy;
            }
            t.MeleeAt = _connectedFor;
        }
        if (_connectedFor - t.MeleeAt > 0.9) return;   // only around an attack
        if (BladePoint(s.PlayerId) is { } p) { t.Add(_connectedFor, p, ToGodot(s.Position)); t.NoBlade = null; }
        else t.NoBlade = BladeMissing(s.PlayerId);
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
        if (idx >= 0 && dist < ContactReach)
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
        // the thrust measure from the weapon's own motion, the attacker's body motion (a lunge, a step) taken out: with it in, every strike read as a thrust (tester round 2)
        float thrust;
        string dirSource;
        Num.Vector3? own = null;
        bool rooted = idx >= 0 && dist < ContactReach && trail.CopyRoots(m.At - 0.6, _rootScratch) == n;
        if (rooted)
        {
            _relScratch.Clear();
            for (int k = 0; k < n; k++) _relScratch.Add(new ClassicHitDirRules.PathPoint(path[k].Time, path[k].Position - HdN(_rootScratch[k])));
            own = WeaponDirection(_relScratch, contactAt);
        }
        if (own is { } ow && _gameSpace is not null && rooted)
        {
            var og = _gameSpace.Basis.Inverse() * HdG(ow);   // game space, Z up
            var head = ToGame(HdG(path[idx].Position)) - ToGame(_rootScratch[idx]) - new Num.Vector3(0f, 0f, ShoulderHeight);
            thrust = ThrustOf(new Num.Vector3(og.X, og.Y, og.Z), head);
            dirSource = $"weapon (own motion thrust {thrust:F2})";
        }
        else { thrust = 0f; dirSource = idx >= 0 && dist < ContactReach ? "weapon (own motion unknown: a sweep)" : idx >= 0 ? $"weapon too far ({dist:F2} m): facing, a sweep" : $"no path ({trail.NoBlade ?? "no point in the last 0.6 s"}): facing, a sweep"; }
        float spread = SpreadFor(thrust);
        if (LogHitDirPaths && _gameSpace is not null && n > 0 && trail.CopyRoots(m.At - 0.6, _rootScratch) == n)
        {
            // test-fixture aid: the recorded path in game space relative to the attacker's feet at contact, with his yaw (tests take a real sweep from here)
            var sb = new System.Text.StringBuilder($"HITDIRPATH P{m.Attacker}->P{m.Victim} yaw {attacker.Yaw:F3} contact-t {contactAt:F3}:");
            var feet0 = ToGame(_rootScratch[n - 1]);
            for (int k = 0; k < n; k++)
            {
                var wp = ToGame(_trailScratch[k].P) - feet0; var rp = ToGame(_rootScratch[k]) - feet0;
                sb.Append(System.FormattableString.Invariant($" [{_trailScratch[k].T - contactAt:F3} {wp.X:F2} {wp.Y:F2} {wp.Z:F2} | {rp.X:F2} {rp.Y:F2}]"));
            }
            HitDirPrint(sb.ToString());
        }
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
        NoteHitDirFocus(contact, dir);
        bool drawn = false;
        if (HasBlood(visual))
            ExtrasClassicBlood(new ClassicContact(m.Victim, m.Attacker, contact, dir, visual, false, false, m.Lethal, m.Heavy, spread, BloodAmount(m.Heavy, m.Lethal), preAge, hist, histCount, weapon), ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        if (drawn) _hitDirBlood++;
        _hitDirMelee++;
        if (LogHitDir) HitDirPrint($"HITDIR melee P{m.Attacker}->P{m.Victim} {visual}{(m.Heavy ? " heavy" : "")}{(m.Lethal ? " lethal" : "")} contact {contact.X:F2},{contact.Y:F2},{contact.Z:F2} dir {dir.X:F2},{dir.Y:F2},{dir.Z:F2} " +
                                $"approach {dist:F2} m, path {path.Count} pts, dir from {dirSource}, waited {waited * 1000:F0} ms, pre-age {preAge * 1000:F0} ms, thrust {thrust:F2} spread {spread:F2}, blood {drawn} (t {_connectedFor:F2})");
        return true;
    }

    static Num.Vector3 HdN(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 HdG(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    void NoteHitDirCost()
    {
        double ms = _hitDirWatch.Elapsed.TotalMilliseconds;
        _hitDirMs += ms;
        if (ms > _hitDirMsPeak) _hitDirMsPeak = ms;
        if (ms > SlowSectionMs) { _hitDirSlow++; _hitDirSlowMs += ms; }
    }

    /// <summary>A timed section longer than this (ms) is counted apart in the cost line: a stall (the thread preempted on a loaded PC, a GC pause) rather than the steady work.</summary>
    const double SlowSectionMs = 2.0;
    int _hitDirSlow;
    double _hitDirSlowMs;

    /// <summary>A <c>--log-hit-dir</c> line, its print time left out of the measured cost (2026-11-24: prints to a busy log made up nearly all of the cost the tester's runs showed).</summary>
    void HitDirPrint(string line)
    {
        bool running = _hitDirWatch.IsRunning;
        if (running) _hitDirWatch.Stop();
        GD.Print(line);
        if (running) _hitDirWatch.Start();
    }

    // ---- unpaired marks: why (--log-hit-dir only; the attribution check of 2026-11-24) ----

    readonly (double T, int Shooter, Num.Vector3 End, ShotHit Hit, int Victim, bool Projectile)[] _shotSeen = new (double, int, Num.Vector3, ShotHit, int, bool)[64];
    int _shotSeenNext;
    readonly (double T, Num.Vector3 At)[] _blastSeen = new (double, Num.Vector3)[16];
    int _blastSeenNext;
    readonly Dictionary<string, int> _unpairedWhy = new();

    /// <summary>Every drawn shot record (FxRemoteShot): where it ended, what the server said it hit and which player the client resolved it to.</summary>
    void NoteShotSeen(int shooter, Num.Vector3 end, ShotHit hit, int victim, bool projectile)
    {
        if (!LogHitDir) return;
        _shotSeen[_shotSeenNext] = (_connectedFor, shooter, end, hit, victim, projectile);
        _shotSeenNext = (_shotSeenNext + 1) % _shotSeen.Length;
    }

    /// <summary>Every drawn detonation (FxDetonation).</summary>
    void NoteBlastSeen(Num.Vector3 at)
    {
        if (!LogHitDir) return;
        _blastSeen[_blastSeenNext] = (_connectedFor, at);
        _blastSeenNext = (_blastSeenNext + 1) % _blastSeen.Length;
    }

    /// <summary>The likeliest source of a mark that paired with nothing, from what this view drew just before it; the category is also counted for the summary line.</summary>
    string UnpairedReason(PlayerState o)
    {
        string why = UnpairedReasonText(o, out string category);
        _unpairedWhy[category] = _unpairedWhy.GetValueOrDefault(category) + 1;
        return category + ": " + why;
    }

    string UnpairedReasonText(PlayerState o, out string category)
    {
        var feet = o.Position;
        if (_shotContacts.TryGetValue(o.PlayerId, out var stale))
        {
            category = "shot-too-old";
            return $"a shot on him by P{stale.Shooter} {_connectedFor - stale.At:F2} s ago (window {(stale.Own ? OwnWindow : RemoteWindow):F2} s)";
        }
        (double T, int Shooter, Num.Vector3 End, ShotHit Hit, int Victim, bool Projectile)? near = null;
        float nearD = float.MaxValue;
        foreach (var e in _shotSeen)
        {
            if (e.T <= 0 || _connectedFor - e.T > 0.6 || e.Shooter == o.PlayerId) continue;
            float d = Num.Vector3.Distance(e.End, feet + new Num.Vector3(0f, 0f, 1.2f));
            if (d < nearD) { nearD = d; near = e; }
        }
        if (near is { } n && nearD < 2.5f)
        {
            if (n.Hit is ShotHit.Body or ShotHit.Head && n.Victim != o.PlayerId)
            {
                category = "shot-resolved-elsewhere";
                return $"a {n.Hit} hit by P{n.Shooter} {nearD:F2} m from him {_connectedFor - n.T:F2} s ago was resolved to {(n.Victim > 0 ? "P" + n.Victim : "nobody")}";
            }
            if (n.Hit == ShotHit.World)
            {
                category = "shot-world-near";
                return $"a shot by P{n.Shooter} ended on the level {nearD:F2} m from him {_connectedFor - n.T:F2} s ago{(n.Projectile ? " (projectile)" : "")}";
            }
        }
        foreach (var b in _blastSeen)
            if (b.T > 0 && _connectedFor - b.T <= 0.8 && Num.Vector3.Distance(b.At, feet) < 8f)
            {
                category = "blast";
                return $"a detonation {Num.Vector3.Distance(b.At, feet):F1} m away {_connectedFor - b.T:F2} s ago";
            }
        PlayerState? best = null;
        float bestD = float.MaxValue;
        void Look(PlayerState p)
        {
            if (!p.Alive || p.PlayerId == o.PlayerId || p.Team == o.Team) return;
            var d = p.Position - feet; d.Z = 0f;
            float l = d.Length();
            if (l < bestD) { bestD = l; best = p; }
        }
        if (_client is { } c && c.PlayerId != o.PlayerId && c.Predicted.PlayerId == c.PlayerId) Look(c.Predicted);
        foreach (var p in _lastOthers) Look(p);
        string enemy = best is null ? "none" : $"P{best.Value.PlayerId} at {bestD:F1} m";
        if (best is { } e2 && bestD < 6f)
        {
            double since = _weaponTrails.TryGetValue(e2.PlayerId, out var t) ? _connectedFor - t.MeleeAt : double.PositiveInfinity;
            var step = e2.MeleeStep > 0 && _config is not null ? Combat.MeleeStepOf(_config, e2) : null;
            bool stomp = step?.Stomp is not null;
            string rush = e2.Rush != RushPhase.None ? $"rush phase {e2.Rush}" : "";
            string doing = e2.Charging ? "charging" : rush.Length > 0 ? rush : e2.JumpSlash ? "air slash" : stomp ? "stomp" : e2.MeleeStep > 0 ? $"melee step {e2.MeleeStep}" : !e2.Grounded ? "airborne" : "no melee";
            doing += $"; his jump {e2.Jump}{(e2.Grounded ? "" : " airborne")}, his reaction {e2.Reaction}, victim reaction {o.Reaction}, victim jump {o.Jump}{(o.Grounded ? "" : " airborne")} vz {o.Velocity.Z:F1}{LastBlastText(feet)}";
            category = e2.Charging ? "charge" : e2.Rush != RushPhase.None ? "rush-" + e2.Rush : e2.JumpSlash ? "air-slash" : stomp ? "stomp" : e2.MeleeStep > 0 || since < 1.0 ? "melee-unpaired" : !e2.Grounded ? "airborne-enemy" : "no-source-near";
            return $"nearest enemy {enemy}, {doing}, last weapon melee {(double.IsInfinity(since) ? "never" : $"{since:F2} s ago")}";
        }
        category = near is null ? "no-shot-drawn" : "no-shot-near";
        return near is null ? $"no shot drawn in 0.6 s; nearest enemy {enemy}" : $"nearest drawn shot end {nearD:F1} m away; nearest enemy {enemy}";
    }

    /// <summary>The nearest detonation this view drew in the last second, at any distance (for the unpaired log).</summary>
    string LastBlastText(Num.Vector3 feet)
    {
        float best = float.MaxValue; double age = 0;
        foreach (var b in _blastSeen) if (b.T > 0 && _connectedFor - b.T <= 1.0) { float d = Num.Vector3.Distance(b.At, feet); if (d < best) { best = d; age = _connectedFor - b.T; } }
        return best < float.MaxValue ? $", a detonation {best:F1} m away {age:F2} s ago" : ", no detonation drawn in 1 s";
    }

    string UnpairedSummary() => _unpairedWhy.Count == 0 ? "" : " (" + string.Join(", ", _unpairedWhy.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)) + ")";

    /// <summary>For the effects log: counts and the CPU cost of the directional hit look (the sprays' GPU cost is their draw calls: one per live spray).</summary>
    string HitDirLog() => $"hit dir: look {HitDirLook}, shots {_hitDirShots} ({_hitDirLooseShots} by the server's hit near him; draw lag mean {(_drawLagCount > 0 ? _drawLagSum / _drawLagCount * 1000 : 0):F0} ms, max {_drawLagMax * 1000:F0} ms), melee {_hitDirMelee} (waited {_hitDirWaited}), charges {_hitDirCharges}, unpaired marks {_hitDirUnpaired}{UnpairedSummary()}, sparks {_hitDirSparks}, blood {_hitDirBlood}, " +
                          $"live sprays {(_hitSpray is null ? 0 : _hitSpray.Live)}, cpu {(_hitDirFrames > 0 ? _hitDirMs / _hitDirFrames : 0):F4} ms/frame mean, {_hitDirMsPeak:F3} ms peak, " +
                          $"{_hitDirSlow} sections over {SlowSectionMs:F0} ms ({_hitDirSlowMs:F0} ms; without them {(_hitDirFrames > 0 ? (_hitDirMs - _hitDirSlowMs) / _hitDirFrames : 0):F4} ms/frame)";

    /// <summary>A kill in the feed (NetworkView.Match.cs).</summary>
    void ClassicHitDirKill(KillView k)
    {
        if (HitDirLook.HasFlag(DirLook.HeadshotDecal)) ExtrasClassicKill(k);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
    }
}
