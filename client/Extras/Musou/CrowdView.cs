using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Havok;


namespace SmClient;

/// <summary>A crowd's budgets: skinned near, vertex-animation per look, skinned without a bake, the near and half-rate distances (m), and how the dead go (s, s, m, how many are kept).</summary>
public sealed record CrowdLimits(int MaxSkinned, int MaxVat, int FallbackSkinned, float NearMetres, float HalfRateMetres, float SinkAfter, float SinkSeconds, float SinkDepth, int MaxBodies);

/// <summary>
/// What a crowd of many characters shares (TF7a, out of <see cref="MusouCrowd"/>; design sections D and E), a node in the networked view's game space (Z up). A crowd says what to draw this
/// frame (<see cref="Items"/>, plus <see cref="AddBodyItems"/>'s dead) and <see cref="Draw"/> sorts them by distance from the camera and draws the nearest <see cref="Visible"/>: the nearest within
/// the near distance, at most the skinned budget, as skinned skeletons from a pool (the look's own mesh and skin, the rig's clips shared by every member; beyond the half-rate distance a skeleton
/// is posed every second frame), the rest by the look's vertex-animation tier (<see cref="MusouVat"/>). Without a baked look (the bake failed or is not done) it falls back to skinned only.
/// A look is a <see cref="MusouLooks.Look"/>, whatever its clips mean: <see cref="LookOf"/> gives it for a type and <see cref="Loops"/> says which of its clips loop. Every number is ours.
/// </summary>
public abstract partial class CrowdView : Node3D
{
    /// <summary>The budgets of this crowd.</summary>
    protected readonly CrowdLimits Limits;

    /// <summary>Most drawn (bodies count).</summary>
    public new int Visible { get; set; }
    /// <summary>Skinned only (the fallback).</summary>
    public bool NoVat { get; set; }

    sealed class Body { public Vector3 Position; public float Yaw, Age; public int Type, Clip, Serial; }
    int _bodySerial;
    readonly List<Body> _bodies = new();

    /// <summary>One thing to draw this frame: a member or a body.</summary>
    protected struct Item
    {
        public int Key;            // a member's id (above 0), or minus a body's serial
        public int Type;
        public Vector3 Position;
        public float Yaw, Time, Sink, Distance;
        public int Clip;
    }
    /// <summary>This frame's list: the crowd fills it before <see cref="Draw"/>.</summary>
    protected readonly List<Item> Items = new();

    /// <summary>A skinned member of the pool.</summary>
    protected sealed class Skinned
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

    sealed class Far { public required MusouLooks.Look Look; public required MultiMeshInstance3D Node; public required float[] Buffer; public int Count; }
    readonly Dictionary<MusouLooks.Look, Far> _far = new(ReferenceEqualityComparer.Instance);
    readonly List<int> _unused = new();
    bool _odd;

    /// <summary>Drawn last frame: skinned, by the vertex-animation tier, left out (over the caps or without a look); and the bodies kept.</summary>
    public int ShownSkinned { get; private set; }
    public int ShownVat { get; private set; }
    public int LeftOut { get; private set; }
    public int Bodies => _bodies.Count;

    protected CrowdView(CrowdLimits limits, int visible) { Limits = limits; Visible = visible; }

    /// <summary>The types this crowd has (<see cref="Item.Type"/>).</summary>
    protected abstract IReadOnlyList<int> Types { get; }
    /// <summary>The look of a type (null: not built yet).</summary>
    protected abstract MusouLooks.Look? LookOf(int type);
    /// <summary>Whether a clip of this crowd's looks loops (otherwise it holds its last frame).</summary>
    protected abstract bool Loops(int clip);

    /// <summary>A member of this crowd has a skinned skeleton near: the place to give it its own pose. By default the item's clip at its time.</summary>
    protected virtual void PoseSkinned(Skinned s, in Item it)
    {
        if (s.Look.Clips[it.Clip] is { } clip) Pose(s, clip, ClipTime(clip, it.Clip, it.Time));
    }

    /// <summary>A body plays <paramref name="clip"/> where the member was last seen, then sinks.</summary>
    protected void AddBody(Vector3 position, float yaw, int type, int clip)
    {
        if (_bodies.Count >= Limits.MaxBodies) _bodies.RemoveAt(0);
        _bodies.Add(new Body { Position = position, Yaw = yaw, Type = type, Clip = clip, Serial = ++_bodySerial });
    }

    /// <summary>Looks whose bake finished are uploaded once, then the far tier can use them. One bake per look, ever: a failure (nothing to bake, too large, an exception) leaves the look skinned for good.</summary>
    protected void UpkeepLooks(string log)
    {
        foreach (int type in Types)
            if (LookOf(type) is { } look)
            {
                if (look.Vat is null && look.Baking is null && !look.BakeFailed && !NoVat)
                {
                    look.Baking = MusouVat.Start(look.Assets, look.Clips, Loops);
                    if (look.Baking is null) { look.BakeFailed = true; GD.Print($"{log}: {look.Name} has nothing to bake; skinned only"); }
                }
                if (look.Baking is { IsCompleted: true } done)
                {
                    look.Baking = null;
                    look.Vat = done.IsCompletedSuccessfully ? done.Result : null;
                    if (look.Vat is { } v) { MusouVat.Upload(v); GD.Print($"{log}: {look.Name} baked in {v.BakeMilliseconds:F0} ms ({v.Vertices} vertices, {v.Height} rows)"); }
                    else { look.BakeFailed = true; GD.Print($"{log}: {look.Name} not baked ({done.Exception?.GetBaseException().Message ?? $"over {MusouVat.MaxRows} rows"}); skinned only"); }
                }
            }
    }

