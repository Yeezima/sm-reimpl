namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The far layer's fight (expansion S6, docs/musou-expansion.md "S6 design"; every value ours, an estimate, tuned on <c>TfwClashTests</c>' Monte Carlo): Lanchester's square law with cohesion. A change here raises
/// <see cref="TfwRules.Version"/>. Times in seconds; counts in men.
/// </summary>
public static class TfwClashRules
{
    /// <summary>The far layer is resolved this often (2 Hz, the brief's).</summary>
    public const float StepSeconds = 0.5f;
    /// <summary>Enemies one man of the aggregate puts out of the fight a second at full cohesion: 200 v 200 lasts about a minute.</summary>
    public const float FirePerSecond = 0.011f;
    /// <summary>A side's fire is this share of its full fire at no cohesion, rising linearly to all of it at full cohesion.</summary>
    public const float CohesionFloor = 0.3f;
    /// <summary>Cohesion lost per share of the army's start lost (a tenth of the start lost costs 0.15).</summary>
    public const float LossShock = 1.5f;
    /// <summary>Cohesion lost a second per unit of the enemy's numerical edge (enemy / self − 1, when above 0).</summary>
    public const float EdgeShock = 0.05f;
    /// <summary>Cohesion regained a second out of a fight, up to full.</summary>
    public const float Recovery = 0.02f;
    /// <summary>An army routs when its cohesion falls to this, or when it has lost this share of its start.</summary>
    public const float RoutCohesion = 0.2f, RoutLoss = 0.65f;
    /// <summary>A step's losses vary by up to this share either way (a seeded roll per side and step).</summary>
    public const float Noise = 0.3f;
    /// <summary>Elites die at this share of their part of the losses (they are tougher).</summary>
    public const float EliteLossShare = 0.5f;
}

/// <summary>One army in a far clash, as <see cref="TfwClash.Step"/> reads and writes it: its count now, its start (losses are of it), its cohesion (0 to 1) and, after a step, what it lost and whether it routed.</summary>
public struct TfwClashUnit
{
    public float Count, Start, Cohesion, Lost;
    /// <summary>Real units counted in a far clash: they fire at full cohesion and never rout (they fight to the last; the battle takes their losses).</summary>
    public bool Steady;
    public bool Routed;

    public TfwClashUnit(float count, float start, float cohesion = 1f, bool steady = false)
    {
        Count = count; Start = Math.Max(count, start); Cohesion = cohesion; Steady = steady; Lost = 0f; Routed = false;
    }

    /// <summary>The share of its start lost.</summary>
    public readonly float LossShare => Start > 0f ? (Start - Count) / Start : 0f;
}

/// <summary>What <see cref="TfwClash.Resolve"/> found: which side won, the shares each kept, whether the loser routed (not wiped out) and how long it took.</summary>
public readonly record struct TfwClashResult(bool AWon, float AKept, float BKept, bool LoserRouted, float Seconds);

/// <summary>
/// The far layer's attrition (pure, deterministic for given rolls): each side's losses in a step are the other side's men times <see cref="TfwClashRules.FirePerSecond"/> times its cohesion's
/// share of fire, times a roll; they fall on the side's armies by their counts. Cohesion falls with losses and with the enemy's numerical edge; an army routs at <see cref="TfwClashRules.RoutCohesion"/>
/// or <see cref="TfwClashRules.RoutLoss"/>. A side is symmetric: one man of an Ork aggregate is worth one Guardsman, so even numbers make an even fight (the brief's 200 v 200).
/// </summary>
public static class TfwClash
{
    /// <summary>The fire one army puts out a second.</summary>
    public static float Fire(in TfwClashUnit u) => u.Routed || u.Count <= 0f ? 0f : u.Count * TfwClashRules.FirePerSecond * (u.Steady ? 1f : TfwClashRules.CohesionFloor + (1f - TfwClashRules.CohesionFloor) * Math.Clamp(u.Cohesion, 0f, 1f));

    static float Standing(ReadOnlySpan<TfwClashUnit> side)
    {
        float n = 0f;
        foreach (var u in side) if (!u.Routed) n += Math.Max(0f, u.Count);
        return n;
    }

