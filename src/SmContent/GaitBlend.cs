using System.Numerics;

namespace SmContent;

/// <summary>One clip of a blend: its direction and band in the <see cref="GaitSpace"/> (the order they were given in) and its weight (the weights of a solution sum to 1).</summary>
public readonly record struct GaitWeight(int Direction, int Band, float Weight);

/// <summary>
/// The locomotion blend space of one weapon family (M40-T4; client presentation, ours; replaces the nearest-of-eight-clips choice): looping clips in
/// <b>directions</b> (eight around the body for the relaxed and ready runs, three over -45 to +45 degrees for the sprint) and <b>bands</b> of speed in each
/// direction (walk, run). Given the body's ground velocity it picks the two neighbouring directions and the two neighbouring bands, the four weights,
/// and the <b>cadence</b> (cycles per second) at which the shared phase (<see cref="GaitPhase"/>) must advance, so that the planted feet of the blended
/// pose move backward by exactly the body's velocity and do not slide.
/// <para>
/// The maths (all from <see cref="ClipGait.Stride"/>, the metres a clip moves the body per cycle): a clip of weight w played so that the cycle
/// takes 1/f seconds moves the body by f x w x stride per second, so the blend moves it by f x C with C = sum of w x stride. The band weight comes from
/// the speed (so that the walk and run strides blend linearly as the body speeds up), the direction weight from requiring C to point along the velocity
/// (one cross product), and f = |velocity| / |C|. A cadence further than <see cref="MinRatio"/> / <see cref="MaxRatio"/> from the blend's own (the
/// weights of 1 / clip length) is clamped, so a stride is stretched at most that far; the foot then slides by the difference. The directions are
/// ordered by the angle of their own stride (not the label), so a clip that travels 40 degrees when named 45 is still placed right.
/// </para>
/// </summary>
public sealed class GaitSpace
{
    /// <summary>Largest change of a clip's own pace (cadence over the blend's natural cadence) the blend allows; the walk clips are played down to 0.6, the run clips up to 1.5.</summary>
    public const float MinRatio = 0.6f, MaxRatio = 1.5f;

    readonly ClipGait[][] _clips;      // [direction][band]
    readonly float[] _angles;          // degrees of each direction's stride (run band), input order
    readonly int[] _sorted;            // direction indices by ascending angle
    readonly bool _cyclic;

    public int DirectionCount => _clips.Length;
    public int BandCount { get; }

    /// <summary>The most clips one solution uses.</summary>
    public const int MaxWeights = 4;

    /// <summary>
    /// A space from <paramref name="directions"/>: each a nominal angle in degrees (0 forward, + right, ±180 back; used when the stride gives none) and its
    /// bands' clips from the slowest to the fastest; every direction has the same number of bands. <paramref name="cyclic"/>: the directions go all round
    /// (the last neighbours the first across ±180); else the ends are the limits (the sprint).
    /// </summary>
    public GaitSpace(IReadOnlyList<(float Angle, IReadOnlyList<ClipGait> Bands)> directions, bool cyclic)
    {
        if (directions.Count == 0) throw new ArgumentException("no directions");
        BandCount = directions[0].Bands.Count;
        if (BandCount == 0) throw new ArgumentException("no bands");
        _clips = new ClipGait[directions.Count][];
        _angles = new float[directions.Count];
        for (int i = 0; i < directions.Count; i++)
        {
            if (directions[i].Bands.Count != BandCount) throw new ArgumentException("the directions have different numbers of bands");
            _clips[i] = directions[i].Bands.ToArray();
            var run = _clips[i][^1].Stride;
            _angles[i] = run.Length() > 0.1f ? Degrees(run) : directions[i].Angle;
        }
        _sorted = Enumerable.Range(0, directions.Count).OrderBy(i => _angles[i]).ToArray();
        _cyclic = cyclic;
    }

    static float Degrees(Vector2 v) => MathF.Atan2(v.X, v.Y) * 180f / MathF.PI;

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>The clip of a direction and band.</summary>
    public ClipGait Clip(int direction, int band) => _clips[direction][band];

