using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Havok;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;


namespace SmClient;

/// <summary>
/// The Musou crowd as drawn (design section D), a node in the networked view's game space (Z up). Each frame the enemies shown (<see cref="MusouClientState"/>)
/// and the bodies of the dead are sorted by distance from the camera and the nearest <see cref="Visible"/> drawn: the nearest within <see cref="NearMetres"/>,
/// at most <see cref="MaxSkinned"/>, as skinned skeletons from a pool (the look's own mesh and skin, the rig's clips shared by every member; beyond
/// <see cref="HalfRateMetres"/> a skeleton is posed every second frame), the rest by the look's vertex-animation tier (<see cref="MusouVat"/>, at most
/// <see cref="MaxVat"/> per look). Without a baked look (the bake failed or is not done) the crowd falls back to skinned only, at most <see cref="FallbackSkinned"/>.
/// A dead enemy plays its death clip where it fell and sinks after <see cref="SinkAfter"/> s. Every number here is ours (design section E's budgets).
/// </summary>
public partial class MusouCrowd : Node3D
{
    public const int DefaultVisible = 150, MaxSkinned = 40, MaxVat = 200, FallbackSkinned = 80;
    public const float NearMetres = 25f, HalfRateMetres = 12f, SinkAfter = 3f, SinkSeconds = 1.5f, SinkDepth = 1.2f;
    /// <summary>Bodies kept at once (ours); the oldest go first.</summary>
    public const int MaxBodies = 60;

    /// <summary>Most enemies drawn (design: 150; the Low setting 80).</summary>
    public int Visible { get; set; } = DefaultVisible;
    /// <summary>Skinned only (the fallback; also <c>--musou-no-vat</c>).</summary>
    public bool NoVat { get; set; }

    readonly MusouLooks _looks;

    sealed class Body { public MusouDeathShown Death; public float Age; public int Serial; }
    int _bodySerial;
    readonly List<Body> _bodies = new();

    /// <summary>One thing to draw this frame: an enemy or a body.</summary>
    struct Item
    {
        public int Key;            // the id, or -(body index + 1)
        public MusouEnemyType Type;
        public Vector3 Position;
        public float Yaw, Time, Sink, Distance;
        public int Clip;
    }
    readonly List<Item> _items = new();

    sealed class Skinned
    {
        public required Node3D Root;
        public required Skeleton3D Skeleton;
        public required MusouLooks.Look Look;
        public required Transform3D[] Rest;
        public int Key = int.MinValue;
        public int Clip = -1;
        public bool Used;
    }
    readonly List<Skinned> _skinned = new();
    readonly Dictionary<int, Skinned> _skinnedByKey = new();
    static HavokTransform[] _sample = Array.Empty<HavokTransform>();
    Transform3D[] _pose = Array.Empty<Transform3D>();

    sealed class Far { public required MusouLooks.Look Look; public required MultiMeshInstance3D Node; public float[] Buffer = new float[MaxVat * 16]; public int Count; }
    readonly Dictionary<MusouLooks.Look, Far> _far = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<int, float> _clocks = new();   // per id: the looping clip's time (shared by both tiers, so a switch of tier does not jump)
    readonly HashSet<int> _seen = new();
    readonly List<int> _gone = new();
    bool _odd;

    /// <summary>Drawn last frame: skinned, by the vertex-animation tier, left out (over the caps or without a look); and the bodies kept.</summary>
    public int ShownSkinned { get; private set; }
    public int ShownVat { get; private set; }
    public int LeftOut { get; private set; }
    public int Bodies => _bodies.Count;

    public MusouCrowd(MusouLooks looks) { _looks = looks; Name = "MusouCrowd"; }

    /// <summary>A death to show: the body plays its death clip where the enemy was last seen.</summary>
    public void AddBody(in MusouDeathShown death)
    {
        if (_bodies.Count >= MaxBodies) _bodies.RemoveAt(0);
        _bodies.Add(new Body { Death = death, Serial = ++_bodySerial });
    }

