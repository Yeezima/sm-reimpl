using System;
using System.Collections.Generic;
using Godot;
using SmClient.ImpactRules;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;
using static SmClient.ImpactRules.GoreStreakRules;

namespace SmClient;

/// <summary>
/// gore-streak in Warfront (owner 2026-11-25: "warfront and extended, off for faithful"): one call from <c>MusouFx.MusouHitFx</c> per enemy hit event, client only (no sim, wire or
/// protocol change). The streak's source from the hit marker: 3 melee (along attacker to enemy, the plane rolled by the seed; the attacker's melee weapon sizes it), 0 with no player
/// fire support (an explosion, a seeded outward direction), 1 or 2 a shot: a detonation drawn within <see cref="WarfrontBlastSeconds"/> and <see cref="WarfrontBlastMetres"/> of the
/// hit is an explosion (radial from it; a plasma blast none), else the shooter's last weapon decides (plasma, melta or any cone weapon: none) and it runs along shooter to enemy.
/// A Death event for the same enemy in the same batch makes it lethal (bigger, a fourth band). The body height is the enemy type's chest height / 0.6 (the unit capped at
/// <see cref="GoreStreakRules.WarfrontUnitMetres"/>). Budget (lead decisions; <see cref="GoreStreakRules.AdmitStreaks"/>): one streak per enemy every 0.15 s, a killing hit always (it
/// replaces that enemy's streak, finding M1); the frame's hits wait for the view's frame and are admitted own first, killing first, nearest first: a killing hit always (repair round 3), the
/// other own ones at most 7 per 0.15 s and 48 showing, the others through a per-second token bucket taken last (finding L2), all under 64 showing (finding M2); within 45 m and none for a body under 12 px on screen.
/// Musou Mode alone never draws it (<see cref="NetworkView.StreakDraws"/>); the Ork blood keeps playing beside it.
/// </summary>
public partial class NetworkView
{
    const double WarfrontBlastSeconds = 0.25;
    const float WarfrontBlastMetres = 6f;
    readonly (double T, Vector3 At, string Wargear)[] _wfBlasts = new (double, Vector3, string)[16];
    int _wfBlastNext;
    readonly Bucket _wfBucket = new(WarfrontPerSecond, WarfrontBurst);
    readonly Dictionary<ushort, double> _wfEnemyLast = new();
    readonly HashSet<ushort> _wfDead = new();
    IReadOnlyList<MusouEvent>? _wfDeadFrom;
    ulong _wfDeadFrame = ulong.MaxValue;
    /// <summary>A Warfront streak waiting for the frame's admission.</summary>
    readonly record struct WfPending(ushort Enemy, bool Own, bool Lethal, float Distance, Vector3 Chest, Vector3 Axis, Vector3 Normal, float H, float Scale, bool Heavy, int Seed, Source Src);
    readonly List<WfPending> _wfPending = new();
    readonly List<StreakAsk> _wfAsks = new();
    readonly List<int> _wfAdmitted = new();
    readonly List<ushort> _wfStale = new();
    readonly HashSet<ushort> _wfFlushEnemies = new();
    double _wfOwnWindowAt = double.NegativeInfinity;
    int _wfOwnInWindow;
    bool _wfPrewarmed;
    /// <summary>A detonation in Warfront (from <see cref="ExtrasClassicBlast"/>'s call site: the same hook feeds both).</summary>
    void StreakWarfrontBlast(Vector3 at, string wargear)
    {
        _wfBlasts[_wfBlastNext] = (_connectedFor, at, wargear);
        _wfBlastNext = (_wfBlastNext + 1) % _wfBlasts.Length;
    }

    /// <summary>The enemies killed in this batch (finding L5: built once a batch, not scanned per hit).</summary>
    HashSet<ushort> StreakDeadThisBatch()
    {
        var events = _musouState.NewEvents;
        ulong frame = Engine.GetProcessFrames();
        if (frame == _wfDeadFrame && ReferenceEquals(events, _wfDeadFrom)) return _wfDead;
        _wfDeadFrame = frame; _wfDeadFrom = events;
        _wfDead.Clear();
        for (int i = 0; i < events.Count; i++) if (events[i].Kind == MusouEventKind.Death) _wfDead.Add(events[i].Enemy);
        return _wfDead;
    }

