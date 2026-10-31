using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using Godot;
using SmContent;
using SmFormats.Fx;

namespace SmClient;

/// <summary>One playing effect: a compiled system at a place, with its own clock.</summary>
public sealed class FxEffect
{
    public required CSystem System { get; init; }
    public Vector3 Origin;
    public Basis Axes = Basis.Identity;
    public float Scale = 1f;
    /// <summary>Where a beam-type effect ends (tracers, the melta blast); beams whose axis is the effect's axis span the distance to it.</summary>
    public Vector3? Target;
    /// <summary>Called each frame for an attached effect: the marker's world place, or null when it is gone (the effect then stays where it was).</summary>
    public Func<(Vector3 Position, Basis Axes)?>? Follow;
    /// <summary>Seconds since it started (negative while a delay runs).</summary>
    public double Age;
    public double Life = 5.0;
    public bool Repeat;
    public uint Seed;
    public bool Dead;
    public int Priority;
    /// <summary>Samples of the root's place for streamers (time, position), newest last.</summary>
    internal List<(double T, Vector3 P)>? History;
    /// <summary>Decal elements of this effect already placed (a decal is placed once, then lives on its own: <see cref="FxPlayer"/> decals, M38).</summary>
    internal HashSet<CDecal>? PlacedDecals;
    internal int QuadsThisFrame;
    public string Name = "";
    public float DistanceFade = 1f;
    public bool Ambient;
    /// <summary>Stops soon after <see cref="Follow"/> is lost (a projectile's trail when it is gone).</summary>
    public bool StopOnLost;
    /// <summary>M40-T13 (ours): recolours the effect's sprites, beams and streamers to this hue at their own brightness (the lens colour on the Chaos eye glow); null: the data's colours.</summary>
    public Color? Tint;
    /// <summary>Metres per second of a clamped beam's bright head, replacing the element's <c>USpeed</c> (0: the element's own); set for tracers whose flight is capped.</summary>
    public float HeadSpeed;
    /// <summary>fx/visuals-2 (ours): scales the width of a clamped bright-head beam (a bolt tracer's streak) and the length of the streak's trail (metres per U); 1: the data's. Other beams are untouched.</summary>
    public float TracerWidthScale = 1f, TracerTrailScale = 1f;
    /// <summary>M41: run the collision descriptions (<see cref="CNode.OnCollision"/>): a "dropper" particle touches the level where it starts and starts what it holds (the ground pound's impact). The effect must be placed on the ground, its +Y along the surface normal.</summary>
    public bool Collisions;
    /// <summary>
    /// M44: draw only the system's roots that hold a dropper (with <see cref="Collisions"/>: what the dropper starts on the ground; the jump pack's thrust
    /// then shows only its ground burst where it is placed, not its flames).
    /// </summary>
    public bool GroundOnly;
    /// <summary>fx/air-slash-landing: the effect's decals (crater, cracks) are not placed (the air slash's flash is the pound's burst without its ground marks).</summary>
    public bool NoDecals;
    /// <summary>feel/muzzle-smoke (owner's change of 2026-10-29, ours): 1 plays the data's look; below 1 a weapon's smoke elements (smoke trail beams and streamers, smoke puffs, sprays that emit them) keep this fraction of their width, radius and sideways speed. Set by the networked view for weapon fire, tracer and stop-fire effects only.</summary>
    public float SmokeRein = 1f;
    /// <summary>feel/scope-lascannon: seconds after its start that <see cref="Follow"/> keeps moving the effect (an effect a shot attaches to the weapon follows its marker for the first frames only); infinity: as long as it lives.</summary>
    public float FollowFor = float.PositiveInfinity;
    /// <summary>feel/jump-pack: a ground effect whose dust and smoke must not show hard lines where the scene cuts them (<see cref="FxBatch.GroundSoftScale"/>).</summary>
    public bool SoftGround;
    /// <summary>feel/camera-hitstop: not drawn while set (the own jump pack's thrust and idle glow while the camera hides the own character); the effect keeps its clock.</summary>
    public bool Hidden;
    /// <summary>
    /// fx/impact-pass (the owner's request of 2026-10-29, "shorten the trail by a lot"; ours): above 0, a streamer that follows the effect's own path (not a particle's) is laid along the
    /// last <c>Length x scale x TrailScale</c> metres of that path (measured along it, from where the effect is now) instead of the data's length read as seconds of travel at 4 m/s
    /// (<see cref="FxPlayer"/>'s streamer rule), which drew a jump pack's thrust trail back to the take-off point. 0: the old reading.
    /// </summary>
    public float TrailScale;
    /// <summary>fx/impact-pass (ours): scales the length of the effect's beams that have no target (a jump pack thrust's <c>rocket</c> flame); 1: the data's length.</summary>
    public float BeamScale = 1f;
}

/// <summary>
/// M25b: the effect player. Plays the original particle systems (<see cref="FxSystem"/> trees read from <c>master_fx.oppc</c>) as camera-facing
/// and axis-facing sprites out of the original atlas (<see cref="FxBatch"/>), lights (a few pooled omni lights) and decals (Godot <see cref="Decal"/>s with
/// the original decal textures). It evaluates each effect as a pure function of its age (no per-particle state), so a frame costs only what is visible.
/// <para>
/// What each element is taken to mean (the data names the fields; the renderer's semantics are not documented, so every rule here is an
/// **interpretation**, said in the F1 line): curve keys are fractions of the element's duration; a leaf shows for its duration (at least
/// <see cref="MinShow"/> s, so one-frame flashes are seen); combos pass time through, scale and offset their children and turn them by their start angle;
/// the effect's local +Y is its axis (muzzle: out of the barrel; tracer: toward the target; impact: the surface normal); a ring is a quad of
/// half-size <c>FrameRadius</c> (camera-facing, or flat across the axis), a beam a quad along the axis of length <c>FrameLength</c> (times the span to the
/// target when the effect has one) and width <c>FrameWidth</c>, tapered by <c>WidthOverLength</c>; a beam's texture runs along it every <c>MeterPerU</c> metres and
/// scrolls at <c>USpeed</c> metres per second (a clamped one, such as the bolt tracer, is a bright streak whose head flies at that speed); a spray emits
/// <c>rate</c> particles over its duration (and its bursts) along a cone of <c>EmitFrameDeviation</c> at <c>EmitFrameInitialSpeed</c> with its environment's drag and gravity, each showing the spray's children
/// in the particle's frame (+Y along its flight); a streamer is a ribbon through the recent places of its owner (a particle: its own earlier path).
/// Not drawn: distortion materials, soft-particle fading, sound, mesh elements, the beam's noise and arcs.
/// </para>
/// </summary>
public sealed partial class FxPlayer : Node3D
{
    public const int MaxParticles = 64;
    /// <summary>A leaf shorter than this still shows this long (one frame of a 0.001 s flash would be missed at 30 fps).</summary>
    public const float MinShow = 0.045f;
    /// <summary>Effects alive at once (shots, hits, explosions); the map's ambient ones are counted apart (<see cref="MaxAmbient"/>).</summary>
    const int MaxEffects = 160;
    const int MaxAmbient = 200;
    const int MaxLights = 6;
    /// <summary>Alpha decals (Godot Decal boxes) and additive glows (flat quads) alive at once; the oldest fade out when more come (M38: 36 shared before).</summary>
    const int MaxDecals = 48, MaxGlowDecals = 24;
    const int MaxDepth = 14;