    /// <summary>The frame: <paramref name="shown"/> at the render tick, the camera in game space.</summary>
    public void Frame(IReadOnlyList<MusouShown> shown, Vector3 camera, float delta)
    {
        _odd = !_odd;
        // Looks whose bake finished: upload once, then the far tier can use them.
        foreach (var type in Enum.GetValues<MusouEnemyType>())
            if (_looks.For(type) is { } look)
            {
                // One bake per look, ever: a failure (nothing to bake, too large, an exception) leaves the look in the skinned tier for good.
                if (look.Vat is null && look.Baking is null && !look.BakeFailed && !NoVat)
                {
                    look.Baking = MusouVat.Start(look.Assets, look.Clips);
                    if (look.Baking is null) { look.BakeFailed = true; GD.Print($"musou: {look.Name} has nothing to bake; skinned only"); }
                }
                if (look.Baking is { IsCompleted: true } done)
                {
                    look.Baking = null;
                    look.Vat = done.IsCompletedSuccessfully ? done.Result : null;
                    if (look.Vat is { } v) { MusouVat.Upload(v); GD.Print($"musou: {look.Name} baked in {v.BakeMilliseconds:F0} ms ({v.Vertices} vertices, {v.Height} rows)"); }
                    else { look.BakeFailed = true; GD.Print($"musou: {look.Name} not baked ({done.Exception?.GetBaseException().Message ?? $"over {MusouVat.MaxRows} rows"}); skinned only"); }
                }
            }

        _items.Clear();
        _seen.Clear();
        foreach (var e in shown)
        {
            var p = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);
            int clip = ClipOf(e.State);
            _seen.Add(e.Id);
            float time;
            if (MusouLooks.Loops(clip)) { _clocks.TryGetValue(e.Id, out time); time += delta; _clocks[e.Id] = time; }
            else time = e.StateSeconds;
            _items.Add(new Item { Key = e.Id, Type = e.Type, Position = p, Yaw = e.Yaw, Time = time, Clip = clip, Distance = p.DistanceTo(camera) });
        }
        _gone.Clear();
        foreach (var id in _clocks.Keys) if (!_seen.Contains(id)) _gone.Add(id);
        foreach (var id in _gone) _clocks.Remove(id);
        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            var b = _bodies[i];
            b.Age += delta;
            if (b.Age > SinkAfter + SinkSeconds) { _bodies.RemoveAt(i); continue; }
        }
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            var p = new Vector3(b.Death.Position.X, b.Death.Position.Y, b.Death.Position.Z);
            float sink = b.Age <= SinkAfter ? 0f : (b.Age - SinkAfter) / SinkSeconds * SinkDepth;
            _items.Add(new Item { Key = -b.Serial, Type = b.Death.Type, Position = p, Yaw = b.Death.Yaw, Time = b.Age, Sink = sink,
                                  Clip = MusouLooks.Death, Distance = p.DistanceTo(camera) });
        }
        _items.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        bool anyVat = false;
        if (!NoVat) foreach (var type in Enum.GetValues<MusouEnemyType>()) if (_looks.For(type)?.Vat?.Mesh is not null) { anyVat = true; break; }
        int skinnedCap = anyVat ? MaxSkinned : FallbackSkinned;
        int visible = Math.Min(Visible, anyVat ? Visible : FallbackSkinned);

        foreach (var s in _skinned) s.Used = false;
        foreach (var f in _far.Values) f.Count = 0;
        int skinned = 0, vat = 0, left = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var look = _looks.For(it.Type);
            if (i >= visible || look is null) { left++; continue; }
            // A look without a bake (failed, or not done yet) draws skinned at any distance, up to the fallback's cap.
            bool unbaked = look.Vat?.Mesh is null;
            if (skinned < (unbaked ? FallbackSkinned : skinnedCap) && (it.Distance <= NearMetres || unbaked) && TakeSkinned(it, look, delta)) { skinned++; continue; }
            if (look.Vat?.Mesh is not null && AddFar(look, it)) { vat++; continue; }
            left++;
        }
        // Free the skinned members not used this frame (kept in the pool, hidden).
        _gone.Clear();
        foreach (var (key, s) in _skinnedByKey) if (!s.Used) { s.Root.Visible = false; s.Key = int.MinValue; _gone.Add(key); }
        foreach (var key in _gone) _skinnedByKey.Remove(key);
        foreach (var f in _far.Values)
        {
            f.Node.Multimesh.VisibleInstanceCount = f.Count;
            if (f.Count > 0) RenderingServer.MultimeshSetBuffer(f.Node.Multimesh.GetRid(), f.Buffer);
        }
        ShownSkinned = skinned; ShownVat = vat; LeftOut = left;
    }

    static int ClipOf(MusouEnemyState state) => state switch
    {
        MusouEnemyState.Seek => MusouLooks.Run,
        MusouEnemyState.WindUp or MusouEnemyState.Strike or MusouEnemyState.Recover => MusouLooks.Attack,
        MusouEnemyState.Stagger => MusouLooks.Stagger,
        MusouEnemyState.Dying => MusouLooks.Death,
        _ => MusouLooks.Idle,
    };

    /// <summary>Clip time for a non-looping clip in a state chain: the attack spans the wind-up, strike and recovery (ours: from the state's start).</summary>
    static float ClipTime(HavokBoundAnimation clip, int index, float time) =>
        MusouLooks.Loops(index) ? time % MathF.Max(clip.Animation.Duration, 1e-3f) : MathF.Min(time, clip.Animation.Duration);

    bool TakeSkinned(in Item it, MusouLooks.Look look, float delta)
    {
        if (!_skinnedByKey.TryGetValue(it.Key, out var s) || !ReferenceEquals(s.Look, look))
        {
            if (s is not null) { s.Root.Visible = false; s.Key = int.MinValue; _skinnedByKey.Remove(it.Key); }
            s = null;
            foreach (var c in _skinned) if (c.Key == int.MinValue && !c.Used && ReferenceEquals(c.Look, look)) { s = c; break; }
            if (s is null)
            {
                if (_skinned.Count >= FallbackSkinned + 8)
                {
                    // Pool full of other looks: rebuild a free one for this look.
                    Skinned? spare = null;
                    foreach (var c in _skinned) if (c.Key == int.MinValue && !c.Used) { spare = c; break; }
                    if (spare is null) return false;
                    _skinned.Remove(spare);
                    spare.Root.QueueFree();
                }
                s = Build(look);
                _skinned.Add(s);
            }
            s.Key = it.Key; s.Clip = -1;
            _skinnedByKey[it.Key] = s;
        }
        s.Used = true;
        s.Root.Visible = true;
        s.Root.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), it.Yaw).Scaled(Vector3.One * look.Scale), it.Position - new Vector3(0, 0, it.Sink));
        // Beyond 12 m every second frame, half the members on each (by the key's parity; the frames between keep the last pose); a changed clip poses at once.
        bool due = it.Clip != s.Clip || it.Distance <= HalfRateMetres || ((it.Key & 1) == 0) == _odd;
        if (!due) return true;
        s.Clip = it.Clip;
        if (look.Clips[it.Clip] is { } clip) Pose(s, clip, ClipTime(clip, it.Clip, it.Time));
        return true;
    }

    Skinned Build(MusouLooks.Look look)
    {
        var skeleton = look.Assets.Model.Rig.Skeleton;
        var root = new Node3D { Name = "Skinned" + _skinned.Count };
        var sk = new Skeleton3D { Name = "Skeleton" };
        root.AddChild(sk);
        var rest = new Transform3D[skeleton.BoneNames.Count];
        for (int i = 0; i < skeleton.BoneNames.Count; i++)
        {
            sk.AddBone(skeleton.BoneNames[i]);
            if (skeleton.Parents[i] >= 0) sk.SetBoneParent(i, skeleton.Parents[i]);
            rest[i] = MarineAssets.ToGodot(skeleton.ReferencePose[i]);
            sk.SetBoneRest(i, rest[i]);
        }
        sk.ResetBonePoses();
        foreach (var piece in look.Assets.Pieces)
        {
            var mesh = new MeshInstance3D { Name = piece.Name, Mesh = piece.Mesh, Skin = piece.Skin, Skeleton = new NodePath("..") };
            for (int i = 0; i < piece.Overrides.Count; i++) if (piece.Overrides[i] is { } m) mesh.SetSurfaceOverrideMaterial(i, m);
            sk.AddChild(mesh);
        }
        AddChild(root);
        return new Skinned { Root = root, Skeleton = sk, Look = look, Rest = rest };
    }

    void Pose(Skinned s, HavokBoundAnimation clip, float time)
    {
        if (_sample.Length != clip.Animation.TrackCount) _sample = new HavokTransform[clip.Animation.TrackCount];
        clip.Animation.SampleInto(time, _sample);
        if (_pose.Length != s.Rest.Length) _pose = new Transform3D[s.Rest.Length];
        Array.Copy(s.Rest, _pose, s.Rest.Length);
        for (int k = 0; k < _sample.Length; k++)
        {
            int bone = clip.TrackToBone[k];
            if ((uint)bone < (uint)_pose.Length) _pose[bone] = MarineAssets.ToGodot(_sample[k]);
        }
        MusouVat.LockRoot(_pose, s.Rest);
        for (int b = 0; b < _pose.Length; b++)
        {
            var t = _pose[b];
            s.Skeleton.SetBonePosePosition(b, t.Origin);
            s.Skeleton.SetBonePoseRotation(b, t.Basis.GetRotationQuaternion());
            s.Skeleton.SetBonePoseScale(b, t.Basis.Scale);
        }
    }

    bool AddFar(MusouLooks.Look look, in Item it)
    {
        if (!_far.TryGetValue(look, out var f))
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = look.Vat!.Mesh, InstanceCount = MaxVat };
            mm.VisibleInstanceCount = 0;
            var node = new MultiMeshInstance3D { Name = "Far " + look.Name, Multimesh = mm, ExtraCullMargin = 4f };
            AddChild(node);
            _far[look] = f = new Far { Look = look, Node = node };
        }
        if (f.Count >= MaxVat) return false;
        var vat = look.Vat!;
        var (r0, r1, blend) = MusouVat.FramesAt(vat.Clips[it.Clip], it.Time);
        var basis = new Basis(new Vector3(0, 0, 1), it.Yaw).Scaled(Vector3.One * look.Scale);
        var o = it.Position;
        int at = f.Count * 16;
        var buf = f.Buffer;
        buf[at] = basis.X.X; buf[at + 1] = basis.Y.X; buf[at + 2] = basis.Z.X; buf[at + 3] = o.X;
        buf[at + 4] = basis.X.Y; buf[at + 5] = basis.Y.Y; buf[at + 6] = basis.Z.Y; buf[at + 7] = o.Y;
        buf[at + 8] = basis.X.Z; buf[at + 9] = basis.Y.Z; buf[at + 10] = basis.Z.Z; buf[at + 11] = o.Z;
        buf[at + 12] = r0; buf[at + 13] = r1; buf[at + 14] = blend; buf[at + 15] = it.Sink / look.Scale;
        f.Count++;
        return true;
    }
}
