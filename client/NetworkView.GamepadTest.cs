using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--pad-test</c> (test aid, ours): a human client (headless, no physical controller) injects synthetic joypad events with <c>Input.ParseInputEvent</c> and prints <c>PADTEST</c> lines
/// of what the simulation did with them: the scheme switch, look (yaw), move (position), fire (clip), melee (attack step), charge (stick forward + Y: the rush phase), aim, jump, reload
/// and the weapon swap, then the mouse taking the scheme back; <c>--pad-test-done</c> quits at the end. The server (bound to 127.0.0.1) sees the same inputs: the corrections
/// (predicted against authoritative position) are printed too, so a input the server did not get shows as a snap.
/// </summary>
public partial class NetworkView
{
    static readonly bool PadTestOn = OS.GetCmdlineUserArgs().Contains("--pad-test");
    readonly List<(double At, string Name, Action Do)> _padOps = new();
    double _padStart = -1;
    int _padNext;
    int _padMaxStep, _padMinClip = int.MaxValue; bool _padSawAim, _padSawAir; RushPhase _padRush = RushPhase.None; WeaponSlot? _padSlot;
    System.Numerics.Vector3 _padPos; float _padYaw;
    int _padCorrections0, _padClip0;

    static void PadAxis(JoyAxis axis, float value) => Input.ParseInputEvent(new InputEventJoypadMotion { Device = 0, Axis = axis, AxisValue = value });
    static void PadButton(JoyButton button, bool pressed) => Input.ParseInputEvent(new InputEventJoypadButton { Device = 0, ButtonIndex = button, Pressed = pressed, Pressure = pressed ? 1f : 0f });

