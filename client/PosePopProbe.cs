using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// <c>--pose-pop-probe</c> (M40-T3 test aid): once per rendered frame, after every character has been posed, measures how far each bone's
/// local rotation turned since the previous frame (degrees per rendered frame; run Godot with <c>--max-fps 60</c> so a frame is 1/60 s) and keeps
/// the largest per player. Around every clip entry (<see cref="CharacterViewer.EntryStamp"/>: a Play / Pose taking over from another
/// clip or from the stances; the stance blends have their own fade and are not entries) it records an event: the largest turn in the
/// <see cref="EntryWindowSeconds"/> after the entry. In steady state (outside every entry window) it keeps each clip's own largest
/// turn, the authored fast moves (a sprint's foot, a swing). An event is a <b>pop</b> when its largest turn is above
/// <see cref="FlagDegrees"/> and more than <see cref="AuthoredMargin"/> degrees above the clip's own steady largest; the acceptance
/// (T3) is no pop. Every <see cref="ReportSeconds"/> a summary line per player and a total are logged; popping events are logged as they close.
/// Started by the first <see cref="CharacterViewer"/> that enters the tree when the flag is on the command line.
/// </summary>
public partial class PosePopProbe : Node
{
    public const float FlagDegrees = 25f;
    public const float AuthoredMargin = 5f;
    /// <summary>A frame within this long after an entry belongs to its event (the longest blend used is 0.167 s).</summary>
    public const double EntryWindowSeconds = 0.2;
    const double ReportSeconds = 5.0;
    static PosePopProbe? _instance;

    /// <summary>True when <c>--pose-pop-probe</c> was given.</summary>
    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--pose-pop-probe");

