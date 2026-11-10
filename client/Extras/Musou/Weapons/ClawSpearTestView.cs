using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Extras.Musou;

namespace SmClient;

/// <summary>
/// Preview stage for the lightning claw's and the power spear's strike animations (added content, Musou Mode; <c>docs/claw-spear-animations.md</c>): the multiplayer marine in the
/// basic armour on a plain stage plays the keyframed strikes (<see cref="ClawSpearClips"/>) over the install's stances, with the stand-in weapons (<see cref="ClawStandIn"/>,
/// <see cref="SpearStandIn"/>) in hand, then quits. <c>--claw-spear-test --clip claw|spear|all|&lt;name,name&gt;</c> (default all), <c>--view tp|side|front</c> (the game's third-person
/// camera behind the right shoulder, the right side, the front; default tp), <c>--out &lt;dir&gt;</c> (stills; default <c>%LOCALAPPDATA%\sm-reimpl\tmp-build\claw-spear-anims</c>, never the
/// repository or the install), <c>--stills</c> (a PNG at each strike's anticipation, hit, follow-through and settle), <c>--hold &lt;clip&gt; --time &lt;s&gt;</c> (one clip frozen at a time).
/// The claw's light string chains at each strike's combo opening as the sim would; recordings: <c>godot.cmd --windowed --write-movie f.avi --fixed-fps 30 -- --claw-spear-test ...</c>.
/// Prints a <c>CLAWSPEAR</c> line per strike (stage time of its start, hit and end) and the rig's bones a clip names that it lacks. **Placeholder demonstration** around original assets read at run time.
/// </summary>
public partial class ClawSpearTestView : Node3D
{
    readonly string _gameDir;
    readonly Dictionary<string, string?> _args;
    CharacterViewer? _marine;
    WeaponStandIn? _claw, _spear;
    readonly WeaponPoseState _pose = new();
    Label? _label;
    ColorRect? _bar;
    readonly List<Segment> _segments = new();
    double _t = -0.5;   // stage time; the first half second settles the stance
    int _segment = -1;
    string _outDir = "";
    bool _stills, _quit;
    string _view = "tp";
    readonly HashSet<string> _taken = new();
    readonly List<string> _notes = new();

    /// <summary>One piece of the stage's timeline: a strike (or none: the stance alone) over a base clip, from a stage time for a length; the base clip loops freely or runs from its start.</summary>
    sealed record Segment(WeaponPoseClip? Clip, string BaseClip, bool BaseLoops, double Start, double Length, string Weapon)
    {
        public double End => Start + Length;
    }

    public ClawSpearTestView(string gameDir, Dictionary<string, string?> args) { _gameDir = gameDir; _args = args; }
    public ClawSpearTestView() : this("", new Dictionary<string, string?>()) { }

