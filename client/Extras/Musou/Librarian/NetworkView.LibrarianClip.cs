using System;
using System.Threading.Tasks;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Capture aid <c>--librarian-clip &lt;dir&gt;</c> (Musou Mode, a windowed client on a war server, usually under Movie Maker: <c>godot.cmd --windowed --write-movie x.avi --fixed-fps 30 -- ...</c>; test script,
/// librarian-lightning 2026-11-10): takes the Librarian in the Stormbird, goes to the Firing Range, waits for a crowd within 22 m, then with the real buttons, aimed at the nearest Ork: a short tap of the
/// lightning, a 3 s hold, a Shockwave, a second 2 s hold; stills into <c>dir</c> at set moments, a <c>LIBCLIP</c> line per step; quits 0, or 1 when a step timed out.
/// <c>--librarian-cam side</c>: the camera stands 5 m off the Librarian's right side, a little behind, framing him and the crowd ahead (the third-person view of the cast pose); <c>player</c> (default): the
/// follow camera; <c>front</c>: 4.2 m ahead of him, 3 m to his right and a little below his chest, looking back at it (the palms and the bolts coming at the camera).
/// </summary>
public partial class NetworkView
{
    static readonly string? LibrarianClipDir = CmdArg("librarian-clip");
    static readonly string? LibrarianCam = CmdArg("librarian-cam");
    /// <summary><c>--librarian-clip-no-stills</c>: no stills (a still costs about 130 ms of frame time, which a latency measurement must not carry).</summary>
    static readonly bool LibrarianClipNoStills = OS.GetCmdlineUserArgs().Contains("--librarian-clip-no-stills");

    Task ClipShot(string name) => LibrarianClipNoStills ? Task.CompletedTask : TfwShot(name);
    bool _librarianClipStarted;

    void LibrarianClipFrame()
    {
        if (LibrarianClipDir is not { Length: > 0 } dir || _librarianClipStarted || _bot) return;
        _librarianClipStarted = true;
        _tfwShots = dir;
        System.IO.Directory.CreateDirectory(dir);
        _ = LibrarianClipRun();
    }

    /// <summary>The side camera (after the follow camera and the shake are placed this frame): 5 m off his right, 1.5 m behind, 0.6 m above his chest, looking at a point 3 m ahead of him.</summary>
    void LibrarianClipCamFrame()
    {
        if (LibrarianCam is not ("side" or "front") || LibrarianClipDir is null || _camera is null || _gameSpace is null || _client is null || _local is null) return;
        var me = _client.Predicted;
        float yaw = me.Yaw;
        var forward = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        var right = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var chest = new Vector3(me.Position.X, me.Position.Y, me.Position.Z + 1.55f);
        bool front = LibrarianCam == "front";
        var eye = front ? chest + forward * 4.2f + right * 3.0f - new Vector3(0f, 0f, 0.55f) : chest + right * 5f - forward * 1.5f + new Vector3(0f, 0f, 0.6f);
        var look = front ? chest + forward * 0.4f : chest + forward * 3f;
        var eyeG = _gameSpace.ToGlobal(eye); var lookG = _gameSpace.ToGlobal(look);
        _camera.GlobalTransform = new Transform3D(Basis.LookingAt(lookG - eyeG, Vector3.Up), eyeG);
    }

    async Task LibrarianClipRun()
    {
        bool ok = true;
        try { ok = await LibrarianClipScript(); }
        catch (Exception ex) { GD.Print($"LIBCLIP exception {ex}"); ok = false; }
        _tfwTestButtons = InputButtons.None;
        _librarianDemoAim = false;
        GD.Print($"LIBCLIP done ({(ok ? "ok" : "a step timed out")}); foreseen frames {_librarianForeseenFrames}, cast commands sent {_librarianCastsSent}");
        GetTree().Quit(ok ? 0 : 1);
    }

    async Task Hold(InputButtons button, double seconds, (double At, string Name)[] shots)
    {
        _tfwTestButtons = button;
        double start = Time.GetTicksMsec() / 1000.0;
        int next = 0;
        while (Time.GetTicksMsec() / 1000.0 - start < seconds)
        {
            if (next < shots.Length && Time.GetTicksMsec() / 1000.0 - start >= shots[next].At) await ClipShot(shots[next++].Name);
            await TfwFrames(1);
        }
        _tfwTestButtons = InputButtons.None;
    }

    async Task<bool> LibrarianClipScript()
    {
        var client = _client!;
        if (!await TfwUntil("the war is on, in the Stormbird", () => client.Match.State == MatchState.Playing && _tfwState.War is { Phase: TfwRunPhase.Hub } && client.Predicted.Alive, 90.0)) return false;
        await TfwWait(1.5);
        if (!PickLibrarian("--librarian-clip")) { GD.Print("LIBCLIP the Librarian is not offered"); return false; }
        if (!await TfwUntil("the own class is the Librarian", () => _config is not null && LibrarianRules.IsLibrarian(_config, client.Predicted.Class), 10.0)) return false;
        await TfwWait(2.5);   // the look builds
        _tfwState.Commands.Issue(TfwCommand.RangeGo);
        var range = TfwRangeArea!;
        if (!await TfwUntil("at the Firing Range", () => range.Contains(client.Predicted.Position), 10.0)) return false;
        GD.Print("LIBCLIP at the range; waiting for the Orks");
        if (!await TfwUntil("Orks within 22 m", () => _musouState.NearestTo(client.Predicted.Position, 22f) is not null, 60.0)) return false;
        await TfwWait(1.0);   // more of the crowd closes in
        _librarianDemoAim = true;
        await TfwFrames(3);

        GD.Print($"LIBCLIP tap (wall {Time.GetTicksMsec() / 1000.0:F2} s, frame {Engine.GetProcessFrames()})");
        await Hold(InputButtons.Fire, 0.25, [(0.0, "1_tap_00"), (0.1, "1_tap_01")]);
        await TfwWait(0.9);

        GD.Print($"LIBCLIP hold 3 s (wall {Time.GetTicksMsec() / 1000.0:F2} s, frame {Engine.GetProcessFrames()})");
        await Hold(InputButtons.Fire, 3.0, [(0.03, "2_hold_00"), (0.15, "2_hold_01"), (0.6, "2_hold_02"), (1.4, "2_hold_03"), (2.4, "2_hold_04")]);
        await ClipShot("2_release_00");
        await TfwWait(0.15);
        await ClipShot("2_release_01");
        await TfwWait(1.0);

        GD.Print($"LIBCLIP shockwave (wall {Time.GetTicksMsec() / 1000.0:F2} s, frame {Engine.GetProcessFrames()})");
        _tfwTestButtons = InputButtons.Reload; await TfwFrames(3); _tfwTestButtons = InputButtons.None;
        await TfwWait(0.08); await ClipShot("3_shockwave_00");
        await TfwWait(0.15); await ClipShot("3_shockwave_01");
        await TfwWait(0.2); await ClipShot("3_shockwave_02");
        await TfwWait(1.5);

        GD.Print($"LIBCLIP hold 2 s (wall {Time.GetTicksMsec() / 1000.0:F2} s, frame {Engine.GetProcessFrames()})");
        if (_musouState.NearestTo(client.Predicted.Position, 30f) is null) await TfwUntil("Orks back within 30 m", () => _musouState.NearestTo(client.Predicted.Position, 30f) is not null, 15.0);
        await Hold(InputButtons.Fire, 2.0, [(0.5, "4_hold_00"), (1.5, "4_hold_01")]);
        await TfwWait(1.0);
        _librarianDemoAim = false;
        GD.Print($"LIBCLIP end (wall {Time.GetTicksMsec() / 1000.0:F2} s, frame {Engine.GetProcessFrames()})");
        return true;
    }
}
