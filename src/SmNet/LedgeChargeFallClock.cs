using System;
using SmSim;

namespace SmNet;

/// <summary>
/// sim/ledge-charge-air, answer 3 (the owner 2026-11-23: "Keep landings like today"): the client presentation of a Classic PvP ledge charge's fall follows the drop height, so a ledge charge off a given ledge looks,
/// sounds and feels as at protocol 89 although the simulation drops it under 4 x gravity (<see cref="LedgeChargeAir"/>). Engine-free, one per drawn player, fed once per frame; presentation only (nothing reaches the
/// simulation, the wire or the config hash). Its outputs replace the render clock and the vertical speed in the fall presentation's inputs: the fall pose's delays (<c>FallRules.ChargeShowsFall</c>,
/// <c>EvadeShowsFall</c>), the fall tracker (<c>FallTracker.Observe</c>: its peak speed decides the hard landing, <c>WeightFeel</c>'s settle and camera dip; its air time the landing feel), the landing sound's
/// air time and its speed parameter.
/// <list type="bullet">
/// <item>While the bull rush is held in the air under k x gravity (<see cref="LedgeChargeAir.GravityScale"/>, Classic PvP and the restore switch's gate), the equivalent protocol 89 fall of the same height runs √k
/// times as long and √k times slower: the clock runs at √k and the vertical speed is v / √k (exact for a fall from rest: h = k g t² / 2 = g (√k t)² / 2).</item>
/// <item>When the charge ends in the air (its press comes as many ticks after the departure as at protocol 89), the clock goes back to the render clock. Into a plain fall (a light melee, a reaction) the speed starts
/// at protocol 89's after those ticks (v / k) and the rest of the fall is protocol 89's rest stretched in time to the real one (the caller's height above the ground gives both); into a hop, a jump or the
/// pack the speed is the body's (the hop flies as at protocol 89 from its press).</item>
/// </list>
/// </summary>
public sealed class LedgeChargeFallClock
{
    enum Phase : byte { None, Held, Free }

    Phase _phase;
    double _offset, _lastNow = double.NaN;
    float _lastZ, _eqSpeed, _heldSpeed, _heldScale = 1f, _departZ, _restOurs = float.NaN, _restTheirs, _fallenSince;
    double _t0, _tDepart;
    float _stretch = 1f;

    /// <summary>After a charge ends in the air, the presentation clock stays the render clock this long (s: the fall tracker's debounce and one tick), then stretches.</summary>
    public const float HoldSeconds = FallDebounceSeconds + 1f / 30f;
    /// <summary><c>SmContent.FallRules.DebounceSeconds</c> (SmNet does not reference SmContent; a test ties the two).</summary>
    public const float FallDebounceSeconds = 2f / 30f;
    /// <summary><c>SmContent.FallTracker.GapSeconds</c> (a test ties the two): a frame gap longer than this forgets the clock (a player not drawn, a respawn).</summary>
    public const double GapSeconds = 0.25;
    /// <summary>The largest single-frame jump of <see cref="Now"/> beyond the frame's own time (s): under <see cref="GapSeconds"/>, so the tracker never takes a landing for a gap.</summary>
    public const double MaxJump = 0.2;
    bool _wasGrounded = true;

    /// <summary>The presentation clock (seconds): the render clock plus the time the equivalent fall has gained.</summary>
    public double Now { get; private set; }
    /// <summary>The vertical speed the presentation reads (m/s, up positive).</summary>
    public float VerticalSpeed { get; private set; }
    /// <summary>How far <see cref="Now"/> moved since the last frame (seconds; negative when a hop drops the gained time).</summary>
    public double Step { get; private set; }
    /// <summary>The speed parameter's value (|velocity| with <see cref="VerticalSpeed"/>) of this frame and of the frame before.</summary>
    public float Speed { get; private set; }
    public float LastSpeed { get; private set; }
    /// <summary>Whether the equivalent fall is in use this frame.</summary>
    public bool Mapping => _phase != Phase.None;

    /// <summary>Whether this frame (<paramref name="now"/>) has been fed already (a caller can skip work the second time, e.g. the ray for the height).</summary>
    public bool Fed(double now) => now == _lastNow;

    /// <summary>The state's velocity with its vertical part replaced by <see cref="VerticalSpeed"/> (the landing sound's speed parameter).</summary>
    public System.Numerics.Vector3 Velocity(PlayerState s) => new(s.Velocity.X, s.Velocity.Y, VerticalSpeed);

