using System;
using Godot;
using SmClient.Extras.Weather;
using SmSim;

namespace SmClient;

/// <summary>
/// Weather repair 1 test aid (presentation only, client only, never in normal play): a human client that draws the weather rig can be moved without a person at the keys, so the tester can capture high-speed rain,
/// outdoor looks and the sky blend. It acts only in an F6 developer-tools session (the words come from <c>--dev-script</c> or the panel, which exist only while the server announces developer tools: a <c>--dev-tools</c>
/// server on loopback and a loopback client) and stops at once when that session lapses or the connection ends.
/// <list type="bullet">
/// <item><c>wxmove:right:4</c> (also <c>left</c>, <c>forward</c>, <c>back</c>): the player's own movement input held at full stick for the given seconds (default 4), as if the key were held: the sim's own run speed, nothing faster.</item>
/// <item><c>wxslide:20:3</c>: the camera alone slides sideways (to its right; a negative speed goes left) at the given m/s for the given seconds (default 3), then returns to the player: the rain rig, the sky and the shelter rays all see the
/// moving camera, so the rain's coverage at a jump-pack speed can be captured on a real map without the jump pack. The player does not move; the server sees nothing.</item>
/// <item>Rain motion (series 37): <c>wxslide:20:3:-15</c>: a third number adds a vertical speed (m/s, up positive) to the slide, so a fast rise or drop (a jump pack, a ledge) can be captured; <c>wxslide:0:2:-25</c> is a straight drop.</item>
/// <item>Rain motion (series 37): <c>wxfly</c> (or <c>wxfly:16</c>, the forward speed in m/s): the camera alone flies the path of a full jump-pack jump (spec "Jump pack": lift-off 26.8 m/s up under 20 m/s2 to the apex, the 0.5 s hang under
/// 12 m/s2, the fall under 30 m/s2 back to the start height; 16 m/s along the camera's heading throughout), about 2.7 s, then returns to the player. Camera only, like <c>wxslide</c>.</item>
/// <item><c>wxstop</c>: every move ends at once.</item>
/// <item>Repair 2: <c>wxlook:30</c> holds the camera's pitch at that many degrees (up positive, -80 to 80; the camera's own heading kept) so the sky can be framed; <c>wxlook:off</c> returns it to the player's aim. Camera only: the
/// player's aim, input and the server are untouched.</item>
/// <item>Repair 2: <c>wxpin:lull</c>, <c>wxpin:full</c>, <c>wxpin:peak</c> (or any phase name) and <c>wxpin:off</c>: the same client-only clock pin as <c>--weather-pin</c>, switched during a run (lull: the end of the recovery, the
/// lowest active level; full: the peak).</item>
/// </list>
/// To reach an open spot first use the existing teleports (<c>tp:point:1</c>, <c>tp:spawn-loyal</c>, <c>tp:spawn-chaos</c>).
/// </summary>
public partial class NetworkView
{
    float _wxMoveX, _wxMoveY;
    double _wxMoveUntil = double.NegativeInfinity, _wxSlideUntil = double.NegativeInfinity, _wxSlideLast = double.NaN;
    float _wxSlideSpeed, _wxSlideUp, _wxFlyForward;
    Vector3 _wxSlideOffset;
    bool _wxFly;
    double _wxFlyT;
    Vector3 _wxSlideBase, _wxSlideWrote;
    bool _wxSlideHas;
    float? _wxLookPitch;

    /// <summary>The aid is allowed now: a connected human client in an F6 developer-tools session.</summary>
    bool WeatherTestMoveGate => !_bot && _client is { Status: SmNet.ClientStatus.Connected, Dev: not null };

    static double RealSeconds => Time.GetTicksMsec() / 1000.0;