    void PadTestFrame()
    {
        if (!PadTestOn || _client is null || _client.Status != SmNet.ClientStatus.Connected) return;
        var me = _client.Predicted;
        if (_padStart < 0)
        {
            _padStart = _connectedFor;
            double t = 1.0;
            void At(double dt, string name, Action a) { t += dt; _padOps.Add((t, name, a)); }
            Say("start");
            At(0, "keyboard scheme before any pad input", () => Log("scheme before: " + (PadHost.Active ? "Gamepad" : "KeyboardMouse")));
            At(0.2, "look right: RS x 0.9 for 1 s", () => { _padYaw = _client.Predicted.Yaw; PadAxis(JoyAxis.RightX, 0.9f); });
            At(1.0, "look stop", () => { PadAxis(JoyAxis.RightX, 0f); Log($"look: scheme {(PadHost.Active ? "Gamepad" : "KeyboardMouse")}; view turned {Mathf.RadToDeg(Wrap(_yaw - _padYaw)):F1} degrees for 1 s of RS x 0.9 (right turn is negative)"); });
            At(0.3, "move forward: LS y -1 for 1 s", () => { _padPos = _client.Predicted.Position; PadAxis(JoyAxis.LeftY, -1f); });
            At(1.0, "move stop", () => { PadAxis(JoyAxis.LeftY, 0f); Log($"move: moved {(_client.Predicted.Position - _padPos).Length():F2} m in 1 s (run speed {_config!.RunSpeed:F1} m/s)"); });
            At(0.3, "half stick", () => { _padPos = _client.Predicted.Position; PadAxis(JoyAxis.LeftY, -0.6f); });
            At(1.0, "half stick stop", () => { PadAxis(JoyAxis.LeftY, 0f); Log($"move at stick 0.6: moved {(_client.Predicted.Position - _padPos).Length():F2} m in 1 s (analog magnitude kept)"); });
            At(0.5, "aim: LT", () => { PadAxis(JoyAxis.TriggerLeft, 1f); });
            At(0.6, "aim release", () => { Log($"aim: aim mode seen {_padSawAim}"); PadAxis(JoyAxis.TriggerLeft, 0f); });
            At(0.4, "fire: RT 0.5 s", () => { _padClip0 = _client.Predicted.Clip; _padMinClip = _padClip0; PadAxis(JoyAxis.TriggerRight, 1f); });
            At(0.5, "fire release", () => { PadAxis(JoyAxis.TriggerRight, 0f); Log($"fire: clip {_padClip0} -> lowest {_padMinClip} after RT held 0.5 s"); });
            At(0.5, "melee: X tap", () => { _padMaxStep = 0; PadButton(JoyButton.X, true); PadButton(JoyButton.X, false); });
            At(1.0, "melee result", () => Log($"melee: attack step reached {_padMaxStep} after one X tap"));
            At(0.5, "charge: LS forward then Y", () => { _padRush = RushPhase.None; PadAxis(JoyAxis.LeftY, -1f); });
            At(0.4, "Y press", () => PadButton(JoyButton.Y, true));
            At(0.15, "Y release", () => PadButton(JoyButton.Y, false));
            At(1.2, "charge result", () => { PadAxis(JoyAxis.LeftY, 0f); Log($"charge: rush phase seen {_padRush} with the stick forward at Y"); });
            At(0.8, "heavy without stick", () => { _padRush = RushPhase.None; _padMaxStep = 0; PadButton(JoyButton.Y, true); PadButton(JoyButton.Y, false); });
            At(1.2, "heavy result", () => Log($"stun without the stick: rush phase {_padRush}, attack step {_padMaxStep} (no charge expected)"));
            At(0.3, "jump: A", () => { _padSawAir = false; PadButton(JoyButton.A, true); });
            At(0.4, "jump release", () => { PadButton(JoyButton.A, false); });
            At(0.8, "jump result", () => Log($"evade / jump pack: left the ground {_padSawAir} (a class without a pack rolls instead)"));
            At(0.3, "swap: D-pad left (pistol)", () => { _padSlot = null; PadButton(JoyButton.DpadLeft, true); PadButton(JoyButton.DpadLeft, false); });
            At(0.5, "swap result", () => Log($"weapon swap: slot in hand {_client.Predicted.Slot} (D-pad left asks for the pistol)"));
            At(0.1, "swap back: D-pad up (primary)", () => { PadButton(JoyButton.DpadUp, true); PadButton(JoyButton.DpadUp, false); });
            At(0.5, "swap back result", () => Log($"weapon swap: slot in hand {_client.Predicted.Slot} (D-pad up asks for the primary)"));
            At(0.2, "mouse takes the scheme back", () => Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(20, 0), Position = new Vector2(20, 0) }));
            At(0.2, "scheme after mouse", () => Log("scheme after mouse movement: " + (PadHost.Active ? "Gamepad" : "KeyboardMouse")));
            At(0.2, "pad again", () => PadButton(JoyButton.B, true));
            At(0.1, "pad again B up", () => PadButton(JoyButton.B, false));
            At(0.2, "scheme after pad button", () => { Log("scheme after a pad button: " + (PadHost.Active ? "Gamepad" : "KeyboardMouse")); _padCorrections0 = 0; });
            At(0.5, "end", () =>
            {
                var errors = _client.CorrectionErrors;
                Log($"server agreement: {errors.Count} correction samples, largest predicted-to-authoritative position error {(errors.Count > 0 ? errors.Max() : 0):F3} m");
                if (OS.GetCmdlineUserArgs().Contains("--pad-test-done")) GetTree().Quit();
            });
        }
        // Observations every frame.
        _padMaxStep = Math.Max(_padMaxStep, me.MeleeStep);
        _padSawAim |= me.Aiming;
        _padSawAir |= !me.Grounded;
        if (me.Rush != RushPhase.None) _padRush = me.Rush;
        _padMinClip = Math.Min(_padMinClip, me.Clip);
        double now = _connectedFor - _padStart;
        while (_padNext < _padOps.Count && now >= _padOps[_padNext].At)
        {
            var op = _padOps[_padNext++];
            op.Do();
        }
    }

    static float Wrap(float a) { while (a > MathF.PI) a -= 2 * MathF.PI; while (a < -MathF.PI) a += 2 * MathF.PI; return a; }
    void Say(string s) => GD.Print("PADTEST " + s);
    void Log(string s) => GD.Print($"PADTEST t {_connectedFor:F1} s: {s}; clip {_client!.Predicted.Clip}");
}
