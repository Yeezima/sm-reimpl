using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The test aid of Musou Mode pass 2's M7 and M11 (client only; docs/commands.md): <c>--tfw-test-class [dir]</c>, a scripted player on a real server with <c>--profile &lt;file&gt;</c> (two custom
/// classes: slot 1 the one it joins as, slot 2 another class with other armour colours). In the hub it walks to Equipment, opens the loadout screen with E and presses DEPLOY for slot 2 (the button's
/// own code path), then checks and photographs the class change: the player stays alive, the class changes at once, the smoke starts and covers the model, and ends. Then it deploys, checks
/// that the retainer wears the player's colours (the scoreboard's appearance version and the look the client holds), photographs both, and walks to the first position, where the elites' bars
/// show over the defenders, firing its pistol at them. One <c>TFWCLASS PASS</c> or <c>FAIL</c> line per step; quits with 0, or 1 when any failed. In a windowed run <c>dir</c> gets the PNGs.
/// </summary>
public partial class NetworkView
{
    static readonly bool TfwClassTestOn = CmdFlag("tfw-test-class");
    bool _tfwClassTestStarted;
    int _tfwClassPassed, _tfwClassFailed;

    void TfwClassTestFrame()
    {
        if (!TfwClassTestOn || _tfwClassTestStarted || _bot) return;
        _tfwClassTestStarted = true;
        _tfwShots = CmdArg("tfw-test-class");
        if (_tfwShots is { Length: > 0 } dir) Directory.CreateDirectory(dir); else _tfwShots = null;
        _ = TfwClassTestRun();
    }

    async Task TfwClassTestRun()
    {
        try { await TfwClassTestScript(); }
        catch (Exception ex) { TfwClassCheck("exception", false, ex.ToString()); }
        foreach (var key in new[] { Key.W, Key.P, Key.E }) TfwKey(key, false);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        GD.Print($"TFWCLASS done: {_tfwClassPassed} passed, {_tfwClassFailed} failed");
        GetTree().Quit(_tfwClassFailed == 0 ? 0 : 1);
    }

    void TfwClassCheck(string name, bool ok, string detail = "")
    {
        if (ok) _tfwClassPassed++; else _tfwClassFailed++;
        GD.Print($"TFWCLASS {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " - " + detail : "")}");
    }

    int TfwDeathsOf(int player) => _client?.Scoreboard?.Players.Where(r => r.PlayerId == player).Select(r => (int?)r.Deaths).FirstOrDefault() ?? -1;

    /// <summary>The nearest elite the crowd shows (an 'Ard Boy, a Nob or the Warboss), and its flat distance from the player.</summary>
    void TfwNearestElite(out MusouShown? nearest, out float distance)
    {
        nearest = null;
        distance = float.MaxValue;
        var me = _client!.Predicted.Position;
        foreach (var e in _tfwShown)
        {
            if (!TfwBars.IsElite(e.Type) || e.State == MusouEnemyState.Dying) continue;
            float d = Num.Vector2.Distance(new(me.X, me.Y), new(e.Position.X, e.Position.Y));
            if (d < distance) { distance = d; nearest = e; }
        }
    }

