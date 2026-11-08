using System;
using System.Collections.Generic;

namespace SmNet.Extras.Musou;

/// <summary>The look of one melee slash stroke: how long it lasts, its length range and thickness (metres), and a brightness factor on its colour.</summary>
public readonly record struct MusouSlashStyle(float Seconds, float LengthMin, float LengthMax, float Width, float Brightness);

/// <summary>
/// Musou combat feel (owner's brief of 2026-11-09, plan approved the same day; Musou Mode only, client presentation, no simulation, wire or damage change). The server's hit
/// report reaches the client about 186 ms after the predicted contact on 127.0.0.1 (measured, phase 1), so the own weapon's contact feedback (the slash, the power flash, the
/// small shake for a melee strike; the tracer's end, the flesh impact and the hit sound for a shot) is shown at the predicted contact, and the server's report of the same
/// enemy then adds only what the prediction cannot know (the blood of a melee kill, deaths, the hit and kill markers, victims the client did not predict). Nothing about a kill
/// is predicted. The first weapons, chosen by the owner on 2026-11-09: the power sword's strikes and the bolt pistol; the rest keep the confirmed path until the owner has seen
/// the pair. Every value is ours (kind: ours, confidence: low, refine by playtest).
/// </summary>
public static class MusouContactRules
{
    /// <summary>Seconds after a predicted contact within which the server's report of the same enemy by the own player is that contact (past the measured 0.21 s worst plus a margin).</summary>
    public const double MatchSeconds = 0.6;

    /// <summary>The most enemies one predicted strike shows contact on (the slash budget's frame cap).</summary>
    public const int MaxTargets = MusouSlashRules.PerFrame;

    /// <summary>Whether the own strikes of this melee wargear show contact at the predicted hit (the pilot: the power sword, loyalist and Chaos).</summary>
    public static bool PredictsMelee(string? wargear) => wargear is { Length: > 0 } w && w.Contains("powersword", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the own shots of this weapon show their contact on an Ork at the predicted shot (the pilot: the bolt pistols, not the bolters or the pistol-slot bolters).</summary>
    public static bool PredictsGun(string? weapon) => weapon is { Length: > 0 } w && w.Contains("bolt_pistol", StringComparison.OrdinalIgnoreCase);

    /// <summary>The default slash (the 2026-11-08 stroke) and the power sword's: a faster, thinner, brighter stroke for a fast energised slice.</summary>
    public static readonly MusouSlashStyle DefaultSlash = new(MusouSlashRules.Seconds, MusouSlashRules.LengthMin, MusouSlashRules.LengthMax, MusouSlashRules.Width, 1f);
    public static readonly MusouSlashStyle PowerSwordSlash = new(0.18f, 1.9f, 2.5f, 0.22f, 1.35f);

    /// <summary>The slash style of a melee wargear (only the pilot's power sword has its own yet).</summary>
    public static MusouSlashStyle SlashOf(string? wargear) => PredictsMelee(wargear) ? PowerSwordSlash : DefaultSlash;

    /// <summary>Own gun hit sounds: one per shot (the hits of one tick share it), at most this many a second (a token bucket of <see cref="GunSoundBurst"/>).</summary>
    public const float GunSoundsPerSecond = 10f, GunSoundBurst = 4f;

    /// <summary>Musou's camera shake: the strongest source counts in full, a weaker one adds this share of itself, and the total never passes <see cref="ShakeCap"/> (radians).</summary>
    public const float ShakeOthersShare = 0.25f, ShakeCap = 0.04f;

    /// <summary>The shake total after adding <paramref name="amount"/> to <paramref name="current"/>: the larger counts in full, the smaller at its share, capped. Never below the current total.</summary>
    public static float CombineShake(float current, float amount)
    {
        if (!(amount > 0f)) return current;
        float big = Math.Max(current, amount), small = Math.Min(current, amount);
        return Math.Max(current, Math.Min(ShakeCap, big + small * ShakeOthersShare));
    }

    /// <summary>
    /// Where a ray from <paramref name="origin"/> along the unit <paramref name="direction"/> first meets an enemy's shot body standing at <paramref name="feet"/>: the server's shape
    /// (<c>MusouSim.Raycast</c>: a head sphere at the top, a closed vertical cylinder below it; without its path of the last ticks). Null when it misses within <paramref name="maxDistance"/>.
    /// </summary>
    public static float? RayBody(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, System.Numerics.Vector3 feet, float radius, float height, float maxDistance, out bool head)
    {
        head = false;
        float best = maxDistance;
        bool found = false;
        float headRadius = 0.22f * height / 1.9f;
        var centre = feet + new System.Numerics.Vector3(0f, 0f, height - headRadius);
        var m = origin - centre;
        float b = System.Numerics.Vector3.Dot(m, direction), c = System.Numerics.Vector3.Dot(m, m) - headRadius * headRadius;
        if (!(c > 0f && b > 0f) && b * b - c >= 0f)
        {
            float t = MathF.Max(0f, -b - MathF.Sqrt(b * b - c));
            if (t < best) { best = t; head = true; found = true; }
        }
        float a = direction.X * direction.X + direction.Y * direction.Y;
        float from = 0f, to = best;
        float mx = origin.X - feet.X, my = origin.Y - feet.Y;
        if (a > 1e-9f)
        {
            float bb = mx * direction.X + my * direction.Y, cc = mx * mx + my * my - radius * radius, disc = bb * bb - a * cc;
            if (disc < 0f) return found ? best : null;
            float root = MathF.Sqrt(disc);
            from = MathF.Max(from, (-bb - root) / a);
            to = MathF.Min(to, (-bb + root) / a);
        }
        else if (mx * mx + my * my > radius * radius) return found ? best : null;
        float low = feet.Z, high = feet.Z + height - headRadius;
        if (MathF.Abs(direction.Z) > 1e-6f)
        {
            float t1 = (low - origin.Z) / direction.Z, t2 = (high - origin.Z) / direction.Z;
            from = MathF.Max(from, MathF.Min(t1, t2));
            to = MathF.Min(to, MathF.Max(t1, t2));
        }
        else if (origin.Z < low || origin.Z > high) return found ? best : null;
        if (from <= to && from < best) { best = from; head = false; found = true; }
        return found ? best : null;
    }

    /// <summary>A surviving enemy's push away from the blow (metres) and its length (seconds): a shot, a melee hit. Drawn only; the clip, the facing and the movement are kept.</summary>
    public const float RecoilShot = 0.12f, RecoilMelee = 0.2f, RecoilSeconds = 0.15f;

    /// <summary>
    /// Whether a struck enemy of this type is drawn pushed back: the unarmoured rank and file (Boyz, Shoota Boyz, Gretchin, Rokkit Boyz, Stormboyz, Meks, Squigs). The armoured and the big
    /// ones keep their established resistance (no push): 'Ard Boyz, Nobz, the Chaos Marine, the Warboss, the Boss Orks, the Big Shoota Nob and the shielded Weirdboy.
    /// </summary>
    public static bool Recoils(SmSim.Extras.Musou.MusouEnemyType type) => !SmSim.Extras.Musou.MusouBossOrks.Is(type) && type is SmSim.Extras.Musou.MusouEnemyType.Boy
        or SmSim.Extras.Musou.MusouEnemyType.ShootaBoy or SmSim.Extras.Musou.MusouEnemyType.Gretchin or SmSim.Extras.Musou.MusouEnemyType.RokkitBoy
        or SmSim.Extras.Musou.MusouEnemyType.Stormboy or SmSim.Extras.Musou.MusouEnemyType.ScrapMek or SmSim.Extras.Musou.MusouEnemyType.BombSquig;

    /// <summary>The push's offset at <paramref name="age"/> seconds of <paramref name="metres"/>: out fast, back by <see cref="RecoilSeconds"/>.</summary>
    public static float RecoilAt(float age, float metres)
    {
        if (!(age >= 0f) || age >= RecoilSeconds) return 0f;
        float t = age / RecoilSeconds;
        return metres * MathF.Sin(MathF.PI * MathF.Sqrt(t));   // peaks a quarter of the way in, eases back
    }
}

/// <summary>
/// The own predicted contacts waiting for the server's report (engine-free): a contact on an enemy is noted with its time; the server's report of the same enemy within
/// <see cref="MusouContactRules.MatchSeconds"/> matches it (its contact feedback was already shown) and removes it; one never matched in time counts as a contact the server did not confirm.
/// </summary>
public sealed class MusouContactLedger
{
    readonly List<(ushort Enemy, double At, bool Melee)> _waiting = new();

