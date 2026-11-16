using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou;

/// <summary>What an Ork is for target preference (expansion S1, docs/musou-expansion.md "Targeting").</summary>
public enum MusouTargetClass : byte { Ordinary = 0, Elite = 1, Boss = 2 }

/// <summary>
/// Target preference by kind in Musou Mode battles (expansion S1; every value ours, owner brief 2026-11-07, spec "Musou expansion: target preference"). A target's score is its
/// distance times its weight (lower wins): an ordinary Ork weighs a Guard squad, an AI Marine (puppet id) or an objective at 1 and a human player at 2.5 (so a player counts only when
/// about 2.5 times nearer), and a human within <see cref="PlayerWinsWithin"/> wins outright; an elite (Nob, 'Ard Boy, Big Shoota Nob) or boss (the Warboss, the Boss Orks) weighs an AI Marine 1,
/// a human 0.8 and a Guard squad or objective 3. Applied only in a Tfw battle (<see cref="MusouSim.War"/> set), so the skirmish and the 5-wave run keep the nearest-target rule.
/// A preferred target within <see cref="PreferRange"/> is walked to directly (the shared flow field leads to the nearest, not to it).
/// </summary>
public static class MusouTargetRules
{
    public const float OrdinaryGuard = 1f, OrdinaryMarine = 1f, OrdinaryObjective = 1f, OrdinaryPlayer = 2.5f;
    public const float EliteGuard = 3f, EliteMarine = 1f, EliteObjective = 3f, ElitePlayer = 0.8f;
    /// <summary>An ordinary Ork goes for a human this near (metres, straight, height x 3) whatever else is about.</summary>
    public const float PlayerWinsWithin = 12f;
    /// <summary>A preference is honoured (and walked to directly) within this distance; beyond it the flow field's nearest rules (metres).</summary>
    public const float PreferRange = 40f;
    /// <summary>The current target's score is multiplied by this at a re-choice, so two near-equal targets do not flip every half second.</summary>
    public const float Stickiness = 0.85f;

    /// <summary>Elite or boss kinds weigh differently from the rest.</summary>
    public static MusouTargetClass ClassOf(MusouEnemyType type) =>
        type == MusouEnemyType.Warboss || MusouBossOrks.Is(type) ? MusouTargetClass.Boss
        : type is MusouEnemyType.Nob or MusouEnemyType.ArdBoy or MusouEnemyType.BigShootaNob ? MusouTargetClass.Elite
        : MusouTargetClass.Ordinary;

    /// <summary>The weight of <paramref name="target"/> for an Ork of class <paramref name="cls"/> (see the class summary).</summary>
    public static float Weight(MusouTargetClass cls, in MusouTarget target)
    {
        bool elite = cls != MusouTargetClass.Ordinary;
        if (target.Goal) return elite ? EliteObjective : OrdinaryObjective;
        if (TfwContracts.IsGuardTarget(target.Id)) return elite ? EliteGuard : OrdinaryGuard;
        if (TfwContracts.IsPuppet(target.Id)) return elite ? EliteMarine : OrdinaryMarine;
        return elite ? ElitePlayer : OrdinaryPlayer;
    }

    /// <summary>Whether <paramref name="target"/> is a human player (not a Guard squad, an objective or an AI Marine).</summary>
    public static bool IsHuman(in MusouTarget target) => !target.Goal && target.Id > 0 && !TfwContracts.IsPuppet(target.Id);

    /// <summary>
    /// The preferred target's index among <paramref name="targets"/> for an Ork of <paramref name="type"/> at <paramref name="from"/>, or -1 when none lies within <see cref="PreferRange"/>.
    /// <paramref name="current"/> (index or -1) is favoured by <see cref="Stickiness"/>.
    /// </summary>
    public static int Choose(MusouEnemyType type, Vector3 from, ReadOnlySpan<MusouTarget> targets, int current)
    {
        var cls = ClassOf(type);
        int best = -1, nearHuman = -1; float bestScore = float.MaxValue, humanDistance = float.MaxValue;
        for (int t = 0; t < targets.Length; t++)
        {
            ref readonly var target = ref targets[t];
            if (!target.Alive) continue;
            var d = target.Position - from;
            float distance = MathF.Sqrt(d.X * d.X + d.Y * d.Y + 9f * d.Z * d.Z);
            if (distance > PreferRange) continue;
            if (cls == MusouTargetClass.Ordinary && IsHuman(target) && distance <= PlayerWinsWithin && distance < humanDistance) { humanDistance = distance; nearHuman = t; }
            float score = distance * Weight(cls, target) * (t == current ? Stickiness : 1f);
            if (score < bestScore) { bestScore = score; best = t; }
        }
        return nearHuman >= 0 ? nearHuman : best;
    }
}
