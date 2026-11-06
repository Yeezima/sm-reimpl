using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Added content "Musou": the far crowd's motion (2026-11-06, the owner: "the orks movement at distance looks so choppy"). <c>--musou-old-far-motion</c> restores
/// the motion before the fix (<see cref="MusouClientState.OldFarMotion"/>: a far Ork carried on past his newest record for at most 6 ticks, only after a step of at
/// most 6, and never smoothed). <c>--musou-motion-log</c> (a measuring aid, headless or windowed) prints a <c>MUSOUMOTION</c> line every 10 s: for the running
/// Orks shown at about 10, 30, 60 and 100 m from the own player, the frames drawn, the share of frames a runner did not move, the p99 of the frame-to-frame
/// speed change and the largest step (m/s and metres), and the Musou bytes a second received.
/// </summary>
public partial class NetworkView
{
    static readonly float[] MusouMotionBands = [10f, 30f, 60f, 100f];
    bool? _musouMotionLog;
    bool _musouOldMotionSet;
    double _musouMotionAt;
    long _musouMotionBytes;
    readonly Dictionary<ushort, (System.Numerics.Vector3 At, double Speed)> _musouMotionLast = new();
    readonly List<double>[] _musouMotionSteps = MusouMotionBands.Select(_ => new List<double>()).ToArray();
    readonly int[] _musouMotionFrames = new int[4], _musouMotionStalls = new int[4];
    readonly double[] _musouMotionMax = new double[4];
    readonly Dictionary<ushort, uint> _musouMotionRecord = new();
    readonly List<uint>[] _musouMotionGaps = MusouMotionBands.Select(_ => new List<uint>()).ToArray();
    int _musouMotionNewOnSameId;

    /// <summary>Called before the frame's sections are read: the old-motion switch once, and the bytes received for the log.</summary>
    void MusouMotionSections(IReadOnlyList<ExtrasSection> sections)
    {
        if (!_musouOldMotionSet) { _musouOldMotionSet = true; _musouState.OldFarMotion = CmdFlag("musou-old-far-motion"); if (_musouState.OldFarMotion) GD.Print("musou: the far crowd moves as before 2026-11-06 (--musou-old-far-motion)"); }
        if (_musouMotionLog != true) return;
        foreach (var s in sections) _musouMotionBytes += SmNet.Protocol.ExtrasSectionHeaderBytes + s.Payload.Length;
    }

    static int MusouMotionBand(float metres)
    {
        if (metres is >= 5f and < 20f) return 0;
        if (metres is >= 20f and < 45f) return 1;
        if (metres is >= 45f and < 80f) return 2;
        if (metres is >= 80f and < 130f) return 3;
        return -1;
    }

    /// <summary>The frame's measure of the shown crowd (only with <c>--musou-motion-log</c>).</summary>
    void MusouMotionFrame(PlayerState me, IReadOnlyList<MusouShown> shown, double delta)
    {
        _musouMotionLog ??= CmdFlag("musou-motion-log");
        if (_musouMotionLog != true || delta <= 0) return;
        var seen = new HashSet<ushort>();
        foreach (var e in shown)
        {
            seen.Add(e.Id);
            int at = MusouMotionBand(System.Numerics.Vector2.Distance(new(e.Position.X, e.Position.Y), new(me.Position.X, me.Position.Y)));
            if (_musouState.NewestRecordTick(e.Id) is { } newest)
            {
                if (_musouMotionRecord.TryGetValue(e.Id, out var was) && newest > was && at >= 0) _musouMotionGaps[at].Add(newest - was);
                _musouMotionRecord[e.Id] = newest;
            }
            if (e.State != MusouEnemyState.Seek) { _musouMotionLast.Remove(e.Id); continue; }
            if (_musouMotionLast.TryGetValue(e.Id, out var last))
            {
                int band = MusouMotionBand(System.Numerics.Vector2.Distance(new(e.Position.X, e.Position.Y), new(me.Position.X, me.Position.Y)));
                double step = System.Numerics.Vector3.Distance(last.At, e.Position), speed = step / delta;
                if (band >= 0)
                {
                    _musouMotionFrames[band]++;
                    if (step < 1e-4) _musouMotionStalls[band]++;
                    if (last.Speed >= 0) _musouMotionSteps[band].Add(Math.Abs(speed - last.Speed));
                    _musouMotionMax[band] = Math.Max(_musouMotionMax[band], step);
                }
                _musouMotionLast[e.Id] = (e.Position, speed);
            }
            else _musouMotionLast[e.Id] = (e.Position, -1);
        }
        foreach (var id in _musouMotionLast.Keys.Where(k => !seen.Contains(k)).ToList()) _musouMotionLast.Remove(id);
        foreach (var id in _musouMotionRecord.Keys.Where(k => !seen.Contains(k)).ToList()) _musouMotionRecord.Remove(id);
        if (_connectedFor < _musouMotionAt) return;
        double window = _musouMotionAt <= 0 ? 0 : 10.0;
        _musouMotionAt = _connectedFor + 10.0;
        var parts = new List<string>();
        for (int b = 0; b < MusouMotionBands.Length; b++)
        {
            var sorted = _musouMotionSteps[b].OrderBy(x => x).ToArray();
            double p99 = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(0.99 * sorted.Length))];
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{MusouMotionBands[b]:F0} m: {_musouMotionFrames[b]} runner frames, stalls {(_musouMotionFrames[b] == 0 ? 0 : _musouMotionStalls[b] / (double)_musouMotionFrames[b]):P1}, speed step p99 {p99:F2} m/s, largest step {_musouMotionMax[b]:F2} m, records every {(_musouMotionGaps[b].Count == 0 ? 0 : _musouMotionGaps[b].Average(g => (double)g)):F1} ticks (max {(_musouMotionGaps[b].Count == 0 ? 0 : _musouMotionGaps[b].Max())})"));
            _musouMotionSteps[b].Clear(); _musouMotionFrames[b] = 0; _musouMotionStalls[b] = 0; _musouMotionMax[b] = 0; _musouMotionGaps[b].Clear();
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"MUSOUMOTION t {_connectedFor:F0} s ({(_musouState.OldFarMotion ? "old" : "new")} motion): shown {shown.Count}; {string.Join("; ", parts)}; received {(window > 0 ? _musouMotionBytes / window / 1000.0 : 0):F1} KB/s; new tracks on a known id {_musouState.NewOnSameId - _musouMotionNewOnSameId}, silent drops {_musouState.DroppedSilent}"));
        _musouMotionNewOnSameId = _musouState.NewOnSameId;
        _musouMotionBytes = 0;
    }
}
