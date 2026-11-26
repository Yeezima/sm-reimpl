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
/// the Extended profile's: <see cref="ExtrasClassicBlood"/>, <see cref="ExtrasClassicFatalHeadshot"/>; the shot's sparks follow the confirmed class, <see cref="ClassicHitDirRules.GunfireMarkSparks"/>).
/// A victim keeps every drawn shot on him in order (a rapid run of own shots is predicted ahead of its marks: each mark takes the oldest; review of 2b89de15, finding 3).</item>
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
    /// <remarks>Wargear: the hitting weapon's data name when known (gore-streak: its size and the plasma / melta exclusion). History holds the weapon's drawn points on both sides of the contact (times from it, before it negative; third design, owner answer 4); Exit: a thrust that starts on the
    /// exit side; Charge: a bull-rush connect.</remarks>
    public readonly record struct ClassicContact(int Victim, int Attacker, Vector3 Contact, Vector3 Dir, HitVisual Visual, bool Gunfire, bool Head, bool Lethal, bool Heavy,
        float Spread, float Amount, double PreAge, (double T, Vector3 P)[]? History, int HistoryCount, Func<Vector3?>? Weapon, bool Exit = false, bool Charge = false, string? Wargear = null);

    /// <summary>Added content (the Extended profile's blood, Extras/Gore): the red directional spray for a contact; <paramref name="drawn"/> set when it was.</summary>
    partial void ExtrasClassicBlood(ClassicContact c, ref bool drawn);
    /// <summary>Added content: a fatal gunfire headshot's contact (world) and shot direction, for the wall decal behind the head.</summary>
    partial void ExtrasClassicFatalHeadshot(int victim, int shooter, Vector3 contact, Vector3 dir);
    /// <summary>Added content: a kill in the feed (the decal pairs a headshot kill with its shot).</summary>
    partial void ExtrasClassicKill(KillView k);
    /// <summary>Added content: prepare the blood's shader at the join (with the sparks' prewarm).</summary>
    partial void ExtrasClassicPrewarm(Vector3 at);
    /// <summary>Added content: a head hit drawn on a player (the decal's shot when the lethal mark pairs with none).</summary>
    partial void ExtrasClassicHeadContact(int victim, int shooter, Vector3 contact, Vector3 dir);
    /// <summary>Added content (gore-streak, Extras/Gore): a detonation drawn at <paramref name="at"/> (world) by <paramref name="wargear"/>'s projectile, for pairing a later unpaired health mark as an explosion.</summary>
    partial void ExtrasClassicBlast(Vector3 at, string wargear);
    /// <summary>Added content (gore-streak, Extras/Gore): a confirmed damage that paired with no shot, weapon melee or charge (a blast, a stomp, a landing); <paramref name="drawn"/> set when a streak was drawn for it.</summary>
    partial void ExtrasClassicUnpaired(PlayerState o, bool shield, bool health, bool lethal, ref bool drawn);

    // ---- weapon paths ----

    sealed class WeaponTrail
    {
        public const int Capacity = 48;
        public readonly (double T, Vector3 P)[] Ring = new (double, Vector3)[Capacity];
        public readonly Vector3[] Roots = new Vector3[Capacity];
        public readonly long[] Keys = new long[Capacity];        // the attack key each sample was recorded under (lead decision 8)   // the attacker's drawn feet at each sample: the weapon's own motion is the path less the body's
        public int Count, Head;
        public string? NoBlade;   // why the last frame recorded no weapon point (diagnostics)
        public double MeleeAt = double.NegativeInfinity;
        public bool Heavy;
        public bool SlashWas;   // the air slash was on at the last record
        public int Slashes;     // air slashes seen (their attack keys)
        public long Key;
        /// <summary>The attacks recorded lately, the oldest dropped (review of 2b89de15, findings 1 and 2).</summary>
        public readonly List<AttackSpan> Spans = new(8);
        public int CurStep; public bool CurHeavy;
        /// <summary>The span being recorded now (index into <see cref="Spans"/>), -1 between attacks.</summary>
        public int Cur = -1;
        public void StartSpan(AttackSpan sp)
        {
            if (Spans.Count >= 8) Spans.RemoveAt(0);
            Spans.Add(sp);
            Cur = Spans.Count - 1;
        }
        public void Add(double t, Vector3 p, Vector3 root, long key) { Ring[Head] = (t, p); Roots[Head] = root; Keys[Head] = key; Head = (Head + 1) % Capacity; if (Count < Capacity) Count++; }
        /// <summary>The points (and feet) from <paramref name="since"/> on recorded under <paramref name="key"/>, oldest first: a previous combo step never gives the direction.</summary>
        public int CopyKeyed(double since, long key, (double T, Vector3 P)[] into, Vector3[] roots)
        {
            int n = 0;
            for (int k = 0; k < Count; k++)
            {
                int i = (Head - Count + k + Capacity) % Capacity;
                if (Ring[i].T >= since && Keys[i] == key && n < into.Length) { into[n] = Ring[i]; roots[n] = Roots[i]; n++; }
            }
            return n;
        }
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
    readonly (double T, Vector3 P)[] _histScratch = new (double, Vector3)[WeaponTrail.Capacity];
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

    sealed class ShotContact { public double At, Delay; public Vector3 Contact; public Vector3 Dir; public bool Head; public int Shooter; public bool ArmourUp; public bool Own; public bool SparksDrawn; public string? Wargear; }
    /// <summary>Per victim, the shots drawn on him, oldest first (at most <see cref="ShotQueueMax"/>): a mark takes the oldest fresh one (review of 2b89de15, finding 3).</summary>
    readonly Dictionary<int, List<ShotContact>> _shotContacts = new();
    const int ShotQueueMax = 8;
    /// <summary>The server tick the other players (and their damage marks) are drawn at this frame (NetworkView.cs, NetworkView.Observer.cs).</summary>
    double _othersTick;

    void QueueShot(int victim, ShotContact c)
    {
        if (!_shotContacts.TryGetValue(victim, out var q)) _shotContacts[victim] = q = new List<ShotContact>(ShotQueueMax);
        if (q.Count >= ShotQueueMax) q.RemoveAt(0);
        q.Add(c);
    }

    /// <summary>The oldest shot on <paramref name="victim"/> still within its window, taken off his queue (older ones dropped); null when none.</summary>
    ShotContact? TakeShot(int victim)
    {
        if (!_shotContacts.TryGetValue(victim, out var q)) return null;
        while (q.Count > 0)
        {
            var c = q[0];
            q.RemoveAt(0);
            if (_connectedFor - c.At <= (c.Own ? OwnWindow : RemoteWindow)) return c;
        }
        return null;
    }

    /// <summary>The newest shot left on <paramref name="victim"/> (for the unpaired log: too old by then).</summary>
    ShotContact? StaleShot(int victim) => _shotContacts.TryGetValue(victim, out var q) && q.Count > 0 ? q[^1] : null;

    sealed class MeleePending { public int Victim, Attacker; public bool Shield, Health, ArmourBefore, Lethal, Heavy; public long Key; public double At; public int Frames; }
    readonly List<MeleePending> _meleePending = new();
    readonly ContactLedger _contactLedger = new();
    /// <summary>Own melee reports per (victim, own id, attack key): a second damage under one key keeps the old fan (the mark does not draw it).</summary>
    readonly ContactLedger _ownReportKeys = new();
    (int Victim, float ShieldAfter, double At) _ownMeleeReport = (0, 0f, double.NegativeInfinity);

    int _hitDirShots, _hitDirMelee, _hitDirUnpaired, _hitDirSparks, _hitDirBlood, _hitDirWaited;
    double _hitDirMs, _hitDirMsPeak;
    int _hitDirFrames;
    readonly System.Diagnostics.Stopwatch _hitDirWatch = new();

    /// <summary>A shot drawn on a player (FxImpact): remembered for the confirmation of its damage. Own shots wait for the server's mark; another player's arrives with it.</summary>
    void NoteShotContact(int victim, int shooter, Vector3 contact, Vector3 dir, bool head, bool? armourUp, bool own, double delay, string? wargear = null)
    {
        var look = HitDirLook;
        if (look == DirLook.None) return;
        if (victim > 0)
        {
            var c = new ShotContact { At = _connectedFor, Delay = delay, Contact = contact, Dir = dir, Head = head, Shooter = shooter, ArmourUp = armourUp ?? true, Own = own,
                                      SparksDrawn = !SparksWaitForMark(look, victim, _client?.PlayerId ?? 0, own), Wargear = wargear };   // owner answer 5: the own shots' sparks at the impact
            QueueShot(victim, c);
            NoteServerHit(shooter, contact, ToGame(contact), dir, head, armourUp, own, delay, c, victim);
            if (head) ExtrasClassicHeadContact(victim, shooter, contact, dir);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        }
        else NoteServerHit(shooter, contact, ToGame(contact), dir, head, armourUp, own, delay, null, 0, wargear);
    }

    /// <summary>
    /// Every body or head hit the server sent in a shot record (FxImpact), whichever player this view resolved it to (or none): a mark that finds no shot of its own takes the nearest
    /// unused one ending within <see cref="ClassicHitDirRules.LooseShotReach"/> of his drawn chest (live check 2026-11-24: of 64 unpaired marks in a 3v3, 40 were such hits, resolved to
    /// nobody because this view draws the victim about a tenth of a second behind the shot, up to 2.5 m away from where the bullet struck him).
    /// </summary>
    sealed class ServerHit { public ShotContact Contact = null!; public Num.Vector3 End, DirGame; public bool Used; public int ResolvedTo; }

    /// <summary>How far a server hit's end is from the living player it was resolved to (infinite when he is gone or dead).</summary>
    float ResolvedDistance(ServerHit h)
    {
        if (h.ResolvedTo <= 0) return float.PositiveInfinity;
        foreach (var p in _lastOthers)
            if (p.PlayerId == h.ResolvedTo) return p.Alive ? Num.Vector3.Distance(h.End, p.Position + new Num.Vector3(0f, 0f, 1.25f)) : float.PositiveInfinity;
        return float.PositiveInfinity;
    }

    /// <summary>Per (victim, slash key): the damages seen under that slash (its landing blast first, then the blade).</summary>
    readonly Dictionary<(int, long), int> _slashDamages = new();

    /// <summary>Where a shot entered the victim as he is drawn now (Godot world): his body's surface toward the shooter at the struck height.</summary>
    Vector3 ShotEntry(PlayerState o, ShotContact shot)
    {
        var feet = ToGodot(o.Position);
        float height = Math.Clamp(ToGame(shot.Contact).Z - o.Position.Z, 0.9f, 1.85f);
        var back = -shot.Dir; back.Y = 0f;
        back = back.LengthSquared() > 1e-6f ? back.Normalized() : Vector3.Forward;
        return feet + Vector3.Up * height + back * BodySurface;
    }

    /// <summary>The remote players' draw lag in use (s).</summary>
    double DrawLag() => _client is null ? 0.1 : _client.CurrentInterpolationDelay.TotalSeconds;

    /// <summary>Where a shot leaves the victim (Godot world): the body axis at the struck height pushed out along the shot (lead decision 7).</summary>
    Vector3 ShotExit(PlayerState o, ShotContact shot)
    {
        var g = ToGame(shot.Contact);
        var axis = new Num.Vector3(o.Position.X, o.Position.Y, Math.Clamp(g.Z, o.Position.Z + 0.9f, o.Position.Z + 1.85f));
        var gd = _gameSpace is null ? new Vector3(shot.Dir.X, -shot.Dir.Z, shot.Dir.Y) : _gameSpace.Basis.Inverse() * shot.Dir;
        return ToGodot(ExitContact(axis, new Num.Vector3(gd.X, gd.Y, gd.Z)));
    }

    /// <summary>The own melee report's directional decision (review finding 1): the own attacker as a candidate, the same pairing test the mark uses.</summary>
    bool OwnMeleeWillPair(PlayerState victim)
    {
        if (_client is null || !_weaponTrails.TryGetValue(_client.PlayerId, out var t)) return false;
        var me = _client.Predicted;
        // the report comes with the newest snapshot: its damage landed at or before that tick (review of 2b89de15, finding 1: decided with the attack the mark will take)
        var span = SpanForMark(_client.LatestSnapshotTick, t.Spans);
        var own = new MeleeCandidate(me.PlayerId, me.Team, me.Position, me.Yaw, _connectedFor - t.MeleeAt, me.Alive, t.Heavy, true, Slash: span?.Slash ?? t.Key < 0);
        long key = span?.Key ?? 0;
        bool before = span is not null && !_ownReportKeys.First(victim.PlayerId, me.PlayerId, key, _connectedFor);
        return OwnReportDirectional(HitDirLook, true, victim.PlayerId, victim.Team, victim.Position, own, span is not null, before ? 1 : 0);
    }
    readonly List<ServerHit> _looseScratch = new();
    readonly List<LooseHit> _looseHitScratch = new();
    double _drawLagSum, _drawLagMax; int _drawLagCount;
    readonly List<ServerHit> _serverHits = new();
    int _hitDirLooseShots;

    void NoteServerHit(int shooter, Vector3 contact, Num.Vector3 end, Vector3 dir, bool head, bool? armourUp, bool own, double delay, ShotContact? resolved, int resolvedTo, string? wargear = null)
    {
        if (HitDirLook == DirLook.None) return;
        for (int i = _serverHits.Count - 1; i >= 0; i--) if (_connectedFor - _serverHits[i].Contact.At > OwnWindow) _serverHits.RemoveAt(i);
        if (_serverHits.Count >= 64) _serverHits.RemoveAt(0);
        var c = resolved ?? new ShotContact { At = _connectedFor, Delay = delay, Contact = contact, Dir = dir, Head = head, Shooter = shooter, ArmourUp = armourUp ?? true, Own = own, SparksDrawn = true, Wargear = wargear };
        var gd = _gameSpace is null ? new Vector3(dir.X, -dir.Z, dir.Y) : _gameSpace.Basis.Inverse() * dir;
        _serverHits.Add(new ServerHit { Contact = c, End = end, DirGame = new Num.Vector3(gd.X, gd.Y, gd.Z), ResolvedTo = resolvedTo });
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
            if (!LooseHitEligible(h.Contact.Own, h.ResolvedTo, ResolvedDistance(h))) continue;   // review finding 5
            _looseScratch.Add(h); _looseHitScratch.Add(new LooseHit(h.End, h.DirGame));
        }
        int pick = PickLooseHit(chest, reach, _looseHitScratch);
        if (pick < 0) return null;
        var best = _looseScratch[pick];
        if (LogHitDir) HitDirPrint($"HITDIR loose hit for P{o.PlayerId}: P{best.Contact.Shooter}'s, end {Num.Vector3.Distance(best.End, chest):F2} m from his chest, line {LineDistance(chest, best.End, best.DirGame):F2} m; reach {reach:F2} m (speed {speed:F1} m/s, lag {lag * 1000:F0} ms; {_looseHitScratch.Count} candidates)");
        best.Used = true;
        foreach (var kv in _shotContacts) kv.Value.Remove(best.Contact);   // resolved to another player: no longer his
        // drawn where this view shows him: his body's surface toward the shot, at the struck height
        var c = best.Contact;
        var feet = ToGodot(o.Position);
        float height = Math.Clamp(best.End.Z - o.Position.Z, 0.9f, 1.8f);
        var back = -c.Dir; back.Y = 0f;
        back = back.LengthSquared() > 1e-6f ? back.Normalized() : Vector3.Forward;
        return new ShotContact { At = c.At, Delay = c.Delay, Contact = feet + Vector3.Up * height + back * BodySurface, Dir = c.Dir, Head = c.Head, Shooter = c.Shooter, ArmourUp = c.ArmourUp, Own = c.Own, SparksDrawn = c.SparksDrawn, Wargear = c.Wargear };
    }

    /// <summary>The own hit report (UpdateHits): the victim's armour after the own melee strike, exact for the own view's mixed-or-blood choice.</summary>
    void NoteOwnMeleeReport(HitReport hits)
    {
        if (hits.LastMelee && hits.LastVictim > 0) _ownMeleeReport = (hits.LastVictim, hits.VictimShield, _connectedFor);
    }


    /// <summary>
    /// A confirmed damage on another player (his mark changed since the last record; <paramref name="nowAlive"/> false: it killed him). Returns whether a melee attacker was paired (the caller then
    /// leaves out the old fan and, with the blood, the red sparks).
    /// </summary>
    /// <param name="damages">How many damages the mark's counter moved by since the record before (two when an air slash's landing blast and blade land in one gap).</param>
    bool ClassicHitDirMark(PlayerState o, bool shield, bool health, bool nowAlive, int damages = 1)
    {
        var look = HitDirLook;
        if (look == DirLook.None || _client is null || _config is null || (!shield && !health)) return false;
        _hitDirWatch.Restart();
        try
        {
            bool lethal = !nowAlive;
            var shot = TakeShot(o.PlayerId);
            // the server's body or head hit that this view resolved to nobody or to another player (it draws the others interpolated, a step behind the shot): the nearest one on him
            if (shot is null && ServerHitNear(o) is { } loose) { shot = loose; _hitDirLooseShots++; }
            if (shot is not null)
            {
                foreach (var h in _serverHits) if (h.Contact == shot) h.Used = true;
                var visual = Classify(shield, health, ShieldLeftAfterMark(shield, health, shot.ArmourUp));
                _hitDirShots++;
                NoteHitDirFocus(shot.Contact, shot.Dir);
                double wait = Math.Max(0.0, shot.At + shot.Delay - _connectedFor);
                if (!shot.SparksDrawn && PlayerHitFxRules.DrawsHitFx(ShotHit.Body, MatchExtras, PlayerHitFxFlag))
                {
                    // the confirmed class's sparks (review of 2b89de15, finding 6): left by the impact to this mark
                    var (yellow, red) = GunfireMarkSparks(look, visual, shot.ArmourUp);
                    var at = ShotEntry(o, shot);   // on his drawn body, not where the shot met him a draw lag ago (review 3, finding 6)
                    if (yellow) ArmourSparks(at, shot.Dir, wait, o.PlayerId);
                    if (red) RedSparks(at, shot.Dir, wait);
                }
                bool drawn = false;
                if (HasBlood(visual))
                {
                    var exit = ShotExit(o, shot);   // the spray starts where the shot leaves him, along it (lead decision 7)
                    var c = new ClassicContact(o.PlayerId, shot.Shooter, exit, shot.Dir, visual, true, shot.Head, lethal, false, 0.3f, BloodAmount(false, lethal), -wait, null, 0, null, Wargear: shot.Wargear);   // the spray waits for the tracer's arrival
                    ExtrasClassicBlood(c, ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
                    if (drawn) _hitDirBlood++;
                }
                if (lethal && shot.Head) ExtrasClassicFatalHeadshot(o.PlayerId, shot.Shooter, shot.Contact, shot.Dir);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
                if (LogHitDir) HitDirPrint($"HITDIR shot P{shot.Shooter}->P{o.PlayerId} {(shot.Head ? "head" : "body")} {visual}{(lethal ? " lethal" : "")} shield {shield} health {health} armour-before {shot.ArmourUp} blood {drawn}{(drawn ? "" : HasBlood(visual) ? " (the blood look declined it: see HITDIR streak none, or no Extended blood)" : " (no blood for this hit class)")} (mark tick {(long)Math.Floor(_othersTick)}) (t {_connectedFor:F2})");
                return false;
            }
            // melee: the attacker who just struck in reach
            var cands = _candidateScratch;
            cands.Clear();
            AddCandidate(cands, _client.Predicted, own: true);
            foreach (var p in _lastOthers) AddCandidate(cands, p, own: false);
            long markTick = (long)Math.Floor(_othersTick);
            int attacker = 0;
            AttackSpan span = default;
            string? noSpan = null;
            for (int tries = 0; tries < 2 && cands.Count > 0; tries++)
            {
                int pick = PickAttacker(o.PlayerId, o.Team, o.Position, cands);
                if (pick == 0) break;
                // the attack of his whose blow the mark is (review of 2b89de15): the own prediction runs ahead into the next combo step (finding 1); a jump-pack landing blast is not the air slash (finding 2)
                if (SpanForMark(markTick, _weaponTrails[pick].Spans) is { } sp) { attacker = pick; span = sp; break; }
                noSpan = $"P{pick}'s attacks do not span the mark's tick {markTick}";
                cands.RemoveAll(c => c.Id == pick);
            }
            if (attacker != 0 && span.Slash)
            {
                // the landing blast keeps its old look; the blade, the victim's second damage under this slash (or a mark carrying both), is sprayed (review 3, finding 1)
                var sk = (o.PlayerId, span.Key);
                int seenDamages = _slashDamages.GetValueOrDefault(sk);
                bool ready = span.HitFromTick != long.MaxValue && markTick >= span.HitFromTick - 1;
                if (_slashDamages.Count > 32) _slashDamages.Clear();
                _slashDamages[sk] = seenDamages + Math.Max(1, damages);
                if (!SlashMarkPairs(seenDamages, Math.Max(1, damages), ready)) { noSpan = $"P{attacker}'s slash: its landing blast ({seenDamages} + {damages} damages, blade not ready)"; attacker = 0; }
            }
            if (attacker != 0 && _contactLedger.Seen(o.PlayerId, attacker, span.Key, _connectedFor)) attacker = 0;   // a second mark under the same attack key is other damage: it is not swallowed (review decision 10)
            // a bull-rush charge's connect, only after shots and melee found nothing, and only on contact (owner 2026-11-24; review decision 10; NetworkView.ClassicHitDirCharge.cs)
            if (attacker == 0 && ClassicChargeMark(o, shield, health, lethal)) return true;
            if (attacker == 0)
            {
                bool streak = false;
                if (health) ExtrasClassicUnpaired(o, shield, health, lethal, ref streak);   // EXTRAS-HOOK: gore-streak, a blast's or a stomp's streak (Extras/Gore)
                if (streak) _hitDirBlood++;
                _hitDirUnpaired++;
                if (LogHitDir) HitDirPrint($"HITDIR mark P{o.PlayerId} shield {shield} health {health}: no shot and no weapon melee attacker in reach (left to the existing look; t {_connectedFor:F2}); why: {UnpairedReason(o)}{(noSpan is null ? "" : "; " + noSpan)}");
                return false;
            }
            var trail = _weaponTrails[attacker];
            _contactLedger.First(o.PlayerId, attacker, span.Key, _connectedFor);
            bool before = ArmourBeforeForMark(_armourFlash.ArmourUp(o.PlayerId, _connectedFor));   // unknown: down (review finding 2)
            _meleePending.Add(new MeleePending { Victim = o.PlayerId, Attacker = attacker, Shield = shield, Health = health, ArmourBefore = before, Lethal = lethal, Heavy = SpanHeavy(span), Key = span.Key, At = _connectedFor });
            if (LogHitDir) HitDirPrint($"HITDIR mark P{o.PlayerId} paired with P{attacker} (key {span.Key}, start {span.StartTick}, mark tick {markTick}) shield {shield} health {health} armour-before {before}{(lethal ? " lethal" : "")} (t {_connectedFor:F2})");
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
        // repair 4, finding N6: in front of the camera (an observer's own position can be anywhere, and a spray outside the view is culled, so its pipeline was first built on the first hit and
        // the first spray drew its first frames late)
        if (_hitSpray is null) { var warm = _camera is { } cam ? cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 3f : ToGodot(me.Position); HitSpray().Prewarm(warm); ExtrasClassicPrewarm(warm); }   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
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
        // ticks in the server's numbering: the own attack from the prediction, another player's from the tick he is drawn at (his marks are drawn at the same tick)
        long tick = own ? _client!.Tick : (long)Math.Floor(_othersTick);
        if (WeaponMeleeActive(s))
        {
            var cur = t.Cur >= 0 && t.Cur < t.Spans.Count ? t.Spans[t.Cur] : (AttackSpan?)null;
            if (slash)
            {
                // owner 2026-11-24: the air slash is weapon melee; its blow lands its hit time after the landing (the landing's own blast is not the slash's: review of 2b89de15, finding 2)
                var key = ClassicHitDirRules.SlashKey(t.Slashes);
                if (cur is not { Slash: true } c0 || c0.Key != key) t.StartSpan(new AttackSpan(key, tick, tick, long.MaxValue, true));
                var sp = t.Spans[t.Cur];
                sp.LastTick = tick;
                if (!s.Grounded) sp.Air = true;
                if (sp.HitFromTick == long.MaxValue)
                {
                    // landed: the own prediction knows the recovery's ticks (its landing exactly); another player's record only says grounded (a buffered slash is flagged on the ground before the take-off too)
                    if (own && s.Jump == JumpPhase.Recover) sp.HitFromTick = tick - s.JumpTicks + SlashHitTicks(s);
                    else if (sp.Air && s.Grounded) sp.HitFromTick = tick + SlashHitTicks(s);
                }
                t.Spans[t.Cur] = sp;
                t.Key = key; t.Heavy = false;
            }
            else
            {
                long start = MeleeHitGate.StartTick(tick, s.MeleeTime, _config!.TickSeconds);
                // one key per swing: another player's start estimate jitters a tick or two with the snapshots (a fresh key split the swing's path and let its second damage through)
                if (cur is { Slash: false } c1 && SameAttack(c1.StartTick, t.CurStep, t.CurHeavy, start, s.MeleeStep, s.MeleeHeavy)) start = c1.StartTick;
                else
                {
                    t.StartSpan(new AttackSpan(ClassicHitDirRules.AttackKey(start, s.MeleeStep, s.MeleeHeavy), start, tick, start + StepHitTicks(s), false));
                    t.CurStep = s.MeleeStep; t.CurHeavy = s.MeleeHeavy;
                }
                var sp = t.Spans[t.Cur];
                sp.LastTick = tick;
                t.Spans[t.Cur] = sp;
                t.Key = sp.Key; t.Heavy = s.MeleeHeavy;
            }
            t.MeleeAt = _connectedFor;
        }
        else t.Cur = -1;
        if (_connectedFor - t.MeleeAt > 0.9) return;   // only around an attack
        if (BladePoint(s.PlayerId) is { } p) { t.Add(_connectedFor, p, ToGodot(s.Position), t.Key); t.NoBlade = null; }
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
        int n = trail.CopyKeyed(m.At - 0.6, m.Key, _trailScratch, _rootScratch);
        var path = _pathScratch; path.Clear();
        for (int k = 0; k < n; k++) path.Add(new ClassicHitDirRules.PathPoint(_trailScratch[k].T, HdN(_trailScratch[k].P)));
        var (idx, dist) = ClosestApproach(path, HdN(low), HdN(high));
        double waited = _connectedFor - m.At;
        if (idx >= 0 && WaitForContact(idx, path.Count, dist, waited)) { if (m.Frames == 1) _hitDirWaited++; return false; }

        var towardAttacker = ToGodot(attacker.Position) - ToGodot(v.Position); towardAttacker.Y = 0f;
        var facing = DirToGodot(new Num.Vector3(-MathF.Sin(attacker.Yaw), MathF.Cos(attacker.Yaw), 0f)).Normalized();
        Vector3 contact, dir;
        double contactAt;
        int histCount = 0, firstAfter = 0;
        if (idx >= 0 && dist < ContactReach)
        {
            contactAt = path[idx].Time;
            contact = HdG(ContactOnBody(path[idx].Position, HdN(low), HdN(high)));
            dir = WeaponDirection(path, contactAt) is { } wd ? HdG(wd) : facing;
            // the weapon's drawn points on both sides of the contact: the blood is laid along them (third design, owner answer 4); the sparks take the part after it
            for (int k = 0; k < path.Count && histCount < _histScratch.Length; k++) _histScratch[histCount++] = (path[k].Time - contactAt, HdG(path[k].Position));
            firstAfter = Math.Min(idx + 1, histCount);
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
        bool rooted = idx >= 0 && dist < ContactReach;
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
        bool exitSide = StartsAtExit(false, thrust);
        if (exitSide)
        {
            // a thrust drives through him: the spray starts on the far side, along the thrust, so it is seen from the attacker's view too (lead decision 7)
            var axis = ClosestOnSegment(HdN(contact), HdN(low), HdN(high));
            var ex = ExitContact(new Num.Vector3(axis.X, -axis.Z, axis.Y), new Num.Vector3(dir.X, -dir.Z, dir.Y));   // Godot to a Z-up frame and back (only the level part matters)
            contact = new Vector3(ex.X, ex.Z, -ex.Y);
            dirSource += ", from the exit side";
        }
        if (LogHitDirPaths && _gameSpace is not null && n > 0)
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
        bool shieldAfter = ownAttacker && _ownMeleeReport.Victim == m.Victim && _ownMeleeReport.At >= m.At - OwnReportWindow(DrawLag())
            ? _ownMeleeReport.ShieldAfter > 0f : ShieldLeftAfterMark(m.Shield, m.Health, m.ArmourBefore);
        var visual = Classify(m.Shield, m.Health, shieldAfter);
        double preAge = PreAge(_connectedFor, contactAt);
        int attackerId = m.Attacker;
        Func<Vector3?> weapon = () => BladePoint(attackerId);
        var hist = histCount > 0 ? _histScratch : null;
        var look = HitDirLook;
        if (HasSparks(visual) && look.HasFlag(DirLook.MeleeSparks))
        {
            HitSpray().SpawnMelee(contact, new ReadOnlySpan<(double, Vector3)>(_histScratch, firstAfter, histCount - firstAfter), weapon, dir, new ClassicHitSpray.Look(true, 1f, spread * 0.8f), preAge);
            _hitDirSparks++;
        }
        NoteHitDirFocus(contact, dir);
        bool drawn = false;
        if (HasBlood(visual))
            ExtrasClassicBlood(new ClassicContact(m.Victim, m.Attacker, contact, dir, visual, false, false, m.Lethal, m.Heavy, spread, BloodAmount(m.Heavy, m.Lethal), preAge, hist, histCount, weapon, Exit: exitSide, Wargear: MeleeWargear(attacker)), ref drawn);   // EXTRAS-HOOK: classic-hit-effects, the Extended blood (Extras/Gore)
        if (drawn) _hitDirBlood++;
        _hitDirMelee++;
        if (LogHitDir) HitDirPrint($"HITDIR melee P{m.Attacker}->P{m.Victim} {visual}{(m.Heavy ? " heavy" : "")}{(m.Lethal ? " lethal" : "")} contact {contact.X:F2},{contact.Y:F2},{contact.Z:F2} dir {dir.X:F2},{dir.Y:F2},{dir.Z:F2} " +
                                $"approach {dist:F2} m, path {path.Count} pts, dir from {dirSource}, waited {waited * 1000:F0} ms, pre-age {preAge * 1000:F0} ms, thrust {thrust:F2} spread {spread:F2}, blood {drawn} (t {_connectedFor:F2})");
        return true;
    }

    /// <summary>Ticks from an air slash's landing to its blow (the pack's slash hit time; one tick when it is not known).</summary>
    long SlashHitTicks(PlayerState s) =>
        _config is not null && Loadouts.JumpPack(_config, s)?.Slash is { } slash ? Math.Max(1L, (long)MathF.Round(slash.HitTime / _config.TickSeconds)) : 1L;

    /// <summary>Ticks from a melee step's start to its blow (the step's hit time; 0 when it is not known).</summary>
    long StepHitTicks(PlayerState s) =>
        _config is not null && s.MeleeStep > 0 && Combat.MeleeStepOf(_config, s) is { } m && float.IsFinite(m.HitTime) ? Math.Max(0L, (long)MathF.Round(m.HitTime / _config.TickSeconds)) : 0L;

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
        if (StaleShot(o.PlayerId) is { } stale)
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
