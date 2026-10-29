using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// <c>--anim-probe</c> (M40-T1 test aid, with <c>--bot-gait --gait-sweep</c> bots or any play): measures what a viewer sees of the legs, per drawn marine, once
/// per rendered frame after everything is posed. Off by default and free when off (the node is not created). Started by the first
/// <see cref="CharacterViewer"/> that enters the tree when the flag is on the command line (as <see cref="PosePopProbe"/>, which measures the pose pops;
/// run both). Run Godot with <c>--fixed-fps 60</c> (a frame is 1/60 s) on a demo replay for repeatable numbers; <c>--anim-probe-csv &lt;file&gt;</c> writes one row
/// per marine and frame for offline analysis.
/// <list type="bullet">
/// <item><b>Planted-foot slide.</b> A foot is <b>flat</b> (planted) while its toe is lower than <see cref="ToeEnter"/> m above the marine's ground (the
/// character origin; the rig's toe bones rest 0.006 to 0.014 m above it) and its ankle lower than <see cref="AnkleEnter"/> m (the ankle is 0.20 m up when the
/// heel is down), released above <see cref="ToeExit"/> / <see cref="AnkleExit"/>. A second count, <b>toe only</b>, ignores the ankle (it includes the toe-pivot at the ends of a
/// stance, where the heel lifts and the toe still touches). While planted the toe's horizontal world speed is the slide (a standing foot has none: it is fixed
/// to the ground); the speeds of the planted frames are collected, and so is, per planted stretch ("step"), the distance from where the foot landed to where it
/// last was before it lifted. Both only count while the marine runs steadily: its drawn speed over the last <see cref="WindowSeconds"/> is at least
/// <see cref="MinSpeed"/> and its speed and heading did not change by more than <see cref="SteadySpeed"/> / <see cref="SteadyHeading"/> over the two halves of
/// that window, and the whole step was steady. They are binned by the movement heading against the body's facing (the sector of 22.5 degrees,
/// 0 forward, +90 right) and by speed band (walk, run, sprint).</item>
/// <item><b>Stance changes.</b> The dominant clip of every moving frame (<see cref="CharacterViewer.ClipTime"/>: the discrete stance before T4, the
/// blend layer's clip with the largest weight, held until another leads by <see cref="CharacterViewer.DominantHysteresis"/>, after); changes in any
/// <see cref="ChangeWindowSeconds"/> window, in total, while strafing (heading 60 to 120 degrees off forward or back) and while strafing at a steady speed and
/// heading (the audit's acceptance: at most one), and flip-flops (a clip left and returned to within one window: the flicker).</item>
/// </list>
/// Printed every <see cref="ReportSeconds"/> as ANIMPROBE lines and at exit. Client test aid, ours.
/// </summary>
public partial class AnimProbe : Node
{
    public const float ToeEnter = 0.02f, ToeExit = 0.03f, AnkleEnter = 0.24f, AnkleExit = 0.27f;
    /// <summary>The lowest height (m, above the character origin) each toe has reached in this run (left, right; reported). Frames before <see cref="WarmUpFrames"/> of a marine are not counted.</summary>
    static readonly float[] ToeFloor = [float.MaxValue, float.MaxValue];
    public const int WarmUpFrames = 90;
    public const double WindowSeconds = 0.2, ChangeWindowSeconds = 0.3;
    public const float MinSpeed = 1.0f, SteadySpeed = 0.1f, SteadyHeading = 10f;
    const double ReportSeconds = 20.0;
    static AnimProbe? _instance;

    /// <summary>True when <c>--anim-probe</c> was given.</summary>
    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--anim-probe");

    public static void EnsureStarted(Node from)
    {
        if (!Enabled || _instance is not null || !GodotObject.IsInstanceValid(from)) return;
        _instance = new AnimProbe { Name = "AnimProbe", ProcessPriority = 1000 };
        from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        GD.Print("anim probe on: planted-foot slide (flat foot: toe within 2 cm and ankle within 24 cm of the ground; toe-only second count), stance changes per 0.3 s; binned by heading sector and speed band");
    }

    sealed class Foot
    {
        public bool Planted, Steady, Melee;   // Melee (feel/melee-lock): the planted stretch was under a melee attack clip throughout
        public Vector2 Start, Last;      // horizontal world position where it landed / the last planted frame
    }

