using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmNet;
using SmSim;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid <c>--tfw-test-range [dir]</c> (client only; docs/commands.md): a scripted player on a real war server walks to the Firing Range's button in the Stormbird, presses E (the confirm opens),
/// presses TELEPORT, and checks that it stands at the range, that about 200 Orks stay alive and come at it, that an orbital strike (B) there kills some, and that the return pad brings it back. One
/// <c>TFWRANGE PASS</c> or <c>FAIL</c> line per step, a <c>TFWRANGE note</c> per second with the Orks known and the nearest; quits with 0, or 1 when any failed. In a windowed run <c>dir</c> gets the PNGs.
/// </summary>
public partial class NetworkView
{
    static readonly bool TfwRangeTestOn = CmdFlag("tfw-test-range");
    bool _tfwRangeTestStarted;
    int _tfwRangePassed, _tfwRangeFailed;

    void TfwRangeTestFrame()
    {
        if (!TfwRangeTestOn || _tfwRangeTestStarted || _bot) return;
        _tfwRangeTestStarted = true;
        _tfwShots = CmdArg("tfw-test-range");
        if (_tfwShots is { Length: > 0 } dir) System.IO.Directory.CreateDirectory(dir); else _tfwShots = null;
        _ = TfwRangeTestRun();
    }

    async Task TfwRangeTestRun()
    {
        try { await TfwRangeTestScript(); }
        catch (Exception ex) { TfwRangeCheck("exception", false, ex.ToString()); }
        foreach (var key in new[] { Key.W, Key.B, Key.E }) TfwKey(key, false);
        GD.Print($"TFWRANGE done: {_tfwRangePassed} passed, {_tfwRangeFailed} failed");
        GetTree().Quit(_tfwRangeFailed == 0 ? 0 : 1);
    }

    void TfwRangeCheck(string name, bool ok, string detail = "")
    {
        if (ok) _tfwRangePassed++; else _tfwRangeFailed++;
        GD.Print($"TFWRANGE {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " - " + detail : "")}");
    }

    (int Known, float Nearest) TfwRangeCount()
    {
        var me = _client!.Predicted.Position;
        float nearest = float.MaxValue;
        foreach (var e in _tfwShown) nearest = MathF.Min(nearest, Num.Vector2.Distance(new(me.X, me.Y), new(e.Position.X, e.Position.Y)));
        return (_tfwShown.Count, nearest);
    }

    async Task TfwRangeTestScript()
    {
        var client = _client!;
        if (!await TfwUntil("the war is on, in the hub", () => client.Match.State == MatchState.Playing && _tfwState.War is { Phase: TfwRunPhase.Hub } && client.Predicted.Alive, 60.0)) { _tfwRangeFailed++; return; }
        var range = TfwRangeArea!;
        await TfwWait(1.5);
        if (!await TfwWalkTo(TfwHub!.RangeStation, "walked to the Firing Range button")) { _tfwRangeFailed++; return; }
        TfwRangeCheck("the station prompt reads FIRING RANGE", _tfwAt == TfwStation.Range, $"{_tfwAt}");
        await TfwShot("range-1-button");
        await TfwTap(Key.E);
        if (!await TfwUntil("E at the button opens the confirm", () => _tfwConfirm is not null, 3.0)) { _tfwRangeFailed++; return; }
        TfwRangeCheck("the confirm is open and the player still in the hub", TfwHub.Contains(client.Predicted.Position));
        await TfwShot("range-2-confirm");
        ((TfwRangeConfirm)_tfwConfirm!).GoButton.EmitSignal(BaseButton.SignalName.Pressed);   // the TELEPORT button's own path
        TfwRangeCheck("TELEPORT closed the confirm", _tfwConfirm is null);
        if (!await TfwUntil("the server moves the player to the range", () => range.Contains(client.Predicted.Position), 6.0)) { _tfwRangeFailed++; return; }
        TfwRangeCheck("the player stands at the range", true, client.Predicted.Position.ToString());

        int peak = 0;
        float first = float.MaxValue, last = float.MaxValue;
        double end = _connectedFor + 25.0, note = 0;
        while (_connectedFor < end)
        {
            await TfwFrames();
            var (known, nearest) = TfwRangeCount();
            peak = Math.Max(peak, known);
            if (known > 0 && first == float.MaxValue) first = nearest;
            last = nearest;
            if (_connectedFor >= note) { note = _connectedFor + 2.0; GD.Print($"TFWRANGE note: t {_connectedFor:F1} orks known {known}, nearest {nearest:F1} m, alive {client.Predicted.Alive}, health {client.Predicted.Health:F0}"); }
            if (peak >= 190 && last < first - 5f && _connectedFor > end - 18.0) break;
        }
        await TfwShot("range-3-orks");
        TfwRangeCheck("about 200 Orks are alive at the range", peak >= 180, $"peak {peak}");
        TfwRangeCheck("they come at the player", last < first - 5f || last < 6f, $"nearest {first:F1} m at first, {last:F1} m later");

        // The strike: B at the crosshair; the Orks near the mark fall.
        long deathsBefore = MusouDeathsSeen;   // the range tops the Orks up to 200 again, so the count of the living cannot show the strike: the deaths told do
        TfwKey(Key.B, true); await TfwFrames(4); TfwKey(Key.B, false);   // the real key (its point is the crosshair's, which here looks at a far wall)
        var mass = _tfwShown.Count == 0 ? default : _tfwShown.OrderBy(e => Num.Vector2.Distance(new(e.Position.X, e.Position.Y), new(client.Predicted.Position.X, client.Predicted.Position.Y))).Take(8).Aggregate(Num.Vector3.Zero, (s, e) => s + new Num.Vector3(e.Position.X, e.Position.Y, e.Position.Z)) / 8f;
        _tfwState.Commands.Issue(TfwCommand.Orbital, 0, mass);   // and one on the Orks round the player, the server's rules the same
        await TfwWait(14.0);
        await TfwShot("range-4-strike");
        long killed = MusouDeathsSeen - deathsBefore;
        TfwRangeCheck("an orbital strike at the range kills Orks", killed >= 10, $"{killed} deaths told in 14 s, {TfwRangeCount().Known} Orks known after (topped up to 200)");

        // Back by the pad (or, if the Orks are too many to walk through, by the command: reported as a failure of the walk only).
        bool onPad = await TfwWalkTo(range.ReturnStation, "walked to the return pad", 20.0);
        if (onPad) await TfwTap(Key.E);
        else _tfwState.Commands.Issue(TfwCommand.RangeBack);
        bool home = await TfwUntil("the player is back in the Stormbird", () => TfwHub.Contains(client.Predicted.Position), 8.0);
        TfwRangeCheck("the return pad brings the player back", home && onPad, $"walked {onPad}, home {home}");
        await TfwWait(3.0);
        var (leftover, _) = TfwRangeCount();
        TfwRangeCheck("the range's Orks are gone when the last player leaves", leftover == 0, $"{leftover} known");
    }
}