    public override void _Ready()
    {
        if (_gameDir.Length == 0) { GD.PrintErr("claw-spear test: no game folder"); GetTree().Quit(1); return; }
        _view = (_args.GetValueOrDefault("view") ?? "tp").ToLowerInvariant();
        _stills = _args.ContainsKey("stills");
        _poseLog = _args.ContainsKey("pose-log");
        _clearance = _args.ContainsKey("clearance");
        _logEvery = int.TryParse(_args.GetValueOrDefault("pose-log"), out var every) ? every : 0;
        _outDir = _args.GetValueOrDefault("out") ?? System.IO.Path.Combine(GameInstall.DataRoot, "tmp-build", "claw-spear-anims");
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        try
        {
            string full = System.IO.Path.GetFullPath(_outDir);
            if (SmFormats.InstallPaths.IsWithin(full, repoRoot) || SmFormats.InstallPaths.IsWithin(full, _gameDir)) { GD.PrintErr($"claw-spear test: {_outDir} is inside the repository or the game folder; stills refused"); _stills = false; }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { GD.PrintErr($"claw-spear test: bad --out: {ex.Message}"); _stills = false; }

        var assets = MarineAssets.Load(_gameDir, _notes);
        _marine = new CharacterViewer(assets, ClawSpearClips.ClawIdle, gameSpaceRoot: true) { Name = "Marine" };
        AddChild(_marine);
        _marine.WeaponPose = _pose;
        BuildTimeline();
        // The pistol in the left hand (the claw keeps it, design D2) and the jump pack worn (the Assault's), both the install's own models.
        try
        {
            var weaponNotes = new List<string>();
            var weapons = WeaponAssets.Load(_gameDir, assets, ["sm_mp_pistol_bolt_pistol", "sm_mp_abil_jumppack"], weaponNotes);
            var rig = new WeaponRig(_marine.Skeleton!, weapons);
            rig.Configure((0, 0, 0, "claw-spear", null), null, "sm_mp_pistol_bolt_pistol", null, ["sm_mp_abil_jumppack"], alwaysMelee: false, null, null);
            rig.Update(SmSim.WeaponSlot.Pistol, meleeOut: true, alive: true, 0f, 0.6f);
            _marine.Weapons = rig;
            _marine.SetBackpackHidden(rig.WornCount > 0);
            _notes.AddRange(weaponNotes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _notes.Add("weapons: " + ex.Message); }
        if (_marine.Skeleton is { } sk)
        {
            _claw = new ClawStandIn { Name = "Claw" }; _spear = new SpearStandIn { Name = "Spear" };
            sk.AddChild(_claw); sk.AddChild(_spear);
        }
        AddStage();
        foreach (var n in _notes.Distinct()) GD.Print("  note: " + n);
        GD.Print($"claw-spear test: view {_view}, {_segments.Count} segments, {_segments.Sum(s => s.Length):0.0} s; stills {(_stills ? _outDir : "off")}");
        foreach (var s in _segments.Where(s => s.Clip is not null))
            GD.Print($"CLAWSPEAR {s.Clip!.Name,-15} start {s.Start,6:0.000} hit {(float.IsNaN(s.Clip.HitTime) ? "   -  " : (s.Start + s.Clip.HitTime).ToString("0.000", CultureInfo.InvariantCulture)),6} end {s.End,6:0.000} ({s.Clip.Source})");
    }

    void BuildTimeline()
    {
        string want = (_args.GetValueOrDefault("clip") ?? "all").ToLowerInvariant();
        if (_args.GetValueOrDefault("hold") is { } hold && ClawSpearClips.ByName(hold) is { } held)
        {
            float at = float.TryParse(_args.GetValueOrDefault("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ht) ? ht : held.HitTime;
            _segments.Add(new Segment(held, held.BaseClip, held.Weapon == "claw" || held.BaseClip == ClawSpearClips.SpearIdle, 0.0, 600.0, held.Weapon));
            _t = at;
            _holdAt = at;
            return;
        }
        var clawString = new[] { ClawSpearClips.ClawX1, ClawSpearClips.ClawX2, ClawSpearClips.ClawX3, ClawSpearClips.ClawX4 };
        var spearString = new[] { ClawSpearClips.SpearHew1, ClawSpearClips.SpearHew2, ClawSpearClips.SpearHew3 };
        var spearStunChain = new[] { ClawSpearClips.SpearStun, ClawSpearClips.SpearStab };
        double t = 0.0;
        // The spear's idle carries its upright rest pose (owner, 2026-11-10); the claw's idle is the stance alone.
        void Idle(string baseClip, double seconds, string weapon) { _segments.Add(new Segment(weapon == "spear" ? ClawSpearClips.SpearRest : null, baseClip, true, t, seconds, weapon)); t += seconds; }
        void Chain(IEnumerable<WeaponPoseClip> clips, string baseClip, string weapon)
        {
            foreach (var c in clips)
            {
                double length = float.IsNaN(c.NextAt) ? c.Duration : c.NextAt;
                _segments.Add(new Segment(c, baseClip, true, t, length, weapon));
                t += length;
            }
        }
        void Claw()
        {
            Idle(ClawSpearClips.ClawIdle, 0.8, "claw");
            Chain(clawString, ClawSpearClips.ClawIdle, "claw");
            Idle(ClawSpearClips.ClawIdle, 1.0, "claw");
            Chain([ClawSpearClips.ClawHeavy], ClawSpearClips.ClawIdle, "claw");
            Idle(ClawSpearClips.ClawIdle, 1.0, "claw");
        }
        void Spear()
        {
            Idle(ClawSpearClips.SpearIdle, 0.8, "spear");
            Chain(spearString, ClawSpearClips.SpearIdle, "spear");
            Idle(ClawSpearClips.SpearIdle, 1.0, "spear");
            Chain(spearStunChain, ClawSpearClips.SpearIdle, "spear");   // F, F: the stun strike chains to the stab
            Idle(ClawSpearClips.SpearIdle, 1.0, "spear");
            _segments.Add(new Segment(ClawSpearClips.SpearDive, ClawSpearClips.SpearDiveLoop, false, t, 1.1, "spear")); t += 1.1;
            _segments.Add(new Segment(ClawSpearClips.SpearImpale, ClawSpearClips.SpearDiveImpact, false, t, ClawSpearClips.SpearImpale.Duration, "spear")); t += ClawSpearClips.SpearImpale.Duration;
            Idle(ClawSpearClips.SpearIdle, 1.0, "spear");
        }
        switch (want)
        {
            case "claw": Claw(); break;
            case "spear": Spear(); break;
            case "all": Claw(); Spear(); break;
            default:
                foreach (var name in want.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ClawSpearClips.ByName(name) is not { } c) { GD.PrintErr($"claw-spear test: no clip '{name}' (have {string.Join(", ", ClawSpearClips.All.Select(x => x.Name))})"); continue; }
                    Idle(c.BaseClip == ClawSpearClips.ClawIdle ? ClawSpearClips.ClawIdle : ClawSpearClips.SpearIdle, 0.8, c.Weapon);
                    bool loops = c.BaseClip is ClawSpearClips.ClawIdle or ClawSpearClips.SpearIdle;
                    _segments.Add(new Segment(c, c.BaseClip, loops, t, c.Duration, c.Weapon)); t += c.Duration;
                    Idle(c.BaseClip == ClawSpearClips.ClawIdle ? ClawSpearClips.ClawIdle : ClawSpearClips.SpearIdle, 1.0, c.Weapon);
                }
                break;
        }
        if (_segments.Count == 0) Idle(ClawSpearClips.ClawIdle, 2.0, "claw");
    }

    double? _holdAt;

    public override void _Process(double delta)
    {
        if (_marine is null || _quit) return;
        if (_holdAt is null) _t += delta;
        _pose.Advance((float)delta);
        var seg = _segments.FirstOrDefault(s => _t >= s.Start && _t < s.End) ?? (_t < 0 ? _segments[0] : null);
        if (seg is null)
        {
            foreach (var (name, worst) in _clearanceWorst.OrderBy(k => k.Value.Result.Clearance))
                GD.Print(string.Create(CultureInfo.InvariantCulture, $"CLEARANCE {name,-14} least {worst.Result.Clearance,6:0.000} m ({worst.Result.Part} by the {worst.Result.SpearPiece}, t {worst.Time:0.000}; {worst.Where})"));
            GD.Print("CLAWSPEAR done");
            _quit = true;
            GetTree().Quit(0);
            return;
        }
        int index = _segments.IndexOf(seg);
        if (index != _segment) { _segment = index; _taken.Clear(); }
        double local = Math.Max(0.0, _t - seg.Start);
        float baseTime = seg.BaseLoops ? (float)(_t % 1.5) : (float)local;   // the idles are 1.5 s loops; a dive clip runs from the segment's start
        _pose.Show(seg.Clip, (float)local);   // before the base pose is applied: Pose() lays the overlay in the same call
        _marine.Pose(seg.BaseClip, baseTime, ClipBlends.Default);
        FaceCamera();
        if (_pendingStill is { } pending) { _pendingStill = null; SaveStill(pending.Path, pending.Local); }   // the frame rendered with the moment's pose is the one before this _Process
        if (_poseLog && (_logEvery <= 0 || (_logFrames++ % _logEvery) == 0)) LogPose(seg, (float)local);
        bool claw = seg.Weapon == "claw";
        if (_claw is not null) { _claw.Visible = claw; if (claw) _claw.Follow(_marine.Skeleton!); }
        if (_spear is SpearStandIn spear)
        {
            spear.Visible = !claw;
            if (!claw)
            {
                // The grip tracks fade from the clip before like the pose (as the match does, NetworkView.SpearGripOf), so a hand-over never jumps the shaft onto the other hand.
                float grip = seg.Clip?.GripAt((float)local) ?? 1f, point = seg.Clip?.GripPointAt((float)local) ?? WeaponPoseClip.LowGrip;
                if (_pose.From is { } from && _pose.FromShare is var share and > 0f) { grip += (from.GripAt(_pose.FromTime) - grip) * share; point += (from.GripPointAt(_pose.FromTime) - point) * share; }
                spear.LeftGrip = grip;
                spear.RightHandFromButt = point;
                spear.Follow(_marine.Skeleton!);
                if (_clearance && seg.Clip is { } measured) MeasureClearance(measured, (float)local, spear);
            }
        }
        float glow = 0f;
        if (seg.Clip is { } c && !float.IsNaN(c.HitTime))
        {
            float since = (float)local - c.HitTime;
            glow = since < -0.12f ? 0f : since < 0f ? (0.12f + since) / 0.12f * 0.6f : since < 0.25f ? 1f - since / 0.25f : 0f;
        }
        if (_claw is not null) _claw.Glow = glow;
        if (_spear is not null) _spear.Glow = glow;
        if (_label is not null)
        {
            string hit = seg.Clip is { } cc && !float.IsNaN(cc.HitTime) && local >= cc.HitTime && local < cc.HitTime + 0.1 ? "   HIT" : "";
            _label.Text = seg.Clip is { } cl && !float.IsNaN(cl.HitTime) ? $"{cl.Name}   t = {local:0.000} s   (hit {cl.HitTime:0.000} s, length {cl.Duration:0.000} s){hit}"
                : seg.Clip is { } held ? $"{held.Name}   t = {local:0.000} s   (held pose)" : $"{(claw ? "claw" : "spear")} stance";
            if (_bar is not null) _bar.Size = new Vector2(seg.Clip is { } cb ? 600f * (float)Math.Clamp(local / cb.Duration, 0, 1) : 0f, 6f);
        }
        if (_stills && seg.Clip is { } clip && (!ReferenceEquals(clip, ClawSpearClips.SpearRest) || local >= 0.5)) TakeStills(clip, (float)local);
    }

    void TakeStills(WeaponPoseClip clip, float local)
    {
        (string, float)[] moments = float.IsNaN(clip.HitTime)
            ? [("pose", 0.5f)]
            : [("1-anticipation", clip.Anticipation), ("2-hit", clip.HitTime), ("3-follow", clip.Follow), ("4-settle", clip.Settle)];
        foreach (var (name, at) in moments)
        {
            if (local < at || _taken.Contains(name)) continue;
            _taken.Add(name);
            _pendingStill = (System.IO.Path.Combine(_outDir, $"{clip.Name}_{name}_{_view}.png"), local);
        }
    }

    (string Path, float Local)? _pendingStill;

    /// <summary>
    /// The install's clips do not all face the same way in skeleton space (the hammer's landing faces the other way from its dive loop), so the marine is turned each frame to keep the
    /// hips' front on the stage's +X, where the cameras expect it; the overlay never moves the legs, so the hips read the stance alone.
    /// </summary>
    void FaceCamera()
    {
        // The stance's front as the overlay read it from the hips before it moved the pelvis (third pass: the pelvis turns with the strike; the stage must not turn the marine with it).
        var front = _marine!.WeaponPoseBaseFront;   // skeleton space (Z up); the yaw about the up axis is the same under the game-space root
        float yaw = MathF.Atan2(front.Y, front.X);   // the front's angle from skeleton +X toward +Y (Godot -Z under the game-space root)
        _marine.Rotation = new Vector3(0f, -yaw, 0f);   // Godot's Y rotation: positive turns +X toward -Z, so the opposite turn brings the front back to +X
    }

    void SaveStill(string path, float local)
    {
        System.IO.Directory.CreateDirectory(_outDir);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(err == Error.Ok ? $"screenshot -> {path} (t {local:0.000})" : $"screenshot failed: {err}");
    }

    bool _poseLog, _clearance;
    int _logEvery, _logFrames, _clearanceFrames;
    readonly Dictionary<string, (float Time, SpearClearanceResult Result, string Where)> _clearanceWorst = new();

    /// <summary>
    /// <c>--clearance</c>: the spear against the body's capsules with this stage's own overlay (<see cref="SpearClearance"/>, the measurement the clearance test makes over its mirror of the
    /// overlay, <c>SpearClearanceTests</c>): the least clearance per clip at the run's end (<c>CLEARANCE</c> lines), and with <c>--hold</c> the held frame's clearance and the hands' places.
    /// </summary>
    void MeasureClearance(WeaponPoseClip clip, float local, SpearStandIn spear)
    {
        var s = _marine!.Skeleton!;
        static System.Numerics.Vector3 N(Vector3 v) => new(v.X, v.Y, v.Z);
        (System.Numerics.Vector3, System.Numerics.Quaternion)? Bone(string name)
        {
            int b = s.FindBone(name);
            if (b < 0) return null;
            var g = s.GetBoneGlobalPose(b);
            var q = g.Basis.Orthonormalized().GetRotationQuaternion();
            return (N(g.Origin), new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W));
        }
        if (!_marine.WeaponPoseHands(out var right, out var left)) return;
        var (butt, shaft, edge) = SpearHold.Place(N(right), N(left), spear.LeftGrip, spear.RightHandFromButt, N(_marine.WeaponPoseSpearFront()), new System.Numerics.Vector3(0f, 0f, 1f));
        var r = SpearClearance.Measure(Bone, butt, shaft, edge);
        if (!_clearanceWorst.TryGetValue(clip.Name, out var worst) || r.Clearance < worst.Result.Clearance)
            _clearanceWorst[clip.Name] = (local, r, string.Create(CultureInfo.InvariantCulture, $"stage t {_t:0.000}, grip {spear.LeftGrip:0.00} at {spear.RightHandFromButt:0.00} m, fading from {_pose.From?.Name ?? "-"} ({_pose.FromShare:0.00})"));
        if (_holdAt is not null && _clearanceFrames++ == 3)
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"CLEARANCE hold {clip.Name} t {local:0.000}: {r.Clearance:0.000} m ({r.Part} by the {r.SpearPiece}) | R hand {right.X:0.000} {right.Y:0.000} {right.Z:0.000} | L hand {left.X:0.000} {left.Y:0.000} {left.Z:0.000} | butt {butt.X:0.000} {butt.Y:0.000} {butt.Z:0.000} | shaft {shaft.X:0.000} {shaft.Y:0.000} {shaft.Z:0.000}"));
    }

