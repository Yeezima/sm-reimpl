using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/melee-lock on the player's install (read-only; skipped without it, never in CI): the global targeting values, and every simulated melee
/// weapon's attacks stepped with and without a target (the numbers in spec/behavior.md "Melee target lock"). No original bytes: only what the readers build.
/// </summary>
public class MeleeLockInstallTests(ITestOutputHelper output)
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);
    static readonly Lazy<SimConfig?> Tuned = new(() => Game.Value is { } g ? GameplayTuning.Load(g).Config : null);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void The_lock_reads_the_global_targeting_values()
    {
        var spec = Tuned.Value!.MeleeLock;
        Assert.Equal((45f, 7f, 1.5f, 1f, 6f, 1.5f, 1f, 7f, 45f),
            (spec.HalfAngle, spec.Range, spec.Vertical, spec.DistanceBand, spec.AngleBand, spec.FanStart, spec.FanSetBack, spec.RushRange, spec.RushHalfAngle));
    }

    /// <summary>One attack from rest facing +Y with the target <paramref name="distance"/> m ahead (null: none), until its MoveAt; the attacker's travel and the distance at the hit.</summary>
    static (float Travel, float AtHit, float AtMove) Swing(SimConfig c, int cls, int choice, bool heavy, int step, float? distance)
    {
        var self = Loadouts.Choose(c, Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c), -1, -1, choice, -1, -1, -1);
        var target = Combat.Respawned(new PlayerState(2, new Vector3(0, distance ?? 50f, 0), Vector3.Zero, MathF.PI, true) { Class = cls }, c);
        var view = new Dictionary<int, PlayerState> { [2] = target };
        var list = heavy ? Loadouts.MeleeHeavy(c, self) : Loadouts.Melee(c, self);
        var m = list[step - 1];
        // Put the attacker straight into the step (its time 0) as a chain would.
        self = self with { MeleeStep = step, MeleeHeavy = heavy, MeleeTime = 0f, MeleeHeld = true, MeleeHeavyHeld = true };
        float atHit = float.NaN, atMove = float.NaN;
        for (int i = 0; i < 90 && self.MeleeStep == step && self.MeleeHeavy == heavy; i++)
        {
            float before = self.MeleeTime;
            self = Combat.StepPlayer(self, new PlayerInput((uint)i, 0, 0, 0f, (heavy ? InputButtons.MeleeHeavy : InputButtons.Melee), MeleeTarget: distance is null ? 0 : 2), c, out _, null, view);
            float d = Vector3.Distance(self.Position, target.Position);
            if (before < m.HitTime && self.MeleeTime >= m.HitTime) atHit = d;
            if (before < m.MoveAt && (self.MeleeTime >= m.MoveAt || self.MeleeStep != step)) atMove = self.Position.Y;
        }
        return (self.Position.Y, atHit, atMove);
    }

    [InstallFact]
    public void Every_attack_steps_forward_and_a_locked_one_ends_in_reach()
    {
        var c = Tuned.Value!;
        var done = new HashSet<string>();
        for (int cls = 0; cls < c.Classes.Count; cls++)
            for (int choice = 0; choice < c.Classes[cls].MeleeChoices.Count; choice++)
            {
                var combo = c.Classes[cls].MeleeChoices[choice];
                if (!done.Add(combo.Wargear)) continue;
                foreach (bool heavy in new[] { false, true })
                {
                    var steps = heavy ? combo.Heavy : combo.Steps;
                    for (int step = 1; step <= steps.Count; step++)
                    {
                        var m = steps[step - 1];
                        var free = Swing(c, cls, choice, heavy, step, null);
                        var line = $"{combo.Wargear} {(heavy ? "Y" : "X")}{step}: no target {free.AtMove:F2} m by MoveAt;";
                        // The owner: every swing steps forward (none backs off without a target).
                        Assert.True(!(free.AtMove < -0.01f), $"{combo.Wargear} {(heavy ? "Y" : "X")}{step}: backs off {free.AtMove} m without a target");
                        foreach (float d in new[] { 2f, 3.5f, 5f, 7f })
                        {
                            var locked = Swing(c, cls, choice, heavy, step, d);
                            line += $" {d} m → {locked.AtHit:F2} at the hit;";
                            // A target ahead is within the hit's reach when the hit lands, wherever the attack's farthest clip covers the gap before its hit (some
                            // attacks travel mostly after their hit: the chainsword's kick).
                            if (m.Reach is { Variants.Count: > 1 } r && m.Damage > 0f && r.Variants.Max(v => v.At(m.HitTime, m.Duration)) >= d - c.MeleeRange + 0.1f)
                                Assert.True(locked.AtHit <= c.MeleeRange + 0.05f, $"{combo.Wargear} {(heavy ? "Y" : "X")}{step}: {d} m target at {locked.AtHit} m at the hit");
                            Assert.True(!(locked.AtHit < c.MeleeLock.Separation - 0.05f), $"{combo.Wargear} {(heavy ? "Y" : "X")}{step}: closer than the separation");
                        }
                        output.WriteLine(line);
                    }
                }
            }
        Assert.Contains("sm_mp_melee_combat_knife", done);
        // The owner: "you should step forward when swinging": the knife's first attack steps forward without a target.
        var knife = Enumerable.Range(0, c.Classes.Count).Select(k => (k, c.Classes[k].MeleeChoices.ToList().FindIndex(m => m.Wargear == "sm_mp_melee_combat_knife"))).First(p => p.Item2 >= 0);
        Assert.True(Swing(c, knife.k, knife.Item2, false, 1, null).AtMove > 0.5f);
    }
}