    /// <summary>A test-aid word from the F6 panel or <c>--dev-script</c> (through <see cref="WeatherClientLink.TestMove"/>); false when not one of the aid's words.</summary>
    bool WeatherTestMoveWord(string name, string arg)
    {
        if (name is not ("wxmove" or "wxslide" or "wxfly" or "wxstop" or "wxlook" or "wxpin" or "wxflash")) return false;
        if (!WeatherTestMoveGate) { GD.Print($"weather test move: '{name}:{arg}' ignored (no developer-tools session)"); return true; }
        var parts = arg.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        static double Num(string[] p, int i, double fallback) => i < p.Length && double.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : fallback;
        switch (name)
        {
            case "wxlook":
            {
                if (parts.Length > 0 && parts[0] == "off") { _wxLookPitch = null; GD.Print("weather test look: the camera follows the aim again"); return true; }
                _wxLookPitch = (float)Math.Clamp(Num(parts, 0, 30), -80, 80);
                GD.Print($"weather test look: camera pitch held at {_wxLookPitch:0.#} degrees (camera only)");
                return true;
            }
            case "wxflash":
            {
                string f = parts.Length > 0 ? parts[0] : "";
                _wxFlashOverride = f switch { "full" => true, "reduced" => false, _ => null };
                GD.Print($"weather test flash: {(f is "full" or "reduced" ? f : "the saved option")} for this run (not saved)");
                return true;
            }
            case "wxpin":
            {
                string w = parts.Length > 0 ? parts[0] : "";
                if (w == "off") { _wxModel.PinPhase = null; _wxModel.PinLull = false; _wxModel.PinStrike = false; GD.Print("weather test pin: off (the server's clock)"); return true; }
                if (WeatherClientModel.TryParsePinWord(w, out var phase, out bool lull)) { _wxModel.PinPhase = phase; _wxModel.PinLull = lull; _wxModel.PinStrike = w.Equals("strike", StringComparison.OrdinalIgnoreCase); GD.Print($"weather test pin: {w} (client only)"); }
                else GD.Print($"weather test pin: '{w}' is not lull, full, off or a phase name");
                return true;
            }
            case "wxstop":
                _wxMoveUntil = _wxSlideUntil = double.NegativeInfinity;
                GD.Print("weather test move: stopped");
                return true;
            case "wxmove":
            {
                (float x, float y) = (parts.Length > 0 ? parts[0] : "") switch { "right" => (1f, 0f), "left" => (-1f, 0f), "forward" => (0f, 1f), "back" => (0f, -1f), _ => (0f, 0f) };
                if (x == 0f && y == 0f) { GD.Print($"weather test move: '{arg}' is not right, left, forward or back"); return true; }
                double seconds = Math.Clamp(Num(parts, 1, 4), 0.1, 60);
                _wxMoveX = x; _wxMoveY = y; _wxMoveUntil = RealSeconds + seconds;
                GD.Print($"weather test move: holding {parts[0]} for {seconds:0.#} s");
                return true;
            }
            case "wxfly":
            {
                _wxFlyForward = (float)Math.Clamp(Num(parts, 0, JumpForwardSpeed), -60, 60);
                _wxFly = true; _wxFlyT = 0; _wxSlideOffset = Vector3.Zero; _wxSlideUntil = RealSeconds + JumpPathSeconds; _wxSlideLast = double.NaN; _wxSlideHas = false;
                GD.Print($"weather test move: camera flies a jump-pack path ({JumpPathSeconds:0.00} s, apex {JumpApex:0.0} m, {_wxFlyForward:0.#} m/s forward; camera only)");
                return true;
            }
            default:   // wxslide
            {
                float speed = (float)Math.Clamp(Num(parts, 0, 20), -60, 60);
                double seconds = Math.Clamp(Num(parts, 1, 3), 0.1, 30);
                float up = (float)Math.Clamp(Num(parts, 2, 0), -60, 60);
                _wxFly = false; _wxSlideSpeed = speed; _wxSlideUp = up; _wxSlideOffset = Vector3.Zero; _wxSlideUntil = RealSeconds + seconds; _wxSlideLast = double.NaN; _wxSlideHas = false;
                GD.Print($"weather test move: camera slides {speed:0.#} m/s sideways{(up != 0f ? $" and {up:0.#} m/s up" : "")} for {seconds:0.#} s (camera only; the player stays)");
                return true;
            }
        }
    }

    /// <summary>On every live input (the input hook): the held direction while the move aid runs and its gate holds.</summary>
    void WeatherTestInput(ref PlayerInput input)
    {
        if (RealSeconds >= _wxMoveUntil) return;
        if (!WeatherTestMoveGate) { _wxMoveUntil = double.NegativeInfinity; return; }
        input = input with { MoveX = _wxMoveX, MoveY = _wxMoveY };
    }

    /// <summary>Once a frame before the rig (the weather frame runs after the camera is placed): the slid camera, rebased on the camera's own place each frame.</summary>
    void WeatherTestSlide()
    {
        if (_camera is null) return;
        if (_wxLookPitch is { } pitch)
        {
            if (!WeatherTestMoveGate) _wxLookPitch = null;
            else
            {
                var r = _camera.GlobalRotation;   // the camera rig sets it anew each frame; this frame's heading is kept, only the pitch is held
                _camera.GlobalRotation = new Vector3(Mathf.DegToRad(pitch), r.Y, 0f);
            }
        }
        double now = RealSeconds;
        if (now >= _wxSlideUntil || !WeatherTestMoveGate)
        {
            if (_wxSlideHas && _camera.GlobalPosition.IsEqualApprox(_wxSlideWrote)) _camera.GlobalPosition = _wxSlideBase;   // a camera nobody moved since goes back to its own place
            _wxSlideHas = false; _wxSlideUntil = double.NegativeInfinity;
            return;
        }
        float dt = double.IsNaN(_wxSlideLast) ? 0f : (float)Math.Clamp(now - _wxSlideLast, 0, 0.25);
        _wxSlideLast = now;
        var right = _camera.GlobalBasis.X; right.Y = 0f;
        right = right.LengthSquared() > 1e-6f ? right.Normalized() : Vector3.Right;
        if (_wxFly)
        {
            _wxFlyT += dt;
            var fwd = new Vector3(right.Z, 0f, -right.X);   // the heading (the camera's right turned a quarter toward its front, -Z)
            var (along, upM) = JumpPath(_wxFlyT);
            _wxSlideOffset = fwd * (along * _wxFlyForward / JumpForwardSpeed) + Vector3.Up * upM;
        }
        else _wxSlideOffset += right * (_wxSlideSpeed * dt) + Vector3.Up * (_wxSlideUp * dt);
        var current = _camera.GlobalPosition;
        var basePos = _wxSlideHas && current.IsEqualApprox(_wxSlideWrote) ? _wxSlideBase : current;   // the camera rig placed it anew, or it stood still since our write
        _wxSlideBase = basePos;
        _wxSlideWrote = basePos + _wxSlideOffset;
        _camera.GlobalPosition = _wxSlideWrote;
        _wxSlideHas = true;
    }

    // ---- rain motion (series 37): the wxfly aid's jump-pack path (spec "Jump pack": lift-off, hang, fall; camera only) ----

    const float JumpLiftOff = 26.8f, JumpRiseG = 20f, JumpHangSeconds = 0.5f, JumpHangG = 12f, JumpFallG = 30f, JumpForwardSpeed = 16f;
    static float JumpRiseSeconds => JumpLiftOff / JumpRiseG;
    static float JumpApex => JumpLiftOff * JumpLiftOff / (2f * JumpRiseG);
    static float JumpHangDrop => 0.5f * JumpHangG * JumpHangSeconds * JumpHangSeconds;
    static float JumpFallSeconds
    {
        get { float v0 = JumpHangG * JumpHangSeconds, h = JumpApex - JumpHangDrop; return (-v0 + MathF.Sqrt(v0 * v0 + 2f * JumpFallG * h)) / JumpFallG; }
    }
    static float JumpPathSeconds => JumpRiseSeconds + JumpHangSeconds + JumpFallSeconds;

    /// <summary>The jump-pack path at <paramref name="t"/> s: metres along the heading (at <see cref="JumpForwardSpeed"/>) and metres up from the start (never below it).</summary>
    static (float Along, float Up) JumpPath(double t)
    {
        float s = (float)Math.Clamp(t, 0, JumpPathSeconds), up;
        if (s <= JumpRiseSeconds) up = JumpLiftOff * s - 0.5f * JumpRiseG * s * s;
        else if (s <= JumpRiseSeconds + JumpHangSeconds) { float h = s - JumpRiseSeconds; up = JumpApex - 0.5f * JumpHangG * h * h; }
        else { float f = s - JumpRiseSeconds - JumpHangSeconds, v0 = JumpHangG * JumpHangSeconds; up = JumpApex - JumpHangDrop - (v0 * f + 0.5f * JumpFallG * f * f); }
        return (JumpForwardSpeed * s, Math.Max(0f, up));
    }
}