    /// <summary>A Musou enemy hit event (MusouFx.MusouHitFx), <paramref name="at"/> the hit point raised to the chest (game space): checked here, admitted at the view's frame (<see cref="StreakWarfrontFlush"/>).</summary>
    void StreakWarfrontHit(in MusouEvent e, Num.Vector3 at, bool own, PlayerState me, IReadOnlyList<MusouShown> shown, Num.Vector3 camera)
    {
        if (!MatchExtras.HasFlag(ContentExtras.Warfront) || !StreakDraws || _config is null || _client is null) return;
        var view = GoreStreaks();
        float dist = Num.Vector3.Distance(at, camera);
        if (dist > WarfrontMetres) { view.NoteDropped(); return; }
        bool lethal = StreakDeadThisBatch().Contains(e.Enemy);
        // one streak per enemy every 0.15 s; a killing hit always (finding M1: it replaces that enemy's streak, bigger)
        if (!lethal && _wfEnemyLast.TryGetValue(e.Enemy, out double last) && !EnemyReady(last, _connectedFor)) { view.NoteDropped(); return; }
        var type = _musouTypeOf.TryGetValue(e.Enemy, out var t) ? t : MusouEnemyType.Boy;
        float h = GoreChestHeight(type) / 0.6f;
        if (_camera is { } cam && GodotObject.IsInstanceValid(cam))
        {
            float px = ProjectedPixels(h, dist, cam.Fov, GetViewport()?.GetVisibleRect().Size.Y ?? 1080f);
            if (px < MinBodyPixels) { view.NoteDropped(); return; }
        }
        PlayerState? attacker = e.Player == 0 ? null : own ? me : MusouPlayerState(e.Player);
        var chest = ToGodot(at);
        int seed = NextStreakSeed();
        Source src;
        Weight weight;
        Vector3 axis;
        Vector3? blast = null;
        if (e.Value == 3)
        {
            src = Source.Sweep;
            weight = WeightOf(src, attacker is { } a0 ? MeleeWargear(a0) : null, attacker?.MeleeHeavy ?? false, attacker?.MeleeStep ?? 0);
            axis = attacker is { } a1 ? chest - ToGodot(a1.Position + new Num.Vector3(0f, 0f, 1.2f)) : Vector3.Zero;
        }
        else if (e.Value == 0 && e.Player == 0) { src = Source.Explosion; weight = Weight.Heavy; axis = Vector3.Zero; blast = chest - Vector3.Up * 1.2f; }
        else
        {
            // the shooter's weapon (finding L6: a shot whose weapon this view never drew cannot be shown not to be plasma or melta, so it plays none)
            string? wargear = null; DeliveryKind? delivery = null;
            if (e.Player != 0 && _fxLastShot.TryGetValue(e.Player, out var lastShot) && lastShot.Weapon >= 0 && lastShot.Weapon < _config.Weapons.Count)
            { wargear = _config.Weapons[lastShot.Weapon].Name; delivery = _config.Weapons[lastShot.Weapon].Delivery; }
            int b = StreakNearestWarfrontBlast(chest, wargear);
            if (b >= 0)
            {
                if (Excluded(_wfBlasts[b].Wargear)) return;
                src = Source.Explosion; weight = Weight.Heavy; axis = Vector3.Zero; blast = _wfBlasts[b].At;
            }
            else
            {
                if (wargear is null || Excluded(wargear, delivery)) { view.NoteDropped(); return; }
                src = Source.Gunfire; weight = WeightOf(src, wargear);
                axis = attacker is { } a2 ? chest - ToGodot(a2.Position + new Num.Vector3(0f, 0f, 1.5f)) : Vector3.Zero;
            }
        }
        if (src == Source.Explosion) axis = GsG(ExplosionAxis(GsN(chest), GsN(blast ?? chest), Num.Vector3.UnitY, seed));
        if (axis.LengthSquared() < 1e-6f)
        {
            // no attacker drawn: away from the camera, level
            var away = chest - (_camera?.GlobalPosition ?? chest + Vector3.Back); away.Y = 0f;
            axis = away.LengthSquared() > 1e-6f ? away : Vector3.Forward;
        }
        axis = axis.Normalized();
        var normal = StreakPlane(src == Source.Sweep ? Source.Thrust : src, axis, seed);   // no weapon path in Warfront: the swing's plane is rolled like a thrust's
        _wfPending.Add(new WfPending(e.Enemy, own, lethal, dist, chest, axis, normal, h, GoreStreakRules.Scale(weight, lethal), weight == Weight.Heavy, seed, src));
        if (_wfPending.Count > 512) { view.NoteDropped(_wfPending.Count - 512); _wfPending.RemoveRange(0, _wfPending.Count - 512); }
    }

