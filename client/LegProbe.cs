using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// <c>--leg-probe</c> (test aid, night batch 2026-11-06 anim/sword-legs; not original behaviour): poses the skeleton as it is drawn (no rendering needed: headless works) and, for every melee
/// strike of every drawn character, samples the feet's world positions each frame. Per (weapon, strike step) it reports, as medians over the strikes seen:
/// planted-foot slide (cm: the toe's horizontal travel summed over the frames its toe is within 3 cm of the ground and the ankle within 24 cm), the largest planted step (cm per frame), ankle jitter
/// (the second difference of the ankle in the body frame, cm per frame squared: p99 and max), the largest leg bone turn in one frame (degrees) and the share of the strike's frames the locomotion held the legs
/// (the hand-over). Read from meta <see cref="NetworkView"/> sets each frame: leg_step (0 none), leg_heavy, leg_weapon, leg_time, leg_hand. Run with the engine option <c>--fixed-fps 60</c> for equal frame times.
/// Printed once, at exit.
/// </summary>
public partial class LegProbe : Node
{
    public static readonly bool Enabled = OS.GetCmdlineUserArgs().Contains("--leg-probe");
    static LegProbe? _instance;
    public static void EnsureStarted(Node from)
    {
        if (!Enabled || _instance is not null || !GodotObject.IsInstanceValid(from)) return;
        _instance = new LegProbe { Name = "LegProbe", ProcessPriority = 1001 };
        from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        GD.Print("leg probe on: per-strike foot slide, ankle jitter and leg bone turns (printed at exit)");
    }

    sealed class Run
    {
        public string Weapon = ""; public int Step; public bool Heavy;
        public float LastTime = -1f;
        public int Frames, HandFrames, Planted;
        public float Slide, MaxStep, MaxTurn;
        public readonly List<float> Jitter = new();
        public int Have, Runs, Seq, RawFrames;
        public Vector3 Origin;
        public readonly List<Sample> Samples = new();
    }
    readonly record struct Sample(double T, Vector2 L0, Vector2 L1, Vector2 T0, Vector2 T1, bool P0, bool P1, Quaternion[] Bones);
    double _clock;
    /// <summary><c>--leg-trace</c>: one LEGTRACE line per frame of the first two strikes of each kind (ankles in the body frame, cm: sideways, forward, height).</summary>
    static readonly bool Trace = OS.GetCmdlineUserArgs().Contains("--leg-trace");
    readonly Dictionary<(string, int, bool), int> _seq = new();
    sealed class Track { public Run? Run; public int[] Toe = [], Ankle = [], Leg = []; }
    readonly Dictionary<ulong, Track> _tracks = new();
    readonly List<Run> _done = new();

    void Finish(Track t)
    {
        if (t.Run is { Frames: >= 5 } r) { Measure(r); _done.Add(r); }
        t.Run = null;
    }

