using System;
using System.Collections.Generic;
using System.Numerics;

namespace SmClient.ImpactRules;

/// <summary>
/// fx/impact-pass: the engine-free rules of the impact pass (no Godot types here: the test project links this folder). The client's Godot code
/// (<c>RenderQuality</c>, <c>LookPresets</c>, <c>FxPlayer</c>, <c>PowerBlade</c>, <c>NetworkView.MeleeFx.cs</c>) only calls these.
/// </summary>
public static class LookChoice
{
    /// <summary>The look used without <c>--look</c> (the owner's choice of 2026-10-29: "make the remaster look the default").</summary>
    public const string Default = "remaster";

    /// <summary><c>--look</c> values that name no preset: <c>current</c> (the default look before the remaster became it), <c>none</c>, and <c>classic</c> (the pre-T7 constants).</summary>
    public static bool IsNone(string name) => name.Equals("current", StringComparison.OrdinalIgnoreCase) || name.Equals("none", StringComparison.OrdinalIgnoreCase)
                                              || name.Equals("classic", StringComparison.OrdinalIgnoreCase);

    /// <summary>The look a <c>--look</c> value names (null: no preset); no value: <see cref="Default"/>.</summary>
    public static string? PresetOf(string? look) => look switch
    {
        null => Default,
        _ when IsNone(look) => null,
        _ => look,
    };
}

/// <summary>fx/impact-pass (ours): the power sword's hit flare on the blade: full at the hit, then out with the square of the time left over <see cref="Seconds"/>.</summary>
public static class SwordFlare
{
    public const float Seconds = 0.45f;

    /// <summary>The flare <paramref name="since"/> seconds after a hit (0 before it and from <see cref="Seconds"/> on, 1 at the hit).</summary>
    public static float At(float since) => !(since >= 0f) || since >= Seconds ? 0f : (1f - since / Seconds) * (1f - since / Seconds);
}

/// <summary>
/// fx/character-visuals (owner, 2026-10-30: the power sword's glow "flares up while swinging", as in his reference): the blade's flare while a melee attack is shown. It rises fast and
/// falls a little slower (<see cref="Rise"/> and <see cref="Fall"/> per second, ours), to <see cref="Level"/> (ours: below a hit's full flare, which stays on top).
/// </summary>
public static class SwordSwing
{
    public const float Level = 0.7f, Rise = 14f, Fall = 7f;

    /// <summary>The swing flare after <paramref name="dt"/> seconds from <paramref name="current"/> while the attack is <paramref name="swinging"/> (or not).</summary>
    public static float Next(float current, bool swinging, float dt)
    {
        float target = swinging ? Level : 0f;
        float step = (target > current ? Rise : Fall) * Math.Max(0f, dt) * Level;
        return target > current ? Math.Min(target, current + step) : Math.Max(target, current - step);
    }
}

/// <summary>fx/impact-pass: a trail laid along the last metres of a recorded path (the jump pack's thrust trails).</summary>
public static class TrailPath
{
    /// <summary>
    /// Fills <paramref name="pts"/> (length n + 1) with points along the last <paramref name="metres"/> of a path of <paramref name="count"/> samples (<paramref name="point"/>(count - 1)
    /// the newest), evenly spaced by distance from the newest; a path shorter than that ends at its oldest sample (the remaining points repeat it). Point 0 is the newest sample.
    /// </summary>
    public static void ByDistance(int count, Func<int, Vector3> point, float metres, Span<Vector3> pts)
    {
        if (pts.Length == 0 || count <= 0) return;
        int k = count - 1;
        var at = point(k);
        float walked = 0f;
        int n = pts.Length - 1;
        pts[0] = at;
        for (int j = 1; j <= n; j++)
        {
            float want = metres * j / n;
            while (k > 0)
            {
                var next = point(k - 1);
                float step = Vector3.Distance(at, next);
                if (walked + step >= want && step > 1e-6f) { at = Vector3.Lerp(at, next, (want - walked) / step); walked = want; break; }
                walked += step; at = next; k--;
            }
            pts[j] = at;
        }
    }
}

