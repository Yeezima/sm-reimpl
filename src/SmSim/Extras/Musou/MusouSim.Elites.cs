using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The elite and Boss Orks act on their target as the regular Orks do (the owner, 2026-11-09: "boss and elite orks are having issues that we fixed with regular orks. / not taking visibly doing
/// things when the player or other enemy targets (the ork elite/bosses targets) / boss ork shootas just run away instead of backpedaling and shooting / boss ork boys dont take melee swings, they just
/// kind of run around in melee combat"). Every value ours (spec "Musou Mode: elite and Boss Orks in the fight"); a war's pool only (the 5-wave run, without the crowd rules, as pinned); the server's
/// <c>SM_MUSOU_OLD_ELITES=1</c> restores the rules before (<see cref="MusouSim.OldElites"/>). Causes, measured in <c>MusouEliteOrkTests</c>:
/// <list type="bullet">
/// <item>A Boss Ork turned to his smoothed velocity every tick (<c>TurnToVelocity</c>) and skipped the engaged Orks' "face the target while moving" (<c>_faceWant</c>): a Boss Shoota backing off from a
/// target walking in turned round and ran (193 of 420 ticks retreating with his back to it, his facing 92 degrees off it on average), and fired 7 rounds where a Shoota Boy fired 12.</item>
/// <item>A target hands out its melee tokens (6 for a player, 12 for a squad) to the nearest Ork asking, one a tick: with Boyz already at it, a Nob's or a Boss Ork's wider body never counted as nearest
/// and every token stayed with the Boyz; and the push apart was the same both ways, so the ring of Boyz held a 3x body beyond its 2.4 m reach. A Nob, Boss Boy, Boss 'Ard Boy or Boss Nob 10 m from a
/// player with 12 Boyz round him struck 0 blows in 12 s, circling the crowd.</item>
/// </list>
/// </summary>
public static class MusouEliteRules
{
    /// <summary>Whether <paramref name="type"/> takes a melee token of its own past the target's count (the leaders: the Nobs, the melee Boss Orks and the Warboss between his abilities).</summary>
    public static bool OwnMeleeToken(MusouEnemyType type) => type is MusouEnemyType.Nob or MusouEnemyType.Warboss || (MusouBossOrks.Is(type) && type != MusouEnemyType.BossShootaBoy);

    /// <summary>A melee Boss Ork's way within this of his target (degrees) and he faces the target as he moves; farther off it (round a wall) he faces where he goes.</summary>
    public const float MeleeFaceDegrees = 45f;

    /// <summary>
    /// The share of the push apart between two bodies of <paramref name="mine"/> and <paramref name="theirs"/> radius that moves this one: twice the other's weight (radius squared) over both, so two of a
    /// size share it as before (1) and a big body wades through small ones (a Boss Ork beside a Boy takes 0.2 of it, the Boy 1.8).
    /// </summary>
    public static float PushShare(float mine, float theirs)
    {
        float a = mine * mine, b = theirs * theirs;
        return a + b <= 1e-6f ? 1f : 2f * b / (a + b);
    }
}

public sealed partial class MusouSim
{
    /// <summary>Test aid (server <c>SM_MUSOU_OLD_ELITES=1</c>): the elite and Boss Orks' fight before 2026-11-09 (no token of their own, an even push apart, a Boss Ork facing his velocity).</summary>
    public static bool OldElites { get; set; } = Environment.GetEnvironmentVariable("SM_MUSOU_OLD_ELITES") == "1";

    /// <summary>Whether the elite rules apply (a war's pool, not the old switch).</summary>
    bool EliteRules => !OldElites && Swarm is not null;

    /// <summary>A Nob or a melee Boss Ork in reach of a token takes one of his own (counted with the rest next tick, so the Boyz' next one waits).</summary>
    bool TakeOwnToken(int i, float reach, bool level)
    {
        if (!EliteRules || !MusouEliteRules.OwnMeleeToken(_type[i]) || !level || reach > Rules.MeleeTokenDistance) return false;
        _token[i] = MeleeToken;
        return true;
    }

    /// <summary>The push between this body and a neighbour's, weighted by their sizes (<see cref="MusouEliteRules.PushShare"/>); as before without the elite rules.</summary>
    float PushShareOf(float mine, float theirs) => EliteRules ? MusouEliteRules.PushShare(mine, theirs) : 1f;

    /// <summary>
    /// After a Boss Ork's move: engaged (<c>_faceWant</c>: a melee Boss closing in, a shooter in his band or backing off), he turns to his target at his own turn rate, so he backs off facing it and
    /// fires; else he turns to his smoothed velocity as before.
    /// </summary>
    void FaceBoss(int i, Vector2 desired)
    {
        if (EliteRules && _faceWant[i] != Vector2.Zero && (Rules.Of(_type[i]).Ranged || FacingAlong(desired, _faceWant[i])))
        {
            TurnTo(i, _faceWant[i], BossTurnRate * 180f / MathF.PI);
            _bossHeading[i] = _velocity[i];
            return;
        }
        TurnToVelocity(i);
    }

    /// <summary>
    /// A melee Boss Ork faces his target only while he heads at it (within <see cref="MusouEliteRules.MeleeFaceDegrees"/>): going round a wall to a target straight through it he faces where he goes,
    /// as before. A shooter faces his target in his whole band (backing off, holding, closing).
    /// </summary>
    static bool FacingAlong(Vector2 desired, Vector2 toward) =>
        desired.LengthSquared() > 1e-4f && Vector2.Dot(Vector2.Normalize(desired), toward) >= MathF.Cos(MusouEliteRules.MeleeFaceDegrees * MathF.PI / 180f);
}
