using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// <c>--transition-probe</c> (animation pass 3 test aid, ours): measures the pose discontinuity at every switch of the shown state, per drawn character, once per rendered frame after
/// every character has been posed (run Godot with <c>--fixed-fps 60</c> on a <c>--play-demo</c> replay, so a frame is 1/60 s and two builds see the same inputs).
/// <list type="bullet">
/// <item><b>State.</b> What drives the pose this frame, read from the character alone (<see cref="Label"/>): the clip shown by <c>Pose</c> / <c>Play</c> by its kind (reaction, charge, charge end,
/// melee, jump pack, evade, throw, death, start, stop ...), or the stance blend by its key (idle, move, sprint, aim, ready ...), plus the layers laid with weight (fall legs, landing, switch).
/// A switch is a frame whose label differs from the last frame's.</item>
/// <item><b>Discontinuity.</b> Per frame and bone: the local rotation change (degrees per frame, the M40-T3 measure), the change of the bone's world position relative to the character's
/// origin (cm per frame: a body yaw that snaps shows here, a body that moves smoothly does not) and the change of that velocity (<b>jerk</b>, cm per frame per frame: a pose that jumps
/// gives a velocity spike, a fast but smooth authored move does not). The largest over the bones is kept; for every switch, the largest of each in the
/// <see cref="WindowSeconds"/> after it (the window closes early at the next switch).</item>
/// </list>
/// Printed as TPROBE lines every <see cref="ReportSeconds"/> s and at exit: per switch kind (from → to) the count and the median / p90 / max of the window's jerk and rotation; steady frames
/// (outside every window) per state for comparison. <c>--transition-probe-csv &lt;file&gt;</c> writes one row per switch.
/// </summary>
public partial class TransitionProbe : Node
{
    public const double WindowSeconds = 0.25;
    const double ReportSeconds = 30.0;
    static TransitionProbe? _instance;
    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--transition-probe");
    /// <summary>The character meta the networked view sets: true when the character's RIG_Prop_M_* bones can carry a mesh (a two-handed melee weapon, any Ork) and are measured.</summary>
    public const string TwoHandedMeta = "melee_2h";
    static StreamWriter? _csv;
    /// <summary><c>--transition-probe-trace &lt;bone&gt;</c>: prints that bone's local and character-relative positions every frame of every window (debugging aid).</summary>
    static readonly string? TraceBone = TraceArg();
    static string? TraceArg() { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, "--transition-probe-trace"); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    sealed class Track
    {
        public string Name = "", Label = "";
        public Vector3[] Pos = Array.Empty<Vector3>(), Vel = Array.Empty<Vector3>();
        public Quaternion[] Rot = Array.Empty<Quaternion>();
        public bool[] Skip = Array.Empty<bool>();
        public bool SkipTwoHanded;
        public int Have;                 // frames of history (0: none, 1: positions, 2: velocities)
        public string From = "", To = "";
        public double Left;
        public float WJerk, WRot, WPos; public string WBone = "", WRotBone = "";
        public int Frames;
    }

    sealed class Stat { public readonly List<float> Jerk = new(), Rot = new(), Pos = new(); }

    readonly Dictionary<ulong, Track> _tracks = new();
    readonly Dictionary<string, Stat> _switches = new(StringComparer.Ordinal);
    readonly Dictionary<string, Stat> _steady = new(StringComparer.Ordinal);
    double _since;

