using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// M3 viewer: the multiplayer Space Marine rig with the basic_001 armour set, playing one original animation.
/// Skeleton, meshes and poses stay in the game's Z-up space. Standalone, the character puts them under its own node
/// rotated to Godot's Y-up; with <c>gameSpaceRoot: false</c> it expects to be placed inside a game-space parent (the
/// networked view), so its position and rotation are game-space values.
/// Options: --anim &lt;name&gt; (default SM_NAV_Run_F_01), --time &lt;seconds&gt; (freeze at that time),
/// --bones (draw the skeleton over the meshes).
/// Several characters share one <see cref="MarineAssets"/> (meshes, skins and animations loaded once).
/// Diffuse textures only; the original shaders and chapter colours are not reproduced yet.
/// </summary>
public partial class CharacterViewer : Node3D
{
    public const string DefaultAnimation = "SM_NAV_Run_F_01";

    readonly string _gameDir;
    readonly bool _gameSpaceRoot;
    string? _animationName;
    readonly float? _freezeAt;
    readonly bool _drawBones;
    MarineAssets? _assets;
    readonly string? _chaosSet;
    Skeleton3D? _skeleton;
    ImmediateMesh? _boneLines;
    HavokBoundAnimation? _animation;
    float _time;
    bool _posed;   // set by Pose: the time is driven from outside (no free-running playback) until the next Play

    /// <summary>
    /// One stance of the blended playback (<see cref="Stance"/>): a looping clip, or a pitch triple (aim down / level / aim up)
    /// weighted by the view pitch, cross-faded in and out over <see cref="StanceFadeSeconds"/>.
    /// </summary>
    sealed class Layer
    {
        public string Key = "";
        public HavokBoundAnimation? Low, Mid, High;
        public float WLow, WMid = 1f, WHigh;     // pitch weights (sum 1), applied at once
        public float Time;                       // seconds into Mid (Low and High are sampled at the same time)
        public float Rate = 1f;                  // playback speed (1 = the clip's own)
        public float Fade, Target;               // cross-fade weight now and where it is heading
        public float FadeSeconds = StanceFadeSeconds;   // time for a full fade; set by the Stance call that starts a transition
        // M40-T4: a blend layer (BlendStance) plays several clips at once at a shared normalised gait phase; Mid / Time show the dominant one.
        public BlendClip[]? Blend;               // the set's clips (null: an ordinary stance)
        public HavokBoundAnimation?[] BlendAnims = Array.Empty<HavokBoundAnimation?>();
        public float[] BlendWeights = Array.Empty<float>();   // per clip, set by each BlendStance call
        public float[] BlendFree = Array.Empty<float>();      // free-running clips' times (those without a gait)
        public float Phase, Cadence;             // shared phase (0 to 1) and its speed in cycles per second
        public int Dominant = -1;                // the clip reported as the layer's (Mid), with hysteresis
    }

    /// <summary>One clip of a <see cref="BlendSet"/>: its name (null: a slot the set leaves empty) and its gait; no gait: the clip runs freely at its own speed (the idle pose).</summary>
    public sealed record BlendClip(string? Clip, ClipGait? Gait);

    /// <summary>The clips of one blend layer (<see cref="BlendStance"/>): a key naming the layer and the clips, which the caller weights by index.</summary>
    public sealed record BlendSet(string Key, BlendClip[] Clips);
    readonly List<Layer> _layers = new();
    bool _stanceMode;                            // the stance layers drive the pose (set by Stance, cleared by Play / Pose)
    Vector3[] _accPos = Array.Empty<Vector3>(), _accScale = Array.Empty<Vector3>();
    Quaternion[] _accRot = Array.Empty<Quaternion>();   // sums of weighted (sign-aligned) quaternions, not normalised
    float[] _accWeight = Array.Empty<float>();
    static HavokTransform[] SampleBuffer = Array.Empty<HavokTransform>();   // scratch for ApplyStances' clip samples (main thread only)
    Quaternion[] _signRef = Array.Empty<Quaternion>();   // the rotations shown last by the stance blend (sign reference); refilled from the skeleton after anything else posed it
    bool _signRefValid;
    // The last pose shown before the stance layers took over (after a Play / Pose clip): it fades out as the first layer fades in.
    Vector3[]? _snapPos, _snapScale;
    Quaternion[]? _snapRot;
    bool _snapActive, _snapNoLegs;
    /// <summary>A locked clip entry's legs fade out of the old pose while the entry weight falls from 1 to this (ours: three frames of the 0.125 s fade, so no bone turns more than about 15 degrees a frame).</summary>
    public const float LockedLegKeep = 0.6f;
    // M40-T3: a clip entry (Play / Pose taking over from another clip or from the stances) fades from the pose shown at that moment
    // (the same snapshot arrays) over _entryDuration seconds of real time; the clip's own time may be driven from outside meanwhile.
    bool _entryActive;
    float _entryElapsed, _entryDuration;
    /// <summary>Cross-fade time between stances (~0.15 s: the original aim camera's blend-in time, <c>aim_sm_rifle_mp</c>; the animation blend time itself is not read from the install).</summary>
    public const float StanceFadeSeconds = 0.15f;

    /// <summary>The shared assets this character was built from (null before a standalone viewer is ready).</summary>
    public MarineAssets? Assets => _assets;
    public string Status { get; private set; } = "";
    public List<string> Notes { get; } = new();
    public Aabb Bounds { get; private set; } = new(new Vector3(-1, 0, -1), new Vector3(2, 2.2f, 2));
    /// <summary>Name of the animation playing, or null.</summary>
    public string? Playing => _animation?.Name;
    /// <summary>
    /// M26b (sounds keyed by behaviour tags): the clip that drives the pose now, its time and length (s) and whether it loops:
    /// the strongest stance layer's middle clip (looping), the clip shown by <see cref="Play"/> (looping) or by <see cref="Pose"/> (held).
    /// </summary>
    public (string Clip, float Time, float Duration, bool Looping)? ClipTime
    {
        get
        {
            if (_stanceMode)
            {
                Layer? best = null;
                foreach (var l in _layers) if (best is null || l.Target > best.Target || (l.Target == best.Target && l.Fade > best.Fade)) best = l;
                return best?.Mid is { } m ? (m.Name, best.Time, m.Animation.Duration, true) : null;
            }
            return _animation is { } a ? (a.Name, _time, a.Animation.Duration, !_posed) : null;
        }
    }
    /// <summary>One clip of a blend layer with a gait, for the tag sounds: its name, gait, length (s) and weight.</summary>
    public readonly record struct BlendClipState(string Clip, ClipGait Gait, float Duration, float Weight);