/// <summary>
/// fx/impact-pass (review): when a melee attack's hit time has passed for a player, once per attack. An attack is keyed by its step, kind and start tick
/// (the tick its time was 0: own player from the prediction's tick, others from the snapshot tick), so a reconcile rewind of any size, which takes the
/// attack's time back and forward again, never fires twice; a player first seen mid-attack does not fire for a hit time already passed.
/// </summary>
public sealed class MeleeHitGate
{
    /// <summary>Start ticks within this many ticks of the last fired attack's (same step and kind) are the same attack (others' start estimates jitter with the snapshots).</summary>
    public const int StartTolerance = 3;

    struct Seen
    {
        public int Step; public bool Heavy; public float Time; public bool InAttack;
        public bool HasFired; public int FiredStep; public bool FiredHeavy; public long FiredStart;
    }

    readonly Dictionary<int, Seen> _seen = new();
    readonly List<int> _drop = new();

    /// <summary>The player is in no attack this frame (an entry is kept, so an attack seen from its start counts; the last fired attack is remembered).</summary>
    public void Idle(int id)
    {
        if (_seen.TryGetValue(id, out var s)) { s.InAttack = false; _seen[id] = s; }
        else _seen[id] = new Seen { InAttack = false };
    }

    /// <summary>
    /// The player is in attack <paramref name="step"/> (<paramref name="heavy"/>) at <paramref name="time"/> s, started at <paramref name="startTick"/>; true once when its
    /// <paramref name="hitTime"/> passes. The first sight of a player (no entry) only seeds its time.
    /// </summary>
    public bool Passed(int id, int step, bool heavy, float time, float hitTime, long startTick)
    {
        if (!_seen.TryGetValue(id, out var s))
        {
            _seen[id] = new Seen { Step = step, Heavy = heavy, Time = time, InAttack = true };
            return false;
        }
        bool same = s.InAttack && s.Step == step && s.Heavy == heavy && time >= s.Time;
        float before = same ? s.Time : -1f;
        s.Step = step; s.Heavy = heavy; s.Time = time; s.InAttack = true;
        bool crossed = before < hitTime && time >= hitTime;
        if (crossed && s.HasFired && s.FiredStep == step && s.FiredHeavy == heavy && Math.Abs(s.FiredStart - startTick) <= StartTolerance) crossed = false;
        if (crossed) { s.HasFired = true; s.FiredStep = step; s.FiredHeavy = heavy; s.FiredStart = startTick; }
        _seen[id] = s;
        return crossed;
    }

    /// <summary>
    /// The time to give <see cref="Passed"/> for a strike whose effects follow the drawn pose: the drawn time <paramref name="shown"/> (never ahead of the state's
    /// <paramref name="sim"/> time, which guards a stale time from the strike before), but once the state itself has reached <paramref name="hitTime"/> the effects are
    /// due whatever the drawn clock does (a stalled or culled remote, a strike that ends first): at least <paramref name="fxTime"/>. The gate still fires once.
    /// </summary>
    public static float EffectTime(float shown, float sim, float fxTime, float hitTime)
    {
        float t = MathF.Min(shown, sim);
        return sim >= hitTime ? MathF.Max(t, fxTime) : t;
    }

    /// <summary>The start tick of an attack <paramref name="time"/> s old at <paramref name="tick"/> (ticks of <paramref name="tickSeconds"/>).</summary>
    public static long StartTick(long tick, float time, float tickSeconds) => tick - (long)MathF.Round(time / tickSeconds);

    /// <summary>Forgets the players <paramref name="present"/> says are gone.</summary>
    public void Prune(Func<int, bool> present)
    {
        _drop.Clear();
        foreach (var id in _seen.Keys) if (!present(id)) _drop.Add(id);
        foreach (var id in _drop) _seen.Remove(id);
    }

    public int Count => _seen.Count;
}