    public static void EnsureStarted(Node from)
    {
        if (!Enabled || _instance is not null || !GodotObject.IsInstanceValid(from)) return;
        _instance = new TransitionProbe { Name = "TransitionProbe", ProcessPriority = 1001 };
        from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--transition-probe-csv");
        if (i >= 0 && i + 1 < args.Length)
        {
            try { _csv = new StreamWriter(args[i + 1]) { AutoFlush = false }; _csv.WriteLine("frame,character,from,to,jerk_cm,rot_deg,pos_cm,bone,rot_bone"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { GD.PrintErr("transition probe csv: " + e.Message); }
        }
        GD.Print($"transition probe on: per state switch, the largest bone jerk (cm/frame^2), turn (deg/frame) and step (cm/frame) in the {WindowSeconds} s after it");
    }

    /// <summary>The kind of a clip shown by Pose / Play (by the original names' prefixes).</summary>
    public static string ClipKind(string clip)
    {
        string c = clip.ToUpperInvariant();
        if (c.Contains("DEATH") || c.Contains("HEADSHOT") || c.Contains("SPRINT_STAGGER")) return "death";
        if (c.Contains("BULLRUSH2IDLE") || c.Contains("SPRINT_TO_IDLE")) return "stop";
        if (c.Contains("BULLRUSH_START") || c.Contains("180TURN_TO_SPRINT")) return "sprint-start";
        if (c.StartsWith("SM_BUL_") || c.StartsWith("SM_TH_BUL_")) return "charge";
        if (c.StartsWith("SM_REC_")) return "reaction";
        if (c.StartsWith("SM_MEL_")) return "melee";
        if (c.StartsWith("SM_JMP_") || c.StartsWith("SM_DSH_")) return "jump";
        if (c.StartsWith("SM_EVD_")) return "evade";
        if (c.Contains("TOSS")) return "throw";
        if (c.Contains("BUTTON_DROP") || c.Contains("DROPDOWN") || c.Contains("_LANDING")) return "fall-clip";
        if (c.StartsWith("SM_TRN_")) return "start";
        if (c.Contains("BRACE")) return "brace";
        if (c.Contains("IDLE")) return "idle-clip";
        return "clip";
    }

    /// <summary>The kind of a stance key (the networked view's keys).</summary>
    public static string StanceKind(string key)
    {
        string k = key.ToLowerInvariant();
        if (k.StartsWith("sprint")) return "sprint";
        if (k.StartsWith("normal:")) return "idle-plain";   // the plain idle clip (no aim-offset grid): a different pose from the grid's
        if (k.StartsWith("turn:") || k.Contains("idle")) return "idle";
        if (k.StartsWith("aim") || k.Contains("focus")) return "aim";
        if (k.StartsWith("hip") || k.Contains("ready")) return "ready";
        if (k.StartsWith("brace")) return "brace";
        return "move";
    }

    /// <summary>The state label of a character this frame.</summary>
    /// <summary><c>--transition-probe-clips</c> (anim/melee-flow): melee and charge clips are labelled with their clip's name too, so a combo's step to step (melee → melee) counts as a switch.</summary>
    static readonly bool ClipLabels = OS.GetCmdlineUserArgs().Contains("--transition-probe-clips");

    public static string Label(CharacterViewer c)
    {
        if (c.PoseLocked) return "ragdoll";
        string b = c.StanceKey is { } key ? StanceKind(key) : c.Playing is { } clip ? ClipKind(clip) : "none";
        if (ClipLabels && c.StanceKey is null && c.Playing is { } named && b is "melee" or "charge") b += ":" + named;
        if (c.LayerState(PoseSlots.Land) is { Weight: > 0.5f }) b += "+land";
        else if (c.LayerState(PoseSlots.Fall) is { Weight: > 0.5f }) b += "+fall";
        if (c.LayerState(PoseSlots.Switch) is { Weight: > 0.5f }) b += "+switch";
        return b;
    }

    public override void _Process(double delta)
    {
        foreach (var character in CharacterViewer.Live)
        {
            if (!GodotObject.IsInstanceValid(character) || character.Skeleton is not { } skeleton || !character.IsVisibleInTree()) continue;
            ulong id = character.GetInstanceId();
            if (!_tracks.TryGetValue(id, out var t)) _tracks[id] = t = new Track();
            t.Name = character.Name;
            int n = skeleton.GetBoneCount();
            if (n == 0) continue;
            if (t.Pos.Length != n) { t.Pos = new Vector3[n]; t.Vel = new Vector3[n]; t.Rot = new Quaternion[n]; t.Have = 0; }
            string label = Label(character);
            if (label == "ragdoll") { t.Have = 0; t.Label = label; continue; }   // the ragdoll moves on its own
            var skel = skeleton.GlobalTransform;
            var origin = character.GlobalPosition;
            float jerk = 0f, rot = 0f, step = 0f; int jerkBone = 0, rotBone = 0;
            // qc/reference-pass: the two-handed melee prop bones (RIG_Prop_M_*) carry a mesh only for a two-handed melee weapon (thunder hammer, daemon maul): skipped otherwise.
            bool twoHanded = character.HasMeta(TwoHandedMeta) && character.GetMeta(TwoHandedMeta).AsBool();
            if (t.Skip.Length != n || t.SkipTwoHanded != twoHanded)
            {
                t.Skip = new bool[n]; t.SkipTwoHanded = twoHanded;
                for (int b = 0; b < n; b++)
                {
                    string bn = skeleton.GetBoneName(b);
                    t.Skip[b] = bn.StartsWith("RIG_Interaction", StringComparison.Ordinal) || bn == "RIG_LookAt" || (!twoHanded && bn.StartsWith("RIG_Prop_M_", StringComparison.Ordinal));
                }
            }
            for (int bone = 0; bone < n; bone++)
            {
                if (t.Skip[bone]) continue;   // helper bones without skin (the interaction alignment carries an attack's root motion, up to 9 m)
                var p =(skel * skeleton.GetBoneGlobalPose(bone)).Origin - origin;
                var q = skeleton.GetBonePoseRotation(bone);
                if (t.Have >= 1)
                {
                    var v = p - t.Pos[bone];
                    float s = v.Length() * 100f;
                    if (s > step) step = s;
                    float dot = Math.Clamp(Math.Abs(q.Dot(t.Rot[bone])), 0f, 1f);
                    float deg = Mathf.RadToDeg(2f * MathF.Acos(dot));
                    if (deg > rot) { rot = deg; rotBone = bone; }
                    if (t.Have >= 2)
                    {
                        float j = (v - t.Vel[bone]).Length() * 100f;
                        if (j > jerk) { jerk = j; jerkBone = bone; }
                    }
                    t.Vel[bone] = v;
                }
                t.Pos[bone] = p; t.Rot[bone] = q;
            }
            bool measured = t.Have >= 2;
            t.Have = Math.Min(t.Have + 1, 2);
            if (label != t.Label && t.Label.Length > 0)
            {
                Close(t);
                t.From = t.Label; t.To = label; t.Left = WindowSeconds; t.WJerk = t.WRot = t.WPos = 0f; t.WBone = t.WRotBone = "";
            }
            t.Label = label;
            if (!measured) continue;
            t.Frames++;
            if (t.Left > 0)
            {
                if (TraceBone is not null && skeleton.FindBone(TraceBone) is var tb and >= 0)
                    GD.Print($"TPTRACE f {Engine.GetProcessFrames()} {t.Name} {t.From}->{t.To} {TraceBone} local {skeleton.GetBonePosePosition(tb)} rel {t.Pos[tb]} jerk {jerk:0.0} at {skeleton.GetBoneName(jerkBone)} clip {character.Playing} stance {character.StanceKey} entry {character.EntryWeight:0.00} settling {character.Settling} drops {CharacterViewer.PinsDroppedReach}/{CharacterViewer.PinsDroppedTeleport} {character.LayerDebug()}");
                if (jerk > t.WJerk) { t.WJerk = jerk; t.WBone = skeleton.GetBoneName(jerkBone); }
                if (rot > t.WRot) { t.WRot = rot; t.WRotBone = skeleton.GetBoneName(rotBone); }
                t.WPos = MathF.Max(t.WPos, step);
                t.Left -= delta;
                if (t.Left <= 0) Close(t);
            }
            else
            {
                if (!_steady.TryGetValue(label, out var st)) _steady[label] = st = new Stat();
                st.Jerk.Add(jerk); st.Rot.Add(rot); st.Pos.Add(step);
            }
        }
        _since += delta;
        if (_since >= ReportSeconds) { _since = 0; Report("TPROBE"); }
    }

    void Close(Track t)
    {
        if (t.From.Length == 0) return;
        string key = t.From + " -> " + t.To;
        if (!_switches.TryGetValue(key, out var s)) _switches[key] = s = new Stat();
        s.Jerk.Add(t.WJerk); s.Rot.Add(t.WRot); s.Pos.Add(t.WPos);
        _csv?.WriteLine($"{Engine.GetProcessFrames()},{t.Name},{t.From},{t.To},{t.WJerk:0.0},{t.WRot:0.0},{t.WPos:0.0},{t.WBone},{t.WRotBone}");
        t.From = ""; t.Left = 0;
    }

    static float Q(List<float> v, double q)
    {
        if (v.Count == 0) return 0f;
        var sorted = v.OrderBy(x => x).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
    }

    void Report(string tag)
    {
        var all = _switches.Values.SelectMany(s => s.Jerk).ToList();
        GD.Print($"{tag} snapshots: {CharacterViewer.SnapshotsMoving} with a measured velocity, {CharacterViewer.SnapshotsFrozen} frozen");
        GD.Print($"{tag} total: {all.Count} switches, jerk median {Q(all, 0.5):0.0} p90 {Q(all, 0.9):0.0} max {(all.Count > 0 ? all.Max() : 0f):0.0} cm/f2; above 10 cm/f2: {all.Count(j => j > 10f)}");
        foreach (var (key, s) in _switches.OrderByDescending(kv => kv.Value.Jerk.Count))
            GD.Print($"{tag} switch {key}: n {s.Jerk.Count}, jerk med {Q(s.Jerk, 0.5):0.0} p90 {Q(s.Jerk, 0.9):0.0} max {s.Jerk.Max():0.0} cm/f2; turn med {Q(s.Rot, 0.5):0.0} max {s.Rot.Max():0.0} deg/f; step max {s.Pos.Max():0.0} cm/f");
        foreach (var (key, s) in _steady.OrderByDescending(kv => kv.Value.Jerk.Count))
            GD.Print($"{tag} steady {key}: {s.Jerk.Count} frames, jerk p50 {Q(s.Jerk, 0.5):0.0} p99 {Q(s.Jerk, 0.99):0.0} max {s.Jerk.Max():0.0} cm/f2; turn p99 {Q(s.Rot, 0.99):0.0} deg/f");
    }

    public override void _ExitTree()
    {
        foreach (var t in _tracks.Values) Close(t);
        Report("TPROBE final");
        _csv?.Flush(); _csv?.Dispose(); _csv = null;
    }
}
