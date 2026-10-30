using System;
using System.Collections.Generic;

namespace SmClient.ImpactRules;

/// <summary>
/// fx/character-visuals (owner, 2026-10-30, item 13; engine-free, tested): the flashes on a character's model that everyone sees. Armour damage while armour remains: a subtle yellow
/// flash; the armour breaks: a subtle red flash; the armour starts to recharge: a yellowish glow with a strong outline (his reference). The snapshot carries no other player's shield
/// (slim records, protocol 8 on), so the flashes of **others** are derived from what the client sees: the hits on them (<see cref="Hit"/>: the shot record's armour bit, the state before
/// the hit) and the class's regeneration delay (<see cref="RegenAt"/>: the glow starts that long after the last damage seen). The own player's are exact (<see cref="OwnShield"/>, the
/// prediction's shield). Damage the client does not see (melee, splash out of view) gives no flash on others. Times and curves are ours (estimates, refine register).
/// </summary>
public sealed class ArmourFlashTracker
{
    /// <summary>How long each flash lasts (s, ours).</summary>
    public const float HitSeconds = 0.22f, BreakSeconds = 0.4f, GlowSeconds = 1.1f;

    sealed class Entry
    {
        public double HitAt = double.NegativeInfinity, BreakAt = double.NegativeInfinity, GlowAt = double.NegativeInfinity;
        public double RegenAt = double.PositiveInfinity;   // others: when the armour is expected to start recharging
        public int Armour;                                 // others: 0 not known, 1 up, 2 down
        public float Shield = float.NaN;                   // own: the last sample
        public bool Regenerating;                          // own: the shield is rising
        public double LastRiseAt = double.NegativeInfinity;
    }

    readonly Dictionary<int, Entry> _byId = new();

    Entry Of(int id) { if (!_byId.TryGetValue(id, out var e)) _byId[id] = e = new Entry(); return e; }

    /// <summary>A hit on a player whose armour was <paramref name="armourUp"/> before it (another player's, from a shot record).</summary>
    public void Hit(int id, double now, bool armourUp, float regenDelay)
    {
        var e = Of(id);
        if (armourUp) e.HitAt = now;
        else if (e.Armour == 1) e.BreakAt = now;   // up before, down now: it broke in between (the flash comes with the first hit that finds it down)
        e.Armour = armourUp ? 1 : 2;
        e.RegenAt = now + Math.Max(0f, regenDelay);
    }

    /// <summary>The player died or respawned: no flash, no pending glow.</summary>
    public void Reset(int id) => _byId.Remove(id);

    /// <summary>Forgets the players <paramref name="present"/> says are gone.</summary>
    public void Prune(Func<int, bool> present)
    {
        List<int>? gone = null;
        foreach (var id in _byId.Keys) if (!present(id)) (gone ??= new()).Add(id);
        if (gone is not null) foreach (var id in gone) _byId.Remove(id);
    }

    /// <summary>
    /// The own player's shield sampled each frame: a drop that leaves armour is a hit, a drop to nothing a break, and a rise after the shield stood still or fell is the
    /// recharge starting (the rise goes on for the glow's length without starting again).
    /// </summary>
    public void OwnShield(int id, double now, float shield, float max, bool alive)
    {
        var e = Of(id);
        if (!alive) { e.Shield = float.NaN; e.Regenerating = false; return; }
        if (float.IsNaN(e.Shield)) { e.Shield = shield; return; }   // the first sample (or the first after a respawn): nothing to compare
        const float Eps = 0.01f;
        if (shield < e.Shield - Eps)
        {
            if (shield <= 0f) e.BreakAt = now; else e.HitAt = now;
            e.Regenerating = false;
        }
        else if (shield > e.Shield + Eps)
        {
            if (!e.Regenerating && now - e.LastRiseAt > 0.35) e.GlowAt = now;
            e.Regenerating = true;
            e.LastRiseAt = now;
        }
        else if (e.Regenerating && (shield >= max || now - e.LastRiseAt > 0.35)) e.Regenerating = false;
        e.Shield = shield;
    }

    /// <summary>The flashes of a player now, each 0..1; also starts the glow of an expected recharge when its time has come.</summary>
    public (float Hit, float Break, float Glow) At(int id, double now)
    {
        if (!_byId.TryGetValue(id, out var e)) return (0f, 0f, 0f);
        if (now >= e.RegenAt) { e.GlowAt = e.RegenAt; e.RegenAt = double.PositiveInfinity; e.Armour = 1; }
        return (Fade(now - e.HitAt, HitSeconds), Fade(now - e.BreakAt, BreakSeconds), Glow(now - e.GlowAt));
    }

    /// <summary>A flash: full at its start, out with the square of the time left.</summary>
    public static float Fade(double since, float seconds) => !(since >= 0.0) || since >= seconds ? 0f : (float)((1.0 - since / seconds) * (1.0 - since / seconds));

    /// <summary>The recharge glow: up over its first tenth, then out smoothly.</summary>
    public static float Glow(double since)
    {
        if (!(since >= 0.0) || since >= GlowSeconds) return 0f;
        float rise = (float)Math.Min(1.0, since / (GlowSeconds * 0.12));
        float fall = (float)(1.0 - since / GlowSeconds);
        return rise * fall;
    }
}