    /// <summary>Resamples the strike's frames onto a 60 Hz grid (linear, bones by slerp) so the measures do not depend on the machine's frame rate, then measures slide, jitter and bone turns on the grid.</summary>
    static void Measure(Run r)
    {
        var sm = r.Samples;
        if (sm.Count < 5) return;
        double t0 = sm[0].T, t1 = sm[^1].T;
        int n = (int)((t1 - t0) * 60.0);
        if (n < 5) return;
        var grid = new List<(Vector2 L0, Vector2 L1, Vector2 T0, Vector2 T1, bool P0, bool P1, Quaternion[] B)>(n);
        int k = 0;
        for (int i = 0; i <= n; i++)
        {
            double t = t0 + i / 60.0;
            while (k + 1 < sm.Count - 1 && sm[k + 1].T < t) k++;
            var a = sm[k]; var b = sm[Math.Min(k + 1, sm.Count - 1)];
            float w = b.T > a.T ? (float)Math.Clamp((t - a.T) / (b.T - a.T), 0.0, 1.0) : 0f;
            var q = new Quaternion[a.Bones.Length];
            for (int j = 0; j < q.Length; j++) q[j] = a.Bones[j].Slerp(b.Bones[j], w);
            grid.Add((a.L0.Lerp(b.L0, w), a.L1.Lerp(b.L1, w), a.T0.Lerp(b.T0, w), a.T1.Lerp(b.T1, w), w < 0.5f ? a.P0 : b.P0, w < 0.5f ? a.P1 : b.P1, q));
        }
        r.Frames = grid.Count;
        for (int i = 1; i < grid.Count; i++)
        {
            var g0 = grid[i - 1]; var g1 = grid[i];
            if (g0.P0 && g1.P0) { float s = g0.T0.DistanceTo(g1.T0) * 100f; r.Slide += s; r.MaxStep = MathF.Max(r.MaxStep, s); r.Planted++; }
            if (g0.P1 && g1.P1) { float s = g0.T1.DistanceTo(g1.T1) * 100f; r.Slide += s; r.MaxStep = MathF.Max(r.MaxStep, s); r.Planted++; }
            for (int j = 0; j < g1.B.Length; j++)
                r.MaxTurn = MathF.Max(r.MaxTurn, Mathf.RadToDeg(2f * MathF.Acos(Math.Clamp(MathF.Abs(g1.B[j].Dot(g0.B[j])), 0f, 1f))));
            if (i >= 2)
            {
                var gm = grid[i - 2];
                r.Jitter.Add(((g1.L0 - g0.L0) - (g0.L0 - gm.L0)).Length() * 100f);
                r.Jitter.Add(((g1.L1 - g0.L1) - (g0.L1 - gm.L1)).Length() * 100f);
            }
        }
    }

    public override void _Process(double delta)
    {
        if (delta <= 0) return;
        _clock += delta;
        foreach (var c in CharacterViewer.Live)
        {
            if (!GodotObject.IsInstanceValid(c) || c.Skeleton is not { } sk || c.PoseLocked) continue;
            ulong id = c.GetInstanceId();
            if (!_tracks.TryGetValue(id, out var t)) _tracks[id] = t = new Track();
            if (t.Toe.Length == 0)
            {
                int[] ids = [sk.FindBone("RIG_L_Toe"), sk.FindBone("RIG_R_Toe"), sk.FindBone("RIG_L_Foot"), sk.FindBone("RIG_R_Foot"),
                    sk.FindBone("RIG_L_Thigh"), sk.FindBone("RIG_R_Thigh"), sk.FindBone("RIG_L_Calf"), sk.FindBone("RIG_R_Calf")];
                if (ids.Any(i => i < 0)) continue;
                t.Toe = [ids[0], ids[1]]; t.Ankle = [ids[2], ids[3]]; t.Leg = [ids[4], ids[5], ids[6], ids[7], ids[2], ids[3]];
            }
            int step = c.HasMeta("leg_step") ? (int)c.GetMeta("leg_step") : 0;
            float time = c.HasMeta("leg_time") ? (float)c.GetMeta("leg_time") : 0f;
            string weapon = c.HasMeta("leg_weapon") ? (string)c.GetMeta("leg_weapon") : "";
            bool heavy = c.HasMeta("leg_heavy") && (bool)c.GetMeta("leg_heavy");
            if (step <= 0) { Finish(t); continue; }
            if (t.Run is null || t.Run.Step != step || t.Run.Heavy != heavy || time + 0.001f < t.Run.LastTime)
            {
                Finish(t);
                var key = (weapon, step, heavy);
                int seq = _seq.GetValueOrDefault(key);
                _seq[key] = seq + 1;
                t.Run = new Run { Weapon = weapon, Step = step, Heavy = heavy, Seq = seq };
            }
            var r = t.Run; r.LastTime = time;
            var xf = sk.GlobalTransform; var cx = c.GlobalTransform;
            var fwd = new Vector2(cx.Basis.Y.X, cx.Basis.Y.Z).Normalized(); var right = new Vector2(cx.Basis.X.X, cx.Basis.X.Z).Normalized();
            var origin = cx.Origin;
            r.Frames++; r.RawFrames++;
            if (c.HasMeta("leg_hand") && (bool)c.GetMeta("leg_hand")) r.HandFrames++;
            var local = new Vector2[2]; var ankle = new Vector3[2]; var toe = new Vector3[2];
            for (int f = 0; f < 2; f++)
            {
                ankle[f] = xf * sk.GetBoneGlobalPose(t.Ankle[f]).Origin; toe[f] = xf * sk.GetBoneGlobalPose(t.Toe[f]).Origin;
                var d = new Vector2(ankle[f].X - origin.X, ankle[f].Z - origin.Z);
                local[f] = new Vector2(d.Dot(right), d.Dot(fwd));
            }
            var now = new Quaternion[t.Leg.Length];
            for (int i = 0; i < t.Leg.Length; i++) now[i] = sk.GetBonePoseRotation(t.Leg[i]);
            bool[] planted = new bool[2];
            for (int f = 0; f < 2; f++) planted[f] = toe[f].Y - origin.Y < 0.03f && ankle[f].Y - origin.Y < 0.24f;
            r.Samples.Add(new Sample(_clock, local[0], local[1], new Vector2(toe[0].X, toe[0].Z), new Vector2(toe[1].X, toe[1].Z), planted[0], planted[1], now));
            if (Trace && r.Seq < 2 && r.Have >= 1)
                GD.Print($"LEGTRACE {r.Weapon} {(heavy ? "H" : "L")}{step} #{r.Seq} f{r.Frames} t {time:F3} body {(origin - r.Origin).Length() * 100f:F1} cm/f hand {(c.HasMeta("leg_hand") && (bool)c.GetMeta("leg_hand") ? 1 : 0)} L ({local[0].X * 100f:F0},{local[0].Y * 100f:F0},{(ankle[0].Y - origin.Y) * 100f:F0}) R ({local[1].X * 100f:F0},{local[1].Y * 100f:F0},{(ankle[1].Y - origin.Y) * 100f:F0}) pins {c.ClipLockDebug()} {c.LayerDebug()}");
            r.Origin = origin; r.Have++;
        }
    }

