namespace SmNet;

/// <summary>
/// A clock that runs at a settable fraction of another (developer tools' slow motion and pause, protocol 80; ours). Time stays continuous when the scale changes (the new rate counts from
/// that moment), so nothing jumps; scale 0 stands still. At scale 1 it reads exactly the inner clock plus a fixed offset of 0, so a client or server on it behaves as on the inner clock.
/// The server and every client scale their clocks together (the server's tick clock, the client's tick, interpolation and timeout clock), so ticks, prediction, interpolation and timers
/// follow one scaled time.
/// </summary>
public sealed class ScalableClock : IClock
{
    readonly IClock _inner;
    TimeSpan _anchorInner, _anchorScaled;
    double _scale = 1.0;

    public ScalableClock(IClock inner)
    {
        _inner = inner;
        _anchorInner = inner.Now;
        _anchorScaled = inner.Now;
    }

    /// <summary>The unscaled clock this one runs on (real time): what a rate limit, a keep-alive or a stuck-state timeout must use, since this one stands still when paused.</summary>
    public IClock Real => _inner;

    /// <summary>The fraction of real time that passes (0 to 1; 1: normal). Setting it counts the new rate from now.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
            if (value == _scale) return;
            _anchorScaled = Now;
            _anchorInner = _inner.Now;
            _scale = value;
        }
    }

    public TimeSpan Now => _scale == 1.0 ? _anchorScaled + (_inner.Now - _anchorInner) : _anchorScaled + (_inner.Now - _anchorInner) * _scale;
}
