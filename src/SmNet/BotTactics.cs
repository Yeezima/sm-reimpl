using System.Numerics;

namespace SmNet;

/// <summary>
/// Stream C of the owner's batch 2026-11-06 late (Classic PvP test bots; **ours, a test aid**, not the original AI): the choices that make a bot fight
/// more like a person. The owner's words: "AI bots in classic pvp mode, the assault marine in particular, are too deadly with melee. they should sometimes
/// fail to land their attacks, like a real person ... make the bots never run at you in a straight line, unless they are within reasonable range to try
/// and melee. the bots should prefer to strafe left and right and shoot in gunfights. make them flank around the side when one of their allies is already
/// engaging the enemy from the front." Every number here is an estimate (spec/behavior.md "Bot tactics"). The generator is seeded (the client uses the
/// player id), so a bot's sequence of choices repeats for the same inputs.
/// </summary>
public sealed class BotTactics
{
    /// <summary>Share of melee attacks (each press that starts a swing or a combo step, and each ground pound) a bot deliberately misses (ours).</summary>
    public const float MeleeMissChance = 0.3f;
    /// <summary>
    /// Degrees a missed swing's view is turned off the target: beyond the melee lock's 45 degree cone (whose apex sits 1 m behind the attacker) and the
    /// hit cone's 60 degrees, so neither the lock nor the hit takes the target (ours).
    /// </summary>
    public const float MissTurnDegrees = 80f;
    /// <summary>Within this horizontal distance (m) the lock takes any enemy in the front half (MeleeLockSpec.FanStart 1.5 m plus margin), so a miss turns further.</summary>
    public const float CloseMissDistance = 1.8f, CloseMissTurnDegrees = 105f;
    /// <summary>Metres beside the target a missed pound is aimed at: past the blast's full-damage 4.5 m and its stun (intensity 0.81), inside its 7 m edge (ours).</summary>
    public const float PoundMissOffset = 6f;
    /// <summary>Seconds a bot strafes one way before it may turn to the other side (uniform; ours).</summary>
    public const float StrafeMin = 0.7f, StrafeMax = 2.0f;
    /// <summary>Degrees off the straight line a bot weaves while it closes on a visible enemy (alternating sides; ours).</summary>
    public const float WeaveDegrees = 40f;
    /// <summary>Seconds per weave leg (uniform; ours).</summary>
    public const float WeaveMin = 0.8f, WeaveMax = 1.5f;
    /// <summary>Horizontal metres within which running straight at an enemy is allowed: the charge's reach (4 to 9 m) plus the melee step (ours).</summary>
    public const float StraightRunRange = 9f;
    /// <summary>An ally engages an enemy when it is within this many metres of it and faces it within <see cref="EngageFacingDegrees"/> (ours).</summary>
    public const float EngageRange = 40f, EngageFacingDegrees = 25f;
    /// <summary>A flank is reached when the bot is this many degrees around the enemy from the engaging ally (ours).</summary>
    public const float FlankDegrees = 70f;
    /// <summary>Metres from the enemy a not-yet-visible bot heads for when it flanks (ours; never farther than it already is).</summary>
    public const float FlankRadius = 15f;

    /// <summary>
    /// The owner's choice of 2026-11-07 ("Tighten for bots"): the share of a heavy-role bot's own weapon scatter it aims against (ours, an estimate; spec
    /// "Bot heavy-weapon spread").
    /// </summary>
    public const float HeavyScatterCancel = 0.35f;

    /// <summary>
    /// The shot offsets (yaw, pitch; radians) that take back <paramref name="share"/> of a scatter turn drawn at <paramref name="draw"/> (a point in the unit disc,
    /// <c>SmSim.Spread.Draw</c>) in half-widths of <paramref name="horizontalDegrees"/> and <paramref name="verticalDegrees"/>. <c>Spread.Turn</c> turns a shot
    /// "across" to its right (a smaller yaw) and "up" (a larger pitch), so the offsets point the other way.
    /// </summary>
    public static (float Yaw, float Pitch) ScatterTakeBack(Vector2 draw, float horizontalDegrees, float verticalDegrees, float share)
    {
        float across = draw.X * horizontalDegrees * MathF.PI / 180f, up = draw.Y * verticalDegrees * MathF.PI / 180f;
        return (share * across, -share * up);
    }

    readonly Random _random;
    double _strafeUntil = double.NegativeInfinity, _weaveUntil = double.NegativeInfinity;
    int _strafeSide, _weaveSide;

    public BotTactics(int seed)
    {
        _random = new Random(seed);
        _strafeSide = (seed & 1) == 0 ? 1 : -1;
        _weaveSide = -_strafeSide;
    }

    /// <summary>Draws whether this melee attack is a deliberate miss (<see cref="MeleeMissChance"/>).</summary>
    public bool RollMeleeMiss() => _random.NextDouble() < MeleeMissChance;