    /// <summary>Warfront's per-frame streak work (from the added content's frame): the prewarm once (finding L5: before the first hit, as Classic does).</summary>
    void StreakWarfrontFrame()
    {
        if (_wfPrewarmed || !MatchExtras.HasFlag(ContentExtras.Warfront) || !StreakDraws || _camera is not { } cam || !GodotObject.IsInstanceValid(cam)) return;
        _wfPrewarmed = true;
        GoreStreaks().Prewarm(cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 3f);
    }

    /// <summary>
    /// The frame's admission (<see cref="GoreStreakView.BeforeFrame"/>): one ask per enemy (a killing one first), then <see cref="AdmitStreaks"/> (own first and nearest first, the own
    /// window and live caps, the bucket's token last) and the admitted ones drawn; the rest counted as dropped.
    /// </summary>
    void StreakWarfrontFlush()
    {
        if (_wfPending.Count == 0) return;
        var view = GoreStreaks();
        if (_connectedFor - _wfOwnWindowAt >= WarfrontOwnWindow) { _wfOwnWindowAt = _connectedFor; _wfOwnInWindow = 0; }
        _wfAsks.Clear(); _wfFlushEnemies.Clear();
        // a killing hit wins its enemy's one ask this frame
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < _wfPending.Count; i++)
            {
                var p = _wfPending[i];
                if (p.Lethal != (pass == 0) || !_wfFlushEnemies.Add(p.Enemy)) continue;
                _wfAsks.Add(new StreakAsk(p.Own, p.Lethal, p.Distance, i));
            }
        AdmitStreaks(_wfAsks, _wfOwnInWindow, view.LiveOwn, view.Live, _wfBucket, _connectedFor, _wfAdmitted);
        var admitted = _wfAdmitted;
        foreach (int i in admitted)
        {
            var p = _wfPending[i];
            if (p.Own) _wfOwnInWindow++;
            SpawnStreak(p.Chest, p.Axis, p.Normal, p.H, p.Scale, 0f, 0f, p.Lethal, p.Heavy, p.Seed, p.Src, victim: p.Enemy + 1, own: p.Own, unitCap: WarfrontUnitMetres);
            _wfEnemyLast[p.Enemy] = _connectedFor;
        }
        view.NoteDropped(_wfPending.Count - admitted.Count);
        if (_wfEnemyLast.Count > 2048) StreakWarfrontPrune();
        _wfPending.Clear();
    }

    /// <summary>Forgets the enemies whose last streak is past the per-enemy wait (lows: a full clear dropped every enemy's rate limit at once); the ones still waiting keep theirs.</summary>
    void StreakWarfrontPrune()
    {
        _wfStale.Clear();
        foreach (var (enemy, last) in _wfEnemyLast) if (EnemyReady(last, _connectedFor)) _wfStale.Add(enemy);
        foreach (var enemy in _wfStale) _wfEnemyLast.Remove(enemy);
    }

    /// <summary>The nearest recent detonation to <paramref name="chest"/> that the shooter's own weapon can have made (finding L4: the snapshot names no owner, so the shooter's last weapon must match the blast's; a thrown item, wargear "", matches any).</summary>
    int StreakNearestWarfrontBlast(Vector3 chest, string? shooterWargear)
    {
        int best = -1; float bestD = WarfrontBlastMetres;
        for (int i = 0; i < _wfBlasts.Length; i++)
        {
            var b = _wfBlasts[i];
            if (b.Wargear is null || _connectedFor - b.T > WarfrontBlastSeconds || _connectedFor < b.T) continue;
            if (b.Wargear.Length > 0 && shooterWargear != b.Wargear) continue;
            float d = b.At.DistanceTo(chest);
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
