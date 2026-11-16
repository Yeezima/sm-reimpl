using System;
using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

// ui/loading-blink (the owner, 2026-11-10): the blink between the menu, the loading screen and the match, driven frame by frame on a node outside the tree (nothing drawn).
public sealed partial class UiSelfTest
{
    void ScreenBlinkChecks()
    {
        const double frame = 1.0 / 60.0;
        var blink = new ScreenBlink();
        try
        {
            bool changed = false;
            Check("blink: clear at rest", !blink.Busy && blink.Alpha == 0f);
            blink.Run(() => changed = true);
            int closeFrames = 0;
            float last = 0f;
            bool rising = true;
            while (blink.Alpha < 1f && closeFrames < 200) { blink._Process(frame); closeFrames++; rising &= blink.Alpha >= last; last = blink.Alpha; if (changed) break; }
            Check("blink: closes to black before the change", !changed && blink.Alpha == 1f && rising, $"frames {closeFrames}, alpha {blink.Alpha}, changed {changed}");
            Check("blink: the close takes about its time", Math.Abs(closeFrames * frame - ScreenBlink.CloseSeconds) <= 2 * frame, $"{closeFrames} frames");
            for (int i = 1; i < ScreenBlink.BlackFramesBefore; i++) blink._Process(frame);
            Check("blink: black frames are drawn before the change", !changed);
            blink._Process(frame);
            Check("blink: the change runs at full black", changed && blink.Alpha == 1f);
            // A long frame (the change's load) does not end the hold or skip the open.
            blink._Process(3.0);
            Check("blink: a long frame keeps it black", blink.Alpha == 1f);
            bool ready = false;
            blink.Run(() => { }, () => ready);   // a second change while black (the connect during the menu's blink): runs now, the hold waits for it
            for (int i = 0; i < 20; i++) blink._Process(frame);
            Check("blink: the hold waits for the readiness", blink.Alpha == 1f && blink.Busy);
            ready = true;
            int openFrames = 0;
            last = 1f;
            bool falling = true;
            while (blink.Busy && openFrames < 200) { blink._Process(frame); openFrames++; falling &= blink.Alpha <= last; last = blink.Alpha; }
            Check("blink: opens to clear once ready", !blink.Busy && blink.Alpha == 0f && falling, $"frames {openFrames}");
            // The hold had its time and frames already: one frame to see the readiness, then the open.
            Check("blink: the open takes about its time", Math.Abs((openFrames - 1) * frame - ScreenBlink.OpenSeconds) <= 2 * frame, $"{openFrames} frames, expected {ScreenBlink.OpenSeconds / frame + 1:F0}");
            // Asked again while opening: closes from the same darkness.
            blink.Run(() => { });
            while (blink.Alpha < 1f) blink._Process(frame);
            for (int i = 0; i < 12; i++) blink._Process(frame);
            for (int i = 0; i < 6; i++) blink._Process(frame);
            float before = blink.Alpha;
            blink.Run(() => { });
            Check("blink: a blink asked while opening turns back without a jump", blink.Busy && Math.Abs(blink.Alpha - before) < 0.02f, $"{before} -> {blink.Alpha}");
            // A change that throws does not stop the blink.
            int guard = 0;
            while (blink.Busy && guard++ < 400) blink._Process(frame);
            blink.Run(() => throw new InvalidOperationException("self-test: a failing change"));
            guard = 0;
            while (blink.Busy && guard++ < 400) blink._Process(frame);
            Check("blink: a failing change still opens", !blink.Busy && blink.Alpha == 0f);
            Check("blink: the switch's name", ScreenBlink.OffSwitch == "no-screen-blink");
        }
        finally { blink.Free(); }
        Check("loading: the picture constant names its file", UiKit.LoadingBackgroundPath == "res://assets/menu/loading-background.jpg");
        string dev = ProjectSettings.GlobalizePath(UiKit.LoadingBackgroundPath);
        Check("loading: the picture is beside the project", System.IO.File.Exists(dev) || !System.IO.File.Exists(ProjectSettings.GlobalizePath(UiKit.MenuBackgroundPath)), dev);
    }
}