    /// <summary>
    /// One step of <paramref name="dt"/> seconds between sides <paramref name="a"/> and <paramref name="b"/>, both at once; <paramref name="rollA"/> and <paramref name="rollB"/> (−1 to 1) vary the losses
    /// each side deals. Every army's <see cref="TfwClashUnit.Lost"/> is this step's; a routed army takes no part (it neither fires nor is fired on).
    /// </summary>
    public static void Step(Span<TfwClashUnit> a, Span<TfwClashUnit> b, float dt, float rollA, float rollB)
    {
        float fireA = 0f, fireB = 0f;
        foreach (var u in a) fireA += Fire(u);
        foreach (var u in b) fireB += Fire(u);
        float menA = Standing(a), menB = Standing(b);
        for (int k = 0; k < a.Length; k++) a[k].Lost = 0f;
        for (int k = 0; k < b.Length; k++) b[k].Lost = 0f;
        if (menA <= 0f || menB <= 0f) return;
        float onB = fireA * dt * (1f + TfwClashRules.Noise * Math.Clamp(rollA, -1f, 1f));
        float onA = fireB * dt * (1f + TfwClashRules.Noise * Math.Clamp(rollB, -1f, 1f));
        Take(a, onA, menA, menB, dt);
        Take(b, onB, menB, menA, dt);
    }

    static void Take(Span<TfwClashUnit> side, float losses, float men, float enemy, float dt)
    {
        float edge = Math.Max(0f, enemy / Math.Max(1e-3f, men) - 1f);
        for (int k = 0; k < side.Length; k++)
        {
            ref var u = ref side[k];
            if (u.Routed || u.Count <= 0f) continue;
            float lost = Math.Min(u.Count, losses * u.Count / men);
            u.Lost = lost;
            u.Count -= lost;
            if (u.Steady) continue;
            u.Cohesion = Math.Max(0f, u.Cohesion - TfwClashRules.LossShock * lost / Math.Max(1f, u.Start) - TfwClashRules.EdgeShock * edge * dt);
            if (u.Count > 0f && (u.Cohesion <= TfwClashRules.RoutCohesion || u.LossShare >= TfwClashRules.RoutLoss)) u.Routed = true;
        }
    }

    /// <summary>Cohesion regained by an army out of a fight for <paramref name="dt"/> seconds.</summary>
    public static float Recover(float cohesion, float dt) => Math.Min(1f, cohesion + TfwClashRules.Recovery * dt);

    /// <summary>A seeded roll from −1 to 1 (the far layer's dice: no shared random state).</summary>
    public static float Roll(uint seed, uint step, uint side) => MusouSim.Unit(MusouSim.Mix(MusouSim.Mix(seed, step * 2u + side), 0x636C6173u)) * 2f - 1f;

    /// <summary>A whole clash of one army a side, <paramref name="a"/> men against <paramref name="b"/>, to a rout or a wipe (at most <paramref name="maxSeconds"/>): the Monte Carlo test's and the profile's.</summary>
    public static TfwClashResult Resolve(float a, float b, uint seed, float maxSeconds = 1200f)
    {
        Span<TfwClashUnit> sa = [new TfwClashUnit(a, a)];
        Span<TfwClashUnit> sb = [new TfwClashUnit(b, b)];
        float t = 0f;
        uint step = 0;
        while (t < maxSeconds && !sa[0].Routed && !sb[0].Routed && sa[0].Count > 0f && sb[0].Count > 0f)
        {
            Step(sa, sb, TfwClashRules.StepSeconds, Roll(seed, step, 0u), Roll(seed, step, 1u));
            t += TfwClashRules.StepSeconds;
            step++;
        }
        bool aDown = sa[0].Routed || sa[0].Count <= 0f, bDown = sb[0].Routed || sb[0].Count <= 0f;
        bool aWon = bDown && !aDown || (aDown == bDown && sa[0].Count >= sb[0].Count);
        var loser = aWon ? sb[0] : sa[0];
        return new TfwClashResult(aWon, sa[0].Count / Math.Max(1f, a), sb[0].Count / Math.Max(1f, b), loser.Routed && loser.Count > 0f, t);
    }
}
