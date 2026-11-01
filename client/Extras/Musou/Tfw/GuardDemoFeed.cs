using System;
using System.Collections.Generic;
using System.Numerics;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Test aid (TF7a, <c>--guard-demo N</c>): a scripted feed of Guardsmen that goes through the wire's own writers and the client state, so a windowed test shows the whole client path without a
/// running battle: N men in squads of 10 (<c>GuardSim</c>'s formation spacing), every second squad marching up and down and turning, all firing about once a second at a point 30 m ahead, one hurt
/// every second and one dead every two (he comes back five seconds later). **Placeholder demonstration** of the behaviour; the men drawn are the original assets. Deterministic.
/// </summary>
public sealed class GuardDemoFeed
{
    const int TicksPerSecond = 30, RecordEvery = 3, Squad = 10;
    const float Spacing = 1.5f, SquadGap = 14f, MarchMetres = 12f, MarchSeconds = 10f, DyingSeconds = 2f, AwaySeconds = 3f;

    readonly int _men;
    readonly Vector3 _centre;
    readonly List<TfwGuard> _guards = new();
    readonly List<TfwGuardEvent> _events = new();
    readonly float[] _deadAt;
    uint _tick;
    double _clock;

    /// <summary>The box its positions are written in (a box of its own: the map's director may have none).</summary>
    public MusouBox Box { get; }
    /// <summary>The render tick to sample at (as a client's interpolation delay).</summary>
    public double RenderTick => _tick - 3.0;

    public GuardDemoFeed(int men, Vector3 centre)
    {
        _men = Math.Clamp(men, 0, TfwWire.GuardCapacity);
        _centre = centre;
        _deadAt = new float[_men];
        Array.Fill(_deadAt, float.NegativeInfinity);
        Box = new MusouBox(centre - new Vector3(200f, 200f, 50f), centre + new Vector3(200f, 200f, 50f));
    }

    /// <summary>Advances the script by <paramref name="delta"/> seconds; the sections a server would have sent.</summary>
    public IEnumerable<ExtrasSection> Step(double delta)
    {
        _clock += delta;
        uint to = (uint)(_clock * TicksPerSecond);
        while (_tick < to)
        {
            _tick++;
            foreach (var s in Tick()) yield return s;
        }
    }

    IEnumerable<ExtrasSection> Tick()
    {
        float t = _tick / (float)TicksPerSecond;
        _guards.Clear(); _events.Clear();
        for (int i = 0; i < _men; i++)
        {
            ushort id = (ushort)(i + 1);
            float since = t - _deadAt[i];
            if (since < DyingSeconds + AwaySeconds)
            {
                if (since < DyingSeconds) _guards.Add(new TfwGuard(id, Place(i, _deadAt[i], out _, out _), 0f, (byte)GuardState.Dying));
                continue;
            }
            var p = Place(i, t, out float yaw, out bool moving);
            _guards.Add(new TfwGuard(id, p, yaw, (byte)(moving ? GuardState.Moving : GuardState.Idle)));
            int period = 24 + (i * 7) % 13;   // about one shot a second, out of step
            if ((_tick + i * 11) % period == 0)
            {
                float side = ((i * 37) % 21 - 10) * 1.5f;
                _events.Add(new TfwGuardEvent(TfwGuardEventKind.Shot, _tick, id, (i & 1) == 0 ? (ushort)1 : (ushort)0, p + new Vector3(side, 30f, 1.2f)));
            }
        }
        if (_men > 0)
        {
            if (_tick % TicksPerSecond == 7) _events.Add(new TfwGuardEvent(TfwGuardEventKind.Hurt, _tick, (ushort)(_tick / TicksPerSecond * 31 % _men + 1), 0, default));
            if (_tick % (2 * TicksPerSecond) == 13)
            {
                int i = (int)(_tick / TicksPerSecond * 17 % _men);
                if (t - _deadAt[i] > DyingSeconds + AwaySeconds)
                {
                    _deadAt[i] = t;
                    _events.Add(new TfwGuardEvent(TfwGuardEventKind.Death, _tick, (ushort)(i + 1), 0, Place(i, t, out _, out _)));
                }
            }
        }
        if (_tick % RecordEvery == 0)
            for (int at = 0; at < _guards.Count; at += TfwWire.MaxGuards)
                yield return new ExtrasSection(_tick, TfwWire.GuardsTag, TfwWire.WriteGuards(_guards.GetRange(at, Math.Min(TfwWire.MaxGuards, _guards.Count - at)), Box));
        for (int at = 0; at < _events.Count; at += TfwWire.MaxGuardEvents)
            yield return new ExtrasSection(_tick, TfwWire.GuardEventsTag, TfwWire.WriteGuardEvents(_events.GetRange(at, Math.Min(TfwWire.MaxGuardEvents, _events.Count - at)), Box));
    }

    /// <summary>A man's place: squads on a grid, four across, the odd ones marching along their column.</summary>
    Vector3 Place(int man, float t, out float yaw, out bool moving)
    {
        int squad = man / Squad, member = man % Squad;
        var at = new Vector2((squad % 4 - 1.5f) * SquadGap + (member % 5 - 2f) * Spacing, (squad / 4) * SquadGap + (member / 5 - 0.5f) * Spacing);
        yaw = 0f; moving = false;
        if ((squad & 1) == 1)
        {
            float wave = MathF.Sin(t / MarchSeconds * MathF.Tau + squad);
            float speed = MathF.Cos(t / MarchSeconds * MathF.Tau + squad);
            at.Y += wave * MarchMetres;
            moving = true;
            // Half of a march he faces his way, the other half he keeps his front and backs off.
            yaw = speed >= 0f || (squad & 2) == 2 ? 0f : MathF.PI;
        }
        return _centre + new Vector3(at, 0f);
    }
}
