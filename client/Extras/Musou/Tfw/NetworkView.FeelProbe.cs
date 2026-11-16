using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid <c>--feel-probe</c> (Musou combat feel, 2026-11-09; Musou Mode only, a client that draws): per own hit the server reports, the time from the press to the predicted contact (the
/// wall time at which the client's predicted tick reached the hit's tick) and from that contact to the report's arrival, the client's lead on the newest snapshot and how far the crowd's
/// render tick is behind the hit; per own kill the report's lag; frame times (a FEEL spike line for a frame over 25 ms, a summary every 10 s). Presses are the scripted player's
/// (<c>--tfw-test-melee</c>). Never take screenshots in a frame-time run: a PNG capture costs about 330 ms.
/// </summary>
public partial class NetworkView
{
    static readonly bool FeelProbeOn = CmdFlag("feel-probe");
    readonly Dictionary<uint, double> _fpTickAt = new();
    uint _fpLastTick;
    InputButtons _fpPrev;
    readonly List<(double At, uint Tick, InputButtons What)> _fpPress = new();
    double _fpLastFrame = -1, _fpLastSummary;
    readonly List<double> _fpFrames = new();
    readonly Dictionary<uint, int> _fpHitsPerTick = new();

    void FeelProbeFrame()
    {
        if (!FeelProbeOn || _client is not { } client) return;
        double now = Time.GetTicksUsec() / 1000.0;
        if (_fpLastFrame > 0)
        {
            double ms = now - _fpLastFrame;
            _fpFrames.Add(ms);
            if (ms > 25.0) GD.Print($"FEEL spike {ms:F1} ms at t {_connectedFor:F2}");
        }
        _fpLastFrame = now;
        uint tick = client.Tick;
        if (_fpLastTick == 0) _fpLastTick = tick;
        for (uint t = _fpLastTick + 1; t <= tick && tick - t < 64; t++) _fpTickAt.TryAdd(t, now);
        _fpLastTick = Math.Max(_fpLastTick, tick);
        if (_fpTickAt.Count > 4096) foreach (var old in _fpTickAt.Keys.Where(k => k + 2048 < tick).ToList()) _fpTickAt.Remove(old);
        var held = _tfwTestButtons;
        var fresh = held & ~_fpPrev & ~InputButtons.Sprint;
        if (fresh != 0) { _fpPress.Add((now, tick + 1, fresh)); GD.Print($"FEEL press {fresh} at t {_connectedFor:F3} tick {tick + 1}"); }
        _fpPrev = held;
        var (renderTick, _) = client.RemotePlayers();
        int own = client.PlayerId;
        foreach (var e in _musouState.NewEvents)
        {
            if (e.Player != own) continue;
            double? contactAt = _fpTickAt.TryGetValue(e.Tick, out var c) ? c : null;
            if (e.Kind == MusouEventKind.Hit)
            {
                _fpHitsPerTick[e.Tick] = _fpHitsPerTick.GetValueOrDefault(e.Tick) + 1;
                if (_fpHitsPerTick[e.Tick] != 1) continue;
                bool shot = e.Value is 1 or 2;
                var press = _fpPress.LastOrDefault(p => p.Tick <= e.Tick && ((p.What & InputButtons.Fire) != 0) == shot);
                string pressTo = press.At > 0 && contactAt is { } ca ? $"{ca - press.At:F0} ms ({e.Tick - press.Tick} ticks)" : "-";
                GD.Print($"FEEL hit v{e.Value} tick {e.Tick} press-to-predicted-contact {pressTo} confirm-after-contact {(contactAt is { } cc ? (now - cc).ToString("F0") : "-")} ms; ahead {(long)tick - client.LatestSnapshotTick} ticks; event-render {e.Tick - renderTick:F1} ticks");
            }
            else if (e.Kind == MusouEventKind.Death)
                GD.Print($"FEEL kill tick {e.Tick} confirm-after-contact {(contactAt is { } cc ? (now - cc).ToString("F0") : "-")} ms");
        }
        if (_fpHitsPerTick.Count > 512) _fpHitsPerTick.Clear();
        if (_fpPress.Count > 256) _fpPress.RemoveRange(0, 128);
        if (_connectedFor - _fpLastSummary > 10.0 && _fpFrames.Count > 30)
        {
            _fpLastSummary = _connectedFor;
            var s = _fpFrames.OrderBy(x => x).ToList();
            GD.Print($"FEEL frames n {s.Count} p50 {s[s.Count / 2]:F1} p99 {s[(int)(s.Count * 0.99)]:F1} max {s[^1]:F1} ms; over 25 ms {s.Count(x => x > 25)}; over 50 ms {s.Count(x => x > 50)}; ahead {(long)client.Tick - client.LatestSnapshotTick} ticks");
            _fpFrames.Clear();
        }
    }
}