    readonly Dictionary<string, CSystem?> _systems = new(StringComparer.Ordinal);
    FxLibrary? _library;
    FxBatch? _batch;
    readonly List<FxEffect> _effects = new();
    Camera3D? _camera;
    Vector3 _camPos;
    Basis _camBasis = Basis.Identity;
    uint _nextSeed = 1;
    int _ambientCount;
    /// <summary>Live shot, hit and explosion effects (not the ambient map ones): what the <c>MaxEffects</c> cap counts.</summary>
    public int ShotEffectCount => _effects.Count - _ambientCount;
    readonly List<LightRequest> _lights = new();
    readonly OmniLight3D[] _lightNodes = new OmniLight3D[MaxLights];
    readonly Dictionary<string, Texture2D?> _decalTextures = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    readonly Vector3[] _scratch = new Vector3[16];
    int _frameQuads;

    /// <summary>
    /// fx/no-smoke (the owner, 2026-10-29, a deliberate change from the original): smoke, dust, mist and haze sprites (<see cref="CNode.Smoke"/>, <see cref="SmContent.SmokeRules"/>) are not
    /// drawn; impacts keep their flash, sparks, debris and decal, explosions their fireball and flash. <c>--smoke on</c> (test aid, before / after) draws them as the data has them.
    /// </summary>
    public static bool ShowSmoke { get; set; } = SmokeRules.SwitchOn(OS.GetCmdlineUserArgs());

    /// <summary>
    /// fx/impact-pass (the owner, 2026-10-29: "remove all god rays from Manufactorum", a deliberate change from the original): the map's light-shaft beams (a beam with the data's
    /// <c>GodRayFade</c>: <c>amb_god_ray_thick</c>'s <c>sky_beam</c> shafts, the fake spotlights' cones, <c>light_steamy</c>'s shaft) are not drawn in its ambient effects; the rest of those
    /// effects (lamp glows, flares) stays. <c>--god-rays</c> (test aid, before / after) draws them as the data has them.
    /// </summary>
    public static bool ShowGodRays { get; set; } = OS.GetCmdlineUserArgs().Contains("--god-rays");

    /// <summary>fx/impact-pass: whether a system draws nothing but god-ray beams (<see cref="ShowGodRays"/>), so that a map object playing it need not be started at all.</summary>
    public static bool OnlyGodRays(CSystem system)
    {
        bool any = false;
        foreach (var node in system.Roots.SelectMany(r => r.AllNodes()))
        {
            if (node.Hide) continue;
            if (node is CBeam { GodRayFade: true }) { any = true; continue; }
            if (node is CSprite or CLight or CDecal or CMesh) return false;
        }
        return any;
    }

    /// <summary>Sprite quads drawn (all, alpha-blended) summed over the frames since the last reset, and the most in one frame (fx/no-smoke measurements).</summary>
    public long QuadSum, AlphaQuadSum;
    public int QuadMax;

    public bool IsReady { get; private set; }
    /// <summary>Test aid: called each frame between the sprite batch's begin and end (draws extra quads).</summary>
    public Action<FxBatch>? DebugDraw { get; set; }
    public FxLibrary? Library => _library;
    public string Status { get; private set; } = "not loaded";
    public int Live => _effects.Count;
    public long Spawned { get; private set; }
    public long Culled { get; private set; }
    public int LastQuads => _frameQuads;
    public int LastLights { get; private set; }
    public int LiveDecals => _decalRecs.Count;
    public int Dropped => _batch?.DroppedLastFrame ?? 0;
    public long DroppedTotal => _batch?.DroppedTotal ?? 0;
    public int AdditiveQuads => _batch?.AdditiveLastFrame ?? 0;
    public int AlphaQuads => _batch?.AlphaLastFrame ?? 0;
    // Update cost (ms): sum, max and frames since the last reset.
    public double CostSum, CostMax;
    public int CostFrames;
    public IReadOnlyCollection<string> Missing => _missing;
    readonly Stopwatch _watch = new();

    struct LightRequest { public Vector3 Pos; public Color Colour; public float Energy, Range, Priority; }

