using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// The turn-in-place, stop and start measurements of the M40-T5 work (audit D8 and D9; test aid, ours). Started with <c>--anim-probe</c> (the same flag as
/// <see cref="AnimProbe"/>, by the first <see cref="CharacterViewer"/> that enters the tree); run Godot with <c>--fixed-fps 60</c> on a <c>--play-demo</c> replay
/// of a <c>--bot-turn-sweep</c> recording. Once per rendered frame, after every character is posed, per drawn marine:
/// <list type="bullet">
/// <item><b>Body yaw per frame.</b> The change of the model's facing since the previous frame (degrees), over all frames, over the frames where the marine stands
/// (drawn speed under <see cref="StandSpeed"/>) and over those where it moves; the audit's acceptance is at most 15 degrees per frame. Respawns and teleports
/// (a jump of the position over a metre in a frame) are skipped.</item>
/// <item><b>Stops.</b> The body was moving at <see cref="StopFrom"/> m/s or more in the last <see cref="StopLook"/> s and now stands (under <see cref="StopTo"/> m/s): for the next
/// <see cref="EventSeconds"/> s the probe adds up, per foot, how far the foot slides while it is planted (flat: toe within 2 cm and ankle within 24 cm of the
/// ground, released at 3 / 27 cm, as <see cref="AnimProbe"/>), how far its toe travels in all (steps included) and its largest distance from where it was at
/// the stop. The audit's acceptance is under 10 cm of foot travel after a stop: reported as the planted slide (a viewer sees skating) and, stricter, the whole
/// toe travel.</item>
/// <item><b>Starts.</b> The same for a marine that was standing (under <see cref="StartFrom"/> m/s) and now moves at <see cref="StartTo"/> m/s or more, over <see cref="StartSeconds"/> s.</item>
/// </list>
/// Printed every <see cref="ReportSeconds"/> s and at exit as <c>TURNPROBE</c> lines. A marine's first <see cref="WarmUpFrames"/> frames are skipped.
/// </summary>
public partial class TurnProbe : Node
{
    public const float StandSpeed = 0.5f, StopFrom = 3f, StopTo = 0.3f, StartTo = 0.6f, StartFrom = 0.3f;
    public const double StopLook = 0.6, EventSeconds = 1.2, StartSeconds = 0.8, WindowSeconds = 0.1;
    public const int WarmUpFrames = 90;
    /// <summary><c>--turn-probe-trace &lt;seconds&gt;</c>: prints the feet frame by frame for two seconds from that time.</summary>
    static readonly double TraceFrom = ArgDouble("--turn-probe-trace");

    static double ArgDouble(string name)
    {
        var a = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length && double.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
    }
    const double ReportSeconds = 30.0;
    static TurnProbe? _instance;

    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--anim-probe") || OS.GetCmdlineUserArgs().Contains("--turn-probe");

    public static void EnsureStarted(Node from)
    {
        if (!Enabled || _instance is not null || !GodotObject.IsInstanceValid(from)) return;
        _instance = new TurnProbe { Name = "TurnProbe", ProcessPriority = 1001 };
        from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        GD.Print("turn probe on: body yaw per frame (standing / moving), foot slide and travel after stops and starts");
    }

    sealed class FootEvent
    {
        public bool Planted; public Vector2 Last, AtStart;
        public float Slide, Travel, MaxFromStart, PlantedStep, WorstStep;
    }

    sealed class Event
    {
        public required string Kind;     // "stop after run", "stop after sprint", "start" (reclassified at its end: "start forward" within 45 degrees of the facing, "start other")
        public Vector2 StartPos, StartForward;
        public double Start, Length;
        public float Peak;
        public FootEvent[] Feet = [new(), new()];
        public bool Armed;               // the start positions are taken on the first frame
        public double SettledAt = double.NaN;
        public string Who = "";   // the toes within 3 cm of the final spot for 0.2 s (stops)
        public readonly List<Vector2>[] ToeTrail = [new(), new()];
    }

