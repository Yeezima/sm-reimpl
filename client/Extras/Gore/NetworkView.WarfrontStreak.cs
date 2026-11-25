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
/// A Death event for the same enemy in the same batch makes it lethal (bigger, a fourth branch). The body height is the enemy type's chest height / 0.6. Budget: at most
/// <see cref="GoreStreakRules.WarfrontPerFrame"/> streaks a frame from other players (the own hits always), within <see cref="GoreStreakRules.WarfrontMetres"/>, and none for a body
/// under <see cref="GoreStreakRules.MinBodyPixels"/> on screen. Musou Mode alone never draws it (<see cref="NetworkView.StreakDraws"/>); the Ork blood keeps playing beside it.
/// </summary>
public partial class NetworkView
{
    const double WarfrontBlastSeconds = 0.25;
    const float WarfrontBlastMetres = 6f;
    readonly (double T, Vector3 At, string Wargear)[] _wfBlasts = new (double, Vector3, string)[16];
    int _wfBlastNext;
    ulong _wfStreakFrame;
    int _wfStreaksThisFrame;

    /// <summary>A detonation in Warfront (from <see cref="ExtrasClassicBlast"/>'s call site: the same hook feeds both).</summary>
    void StreakWarfrontBlast(Vector3 at, string wargear)
    {
        _wfBlasts[_wfBlastNext] = (_connectedFor, at, wargear);
        _wfBlastNext = (_wfBlastNext + 1) % _wfBlasts.Length;
    }

    /// <summary>A Musou enemy hit event (MusouFx.MusouHitFx), <paramref name="at"/> the hit point raised to the chest (game space).</summary>
    void StreakWarfrontHit(in MusouEvent e, Num.Vector3 at, bool own, PlayerState me, IReadOnlyList<MusouShown> shown, Num.Vector3 camera)
    {
        if (!MatchExtras.HasFlag(ContentExtras.Warfront) || !StreakDraws || _config is null || _client is null) return;
        if (Num.Vector3.DistanceSquared(at, camera) > WarfrontMetres * WarfrontMetres) { GoreStreaks().NoteDropped(); return; }
        ulong frame = Engine.GetProcessFrames();
        if (frame != _wfStreakFrame) { _wfStreakFrame = frame; _wfStreaksThisFrame = 0; }
        if (!own && _wfStreaksThisFrame >= WarfrontPerFrame) { GoreStreaks().NoteDropped(); return; }
        var type = _musouTypeOf.TryGetValue(e.Enemy, out var t) ? t : MusouEnemyType.Boy;
        float h = GoreChestHeight(type) / 0.6f;
        if (_camera is { } cam && GodotObject.IsInstanceValid(cam))
        {
            float px = ProjectedPixels(h, Num.Vector3.Distance(at, camera), cam.Fov, GetViewport()?.GetVisibleRect().Size.Y ?? 1080f);
            if (px < MinBodyPixels) { GoreStreaks().NoteDropped(); return; }
        }
        bool lethal = false;
        var events = _musouState.NewEvents;
        for (int i = 0; i < events.Count; i++) if (events[i].Kind == MusouEventKind.Death && events[i].Enemy == e.Enemy) { lethal = true; break; }
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
            int b = StreakNearestWarfrontBlast(chest);
            if (b >= 0)
            {
                if (Excluded(_wfBlasts[b].Wargear)) return;
                src = Source.Explosion; weight = Weight.Heavy; axis = Vector3.Zero; blast = _wfBlasts[b].At;
            }
            else
            {
                string? wargear = null; DeliveryKind? delivery = null;
                if (e.Player != 0 && _fxLastShot.TryGetValue(e.Player, out var last) && last.Weapon >= 0 && last.Weapon < _config.Weapons.Count)
                { wargear = _config.Weapons[last.Weapon].Name; delivery = _config.Weapons[last.Weapon].Delivery; }
                if (Excluded(wargear, delivery)) return;
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
        float scale = GoreStreakRules.Scale(weight, lethal);
        // a shot's streak starts where it leaves him (the entry stub behind the wound), the others at the struck side's surface
        var origin = chest + axis * (src == Source.Gunfire ? 0.25f * h : 0.15f * h);
        SpawnStreak(origin, axis, normal, h, scale, 0f, 0f, lethal, weight == Weight.Heavy, seed);
        _wfStreaksThisFrame++;
    }

    int StreakNearestWarfrontBlast(Vector3 chest)
    {
        int best = -1; float bestD = WarfrontBlastMetres;
        for (int i = 0; i < _wfBlasts.Length; i++)
        {
            var b = _wfBlasts[i];
            if (b.Wargear is null || _connectedFor - b.T > WarfrontBlastSeconds || _connectedFor < b.T) continue;
            float d = b.At.DistanceTo(chest);
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
