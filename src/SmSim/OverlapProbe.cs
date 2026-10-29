using System.Globalization;
using System.Numerics;

namespace SmSim;

/// <summary>
/// sim/player-collision test aid (server <c>--log-overlaps</c>, the client's bot log, tests): per sample (a tick, a frame), the clearance of the closest
/// pair of living players' capsules (<see cref="PlayerCollision.Clearance(IReadOnlyList{Vector3}, SimConfig)"/>: negative = overlapping that deep),
/// and how many samples had a pair touching (within 5 cm) or overlapping by more than 1, 5 and 20 cm.
/// </summary>
public sealed class OverlapProbe
{
    public long Samples { get; private set; }
    /// <summary>Samples with at least two living players whose heights overlap.</summary>
    public long Paired { get; private set; }
    /// <summary>The smallest clearance seen (metres; +∞ before any pair).</summary>
    public float Closest { get; private set; } = float.PositiveInfinity;
    public long Touching { get; private set; }
    public long Over1cm { get; private set; }
    public long Over5cm { get; private set; }
    public long Over20cm { get; private set; }

    public void Add(IEnumerable<PlayerState> players, SimConfig c) => Add(players.Where(p => p.Alive).Select(p => p.Position).ToList(), c);

    public void Add(IReadOnlyList<Vector3> feet, SimConfig c) => Add(PlayerCollision.Clearance(feet, c));

    /// <summary>One sample's clearance (+∞: no pair).</summary>
    public void Add(float clearance)
    {
        Samples++;
        if (!float.IsFinite(clearance)) return;
        Paired++;
        Closest = MathF.Min(Closest, clearance);
        if (clearance < 0.05f) Touching++;
        if (clearance < -0.01f) Over1cm++;
        if (clearance < -0.05f) Over5cm++;
        if (clearance < -0.20f) Over20cm++;
    }

    public string Summary()
    {
        string Share(long n) => Paired == 0 ? "0" : (100.0 * n / Paired).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        string closest = float.IsFinite(Closest) ? Closest.ToString("0.000", CultureInfo.InvariantCulture) : "none";
        return $"{Samples} samples ({Paired} with a pair), closest clearance {closest} m; touching (< 5 cm) {Touching} ({Share(Touching)}), " +
               $"overlapping > 1 cm {Over1cm} ({Share(Over1cm)}), > 5 cm {Over5cm} ({Share(Over5cm)}), > 20 cm {Over20cm} ({Share(Over20cm)})";
    }
}
