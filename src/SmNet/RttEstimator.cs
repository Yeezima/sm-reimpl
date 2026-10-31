namespace SmNet;

/// <summary>
/// net/scoreboard-ping (protocol 50; owner 2026-10-31: "add ping (ms) value in the scoreboard for each player"; ours): a smoothed round-trip time in
/// milliseconds. The first sample is taken as it is; each later one moves the estimate by <see cref="Gain"/> of the difference (an exponentially weighted
/// moving average, TCP's smoothed RTT gain of 1/8). Samples and the estimate are clamped to 0..<see cref="MaxMs"/>, so one stall cannot hold the estimate
/// high for long. The server feeds it from snapshot acknowledgements (<c>GameServer</c>): the time from sending a snapshot to the first input batch that
/// acknowledges it, which includes up to one client tick of waiting for the next input send.
/// </summary>
public sealed class RttEstimator
{
    /// <summary>The weight of a new sample (1/8, ours: TCP's smoothed-RTT gain, RFC 6298).</summary>
    public const double Gain = 1.0 / 8.0;
    /// <summary>The largest round-trip time shown (ms): three digits on the scoreboard; anything longer reads as this.</summary>
    public const int MaxMs = 999;

    double _smoothed = -1.0;

    /// <summary>Samples taken so far.</summary>
    public int Samples { get; private set; }

    /// <summary>Folds in one round-trip sample (ms); a non-finite one is ignored.</summary>
    public void Add(double sampleMs)
    {
        if (!double.IsFinite(sampleMs)) return;
        double s = Math.Clamp(sampleMs, 0.0, MaxMs);
        _smoothed = _smoothed < 0.0 ? s : Math.Clamp(_smoothed + Gain * (s - _smoothed), 0.0, MaxMs);
        Samples++;
    }

    /// <summary>The smoothed round-trip time in whole milliseconds (0..<see cref="MaxMs"/>), or -1 before the first sample.</summary>
    public int Ms => _smoothed < 0.0 ? -1 : (int)Math.Round(_smoothed);

    /// <summary>Below this a ping shows green on the scoreboard (ms; ours).</summary>
    public const int GoodBelowMs = 80;
    /// <summary>Below this (and not good) a ping shows yellow; at or above it red (ms; ours).</summary>
    public const int FairBelowMs = 150;

    /// <summary>A scoreboard ping as text: "42", or "—" when not known (negative).</summary>
    public static string Display(int ms) => ms < 0 ? "—" : Math.Min(ms, MaxMs).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>How a scoreboard ping is coloured (ours): good below <see cref="GoodBelowMs"/>, fair below <see cref="FairBelowMs"/>, else poor; unknown when negative.</summary>
    public static PingQuality QualityOf(int ms) => ms < 0 ? PingQuality.Unknown : ms < GoodBelowMs ? PingQuality.Good : ms < FairBelowMs ? PingQuality.Fair : PingQuality.Poor;
}

/// <summary>A scoreboard ping's colour band (protocol 50; ours): green, yellow, red, or plain when not measured.</summary>
public enum PingQuality { Unknown, Good, Fair, Poor }