    /// <summary>
    /// <c>--pose-log [every n frames]</c>: the right and left arm segments' directions in the stance's frame (elevation above the horizontal, azimuth from the front toward the character's right, degrees)
    /// and the right hand's place from the chest (front, right, up, m): the numbers the keys are authored against.
    /// </summary>
    void LogPose(Segment seg, float local)
    {
        var s = _marine!.Skeleton!;
        int lu = s.FindBone("RIG_L_UpperArm"), ru = s.FindBone("RIG_R_UpperArm");
        if (lu < 0 || ru < 0) return;
        var across = s.GetBoneGlobalPose(ru).Origin - s.GetBoneGlobalPose(lu).Origin; across.Z = 0f;
        var right = across.Normalized(); var up = new Vector3(0f, 0f, 1f); var front = up.Cross(right).Normalized();
        var chest = (s.GetBoneGlobalPose(ru).Origin + s.GetBoneGlobalPose(lu).Origin) * 0.5f;
        string Seg(string a, string b)
        {
            int ia = s.FindBone(a), ib = s.FindBone(b);
            if (ia < 0 || ib < 0) return $"{a}:?";
            var d = (s.GetBoneGlobalPose(ib).Origin - s.GetBoneGlobalPose(ia).Origin).Normalized();
            float el = MathF.Asin(Math.Clamp(d.Dot(up), -1f, 1f)) * 180f / MathF.PI, az = MathF.Atan2(d.Dot(right), d.Dot(front)) * 180f / MathF.PI;
            return $"{a[4..]} el {el,5:0} az {az,5:0}";
        }
        string Place(string bone)
        {
            int ib = s.FindBone(bone);
            if (ib < 0) return "?";
            var p = s.GetBoneGlobalPose(ib).Origin - chest;
            return $"f {p.Dot(front):0.00} r {p.Dot(right):0.00} u {p.Dot(up):0.00}";
        }
        GD.Print($"POSE {seg.Clip?.Name ?? seg.BaseClip,-14} t {local,6:0.000} | {Seg("RIG_R_UpperArm", "RIG_R_Forearm")} | {Seg("RIG_R_Forearm", "RIG_R_Hand")} | R hand {Place("RIG_R_Hand")} | {Seg("RIG_L_UpperArm", "RIG_L_Forearm")} | {Seg("RIG_L_Forearm", "RIG_L_Hand")} | L hand {Place("RIG_L_Hand")} | {Seg("RIG_SpineA", "RIG_SpineD")} | front {front.X:0.00},{front.Y:0.00}");
    }

