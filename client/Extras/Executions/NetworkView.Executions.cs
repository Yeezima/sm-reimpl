using System;
using System.Collections.Generic;
using SmNet;
using SmNet.Extras.Executions;
using SmSim;

namespace SmClient;

/// <summary>
/// Executions X5a: the networked view's one dispatcher for executions (docs/added-content.md section 13). It implements X1's <see cref="ExecutionsEnvelope"/> (tag 50 into
/// <see cref="ExecutionsState"/>) and <see cref="ExecutionsFrame"/>, and the core client seams of X5a (<c>ExtrasExecutionPlace</c>, <c>ExtrasExecutionOrbitLook</c>,
/// <c>ExtrasExecutionPadLook</c>, <c>ExtrasExecutionCamera</c>, <c>ExtrasDeathHold</c>, <c>ExtrasObserverFrame</c>), each forwarding to a partial that X5 (presentation: the
/// paired animation, proxies, death hold) or X6 (camera, input, prompt, shield) implements in its own file. Every forward returns at once without
/// <see cref="ContentExtras.Executions"/> in the match's config, so the faithful game and Musou Mode are untouched.
/// </summary>
public partial class NetworkView
{
    /// <summary>The executions the server announced (tag 50), the own offer and the overshield list.</summary>
    internal readonly ExecutionClientState ExecutionsState = new();

    /// <summary>Whether this match runs executions (Classic PvP Extended, Warfront).</summary>
    bool ExecutionsOn => _config is { } c && c.Extras.HasFlag(ContentExtras.Executions);

    partial void ExecutionsEnvelope(byte tag, byte[] payload, ref bool handled)
    {
        if (tag != ExecutionWire.Tag || !ExecutionsOn) return;
        handled = true;
        ExecutionsState.Apply(payload, _connectedFor, _client?.PlayerId ?? -1);
    }

    partial void ExecutionsFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        ExecutionsPrune();
        ExecutionsPresentFrame(me, others, delta);   // X5 (ExecutionPresenter.cs)
        ExecutionsViewFrame(me, others, delta);      // X6 (camera, input, prompt, shield)
    }

    partial void ExtrasObserverFrame(IReadOnlyList<PlayerState> others, double delta)
    {
        if (!ExecutionsOn || _client is not { Status: ClientStatus.Connected } client) return;
        foreach (var section in client.TakeExtras())
            if (section.Tag == ExecutionWire.Tag) ExecutionsState.Apply(section.Payload, _connectedFor, -1);   // an observer: only tag 50 (the other features never drew for it)
        ExecutionsPrune();
        ExecutionsPresentFrame(default, others, delta);
    }

    /// <summary>
    /// The state's prune at the view tick (the remote players' render tick: the tick the marker shows the offer at and the server checks a press at; X5 review X5-5), with each live run's own
    /// last tick (<see cref="ExecutionPrediction.LastTick"/>: the track's end, 12 s for a row this install lacks), so a run whose End was lost ends (X5 review X5-1). The live list's silence is
    /// measured on the newest snapshot's tick, which stands still through a receive stall (the view tick runs on with the wall clock; X5 re-review 2). The view tick is the frame's
    /// <c>_othersTick</c> (set by the frame before the extras run), so no second interpolation of the remote players.
    /// </summary>
    void ExecutionsPrune()
    {
        if (_client is not { } client) { ExecutionsState.Prune(_connectedFor, 0, 0); return; }
        double snapshotTick = client.LatestSnapshotTick;
        double viewTick = _othersTick > 0 ? _othersTick : snapshotTick;
        _execLastTick ??= ExecRunLastTick;
        ExecutionsState.Prune(_connectedFor, viewTick, snapshotTick, _execLastTick, _config?.TickRate ?? 30);
    }

    Func<SmSim.Extras.Executions.ExecutionRun, uint>? _execLastTick;

    uint ExecRunLastTick(SmSim.Extras.Executions.ExecutionRun run) =>
        ExecutionPrediction.LastTick(run, _config is { } c ? ExecutionRowsOf(c).AtWire(run.Wire) : null, _config?.TickRate ?? 30);

    partial void ExtrasExecutionPlace(CharacterViewer character, PlayerState state, bool own, ref bool handled)
    {
        if (ExecutionsOn) ExecutionsPlace(character, state, own, ref handled);
    }

    partial void ExtrasExecutionMeleeShown(PlayerState state, ref bool shown)
    {
        if (ExecutionsOn && state.Alive && ExecutionsState.TryLive(state.PlayerId, out _)) shown = true;   // X5: the executor's melee weapon in hand through the move
    }

    partial void ExtrasDeathHold(int id, CharacterViewer character, PlayerState state, bool own, ref bool hold)
    {
        if (ExecutionsOn) ExecutionsDeathHold(id, character, state, own, ref hold);
    }

    partial void ExtrasExecutionCamera(PlayerState me, float delta, ref bool handled)
    {
        if (ExecutionsOn) ExecutionsCameraWanted(me, delta, ref handled);
    }

    partial void ExtrasExecutionOrbitLook(float yaw, float pitch, ref bool used)
    {
        if (ExecutionsOn) ExecutionsOrbitLook(yaw, pitch, ref used);
    }

    partial void ExtrasExecutionPadLook(double delta, ref bool used)
    {
        if (ExecutionsOn) ExecutionsPadLook(delta, ref used);
    }

    // ---- X5 (presentation): implemented in ExecutionPresenter.cs, ExecutionProxy.cs, NetworkView.ExecutionsDeathHold.cs ----

    /// <summary>Once a frame (also for an observer, <paramref name="me"/> default): proxies, the crowd hide set, clean-up.</summary>
    partial void ExecutionsPresentFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta);
    /// <summary>A live executor's character: posed by its paired clip, placed by the track; <paramref name="handled"/> set.</summary>
    partial void ExecutionsPlace(CharacterViewer character, PlayerState state, bool own, ref bool handled);
    /// <summary>Classic Extended: a melee victim's death waits (its pose held) for the window or until its proxy takes over.</summary>
    partial void ExecutionsDeathHold(int id, CharacterViewer character, PlayerState state, bool own, ref bool hold);

    // ---- X6 (camera, input, HUD, shield): implemented in NetworkView.ExecutionCamera.cs, NetworkView.ExecutionInput.cs ----

    /// <summary>Once a frame after the presentation (X6: the prediction delegates, the shell, the prompt's target).</summary>
    partial void ExecutionsViewFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta);
    /// <summary>The own executor's orbit camera; <paramref name="handled"/> set when it placed the camera.</summary>
    partial void ExecutionsCameraWanted(PlayerState me, float delta, ref bool handled);
    /// <summary>Mouse look while alive (radians); <paramref name="used"/> set when the orbit took it.</summary>
    partial void ExecutionsOrbitLook(float yaw, float pitch, ref bool used);
    /// <summary>Right stick while alive; <paramref name="used"/> set when the orbit took it.</summary>
    partial void ExecutionsPadLook(double delta, ref bool used);
}
