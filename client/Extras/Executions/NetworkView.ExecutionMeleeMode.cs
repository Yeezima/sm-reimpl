using SmClient.Customizer.Model;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Executions and the melee camera (owner 2026-11-26, decision series-37-pre-ship-executions "Melee cam": "Ignore them"): while the own finisher holds the player, Fire, Aim
/// and Reload do nothing to melee mode (<see cref="MeleeMode.Inputs.Locked"/>), so he comes out of the finisher still in the free melee camera and the next fresh press after the
/// End locks it as usual. Only with <see cref="ContentExtras.Executions"/>.
/// <list type="bullet">
/// <item><b>The lock</b>: the execution orbit camera's own test (<see cref="OwnExecuting"/>: the own live run from its Start until its End or its own end tick), so the melee mode and
/// the orbit's return (<c>ExecutionOrbitLeave</c>) agree on when the finisher is over.</item>
/// <item><b>Before the Start arrives</b> (lead 41): an Execute press made while the Execute marker showed (the prediction's press count, the rising edge X6 counts; the marker as the
/// last frame left it, as <c>ExecutionPromptRules.PressSpendsOffer</c> reads it) holds the mode speculatively (<see cref="FinisherPressHold"/>) until the Start is heard, an own End
/// is heard, or <see cref="FinisherPressHold.Bound"/> (0.6 s or twice the round trip) passes: a refusal or a lost Start falls back to the normal rule.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    readonly FinisherPressHold _meleeFinisherPress = new();
    ExecutionPrediction? _meleeHoldPrediction;
    int _meleeHoldPresses;
    bool _meleeHoldMarkerShown;

    partial void ExtrasMeleeModeLocked(PlayerState me, ref bool locked)
    {
        if (!ExecutionsOn || _client is not { } client) return;
        // A new prediction (a new connection) counts its presses from 0: that is no press.
        if (!ReferenceEquals(_execPrediction, _meleeHoldPrediction))
        {
            _meleeHoldPrediction = _execPrediction;
            _meleeHoldPresses = _execPrediction?.Presses ?? 0;
            _meleeFinisherPress.Reset();
        }
        int presses = _execPrediction?.Presses ?? 0;
        if (presses != _meleeHoldPresses && _meleeHoldMarkerShown) _meleeFinisherPress.Press(_connectedFor);
        _meleeHoldPresses = presses;
        _meleeHoldMarkerShown = ExecutionMarkerShown;
        bool live = OwnExecuting(out _);
        bool ended = false;
        if (!double.IsNaN(_meleeFinisherPress.PressedAt))
            foreach (var e in ExecutionsState.Ended)
                if (e.Run.Executor == client.PlayerId && e.HeardAt >= _meleeFinisherPress.PressedAt) { ended = true; break; }
        double rtt = client.EchoRoundTripMs > 0 ? client.EchoRoundTripMs / 1000.0 : client.HandshakeRoundTrip.TotalSeconds;
        bool speculative = _meleeFinisherPress.Held(_connectedFor, live, ended, FinisherPressHold.Bound(rtt));
        if (me.Alive && (live || speculative)) locked = true;
    }
}