    /// <summary>Builds the renderer from the effects library (atlas texture, rect table); <paramref name="casing"/> (or the <paramref name="casingError"/> of reading it) comes from a background read, else it is read here. False, with <see cref="Status"/>, when the atlas cannot be used.</summary>
    public bool Initialize(FxLibrary library, string gameDir = "", Image? atlasImage = null, StaticModel? casing = null, string? casingError = null)
    {
        _library = library;
        try
        {
            var dds = atlasImage is null ? library.Texture(library.Page.AtlasTexture) : null;
            if (dds is null && atlasImage is null) { Status = $"atlas '{library.Page.AtlasTexture}' not in the effects package"; return false; }
            _batch = new FxBatch { Name = "Sprites" };
            AddChild(_batch);
            if (!_batch.Initialize(library.Page, dds, atlasImage, out string error)) { Status = error; return false; }
            for (int i = 0; i < MaxLights; i++)
            {
                _lightNodes[i] = new OmniLight3D { Name = "FxLight" + i, Visible = false, ShadowEnabled = false, OmniAttenuation = 1.2f };
                AddChild(_lightNodes[i]);
            }
            if (gameDir.Length > 0) LoadCasing(gameDir, casing, casingError);
            IsReady = true;
            Status = $"atlas {library.Page.Rects.Count} sprites, {library.SystemNames.Count} systems";
            return true;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Status = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    readonly Queue<string> _decalQueue = new();
    /// <summary>Systems whose collision descriptions are played (M41), so that their decals are preloaded.</summary>
    readonly HashSet<string> _collisionSystems = new(StringComparer.Ordinal);

    /// <summary>The decals' DDS bytes read in the background (by texture name); the main thread never reads the package (<see cref="FxLibrary"/>: one inflate pass per read).</summary>
    readonly Dictionary<string, byte[]> _decalBytes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The decal textures a system uses (the background load reads their bytes in one pass).</summary>
    public static IEnumerable<string> DecalNames(IEnumerable<CSystem> systems, IReadOnlySet<string>? collisionSystems = null)
    {
        // Collision descriptions count only for the systems that run them (M41: the jump pack's); the others' (blood droplets, grenade craters) are not read.
        static IEnumerable<CNode> Plain(CNode n) { yield return n; foreach (var c in n.Children) foreach (var d in Plain(c)) yield return d; }
        return systems.SelectMany(s => s.Roots.SelectMany(r => collisionSystems?.Contains(s.Name) == true ? r.AllNodes() : Plain(r))).OfType<CDecal>().Select(d => d.Diffuse).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Adds systems compiled elsewhere (the background load) with their decal textures' DDS bytes, and queues the textures for creation a few per frame.</summary>
    public void Preload(IReadOnlyDictionary<string, CSystem> systems, IReadOnlyDictionary<string, byte[]> decalBytes, IReadOnlySet<string>? collisionSystems = null)
    {
        if (collisionSystems is not null) foreach (var name in collisionSystems) _collisionSystems.Add(name);
        foreach (var (name, bytes) in decalBytes) _decalBytes.TryAdd(name, bytes);
        foreach (var (name, system) in systems)
            if (!_systems.TryGetValue(name, out var known) || known is null)
            {
                _systems[name] = system;
                foreach (var decal in DecalNames([system], _collisionSystems))
                    if (!_decalTextures.ContainsKey(decal) && !_decalQueue.Contains(decal)) _decalQueue.Enqueue(decal);
            }
    }

    /// <summary>
    /// A compiled system by name, from those preloaded (<see cref="Preload"/>); null, with the name noted in <see cref="Missing"/>, when there is none.
    /// Never reads the package: that inflates it up to the entry, seconds on a busy machine, and this runs in the frame loop.
    /// </summary>
    public CSystem? System(string name)
    {
        if (_systems.TryGetValue(name, out var s)) return s;
        _missing.Add(name);
        _systems[name] = null;
        return null;
    }

    /// <summary>
    /// Starts an effect. <paramref name="axes"/>' Y column is the effect's axis. <paramref name="lifetime"/> &gt; 0 overrides the system's own;
    /// <paramref name="delay"/> seconds before it starts; <paramref name="repeat"/> restarts it for ever (ambient map effects, started at a random age).
    /// <paramref name="tint"/> recolours the effect (<see cref="FxEffect.Tint"/>; the Chaos plasma, <see cref="SmContent.PlasmaColour"/>); null: the data's colours.
    /// Returns null when the system is unknown or the player is not ready.
    /// </summary>
    public FxEffect? Play(string name, Vector3 origin, Basis axes, float scale, Vector3? target = null, Func<(Vector3 Position, Basis Axes)?>? follow = null,
                          float lifetime = 0f, double delay = 0.0, bool repeat = false, int priority = 0, bool ambient = false, Color? tint = null, float smokeRein = 1f, float followFor = float.PositiveInfinity)
    {
        if (!IsReady || System(name) is not { } system) return null;
        if (!float.IsFinite(origin.X + origin.Y + origin.Z) || !float.IsFinite(scale)) return null;
        int ambientCount = _ambientCount;
        if (ambient ? ambientCount >= MaxAmbient : _effects.Count - ambientCount >= MaxEffects)
        {
            if (ambient) { Culled++; return null; }
            // Drop the oldest of the lowest priority.
            int victim = -1;
            for (int i = 0; i < _effects.Count; i++)
                if (!_effects[i].Ambient && _effects[i].Priority <= priority && (victim < 0 || _effects[i].Priority < _effects[victim].Priority || _effects[i].Age > _effects[victim].Age)) victim = i;
            if (victim < 0) { Culled++; return null; }
            Release(_effects[victim]); _effects.RemoveAt(victim); Culled++;
        }
        var e = new FxEffect
        {
            System = system, Origin = origin, Axes = axes, Scale = scale, Target = target, Follow = follow, Age = -delay, Repeat = repeat, Priority = priority,
            Seed = _nextSeed++ * 2654435761u, Name = name, Ambient = ambient, Tint = tint, SmokeRein = smokeRein, FollowFor = followFor,
            Life = lifetime > 0f ? lifetime : system.Lifetime is { } l ? l + 0.05 : repeat ? 8.0 : 3.0,
        };
        if (repeat && system.Lifetime is { } rl && rl > 0.01f) e.Life = double.PositiveInfinity;
        if (repeat) e.Life = double.PositiveInfinity;
        if (system.HasStreamer) e.History = new List<(double, Vector3)>();
        _effects.Add(e);
        if (ambient) _ambientCount++;
        Spawned++;
        return e;
    }

    public void Clear()
    {
        foreach (var e in _effects) Release(e);
        _effects.Clear();
        _ambientCount = 0;
        ClearDecals();
    }

    /// <summary>Advances every effect by <paramref name="dt"/> and draws them for <paramref name="camera"/>.</summary>
    public void Update(double dt, Camera3D camera)
    {
        if (!IsReady || _batch is null) return;
        _watch.Restart();
        for (int i = 0; i < 2 && _decalQueue.Count > 0; i++) DecalTexture(_decalQueue.Dequeue());
        _camera = camera;
        _camPos = camera.GlobalPosition;
        _camBasis = camera.GlobalBasis;
        _batch.Begin(_camPos);
        _lights.Clear();
        _frameQuads = 0;
        BeginMeshes();
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            var e = _effects[i];
            e.Age += dt;
            if (e.Age > e.Life) { Release(e); if (e.Ambient) _ambientCount--; _effects.RemoveAt(i); continue; }
            if (e.Follow is not null && e.Age >= e.FollowFor) e.Follow = null;   // feel/scope-lascannon: a shot's effect follows its marker for a moment only, then stays where it is
            if (e.Follow is { } follow)
            {
                if (follow() is { } place) { e.Origin = place.Position; e.Axes = place.Axes; }
                else { e.Follow = null; if (e.StopOnLost) e.Life = Math.Min(e.Life, e.Age + 0.9); }
            }
            if (e.Age < 0.0) continue;
            if (e.History is { } h)
            {
                h.Add((e.Age, e.Origin));
                while (h.Count > 3 && e.Age - h[0].T > 4.0) h.RemoveAt(0);
            }
            DrawEffect(e);
        }
        DebugDraw?.Invoke(_batch);
        _batch.End();
        QuadSum += _batch.AdditiveLastFrame + _batch.AlphaLastFrame; AlphaQuadSum += _batch.AlphaLastFrame;
        QuadMax = Math.Max(QuadMax, _batch.AdditiveLastFrame + _batch.AlphaLastFrame);
        FlushMeshes();
        ApplyLights();
        // Decals live on their own once placed (M38): age, fade and retire them.
        FadeDecals(dt);
        _watch.Stop();
        double ms = _watch.Elapsed.TotalMilliseconds;
        CostSum += ms; CostMax = Math.Max(CostMax, ms); CostFrames++;
    }

    void Release(FxEffect e)
    {
        e.PlacedDecals = null;   // M38: its decals stay (they have their own clocks)
        e.Dead = true;           // M40-T13: owners of long-lived effects (the eye glow) see an evicted or cleared effect
    }

    // ------------------------------------------------------------------------------------------------ evaluation

    readonly struct Ctx(Vector3 pos, Basis b, float s, uint seed)
    {
        public readonly Vector3 Pos = pos;
        public readonly Basis B = b;
        public readonly float S = s;
        public readonly uint Seed = seed;
        // Particle motion (for streamers following their particle): start, direction, speed, drag, gravity, age.
        public readonly bool InParticle;
        public readonly Vector3 P0, Dir;
        public readonly float Speed, Drag, Gravity, Tau;

        public Ctx(Vector3 pos, Basis b, float s, uint seed, Vector3 p0, Vector3 dir, float speed, float drag, float gravity, float tau) : this(pos, b, s, seed)
        {
            InParticle = true; P0 = p0; Dir = dir; Speed = speed; Drag = drag; Gravity = gravity; Tau = tau;
        }

        public Ctx With(Vector3 pos, Basis b, float s, uint seed) => InParticle
            ? new Ctx(pos, b, s, seed, P0, Dir, Speed, Drag, Gravity, Tau)
            : new Ctx(pos, b, s, seed);
    }

    static Vector3 ParticlePos(in Ctx c, float tau)
    {
        float d = c.Drag;
        float disp = d > 1e-4f ? c.Speed * (1f - MathF.Exp(-d * tau)) / d : c.Speed * tau;
        return c.P0 + c.Dir * disp + new Vector3(0f, -Drop(c.Gravity, d, tau), 0f);
    }

    /// <summary>How far a particle with gravity <paramref name="g"/> and drag <paramref name="d"/> has fallen after <paramref name="tau"/> seconds.</summary>
    static float Drop(float g, float d, float tau) =>
        d > 1e-4f ? g * (tau / d - (1f - MathF.Exp(-d * tau)) / (d * d)) : 0.5f * g * tau * tau;

    static uint Mix(uint a, int b) { unchecked { return (a ^ ((uint)b * 2654435761u)) * 16777619u + 0x9e3779b9u; } }

    /// <summary>A repeatable pseudo-random number in [0, 1) for a seed and a slot.</summary>
    static float R(uint seed, int k)
    {
        unchecked
        {
            uint h = seed * 747796405u + (uint)k * 2891336453u + 1u;
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    /// <summary>1 ± (epsilon / 2), never below a quarter: the reading of the files' Epsilon fields.</summary>
    static float Jitter(float epsilon, uint seed, int k) => epsilon == 0f ? 1f : MathF.Max(0.25f, 1f + epsilon * (R(seed, k) - 0.5f));

    static Basis RotXYZ(float ax, float ay, float az) =>
        new Basis(Vector3.Back, az) * new Basis(Vector3.Up, ay) * new Basis(Vector3.Right, ax);

    static Basis FromY(Vector3 y, float roll)
    {
        y = y.LengthSquared() < 1e-10f ? Vector3.Up : y.Normalized();
        var r = MathF.Abs(y.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        var x = r.Cross(y).Normalized();
        var z = x.Cross(y);
        return new Basis(y, roll) * new Basis(x, y, z);
    }

    static float At(FxCurve c, float t, float fallback) => c.IsEmpty ? fallback : c.Evaluate(t);

    /// <summary>M40-T13: <paramref name="tint"/>'s hue at the colour's brightest channel (alpha kept); the colour itself without a tint.</summary>
    static Color Recolour(Color c, Color? tint) => tint is { } t ? new Color(t.R * Math.Max(c.R, Math.Max(c.G, c.B)), t.G * Math.Max(c.R, Math.Max(c.G, c.B)), t.B * Math.Max(c.R, Math.Max(c.G, c.B)), c.A) : c;

    static Color Tint(FxColourCurve c, float t, float alphaScale = 1f)
    {
        var v = c.Evaluate(t);
        return new Color(v.X, v.Y, v.Z, v.W * alphaScale);
    }

    /// <summary>Whether a leaf element shows at <paramref name="age"/>; its time fraction and duration.</summary>
    static bool LeafTime(CNode n, double age, uint seed, out float t, out float dur)
    {
        dur = n.Duration + n.Epsilon * (R(seed, 7) - 0.5f);
        if (n.Looping)
        {
            if (dur <= 0.001f) { t = 0f; return age >= 0; }
            t = (float)((age % dur) / dur);
            return age >= 0;
        }
        dur = MathF.Max(dur, 0.001f);
        t = (float)Math.Min(1.0, Math.Max(0.0, age) / dur);
        return age >= 0 && age <= Math.Max(dur, MinShow);
    }

    void DrawEffect(FxEffect e)
    {
        if (e.Hidden) return;
        float dist = _camPos.DistanceTo(e.Origin);
        // Distance culling (map ambience much farther than a hit spark).
        if (dist > (e.Ambient ? 100f : 220f)) return;
        double age = e.Age;
        if (e.Repeat && e.System.Lifetime is { } l && l > 0.05f && !e.System.HasStreamer) age %= l + 0.05;
        var root = new Ctx(e.Origin, e.Axes, e.Scale, e.Seed);
        int before = _frameQuads;
        if (_batch is not null) _batch.SoftGround = e.SoftGround;
        foreach (var node in e.System.Roots)
        {
            if (e.GroundOnly && !HoldsDropper(node)) continue;
            DrawNode(e, node, root, age, 0, Mix(e.Seed, 1));
        }
        if (_batch is not null) _batch.SoftGround = false;
        e.QuadsThisFrame = _frameQuads - before;
    }

    readonly Dictionary<CNode, bool> _holdsSmoke = new();

    /// <summary>A smoke element: a sprite whose material is a smoke cloud or a smoke trail (<c>lingering_smoke_cloud_*</c>, <c>alpha_trail_*</c>, <c>*smoke*</c>); what <see cref="FxEffect.SmokeRein"/> narrows.</summary>
    static bool IsSmoke(CSprite s) => s.Material.Name.Contains("smoke", StringComparison.OrdinalIgnoreCase) || s.Material.Name.StartsWith("alpha_trail", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a node or one of its descendants is a smoke element (cached).</summary>
    bool HoldsSmoke(CNode n)
    {
        if (_holdsSmoke.TryGetValue(n, out bool known)) return known;
        bool found = n is CSprite s && IsSmoke(s) || n.Children.Any(HoldsSmoke);
        return _holdsSmoke[n] = found;
    }

    readonly Dictionary<CNode, bool> _holdsDropper = new();

    /// <summary>Whether a node or one of its children (not its collision content) is a dropper spray (cached).</summary>
    bool HoldsDropper(CNode n)
    {
        if (_holdsDropper.TryGetValue(n, out bool known)) return known;
        bool found = n is CSpray { Collides: true } || n.Children.Any(HoldsDropper);
        return _holdsDropper[n] = found;
    }

    void DrawNode(FxEffect e, CNode n, in Ctx c, double age, int depth, uint seed)
    {
        if (n.Hide || depth > MaxDepth || age < 0.0) return;
        // fx/no-smoke: smoke, dust, mist and haze are not drawn (the owner's change). A smoke sprite is not drawn; its collision content (a dropper collider's decal or debris) is
        // played by DrawSpray's collision path, not from here, so its parents are skipped whole only when that content is smoke too (SmokeOnly).
        if (!ShowSmoke && (n.SmokeOnly || n.Smoke)) return;
        if (DecalsOnly && n is not (CCombo or CSpray or CDecal)) return;   // test aid
        switch (n)
        {
            case CCombo combo: DrawCombo(e, combo, c, age, depth, seed); break;
            case CSpray spray: DrawSpray(e, spray, c, age, depth, seed); break;
            case CRing ring: DrawRing(e, ring, c, age, seed); break;
            case CBeam beam: DrawBeam(e, beam, c, age, seed); break;
            case CStreamer streamer: DrawStreamer(e, streamer, c, age, seed); break;
            case CLight light: DrawLight(e, light, c, age, seed); break;
            case CDecal decal: if (!e.NoDecals) DrawDecal(e, decal, c, age, seed); break;
            case CMesh mesh: DrawMesh(e, mesh, c, age, seed); break;
        }
    }

    void DrawCombo(FxEffect e, CCombo n, in Ctx c, double age, int depth, uint seed)
    {
        float dur = MathF.Max(n.Duration, 0.001f);
        float t = n.Looping ? (float)((age % dur) / dur) : (float)Math.Min(1.0, age / dur);
        float scale = c.S * At(n.Scale, t, 1f);
        var off = new Vector3(At(n.OffX, t, 0f), At(n.OffY, t, 0f), At(n.OffZ, t, 0f)) * c.S;
        float D = MathF.PI / 180f;
        float ax = n.UseStart.X ? n.StartAngle.X * D : R(seed, 1) * MathF.Tau;
        float ay = n.UseStart.Y ? n.StartAngle.Y * D : R(seed, 2) * MathF.Tau;
        float az = n.UseStart.Z ? n.StartAngle.Z * D : R(seed, 3) * MathF.Tau;
        var basis = c.B * RotXYZ(ax, ay, az);
        var cc = c.With(c.Pos + c.B * off, basis, scale, seed);
        if (n.OneAtRandom && n.Children.Length > 0)
        {
            int pick = Math.Min(n.Children.Length - 1, (int)(R(seed, 4) * n.Children.Length));
            DrawNode(e, n.Children[pick], cc, age, depth + 1, Mix(seed, pick + 11));
            return;
        }
        for (int i = 0; i < n.Children.Length; i++) DrawNode(e, n.Children[i], cc, age, depth + 1, Mix(seed, i + 11));
    }

    void DrawSpray(FxEffect e, CSpray n, in Ctx c, double age, int depth, uint seed)
    {
        if (n.Spawns.Length == 0 || n.Children.Length == 0) return;
        float duration = MathF.Max(n.Duration, 0.001f);
        float life = MathF.Max(n.ChildLife, 0.02f);
        int cycleMin = 0, cycleMax = 0;
        if (n.Looping)
        {
            cycleMax = (int)Math.Floor(age / duration);
            cycleMin = Math.Max(0, (int)Math.Floor((age - life) / duration));
            if (cycleMax - cycleMin > 6) cycleMin = cycleMax - 6;
        }
        for (int cycle = cycleMin; cycle <= cycleMax; cycle++)
        {
            for (int i = 0; i < n.Spawns.Length; i++)
            {
                double spawn = cycle * (double)duration + n.Spawns[i];
                float tau = (float)(age - spawn);
                if (tau < 0f || tau > life) continue;
                uint ps = Mix(Mix(seed, cycle * 97 + i), 31);
                float te = Math.Clamp(n.Spawns[i] / duration, 0f, 1f);
                // Direction: uniform over the cone between the inner and the outer deviation about the local +Y axis.
                float devMax = Math.Clamp(At(n.Deviation, te, 0f), 0f, MathF.PI), devMin = Math.Clamp(At(n.Inner, te, 0f), 0f, devMax);
                float cosT = Mathf.Lerp(MathF.Cos(devMin), MathF.Cos(devMax), R(ps, 1));
                float sinT = MathF.Sqrt(MathF.Max(0f, 1f - cosT * cosT)), phi = R(ps, 2) * MathF.Tau;
                var local = new Vector3(sinT * MathF.Cos(phi), cosT, sinT * MathF.Sin(phi));
                var dir = (c.B * local).Normalized();
                // feel/muzzle-smoke (owner's change, NetworkView.Smoke.cs): a gun's smoke puffs keep their forward speed and lose part of their sideways speed.
                var moveDir = e.SmokeRein < 1f && HoldsSmoke(n) ? c.B * new Vector3(local.X * e.SmokeRein, local.Y, local.Z * e.SmokeRein) : dir;
                float speed = At(n.Speed, te, 0f) * (n.ScaleSpeed ? c.S : 1f);
                var origin = c.Pos + c.B * (new Vector3(At(n.OffX, te, 0f), At(n.OffY, te, 0f), At(n.OffZ, te, 0f)) * c.S);
                if (!n.FromPoint)
                    origin += c.B * (new Vector3(R(ps, 3) - 0.5f, R(ps, 4) - 0.5f, R(ps, 5) - 0.5f) * new Vector3(At(n.VolX, te, 0f), At(n.VolY, te, 0f), At(n.VolZ, te, 0f)) * c.S);
                if (e.Collisions && n.Collides)
                {
                    // M41: the dropper lands where it starts (the effect sits on the ground): its collision descriptions play in the surface's frame (+Y = the normal),
                    // and the collider itself (a sprite of the dropper's own) is not drawn.
                    var landed = new Ctx(origin, c.B, c.S, ps);
                    for (int k = 0; k < n.Children.Length; k++)
                    {
                        var child = n.Children[k];
                        if (child.OnCollision.Length == 0) { DrawNode(e, child, landed, tau, depth + 1, Mix(ps, k + 5)); continue; }
                        for (int h = 0; h < child.OnCollision.Length; h++) DrawNode(e, child.OnCollision[h], landed, tau, depth + 1, Mix(ps, k * 16 + h + 41));
                    }
                    continue;
                }
                float g = n.Gravity;
                var pc = new Ctx(origin, FromY(dir, R(ps, 6) * MathF.Tau), c.S, ps, origin, moveDir, speed, n.Drag, g, tau);
                var pos = ParticlePos(pc, tau);
                float scale = c.S * At(n.ParticleScale, Math.Clamp(tau / life, 0f, 1f), 1f);
                var kid = new Ctx(pos, pc.B, scale, ps, origin, moveDir, speed, n.Drag, g, tau);
                for (int k = 0; k < n.Children.Length; k++) DrawNode(e, n.Children[k], kid, tau, depth + 1, Mix(ps, k + 5));
            }
        }
    }

    Color Final(CSprite s, float t, uint seed, float alphaScale = 1f)
    {
        var col = s.Colour.IsEmpty ? Colors.White : Tint(s.Colour, t, alphaScale);
        return col;
    }

    int CellFor(CSprite s, float t, uint seed)
    {
        int cells = s.Cells;
        if (cells <= 1) return 0;
        if (s.Material.RandomTile) return Math.Min(cells - 1, (int)(R(seed, 9) * cells));
        float rate = At(s.Anim, t, 1f);
        return Math.Clamp((int)(t * rate * cells), 0, cells - 1);
    }

    void DrawRing(FxEffect e, CRing n, in Ctx c, double age, uint seed)
    {
        if (n.Distortion || _batch is null || !_batch.HasRoom) return;
        if (!LeafTime(n, age, seed, out float t, out float dur)) return;
        float radius = At(n.Radius, t, 0.1f) * c.S * Jitter(n.EpsRadius, seed, 8);
        if (e.SmokeRein < 1f && IsSmoke(n)) radius *= e.SmokeRein;   // feel/muzzle-smoke
        if (radius <= 0.0005f) return;
        var colour = Recolour(Final(n, t, seed), e.Tint);
        if (!_batch.Sprite(n.Material, CellFor(n, t, seed), out var rect)) { if (!n.Material.IsEmpty) _missing.Add("sprite:" + n.Material.Name); return; }
        var centre = c.Pos + c.B * (new Vector3(At(n.OffX, t, 0f), At(n.OffY, t, 0f), At(n.OffZ, t, 0f)) * c.S);
        float D = MathF.PI / 180f;
        float dir = n.RandomSpinDirection && R(seed, 12) < 0.5f ? -1f : 1f;
        Vector3 x, y;
        if (n.Billboard)
        {
            float roll = (n.UseStart.Z ? n.StartAngle.Z * D : R(seed, 3) * MathF.Tau) + dir * At(n.SpinZ, t, 0f) * (float)age;
            float cs = MathF.Cos(roll), sn = MathF.Sin(roll);
            var right = _camBasis.X; var up = _camBasis.Y;
            x = (right * cs + up * sn) * (2f * radius);
            y = (up * cs - right * sn) * (2f * radius);
        }
        else
        {
            // A flat sprite across the axis, tumbling by its spin (rad/s).
            float ax = (n.UseStart.X ? n.StartAngle.X * D : R(seed, 1) * MathF.Tau) + dir * At(n.SpinX, t, 0f) * (float)age;
            float ay = (n.UseStart.Y ? n.StartAngle.Y * D : R(seed, 2) * MathF.Tau) + dir * At(n.SpinY, t, 0f) * (float)age;
            float az = (n.UseStart.Z ? n.StartAngle.Z * D : R(seed, 3) * MathF.Tau) + dir * At(n.SpinZ, t, 0f) * (float)age;
            var b = c.B * RotXYZ(ax, ay, az);
            x = b.X * (2f * radius); y = b.Z * (2f * radius);
        }
        if (_batch.Add(n.Additive, centre, x, y, colour, rect, kind: n.Billboard ? FxBatch.QuadKind.CameraBillboard : FxBatch.QuadKind.FlatRing)) _frameQuads++;
    }

    void DrawBeam(FxEffect e, CBeam n, in Ctx c, double age, uint seed)
    {
        if (n.Distortion || _batch is null || !_batch.HasRoom) return;
        if (n.GodRayFade && e.Ambient && !ShowGodRays) return;   // fx/impact-pass: the map's god rays are gone (the owner's change)
        if (!LeafTime(n, age, seed, out float t, out float dur)) return;
        var axis = c.B.Y.Normalized();
        var start = c.Pos + c.B * (new Vector3(At(n.OffX, t, 0f), At(n.OffY, t, 0f), At(n.OffZ, t, 0f)) * c.S);
        float length;
        float lengthFraction = At(n.Length, t, 1f);
        if (e.Target is { } target && axis.Dot(e.Axes.Y.Normalized()) > 0.98f)
        {
            float span = e.Origin.DistanceTo(target);
            float along = (start - e.Origin).Dot(axis);
            length = MathF.Min(lengthFraction * span, span - along);
        }
        else length = lengthFraction * c.S * Jitter(n.EpsLength, seed, 10) * e.BeamScale;
        float width = At(n.Width, t, 0.1f) * c.S * Jitter(n.EpsWidth, seed, 11);
        if (e.SmokeRein < 1f && IsSmoke(n)) width *= e.SmokeRein;   // feel/muzzle-smoke
        bool headBeam = n.USpeed != 0f && n.Material.UAddressMode == 1 && n.MeterPerU > 0.0001f;
        if (headBeam) width *= e.TracerWidthScale;   // fx/visuals-2
        if (length <= 0.001f || width <= 0.0005f) return;
        if (!_batch.Sprite(n.Material, CellFor(n, t, seed), out var rect)) { if (!n.Material.IsEmpty) _missing.Add("sprite:" + n.Material.Name); return; }
        float alphaScale = 1f;
        var mid = start + axis * (length * 0.5f);
        if (n.GodRayFade)
        {
            float cosA = MathF.Abs((mid - _camPos).Normalized().Dot(axis));
            float angle = MathF.Acos(Math.Clamp(cosA, 0f, 1f)) * 180f / MathF.PI;
            alphaScale = Math.Clamp(angle / MathF.Max(1f, n.FadeAngle), 0f, 1f);
        }
        var colour = Recolour(Final(n, t, seed, alphaScale), e.Tint);
        // U range: the texture's metres per U, its scroll and offset.
        float u0, u1; int mode;
        bool clamp = n.Material.UAddressMode == 1;
        float mpu = n.MeterPerU * Jitter(n.EpsMeterPerU, seed, 13);
        if (headBeam) mpu *= e.TracerTrailScale;   // fx/visuals-2: a shorter streak
        if (mpu <= 0.0001f)
        {
            float off = At(n.UOffset, t, 0f), rep = At(n.URepeat, t, 1f);
            u0 = off; u1 = off + rep;
            mode = (u0 < 0f || u1 > 1.0001f) ? (clamp ? 2 : 1) : 0;
        }
        else if (n.USpeed != 0f && clamp)
        {
            // A bright streak whose head flies down the beam at USpeed m/s (the effect's HeadSpeed, when set) and trails MeterPerU metres behind (provisional reading).
            float head = (e.HeadSpeed > 0f ? e.HeadSpeed : n.USpeed) * (float)age;
            u0 = 1f - head / mpu; u1 = 1f - (head - length) / mpu; mode = 2;
        }
        else
        {
            float scroll = n.USpeed * (float)age / mpu, off = At(n.UOffset, t, 0f);
            u0 = off - scroll; u1 = u0 + length / mpu; mode = clamp ? 2 : 1;
        }
        // Side vector: around the axis toward the camera (billboard), else the frame's X.
        Vector3 side;
        if (n.Billboard)
        {
            side = axis.Cross(_camPos - mid);
            side = side.LengthSquared() < 1e-8f ? _camBasis.X : side.Normalized();
        }
        else side = c.B.X.Normalized();
        bool tapered = !n.WidthOverLength.IsEmpty && (n.WidthOverLength.Keys.Count > 1 || MathF.Abs(n.WidthOverLength.Keys[0].Value - 1f) > 0.01f);
        // M42: what the quad is, for FxBatch's fades (no edge-on fade on axis billboards; god rays get their own soft, end and near fades).
        var kind = n.GodRayFade ? (n.Billboard ? FxBatch.QuadKind.GodRayAxis : FxBatch.QuadKind.GodRay)
                                : (n.Billboard ? FxBatch.QuadKind.AxisBillboard : FxBatch.QuadKind.FlatCard);
        if (!tapered)
        {
            if (_batch.Add(n.Additive, mid, axis * length, side * width, colour, rect, u0, u1, mode, kind)) _frameQuads++;
            return;
        }
        const int segments = 8;
        for (int i = 0; i < segments; i++)
        {
            float f0 = (float)i / segments, f1 = (i + 1f) / segments, fm = (f0 + f1) * 0.5f;
            float w = width * MathF.Max(0f, n.WidthOverLength.Evaluate(fm));
            if (w <= 0.0005f) continue;
            var centre = start + axis * (length * fm);
            if (_batch.Add(n.Additive, centre, axis * (length / segments), side * w, colour, rect, Mathf.Lerp(u0, u1, f0), Mathf.Lerp(u0, u1, f1), mode, kind)) _frameQuads++;
        }
    }

    void DrawStreamer(FxEffect e, CStreamer n, in Ctx c, double age, uint seed)
    {
        if (n.Distortion || _batch is null || !_batch.HasRoom) return;
        if (!LeafTime(n, age, seed, out float t, out float dur)) return;
        const int N = 8;
        float trail = At(n.Length, t, 0.3f);
        if (trail <= 0.001f) return;
        float seconds = n.LengthAsSeconds ? trail : trail / MathF.Max(2f, c.InParticle ? MathF.Abs(c.Speed) : 4f);
        if (!_batch.Sprite(n.Material, 0, out var rect)) { if (!n.Material.IsEmpty) _missing.Add("sprite:" + n.Material.Name); return; }
        var pts = _scratch;
        if (e.TrailScale > 0f && !c.InParticle && !n.LengthAsSeconds && e.History is { Count: > 1 } path)
            TrailByDistance(path, c.Pos - e.Origin, trail * c.S * e.TrailScale, pts, N);   // fx/impact-pass: the last metres of the path, starting where the effect is now
        else
        for (int j = 0; j <= N; j++)
        {
            float back = seconds * j / N;
            if (c.InParticle)
            {
                float tau = MathF.Max(0f, c.Tau - back);
                pts[j] = ParticlePos(c, tau) + (c.Pos - ParticlePos(c, c.Tau));
            }
            else if (e.History is { Count: > 1 } h)
            {
                double want = e.Age - back;
                int k = h.Count - 1;
                while (k > 0 && h[k].T > want) k--;
                var a = h[k]; var b = h[Math.Min(k + 1, h.Count - 1)];
                float f = b.T > a.T ? (float)Math.Clamp((want - a.T) / (b.T - a.T), 0.0, 1.0) : 0f;
                pts[j] = a.P.Lerp(b.P, f) + (c.Pos - e.Origin);
            }
            else pts[j] = c.Pos;
        }
        float master = n.MasterColour.IsEmpty ? 1f : n.MasterColour.Evaluate(t).W;
        float width0 = At(n.Width, t, 0.1f) * c.S;
        if (e.SmokeRein < 1f && IsSmoke(n)) width0 *= e.SmokeRein;   // feel/muzzle-smoke
        float u = 0f;
        for (int j = 0; j < N; j++)
        {
            var a = pts[j]; var b = pts[j + 1];
            var d = b - a;
            float len = d.Length();
            if (len < 1e-4f) continue;
            var ax = d / len;
            var mid = (a + b) * 0.5f;
            var side = ax.Cross(_camPos - mid);
            side = side.LengthSquared() < 1e-8f ? _camBasis.X : side.Normalized();
            float f = (j + 0.5f) / N;
            float width = width0 * (1f - 0.7f * f);
            var col = Recolour(n.Colour.IsEmpty ? Colors.White : Tint(n.Colour, f, master), e.Tint);
            float u0 = u, u1 = n.MeterPerU > 0.001f ? u + len / n.MeterPerU : (j + 1f) / N;
            if (n.MeterPerU <= 0.001f) u0 = (float)j / N;
            u = u1;
            if (_batch.Add(n.Additive, mid, d, side * width, col, rect, u0, u1, 1, FxBatch.QuadKind.AxisBillboard)) _frameQuads++;
        }
    }

    /// <summary>
    /// fx/impact-pass: <paramref name="count"/> + 1 points along the last <paramref name="metres"/> of a recorded path (newest last), evenly spaced by distance and moved by
    /// <paramref name="offset"/>; point 0 is the newest place (where the effect is drawn this frame). A path shorter than that ends at its oldest sample.
    /// </summary>
    internal void TrailByDistance(List<(double T, Vector3 P)> path, Vector3 offset, float metres, Vector3[] pts, int count)
    {
        var points = _trailScratch.AsSpan(0, count + 1);
        ImpactRules.TrailPath.ByDistance(path.Count, i => { var p = path[i].P; return new System.Numerics.Vector3(p.X, p.Y, p.Z); }, metres, points);   // engine-free, tested
        for (int j = 0; j <= count; j++) pts[j] = new Vector3(points[j].X, points[j].Y, points[j].Z) + offset;
    }

    readonly System.Numerics.Vector3[] _trailScratch = new System.Numerics.Vector3[16];

    void DrawLight(FxEffect e, CLight n, in Ctx c, double age, uint seed)
    {
        // A light flashes for its duration (at least 0.1 s: the data's lights are 0 to 5 ms long, one frame) and fades out over it.
        double show = Math.Max(n.Duration, 0.1);
        if (age < 0 || (!n.Looping && age > show)) return;
        // A looping light (the map's lamps) stays on at its first key; the flash fades out.
        float t = n.Looping ? 0f : (float)(age / show);
        float intensity = At(n.Intensity, t, 1f) * At(n.Power, t, 1f);
        var col = Recolour(n.Colour.IsEmpty ? Colors.White : Tint(n.Colour, t), e.Tint);   // the Chaos plasma's lights are purple too
        float range = At(n.AttenEnd, t, 4f) * c.S;
        if (range < 0.3f || intensity <= 0f) return;
        var pos = c.Pos + c.B * (new Vector3(At(n.OffX, t, 0f), At(n.OffY, t, 0f), At(n.OffZ, t, 0f)) * c.S);
        float fade = n.Looping ? 1f : 1f - t;
        float energy = intensity * 1.6f * fade;
        _lights.Add(new LightRequest { Pos = pos, Colour = new Color(col.R, col.G, col.B), Energy = energy, Range = range, Priority = energy * range / (1f + _camPos.DistanceTo(pos)) });
    }

    void ApplyLights()
    {
        _lights.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        int n = Math.Min(_lights.Count, MaxLights);
        for (int i = 0; i < MaxLights; i++)
        {
            var node = _lightNodes[i];
            if (i < n)
            {
                var l = _lights[i];
                node.GlobalPosition = l.Pos; node.LightColor = l.Colour; node.LightEnergy = l.Energy; node.OmniRange = l.Range;
                node.Visible = true;
            }
            else node.Visible = false;
        }
        LastLights = n;
    }
}
