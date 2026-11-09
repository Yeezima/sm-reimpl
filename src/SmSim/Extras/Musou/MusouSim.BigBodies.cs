using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The big Orks' bodies against the map (the owner, 2026-11-09, with a recording on Ashfall: "boss enemies are getting stuck along objects and moving in slow motion"). Every value ours (spec "Musou Mode:
/// the big Orks' deaths and gait"); a war's pool only (the 5-wave run, without the crowd rules, as pinned); the server's <c>SM_MUSOU_OLD_BIG_BODIES=1</c> restores the rules before
/// (<see cref="MusouSim.OldBigBodies"/>). Causes, measured on the Ashfall draft 10 walk graph (<c>BigOrkSnagTests</c>):
/// <list type="bullet">
/// <item>Height: whether a target was level enough to strike, to wait in reach of, to take a melee token for or to ring was 1.5 m for every body. A Boss Ork stands 5.7 m (the Warboss 13 m), so with
/// the player two stair steps up a landing he could neither strike nor take a token, and stood beside the stair (the recording's last 15 s: two Boss Orks at the parapet, no blow). Now the vertical
/// reach grows with the body's height over a Boy's (<see cref="MusouBigBodyRules.VerticalReach"/>, at most 3x: 4.5 m), and a shooter's eye sits at the same share of his height (a 6.5 m Boss Shoota
/// looked out at 1.5 m, below every parapet).</item>
/// <item>Ground speed: a Boss Ork's lean off a near wall (<c>BossWallPush</c>, a 3x body on a graph with a Boy's margin) was added to his move and clamped with it, and a step that left the graph fell
/// back to its x or y part alone: on Ashfall's streets the Boss Orks moved at 3.8 to 3.9 m/s of their 4.4 (13 % lost scraping). Now the lean never takes from the way he wants (its backward part
/// is dropped and the move keeps the wanted speed), and a blocked step slides along the wall (the step less its part into the wall, at full length) before the axis fall-backs.</item>
/// </list>
/// </summary>
public static class MusouBigBodyRules
{
    /// <summary>A Boy's height (m, <see cref="MusouRules"/>'s Boy row): the height the 1.5 m level reach was set for.</summary>
    public const float BoyHeight = 1.9f;
    /// <summary>The level reach of an ordinary body (m), as before.</summary>
    public const float LevelReach = 1.5f;
    /// <summary>The most the level reach grows by (x): a Boss Ork's 3x, the Warboss the same.</summary>
    public const float MostScale = 3f;
    /// <summary>A body's eye as a share of a Boy's 1.5 m over his 1.9 m.</summary>
    public const float EyeShare = 1.5f / 1.9f;

    /// <summary>The height difference (m) within which a body of <paramref name="height"/> m reaches a target: 1.5 m for a Boy or smaller, growing with the height up to 3x.</summary>
    public static float VerticalReach(float height) => LevelReach * Math.Clamp(height / BoyHeight, 1f, MostScale);

    /// <summary>A shooter's eye above his feet (m): 1.5 m for a Boy or smaller, else the same share of his height up to <see cref="MostScale"/>x.</summary>
    public static float Eye(float height) => 1.5f * Math.Clamp(height / BoyHeight, 1f, MostScale);
}

public sealed partial class MusouSim
{
    /// <summary>Test aid (server <c>SM_MUSOU_OLD_BIG_BODIES=1</c>): the big bodies' reach, eye, lean and blocked steps before 2026-11-09.</summary>
    public static bool OldBigBodies { get; set; } = Environment.GetEnvironmentVariable("SM_MUSOU_OLD_BIG_BODIES") == "1";

    bool BigBodyRules => !OldBigBodies && Swarm is not null;

    /// <summary>The height difference within which this enemy reaches a target (1.5 m before, and for every body of a Boy's height or less).</summary>
    float LevelReachOf(int i) => BigBodyRules ? MusouBigBodyRules.VerticalReach(Body(_type[i]).Height) : MusouBigBodyRules.LevelReach;

    /// <summary>This enemy's eye above its feet for the line of sight.</summary>
    float EyeOf(int i) => BigBodyRules ? MusouBigBodyRules.Eye(Body(_type[i]).Height) : 1.5f;

    /// <summary>A Boss Ork's lean off a wall, without its part against <paramref name="desired"/>; the move then keeps the wanted speed.</summary>
    Vector2 LeanAdded(Vector2 v, Vector2 desired, Vector2 lean)
    {
        if (!BigBodyRules) return v + lean;
        float want = desired.Length();
        if (want > 1e-4f)
        {
            var along = desired / want;
            float back = Vector2.Dot(lean, along);
            if (back < 0f) lean -= along * back;
        }
        var w = v + lean;
        float len = w.Length(), keep = MathF.Max(want, v.Length());
        if (len > 1e-4f && (len < want || len > keep)) w *= (len < want ? want : keep) / len;   // neither slower nor faster than the move without the lean
        return w;
    }

    /// <summary>A blocked step slid along the wall at its full length: the step less its part into the wall (the graph's edge gradient points away from it); -1 when there is none.</summary>
    int SlideAlongWall(int i, Vector3 p, Vector2 step, out Vector2 at)
    {
        at = default;
        if (!BigBodyRules || Walk is null || _node[i] < 0 || !MusouBigOrkSize(_type[i])) return -1;
        var away = Walk.EdgeGradient(_node[i]);
        if (away.LengthSquared() < 1e-6f) return -1;
        away = Vector2.Normalize(away);
        float into = Vector2.Dot(step, away);
        if (into >= 0f) return -1;
        var slide = step - away * into;
        float len = slide.Length(), full = step.Length();
        if (len < 1e-4f) return -1;
        slide *= full / len;
        int node = Walk.MoveTo(_node[i], p.X + slide.X, p.Y + slide.Y, _aim[i]);
        at = new Vector2(p.X + slide.X, p.Y + slide.Y);
        return node;
    }

    /// <summary>The bodies the slide is for: wider than a Boy (the Nobs, the Boss Orks, the Warboss, the Big Shoota Nob).</summary>
    bool MusouBigOrkSize(MusouEnemyType type) => Body(type).Height > MusouBigBodyRules.BoyHeight + 0.2f || type == MusouEnemyType.Nob;
}
