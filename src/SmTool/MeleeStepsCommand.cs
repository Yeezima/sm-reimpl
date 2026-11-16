using System.Globalization;
using SmContent;
using System.Numerics;
using SmSim;

namespace SmTool;

/// <summary>
/// <c>smtool melee-steps &lt;game-dir|auto&gt; [thunder_hammer|power_sword|chainsword|power_axe|knife]</c> (anim/hammer-swing test aid): each strike of a melee weapon's combo with its timing and the forward
/// root motion of its distance variants (metres at the move gate, at the hit and at the end), and the step the simulation takes with no target and with a
/// target at each variant's distance (the clip's motion times <see cref="SimConfig.MeleeLungeScale"/>, as <see cref="MeleeLock.LungeStep"/> sums it). Read-only.
/// </summary>
static class MeleeStepsCommand
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        string which = rest.FirstOrDefault(a => !a.StartsWith("--")) ?? "thunder_hammer";
        var map = MpClasses.MeleeMapFor(which switch { "thunder_hammer" => "sm_mp_melee_2hand_thunder_hammer", "power_sword" => "sm_mp_melee_powersword", "chainsword" => "sm_mp_melee_chainsword", "power_axe" => "sm_mp_melee_power_axe", "knife" => "sm_mp_melee_combat_knife", _ => which });
        if (map is null) { Console.Error.WriteLine("unknown weapon " + which); return 1; }
        var tuning = MeleeTuning.Load(gameDir, map);
        var config = GameplayTuning.Load(gameDir).Config;
        Console.WriteLine($"{map.Wargear}  graph {map.Graph}  lunge scale {config.LungeScale}  lock burst {config.MeleeLock.BurstSpeed} m/s x {config.MeleeLock.BurstFraction} of the hit time, separation {config.MeleeLock.Separation} m");
        Dump("light", tuning.Steps, tuning.Animations, config);
        Dump("heavy", tuning.Heavy, tuning.HeavyAnimations, config);
        return 0;
    }

    static void Dump(string kind, IReadOnlyList<MeleeStep> steps, IReadOnlyList<string> animations, SimConfig config)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var m = steps[i];
            Console.WriteLine($"{kind} {i + 1}: {(i < animations.Count ? animations[i] : "?")}  duration {m.Duration:0.###} s, hit {m.HitTime:0.###}, moveAt {m.MoveAt:0.###}, combo {m.ComboOpen:0.###}..{m.ComboClose:0.###}, inPlace {m.InPlace}");
            if (m.Reach is not { } r) { Console.WriteLine("    no reach data"); continue; }
            Console.WriteLine($"    align {r.AlignFrom:0.###}..{r.AlignTo:0.###}, distance window {r.DistanceFrom:0.###}..{r.DistanceTo:0.###} = {r.Distance:0.###}, default blend {r.DefaultDistance:0.###}");
            foreach (var v in r.Variants)
                Console.WriteLine($"    variant {v.Distance,5:0.##} m {v.Clip,-40} forward at moveAt {v.At(m.MoveAt, m.Duration),6:0.00}, at hit {v.At(m.HitTime, m.Duration),6:0.00}, at end {v.At(m.Duration, m.Duration),6:0.00}, min {v.Forward.Min():0.00}, max {v.Forward.Max():0.00}");
            // The step with no target, summed the way the simulation does it (30 Hz ticks over the whole strike), and with a target at each variant's distance.
            Console.WriteLine($"    simulated total, no target: {Sum(m, null, config):0.00} m (the clip's motion x{config.LungeScale * m.LungeFactorApplied:0.###})");
            Console.WriteLine($"    free step timeline: {Timeline(m, config)}");
            foreach (float d in new[] { 2f, 4f, 7f })
                Console.WriteLine($"    simulated total, target {d:0.#} m ahead: {Sum(m, d, config):0.00} m (the target is {Math.Max(0f, d - Sum(m, d, config)):0.00} m away at the end)");
        }
    }

    /// <summary>sim/instant-step: the no-target step's first tick, distance at the hit, peak speed and the time it passes half of its total.</summary>
    static string Timeline(MeleeStep m, SimConfig config)
    {
        float dt = config.TickSeconds, pos = 0f, first = 0f, atHit = 0f, peak = 0f, half = -1f;
        var steps = new List<(float T, float Step)>();
        for (float t = 0f; t < m.Duration; t += dt) { float step = MeleeLock.LungeStep(m, t, MathF.Min(t + dt, m.Duration), 0f, null, config); steps.Add((t, step)); }
        float total = steps.Sum(x => x.Step);
        foreach (var (t, step) in steps)
        {
            if (t == 0f) first = step;
            pos += step; peak = MathF.Max(peak, step / dt);
            if (t + dt <= m.HitTime + 1e-4f) atHit = pos;
            if (half < 0f && pos >= total / 2f) half = t + dt;
        }
        return $"first tick {first:0.00} m ({first / dt:0.0} m/s), by the hit {atHit:0.00} m, half of {total:0.00} m at {half:0.00} s, peak {peak:0.0} m/s";
    }

    static float Sum(MeleeStep m, float? targetDistance, SimConfig config)
    {
        float dt = config.TickSeconds, total = 0f, pos = 0f;
        for (float t = 0f; t < m.Duration; t += dt)
        {
            Vector2? toTarget = targetDistance is { } d ? new Vector2(0f, Math.Max(0f, d - pos)) : null;
            float step = MeleeLock.LungeStep(m, t, MathF.Min(t + dt, m.Duration), 0f, toTarget, config);
            pos += step; total += step;
        }
        return total;
    }
}
