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
/// <item><c>wxstop</c>: both end at once.</item>
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
    float _wxSlideSpeed, _wxSlideOffset;
    Vector3 _wxSlideBase, _wxSlideWrote;
    bool _wxSlideHas;
    float? _wxLookPitch;

    /// <summary>The aid is allowed now: a connected human client in an F6 developer-tools session.</summary>
    bool WeatherTestMoveGate => !_bot && _client is { Status: SmNet.ClientStatus.Connected, Dev: not null };

    static double RealSeconds => Time.GetTicksMsec() / 1000.0;

    /// <summary>A test-aid word from the F6 panel or <c>--dev-script</c> (through <see cref="WeatherClientLink.TestMove"/>); false when not one of the aid's words.</summary>
    bool WeatherTestMoveWord(string name, string arg)
    {
        if (name is not ("wxmove" or "wxslide" or "wxstop" or "wxlook" or "wxpin")) return false;
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
            case "wxpin":
            {
                string w = parts.Length > 0 ? parts[0] : "";
                if (w == "off") { _wxModel.PinPhase = null; _wxModel.PinLull = false; GD.Print("weather test pin: off (the server's clock)"); return true; }
                if (WeatherClientModel.TryParsePinWord(w, out var phase, out bool lull)) { _wxModel.PinPhase = phase; _wxModel.PinLull = lull; GD.Print($"weather test pin: {w} (client only)"); }
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
            default:   // wxslide
            {
                float speed = (float)Math.Clamp(Num(parts, 0, 20), -60, 60);
                double seconds = Math.Clamp(Num(parts, 1, 3), 0.1, 30);
                _wxSlideSpeed = speed; _wxSlideOffset = 0f; _wxSlideUntil = RealSeconds + seconds; _wxSlideLast = double.NaN; _wxSlideHas = false;
                GD.Print($"weather test move: camera slides {speed:0.#} m/s sideways for {seconds:0.#} s (camera only; the player stays)");
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
        _wxSlideOffset += _wxSlideSpeed * dt;
        var current = _camera.GlobalPosition;
        var basePos = _wxSlideHas && current.IsEqualApprox(_wxSlideWrote) ? _wxSlideBase : current;   // the camera rig placed it anew, or it stood still since our write
        var right = _camera.GlobalBasis.X; right.Y = 0f;
        right = right.LengthSquared() > 1e-6f ? right.Normalized() : Vector3.Right;
        _wxSlideBase = basePos;
        _wxSlideWrote = basePos + right * _wxSlideOffset;
        _camera.GlobalPosition = _wxSlideWrote;
        _wxSlideHas = true;
    }
}