    /// <summary>The side (-1 left, 1 right) a missed swing or pound turns to.</summary>
    public int RollSide() => _random.Next(2) == 0 ? -1 : 1;

    /// <summary>The view turn (degrees) of a missed swing at an enemy <paramref name="horizontal"/> metres away.</summary>
    public static float MissTurn(float horizontal) => horizontal <= CloseMissDistance ? CloseMissTurnDegrees : MissTurnDegrees;

    /// <summary>The strafe side at <paramref name="now"/> seconds (-1 left, 1 right): held for <see cref="StrafeMin"/> to <see cref="StrafeMax"/> s, then turned.</summary>
    public int StrafeSide(double now)
    {
        if (now >= _strafeUntil)
        {
            if (!double.IsNegativeInfinity(_strafeUntil)) _strafeSide = -_strafeSide;
            _strafeUntil = now + StrafeMin + _random.NextDouble() * (StrafeMax - StrafeMin);
        }
        return _strafeSide;
    }

    /// <summary>Turns the strafe at once (the side was blocked); the next leg is drawn afresh.</summary>
    public void FlipStrafe(double now)
    {
        _strafeSide = -_strafeSide;
        _strafeUntil = now + StrafeMin + _random.NextDouble() * (StrafeMax - StrafeMin);
    }

    /// <summary>The weave angle (degrees, signed) at <paramref name="now"/>: <see cref="WeaveDegrees"/> to alternating sides, each leg <see cref="WeaveMin"/> to <see cref="WeaveMax"/> s.</summary>
    public float WeaveAngle(double now)
    {
        if (now >= _weaveUntil)
        {
            _weaveSide = -_weaveSide;
            _weaveUntil = now + WeaveMin + _random.NextDouble() * (WeaveMax - WeaveMin);
        }
        return _weaveSide * WeaveDegrees;
    }

    /// <summary>Whether a player at <paramref name="ally"/> facing <paramref name="allyYaw"/> (game yaw: forward = (-sin, cos)) engages an enemy at <paramref name="enemy"/>.</summary>
    public static bool Engages(Vector2 ally, float allyYaw, Vector2 enemy)
    {
        var to = enemy - ally;
        float d = to.Length();
        if (d > EngageRange || d < 1e-3f) return false;
        var forward = new Vector2(-MathF.Sin(allyYaw), MathF.Cos(allyYaw));
        return Vector2.Dot(to / d, forward) >= MathF.Cos(EngageFacingDegrees * MathF.PI / 180f);
    }

    /// <summary>Degrees around <paramref name="enemy"/> between <paramref name="me"/> and <paramref name="ally"/> (0 to 180).</summary>
    public static float AngleAround(Vector2 enemy, Vector2 me, Vector2 ally)
    {
        var a = me - enemy; var b = ally - enemy;
        float la = a.Length(), lb = b.Length();
        if (la < 1e-4f || lb < 1e-4f) return 0f;
        return MathF.Acos(Math.Clamp(Vector2.Dot(a / la, b / lb), -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>
    /// The strafe side (-1 left, 1 right, in the bot's frame facing the enemy) that carries the bot around <paramref name="enemy"/> away from the engaging
    /// <paramref name="ally"/>; 0 once it is <see cref="FlankDegrees"/> or more around (the flank is reached). Strafing while facing the enemy moves along the
    /// circle about it, so a held side opens the angle.
    /// </summary>
    public static int FlankSide(Vector2 enemy, Vector2 me, Vector2 ally)
    {
        float now = AngleAround(enemy, me, ally);
        if (now >= FlankDegrees) return 0;
        var toEnemy = enemy - me;
        if (toEnemy.LengthSquared() < 1e-6f) return 0;
        toEnemy = Vector2.Normalize(toEnemy);
        var right = new Vector2(toEnemy.Y, -toEnemy.X);
        float step = 0.5f;
        float ifRight = AngleAround(enemy, me + right * step, ally), ifLeft = AngleAround(enemy, me - right * step, ally);
        return ifRight >= ifLeft ? 1 : -1;
    }

    /// <summary>
    /// The ground point a bot that cannot see <paramref name="enemy"/> yet heads for to come in from the side: <see cref="FlankRadius"/> m (never farther than
    /// the bot is) from the enemy, a right angle around it from the engaging <paramref name="ally"/>, on the bot's side of the ally's line.
    /// </summary>
    public static Vector2 FlankPoint(Vector2 enemy, Vector2 me, Vector2 ally)
    {
        var fromAlly = ally - enemy;
        fromAlly = fromAlly.LengthSquared() > 1e-6f ? Vector2.Normalize(fromAlly) : new Vector2(0, 1);
        var side = new Vector2(fromAlly.Y, -fromAlly.X);
        var mine = me - enemy;
        if (Vector2.Dot(mine, side) < 0f) side = -side;
        float radius = MathF.Min(FlankRadius, MathF.Max(mine.Length(), 4f));
        return enemy + side * radius;
    }
}
