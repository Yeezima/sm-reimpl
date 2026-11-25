using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Warfront;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmClient;

/// <summary>
/// <c>--wf-duel-demo [--wf-duel-demo-shots &lt;dir&gt;]</c> (Chaos Warfront C5c capture aid; a test script, not a game rule; a placeholder demonstration): 8 s after the join, on any match, the client feeds itself a
/// scripted finale as tag 51 kind 16 and 17 sections encoded with the real wire and decoded through the same path as the server's (<see cref="WarfrontDuelApply"/>): the viewer duels the Lord (the entry line, the bar
/// from 100 % down, a leap marker 6 m ahead, three bolt flashes 14 m ahead), falls (the 3 s handoff countdown), a second challenger (player id own + 1, a name the client does not know) duels with the viewer spectating,
/// the Lord's health carried across bit for bit, then wins; then a lost relay. One <c>WFDUELDEMO</c> line per step; with <c>--wf-duel-demo-shots</c> a PNG of the view at each step.
/// </summary>
public partial class NetworkView
{
    static readonly bool DuelDemoOn = CmdFlag("wf-duel-demo");
    static readonly string? DuelDemoShots = CmdArg("wf-duel-demo-shots");
    double _ddStart = -1;
    int _ddStep;
    uint _ddTick = 1000;

    sealed record DuelDemoStep(double At, string Name, Func<ushort, Godot.Vector3, List<byte[]>> Sections, bool Shot);

    List<DuelDemoStep>? _ddSteps;

