using System;
using System.Collections.Generic;
using SmNet;
using SmSim;

namespace SmClient.Bullets;

/// <summary>
/// The last 64 inputs as the wire carries them (sanitised, quantised: what the prediction steps and the server applies), by tick: the own shot of a tick
/// is drawn on the ray its input carried (feel/bullets; engine-free, tested).
/// </summary>
public sealed class SentInputRing
{
    readonly PlayerInput[] _ring = new PlayerInput[64];

    /// <summary>Remembers the input of its tick (<see cref="PlayerInput.Tick"/>).</summary>
    public void Note(PlayerInput input) => _ring[input.Tick & 63] = Protocol.Quantize(input.Sanitized());

    /// <summary>The input of <paramref name="tick"/>, or false when it is older than 64 ticks or was never noted.</summary>
    public bool TryGet(uint tick, out PlayerInput input)
    {
        input = _ring[tick & 63];
        return input.Tick == tick;
    }
}

/// <summary>Which tick's input an own shot of a frame belongs to.</summary>
public static class ShotTicks
{
    /// <summary>
    /// The tick of the <paramref name="nth"/> of the <paramref name="of"/> shots drawn this frame, from the frame's own events in tick order: the nth of the
    /// last <paramref name="of"/> events that fired (a slow frame runs several ticks; fewer fired events than shots clamp to the first or last). False when none fired.
    /// </summary>
    public static bool TryPick(IReadOnlyList<(uint Tick, SelfEvents Events)> events, int nth, int of, out uint tick)
    {
        tick = 0;
        int fired = 0;
        foreach (var (_, ev) in events) if (ev.Fired) fired++;
        if (fired == 0) return false;
        int want = Math.Clamp(fired - Math.Max(1, of) + nth, 0, fired - 1), seen = 0;
        foreach (var (t, ev) in events)
            if (ev.Fired && seen++ == want) { tick = t; return true; }
        return false;
    }
}