    sealed class Track
    {
        public int[] Toes = [], Ankles = [];
        public int ClavL = -1, ClavR = -1, ThighL = -1, ThighR = -1;
        public readonly List<(double T, Vector2 P)> Positions = new();
        public int Frames;
        public float PrevYaw; public bool HaveYaw; public Vector2 PrevPos;
        public readonly List<(double T, float Speed)> Speeds = new();
        public bool WasStanding = true;
        public float BodyRate;
        public double LastFast = double.NegativeInfinity; public float LastPeak;
        public Event? Active;
        public Vector2[] PrevToe = new Vector2[2]; public bool HavePrevToe;
    }

    readonly Dictionary<ulong, Track> _tracks = new();
    readonly List<float> _yawAll = new(), _yawStand = new(), _yawMove = new();
    readonly List<(float Step, double T, string Who, bool Standing)> _worst = new();
    // Standing marines: the chest heading against the aim and the pelvis against the body, by the angle between the aim and the body (degrees); planted-foot speed while the body turns.
    readonly List<float>[] _chestErr = [new(), new(), new(), new()], _pelvisErr = [new(), new(), new(), new()];
    readonly List<float> _turnSlide = new(), _turnSlideAll = new();
    readonly List<float>[] _turnSlideByRate = [new(), new(), new()];   // body yaw rate 30-100, 100-200, over 200 degrees/s
    int _turningFrames, _debugPrints;
    readonly List<Event> _done = new();
    double _clock, _since;

    /// <summary>The game-space yaw of a direction given as the Godot x and z of the model's +Y axis ... (x, z) of Godot = (x, -y) of game: yaw 0 faces +Y.</summary>
    static float GameYaw(float godotX, float godotZ) => MathF.Atan2(-godotX, -godotZ);