    /// <summary>The angle (degrees) a direction's stride moves the body at.</summary>
    public float AngleOf(int direction) => _angles[direction];

    /// <summary>The two neighbouring directions of an angle and the angular fraction from the first to the second (equal indices at an end of a non-cyclic space).</summary>
    void Bracket(float theta, out int lo, out int hi, out float fraction)
    {
        int n = _sorted.Length;
        lo = hi = _sorted[0]; fraction = 0f;
        if (n == 1) return;
        float first = _angles[_sorted[0]], last = _angles[_sorted[n - 1]];
        if (theta <= first && !_cyclic) { lo = hi = _sorted[0]; return; }
        if (theta >= last && !_cyclic) { lo = hi = _sorted[n - 1]; return; }
        if (theta >= last || theta < first)
        {
            // Across the seam: from the last direction up round to the first.
            lo = _sorted[n - 1]; hi = _sorted[0];
            float span = first + 360f - last;
            float t = theta >= last ? theta - last : theta + 360f - last;
            fraction = span > 1e-3f ? Math.Clamp(t / span, 0f, 1f) : 0f;
            return;
        }
        for (int k = 0; k < n - 1; k++)
        {
            float a = _angles[_sorted[k]], b = _angles[_sorted[k + 1]];
            if (theta >= a && theta < b)
            {
                lo = _sorted[k]; hi = _sorted[k + 1];
                fraction = b - a > 1e-3f ? (theta - a) / (b - a) : 0f;
                return;
            }
        }
    }

    /// <summary>
    /// Solves for a body ground velocity (m/s; X right, Y forward). Writes up to <see cref="MaxWeights"/> weights (dropping those under 0.001) and returns how
    /// many; zero for a velocity of (nearly) zero. <paramref name="cadence"/> is the phase speed in cycles per second, <paramref name="ratio"/> the cadence
    /// over the blend's natural one (1 = every clip at its own speed; the clamp is applied), <paramref name="exact"/> whether the ratio was inside the limits
    /// and the direction weight inside [0, 1] (so the blend's planted feet cancel the velocity exactly).
    /// </summary>
    public int Solve(Vector2 velocity, Span<GaitWeight> output, out float cadence, out float ratio, out bool exact)
    {
        cadence = 0f; ratio = 1f; exact = true;
        float speed = velocity.Length();
        if (!(speed > 1e-3f) || !float.IsFinite(speed) || output.Length < MaxWeights) return 0;
        Bracket(Degrees(velocity), out int lo, out int hi, out float fraction);

        // Bands: the speed of each band at this heading (the two directions' strides blended by angle), then the pair around the velocity's speed.
        int k = 0;
        float u = 0f;
        if (BandCount > 1)
        {
            float SpeedOf(int band)
            {
                var stride = Vector2.Lerp(_clips[lo][band].Stride, _clips[hi][band].Stride, fraction);
                float duration = _clips[lo][band].Duration + (_clips[hi][band].Duration - _clips[lo][band].Duration) * fraction;
                return duration > 0f ? stride.Length() / duration : 0f;
            }
            k = BandCount - 2;
            for (int b = 0; b < BandCount - 1; b++)
                if (speed <= SpeedOf(b + 1)) { k = b; break; }
            float v0 = SpeedOf(k), v1 = SpeedOf(k + 1);
            u = v1 - v0 > 1e-3f ? Math.Clamp((speed - v0) / (v1 - v0), 0f, 1f) : 0f;
        }
        int k2 = BandCount > 1 ? k + 1 : k;

        // Direction: the weight w in [0, 1] for which the blended stride is parallel to the velocity.
        var c0 = _clips[lo][k].Stride * (1f - u) + _clips[lo][k2].Stride * u;
        var c1 = _clips[hi][k].Stride * (1f - u) + _clips[hi][k2].Stride * u;
        float w = lo == hi ? 0f : fraction;
        if (lo != hi)
        {
            float a = Cross(c0, velocity), b = Cross(c1, velocity);
            float denominator = a - b;
            if (MathF.Abs(denominator) > 1e-4f)
            {
                float solved = a / denominator;
                if (solved is >= 0f and <= 1f && Vector2.Dot(c0 * (1f - solved) + c1 * solved, velocity) > 0f) w = solved;
                else { w = Math.Clamp(solved, 0f, 1f); exact = false; }
            }
            else exact = false;
        }
        var stride2 = c0 * (1f - w) + c1 * w;
        float strideLength = stride2.Length();
        if (!(strideLength > 1e-3f)) return 0;

        // Cadence: the body moves by cadence x stride per second. The natural cadence is the weights' mean of 1 / length.
        float meanLength = (1f - w) * ((1f - u) * _clips[lo][k].Duration + u * _clips[lo][k2].Duration)
                         + w * ((1f - u) * _clips[hi][k].Duration + u * _clips[hi][k2].Duration);
        float natural = meanLength > 1e-3f ? 1f / meanLength : 1f;
        float wanted = speed / strideLength;
        ratio = Math.Clamp(wanted / natural, MinRatio, MaxRatio);
        if (MathF.Abs(ratio * natural - wanted) > 1e-3f * wanted) exact = false;
        cadence = natural * ratio;

        int n = 0;
        Span<float> raw = stackalloc float[4];
        raw[0] = (1f - w) * (1f - u);
        raw[1] = k2 != k ? (1f - w) * u : 0f;
        raw[2] = hi != lo ? w * (1f - u) : 0f;
        raw[3] = hi != lo && k2 != k ? w * u : 0f;
        for (int i = 0; i < 4; i++)
            if (raw[i] >= 1e-3f) output[n++] = new GaitWeight(i < 2 ? lo : hi, i % 2 == 0 ? k : k2, raw[i]);
        // Renormalise after dropping the negligible ones.
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += output[i].Weight;
        if (!(sum > 0f)) return 0;
        for (int i = 0; i < n; i++) output[i] = output[i] with { Weight = output[i].Weight / sum };
        return n;
    }
}