    void AddStage()
    {
        var floor = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.21f, 0.22f), Roughness = 0.9f };
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) }, MaterialOverride = floor });
        var ringMat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.5f, 0.55f), Roughness = 0.8f };
        foreach (float radius in new[] { 2f, 4f })   // reach rings on the floor, 2 and 4 m from the feet
            AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius - 0.012f, OuterRadius = radius + 0.012f, Rings = 96, RingSegments = 6 }, Position = new Vector3(0f, 0.005f, 0f), MaterialOverride = ringMat });
        var post = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.5f) };
        AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.1f, 2f, 0.1f) }, Position = new Vector3(2.0f, 1f, -2.4f), MaterialOverride = post });   // 2 m for scale, ahead and to his left
        // The multiplayer clips face the skeleton's +X (Godot +X here; the Librarian's cast pose measured the same), the character's right is Godot +Z.
        // Cameras: the game's third person behind the right shoulder, the right side, the front.
        var camera = new Camera3D { Fov = 45f, Near = 0.05f, Far = 200f, Current = true };
        AddChild(camera);
        Vector3 f = new(1f, 0f, 0f), r = new(0f, 0f, 1f), u = Vector3.Up;
        Vector3 at, look;
        switch (_view)
        {
            case "side": at = r * 6.4f + u * 1.5f + f * 0.3f; look = u * 1.2f + f * 0.3f; break;
            case "front": at = f * 6.4f + u * 1.5f - r * 0.3f; look = u * 1.2f; break;
            default: at = -f * 4.4f + r * 1.4f + u * 2.3f; look = f * 1.2f + u * 1.2f + r * 0.2f; break;
        }
        camera.Position = at;
        camera.LookAt(look, Vector3.Up);
        AddChild(new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true, RotationDegrees = new Vector3(-42f, 210f, 0f) });
        AddChild(new DirectionalLight3D { LightEnergy = 0.4f, RotationDegrees = new Vector3(-25f, 40f, 0f) });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.16f, 0.18f, 0.21f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.57f, 0.6f), AmbientLightEnergy = 0.7f,
                GlowEnabled = true, GlowIntensity = 0.5f, GlowBloom = 0.05f,
            },
        });
        var layer = new CanvasLayer();
        AddChild(layer);
        _label = new Label { Position = new Vector2(24f, 18f) };
        _label.AddThemeFontSizeOverride("font_size", 26);
        layer.AddChild(_label);
        _bar = new ColorRect { Position = new Vector2(24f, 56f), Size = new Vector2(0f, 6f), Color = new Color(0.4f, 0.7f, 1f) };
        layer.AddChild(_bar);
    }
}
