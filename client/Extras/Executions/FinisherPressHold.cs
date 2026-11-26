using System;

namespace SmClient;

/// <summary>
/// Executions and melee mode (owner 2026-11-26, "Melee cam": "Ignore them"; lead 41): the speculative part of <see cref="SmClient.Customizer.Model.MeleeMode.Inputs.Locked"/>. An Execute press that took a shown
/// target (the Execute marker was up) holds melee mode from the press until the server's Start of the own run is heard (the real lock then takes over), an own run's End is heard
/// after the press, or <see cref="Bound"/> passes with neither (a refusal, or a lost Start: the normal rule again). So a Fire, Aim or Reload press in the round trip between the press
/// and the Start does not leave melee mode either. Engine-free; times in seconds on one clock.
/// </summary>
public sealed class FinisherPressHold
{
    /// <summary>The shortest wait for the Start (seconds; ours, lead 41).</summary>
    public const double MinSeconds = 0.6;

    double _since = double.NaN;

    /// <summary>How long a press waits for its Start: <see cref="MinSeconds"/> or twice the round trip, whichever is longer (seconds).</summary>
    public static double Bound(double roundTripSeconds) => Math.Max(MinSeconds, 2.0 * Math.Max(0.0, roundTripSeconds));

    /// <summary>When the waiting press was made (NaN: none waits).</summary>
    public double PressedAt => _since;

    /// <summary>An Execute press on a shown target at <paramref name="now"/>.</summary>
    public void Press(double now) => _since = now;

    /// <summary>Forgets a waiting press (a new connection).</summary>
    public void Reset() => _since = double.NaN;

    /// <summary>
    /// Whether the waiting press still holds melee mode at <paramref name="now"/>: false (and forgotten) once <paramref name="live"/> (the own run's Start is heard: the real lock holds
    /// from here), <paramref name="endedSincePress"/> (an own run's End heard since the press) or <paramref name="bound"/> seconds have passed.
    /// </summary>
    public bool Held(double now, bool live, bool endedSincePress, double bound)
    {
        if (double.IsNaN(_since)) return false;
        if (live || endedSincePress || now - _since > bound || now < _since) { _since = double.NaN; return false; }
        return true;
    }
}