    /// <summary>Ages the bodies and adds those left to <see cref="Items"/>.</summary>
    protected void AddBodyItems(Vector3 camera, float delta)
    {
        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            var b = _bodies[i];
            b.Age += delta;
            if (b.Age > Limits.SinkAfter + Limits.SinkSeconds) { _bodies.RemoveAt(i); continue; }
        }
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            float sink = b.Age <= Limits.SinkAfter ? 0f : (b.Age - Limits.SinkAfter) / Limits.SinkSeconds * Limits.SinkDepth;
            Items.Add(new Item { Key = -b.Serial, Type = b.Type, Position = b.Position, Yaw = b.Yaw, Time = b.Age, Sink = sink, Clip = b.Clip, Distance = b.Position.DistanceTo(camera) });
        }
    }

    /// <summary>Draws <see cref="Items"/>.</summary>
    protected void Draw(float delta)
    {
        _odd = !_odd;
        Items.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        bool anyVat = false;
        if (!NoVat) foreach (int type in Types) if (LookOf(type)?.Vat?.Mesh is not null) { anyVat = true; break; }
        int skinnedCap = anyVat ? Limits.MaxSkinned : Limits.FallbackSkinned;
        int visible = Math.Min(Visible, anyVat ? Visible : Limits.FallbackSkinned);

        foreach (var s in _skinned) s.Used = false;
        foreach (var f in _far.Values) f.Count = 0;
        int skinned = 0, vat = 0, left = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            var look = LookOf(it.Type);
            if (i >= visible || look is null) { left++; continue; }
            // A look without a bake (failed, or not done yet) draws skinned at any distance, up to the fallback's cap.
            bool unbaked = look.Vat?.Mesh is null;
            if (skinned < (unbaked ? Limits.FallbackSkinned : skinnedCap) && (it.Distance <= Limits.NearMetres || unbaked) && TakeSkinned(it, look)) { skinned++; continue; }
            if (look.Vat?.Mesh is not null && AddFar(look, it)) { vat++; continue; }
            left++;
        }
        // Free the skinned members not used this frame (kept in the pool, hidden).
        _unused.Clear();
        foreach (var (key, s) in _skinnedByKey) if (!s.Used) { s.Root.Visible = false; s.Key = int.MinValue; _unused.Add(key); }
        foreach (var key in _unused) _skinnedByKey.Remove(key);
        foreach (var f in _far.Values)
        {
            f.Node.Multimesh.VisibleInstanceCount = f.Count;
            if (f.Count > 0) RenderingServer.MultimeshSetBuffer(f.Node.Multimesh.GetRid(), f.Buffer);
        }
        ShownSkinned = skinned; ShownVat = vat; LeftOut = left;
    }

    /// <summary>The skinned member drawing a key this frame, if any.</summary>
    protected Skinned? SkinnedOf(int key) => _skinnedByKey.TryGetValue(key, out var s) && s.Used ? s : null;

    /// <summary>Clip time for a non-looping clip in a state chain: the attack spans the wind-up, strike and recovery (ours: from the state's start).</summary>
    protected float ClipTime(HavokBoundAnimation clip, int index, float time) =>
        Loops(index) ? time % MathF.Max(clip.Animation.Duration, 1e-3f) : MathF.Min(time, clip.Animation.Duration);

    bool TakeSkinned(in Item it, MusouLooks.Look look)
    {
        if (!_skinnedByKey.TryGetValue(it.Key, out var s) || !ReferenceEquals(s.Look, look))
        {
            if (s is not null) { s.Root.Visible = false; s.Key = int.MinValue; _skinnedByKey.Remove(it.Key); }
            s = null;
            foreach (var c in _skinned) if (c.Key == int.MinValue && !c.Used && ReferenceEquals(c.Look, look)) { s = c; break; }
            if (s is null)
            {
                if (_skinned.Count >= Limits.FallbackSkinned + 8)
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
        // Beyond the half-rate distance every second frame, half the members on each (by the key's parity; the frames between keep the last pose); a changed clip poses at once.
        bool due = it.Clip != s.Clip || it.Distance <= Limits.HalfRateMetres || ((it.Key & 1) == 0) == _odd;
        if (!due) return true;
        s.Clip = it.Clip;
        PoseSkinned(s, it);
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
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = look.Vat!.Mesh, InstanceCount = Limits.MaxVat };
            mm.VisibleInstanceCount = 0;
            var node = new MultiMeshInstance3D { Name = "Far " + look.Name, Multimesh = mm, ExtraCullMargin = 4f };
            AddChild(node);
            _far[look] = f = new Far { Look = look, Node = node, Buffer = new float[Limits.MaxVat * 16] };
        }
        if (f.Count >= Limits.MaxVat) return false;
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
