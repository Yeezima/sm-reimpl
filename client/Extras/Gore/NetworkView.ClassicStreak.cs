using System;
using Godot;
using SmClient.ImpactRules;
using SmSim;
using Num = System.Numerics;
using static SmClient.ImpactRules.GoreStreakRules;

namespace SmClient;

/// <summary>
/// gore-streak in Classic PvP with the Extended gore (owner 2026-11-25: every health hit, explosions included; not plasma, melta or flamers; scaled by weapon, bigger on a kill; enemies
/// only). The confirmed contacts of the directional hit look (NetworkView.ClassicHitDir.cs) become streaks:
/// <list type="bullet">
/// <item>gunfire: along the shot from where it leaves the victim (the entry stub behind it), the plane rolled by the seed; the tracer's wait is the streak's negative pre-age;</item>
/// <item>weapon melee: along the weapon's direction at the contact, on the swing plane from the recorded path's tangents 0.08 s either side (a thrust: rolled), bent by the arc;</item>
/// <item>a bull-rush charge: along the charge, heavy;</item>
/// <item>a health mark nothing else explains (<see cref="ExtrasClassicUnpaired"/>): a detonation drawn in the last 0.4 s plus the draw lag within <see cref="BlastPairMetres"/>
/// (<see cref="ExtrasClassicBlast"/>; not plasma), else an enemy's stomp or jump-pack landing within <see cref="StompPairMetres"/>: radial from the blast, lifted 15 degrees, heavy.</item>
/// </list>
/// Faithful never comes here (the hooks are empty without Extras/Gore and <see cref="NetworkView.StreakDraws"/> is false); <c>--old-gore-streak</c> keeps repair 4's ribbons.
/// </summary>
public partial class NetworkView
{
    /// <summary>A blast pairs a health mark within this distance of the victim's chest (m; ours) and this long after it plus the draw lag (s; ours).</summary>
    const float BlastPairMetres = 6f;
    const double BlastPairSeconds = 0.4;
    /// <summary>A stomping or landing enemy pairs a health mark within this level distance (m; ours).</summary>
    const float StompPairMetres = 4.5f;

    readonly (double T, Vector3 At, string Wargear)[] _streakBlasts = new (double, Vector3, string)[16];
    int _streakBlastNext;
    int _streakExplosions, _streakStomps, _streakExcluded;

    bool ClassicStreak(ClassicContact c)
    {
        if (Excluded(c.Wargear)) { _streakExcluded++; return false; }
        if (_client is not null && c.Victim == _client.PlayerId && c.Victim > 0) return false;   // enemies only: never on the own body (lead decision 4)
        var src = c.Gunfire ? Source.Gunfire : c.Charge ? Source.Charge : c.Exit ? Source.Thrust : Source.Sweep;
        var weight = WeightOf(src, c.Wargear, c.Heavy);
        float scale = GoreStreakRules.Scale(weight, c.Lethal);
        var axis = c.Dir.LengthSquared() > 1e-8f ? c.Dir.Normalized() : Vector3.Forward;
        int seed = NextStreakSeed();
        Vector3? before = null, after = null;
        float bend = 0f;
        if (src == Source.Sweep && c.History is { } h && c.HistoryCount >= 3)
        {
            var p0 = PathAt(h, c.HistoryCount, -0.08); var p1 = PathAt(h, c.HistoryCount, 0.0); var p2 = PathAt(h, c.HistoryCount, 0.08);
            var b = p1 - p0; var a = p2 - p1;
            if (b.LengthSquared() > 1e-6f && a.LengthSquared() > 1e-6f)
            {
                before = b; after = a;
                // the arc's curvature in the plane: how far the direction turns across the exit axis
                var n = StreakPlane(Source.Sweep, axis, seed, b, a);
                var across = n.Cross(axis).Normalized();
                bend = 0.6f * (a.Normalized() - b.Normalized()).Dot(across);
            }
        }
        var normal = StreakPlane(src, axis, seed, before, after);
        SpawnStreak(c.Contact, axis, normal, ClassicBodyHeight, scale, (float)c.PreAge, bend, c.Lethal, weight == Weight.Heavy, seed, src, victim: c.Victim);
        if (LogHitDir) HitDirPrint($"HITDIR streak P{c.Attacker}->P{c.Victim} {src} {weight}{(c.Lethal ? " lethal" : "")} scale {scale:F2} wargear {c.Wargear ?? "?"} pre-age {c.PreAge * 1000:F0} ms bend {bend:F2} (t {_connectedFor:F2})");
        return true;
    }