    /// <summary>One definition of "planted": its slide and step samples by (heading sector, speed band).</summary>
    sealed class Stats
    {
        public readonly Dictionary<(int Sector, int Band), List<float>> Slide = new();
        public readonly Dictionary<(int Sector, int Band), List<float>> Steps = new();
    }

    sealed class Track
    {
        public int[] Toes = Array.Empty<int>(), Ankles = Array.Empty<int>(), Knees = Array.Empty<int>(), Hips = Array.Empty<int>();
        public Foot[][] Feet = [[new(), new()], [new(), new()]];   // [definition][foot]: 0 flat, 1 toe only
        public Vector2[] Previous = new Vector2[2];
        public bool HavePrevious;
        public readonly List<(double T, Vector2 P)> Positions = new();
        public string Dominant = "";
        public readonly List<(double T, string From, string To)> Changes = new();
        public int Flips, Frames;
        public double MoveRun;   // seconds the clip has been showing while the body moved
        public Quaternion[] PreviousPose = Array.Empty<Quaternion>();
        public bool HavePose;
    }

    /// <summary>The biggest single-frame bone turns seen (degrees, time, player, bone, the layers then), for the report.</summary>
    readonly List<(float Degrees, double T, string Player, string Bone, string Layers)> _jumps = new();

    readonly Stats[] _stats = [new(), new()];
    /// <summary>Body speed (m/s, over the last 0.1 s) from which a still-playing action clip counts as showing while the body moves.</summary>
    public const float ActionMoveSpeed = 1.5f;
    sealed class ActionStat { public int Frames, Moving; public double MovingSeconds, Longest; public readonly List<float> Slide = new(); }
    readonly Dictionary<string, ActionStat> _actions = new();
    /// <summary>feel/melee-lock: planted-foot speeds (flat foot, m/s) and planted-stretch slides (m) under melee attack clips, whatever the body's speed (the lunge's foot slide).</summary>
    readonly List<float> _meleeSlide = new(), _meleeSteps = new();
    int _meleeFrames;
    readonly List<double> _poseMs = new(), _frameMs = new();   // per rendered frame: the characters' pose work, and the frame's own time
    readonly List<float> _kneeAngles = new();   // the knee's interior angle (degrees) while moving
    readonly Dictionary<ulong, Track> _tracks = new();
    readonly List<int> _windowChanges = new();   // changes in the 0.3 s window ending at each moving frame
    readonly List<int> _strafeWindowChanges = new();
    readonly List<int> _steadyStrafeChanges = new();   // the same while strafing at a steady speed and heading (the audit's T4 acceptance)
    readonly Dictionary<(int Sector, int Band), int> _steadyFrames = new();
    int _frames, _movingFrames, _steadyStrafeEvents;
    System.IO.StreamWriter? _csv;
    static readonly string? CsvPath = OS.GetCmdlineUserArgs() is var a && Array.IndexOf(a, "--anim-probe-csv") is var i && i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    double _since, _clock;

    static (int Sector, int Band) Bin(float headingDegrees, float speed)
    {
        int sector = (int)MathF.Round(((headingDegrees % 360f) + 360f) % 360f / 22.5f) % 16;
        int band = speed < 3.25f ? 0 : speed < 7.4f ? 1 : 2;
        return (sector, band);
    }