/// <summary>
/// fx/melee-presentation (interpretation): other players' melee hits are not reported to a client (only the own player's are), so a hit's sound is inferred from
/// what the snapshots show: a candidate (an attacker whose hit time has just passed with an enemy in reach, or whose charge has just connected) is confirmed when its victim
/// <b>reacts</b> in the same window: the victim's health plus shield falls, or it dies, within <see cref="Before"/> s before to <see cref="After"/> s after the candidate.
/// An evaded, blocked or missed swing gets no sound. Candidates come from <see cref="MeleeHitGate"/> (once per attack, never on a reconcile rewind), so a confirmed
/// candidate is posted once. Engine-free and tested.
/// </summary>
public sealed class MeleeHitConfirm
{
    /// <summary>A drop in the victim's health or shield seen this long before the candidate still confirms it (the snapshots interpolate the two players separately).</summary>
    public const double Before = 0.25;
    /// <summary>A candidate waits this long for its victim's reaction, then is dropped.</summary>
    public const double After = 0.35;
    /// <summary>The smallest fall of health plus shield that counts as a reaction.</summary>
    public const float MinDrop = 0.5f;

    public readonly record struct Hit(int Attacker, int Victim, bool Charge, bool Dash, double At);

    struct Seen { public float Total; public bool Alive; public double DropAt; }

    readonly Dictionary<int, Seen> _seen = new();
    readonly List<Hit> _pending = new();
    readonly List<int> _drop = new();

    /// <summary>Records a player's state this frame (<paramref name="total"/>: health plus shield); a fall or a death marks a reaction at <paramref name="now"/>.</summary>
    public void Observe(int id, bool alive, float total, double now)
    {
        if (!_seen.TryGetValue(id, out var s)) { _seen[id] = new Seen { Total = total, Alive = alive, DropAt = double.NegativeInfinity }; return; }
        if (s.Alive && (total < s.Total - MinDrop || !alive)) s.DropAt = now;
        s.Total = total; s.Alive = alive;
        _seen[id] = s;
    }

    /// <summary>A candidate hit at <paramref name="now"/> (<paramref name="charge"/>: a charge, <paramref name="dash"/>: the jump-pack dash attack's).</summary>
    public void Add(int attacker, int victim, bool charge, bool dash, double now) => _pending.Add(new Hit(attacker, victim, charge, dash, now));

    /// <summary>Moves the candidates whose victim has reacted into <paramref name="confirmed"/> and forgets those past <see cref="After"/>.</summary>
    public void Drain(double now, List<Hit> confirmed)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var h = _pending[i];
            if (_seen.TryGetValue(h.Victim, out var v) && v.DropAt >= h.At - Before && v.DropAt <= h.At + After) { confirmed.Add(h); _pending.RemoveAt(i); }
            else if (now > h.At + After) _pending.RemoveAt(i);
        }
    }

    public int Pending => _pending.Count;

    /// <summary>Forgets the players <paramref name="present"/> says are gone.</summary>
    public void Prune(Func<int, bool> present)
    {
        _drop.Clear();
        foreach (var id in _seen.Keys) if (!present(id)) _drop.Add(id);
        foreach (var id in _drop) _seen.Remove(id);
        _pending.RemoveAll(h => !present(h.Attacker) && !present(h.Victim));
    }
}

/// <summary>fx/melee-presentation: whether a player's charge has just connected (engine-free so it is tested).</summary>
public static class ChargeConnect
{
    /// <summary>
    /// True when the charge phase went from charging (<paramref name="before"/>: the bull rush or the dash attack's lunge) to the stop after connecting (the collision clip or the
    /// dash's hit state): the same phase change the simulation makes when a charge meets a player or a wall (a wall gets no confirmation from <see cref="MeleeHitConfirm"/>).
    /// </summary>
    public static bool Connected(SmSim.RushPhase before, SmSim.RushPhase now) =>
        (before == SmSim.RushPhase.Rush && now == SmSim.RushPhase.Collision) || (before == SmSim.RushPhase.DashAttack && now == SmSim.RushPhase.DashHit);
}

/// <summary>fx/melee-presentation: the melee weapons' idle hum and glow belong to the marines' wargear; an Ork (our added race) gets neither, whatever its weapon's files say.</summary>
public static class MeleeIdleGuard
{
    public static bool Allows(bool isOrk) => !isOrk;
}
