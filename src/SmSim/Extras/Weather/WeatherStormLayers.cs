namespace SmSim.Extras.Weather;

/// <summary>
/// How much of Warfront's Ashfall storm look stays in the air under each weather preset (weather fix 1; presentation only, client only, **ours**; pure, no engine types). The storm's camera-following layers come in two
/// groups: the near ones (ash, rock flakes, embers, floor dust) and the far ones (tumbling debris, dust banks). <see cref="Of"/> gives each group's share (0 to 1) for a preset, phase, progress within the phase and the
/// shelter level; <see cref="Ease"/> slides the shown value toward it. Fires, smoke plumes, light shafts, grime, sky, fog and sun are not touched by any preset.
/// </summary>
public static class WeatherStormLayers
{
    /// <summary>Seconds a change of the target takes to show (ours, an estimate; F6 switches and preset changes included).</summary>
    public const double EaseSeconds = 2.0;

    /// <summary>The near and far shares (0 to 1) of the storm's airborne layers. None, Ash Squall and Ember Wind build on the storm: the near layers fade out under a roof, the far ones stay. Clear, Low Fog and Rain have neither.
    /// Thunderstorm has the far layers only, at the storm's active level (the weather finish: full from its start, easing to its lull of 0.6 in the opening and the recovery, back to full through the buildup).</summary>
    public static (float Near, float Far) Of(WeatherId id, WeatherPhase phase, double progress, float shelter)
    {
        if (!float.IsFinite(shelter)) shelter = 0f;
        shelter = System.Math.Clamp(shelter, 0f, 1f);
        float p = double.IsFinite(progress) ? (float)System.Math.Clamp(progress, 0.0, 1.0) : 0f;
        switch (id)
        {
            case WeatherId.None:
            case WeatherId.AshSquall:
            case WeatherId.EmberWind:
                return (1f - shelter, 1f);
            case WeatherId.Thunderstorm:
                // the weather finish: the far layers follow the storm's active level (full at once, 0.5 to 1 after), the same smoothstep shape as WeatherClock
                float low = WeatherClock.Lull(WeatherCatalog.Of(WeatherId.Thunderstorm)), f = p * p * (3f - 2f * p);
                return (0f, phase switch { WeatherPhase.Buildup => low + (1f - low) * f, WeatherPhase.Peak => 1f, _ => 1f - (1f - low) * f });
            default:   // Clear, Low Fog, Rain
                return (0f, 0f);
        }
    }

    /// <summary>One frame of the smoothing: moves <paramref name="shown"/> toward <paramref name="target"/> at a rate of 1 per <see cref="EaseSeconds"/> (a full swing takes that long).</summary>
    public static float Ease(float shown, float target, double dt)
    {
        if (!(dt > 0) || double.IsInfinity(dt)) return shown;
        float step = (float)(dt / EaseSeconds);
        return shown < target ? System.Math.Min(target, shown + step) : System.Math.Max(target, shown - step);
    }
}