    static float Med(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? float.NaN : s[s.Count / 2]; }
    static float Pct(List<float> v, float p) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? float.NaN : s[Math.Min(s.Count - 1, (int)(s.Count * p))]; }

    void Report(string tag)
    {
        foreach (var t in _tracks.Values) Finish(t);
        GD.Print($"{tag} per (weapon, light/heavy, strike): runs, grid frames at 60 Hz (median), hand-over share, planted-foot slide cm (median / max over runs), largest planted step cm per 1/60 s (median), ankle jitter = second difference of the ankle in the body frame, cm per (1/60 s)^2 (p99 median / max), leg bone turn deg per 1/60 s (median of max / max)");
        foreach (var g in _done.GroupBy(r => (r.Weapon, r.Step, r.Heavy)).OrderBy(g => g.Key.Weapon).ThenBy(g => g.Key.Heavy).ThenBy(g => g.Key.Step))
        {
            var rs = g.ToList();
            GD.Print($"{tag} {g.Key.Weapon,-34} {(g.Key.Heavy ? "heavy" : "light")} {g.Key.Step}: runs {rs.Count,3}, frames {Med(rs.Select(r => (float)r.Frames)):F0}, hand {Med(rs.Select(r => r.HandFrames / (float)r.RawFrames)) * 100f:F0}%, slide {Med(rs.Select(r => r.Slide)):F1} / {rs.Max(r => r.Slide):F1}, step {Med(rs.Select(r => r.MaxStep)):F1}, jitter p99 {Med(rs.Select(r => Pct(r.Jitter, 0.99f))):F1} / max {rs.Max(r => r.Jitter.Count > 0 ? r.Jitter.Max() : 0f):F1}, turn {Med(rs.Select(r => r.MaxTurn)):F0} / {rs.Max(r => r.MaxTurn):F0}");
        }
        _done.Clear(); _tracks.Clear();
    }

    public override void _ExitTree() => Report("LEGPROBE final");
}