    /// <summary>
    /// M40-T4 (tag sounds): when a blend layer drives the pose (the target stance), fills <paramref name="into"/> with its clips that have a gait and a weight, and
    /// returns true with the layer's shared phase (0 to 1: <see cref="GaitPhase"/>); false (list cleared) for any other pose.
    /// </summary>
    public bool BlendState(List<BlendClipState> into, out float phase)
    {
        into.Clear();
        phase = 0f;
        if (!_stanceMode) return false;
        Layer? best = null;
        foreach (var l in _layers) if (best is null || l.Target > best.Target || (l.Target == best.Target && l.Fade > best.Fade)) best = l;
        if (best?.Blend is not { } clips) return false;
        phase = best.Phase;
        for (int i = 0; i < clips.Length; i++)
            if (clips[i].Gait is { } gait && best.BlendAnims[i] is { } anim && best.BlendWeights[i] > 1e-3f)
                into.Add(new BlendClipState(anim.Name, gait, anim.Animation.Duration, best.BlendWeights[i]));
        return true;
    }

    /// <summary>Test aid (<c>--anim-probe-csv</c>): the layers' state, "key fade/target: clip weight ...", for the layers driving the pose.</summary>
    public string LayerDebug()
    {
        if (!_stanceMode) return "-";
        return string.Join(" | ", _layers.Select(l => $"{l.Key} {l.Fade:F2}/{l.Target:F0}" + (l.Blend is null ? "" : $" cad {l.Cadence:F2} ph {l.Phase:F2}: " + string.Join(" ", Enumerable.Range(0, l.BlendWeights.Length).Where(i => l.BlendWeights[i] > 0.01f).Select(i => $"{l.Blend[i].Clip}={l.BlendWeights[i]:F2}")))));
    }

    /// <summary>The stance key last asked for with <see cref="Stance"/> while the stance layers drive the pose, else null.</summary>
    public string? StanceKey => _stanceMode ? _layers.LastOrDefault(l => l.Target > 0f)?.Key : null;

    /// <summary>Standalone viewer that loads its own assets from the install.</summary>
    public CharacterViewer(string gameDir, string? animationName, float? freezeAt, bool drawBones, string? chaosSet = null)
    {
        _chaosSet = chaosSet; _gameDir = gameDir; _animationName = animationName; _freezeAt = freezeAt; _drawBones = drawBones; _gameSpaceRoot = true;
    }

    /// <summary>A character using already loaded shared assets.</summary>
    public CharacterViewer(MarineAssets assets, string? animationName, bool gameSpaceRoot)
    {
        _gameDir = ""; _assets = assets; _animationName = animationName; _gameSpaceRoot = gameSpaceRoot;
    }

    public CharacterViewer() : this("", null, null, false) { }

    // The characters animate before the networked view's own _Process (M24): bone poses read there, e.g. the muzzle, are this frame's.
    public override void _EnterTree()
    {
        ProcessPriority = -10;
        Live.Add(this);
        PosePopProbe.EnsureStarted(this);   // --pose-pop-probe (M40-T3)
        AnimProbe.EnsureStarted(this);      // --anim-probe (M40-T1)
        TurnProbe.EnsureStarted(this);      // --anim-probe too (M40-T5)
        PoseHashProbe.EnsureStarted(this);  // --pose-hash <file> (animation pass 1)
        TransitionProbe.EnsureStarted(this); // --transition-probe (animation pass 3)
    }

    public override void _ExitTree() => Live.Remove(this);

    /// <summary><c>--no-crossfade</c> (test aid, M40-T3): clips take over at once, as before the cross-fade (before/after probe runs).</summary>
    public static readonly bool NoCrossfade = OS.GetCmdlineUserArgs().Contains("--no-crossfade");

    /// <summary>The characters in the tree (test aids, e.g. <see cref="PosePopProbe"/>).</summary>
    public static readonly List<CharacterViewer> Live = new();