    /// <summary>The weapon's drawn point at <paramref name="t"/> s from the contact (linear between recorded points; the nearest end outside them).</summary>
    static Vector3 PathAt((double T, Vector3 P)[] h, int n, double t)
    {
        if (t <= h[0].T) return h[0].P;
        for (int i = 1; i < n; i++)
            if (h[i].T >= t) { double u = (t - h[i - 1].T) / Math.Max(1e-6, h[i].T - h[i - 1].T); return h[i - 1].P.Lerp(h[i].P, (float)u); }
        return h[n - 1].P;
    }

    partial void ExtrasClassicBlast(Vector3 at, string wargear)
    {
        if (!StreakDraws) return;
        if (MatchExtras.HasFlag(ContentExtras.Warfront)) { StreakWarfrontBlast(at, wargear); return; }   // the same hook feeds Warfront's explosion check (NetworkView.WarfrontStreak.cs)
        if (!PlayerHitFxRules.Classic(MatchExtras)) return;
        _streakBlasts[_streakBlastNext] = (_connectedFor, at, wargear);
        _streakBlastNext = (_streakBlastNext + 1) % _streakBlasts.Length;
    }

    partial void ExtrasClassicUnpaired(PlayerState o, bool shield, bool health, bool lethal, ref bool drawn)
    {
        if (!health || !StreakDraws || !HitDirLook.HasFlag(ClassicHitDirRules.DirLook.BloodSpray) || _client is null || _config is null) return;
        var chest = ToGodot(o.Position + new Num.Vector3(0f, 0f, 1.25f));
        double window = BlastPairSeconds + DrawLag();
        int best = -1; float bestD = BlastPairMetres;
        for (int i = 0; i < _streakBlasts.Length; i++)
        {
            var b = _streakBlasts[i];
            if (b.Wargear is null || _connectedFor - b.T > window || _connectedFor < b.T) continue;
            float d = b.At.DistanceTo(chest);
            if (d <= bestD) { bestD = d; best = i; }
        }
        Vector3 centre;
        string what;
        if (best >= 0)
        {
            if (Excluded(_streakBlasts[best].Wargear)) { _streakExcluded++; return; }   // a plasma blast: no streak
            centre = _streakBlasts[best].At; what = $"blast {_streakBlasts[best].Wargear} {bestD:F1} m";
            _streakExplosions++;
        }
        else if (StompingEnemy(o) is { } feet) { centre = feet; what = "stomp or landing"; _streakStomps++; }
        else return;
        int seed = NextStreakSeed();
        var axis = GsG(ExplosionAxis(GsN(chest), GsN(centre), Num.Vector3.UnitY, seed));
        var normal = StreakPlane(Source.Explosion, axis, seed);
        float scale = GoreStreakRules.Scale(Weight.Heavy, lethal);
        SpawnStreak(chest + axis * ClassicHitDirRules.BodySurface, axis, normal, ClassicBodyHeight, scale, 0f, 0f, lethal, true, seed, Source.Explosion, victim: o.PlayerId);
        drawn = true;
        if (LogHitDir) HitDirPrint($"HITDIR streak explosion on P{o.PlayerId} from {what}{(lethal ? " lethal" : "")} scale {scale:F2} (t {_connectedFor:F2})");
    }

    /// <summary>The feet (Godot world) of the nearest enemy of <paramref name="o"/> within <see cref="StompPairMetres"/> who is stomping, air-slashing or landing a jump; null when none.</summary>
    Vector3? StompingEnemy(PlayerState o)
    {
        Vector3? best = null; float bestD = StompPairMetres;
        void Look(PlayerState e)
        {
            if (!e.Alive || e.PlayerId == o.PlayerId || e.Team == o.Team) return;
            bool stomp = e.MeleeStep > 0 && Combat.MeleeStepOf(_config!, e) is { Stomp: not null };
            bool landing = e.JumpSlash || (e.Jump != JumpPhase.None && e.Grounded);
            if (!stomp && !landing) return;
            var d = e.Position - o.Position; d.Z = 0f;
            float l = d.Length();
            if (l <= bestD) { bestD = l; best = ToGodot(e.Position); }
        }
        if (_client is { } c && c.Predicted.PlayerId == c.PlayerId) Look(c.Predicted);
        foreach (var p in _lastOthers) Look(p);
        return best;
    }
}