    /// <summary>One frame at render time <paramref name="now"/> for state <paramref name="s"/>; a second call with the same time returns the same values.</summary>
    /// <param name="heightAboveGround">The feet's height above the level below (m; NaN when unknown): when the charge ends in the air, the rest of the fall is stretched over it so the whole drop presents as
    /// protocol 89's (the body is lower at the press than it was there: (1 - 1/k) of what it had fallen).</param>
    public void Feed(double now, PlayerState s, SimConfig? c, float heightAboveGround = float.NaN)
    {
        if (now == _lastNow) return;
        // A stale clock (the player was not drawn for a while, a returning id): start afresh, no Step over the gap.
        if (double.IsFinite(_lastNow) && !(now - _lastNow <= GapSeconds)) { _phase = Phase.None; _offset = 0; _lastNow = double.NaN; _wasGrounded = true; Now = now; Speed = 0f; }
        double previous = Now;
        bool first = !double.IsFinite(_lastNow);
        double dt = double.IsFinite(_lastNow) ? Math.Max(0.0, now - _lastNow) : 0.0;
        _lastNow = now;
        float g = c is null ? 0f : MathF.Max(0f, c.Gravity);
        float k = c is null || s.Grounded || !s.Alive ? 1f : LedgeChargeAir.GravityScale(s, c);
        float vz = float.IsFinite(s.Velocity.Z) ? s.Velocity.Z : 0f;
        float z = float.IsFinite(s.Position.Z) ? s.Position.Z : _lastZ;
        if (k > 1f)
        {
            float root = MathF.Sqrt(k);
            if (_phase == Phase.Held) _offset += (root - 1f) * dt;
            else { _departZ = _wasGrounded ? _lastZ : z; _tDepart = now - dt; }
            _phase = Phase.Held;
            _eqSpeed = MathF.Max(0f, -vz) / root;
            _heldSpeed = MathF.Max(0f, -vz); _heldScale = k;
            VerticalSpeed = vz / root;
        }
        else if (_phase != Phase.None && s.Alive && !s.Grounded && s.Jump == JumpPhase.None && !Evade.Evading(s))
        {
            // The charge ended in the air (a light melee, a reaction). The press came after as many ticks as at protocol 89, where the body was higher by (1 - 1/k) of what it had fallen and fell at
            // v / k. The rest of the fall is presented as protocol 89's rest: the height still to fall is mapped onto protocol 89's (stretched by (rest + deficit) / rest) and the speed is that fall's at the
            // mapped height (from v / k under normal gravity); the clock is the render clock for HoldSeconds (so the fall pose and its tracker start on protocol 89's tick), then runs stretched so that the
            // touchdown frame is at protocol 89's touchdown. Without a height (no level below known) the speed grows from v / k with the render clock.
            if (_phase == Phase.Held)
            {
                _t0 = now - dt; _eqSpeed = _heldSpeed / _heldScale; _fallenSince = 0f;
                float deficit = MathF.Max(0f, _departZ - _lastZ) * (1f - 1f / _heldScale);
                _restOurs = heightAboveGround + MathF.Max(0f, _lastZ - z);
                _restTheirs = _restOurs + deficit;
                float ours = Rise(MathF.Max(0f, -vz), _restOurs, g), theirs = Rise(_eqSpeed, _restTheirs, g);
                // the clock: the render clock for the tracker's start (HoldSeconds: the debounce and a tick), then stretched so the touchdown comes at protocol 89's
                _stretch = ours > HoldSeconds + 1e-3f && theirs > HoldSeconds ? (theirs - HoldSeconds) / (ours - HoldSeconds) : 1f;
            }
            _fallenSince += MathF.Max(0f, _lastZ - z);
            _phase = Phase.Free;
            if (vz > 0f) VerticalSpeed = vz;
            else if (float.IsFinite(_restOurs) && _restOurs > 0.05f)
            {
                float left = float.IsFinite(heightAboveGround) ? MathF.Max(0f, heightAboveGround) : MathF.Max(0f, _restOurs - _fallenSince);
                float fallen = MathF.Max(0f, _restTheirs - left * (_restTheirs / _restOurs));
                double e = now - _t0;
                _offset = e <= HoldSeconds ? 0 : (e - HoldSeconds) * (_stretch - 1f);
                VerticalSpeed = -MathF.Sqrt(_eqSpeed * _eqSpeed + 2f * g * fallen);
            }
            else
            {
                _offset = 0;
                VerticalSpeed = -(_eqSpeed + g * (float)(now - _t0));
            }
        }
        else
        {
            if (_phase != Phase.None && !s.Grounded) _offset = 0;   // a hop, a jump or the pack: back to the body's own time
            else if (_phase == Phase.Free && s.Grounded && float.IsFinite(_restOurs) && _restOurs > 0.05f) _offset = _t0 + Rise(_eqSpeed, _restTheirs, g) - now;   // the landing at protocol 89's touchdown
            else if (_phase == Phase.Held && s.Grounded) _offset = Math.Max(_offset, _tDepart + Rise(0f, _departZ - z, g) - now);   // a charge landing: protocol 89's touchdown of that drop (from rest)
            if (s.Grounded && _wasGrounded) _offset = 0;   // the landing frame keeps the gained time (its air time is read on it); the frame after drops it
            _phase = Phase.None;
            VerticalSpeed = vz;
        }
        _lastZ = z;
        _wasGrounded = s.Grounded;
        // a landing on other ground than the ray saw (a crate, a slope) or a short drop can ask for a long jump: kept under the tracker's gap
        if (!first && now + _offset - previous > dt + MaxJump) _offset = previous + dt + MaxJump - now;
        Now = now + _offset;
        Step = first ? 0.0 : Now - previous;
        LastSpeed = first ? 0f : Speed;
        Speed = Velocity(s).Length();
    }

    /// <summary>The time to fall <paramref name="height"/> m from a downward speed <paramref name="speed"/> under <paramref name="g"/> (s; 0 for no height or no gravity).</summary>
    static float Rise(float speed, float height, float g) =>
        !(height > 0f) || !(g > 0f) ? 0f : (-speed + MathF.Sqrt(speed * speed + 2f * g * height)) / g;
}
