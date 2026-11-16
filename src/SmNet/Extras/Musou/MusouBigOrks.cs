using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// The big Orks' deaths and gait as drawn in Musou Mode (the owner, 2026-11-09: "the elite and boss orks and the warboss need to have death animations. they also look like they move in slow motion
/// because of the way they're scaled up. resolve these issues."). Client presentation only; engine-free so the tests share the rules. Every value is ours (spec "Musou Mode: the big Orks' deaths and gait").
/// <para>
/// Deaths. The big kinds are the elites and the bosses of <see cref="MusouTargetRules.ClassOf"/> (Nob, 'Ard Boy, Big Shoota Nob; the Warboss and the four Boss Orks). Before, a death the gore called
/// an explosion (a frag, launcher or plasma burst, every bolt blast: a bolt weapon's round bursts at every enemy it strikes, so most bolter kills; a lascannon shot) and a share of the melee and strafe
/// deaths tore the body apart: its burst was drawn and no body was left, so a 9 m Boss Ork or the 13 m Warboss vanished in a spray without falling. Now a big Ork is never torn apart: the burst still
/// plays, and the body plays its death clip and lies like any Ork's (a blast's throw and the Nob's ragdoll unchanged). The Warboss's rig has no death that ends lying (his campaign death is scripted:
/// <c>WB_REC_Explode</c>, the clip played before, is a blast reaction that stands him up again), so his death is his get-up, <c>WB_Get_Up</c>, played backwards: from standing he drops to his knees
/// and falls on his back (<c>WarbossRig.DeathReversed</c>).
/// </para>
/// <para>
/// Gait. The crowd's run plays at the drawn speed over the run clip's planted-foot speed at the look's scale (<see cref="MusouGaitRules"/>), so a body drawn 3x as large at an ordinary Ork's speed (a
/// Boss Ork's 4.4 m/s against the run clip's 18 m/s at 3x) played its run at a quarter of the rate, and the Warboss's walk likewise: slow motion. The simulation's speeds are not changed (that is
/// gameplay). Instead the stride the rate is matched to grows with the square root of the scale (<see cref="StrideExponent"/>, the dynamic similarity of walking bodies), not the scale itself: a 3x
/// Boss Ork steps 1.7 times as often as before, and his feet slide forward by that much over the ground (the price of a quicker step at the same ground speed). Attacks, staggers and deaths play on
/// the server's state clock at 1x and turns follow the server's facing: no rate there was scaled. The client's <c>--musou-old-big-orks</c> restores both rules (torn big bodies, the reaction for a
/// death, the full scale's stride), <c>--musou-stride-exponent f</c> tries another exponent (0: an ordinary Ork's cadence, 1: before).
/// </para>
/// </summary>
public static class MusouBigOrkRules
{
    /// <summary>The power of the look's scale the run's stride grows by (0.5: its square root; 1 before).</summary>
    public const float StrideExponent = 0.5f;

    /// <summary>Whether a kind is one of the big Orks: an elite or a boss.</summary>
    public static bool IsBig(MusouEnemyType type) => MusouTargetRules.ClassOf(type) != MusouTargetClass.Ordinary;

    /// <summary>Whether a death the gore would tear apart leaves this kind's body whole (its burst still drawn): every big Ork's, unless <paramref name="old"/>.</summary>
    public static bool KeepsBody(MusouEnemyType type, bool old) => !old && IsBig(type);

    /// <summary>The factor a run clip's planted-foot speed at scale 1 is multiplied by for a look drawn at <paramref name="scale"/> (<paramref name="exponent"/>: <see cref="StrideExponent"/>; 1 before).</summary>
    public static float StrideScale(float scale, float exponent) => scale > 0f ? MathF.Pow(scale, Math.Clamp(exponent, 0f, 1f)) : 1f;
}
