namespace SmNet;

/// <summary>
/// The test bots' aim error (M44a; **ours, a test aid**, not the original AI): the angle (yaw and pitch, radians) by which a bot's
/// view is turned off the line to its target while it shoots, so that bots miss most of their shots in recorded footage but still
/// look as if they were trying. It has two parts, each Gaussian per axis: a slow wandering offset (an Ornstein-Uhlenbeck process
/// with time constant <see cref="WanderTime"/> s, so the aim drifts about the target) and a fresh scatter for every tick the
/// trigger is held (every shot of a full-automatic weapon, every press of a semi-automatic one). The error is an angle, so a
/// shot misses by more metres the farther the target is.
/// <para>
/// <c>--bot-accuracy a</c> (0 to 1) is "the fraction of shots meant to hit at medium range": with a total per-axis standard
/// deviation σ the chance that a 2D Gaussian error stays inside a disc of angular radius r/d is 1 - exp(-(r/d)²/2σ²); setting that
/// to <c>a</c> at <see cref="ReferenceDistance"/> with the effective body radius <see cref="BodyRadius"/> gives σ
/// (<see cref="SigmaFor"/>). Accuracy 1 means no error at all. The wander takes <see cref="WanderShare"/> of σ and the per-shot scatter
/// <see cref="ShotShare"/> (their squares add up to σ²). The generator is seeded (the client uses the player id): the same seed gives the same sequence of errors, but a whole run does not repeat exactly, since what the bot sees differs from run to run.
/// </para>
/// </summary>
public sealed class BotAimModel
{
    /// <summary>Medium range (metres) at which <c>--bot-accuracy</c> is the intended hit fraction (ours).</summary>
    public const float ReferenceDistance = 15f;
    /// <summary>Effective radius (metres) of the target's body in the hit-fraction formula (ours; calibrated against the hit volumes by a measured run, spec "Test bot aim").</summary>
    public const float BodyRadius = 0.5f;
    /// <summary>Seconds the wandering offset takes to forget itself (ours).</summary>
    public const float WanderTime = 1.5f;
    /// <summary>Share of σ in the slow wander and in the per-shot scatter (0.8² + 0.6² = 1).</summary>
    public const float WanderShare = 0.8f, ShotShare = 0.6f;
    /// <summary>Accuracy at and above which there is no error.</summary>
    public const float Perfect = 0.999f;
    /// <summary>The lowest accuracy used (0 would be an infinite error).</summary>
    public const float Floor = 0.01f;

    readonly Random _random;
    float _wanderYaw, _wanderPitch;

    public BotAimModel(float accuracy, int seed)
    {
        Accuracy = float.IsFinite(accuracy) ? Math.Clamp(accuracy, 0f, 1f) : 1f;
        Sigma = SigmaFor(Accuracy);
        _random = new Random(seed);
    }

    /// <summary>The accuracy asked for (0 to 1).</summary>
    public float Accuracy { get; }
    /// <summary>Total per-axis standard deviation (radians); 0 for a perfect aim.</summary>
    public float Sigma { get; }
    /// <summary>The aim is perfect (no error is ever added).</summary>
    public bool IsPerfect => Sigma <= 0f;

    /// <summary>The per-axis σ (radians) that makes <paramref name="accuracy"/> of the shots land within <paramref name="radius"/> at <paramref name="distance"/> (see the class).</summary>
    public static float SigmaFor(float accuracy, float distance = ReferenceDistance, float radius = BodyRadius)
    {
        if (!float.IsFinite(accuracy) || accuracy >= Perfect) return 0f;
        float a = Math.Clamp(accuracy, Floor, Perfect);
        return radius / distance / MathF.Sqrt(-2f * MathF.Log(1f - a));
    }

    /// <summary>The chance that a shot at <paramref name="distance"/> lands within <paramref name="radius"/> of the aim point for a total per-axis σ (the formula behind <see cref="SigmaFor"/>).</summary>
    public static float HitChance(float sigma, float distance, float radius = BodyRadius)
    {
        if (sigma <= 0f) return 1f;
        float theta = radius / Math.Max(distance, 0.1f);
        return 1f - MathF.Exp(-theta * theta / (2f * sigma * sigma));
    }

    float Gauss()
    {
        double u1 = 1.0 - _random.NextDouble(), u2 = _random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    /// <summary>
    /// The error for a tick of <paramref name="dt"/> seconds, in two parts (radians): the slow <c>Wander</c> (the view may follow it: it moves a little each tick)
    /// and, when <paramref name="firing"/>, a fresh per-shot <c>Scatter</c> (only for the shot's direction: the protocol's shot offsets, so the body does not shake).
    /// </summary>
    public (float WanderYaw, float WanderPitch, float ScatterYaw, float ScatterPitch) StepParts(float dt, bool firing)
    {
        if (IsPerfect) return (0f, 0f, 0f, 0f);
        float keep = MathF.Exp(-dt / WanderTime);
        float spread = Sigma * WanderShare * MathF.Sqrt(1f - keep * keep);
        _wanderYaw = _wanderYaw * keep + spread * Gauss();
        _wanderPitch = _wanderPitch * keep + spread * Gauss();
        if (!firing) return (_wanderYaw, _wanderPitch, 0f, 0f);
        float shot = Sigma * ShotShare;
        return (_wanderYaw, _wanderPitch, shot * Gauss(), shot * Gauss());
    }

    /// <summary>The whole error (wander plus scatter) for a tick: the shot's direction relative to the line to the target.</summary>
    public (float Yaw, float Pitch) Step(float dt, bool firing)
    {
        var (wy, wp, sy, sp) = StepParts(dt, firing);
        return (wy + sy, wp + sp);
    }
}