/// <summary>Small rules around the blend space (M40-T4; client presentation, ours).</summary>
public static class GaitBlend
{
    /// <summary>
    /// Weight of the moving clips against the idle pose: 0 standing still, 1 from <paramref name="fullSpeed"/> up (m/s), linear between, so a
    /// reversal through zero speed passes through the idle pose instead of flipping from one direction's clip to the other's (the original's
    /// walk-on-the-spot clip sat at the speed-0 end of the same blend).
    /// </summary>
    public static float MovingWeight(float speed, float fullSpeed) =>
        fullSpeed > 0f && float.IsFinite(speed) ? Math.Clamp(speed / fullSpeed, 0f, 1f) : (speed > 0f ? 1f : 0f);
}

/// <summary>
/// A low-pass filter on the body's ground velocity (M40-T4): the blend parameters (heading and speed) come from this instead of the raw velocity, so the
/// 30 Hz steps of the simulation and the snapshots, and a reversal's jump from one heading to the opposite, do not make the pose flicker. An exponential
/// filter of time constant <see cref="Seconds"/>; it snaps to the target after a gap of more than <see cref="GapSeconds"/> (no frames drawn: the player
/// was not shown) or a jump of more than <see cref="SnapSpeed"/> m/s (a respawn or teleport).
/// </summary>
public sealed class VelocityDamper
{
    /// <summary>Time constant (s); about two simulation ticks. The simulation reaches full speed in 0.125 s, so the filter lags a start by well under a tenth of a second (ours).</summary>
    public const float Seconds = 0.06f;
    public const float GapSeconds = 0.25f, SnapSpeed = 30f;

    public Vector2 Value { get; private set; }
    bool _has;

    public Vector2 Step(Vector2 target, float dt)
    {
        if (!float.IsFinite(target.X) || !float.IsFinite(target.Y)) return Value;
        if (!_has || !(dt >= 0f) || dt > GapSeconds || (target - Value).Length() > SnapSpeed) { Value = target; _has = true; return Value; }
        float alpha = 1f - MathF.Exp(-dt / Seconds);
        Value += (target - Value) * alpha;
        return Value;
    }

    public void Reset() => _has = false;
}