    static float Median(List<float> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? float.NaN : s[s.Count / 2]; }
    static float Percentile(List<float> v, float p) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? float.NaN : s[Math.Min(s.Count - 1, (int)(s.Count * p))]; }

    public override void _Process(double delta)
    {
        if (delta <= 0) return;
        _clock += delta;
        if (CharacterViewer.PoseTicksThisFrame > 0 && _clock > 3.0)
        {
            _poseMs.Add(CharacterViewer.PoseTicksThisFrame * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _frameMs.Add(delta * 1000.0);
        }
        CharacterViewer.PoseTicksThisFrame = 0;
        foreach (var character in CharacterViewer.Live)
        {
            if (!GodotObject.IsInstanceValid(character) || character.Skeleton is not { } skeleton || !character.IsVisibleInTree() || character.PoseLocked) continue;
            ulong id = character.GetInstanceId();
            if (!_tracks.TryGetValue(id, out var track)) _tracks[id] = track = new Track();
            if (track.Toes.Length == 0)
            {
                int l = skeleton.FindBone("RIG_L_Toe"), r = skeleton.FindBone("RIG_R_Toe"), la = skeleton.FindBone("RIG_L_Foot"), ra = skeleton.FindBone("RIG_R_Foot");
                if (l < 0 || r < 0 || la < 0 || ra < 0) continue;
                track.Toes = [l, r]; track.Ankles = [la, ra];
                track.Knees = [skeleton.FindBone("RIG_L_Calf"), skeleton.FindBone("RIG_R_Calf")]; track.Hips = [skeleton.FindBone("RIG_L_Thigh"), skeleton.FindBone("RIG_R_Thigh")];
            }
            Step(character, skeleton, track, delta);
        }
        _since += delta;
        if (_since >= ReportSeconds) { _since = 0; Report(false); }
    }

    void Step(CharacterViewer character, Skeleton3D skeleton, Track track, double delta)
    {
        var origin = character.GlobalPosition;
        var here = new Vector2(origin.X, origin.Z);
        track.Positions.Add((_clock, here));
        while (track.Positions.Count > 1 && _clock - track.Positions[1].T >= WindowSeconds) track.Positions.RemoveAt(0);
        var basis = character.GlobalTransform.Basis;
        var forward = new Vector2(basis.Y.X, basis.Y.Z).Normalized();   // the model's facing (local +Y, game space)
        var right = new Vector2(basis.X.X, basis.X.Z).Normalized();

        // The drawn velocity over the window, and over its halves for the steadiness test.
        bool steady = false;
        float speed = 0f, heading = 0f;
        if (track.Positions.Count >= 4 && _clock - track.Positions[0].T >= WindowSeconds * 0.9)
        {
            var first = track.Positions[0];
            var mid = track.Positions[track.Positions.Count / 2];
            var v = (here - first.P) / (float)(_clock - first.T);
            var v1 = (mid.P - first.P) / (float)(mid.T - first.T);
            var v2 = (here - mid.P) / (float)(_clock - mid.T);
            speed = v.Length();
            if (speed >= MinSpeed && v1.Length() > 1e-3f && v2.Length() > 1e-3f)
            {
                heading = Mathf.RadToDeg(MathF.Atan2(v.Dot(right), v.Dot(forward)));
                float turn = MathF.Abs(Mathf.RadToDeg(v1.AngleTo(v2)));
                steady = MathF.Abs(v2.Length() - v1.Length()) <= SteadySpeed * speed && turn <= SteadyHeading;
            }
        }

        // The biggest bone turn since the previous frame (the pose-pop probe's measure, with the layers that made it).
        int boneCount = skeleton.GetBoneCount();
        if (track.PreviousPose.Length != boneCount) { track.PreviousPose = new Quaternion[boneCount]; track.HavePose = false; }
        float worstTurn = 0f; int worstBone = 0;
        for (int b = 0; b < boneCount; b++)
        {
            var q = skeleton.GetBonePoseRotation(b);
            if (track.HavePose)
            {
                float turn = Mathf.RadToDeg(2f * MathF.Acos(Math.Clamp(MathF.Abs(q.Dot(track.PreviousPose[b])), 0f, 1f)));
                if (turn > worstTurn) { worstTurn = turn; worstBone = b; }
            }
            track.PreviousPose[b] = q;
        }
        track.HavePose = true;
        if (worstTurn > 45f && track.Frames > WarmUpFrames)
        {
            _jumps.Add((worstTurn, _clock, character.Name, skeleton.GetBoneName(worstBone), character.LayerDebug()));
            if (_jumps.Count > 400) _jumps.RemoveRange(0, 200);
        }

        // Action clips (a clip drives the pose, no stance layer): how long they keep showing while the body moves (M40-T4 follow-up).
        string actionClip = character.StanceKey is null ? character.Playing ?? ""
            : character.PoseLayerClip is { } layerClip && (layerClip.Contains("MEL_", StringComparison.OrdinalIgnoreCase) || layerClip.Contains("Toss", StringComparison.OrdinalIgnoreCase)) ? layerClip + " (upper-body layer over the locomotion)" : "";
        float fastSpeed = 0f;
        {
            int k = 0;
            while (k < track.Positions.Count - 1 && _clock - track.Positions[k].T > 0.1) k++;
            if (track.Positions.Count > 2 && _clock - track.Positions[k].T > 0.04)
                fastSpeed = (here - track.Positions[k].P).Length() / (float)(_clock - track.Positions[k].T);
        }
        ActionStat? action = null;
        if (actionClip.Length > 0 && track.Frames > WarmUpFrames)
        {
            if (!_actions.TryGetValue(actionClip, out action)) _actions[actionClip] = action = new ActionStat();
            action.Frames++;
            if (fastSpeed >= ActionMoveSpeed)
            {
                action.Moving++; action.MovingSeconds += delta;
                track.MoveRun += delta; action.Longest = Math.Max(action.Longest, track.MoveRun);
            }
            else track.MoveRun = 0;
        }
        else track.MoveRun = 0;
        bool actionMoving = action is not null && fastSpeed >= ActionMoveSpeed;
        // feel/melee-lock: a melee attack clip drives the pose (whole body, its distance variants included; not the upper-body hand-over layer).
        bool meleeClip = track.Frames > WarmUpFrames && character.StanceKey is null && (character.Playing ?? "").Contains("_MEL_", StringComparison.OrdinalIgnoreCase);
        if (meleeClip) _meleeFrames++;

        // Planted-foot slide.
        track.Frames++;
        if (steady) { var sk = Bin(heading, speed); _steadyFrames[sk] = _steadyFrames.GetValueOrDefault(sk) + 1; }
        string csvRow = "";
        for (int f = 0; f < 2; f++)
        {
            var toe = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(track.Toes[f]).Origin;
            float toeHeight = toe.Y - origin.Y;
            float ankleHeight = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(track.Ankles[f]).Origin).Y - origin.Y;
            var p = new Vector2(toe.X, toe.Z);
            ToeFloor[f] = MathF.Min(ToeFloor[f], toeHeight);
            if (track.Knees[f] >= 0 && track.Hips[f] >= 0 && speed >= MinSpeed)
            {
                var hip = skeleton.GetBoneGlobalPose(track.Hips[f]).Origin; var knee = skeleton.GetBoneGlobalPose(track.Knees[f]).Origin; var ankleP = skeleton.GetBoneGlobalPose(track.Ankles[f]).Origin;
                var a = (hip - knee).Normalized(); var b = (ankleP - knee).Normalized();
                _kneeAngles.Add(Mathf.RadToDeg(MathF.Acos(Math.Clamp(a.Dot(b), -1f, 1f))));
            }
            for (int definition = 0; definition < 2; definition++)
            {
                var foot = track.Feet[definition][f];
                bool flat = definition == 1 || (foot.Planted ? ankleHeight < AnkleExit : ankleHeight < AnkleEnter);
                bool planted = track.Frames > WarmUpFrames && flat && (foot.Planted ? toeHeight < ToeExit : toeHeight < ToeEnter);
                if (planted)
                {
                    bool wasPlanted = foot.Planted;
                    if (!foot.Planted) { foot.Planted = true; foot.Start = p; foot.Steady = steady; foot.Melee = meleeClip; }
                    else if (track.HavePrevious && definition == 0 && actionMoving) action!.Slide.Add((p - track.Previous[f]).Length() / (float)delta);
                    if (wasPlanted && definition == 0 && track.HavePrevious && meleeClip && foot.Melee) _meleeSlide.Add((p - track.Previous[f]).Length() / (float)delta);
                    foot.Melee &= meleeClip;
                    if (wasPlanted && track.HavePrevious && steady)
                    {
                        float slide = (p - track.Previous[f]).Length() / (float)delta;
                        var key = Bin(heading, speed);
                        if (!_stats[definition].Slide.TryGetValue(key, out var list)) _stats[definition].Slide[key] = list = new List<float>();
                        list.Add(slide);
                    }
                    foot.Steady &= steady;
                    foot.Last = p;
                }
                else if (foot.Planted)
                {
                    if (definition == 0 && foot.Melee) _meleeSteps.Add((foot.Last - foot.Start).Length());
                    if (foot.Steady && steady) AddStep(_stats[definition], heading, speed, (foot.Last - foot.Start).Length());
                    foot.Planted = false;
                }
                if (definition == 0 && CsvPath is not null) csvRow += $",{toeHeight:F4},{ankleHeight:F4},{(track.HavePrevious ? (p - track.Previous[f]).Length() / (float)delta : 0f):F3},{(planted ? 1 : 0)}";
            }
            track.Previous[f] = p;
        }
        track.HavePrevious = true;
        if (CsvPath is not null)
        {
            _csv ??= new System.IO.StreamWriter(CsvPath) { AutoFlush = false };
            _csv.WriteLine($"{_clock:F4},{character.Name},{speed:F3},{heading:F1},{(steady ? 1 : 0)},{character.ClipTime?.Clip}{csvRow},{character.LayerDebug()}");
        }

        // Stance changes while moving.
        _frames++;
        string dominant = character.ClipTime?.Clip ?? "";
        bool moving = speed >= MinSpeed;
        if (dominant != track.Dominant)
        {
            if (moving && track.Dominant.Length > 0)
            {
                if (track.Changes.Count > 0 && _clock - track.Changes[^1].T <= ChangeWindowSeconds && track.Changes[^1].From == dominant) track.Flips++;
                track.Changes.Add((_clock, track.Dominant, dominant));
                if (steady && MathF.Abs(MathF.Abs(heading) - 90f) <= 30f) _steadyStrafeEvents++;
            }
            track.Dominant = dominant;
        }
        track.Changes.RemoveAll(c => _clock - c.T > ChangeWindowSeconds + 1.0);
        if (moving)
        {
            _movingFrames++;
            int inWindow = track.Changes.Count(c => _clock - c.T <= ChangeWindowSeconds);
            _windowChanges.Add(inWindow);
            if (MathF.Abs(MathF.Abs(heading) - 90f) <= 30f) { _strafeWindowChanges.Add(inWindow); if (steady) _steadyStrafeChanges.Add(inWindow); }
        }
    }

    static void AddStep(Stats stats, float heading, float speed, float distance)
    {
        var key = Bin(heading, speed);
        if (!stats.Steps.TryGetValue(key, out var list)) stats.Steps[key] = list = new List<float>();
        list.Add(distance);
    }

    /// <summary>The acceptance view of one definition: the walk and run bands together (the sprint has its own table: its flat foot is a few frames long), per heading sector.</summary>
    string BySector(Stats stats, string tag, string label, out float worstSlide, out float worstStep)
    {
        var rows = new List<string>();
        worstSlide = 0f; worstStep = 0f;
        for (int sector = 0; sector < 16; sector++)
        {
            var slide = Enumerable.Range(0, 2).SelectMany(b => stats.Slide.GetValueOrDefault((sector, b)) ?? new List<float>()).ToList();
            var steps = Enumerable.Range(0, 2).SelectMany(b => stats.Steps.GetValueOrDefault((sector, b)) ?? new List<float>()).ToList();
            if (slide.Count < 10) continue;
            float ms = Median(slide), mp = steps.Count > 0 ? Median(steps) * 100f : float.NaN;
            worstSlide = MathF.Max(worstSlide, ms); if (!float.IsNaN(mp)) worstStep = MathF.Max(worstStep, mp);
            int steadyCount = Enumerable.Range(0, 2).Sum(b => _steadyFrames.GetValueOrDefault((sector, b)));
            rows.Add($"{sector * 22.5f,5:F1}: [{steadyCount} steady frames, {100f * slide.Count / Math.Max(1, steadyCount):F0} % with a foot planted] slide median {ms:F2} p90 {Percentile(slide, 0.9f):F2} m/s ({slide.Count} frames); step median {mp:F1} cm p90 {(steps.Count > 0 ? Percentile(steps, 0.9f) * 100f : float.NaN):F1} cm ({steps.Count} steps)");
        }
        return $"{tag} {label} by heading (deg, + right):\n  " + string.Join("\n  ", rows);
    }

    void Report(bool final)
    {
        string tag = final ? "ANIMPROBE final" : "ANIMPROBE";
        GD.Print($"{tag}: {_frames} marine-frames, {_movingFrames} moving (drawn speed >= {MinSpeed} m/s); lowest toe seen (m above the character origin) left {ToeFloor[0]:F3} right {ToeFloor[1]:F3}");
        if (_meleeFrames > 0)
            GD.Print($"{tag} melee attack clips (feel/melee-lock; whole-body clips, any body speed): {_meleeFrames} marine-frames; planted-foot speed median {Median(_meleeSlide):F2} p90 {Percentile(_meleeSlide, 0.9f):F2} m/s ({_meleeSlide.Count} frames); planted-stretch slide median {(_meleeSteps.Count > 0 ? Median(_meleeSteps) * 100f : float.NaN):F1} p90 {(_meleeSteps.Count > 0 ? Percentile(_meleeSteps, 0.9f) * 100f : float.NaN):F1} cm ({_meleeSteps.Count} stretches)");
        var movingActions = _actions.Where(kv => kv.Value.Moving >= 3).OrderByDescending(kv => kv.Value.MovingSeconds).Take(24).ToList();
        if (movingActions.Count > 0)
            GD.Print($"{tag} action clips still showing while the body moves (>= {ActionMoveSpeed} m/s over 0.1 s), per clip: seconds moving, longest run, planted-foot slide median / p90 (m/s, flat foot):\n  " +
                     string.Join("\n  ", movingActions.Select(kv => $"{kv.Key,-40} {kv.Value.Frames,5} clip frames, {kv.Value.Moving,4} moving = {kv.Value.MovingSeconds:F2} s, longest {kv.Value.Longest:F2} s; slide {(kv.Value.Slide.Count > 0 ? Median(kv.Value.Slide) : float.NaN):F2} / {(kv.Value.Slide.Count > 0 ? Percentile(kv.Value.Slide, 0.9f) : float.NaN):F2} ({kv.Value.Slide.Count} frames)")) +
                     $"\n  total {movingActions.Sum(kv => kv.Value.MovingSeconds):F2} s moving under an action clip");
        var still = _actions.Where(kv => kv.Value.Moving < 3 && kv.Value.Frames >= 3).OrderByDescending(kv => kv.Value.Frames).Take(30).ToList();
        if (still.Count > 0)
            GD.Print($"{tag} action clips seen without the body moving under them (clip frames): " + string.Join(", ", still.Select(kv => $"{kv.Key} {kv.Value.Frames}")));
        if (_poseMs.Count > 20)
        {
            var pose = _poseMs.OrderBy(x => x).ToList(); var frame = _frameMs.OrderBy(x => x).ToList();
            double P(List<double> l, double q) => l[Math.Min(l.Count - 1, (int)(l.Count * q))];
            GD.Print($"{tag} frame cost: characters' pose work per frame (ms) median {P(pose, 0.5):F2}, mean {pose.Average():F2}, p99 {P(pose, 0.99):F2}, worst {pose[^1]:F2}; frame time (ms) median {P(frame, 0.5):F2}, p99 {P(frame, 0.99):F2}, p99.9 {P(frame, 0.999):F2}, worst {frame[^1]:F2} ({pose.Count} frames)");
        }
        GD.Print($"{tag} foot pins: {CharacterViewer.SettlesBegun} settles begun, {CharacterViewer.FeetPinned} feet pinned, dropped {CharacterViewer.PinsDroppedReach} out of reach and {CharacterViewer.PinsDroppedTeleport} settles on a teleport");
        if (_kneeAngles.Count > 0) GD.Print($"{tag} knee angle while moving (degrees; 180 is a straight leg): min {_kneeAngles.Min():F0}, p1 {Percentile(_kneeAngles, 0.01f):F0}, median {Median(_kneeAngles):F0}, p99 {Percentile(_kneeAngles, 0.99f):F0}, max {_kneeAngles.Max():F0}");
        if (CharacterViewer.IkSingleLegs > 0) GD.Print($"{tag} leg correction while a single clip plays (it should be 0): {CharacterViewer.IkSingleLegs} legs, mean {CharacterViewer.IkSingleMoveSum / CharacterViewer.IkSingleLegs * 100f:F2} cm, largest {CharacterViewer.IkSingleMoveMax * 100f:F2} cm");
        GD.Print($"{tag} leg correction (blend ankle IK): {CharacterViewer.IkLegs} legs corrected, {CharacterViewer.IkUnreachable} with the wanted ankle out of reach (largest shortfall {CharacterViewer.IkWorstShortfall * 100f:F1} cm), largest ankle move {CharacterViewer.IkWorstMove * 100f:F1} cm ({CharacterViewer.IkWorstWhere})");
        for (int definition = 0; definition < 2; definition++)
        {
            var stats = _stats[definition];
            string name = definition == 0 ? "flat foot" : "toe only";
            if (definition == 0)
                foreach (int band in new[] { 0, 1, 2 })
                {
                    var rows = new List<string>();
                    for (int sector = 0; sector < 16; sector++)
                    {
                        if (!stats.Slide.TryGetValue((sector, band), out var slide) || slide.Count < 10) continue;
                        stats.Steps.TryGetValue((sector, band), out var steps);
                        rows.Add($"{sector * 22.5f,5:F1}: slide median {Median(slide):F2} p90 {Percentile(slide, 0.9f):F2} m/s ({slide.Count} frames); step median {(steps is { Count: > 0 } ? Median(steps) * 100 : float.NaN):F1} cm max {(steps is { Count: > 0 } ? steps.Max() * 100 : float.NaN):F1} cm ({steps?.Count ?? 0} steps)");
                    }
                    if (rows.Count > 0) GD.Print($"{tag} {new[] { "walk", "run", "sprint" }[band]} band, by heading (deg, + right):\n  " + string.Join("\n  ", rows));
                }
            string table = BySector(stats, tag, $"WALK AND RUN BANDS ({name})", out float worstSlide, out float worstStep);
            if (definition == 0 || final) GD.Print(table);
            GD.Print($"{tag} worst heading ({name}): planted-foot speed median {worstSlide:F2} m/s (acceptance < 0.3), step slide median {worstStep:F1} cm (acceptance < 15)");
            var all = stats.Slide.Where(kv => kv.Key.Band < 2).SelectMany(kv => kv.Value).ToList();
            var allSteps = stats.Steps.Where(kv => kv.Key.Band < 2).SelectMany(kv => kv.Value).ToList();
            if (all.Count > 0) GD.Print($"{tag} all steady frames ({name}): planted-foot speed median {Median(all):F2} p90 {Percentile(all, 0.9f):F2} m/s, {all.Count} frames; step slide median {(allSteps.Count > 0 ? Median(allSteps) * 100 : float.NaN):F1} cm p90 {(allSteps.Count > 0 ? Percentile(allSteps, 0.9f) * 100 : float.NaN):F1} cm max {(allSteps.Count > 0 ? allSteps.Max() * 100 : float.NaN):F1} cm");
        }
        if (_windowChanges.Count > 0)
        {
            double mean(List<int> l) => l.Count > 0 ? l.Average() : double.NaN;
            GD.Print($"{tag} stance changes per {ChangeWindowSeconds} s while moving: max {_windowChanges.Max()}, mean {mean(_windowChanges):F3}, frames with more than 1: {_windowChanges.Count(c => c > 1)}; " +
                     $"while strafing: max {(_strafeWindowChanges.Count > 0 ? _strafeWindowChanges.Max() : 0)}, mean {mean(_strafeWindowChanges):F3}, frames {_strafeWindowChanges.Count}; " +
                     $"while strafing steadily (acceptance <= 1): max {(_steadyStrafeChanges.Count > 0 ? _steadyStrafeChanges.Max() : 0)}, mean {mean(_steadyStrafeChanges):F3}, frames {_steadyStrafeChanges.Count}, with more than 1: {_steadyStrafeChanges.Count(c => c > 1)} (a window also holds the changes just before the steady stretch); changes that happened during steady strafing: {_steadyStrafeEvents} in {_steadyStrafeChanges.Count / 60.0:F0} s of marine-frames at 60 fps; " +
                     $"flip-flops (clip left and returned to within the window): {_tracks.Values.Sum(t => t.Flips)}");
        }
    }

    void ReportJumps(string tag)
    {
        if (_jumps.Count == 0) return;
        GD.Print($"{tag} bone turns above 45 degrees in one frame: {_jumps.Count} (largest first; the sprint's and swings' authored fast moves are among them)");
        GD.Print("    by bone: " + string.Join(", ", _jumps.GroupBy(j => j.Bone).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count()} (largest {g.Max(j => j.Degrees):F0})")));
        foreach (var j in _jumps.OrderByDescending(j => j.Degrees).Take(6))
            GD.Print($"    {j.Degrees:F0} deg {j.Bone} at {j.T:F2} s {j.Player}: {j.Layers}");
    }

    public override void _ExitTree() { Report(true); ReportJumps("ANIMPROBE final"); _csv?.Dispose(); }
}