    void WarfrontDuelDemoFrame()
    {
        if (!DuelDemoOn || _bot || _client is not { PlayerId: > 0 } c || _gameSpace is null || _ddSteps is { } done && _ddStep >= done.Count) return;
        double now = _connectedFor;
        if (_ddStart < 0) { if (now < 8.0) return; _ddStart = now; _ddSteps = DuelDemoScript(); GD.Print($"t {now:F1} s: WFDUELDEMO start (placeholder demonstration: synthetic sections)"); }
        var steps = _ddSteps!;
        ushort own = (ushort)c.PlayerId;
        var me = c.Predicted;
        var at = new Godot.Vector3(me.Position.X, me.Position.Y, me.Position.Z);
        float yaw = me.Yaw;
        // the Lord's notional place: 14 m ahead of the viewer's facing (sim axes: forward is (-sin yaw, cos yaw))
        var ahead = new Godot.Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        _ddLordAt = at + ahead * 14f;
        _ddAhead = at + ahead * 6f;
        while (_ddStep < steps.Count && now - _ddStart >= steps[_ddStep].At)
        {
            var step = steps[_ddStep++];
            foreach (var bytes in step.Sections(own, at)) { WarfrontDuelApply(bytes, now); if (bytes[0] == WarfrontChaosDuelWire.KindState) { _ddLast = bytes; _ddLastAt = now; } }
            GD.Print($"t {now:F1} s (frame {Engine.GetFramesDrawn()}): WFDUELDEMO {step.Name}");
            if (step.Shot) _ddShotAt = now + 0.35;   // a frame later: the HUD has drawn the step
        }
        if (_ddLast is { } last && _ddLast[1] < (byte)WarfrontDuelPhase.Won && now - _ddLastAt >= 0.5) { WarfrontDuelApply(last, now); _ddLastAt = now; }   // the server's 2 Hz repeat
        if (_ddShotAt > 0 && now >= _ddShotAt && DuelDemoShots is { Length: > 0 } dir && GetViewport()?.GetTexture()?.GetImage() is { } image)
        {
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, $"duel-{_ddShots++:D2}-{SafeName(steps[Math.Max(0, _ddStep - 1)].Name)}.png");
            var saved = image.SavePng(file);
            GD.Print($"t {now:F1} s: WFDUELDEMO still {file}{(saved == Error.Ok ? "" : $" NOT SAVED ({saved})")}");
            _ddShotAt = -1;
        }
    }

    /// <summary>A step name as a file name: no colons, slashes, percent or other characters Windows refuses.</summary>
    static string SafeName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in name) sb.Append(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '.' ? ch : '-');
        return sb.ToString().Trim();
    }

    Godot.Vector3 _ddLordAt, _ddAhead;
    double _ddShotAt = -1, _ddLastAt;
    byte[]? _ddLast;
    int _ddShots;

    byte[] DdState(WarfrontDuelPhase phase, ushort champion, ushort next, int remaining, int attempt, float health)
    {
        const float max = 12000f;
        return WarfrontChaosDuelWire.Encode(WarfrontChaosDuelWire.Of(phase, champion, next, remaining, attempt, 777, health, max));
    }

    byte[] DdTold(byte ability, Godot.Vector3 at, float seconds) =>
        WarfrontChaosDuelWire.Encode(WarfrontChaosDuelWire.Of(new WarfrontLordTelegraph(_ddTick++, ability, new System.Numerics.Vector3(at.X, at.Y, at.Z), seconds)));

    List<DuelDemoStep> DuelDemoScript()
    {
        // the Lord's health across the script: 12000 down to 7440 (62 %) in the first duel, carried exactly into the second, then 0
        const float full = 12000f, carried = 7440.25f;
        List<byte[]> One(byte[] b) => [b];
        return
        [
            new(0.0, "entry: you are the champion", (o, _) => One(DdState(WarfrontDuelPhase.Duel, o, (ushort)(o + 1), 1, 0, full)), true),
            new(2.5, "the Lord at 85 %", (o, _) => One(DdState(WarfrontDuelPhase.Duel, o, (ushort)(o + 1), 1, 0, full * 0.85f)), false),
            new(4.0, "leap marker 6 m ahead (1.0 s)", (o, _) => { var leap = DdTold(WarfrontLordTelegraph.Leap, _ddAhead, WarfrontDuelRules.LeapMarkerSeconds); return [leap, leap]; }, true),
            new(6.0, "bolt 1", (o, _) => One(DdTold(WarfrontLordTelegraph.Bolt, _ddLordAt + new Godot.Vector3(0f, 0f, 1.9f), 0f)), true),
            new(6.6, "bolt 2", (o, _) => One(DdTold(WarfrontLordTelegraph.Bolt, _ddLordAt + new Godot.Vector3(0f, 0f, 1.9f), 0f)), false),
            new(7.2, "bolt 3, the Lord at 62 %", (o, _) => [DdTold(WarfrontLordTelegraph.Bolt, _ddLordAt + new Godot.Vector3(0f, 0f, 1.9f), 0f), DdState(WarfrontDuelPhase.Duel, o, (ushort)(o + 1), 1, 0, carried)], false),
            new(10.0, "you fall: handoff", (o, _) => One(DdState(WarfrontDuelPhase.Handoff, 0, (ushort)(o + 1), 1, -1, carried)), true),
            new(11.0, "handoff 2 s", (o, _) => One(DdState(WarfrontDuelPhase.Handoff, 0, (ushort)(o + 1), 1, -1, carried)), false),
            new(12.0, "handoff 1 s", (o, _) => One(DdState(WarfrontDuelPhase.Handoff, 0, (ushort)(o + 1), 1, -1, carried)), false),
            new(13.0, "second challenger: you spectate, the Lord still 62 %", (o, _) => One(DdState(WarfrontDuelPhase.Duel, (ushort)(o + 1), 0, 0, 1, carried)), true),
            new(16.0, "the Lord at 20 %", (o, _) => One(DdState(WarfrontDuelPhase.Duel, (ushort)(o + 1), 0, 0, 1, full * 0.2f)), false),
            new(18.0, "the Lord at 3 %", (o, _) => One(DdState(WarfrontDuelPhase.Duel, (ushort)(o + 1), 0, 0, 1, full * 0.03f)), false),
            new(19.0, "won", (o, _) => One(DdState(WarfrontDuelPhase.Won, (ushort)(o + 1), 0, 0, 1, 0f)), true),
            new(30.0, "a lost relay: you duel last", (o, _) => One(DdState(WarfrontDuelPhase.Duel, o, 0, 0, 0, full * 0.4f)), false),
            new(32.0, "lost", (o, _) => One(DdState(WarfrontDuelPhase.Lost, 0, 0, 0, -1, full * 0.4f)), true),
            new(42.0, "end", (o, _) => [], false),
        ];
    }
}