    async Task TfwClassTestScript()
    {
        var client = _client!;
        if (!await TfwUntil("the war is on, in the hub", () => client.Match.State == MatchState.Playing && _tfwState.War is { Phase: TfwRunPhase.Hub } && client.Predicted.Alive, 60.0)) { _tfwClassFailed++; return; }
        int me = client.PlayerId, startClass = client.Predicted.Class;
        await TfwWait(2.0);
        await TfwShot("class-1-before");

        // Equipment, then DEPLOY for slot 2.
        if (!await TfwWalkTo(TfwHub!.EquipmentStation, "walked to Equipment")) { _tfwClassFailed++; return; }
        await TfwTap(Key.E);
        if (!await TfwUntil("E at Equipment opens the loadout screen", () => _pickerCreator is { Visible: true } && _tfwEquipmentShown, 90.0)) { _tfwClassFailed++; return; }
        await TfwShot("class-2-picker");
        int deathsBefore = TfwDeathsOf(me), smokesBefore = TfwSmokesStarted;
        DeployFromPicker(SmContent.Customization.Selection.CustomSlot(2));   // the DEPLOY button's own path
        TfwClassCheck("DEPLOY closed the picker", !PickerBusy);
        bool smoking = false;
        double started = -1, classChanged = -1;
        bool alwaysAlive = true;
        var shots = new (double At, string Name)[] { (0.12, "class-3-smoke-rising"), (0.45, "class-4-smoke-opaque"), (0.9, "class-5-smoke-fading"), (1.5, "class-6-smoke-thin"), (2.6, "class-7-after") };
        int next = 0;
        double limit = _connectedFor + 20.0;
        while (_connectedFor < limit && (next < shots.Length || !smoking))
        {
            await TfwFrames();
            alwaysAlive &= client.Predicted.Alive;
            if (!smoking && TfwSmokesStarted > smokesBefore) { smoking = true; started = _connectedFor; GD.Print($"TFWCLASS note: smoke started, class {client.Predicted.Class}"); }
            if (classChanged < 0 && client.Predicted.Class != startClass) { classChanged = _connectedFor; GD.Print($"TFWCLASS note: class is {client.Predicted.Class} at {(started < 0 ? "before the smoke" : (_connectedFor - started).ToString("F2") + " s into the smoke")}"); }
            if (smoking && next < shots.Length && _connectedFor - started >= shots[next].At) { await TfwShot(shots[next].Name); next++; }
        }
        TfwClassCheck("the smoke started", smoking);
        TfwClassCheck("the class changed on the spot", classChanged >= 0 && client.Predicted.Class != startClass, $"{startClass} -> {client.Predicted.Class}");
        TfwClassCheck("the player never fell and stayed in the hub", alwaysAlive && TfwNow.Phase == TfwRunPhase.Hub && TfwHub.Contains(client.Predicted.Position), client.Predicted.Position.ToString());
        TfwClassCheck("the class change was no death", TfwDeathsOf(me) == deathsBefore, $"{deathsBefore} -> {TfwDeathsOf(me)}");
        TfwClassCheck("one cloud, not several", TfwSmokesStarted == smokesBefore + 1, $"{TfwSmokesStarted - smokesBefore}");
        TfwClassCheck("the smoke started with or before the new class (it covers the swap)", started >= 0 && classChanged >= started - 0.25, $"smoke {started:F2} class {classChanged:F2}");
        await TfwWait(0.5);

        // Item 1: into a battle, the retainer wears the player's colours.
        if (!await TfwTestDeploy(TfwLane.B)) { _tfwClassFailed++; return; }
        await TfwWait(1.0);
        // At the landing the retainer stands 2 m behind the player: the camera turned to it ends up past it, so it fills the picture with the player beyond.
        if (client.Scoreboard?.Players.FirstOrDefault(r => TfwContracts.IsPuppet(r.PlayerId) && r.Name == "Retainer") is { } landed
            && client.RemotePlayers().Players.FirstOrDefault(p => p.PlayerId == landed.PlayerId) is { } landedAt)
        {
            var mine = client.Predicted.Position;
            _yaw = MathF.Atan2(-(landedAt.Position.X - mine.X), landedAt.Position.Y - mine.Y);
            await TfwWait(0.8);
            await TfwShot("retainer-0-landing");
        }
        await TfwWait(1.0);
        var board = client.Scoreboard;
        var own = board?.Players.FirstOrDefault(r => r.PlayerId == me);
        var retainer = board?.Players.Where(r => TfwContracts.IsPuppet(r.PlayerId) && r.Name == "Retainer").Cast<ScoreEntry?>().FirstOrDefault();
        TfwClassCheck("a retainer stands beside the player", retainer is not null, string.Join(",", board?.Players.Select(r => r.PlayerId + ":" + r.Name) ?? []));
        if (own is { } o && retainer is { } r)
        {
            TfwClassCheck("the player's look has a version", o.AppearanceVersion != 0, o.AppearanceVersion.ToString());
            TfwClassCheck("the retainer reports the same version", r.AppearanceVersion == o.AppearanceVersion, $"{r.AppearanceVersion} vs {o.AppearanceVersion}");
            var seen = client.AppearanceOf(r.PlayerId);
            TfwClassCheck("the client holds the retainer's look", seen is not null);
            TfwClassCheck("it is the player's own look", seen is not null && seen.Code == _ownLooks.Active(Team.SpaceMarines), $"{seen?.Code} vs {_ownLooks.Active(Team.SpaceMarines)}");
        }
        // What each is drawn with: the class key, the torso's primary paint and the model's look key against the wanted ones.
        if (own is not null && retainer is { } rtn)
        {
            string Describe(int id, CharacterViewer? c)
            {
                if (!_playerLooks.TryGetValue(id, out var pl)) return "no look cached";
                string key = c is not null && c.HasMeta("look_key") ? c.GetMeta("look_key").AsString() : "?";
                string drawn = key == pl.Ready.Key ? "the wanted look" : key == pl.Waiting.Key ? "the waiting look" : "another look: " + key;
                return $"{pl.Entry.Look.ClassKey}, torso {(pl.Entry.Look.Paint.TryGetValue("torso", out var tp) ? tp.Primary?.Name : null)}, head {pl.Entry.Look.Piece("head")?.Name}, drawn as {drawn}";
            }
            GD.Print($"TFWCLASS note: own  {Describe(me, _local)}");
            GD.Print($"TFWCLASS note: ret. {Describe(rtn.PlayerId, _remotes.GetValueOrDefault(rtn.PlayerId))}");
        }

        // Away from the landing zone's crowd, the camera turns to the retainer (it follows a few metres behind), so one picture holds both armours.
        double walkUntil = _connectedFor + 9.0;
        await TfwPathTo(_tfwState.Objectives[0].Position, "walked a little way towards the first position", 30.0, over: () => _connectedFor > walkUntil || !client.Predicted.Alive);
        await TfwWait(1.2);
        if (retainer is { } rt && client.RemotePlayers().Players.FirstOrDefault(p => p.PlayerId == rt.PlayerId) is { } rp)
        {
            var mine = client.Predicted.Position;
            _yaw = MathF.Atan2(-(rp.Position.X - mine.X), rp.Position.Y - mine.Y);
            GD.Print($"TFWCLASS note: the retainer stands {Num.Vector2.Distance(new(mine.X, mine.Y), new(rp.Position.X, rp.Position.Y)):F1} m away");
        }
        await TfwWait(1.0);
        await TfwShot("retainer-1-player-and-retainer");

        // Item 14: the nearest elite is walked to until 20 m away, then faced: its bar is over its head.
        TfwNearestElite(out var elite, out float away);
        TfwClassCheck("an elite is known (bars named)", elite is not null && _tfwState.Bars.Count > 0, $"{_tfwState.Bars.Count} named, {away:F0} m to the nearest");
        if (elite is { } target)
        {
            await TfwPathTo(target.Position, "walked towards the nearest elite", 90.0, over: () => { TfwNearestElite(out _, out float d); return d < 20f || !client.Predicted.Alive; });
        }
        TfwNearestElite(out elite, out away);
        if (elite is { } near)
        {
            var mine = client.Predicted.Position;
            _yaw = MathF.Atan2(-(near.Position.X - mine.X), near.Position.Y - mine.Y);
        }
        await TfwWait(0.6);
        TfwClassCheck("elites are in view with bars over them", TfwBarsDrawn >= 1 && away < 40f, $"{TfwBarsDrawn} bars, {_tfwState.Bars.Count} named, {away:F0} m to the nearest");
        await TfwShot("bars-1-first-sight");
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        double fireUntil = _connectedFor + 12.0;
        while (_connectedFor < fireUntil && client.Predicted.Alive && !_tfwState.Bars.Any(b => b.Health < 255)) await TfwFrames();
        await TfwWait(0.6);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        await TfwShot("bars-2-under-fire");
        TfwClassCheck("a bar was hurt", _tfwState.Bars.Any(b => b.Health < 255), string.Join(",", _tfwState.Bars.Select(b => b.Enemy + ":" + b.Health)));
        await TfwWait(1.5);
        await TfwShot("bars-3-later");
    }
}