    public override void _Ready()
    {
        _assets ??= MarineAssets.Load(_gameDir, Notes, _chaosSet);
        var model = _assets.Model;
        var skeleton = model.Rig.Skeleton;

        Node3D root = this;
        if (_gameSpaceRoot)
        {
            // Game space (Z up) to Godot (Y up): x → x, y → −z, z → y. A rotation, so winding is unchanged.
            root = new Node3D { Name = "GameSpace", Basis = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)) };
            AddChild(root);
        }
        _skeleton = new Skeleton3D { Name = "Skeleton" };
        root.AddChild(_skeleton);
        for (int i = 0; i < skeleton.BoneNames.Count; i++)
        {
            _skeleton.AddBone(skeleton.BoneNames[i]);
            if (skeleton.Parents[i] >= 0) _skeleton.SetBoneParent(i, skeleton.Parents[i]);
            _skeleton.SetBoneRest(i, MarineAssets.ToGodot(skeleton.ReferencePose[i]));
        }
        _skeleton.ResetBonePoses();
        AddPieces(_assets);

        _animation = _assets.Animation(_animationName ?? DefaultAnimation);
        if (_drawBones)
        {
            _boneLines = new ImmediateMesh();
            var material = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, AlbedoColor = new Color(1f, 0.85f, 0.1f) };
            _skeleton.AddChild(new MeshInstance3D { Name = "Bones", Mesh = _boneLines, MaterialOverride = material });
        }
        _time = _freezeAt ?? 0f;
        ApplyPose();

        if (!ReferenceEquals(_assets.Notes, Notes)) Notes.AddRange(_assets.Notes);
        Status = $"Multiplayer rig ({skeleton.BoneNames.Count} bones) with {_assets.Pieces.Count} armour pieces (e.g. {(_assets.Pieces.Count > 0 ? _assets.Pieces[0].Name : "none")}), " +
                 $"{_assets.TexturedSurfaces}/{_assets.Surfaces} surfaces with original diffuse texture; animation " +
                 (_animation is null ? "none" : $"'{_animation.Name}' ({_animation.Animation.Duration:F2} s{(_freezeAt is { } f ? $", frozen at {f:F2} s" : ", looping")})") +
                 $" of {model.Rig.AnimationNames.Count}. Label: original asset loaded.";
    }

    /// <summary>
    /// Switches to another animation (from its start); no change when it is already playing or cannot be loaded. The pose cross-fades
    /// from the one shown now over <paramref name="blendSeconds"/> (M40-T3; 0 = at once; <see cref="ClipBlends"/> has the graphs' values).
    /// </summary>
    public void Play(string name, float blendSeconds = ClipBlends.Default)
    {
        if (_assets is null) return;
        if (!_stanceMode && string.Equals(_animation?.Name, name, StringComparison.OrdinalIgnoreCase)) { _posed = false; return; }
        var animation = _assets.Animation(name);
        if (animation is null) return;
        LeaveStance();
        BeginEntry(blendSeconds);
        _animation = animation;
        _animationName = name;
        _time = 0f;
        _posed = false;
        ApplyPose();
    }

    /// <summary>
    /// Shows the named animation at <paramref name="time"/> seconds (clamped to its length) and holds it there: playback
    /// does not advance on its own until <see cref="Play"/> is called. Used for attacks whose time comes from the
    /// simulation. When this takes over from another clip or from the stances, the pose cross-fades from the one shown now over
    /// <paramref name="blendSeconds"/> of real time (M40-T3; 0 = at once). Returns false (nothing changes) when the animation cannot be loaded.
    /// </summary>
    public bool Pose(string name, float time, float blendSeconds = ClipBlends.Default, bool lockFeet = false)
    {
        if (_assets is null) return false;
        bool changed = _stanceMode || !string.Equals(_animation?.Name, name, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            var animation = _assets.Animation(name);
            if (animation is null) return false;
            LeaveStance();
            BeginEntry(blendSeconds);
            _animation = animation;
            _animationName = name;
            _entryFootLock = lockFeet;   // M40-T5 (CharacterViewer.FootPin.cs)
        }
        float clamped = Math.Clamp(time, 0f, _animation!.Animation.Duration);
        // anim/feel-pass: the same clip started again (a second slash, a roll in the same direction, a remote clip whose time is set back) jumped to its new time in one frame;
        // it now fades from a moving copy of the shown pose over the caller's blend, as an entry does.
        if (!changed && _posed && !NoFeel && clamped < _time - RestartBackSeconds) { BeginEntry(blendSeconds); ClipRestarts++; }
        if (!changed && _posed && clamped == _time && !_layersDirty) return true;
        _posed = true;
        _time = clamped;
        ApplyPose();
        return true;
    }

    /// <summary>
    /// Shortest cross-fade a clip entry gets (ours): a linear fade turns a bone that differs by up to 180 degrees by at most 180 / (0.125 x 60)
    /// = 24 degrees per frame at 60 fps, under the 25 degrees the M40 audit accepts. The graphs' 0.1 s ("Default 3 Frame Transition") is lengthened to it.
    /// </summary>
    public const float MinEntryBlendSeconds = 0.125f;

    /// <summary>feel/weight-impact: set around one <see cref="Pose"/> call, this entry's shortest fade instead of the usual floor (s; 0: the usual; <c>NetworkView.PoseSharp</c>).</summary>
    public float EntryFloorOnce;

    /// <summary>Starts a cross-fade from the pose shown now (before a clip takes over); no fade when <paramref name="seconds"/> is 0 or no pose was shown yet.</summary>
    void BeginEntry(float seconds)
    {
        _entryActive = false;
        EntryStamp++;
        if (NoCrossfade || !(seconds > 1e-3f) || !float.IsFinite(seconds) || _animation is null && !_stanceMode) return;
        if (!TakeSnapshot()) return;
        _snapActive = false;   // the stance blend's flag: the snapshot belongs to this entry now
        ResetEntryArcs();   // animation pass 3 (CharacterViewer.Transitions.cs)
        _entryActive = true; _entryElapsed = 0f; _entryDuration = Math.Clamp(seconds, EntryFloorOnce > 0f ? EntryFloorOnce : NoP3 ? MinEntryBlendSeconds : SmoothMinEntryBlendSeconds, 2f);
    }

    /// <summary>anim/feel-pass: a clip posed again further back than this (s) is taken as started again and faded into (ours: more than a remote clip's smoothing ever sets it back).</summary>
    public const float RestartBackSeconds = 0.25f;
    /// <summary>Test aid: clip restarts faded into (<see cref="RestartBackSeconds"/>).</summary>
    public static int ClipRestarts;

    /// <summary>Counts the clip entries (<see cref="Play"/> / <see cref="Pose"/> taking over from another clip or the stances), faded or not: <see cref="PosePopProbe"/> measures around them.</summary>
    public int EntryStamp { get; private set; }

    /// <summary>Weight of the snapshot in the pose now (1 at the start of a clip entry, 0 when it is over).</summary>
    public float EntryWeight => _entryActive && _snapRot is not null ? Math.Clamp(1f - _entryElapsed / _entryDuration, 0f, 1f) : 0f;

    /// <summary>Drops the stance layers (a clip is about to take over at once); true when they were driving the pose.</summary>
    bool LeaveStance()
    {
        bool was = _stanceMode;
        CancelSettle();   // M40-T5: foot pins belong to the stance layers
        _stanceMode = false;
        _snapActive = false;
        _layers.Clear();
        return was;
    }

    /// <summary>
    /// Shows a looping stance, cross-fading from the previous one over <see cref="StanceFadeSeconds"/> (the first one after
    /// <see cref="Play"/> / <see cref="Pose"/> or at the start shows at once). The stance is the clip <paramref name="mid"/>
    /// alone, or with <paramref name="low"/> and <paramref name="high"/> a pitch triple: <paramref name="pitchT"/> is -1
    /// (all <paramref name="low"/>), 0 (all <paramref name="mid"/>) to +1 (all <paramref name="high"/>) and takes effect
    /// at once, without a fade. <paramref name="rate"/> scales the playback speed (a walk cycle slowed or sped to the movement
    /// speed). The same <paramref name="key"/> continues the running stance (pitch and rate updated); a new one
    /// starts at the phase of the strongest running stance so a walk cycle carries on instead of restarting. Returns false
    /// (nothing changes) when a clip cannot be loaded.
    /// <paramref name="fadeSeconds"/> (default <see cref="StanceFadeSeconds"/>) is the length of the cross-fade when this call
    /// selects a stance that was not the target before (the incoming stance fades in and the outgoing ones out over that time;
    /// ignored while the same stance continues): M27 uses the behaviour graphs' relaxed / ready blend times for it.
    /// </summary>
    public bool Stance(string key, string? low, string mid, string? high, float pitchT, float rate = 1f, float fadeSeconds = StanceFadeSeconds, float startFraction = -1f)
    {
        if (_assets is null || _skeleton is null) return false;
        pitchT = Math.Clamp(float.IsFinite(pitchT) ? pitchT : 0f, -1f, 1f);
        Layer? layer = null;
        foreach (var l in _layers) if (l.Key == key) { layer = l; break; }
        if (layer is null)
        {
            var m = _assets.Animation(mid);
            if (m is null) return false;
            var lo = low is null ? null : _assets.Animation(low);
            var hi = high is null ? null : _assets.Animation(high);
            layer = new Layer { Key = key, Mid = m, Low = lo, High = hi };
            var strongest = StrongestLayer();
            if (strongest?.Mid is { } sm && sm.Animation.Duration > 1e-3f)
                layer.Time = strongest.Time / sm.Animation.Duration * m.Animation.Duration;
            if (startFraction >= 0f && float.IsFinite(startFraction)) layer.Time = (startFraction % 1f) * m.Animation.Duration;   // animation pass 3: a loop taking over at a given foot (the sprint after its start clip)
            AttachLayer(layer);
        }
        layer.Rate = float.IsFinite(rate) ? Math.Clamp(rate, 0.1f, 4f) : 1f;
        layer.WLow = layer.Low is not null ? MathF.Max(0f, -pitchT) : 0f;
        layer.WHigh = layer.High is not null ? MathF.Max(0f, pitchT) : 0f;
        layer.WMid = 1f - layer.WLow - layer.WHigh;
        SelectLayer(layer, fadeSeconds);
        _animation = layer.Mid;
        _animationName = layer.Mid!.Name;
        return true;   // the pose is applied by _Process (once per frame), after the caller's update
    }

    /// <summary>The running stance with the largest fade (the one a new stance starts at the phase of), or null when no stance drives the pose.</summary>
    Layer? StrongestLayer()
    {
        Layer? strongest = null;
        if (_stanceMode) foreach (var l in _layers) if (strongest is null || l.Fade > strongest.Fade) strongest = l;
        return strongest;
    }

    /// <summary>Adds a new stance layer: it fades in beside the running ones, or at once when none runs; coming from a clip shown by Play / Pose it fades in from that clip's last frame.</summary>
    void AttachLayer(Layer layer)
    {
        layer.Fade = _stanceMode && _layers.Count > 0 ? 0f : 1f;
        if (!_stanceMode)
        {
            _layers.Clear(); _posed = false;
            // Coming from a clip shown by Play / Pose: fade from its last frame instead of popping.
            _entryActive = false;
            if (_animation is not null && TakeSnapshot()) layer.Fade = 0f;
        }
        _layers.Add(layer);
        _stanceMode = true;
    }

    /// <summary>Makes a layer the one fading in (the others fade out); the fade time applies when it was not the target before.</summary>
    void SelectLayer(Layer layer, float fadeSeconds)
    {
        bool selected = layer.Target < 1f;
        foreach (var l in _layers) l.Target = ReferenceEquals(l, layer) ? 1f : 0f;
        if (selected)
        {
            float seconds = float.IsFinite(fadeSeconds) ? Math.Clamp(fadeSeconds, NoP3 ? 0.02f : SmoothMinStanceFadeSeconds, 2f) : StanceFadeSeconds;
            foreach (var l in _layers) l.FadeSeconds = seconds;
        }
    }

    /// <summary>
    /// M40-T4: shows a blend of several looping clips at once. The layer plays the clips of <paramref name="set"/> with the given <paramref name="weights"/>
    /// (one per clip of the set, by index; they need not sum to 1, the pose is their weighted mean); clips that have a gait share one normalised phase
    /// (<see cref="GaitPhase"/>: 0 at the left foot's plant, 0.5 at the right's) that advances by <paramref name="cyclesPerSecond"/>, each clip's time following
    /// from its own sync points, so blended walk, run and strafe clips keep the same foot on the ground; clips without a gait (the idle pose) run freely.
    /// The same set continues the running layer (weights and cadence updated, every call); a different one fades in over <paramref name="fadeSeconds"/> as
    /// <see cref="Stance"/> does and starts at the phase the strongest running blend layer has (else at the proportional time of its clip). The dominant clip
    /// (largest weight, preferring clips with a gait) is what <see cref="ClipTime"/> and <see cref="Playing"/> report. Returns false (nothing changes) when
    /// a clip with weight cannot be loaded.
    /// </summary>
    public bool BlendStance(BlendSet set, ReadOnlySpan<float> weights, float cyclesPerSecond, float fadeSeconds = StanceFadeSeconds)
    {
        if (_assets is null || _skeleton is null || weights.Length != set.Clips.Length) return false;
        Layer? layer = null;
        foreach (var l in _layers) if (l.Key == set.Key) { layer = l; break; }
        if (layer is null)
        {
            var anims = new HavokBoundAnimation?[set.Clips.Length];
            for (int i = 0; i < anims.Length; i++)
            {
                if (set.Clips[i].Clip is { } name) anims[i] = _assets.Animation(name);
                if (anims[i] is null && weights[i] > 1e-4f) return false;
            }
            int first = Array.FindIndex(anims, a => a is not null);
            if (first < 0) return false;
            layer = new Layer
            {
                Key = set.Key, Mid = anims[first], Blend = set.Clips, BlendAnims = anims,
                BlendWeights = new float[anims.Length], BlendFree = new float[anims.Length],
            };
            var strongest = StrongestLayer();
            if (strongest is { Blend: not null }) layer.Phase = strongest.Phase;
            else if (strongest?.Mid is { } sm && sm.Animation.Duration > 1e-3f)
            {
                // Coming from an ordinary stance: its position in its cycle, as a phase of the first clip with a gait.
                int g = Array.FindIndex(set.Clips, c => c.Gait is not null);
                if (g >= 0) layer.Phase = GaitPhase.PhaseAt(set.Clips[g].Gait!, strongest.Time / sm.Animation.Duration * set.Clips[g].Gait!.Duration);
            }
            _snapNoLegs = false;
            if (_seedPhase is { } seed && set.Clips.Any(c => c.Gait is not null)) { layer.Phase = seed; _snapNoLegs = true; }   // M40-T5: the run start hands over at the foot it ended on: the legs take the cycle at once
            _seedPhase = null;
            AttachLayer(layer);
        }
        for (int i = 0; i < weights.Length; i++)
            layer.BlendWeights[i] = layer.BlendAnims[i] is not null && float.IsFinite(weights[i]) ? Math.Max(0f, weights[i]) : 0f;
        layer.Cadence = float.IsFinite(cyclesPerSecond) ? Math.Clamp(cyclesPerSecond, 0f, 6f) : 0f;
        UpdateDominant(layer);
        SelectLayer(layer, fadeSeconds);
        _animation = layer.Mid;
        _animationName = layer.Mid!.Name;
        return true;
    }

    /// <summary>The time in clip <paramref name="i"/> of a blend layer now.</summary>
    static float BlendTime(Layer layer, int i) =>
        layer.Blend![i].Gait is { } gait ? GaitPhase.TimeAt(gait, layer.Phase) : layer.BlendFree[i];

    /// <summary>A blend layer's reported (dominant) clip changes only when another clip's weight is this much above it: the reported clip drives the tag sounds and must not flicker where two weigh the same.</summary>
    public const float DominantHysteresis = 0.2f;

    /// <summary>Points the layer's Mid / Time at its dominant clip (the largest weight, ties and the idle clip losing to clips with a gait; the clip shown last is kept while within <see cref="DominantHysteresis"/> of the best).</summary>
    static void UpdateDominant(Layer layer)
    {
        int best = -1;
        float bestWeight = -1f;
        for (int i = 0; i < layer.BlendWeights.Length; i++)
        {
            if (layer.BlendAnims[i] is null) continue;
            float w = layer.BlendWeights[i] + (layer.Blend![i].Gait is not null ? 1e-4f : 0f);
            if (w > bestWeight) { bestWeight = w; best = i; }
        }
        if (best < 0) return;
        int held = layer.Dominant;
        if (held >= 0 && held != best && layer.BlendAnims[held] is not null && layer.BlendWeights[held] > 1e-3f && layer.BlendWeights[held] + DominantHysteresis >= bestWeight) best = held;
        layer.Dominant = best;
        layer.Mid = layer.BlendAnims[best];
        layer.Time = BlendTime(layer, best);
    }

    /// <summary>
    /// Test aid (<c>--clip-stats</c>): the length of an original clip and how far each foot bone travels in skeleton space
    /// over it (sampled 24 times; metres per axis: x, y, z in game space) - a walk-on-the-spot cycle shows feet travel of tens of
    /// centimetres, a standing pose a few centimetres. Leaves the character posed by the last sample; null if the clip cannot be loaded.
    /// </summary>
    public string? ClipFootTravel(string name)
    {
        if (_assets is null || _skeleton is null || _assets.Animation(name) is not { } clip) return null;
        var feet = Enumerable.Range(0, _skeleton.GetBoneCount()).Where(b => _skeleton.GetBoneName(b).Contains("foot", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (feet.Length == 0) return $"{name}: {clip.Animation.Duration:F2} s, no foot bones";
        const int N = 48;
        float dt = clip.Animation.Duration / N;
        var path = feet.Select(_ => new Vector3[N]).ToArray();
        int pelvis = _skeleton.FindBone("RIG_Pelvis");
        var hips = new Vector3[N];
        for (int i = 0; i < N; i++)
        {
            Pose(name, dt * i, 0f);
            for (int f = 0; f < feet.Length; f++) path[f][i] = _skeleton.GetBoneGlobalPose(feet[f]).Origin;
            if (pelvis >= 0) hips[i] = _skeleton.GetBoneGlobalPose(pelvis).Origin;
        }
        var parts = new List<string>();
        for (int f = 0; f < feet.Length; f++)
        {
            var p = path[f];
            float x0 = p.Min(v => v.X), x1 = p.Max(v => v.X), y0 = p.Min(v => v.Y), y1 = p.Max(v => v.Y), z0 = p.Min(v => v.Z), z1 = p.Max(v => v.Z);
            // Ground speed the cycle assumes: the foot's horizontal speed while it is within 3 cm of its lowest point (planted).
            float sum = 0f; int count = 0;
            for (int i = 0; i < N; i++)
            {
                var a = p[i]; var b = p[(i + 1) % N];
                if (a.Z > z0 + 0.03f || b.Z > z0 + 0.03f) continue;
                sum += new Vector2(b.X - a.X, b.Y - a.Y).Length() / dt; count++;
            }
            parts.Add($"{_skeleton.GetBoneName(feet[f])} {x1 - x0:F2}/{y1 - y0:F2}/{z1 - z0:F2} planted {(count > 0 ? sum / count : 0f):F2} m/s ({count}/{N} frames)");
        }
        // feel pass: the pelvis's own travel over the cycle (its bob and side-to-side weight shift), skeleton space.
        string hip = pelvis < 0 ? "" : $"; pelvis travel {hips.Max(v => v.X) - hips.Min(v => v.X):F3}/{hips.Max(v => v.Y) - hips.Min(v => v.Y):F3}/{hips.Max(v => v.Z) - hips.Min(v => v.Z):F3}";
        return $"{name}: {clip.Animation.Duration:F2} s; foot travel (m, x/y/z): " + string.Join("; ", parts) + hip;
    }

    /// <summary>Names of the rig's animations that contain <paramref name="text"/> (case-insensitive); test aid.</summary>
    public IEnumerable<string> FindClips(string text) =>
        _assets?.Model.Rig.AnimationNames.Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)) ?? Enumerable.Empty<string>();

    /// <summary>Copies the skeleton's current bone poses so the next stance can fade in from them.</summary>
    bool TakeSnapshot()
    {
        if (_skeleton is null) return false;
        int n = _skeleton.GetBoneCount();
        if (_snapPos is null || _snapPos.Length != n) { _snapPos = new Vector3[n]; _snapScale = new Vector3[n]; _snapRot = new Quaternion[n]; }
        for (int b = 0; b < n; b++)
        {
            _snapPos[b] = _skeleton.GetBonePosePosition(b); _snapRot![b] = _skeleton.GetBonePoseRotation(b); _snapScale![b] = _skeleton.GetBonePoseScale(b);
        }
        SnapshotVelocities();   // animation pass 3 (CharacterViewer.Transitions.cs): the copy carries on with each bone's motion
        return _snapActive = true;
    }

    void StepStances(double delta)
    {
        float dt = (float)delta;
        StepSettle(dt);   // M40-T5 (CharacterViewer.FootPin.cs)
        foreach (var l in _layers)
        {
            l.Fade = Mathf.MoveToward(l.Fade, l.Target, dt / l.FadeSeconds);
            if (l.Blend is not null)
            {
                // M40-T4: the shared phase advances at the cadence; free clips (idle) at their own speed.
                l.Phase = GaitPhase.Wrap(l.Phase + dt * l.Cadence, 1f);
                for (int i = 0; i < l.BlendFree.Length; i++)
                    if (l.Blend[i].Gait is null && l.BlendAnims[i] is { } free) l.BlendFree[i] = (l.BlendFree[i] + dt) % Math.Max(free.Animation.Duration, 1e-3f);
                UpdateDominant(l);
                continue;
            }
            float duration = Math.Max(l.Mid!.Animation.Duration, 1e-3f);
            l.Time = (l.Time + dt * l.Rate) % duration;
        }
        _layers.RemoveAll(l => l.Fade <= 0f && l.Target <= 0f);
    }

    /// <summary>
    /// The weighted blend of the running stances' clips, bone by bone: translations and scales averaged, rotations summed
    /// (sign-aligned) and normalised. Bones no clip drives keep their last pose.
    /// </summary>
    void ApplyStances()
    {
        int n = _skeleton!.GetBoneCount();
        if (_accWeight.Length != n)
        {
            _accPos = new Vector3[n]; _accScale = new Vector3[n]; _accRot = new Quaternion[n]; _accWeight = new float[n];
        }
        Array.Clear(_accWeight);
        // M40-T4: every clip's rotation is flipped to the hemisphere of the pose shown last (q and -q are one rotation), not of whichever clip was added first, so
        // the weighted sum of rotations cannot jump when the clip that happens to come first drops out of the blend (measured: 90 degrees in a frame on the prop bones).
        if (!_signRefValid || _signRef.Length != n)
        {
            _signRef = new Quaternion[n];
            for (int b = 0; b < n; b++) _signRef[b] = _skeleton.GetBonePoseRotation(b);
            _signRefValid = true;
        }
        IkBegin();
        float blendShare = 0f;
        void Add(HavokBoundAnimation clip, float time, float weight, bool blended = false)
        {
            if (weight <= 1e-4f) return;
            // One buffer for every sample of the frame (the main thread poses the characters one after another): no allocation per clip and character.
            int tracks = clip.Animation.TrackCount;
            if (SampleBuffer.Length < tracks) SampleBuffer = new HavokTransform[tracks];
            var local = SampleBuffer;
            clip.Animation.SampleInto(Math.Clamp(time, 0f, clip.Animation.Duration), local);
            if (blended) IkAdd(clip, local, weight);   // M40-T4: the blend's mean ankle (CharacterViewer.LegIk.cs)
            for (int t = 0; t < tracks; t++)
            {
                int bone = clip.TrackToBone[t];
                if ((uint)bone >= (uint)n) continue;
                var q = new Quaternion(local[t].Rotation.X, local[t].Rotation.Y, local[t].Rotation.Z, local[t].Rotation.W).Normalized();
                if (q.Dot(_signRef[bone]) < 0f) q = -q;
                var pos = new Vector3(local[t].Translation.X, local[t].Translation.Y, local[t].Translation.Z);
                var scale = new Vector3(local[t].Scale.X, local[t].Scale.Y, local[t].Scale.Z);
                if (_accWeight[bone] <= 0f) { _accPos[bone] = pos * weight; _accScale[bone] = scale * weight; _accRot[bone] = q * weight; }
                else { _accPos[bone] += pos * weight; _accScale[bone] += scale * weight; _accRot[bone] += q * weight; }
                _accWeight[bone] += weight;
            }
        }
        float fades = 0f;
        foreach (var l in _layers) fades += Smooth(l.Fade);   // animation pass 3: the smooth blend curve
        if (_snapActive)
        {
            float w = 1f - fades;
            if (w <= 1e-4f) _snapActive = false;
            else
                for (int b = 0; b < n; b++)
                {
                    if (_snapNoLegs && IsLegBelowPelvis(b)) continue;   // M40-T5: the snapshot would carry the planted foot along with the body for the whole fade
                    SnapBone(b, out var sp, out var q);   // animation pass 3: the snapshot keeps moving (dead blending)
                    if (q.Dot(_signRef[b]) < 0f) q = -q;
                    _accPos[b] = sp * w; _accScale[b] = _snapScale![b] * w; _accRot[b] = q * w; _accWeight[b] = w;
                }
        }
        // anim/feel-pass: each layer is summed on its own and the layers (and the snapshot) are then folded together by slerp in their fade weights, so a cross-fade between two
        // stances turns each bone at an even rate along one arc. The plain weighted sum of all of them (kept with --no-feel) passes close to zero for a bone whose two poses are
        // far apart and turns it through the middle of the fade in a frame or two: the Devastator's left hand flipped up to 74 degrees a frame between the ready and relaxed runs.
        bool fold = !NoFeel && (_snapActive || _layers.Count > 1);
        if (fold) FoldBegin(n);
        foreach (var l in _layers)
        {
            if (l.Fade <= 0f) continue;
            float fade = Smooth(l.Fade);
            if (l.Blend is not null)
            {
                for (int i = 0; i < l.BlendAnims.Length; i++)
                    if (l.BlendWeights[i] > 1e-3f && l.BlendAnims[i] is { } blended) Add(blended, BlendTime(l, i), fade * l.BlendWeights[i], blended: true);
                blendShare += fade;
                if (fold) FoldLayer(n);
                continue;
            }
            if (l.Low is not null) Add(l.Low, l.Time, fade * l.WLow);
            Add(l.Mid!, l.Time, fade * l.WMid);
            if (l.High is not null) Add(l.High, l.Time, fade * l.WHigh);
            if (fold) FoldLayer(n);
        }
        if (fold) FoldEnd(n);
        for (int b = 0; b < n; b++)
        {
            float w = _accWeight[b];
            if (w <= 0f) continue;
            _skeleton.SetBonePosePosition(b, _accPos[b] / w);
            var rotation = _accRot[b].Normalized();
            _skeleton.SetBonePoseRotation(b, rotation);
            _signRef[b] = rotation;
            _skeleton.SetBonePoseScale(b, _accScale[b] / w);
        }
        if (blendShare > 0f) IkApply(Math.Min(1f, blendShare));
        ApplyTurnLayer();     // M40-T5 (CharacterViewer.Turn.cs): the turn clip on the pelvis and legs, the spine twist
        ApplySpineTwist();
        ApplyPoseLayer(stanceMode: true);
        ApplyFootPins();
        RecordApplied();   // animation pass 3 (CharacterViewer.Transitions.cs)
        DrawBones();
    }

    // anim/feel-pass: the layer fold (see ApplyStances). The accumulators hold one contributor at a time (the snapshot, then each layer's own sum); FoldLayer folds the
    // contributor into the running result: rotation by slerp, translation and scale by lerp, each by its share of the weight so far. Nothing allocates per frame.
    Vector3[] _foldPos = Array.Empty<Vector3>(), _foldScale = Array.Empty<Vector3>();
    Quaternion[] _foldRot = Array.Empty<Quaternion>();
    float[] _foldWeight = Array.Empty<float>();

    void FoldBegin(int n)
    {
        if (_foldWeight.Length != n) { _foldPos = new Vector3[n]; _foldScale = new Vector3[n]; _foldRot = new Quaternion[n]; _foldWeight = new float[n]; }
        Array.Clear(_foldWeight);
        if (_snapActive) FoldLayer(n);   // the snapshot was written into the accumulators first
    }

    void FoldLayer(int n)
    {
        for (int b = 0; b < n; b++)
        {
            float w = _accWeight[b];
            if (w <= 0f) continue;
            var pos = _accPos[b] / w; var scale = _accScale[b] / w; var rot = _accRot[b].Normalized();
            float total = _foldWeight[b] + w;
            if (_foldWeight[b] <= 0f) { _foldPos[b] = pos; _foldScale[b] = scale; _foldRot[b] = rot; }
            else
            {
                float t = w / total;
                _foldPos[b] = _foldPos[b].Lerp(pos, t); _foldScale[b] = _foldScale[b].Lerp(scale, t);
                if (rot.Dot(_foldRot[b]) < 0f) rot = -rot;
                _foldRot[b] = _foldRot[b].Slerp(rot, t).Normalized();
            }
            _foldWeight[b] = total;
            _accWeight[b] = 0f;
        }
    }

    void FoldEnd(int n)
    {
        for (int b = 0; b < n; b++)
        {
            float w = _foldWeight[b];
            if (w <= 0f) continue;
            var q = _foldRot[b];
            if (q.Dot(_signRef[b]) < 0f) q = -q;
            _accPos[b] = _foldPos[b] * w; _accScale[b] = _foldScale[b] * w; _accRot[b] = q * w; _accWeight[b] = w;
        }
    }

    // ---- extra pose layers: see CharacterViewer.Layers.cs (the stack: fall legs, landing, action overlays) ----
    bool[] _driven = Array.Empty<bool>();   // bones the clip shown by ApplyPose set this frame

    /// <summary>
    /// Draws <paramref name="overlay"/> over every armour piece (null removes it). The networked view uses it as a
    /// placeholder side tint (M15); the pieces' own original materials are unchanged.
    /// </summary>
    public void SetOverlay(Material? overlay)
    {
        Overlay = overlay;
        if (_skeleton is null) return;
        foreach (var child in _skeleton.GetChildren())
            if (child is MeshInstance3D mesh && mesh.Name != "Bones") mesh.MaterialOverlay = overlay;
    }

    /// <summary>The overlay last given to <see cref="SetOverlay"/>, or null.</summary>
    public Material? Overlay { get; private set; }

    const string ArmourMeta = "armour_piece";

    /// <summary>One mesh instance per armour piece of <paramref name="assets"/> on the skeleton, each with this look's paint as surface override materials.</summary>
    void AddPieces(MarineAssets assets)
    {
        foreach (var piece in assets.Pieces)
        {
            var instance = new MeshInstance3D { Name = piece.Name, Mesh = piece.Mesh, Skin = piece.Skin, Skeleton = new NodePath("..") };
            instance.SetMeta(ArmourMeta, true);
            // This look's paint: surface override materials on the shared mesh (null: the mesh's own material), so the GPU buffers are shared by every character.
            for (int i = 0; i < piece.Overrides.Count; i++)
                if (piece.Overrides[i] is { } material) instance.SetSurfaceOverrideMaterial(i, material);
            _skeleton!.AddChild(instance);
        }
    }

    /// <summary>
    /// Customizer T7: dresses this character in another look on the same rig without rebuilding anything else: the armour pieces are replaced, the skeleton, the pose
    /// state, the weapons and the nodes around it stay (a swap costs a few mesh instances, well under a millisecond). The hidden pieces carry over; the overlay (the placeholder tint) is dropped.
    /// False (nothing changed) when the look is for another rig or this character is not ready.
    /// </summary>
    public bool SetLook(MarineAssets assets)
    {
        if (_skeleton is null || _assets is null || !ReferenceEquals(assets.Rig, _assets.Rig)) return false;
        foreach (var child in _skeleton.GetChildren())
        {
            if (child is not MeshInstance3D mesh || !mesh.HasMeta(ArmourMeta)) continue;
            _skeleton.RemoveChild(mesh);
            mesh.Free();   // at once: a queued node would keep its name and the new pieces would be renamed (SetPieceHidden matches names)
        }
        _assets = assets;
        AddPieces(assets);
        SetOverlay(null);   // the placeholder side tint belongs to the placeholder model (the caller works the side look out again for the new one)
        foreach (var suffix in HiddenPieces.ToList()) SetPieceHidden(suffix, true);
        return true;
    }

    /// <summary>The character's skeleton (null before it is ready).</summary>
    public Skeleton3D? Skeleton => _skeleton;

    /// <summary>The weapons this character carries (M24; set up by the networked view), or null.</summary>
    public WeaponRig? Weapons { get; set; }

    /// <summary>Suffixes given to <see cref="SetPieceHidden"/> that are hidden now.</summary>
    public HashSet<string> HiddenPieces { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// wfx (owner's playtest 2026-10-30, item 23): hides or shows the armour's own backpack piece, whichever of the install's two names the set uses: <c>_backpack</c> (basic, veteran, mk4, ...) or <c>_bp</c>
    /// (bloodravens, spacewolves, the DLC sets, Chaos berserk and children). Only <c>_backpack</c> was hidden under a worn pack model, so in a <c>_bp</c> set the armour's backpack stayed inside the
    /// Devastator's weapon pack: two near-coplanar hulls, flickering along the box's side edges.
    /// </summary>
    public bool SetBackpackHidden(bool hidden) => SetPieceHidden("_backpack", hidden) | SetPieceHidden("_bp", hidden);

    /// <summary>Hides or shows the armour piece whose name ends with <paramref name="suffix"/> (e.g. "_backpack" while a worn pack model is drawn); true when a piece matched.</summary>
    public bool SetPieceHidden(string suffix, bool hidden)
    {
        if (hidden) HiddenPieces.Add(suffix); else HiddenPieces.Remove(suffix);
        bool found = false;
        if (_skeleton is null) return false;
        foreach (var child in _skeleton.GetChildren())
            if (child is MeshInstance3D mesh && mesh.Name.ToString().EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { mesh.Visible = !hidden; found = true; }
        return found;
    }

    /// <summary>
    /// True while something else drives the bones (a death ragdoll, <see cref="Ragdoll"/>): no animation is applied, and
    /// <see cref="Play"/>, <see cref="Pose"/> and <see cref="Stance"/> only record their state.
    /// </summary>
    public bool PoseLocked { get; set; }

    /// <summary>Copies every bone's current pose from another character of the same rig (a corpse starts from the last animated pose).</summary>
    public void CopyPoseFrom(CharacterViewer other)
    {
        if (_skeleton is null || other._skeleton is null) return;
        _signRefValid = false;
        int n = Math.Min(_skeleton.GetBoneCount(), other._skeleton.GetBoneCount());
        for (int b = 0; b < n; b++)
        {
            _skeleton.SetBonePosePosition(b, other._skeleton.GetBonePosePosition(b));
            _skeleton.SetBonePoseRotation(b, other._skeleton.GetBonePoseRotation(b));
            _skeleton.SetBonePoseScale(b, other._skeleton.GetBonePoseScale(b));
        }
    }

    /// <summary>Test aid (<c>--anim-probe</c>): the ticks the characters' pose work took in this frame (stances, clip samples, leg correction); the probe reads and resets it.</summary>
    public static long PoseTicksThisFrame;

    public override void _Process(double delta)
    {
        if (!AnimProbe.Enabled) { ProcessPose(delta); return; }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        ProcessPose(delta);
        PoseTicksThisFrame += System.Diagnostics.Stopwatch.GetTimestamp() - start;
    }

    void ProcessPose(double delta)
    {
        if (PoseLocked) return;
        AdvanceClock(delta);   // animation pass 3: the clock the moving snapshot's velocity and age are measured on
        AdvanceLockClock(delta);   // anim/melee-flow: the clip foot lock's release clock
        bool blending = _entryActive;   // M40-T3: one more application when the fade ends, so the pose settles on the clip
        if (blending)
        {
            _entryElapsed += (float)delta;
            if (_entryElapsed >= _entryDuration) _entryActive = false;
        }
        if (_stanceMode) { StepStances(delta); ApplyPose(); return; }
        if (_animation is null || _freezeAt is not null || (_posed && !blending && !_layersDirty)) return;
        if (!_posed) _time = (_time + (float)delta) % Math.Max(_animation.Animation.Duration, 1e-3f);
        ApplyPose();
    }

    /// <summary>Applies the current pose to the bones at once (test aids that read bone poses in the same frame).</summary>
    public void ForcePose() => ApplyPose();

    void ApplyPose()
    {
        if (_stanceMode && _skeleton is not null) { ApplyStances(); return; }
        if (_animation is null || _skeleton is null) return;
        _signRefValid = false;   // a clip poses the bones: the stance blend refills its sign reference from them
        var local = _animation.Animation.Sample(_time);
        int count = _skeleton.GetBoneCount();
        if (_driven.Length != count) _driven = new bool[count];
        else Array.Clear(_driven);
        float keep = 1f - Smooth(1f - EntryWeight);   // M40-T3: share of the snapshot of the pose this clip took over from (animation pass 3: on the smooth curve)
        for (int t = 0; t < local.Length; t++)
        {
            int bone = _animation.TrackToBone[t];
            if ((uint)bone < (uint)count) _driven[bone] = true;
            var pos = new Vector3(local[t].Translation.X, local[t].Translation.Y, local[t].Translation.Z);
            var rot = new Quaternion(local[t].Rotation.X, local[t].Rotation.Y, local[t].Rotation.Z, local[t].Rotation.W).Normalized();
            var scale = new Vector3(local[t].Scale.X, local[t].Scale.Y, local[t].Scale.Z);
            float boneKeep = _entryFootLock && IsLegBelowPelvis(bone) ? Math.Max(0f, (keep - LockedLegKeep) / (1f - LockedLegKeep)) : keep;   // M40-T5: a locked entry takes the legs from the clip in the first fraction of the fade (the planted feet are held by the IK from the first frame)
            if (boneKeep > 0f && (uint)bone < (uint)_snapRot!.Length)
            {
                SnapBone(bone, out var sp, out var sq);   // animation pass 3: the snapshot keeps moving (dead blending)
                pos = sp.Lerp(pos, 1f - boneKeep);
                rot = ContinuousSlerp(bone, sq, rot, 1f - boneKeep);   // animation pass 3: no arc flip when the clip crosses 180 degrees from the snapshot
                scale = _snapScale![bone].Lerp(scale, 1f - boneKeep);
            }
            _skeleton.SetBonePosePosition(bone, pos);
            _skeleton.SetBonePoseRotation(bone, rot);
            _skeleton.SetBonePoseScale(bone, scale);
        }
        if (_entryFootLock) { if (keep > 0f) EntryFootLock(local); else _entryFootLock = false; }
        _clipEntryKeep = keep;   // anim/melee-flow: the melee variant layer fades in with the entry (CharacterViewer.Layers.cs)
        ApplyPoseLayer(stanceMode: false);
        ApplyClipFootLock();   // anim/melee-flow (CharacterViewer.ClipFootLock.cs): the feet the clip puts down stay where they were put down
        RecordApplied();   // animation pass 3 (CharacterViewer.Transitions.cs)
        DrawBones();
    }

    /// One line per bone to its parent, in skeleton space (the lines' node is a child of the skeleton).
    void DrawBones()
    {
        if (_boneLines is null || _skeleton is null) return;
        _boneLines.ClearSurfaces();
        _boneLines.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            int parent = _skeleton.GetBoneParent(i);
            if (parent < 0) continue;
            _boneLines.SurfaceAddVertex(_skeleton.GetBoneGlobalPose(parent).Origin);
            _boneLines.SurfaceAddVertex(_skeleton.GetBoneGlobalPose(i).Origin);
        }
        _boneLines.SurfaceEnd();
    }
}
