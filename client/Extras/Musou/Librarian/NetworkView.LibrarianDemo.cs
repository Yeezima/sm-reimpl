using System;
using System.Threading.Tasks;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Capture aid <c>--librarian-demo &lt;dir&gt;</c> (Musou Mode, a windowed client on a war server; test script): in the Stormbird it takes the Librarian through the Esc menu row's own path
/// (<see cref="PickLibrarian"/>) and shoots the armour and the smoke, goes to the Firing Range (the range's command), and when the Orks come within 22 m casts with the real buttons, aimed at the nearest:
/// chain lightning held 3 s, a grenade (his equipment), a Shockwave, then three sword swings (two more first in the empty Stormbird), shooting a frame sequence of each into <c>dir</c>. A <c>LIBDEMO</c> line per step; quits 0, or 1 when a step timed out.
/// </summary>
public partial class NetworkView
{
    static readonly bool LibrarianDemoOn = CmdArg("librarian-demo") is { Length: > 0 };
    bool _librarianDemoStarted;
    bool _librarianDemoAim;

    void LibrarianDemoFrame()
    {
        if (!LibrarianDemoOn || _librarianDemoStarted || _bot) return;
        _librarianDemoStarted = true;
        _tfwShots = CmdArg("librarian-demo");
        System.IO.Directory.CreateDirectory(_tfwShots!);
        _ = LibrarianDemoRun();
    }

    /// <summary>Keeps the view on the nearest Ork while a cast runs.</summary>
    void LibrarianDemoAimFrame()
    {
        if (!_librarianDemoAim || _client is null || _config is null) return;
        var me = _client.Predicted;
        if (_musouState.NearestTo(me.Position, 40f) is not { } ork) return;
        float dx = ork.X - me.Position.X, dy = ork.Y - me.Position.Y, dz = ork.Z + 1.0f - (me.Position.Z + _config.EyeHeight);
        _yaw = MathF.Atan2(-dx, dy);
        _pitch = Math.Clamp(MathF.Atan2(dz, MathF.Max(MathF.Sqrt(dx * dx + dy * dy), 0.1f)), -MaxPitchDown, MaxPitchUp);
    }

    async Task LibrarianDemoRun()
    {
        bool ok = true;
        try { ok = await LibrarianDemoScript(); }
        catch (Exception ex) { GD.Print($"LIBDEMO exception {ex}"); ok = false; }
        _tfwTestButtons = InputButtons.None;
        _librarianDemoAim = false;
        GD.Print($"LIBDEMO done ({(ok ? "ok" : "a step timed out")})");
        GetTree().Quit(ok ? 0 : 1);
    }

    async Task Series(string name, int count, double every)
    {
        for (int k = 0; k < count; k++) { await TfwShot($"{name}_{k:00}"); await TfwWait(every); }
    }

    async Task<bool> LibrarianDemoScript()
    {
        var client = _client!;
        if (!await TfwUntil("the war is on, in the Stormbird", () => client.Match.State == MatchState.Playing && _tfwState.War is { Phase: TfwRunPhase.Hub } && client.Predicted.Alive, 90.0)) return false;
        await TfwWait(2.0);
        await TfwShot("0_tactical_before");
        if (!PickLibrarian("--librarian-demo")) { GD.Print("LIBDEMO the Librarian is not offered"); return false; }
        await Series("1_class_change_smoke", 6, 0.3);
        if (!await TfwUntil("the own class is the Librarian", () => _config is not null && LibrarianRules.IsLibrarian(_config, client.Predicted.Class), 10.0)) return false;
        await TfwWait(2.5);   // the look builds
        await Series("2_armour", 3, 0.5);
        for (int k = 0; k < 2; k++)   // the force sword in the empty Stormbird, where nothing hides it
        {
            _tfwTestButtons = InputButtons.Melee; await TfwFrames(3); _tfwTestButtons = InputButtons.None;
            await Series($"2_force_sword_stormbird_{k}", 5, 0.07);
        }
        await TfwWait(1.0);

        _tfwState.Commands.Issue(TfwCommand.RangeGo);
        var range = TfwRangeArea!;
        if (!await TfwUntil("at the Firing Range", () => range.Contains(client.Predicted.Position), 10.0)) return false;
        GD.Print("LIBDEMO at the range; waiting for the Orks");
        if (!await TfwUntil("Orks within 22 m", () => _musouState.NearestTo(client.Predicted.Position, 22f) is not null, 60.0)) return false;
        _librarianDemoAim = true;
        await TfwFrames(3);

        GD.Print("LIBDEMO chain lightning");
        _tfwTestButtons = InputButtons.Fire;
        await TfwWait(0.2);
        await Series("3_lightning", 12, 0.15);
        _tfwTestButtons = InputButtons.None;
        await TfwWait(1.2);

        GD.Print("LIBDEMO grenade (his equipment; the Warp Pull was removed)");
        _tfwTestButtons = InputButtons.Grenade; await TfwFrames(4); _tfwTestButtons = InputButtons.None;
        await Series("4_grenade", 8, 0.1);
        await TfwWait(0.6);

        GD.Print("LIBDEMO shockwave");
        _tfwTestButtons = InputButtons.Reload; await TfwFrames(4); _tfwTestButtons = InputButtons.None;
        await Series("5_shockwave", 8, 0.12);
        await TfwWait(1.0);

        GD.Print("LIBDEMO force sword");
        for (int k = 0; k < 3; k++)
        {
            _tfwTestButtons = InputButtons.Melee; await TfwFrames(3); _tfwTestButtons = InputButtons.None;
            await Series($"6_force_sword_{k}", 4, 0.08);
        }
        _librarianDemoAim = false;
        await TfwWait(0.5);
        await Series("7_hud_after", 2, 0.5);
        return true;
    }
}