    static float Percentile(List<float> v, float p) { if (v.Count == 0) return float.NaN; var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(s.Count * p))]; }
    static float Median(List<float> v) => Percentile(v, 0.5f);

    public override void _Process(double delta)
    {
        if (delta <= 0) return;
        _clock += delta;
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
                track.ClavL = skeleton.FindBone("RIG_L_Clavicle"); track.ClavR = skeleton.FindBone("RIG_R_Clavicle");
                track.ThighL = skeleton.FindBone("RIG_L_Thigh"); track.ThighR = skeleton.FindBone("RIG_R_Thigh");
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
        track.Frames++;
        track.Positions.Add((_clock, here));
        while (track.Positions.Count > 2 && _clock - track.Positions[1].T >= WindowSeconds) track.Positions.RemoveAt(0);
        var basis = character.GlobalTransform.Basis;
        float yaw = MathF.Atan2(basis.Y.X, basis.Y.Z);
        float speed = 0f;
        if (track.Positions.Count >= 2 && _clock - track.Positions[0].T > 0.04) speed = (here - track.Positions[0].P).Length() / (float)(_clock - track.Positions[0].T);
        bool teleport = track.Frames > 1 && (here - track.PrevPos).Length() > 1.0f;
        var forward2 = new Vector2(basis.Y.X, basis.Y.Z).Normalized();
        bool standing = speed < StandSpeed;
        if (track.HaveYaw && track.Frames > WarmUpFrames && !teleport)
        {
            float step = MathF.Abs(Mathf.RadToDeg(Mathf.AngleDifference(track.PrevYaw, yaw)));
            _yawAll.Add(step);
            (standing ? _yawStand : _yawMove).Add(step);
            if (step > 6f) { _worst.Add((step, _clock, character.Name, standing)); if (_worst.Count > 600) _worst.RemoveRange(0, 300); }
        }
        float bodyRate = track.HaveYaw && delta > 0 ? MathF.Abs(Mathf.RadToDeg(Mathf.AngleDifference(track.PrevYaw, yaw))) / (float)delta : 0f;
        if (standing && track.Frames > WarmUpFrames && !teleport && character.HasMeta("aim_yaw") && track.ClavL >= 0 && track.ClavR >= 0 && track.ThighL >= 0 && track.ThighR >= 0)
        {
            float aim = (float)character.GetMeta("aim_yaw");
            float bodyGame = GameYaw(basis.Y.X, basis.Y.Z);
            var gt = skeleton.GlobalTransform;
            Vector3 P(int bone) => gt * skeleton.GetBoneGlobalPose(bone).Origin;
            var shoulders = P(track.ClavR) - P(track.ClavL); var hips = P(track.ThighR) - P(track.ThighL);
            // Right vector in game space (x, -z of Godot) turned 90 degrees counter-clockwise is the forward direction.
            float chestYaw = GameYaw(shoulders.Z, -shoulders.X), pelvisYaw = GameYaw(hips.Z, -hips.X);
            float twist = MathF.Abs(Mathf.RadToDeg(Mathf.AngleDifference(bodyGame, aim)));
            if (_debugPrints < 5 && track.Frames % 200 == 0) { _debugPrints++; GD.Print($"TURNPROBE debug {character.Name}: char basis Y {basis.Y}, X {basis.X}, origin {origin}; L clav {P(track.ClavL)}, R clav {P(track.ClavR)}; shoulders {shoulders}; hips {hips}; bodyGame {Mathf.RadToDeg(bodyGame):F1}, chest {Mathf.RadToDeg(chestYaw):F1}, pelvis {Mathf.RadToDeg(pelvisYaw):F1}, aim {Mathf.RadToDeg(aim):F1}"); }
            int bucket = twist < 30f ? 0 : twist < 60f ? 1 : twist < 90f ? 2 : 3;
            _chestErr[bucket].Add(MathF.Abs(Mathf.RadToDeg(Mathf.AngleDifference(chestYaw, aim))));
            _pelvisErr[bucket].Add(MathF.Abs(Mathf.RadToDeg(Mathf.AngleDifference(pelvisYaw, bodyGame))));
            if (bodyRate > 30f) _turningFrames++;
        }
        track.PrevYaw = yaw; track.HaveYaw = true; track.PrevPos = here;
        track.BodyRate = bodyRate;
        if (teleport) { track.Active = null; track.LastFast = double.NegativeInfinity; }

        if (track.Frames <= WarmUpFrames) return;
        // Event detection.
        if (speed >= StopFrom)
        {
            if (_clock - track.LastFast >= StopLook) track.LastPeak = 0f;
            track.LastFast = _clock; track.LastPeak = MathF.Max(track.LastPeak, speed);
        }
        bool stopped = speed < StopTo;
        if (track.Active is null)
        {
            if (stopped && _clock - track.LastFast < StopLook && track.LastFast > double.NegativeInfinity)
                track.Active = new Event { Kind = track.LastPeak >= 8f ? "stop after sprint" : "stop after run", Start = _clock, Length = EventSeconds, Peak = track.LastPeak, Who = character.Name };
            else if (speed >= StartTo && track.WasStanding)
                track.Active = new Event { Kind = "start", Start = _clock, Length = StartSeconds, Peak = speed, Who = character.Name, StartPos = here, StartForward = forward2 };
        }
        if (speed < StartFrom) track.WasStanding = true; else if (speed >= StartTo) track.WasStanding = false;   // hysteresis: a start is the rise from standing to StartTo
        if (speed >= StopFrom && track.Active is { Kind: not "start" } && track.Active.Armed) { track.Active = null; }   // moved off again before the window ended: no clean stop

        // Foot tracking.
        var toeNow = new Vector2[2];
        if (TraceFrom >= 0 && _clock >= TraceFrom && _clock < TraceFrom + 2.0)
        {
            var tr = new System.Text.StringBuilder($"TURNPROBE trace {_clock:F3} speed {speed:F2} settling {character.Settling}");
            for (int f = 0; f < 2; f++)
            {
                var toe = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(track.Toes[f]).Origin;
                var ankle = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(track.Ankles[f]).Origin;
                float moved = track.HavePrevToe ? (new Vector2(toe.X, toe.Z) - track.PrevToe[f]).Length() * 100f : 0f;
                tr.Append($" | {(f == 0 ? "L" : "R")} toe h {(toe.Y - origin.Y) * 100f:F1} ankle h {(ankle.Y - origin.Y) * 100f:F1} moved {moved:F1} cm toe xz {toe.X:F3},{toe.Z:F3}");
            }
            GD.Print(tr.ToString() + $" | stance {character.StanceKey} playing {character.Playing}");
        }
        for (int f = 0; f < 2; f++)
        {
            var toe = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(track.Toes[f]).Origin;
            toeNow[f] = new Vector2(toe.X, toe.Z);
            float toeHeight = toe.Y - origin.Y;
            float ankleHeight = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(track.Ankles[f]).Origin).Y - origin.Y;
            if (track.HavePrevToe && standing && !teleport && ankleHeight < AnimProbe.AnkleEnter && toeHeight < AnimProbe.ToeEnter)
            {
                float toeSpeed = (toeNow[f] - track.PrevToe[f]).Length() / (float)delta;
                _turnSlideAll.Add(toeSpeed);
                if (track.BodyRate > 30f) { _turnSlide.Add(toeSpeed); _turnSlideByRate[track.BodyRate < 100f ? 0 : track.BodyRate < 200f ? 1 : 2].Add(toeSpeed); }
            }
            if (track.Active is { } ev)
            {
                var foot = ev.Feet[f];
                if (!ev.Armed) { foot.AtStart = toeNow[f]; foot.Last = toeNow[f]; }
                else
                {
                    float d = (toeNow[f] - track.PrevToe[f]).Length();
                    foot.Travel += d;
                    foot.MaxFromStart = MathF.Max(foot.MaxFromStart, (toeNow[f] - foot.AtStart).Length());
                    bool planted = foot.Planted ? ankleHeight < AnimProbe.AnkleExit && toeHeight < AnimProbe.ToeExit : ankleHeight < AnimProbe.AnkleEnter && toeHeight < AnimProbe.ToeEnter;
                    if (planted && foot.Planted) { foot.Slide += d; foot.PlantedStep += d; }
                    else if (planted) { foot.PlantedStep = 0f; }
                    if (!planted && foot.Planted) foot.WorstStep = MathF.Max(foot.WorstStep, foot.PlantedStep);
                    foot.Planted = planted;
                    foot.Last = toeNow[f];
                }
                ev.ToeTrail[f].Add(toeNow[f]);
            }
        }
        if (track.Active is { } e2)
        {
            if (!e2.Armed) e2.Armed = true;
            else if (_clock - e2.Start >= e2.Length)
            {
                foreach (var foot in e2.Feet) if (foot.Planted) foot.WorstStep = MathF.Max(foot.WorstStep, foot.PlantedStep);
                // Settled: the first time both toes stay within 3 cm of where they end for 0.2 s (stops only).
                if (e2.Kind != "start")
                {
                    int n = e2.ToeTrail[0].Count, window = (int)(0.2 / delta);
                    var finalToe = new[] { e2.ToeTrail[0][^1], e2.ToeTrail[1][^1] };
                    for (int i = n - 1; i >= 0; i--)
                    {
                        if ((e2.ToeTrail[0][i] - finalToe[0]).Length() > 0.03f || (e2.ToeTrail[1][i] - finalToe[1]).Length() > 0.03f) { e2.SettledAt = (i + 1) * delta; break; }
                        if (i == 0) e2.SettledAt = 0;
                    }
                    _ = window;
                }
                if (e2.Kind == "start")
                {
                    var travel = here - e2.StartPos;
                    e2.Kind = travel.Length() > 0.5f && travel.Normalized().Dot(e2.StartForward) >= MathF.Cos(Mathf.DegToRad(45f)) ? "start forward" : "start other";
                }
                _done.Add(e2);
                track.Active = null;
            }
        }
        track.PrevToe = toeNow; track.HavePrevToe = true;
    }

    void Report(bool final)
    {
        string tag = final ? "TURNPROBE final" : "TURNPROBE";
        string Line(string name, List<float> v) => v.Count == 0 ? $"{name}: no frames" :
            $"{name}: {v.Count} frames, max {v.Max():F1}, p99.9 {Percentile(v, 0.999f):F1}, p99 {Percentile(v, 0.99f):F1}, mean {v.Average():F2}, above 15 deg: {v.Count(x => x > 15f)}, above 6 deg: {v.Count(x => x > 6f)}";
        GD.Print($"{tag} body yaw step per rendered frame (degrees; acceptance <= 15): {Line("all", _yawAll)}; {Line("standing (<0.5 m/s)", _yawStand)}; {Line("moving", _yawMove)}");
        if (_worst.Count > 0)
            GD.Print($"{tag}  largest body yaw steps: " + string.Join(", ", _worst.OrderByDescending(w => w.Step).Take(6).Select(w => $"{w.Step:F1} deg at {w.T:F2} s {w.Who} ({(w.Standing ? "standing" : "moving")})")));
        string[] bucketNames = ["0-30", "30-60", "60-90", ">90"];
        GD.Print($"{tag} standing, chest heading against the aim / pelvis heading against the body (degrees, absolute, by the angle between the aim and the body): " +
                 string.Join("; ", Enumerable.Range(0, 4).Select(i => $"{bucketNames[i]} deg ({_chestErr[i].Count} frames): chest median {Median(_chestErr[i]):F1} p90 {Percentile(_chestErr[i], 0.9f):F1} max {(_chestErr[i].Count > 0 ? _chestErr[i].Max() : float.NaN):F1}, pelvis median {Median(_pelvisErr[i]):F1} p90 {Percentile(_pelvisErr[i], 0.9f):F1}")));
        GD.Print($"{tag} flat planted toe speed while standing (m/s): {_turnSlideAll.Count} frames, median {Median(_turnSlideAll):F3} p90 {Percentile(_turnSlideAll, 0.9f):F3}; while the body turns faster than 30 deg/s ({_turningFrames} frames): {_turnSlide.Count} planted frames, median {Median(_turnSlide):F3} p90 {Percentile(_turnSlide, 0.9f):F3} max {(_turnSlide.Count > 0 ? _turnSlide.Max() : float.NaN):F2}");
        GD.Print($"{tag} flat planted toe speed by body yaw rate (m/s): " + string.Join("; ", new[] { "30-100", "100-200", "over 200" }.Select((name, i) => $"{name} deg/s: {_turnSlideByRate[i].Count} frames, median {Median(_turnSlideByRate[i]):F3} p90 {Percentile(_turnSlideByRate[i], 0.9f):F3}")));
        foreach (var kind in new[] { "stop after run", "stop after sprint", "start forward", "start other" })
        {
            var events = _done.Where(e => e.Kind == kind).ToList();
            if (events.Count == 0) { GD.Print($"{tag} {kind}: no events"); continue; }
            var slide = events.Select(e => e.Feet.Max(f => f.Slide) * 100f).ToList();            // worst foot's planted slide, cm
            var slideSum = events.Select(e => e.Feet.Sum(f => f.Slide) * 100f).ToList();
            var travel = events.Select(e => e.Feet.Max(f => f.Travel) * 100f).ToList();
            var farthest = events.Select(e => e.Feet.Max(f => f.MaxFromStart) * 100f).ToList();
            var step = events.Select(e => e.Feet.Max(f => f.WorstStep) * 100f).ToList();
            if (OS.GetCmdlineUserArgs().Contains("--turn-probe-events"))
                foreach (var e in events)
                    GD.Print($"{tag}   {kind} at {e.Start:F2} s: slide L {e.Feet[0].Slide * 100f:F1} R {e.Feet[1].Slide * 100f:F1} cm (longest stretch {e.Feet[0].WorstStep * 100f:F1} / {e.Feet[1].WorstStep * 100f:F1}), toe travel {e.Feet[0].Travel * 100f:F1} / {e.Feet[1].Travel * 100f:F1} cm, from {e.Peak:F1} m/s");
            string settle = kind.StartsWith("start") ? "" : $"; settled (toes within 3 cm of the final spot) after median {Median(events.Select(e => (float)e.SettledAt).ToList()):F2} s";
            GD.Print($"{tag} {kind} ({events.Count} events, peak speed median {Median(events.Select(e => e.Peak).ToList()):F1} m/s, window {(kind.StartsWith("start") ? StartSeconds : EventSeconds)} s): " +
                     $"planted-foot slide of the worse foot (cm) median {Median(slide):F1} p90 {Percentile(slide, 0.9f):F1} max {slide.Max():F1}; both feet summed median {Median(slideSum):F1}; " +
                     $"longest single planted stretch median {Median(step):F1} max {step.Max():F1}; whole toe travel of the worse foot median {Median(travel):F1} p90 {Percentile(travel, 0.9f):F1} max {travel.Max():F1}; " +
                     $"toe farthest from where it was at the event median {Median(farthest):F1} max {farthest.Max():F1}{settle}");
        }
    }

    public override void _ExitTree() => Report(true);
}
