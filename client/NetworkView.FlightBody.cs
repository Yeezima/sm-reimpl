using SmSim;

namespace SmClient;

/// <summary>
/// fix/hitboxes (S6 of the review): the client's own hit tests against other players (the crosshair's name, the own tracer's end, the pierce picture) use
/// the volumes of the pose this client draws them in: its jump track's take-off window, damped flight fraction and low or high set
/// (<see cref="SimConfig.HitBody(in PlayerState, FlightView)"/>, the same sets the server hits), and each class's own body (the Ork's).
/// </summary>
public partial class NetworkView
{
    /// <summary>The config <paramref name="other"/> is hit with as this client draws it.</summary>
    SimConfig HitBodyAsDrawn(PlayerState other)
    {
        var c = _config!;
        if (c.FlightHitVolumes is null || !_jumpTracks.TryGetValue(other.PlayerId, out var t)) return c.For(other.Class);
        float since = (float)(_connectedFor - t.PressAt);
        var view = t.View switch
        {
            JumpView.TakeOff => new FlightView(FlightViewPhase.TakeOff, since, float.NaN, t.Low),
            JumpView.Air when since < TakeOffBranch => new FlightView(FlightViewPhase.TakeOff, since, float.NaN, t.Low),
            JumpView.Air => new FlightView(FlightViewPhase.Flight, since, float.IsFinite(t.Fraction) ? t.Fraction : FlightPoseRules.Target(other), t.Low),
            _ => default,
        };
        return c.HitBody(other, view);
    }
}
