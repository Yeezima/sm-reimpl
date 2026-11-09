using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Havok;


namespace SmClient;

/// <summary>
/// A crowd's budgets: skinned near, vertex-animation per look, skinned without a bake, the near and half-rate distances (m), and how the dead go (s, s, m, how many are kept; the corpse floor,
/// 2026-11-09: past <c>MaxBodies</c> only bodies past <see cref="SmNet.Extras.Musou.MusouCorpseRules.FloorSeconds"/> go, and the pool grows up to <c>HardBodies</c>).
/// </summary>
public sealed record CrowdLimits(int MaxSkinned, int MaxVat, int FallbackSkinned, float NearMetres, float HalfRateMetres, float SinkAfter, float SinkSeconds, float SinkDepth, int MaxBodies, int HardBodies = 0);

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

    sealed class Body { public Vector3 Position, Velocity, Spin; public Quaternion Turn = Quaternion.Identity; public float Yaw, Age, Flight, Ground; public int Type, Clip, Serial; public bool Flung, Landed, Resting; }
    /// <summary>Bodies flung by a blast and still moving or lying.</summary>
    public int Flung { get { int n = 0; foreach (var b in _bodies) if (b.Flung) n++; return n; } }
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
        public Quaternion Turn;    // a thrown body's tumble on top of its yaw (all zero: none)
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

    /// <summary>
    /// One batch of the vertex-animation tier: a look's members in one cell of <see cref="CellMetres"/> (musou/perf-pass, ours). One batch per look over the whole crowd had one box
    /// spanning the field, so the renderer drew every far member in every view and every shadow cascade; a cell's box (<see cref="MultiMesh.CustomAabb"/>) lets the frustum and the
    /// cascades leave out the cells they do not see. <c>--musou-crowd-one-batch</c> (test aid) restores one batch per look.
    /// </summary>
    sealed class Far
    {
        public required MusouLooks.Look Look; public required MultiMeshInstance3D Node; public float[] Buffer = Array.Empty<float>(); public int Count, Capacity;
        public long Cell; public float Low = float.MaxValue, High = float.MinValue; public bool Boxed;
    }
    readonly Dictionary<(MusouLooks.Look Look, long Cell), Far> _far = new();
    readonly Dictionary<MusouLooks.Look, int> _farPerLook = new(ReferenceEqualityComparer.Instance), _farBodiesPerLook = new(ReferenceEqualityComparer.Instance);
    /// <summary>The side of a far batch's cell (m, ours) and the margin round it a member may reach (a body's size, a big look's scale).</summary>
    public const float CellMetres = 32f, CellMargin = 4f;
    /// <summary>
    /// crowd-smooth (2026-11-08, ours): a member drawn skinned stays skinned until this much past <see cref="CrowdLimits.NearMetres"/>, so one walking along the line does not swap between the
    /// skinned pose and the baked one frame by frame. <see cref="OldTierSwitch"/> (client <c>--musou-old-crowd-motion</c>): no hold.
    /// </summary>
    public const float SkinnedHoldMetres = 3f;
    /// <summary>Test aid: the tier switch before crowd-smooth (no <see cref="SkinnedHoldMetres"/>).</summary>
    public bool OldTierSwitch { get; set; }
    /// <summary>Test aid: <c>--musou-crowd-one-batch</c>, one far batch per look over the whole crowd (before musou/perf-pass).</summary>
    public static bool OneBatch { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-crowd-one-batch") >= 0;
    /// <summary>Far batches with members this frame (the profile's).</summary>
    public int FarBatches { get; private set; }
    readonly List<int> _unused = new();
    bool _odd;

    /// <summary>Drawn last frame: skinned, by the vertex-animation tier, left out (over the caps or without a look); and the bodies kept.</summary>
    public int ShownSkinned { get; private set; }
    public int ShownVat { get; private set; }
    public int LeftOut { get; private set; }
    public int Bodies => _bodies.Count;
    /// <summary>The most bodies kept at once, and the bodies dropped younger than the corpse floor (the hard ceiling was full), since the start.</summary>
    public int BodiesMost { get; private set; }
    public int FloorBroken { get; private set; }
    /// <summary>
    /// Test aid: <c>--musou-old-corpses</c>, the bodies before the corpse floor (the owner, 2026-11-09): the oldest first out once <see cref="CrowdLimits.MaxBodies"/> are kept, and drawn within
    /// <see cref="Visible"/> and the look's vertex-animation cap like any member. Now a body is drawn past both (its own room) and none goes before the floor.
    /// </summary>
    public static bool OldCorpses { get; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-old-corpses") >= 0;

    /// <summary>Test aid (<c>--musou-death-spike-test</c>): the worst ms since the last <see cref="Timing"/> of the bodies' step, the drawing, and skinned members built.</summary>
    double _stepWorst, _drawWorst; int _builtSince;
    public string Timing() { var s = $"bodies {_stepWorst:F1}, draw {_drawWorst:F1}, built {_builtSince}"; _stepWorst = _drawWorst = 0; _builtSince = 0; return s; }

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

    /// <summary>
    /// A body plays <paramref name="clip"/> where the member was last seen, then sinks. <paramref name="from"/> (a member's key, or 0): the skinned skeleton drawing that key passes
    /// to the body (night batch 2026-11-06: a death that waited was drawn dying under the member's own key; its body keeps that skeleton instead of taking, or building, another).
    /// </summary>
    protected void AddBody(Vector3 position, float yaw, int type, int clip, float age = 0f, int from = 0)
    {
        MakeBodyRoom();
        _bodies.Add(new Body { Position = position, Yaw = yaw, Type = type, Clip = clip, Serial = ++_bodySerial, Age = age });
        KeepSkinned(from);
    }

    /// <summary>A body thrown by a blast: it leaves with <paramref name="velocity"/> (m/s, game space) tipping over about the one axis of <paramref name="spin"/> (rad/s, game space; <see cref="SmNet.Extras.Musou.MusouFling.Tumble"/>), plays its clip in the air, falls under gravity in one arc, stops at the walls (<see cref="SolidBetween"/>), lands without a bounce, rolls out of its tumble and slides to rest. It starts to sink only once it rests.</summary>
    protected void AddFlungBody(Vector3 position, float yaw, int type, int clip, Vector3 velocity, Vector3 spin, int from = 0)
    {
        MakeBodyRoom();
        _bodies.Add(new Body { Position = position, Yaw = yaw, Type = type, Clip = clip, Serial = ++_bodySerial, Flung = true, Velocity = velocity, Spin = spin, Ground = position.Z });
        KeepSkinned(from);
    }

    /// <summary>Room for one more body (the corpse floor, <see cref="SmNet.Extras.Musou.MusouCorpseRules.MakeRoom"/>): a body past the floor goes, or none and the pool grows.</summary>
    void MakeBodyRoom()
    {
        int drop = SmNet.Extras.Musou.MusouCorpseRules.MakeRoom(_bodies.Count, _ageOf ??= i => _bodies[i].Age + _bodies[i].Flight, Limits.MaxBodies, Limits.HardBodies, OldCorpses, out bool broken);
        if (drop >= 0) _bodies.RemoveAt(drop);
        if (broken) FloorBroken++;
        BodiesMost = Math.Max(BodiesMost, _bodies.Count + 1);
    }
    Func<int, float>? _ageOf;

    /// <summary>The skeleton drawing <paramref name="from"/> (0: none) passes to the body just added.</summary>
    void KeepSkinned(int from)
    {
        if (from != 0 && !_skinnedByKey.ContainsKey(-_bodySerial) && _skinnedByKey.Remove(from, out var s)) { s.Key = -_bodySerial; _skinnedByKey[-_bodySerial] = s; }
    }

    /// <summary>The distance to the first solid thing on the way from one point to another (null: clear, or no collision to ask). Set by the view.</summary>
    public Func<Vector3, Vector3, float?>? SolidBetween { get; set; }

    /// <summary>
    /// The ground's slide, the height a body turns about, how fast it rolls out of its tumble once down, and the most seconds in the air (ours, spec "Flung corpses"). Corpse arcs (the owner,
    /// 2026-11-08): no bounce (it lands and stays down), a wall stops the flat travel instead of throwing it back, and the air's gravity and tumble are <see cref="SmNet.Extras.Musou.MusouFling"/>'s.
    /// </summary>
    const float FlungSlide = 4f, FlungPivot = 0.9f, FlungRollOut = 6f, FlungMaxFlight = 8f;

    void StepFlung(Body b, float dt)
    {
        b.Flight += dt;
        var v = b.Velocity;
        var p = b.Position;
        bool air = p.Z > b.Ground + 0.001f || v.Z > 0f;
        if (air) v.Z -= SmNet.Extras.Musou.MusouFling.Gravity * dt;   // MusouFling.Fly: the flat velocity is not touched in the air
        p += v * dt;
        if (SolidBetween is { } solid)
        {
            var chest = b.Position + new Vector3(0f, 0f, FlungPivot);
            var along = new Vector3(v.X, v.Y, 0f) * dt;
            if (along.LengthSquared() > 1e-6f && solid(chest, chest + along) is not null) { v.X = 0f; v.Y = 0f; p.X = b.Position.X; p.Y = b.Position.Y; }   // a wall: stops, then drops straight down
            if (v.Z > 0f && solid(chest, chest + new Vector3(0f, 0f, v.Z * dt + 1f)) is not null) v.Z = 0f;   // a ceiling
            // The floor under the next place, wherever the body has gone: up a step, or none near (off a ledge, or high in the air) and it keeps falling.
            float top = MathF.Max(b.Position.Z, p.Z) + 0.25f, bottom = MathF.Min(b.Position.Z, p.Z) - 1.5f;
            b.Ground = solid(new Vector3(p.X, p.Y, top), new Vector3(p.X, p.Y, bottom)) is { } drop ? top - drop : bottom;
        }
        if (p.Z <= b.Ground)
        {
            p.Z = b.Ground;
            v.Z = 0f;   // corpse arcs: no bounce
            b.Landed = true;
            float keep = Mathf.Max(0f, 1f - FlungSlide * dt);   // sliding on the floor
            v.X *= keep; v.Y *= keep;
        }
        // One fixed axis, the angle from the flight time (no integrated spin to drift into a corkscrew); musou/perf-pass: normalised, as Godot's quaternion product throws on a turn "not normalized".
        if (b.Landed) b.Turn = b.Turn.Slerp(Quaternion.Identity, Mathf.Min(1f, FlungRollOut * dt)).Normalized();
        else if (b.Spin.LengthSquared() > 1e-6f) b.Turn = new Quaternion(b.Spin.Normalized(), SmNet.Extras.Musou.MusouFling.TumbleAngle(new System.Numerics.Vector3(b.Spin.X, b.Spin.Y, b.Spin.Z), b.Flight)).Normalized();
        b.Position = p; b.Velocity = v;
        b.Resting = (b.Landed && p.Z <= b.Ground + 0.01f && v.LengthSquared() < 0.05f) || b.Flight >= FlungMaxFlight;
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
        ulong started = ThreadClock.Mark();
        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            var b = _bodies[i];
            if (b.Flung && !b.Resting) StepFlung(b, delta);
            else b.Age += delta;
            if (b.Age > Limits.SinkAfter + Limits.SinkSeconds) { _bodies.RemoveAt(i); continue; }
        }
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            float sink = b.Age <= Limits.SinkAfter ? 0f : (b.Age - Limits.SinkAfter) / Limits.SinkSeconds * Limits.SinkDepth;
            var at = b.Position;
            if (b.Flung) at += new Vector3(0f, 0f, FlungPivot) - b.Turn * new Vector3(0f, 0f, FlungPivot);   // tumbling about the chest, not the feet
            Items.Add(new Item { Key = -b.Serial, Type = b.Type, Position = at, Yaw = b.Yaw, Time = b.Age + b.Flight, Sink = sink, Clip = b.Clip, Distance = b.Position.DistanceTo(camera), Turn = b.Flung ? b.Turn : default });
        }
        _stepWorst = Math.Max(_stepWorst, ThreadClock.Ms(started));
    }

    /// <summary>Draws <see cref="Items"/>.</summary>
    protected void Draw(float delta)
    {
        ulong started = ThreadClock.Mark();
        _odd = !_odd;
        Items.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        bool anyVat = false;
        if (!NoVat) foreach (int type in Types) if (LookOf(type)?.Vat?.Mesh is not null) { anyVat = true; break; }
        int skinnedCap = anyVat ? Limits.MaxSkinned : Limits.FallbackSkinned;
        int visible = Math.Min(Visible, anyVat ? Visible : Limits.FallbackSkinned);

        foreach (var s in _skinned) s.Used = false;
        foreach (var f in _far.Values) f.Count = 0;
        _farPerLook.Clear(); _farBodiesPerLook.Clear();
        int skinned = 0, vat = 0, left = 0, members = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            var look = LookOf(it.Type);
            // The corpse floor (2026-11-09): a body (a negative key) is drawn whatever the members' count; Visible counts the living (and the dying) alone. --musou-old-corpses: every item counts.
            bool body = it.Key < 0 && !OldCorpses;
            if (look is null || (body ? false : (OldCorpses ? i : members++) >= visible)) { left++; continue; }
            // A look without a bake (failed, or not done yet) draws skinned at any distance, up to the fallback's cap.
            bool unbaked = look.Vat?.Mesh is null;
            bool near = it.Distance <= Limits.NearMetres || !OldTierSwitch && it.Distance <= Limits.NearMetres + SkinnedHoldMetres && _skinnedByKey.ContainsKey(it.Key);
            if (skinned < (unbaked ? Limits.FallbackSkinned : skinnedCap) && (near || unbaked) && TakeSkinned(it, look)) { skinned++; continue; }
            if (look.Vat?.Mesh is not null && AddFar(look, it, body)) { vat++; continue; }
            left++;
        }
        // Free the skinned members not used this frame (kept in the pool, hidden).
        _unused.Clear();
        foreach (var (key, s) in _skinnedByKey) if (!s.Used) { s.Root.Visible = false; s.Key = int.MinValue; _unused.Add(key); }
        foreach (var key in _unused) _skinnedByKey.Remove(key);
        int batches = 0;
        foreach (var f in _far.Values)
        {
            f.Node.Multimesh.VisibleInstanceCount = f.Count;
            if (f.Count == 0) continue;
            batches++;
            RenderingServer.MultimeshSetBuffer(f.Node.Multimesh.GetRid(), f.Buffer);
            if (!f.Boxed && !OneBatch) Box(f);
        }
        FarBatches = batches;
        ShownSkinned = skinned; ShownVat = vat; LeftOut = left;
        _drawWorst = Math.Max(_drawWorst, ThreadClock.Ms(started));
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
                _builtSince++;
            }
            s.Key = it.Key; s.Clip = -1;
            _skinnedByKey[it.Key] = s;
        }
        s.Used = true;
        s.Root.Visible = true;
        s.Root.Transform = new Transform3D(BasisOf(it).Scaled(Vector3.One * look.Scale), it.Position - new Vector3(0, 0, it.Sink));
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

    static Basis BasisOf(in Item it)
    {
        var yaw = new Basis(new Vector3(0, 0, 1), it.Yaw);
        return it.Turn.W == 0f && it.Turn.X == 0f && it.Turn.Y == 0f && it.Turn.Z == 0f ? yaw : new Basis(it.Turn) * yaw;
    }

    static long CellOf(Vector3 p) => ((long)Mathf.FloorToInt(p.X / CellMetres) << 32) | (uint)Mathf.FloorToInt(p.Y / CellMetres);

    /// <summary>A cell batch's box: its cell, the margin round it, and the heights its members have stood at (game space, the node's own); set again when a member stands higher or lower.</summary>
    static void Box(Far f)
    {
        f.Boxed = true;
        float x0 = (int)(f.Cell >> 32) * CellMetres, y0 = (int)(uint)f.Cell * CellMetres;
        f.Node.Multimesh.CustomAabb = new Aabb(new Vector3(x0 - CellMargin, y0 - CellMargin, f.Low - CellMargin), new Vector3(CellMetres + 2f * CellMargin, CellMetres + 2f * CellMargin, f.High - f.Low + 2f * CellMargin));
    }

    bool AddFar(MusouLooks.Look look, in Item it, bool body = false)
    {
        // The corpse floor (2026-11-09): the bodies have a count (and room) of their own beside the members' cap, so neither crowds the other out.
        var counts = body ? _farBodiesPerLook : _farPerLook;
        int perLook = counts.GetValueOrDefault(look);
        if (perLook >= (body ? Limits.HardBodies : Limits.MaxVat)) return false;
        long cell = OneBatch ? long.MinValue : CellOf(it.Position);   // one batch: a key no cell has
        if (!_far.TryGetValue((look, cell), out var f))
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = look.Vat!.Mesh };
            var node = new MultiMeshInstance3D { Name = $"Far {look.Name} {cell:X}", Multimesh = mm, ExtraCullMargin = 4f };
            AddChild(node);
            _far[(look, cell)] = f = new Far { Look = look, Node = node, Cell = cell };
        }
        if (f.Count >= f.Capacity)
        {
            // Grown by doubling (one batch per look: its whole cap at once, as before); a new instance count clears the buffer, which is written again below and uploaded whole.
            int most = Limits.MaxVat + (OldCorpses ? 0 : Limits.HardBodies);
            int capacity = OneBatch ? most : Math.Min(most, Math.Max(32, f.Capacity * 2));
            var buffer = new float[capacity * 16];
            Array.Copy(f.Buffer, buffer, f.Count * 16);
            f.Buffer = buffer; f.Capacity = capacity;
            f.Node.Multimesh.InstanceCount = capacity;
        }
        if (!OneBatch && (it.Position.Z < f.Low || it.Position.Z > f.High)) { f.Low = MathF.Min(f.Low, it.Position.Z); f.High = MathF.Max(f.High, it.Position.Z); f.Boxed = false; }
        counts[look] = perLook + 1;
        var vat = look.Vat!;
        var (r0, r1, blend) = MusouVat.FramesAt(vat.Clips[it.Clip], it.Time);
        var basis = BasisOf(it).Scaled(Vector3.One * look.Scale);
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