    /// <summary>Contacts predicted, matched by a report, and expired unmatched (the server missed or the report was lost).</summary>
    public int Predicted { get; private set; }
    public int Matched { get; private set; }
    public int Unconfirmed { get; private set; }

    /// <summary>A predicted contact on <paramref name="enemy"/> at <paramref name="now"/> (seconds).</summary>
    public void Predict(ushort enemy, double now, bool melee)
    {
        Expire(now);
        _waiting.Add((enemy, now, melee));
        Predicted++;
    }

    /// <summary>How many predicted contacts wait unmatched.</summary>
    public int Pending => _waiting.Count;

    /// <summary>
    /// The server's report of the own hit on <paramref name="enemy"/> at <paramref name="now"/>, a melee hit or a shot (<paramref name="melee"/>): true when it matches a predicted contact of
    /// the same kind (the oldest waiting one on that enemy, which is then taken; two shots at one Ork are two contacts and match two reports; a shot never takes a melee contact's place).
    /// </summary>
    public bool Confirm(ushort enemy, double now, bool melee)
    {
        Expire(now);
        for (int i = 0; i < _waiting.Count; i++)
        {
            if (_waiting[i].Enemy != enemy || _waiting[i].Melee != melee) continue;
            _waiting.RemoveAt(i);
            Matched++;
            return true;
        }
        return false;
    }

    /// <summary>Forgets contacts older than the match window, counting them as unconfirmed.</summary>
    public void Expire(double now)
    {
        for (int i = _waiting.Count - 1; i >= 0; i--)
            if (now - _waiting[i].At > MusouContactRules.MatchSeconds) { _waiting.RemoveAt(i); Unconfirmed++; }
    }
}
