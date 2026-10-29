namespace SmNet;

/// <summary>
/// The weights of the standing aim-offset idle (M40-T5; client presentation, ours as far as the interpolation goes): a grid of clips, one column per horizontal
/// aim angle (the rifle's graph: left clips at -90, ahead at 0, right clips at +90 degrees) and in each column a list of clips by aim pitch (-80 down to +80 up,
/// the left and right columns with an extra clip at +40). The graph blends the neighbouring columns by <c>FLT_Aim_Horizontal_Deg_DAMPED</c> and in each the
/// neighbouring clips by <c>FLT_Aim_Vertical_Deg_DAMPED</c>; this does the same with linear weights (Havok's blender normalises the weights of the two
/// neighbouring children). Engine-free so the weights can be tested.
/// </summary>
public sealed class AimOffsetGrid
{
    readonly float[] _columnAngles;
    readonly int[] _columnStart;
    readonly float[][] _pitchKeys;

    /// <summary>Clips in the grid (column by column, each column's clips by ascending pitch).</summary>
    public int Count { get; }

    /// <param name="columnAngles">Ascending horizontal angles, degrees.</param>
    /// <param name="pitchKeys">Per column the ascending pitch angle of each of its clips, degrees.</param>
    public AimOffsetGrid(IReadOnlyList<float> columnAngles, IReadOnlyList<IReadOnlyList<float>> pitchKeys)
    {
        if (columnAngles.Count == 0 || columnAngles.Count != pitchKeys.Count || pitchKeys.Any(p => p.Count == 0)) throw new ArgumentException("a grid needs columns with clips");
        _columnAngles = columnAngles.ToArray();
        _pitchKeys = pitchKeys.Select(p => p.ToArray()).ToArray();
        _columnStart = new int[_columnAngles.Length];
        int n = 0;
        for (int c = 0; c < _columnAngles.Length; c++) { _columnStart[c] = n; n += _pitchKeys[c].Length; }
        Count = n;
    }

    /// <summary>Index of the first clip of column <paramref name="column"/>.</summary>
    public int ColumnStart(int column) => _columnStart[column];

    /// <summary>
    /// Fills <paramref name="weights"/> (length <see cref="Count"/>, cleared first) for an aim at <paramref name="horizontalDegrees"/> (negative left, positive right;
    /// clamped to the grid) and <paramref name="pitchDegrees"/> (positive up; clamped to each column's clips): the two neighbouring columns by the angle, in each the
    /// two neighbouring clips by the pitch. The weights sum to 1. Non-finite angles read as 0.
    /// </summary>
    public void Weights(float horizontalDegrees, float pitchDegrees, Span<float> weights)
    {
        weights.Clear();
        if (!float.IsFinite(horizontalDegrees)) horizontalDegrees = 0f;
        if (!float.IsFinite(pitchDegrees)) pitchDegrees = 0f;
        float h = Math.Clamp(horizontalDegrees, _columnAngles[0], _columnAngles[^1]);
        int c1 = 0;
        while (c1 < _columnAngles.Length - 1 && _columnAngles[c1] < h) c1++;
        int c0 = Math.Max(0, c1 - 1);
        if (c0 == c1 || _columnAngles[c1] - _columnAngles[c0] < 1e-6f) { AddColumn(c1, 1f, pitchDegrees, weights); return; }
        float wh = (h - _columnAngles[c0]) / (_columnAngles[c1] - _columnAngles[c0]);
        AddColumn(c0, 1f - wh, pitchDegrees, weights);
        AddColumn(c1, wh, pitchDegrees, weights);
    }

    void AddColumn(int column, float weight, float pitchDegrees, Span<float> weights)
    {
        if (weight <= 0f) return;
        var keys = _pitchKeys[column];
        float p = Math.Clamp(pitchDegrees, keys[0], keys[^1]);
        int k1 = 0;
        while (k1 < keys.Length - 1 && keys[k1] < p) k1++;
        int k0 = Math.Max(0, k1 - 1);
        if (k0 == k1 || keys[k1] - keys[k0] < 1e-6f) { weights[_columnStart[column] + k1] += weight; return; }
        float wp = (p - keys[k0]) / (keys[k1] - keys[k0]);
        weights[_columnStart[column] + k0] += weight * (1f - wp);
        weights[_columnStart[column] + k1] += weight * wp;
    }
}