    sealed class Track
    {
        public string Name = "";
        public Quaternion[] Previous = Array.Empty<Quaternion>();
        public bool Have;
        public int Stamp;
        public double WindowLeft;
        public float WindowMax; public string WindowBone = "", WindowClip = "";
        public readonly List<string> WindowTrace = new();   // per frame of the window: turn, snapshot weight, clip time
        public int Frames;
        public float Max; public string MaxBone = "", MaxClip = "";
        public readonly List<float> Samples = new();
    }
    readonly Dictionary<ulong, Track> _tracks = new();
    readonly Dictionary<string, float> _steadyMax = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Player, string Clip, string Bone, float Max, string Trace)> _events = new();
    int _reportedEvents;
    double _since;

    /// <summary>Starts the probe node under the tree root (deferred; once).</summary>
    public static void EnsureStarted(Node from)
    {
        if (!Enabled || _instance is not null || !GodotObject.IsInstanceValid(from)) return;
        _instance = new PosePopProbe { Name = "PosePopProbe", ProcessPriority = 1000 };   // after every character and the networked view
        from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        GD.Print($"pose-pop probe on (crossfade {(CharacterViewer.NoCrossfade ? "OFF" : "on")}): a pop is a clip entry whose largest bone turn in {EntryWindowSeconds} s is above {FlagDegrees:0}° per frame and {AuthoredMargin:0}° above the clip's own steady largest");
    }

    void CloseEvent(Track track)
    {
        if (track.WindowClip.Length == 0) return;
        _events.Add((track.Name, track.WindowClip, track.WindowBone, track.WindowMax, string.Join(" ", track.WindowTrace)));
        track.WindowTrace.Clear();
        track.WindowClip = "";
        track.WindowLeft = 0;
    }

    bool IsPop((string Player, string Clip, string Bone, float Max, string Trace) e) =>
        e.Max > FlagDegrees && e.Max > (_steadyMax.TryGetValue(e.Clip, out var steady) ? steady : 0f) + AuthoredMargin;

    public override void _Process(double delta)
    {
        foreach (var character in CharacterViewer.Live)
        {
            if (!GodotObject.IsInstanceValid(character) || character.Skeleton is not { } skeleton || !character.IsVisibleInTree()) continue;
            ulong id = character.GetInstanceId();
            if (!_tracks.TryGetValue(id, out var track)) _tracks[id] = track = new Track { Stamp = character.EntryStamp };
            track.Name = character.Name;
            int n = skeleton.GetBoneCount();
            if (character.PoseLocked || n == 0) { track.Have = false; CloseEvent(track); continue; }
            if (track.Previous.Length != n) { track.Previous = new Quaternion[n]; track.Have = false; }
            string clip = character.Playing ?? "";
            if (character.EntryStamp != track.Stamp)
            {
                track.Stamp = character.EntryStamp;
                CloseEvent(track);
                track.WindowLeft = EntryWindowSeconds; track.WindowMax = 0f; track.WindowBone = ""; track.WindowClip = clip;
            }
            if (track.Have)
            {
                float worst = 0f; int worstBone = 0;
                for (int b = 0; b < n; b++)
                {
                    var q = skeleton.GetBonePoseRotation(b);
                    float dot = Math.Clamp(Math.Abs(q.Dot(track.Previous[b])), 0f, 1f);
                    float degrees = Mathf.RadToDeg(2f * MathF.Acos(dot));
                    if (degrees > worst) { worst = degrees; worstBone = b; }
                }
                track.Frames++;
                track.Samples.Add(worst);
                if (worst > track.Max) { track.Max = worst; track.MaxBone = skeleton.GetBoneName(worstBone); track.MaxClip = clip; }
                if (track.WindowLeft > 0)
                {
                    track.WindowTrace.Add($"{worst:0}/{character.EntryWeight:0.00}/{(character.ClipTime is { } ct ? ct.Time : -1f):0.000}");
                    if (worst > track.WindowMax) { track.WindowMax = worst; track.WindowBone = skeleton.GetBoneName(worstBone); }
                    track.WindowLeft -= delta;
                    if (track.WindowLeft <= 0) CloseEvent(track);
                }
                else if (clip.Length > 0 && (!_steadyMax.TryGetValue(clip, out var known) || worst > known)) _steadyMax[clip] = worst;
            }
            for (int b = 0; b < n; b++) track.Previous[b] = skeleton.GetBonePoseRotation(b);
            track.Have = true;
        }
        _since += delta;
        if (_since < ReportSeconds) return;
        _since = 0;
        foreach (var track in _tracks.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (track.Frames == 0) continue;
            track.Samples.Sort();
            float p99 = track.Samples[Math.Min(track.Samples.Count - 1, (int)(track.Samples.Count * 0.99))];
            GD.Print($"pose-pop summary {track.Name}: {track.Frames} frames, max turn {track.Max:0.0}° ({track.MaxBone}, {track.MaxClip}), p99 {p99:0.0}°");
            track.Frames = 0; track.Max = 0; track.Samples.Clear();
        }
        for (; _reportedEvents < _events.Count; _reportedEvents++)
            if (IsPop(_events[_reportedEvents]))
            {
                var e = _events[_reportedEvents];
                GD.Print($"pose-pop: {e.Player} entry {e.Clip}: {e.Max:0.0}° at {e.Bone} (clip steady largest {(_steadyMax.TryGetValue(e.Clip, out var s) ? s : 0f):0.0}°){(e.Max > 60f ? "; frames turn/snapshot weight/clip time: " + e.Trace : "")}");
            }
        int pops = _events.Count(IsPop);
        var worstEvents = _events.OrderByDescending(e => e.Max).Take(1).ToList();
        GD.Print($"pose-pop total: {_events.Count} clip entries, {_events.Count(e => e.Max > FlagDegrees)} above {FlagDegrees:0}°, {pops} pops (above the clip's own steady largest); " +
                 $"entry window max {(worstEvents.Count > 0 ? worstEvents[0].Max : 0f):0.0}° ({(worstEvents.Count > 0 ? worstEvents[0].Clip + " " + worstEvents[0].Bone : "-")}); " +
                 $"median {(_events.Count > 0 ? _events.OrderBy(e => e.Max).ElementAt(_events.Count / 2).Max : 0f):0.0}°");
    }
}
