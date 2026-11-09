using System;
using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The clock of the Librarian's effects (bolts, ring, cast pose): the frames' own time, advanced once a frame by the networked view, so the effects keep their pace in a Movie Maker recording (fixed frame
/// time, slower than the wall clock) as in play.
/// </summary>
public static class LibrarianClock
{
    public static double Now { get; private set; } = 1000.0;
    public static void Advance(double delta) => Now += Math.Clamp(delta, 0.0, 0.25);
}

/// <summary>
/// The Librarian's chain lightning, drawn procedurally (librarian-lightning, owner 2026-11-10: "improve the visual fidelity and quality of the chain lighting visual effects on cast. make it feel powerful and look good
/// and powerful"; **ours**: no third-party file and no original effect). Per caster:
/// <list type="bullet">
/// <item><b>Bolts</b>: each link a fractal (midpoint-displacement) main bolt with forks and sub-forks branching forward off it, the first links' widest and brightest and the width falling down the chain
/// (x0.9 a link, as the damage falls). The shape's offsets are kept relative to the link, so it follows moving ends every frame and is re-rolled every <see cref="RejagMin"/> to <see cref="RejagMax"/>
/// seconds (the flicker); each re-roll flares the bolt (a re-strike) and leaves the old shape behind as a violet afterglow that fades over <see cref="GhostSeconds"/>. With the cast pose's palms known,
/// the first link leaves both palms as two bolts that meet on the first target.</item>
/// <item><b>Look</b>: camera-facing strips (the vertex shader turns each strip to the camera, so no flat-on view), a hot white core over a soft electric-blue glow, brightness above 1 for the bloom
/// where the renderer has it; additive, unlit, depth-tested, no texture: the same in Forward+ and Compatibility.</item>
/// <item><b>Hands</b>: arcs crawling over and between the palms, a glow on each palm, charging up from the press (they show before the first target is reached, and on the own client the moment Fire
/// is pressed).</item>
/// <item><b>Impacts</b>: on every struck chest a star flare and short arcs bursting out; a newly struck target flashes larger.</item>
/// <item><b>Light</b>: a flickering blue light at the hands and one on the first target, from a budget of <see cref="LightBudget"/> across every caster.</item>
/// </list>
/// Pooled: two bolt meshes and one arc mesh per caster with fixed-size buffers (unused triangles degenerate), one flare multimesh, no node or material made per frame. Positions are the game space of
/// the parent (the caller converts). Restore switch <c>--old-librarian-lightning</c>: <see cref="LibrarianBoltsOld"/>.
/// </summary>
public sealed partial class LibrarianBolts : Node3D
{
    /// <summary>The frame clock the Librarian's effects run on (seconds of frame time: <see cref="LibrarianClock"/>).</summary>
    static double Now => LibrarianClock.Now;

    public const int MaxLinks = LibrarianRules.MaxDrawnLinks;
    /// <summary>Re-roll interval (s, random in the range) and the afterglow's fade (ours).</summary>
    public const float RejagMin = 0.035f, RejagMax = 0.065f, GhostSeconds = 0.12f;
    /// <summary>The fade after the last feed (s): the bolts, then the flares' afterglow (ours).</summary>
    public const float FadeSeconds = 0.22f, AfterglowSeconds = 0.45f;
    /// <summary>The first link's glow half-width (m), the core is the inner part the shader draws white (ours).</summary>
    public const float MainHalfWidth = 0.32f;
    /// <summary>Most procedural lights alive at once across every caster (ours).</summary>
    public const int LightBudget = 6;

    const int MaxPoints = 33;            // a main bolt: 2^5 segments
    const int BoltVerts = 9000, BoltIdx = BoltVerts * 3;
    const int ArcVerts = 2600, ArcIdx = ArcVerts * 3;
    const int Flares = MaxLinks + 2;

    static readonly Color Glow = new(0.2f, 0.42f, 1f, 1f), DeepGlow = new(0.42f, 0.28f, 1f, 1f), ForkGlow = new(0.36f, 0.6f, 1f, 1f);

    const string BoltShader = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, world_vertex_coords;
uniform float intensity = 1.0;
uniform float core_energy = 2.3;
uniform vec3 tint = vec3(1.0);
varying float across;
void vertex() {
    vec3 cam = INV_VIEW_MATRIX[3].xyz;
    vec3 side = cross(NORMAL, cam - VERTEX);
    float l = length(side);
    side = l > 1e-5 ? side / l : vec3(0.0, 1.0, 0.0);
    VERTEX += side * UV2.x;
    across = UV.y * 2.0 - 1.0;
}
void fragment() {
    float d = abs(across);
    float glow = pow(max(1.0 - d, 0.0), 1.7);
    float halo = pow(max(1.0 - d, 0.0), 4.0);
    float core = smoothstep(0.15, 0.01, d);
    float shimmer = 0.78 + 0.22 * sin(UV.x * 7.0 - TIME * 61.0);
    vec3 c = COLOR.rgb * tint * (glow * 1.5 + halo * 1.3) * shimmer + vec3(0.9, 0.95, 1.0) * core * core_energy;
    ALBEDO = c;
    ALPHA = clamp((glow * 0.6 + halo * 0.45 + core) * intensity * COLOR.a, 0.0, 1.0);
}
""";

    const string FlareShader = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled;
varying vec4 cd;
void vertex() {
    cd = INSTANCE_CUSTOM;   // x size (m), y intensity, z spin, w star
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
    VERTEX *= cd.x;
}
void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float r = length(p);
    float glow = exp(-r * r * 5.0);
    float core = exp(-r * r * 38.0);
    float a = atan(p.y, p.x) + cd.z;
    float star = (pow(abs(cos(a * 2.0)), 40.0) + 0.6 * pow(abs(cos(a * 2.0 + 0.785)), 60.0)) * exp(-r * 2.2) * cd.w;
    float m = (glow * 0.6 + core * 1.8 + star) * cd.y * smoothstep(1.0, 0.8, r);
    ALBEDO = mix(COLOR.rgb * 1.4, vec3(1.0), clamp(core * 1.4 + star * 0.6, 0.0, 1.0)) * 1.6;
    ALPHA = clamp(m, 0.0, 1.0);
}
""";

    /// <summary>A growable-free strip buffer: vertices, tangents, colours, the two UV sets and indices; the tail past <see cref="Idx"/> is zero (degenerate triangles).</summary>
    sealed class Strips
    {
        public readonly Vector3[] Pos, Nrm; public readonly Color[] Col; public readonly Vector2[] Uv, Uv2; public readonly int[] Index;
        public int Verts, Idx, LastIdx;
        readonly Godot.Collections.Array _arrays = new();
        public Strips(int verts, int idx)
        {
            Pos = new Vector3[verts]; Nrm = new Vector3[verts]; Col = new Color[verts]; Uv = new Vector2[verts]; Uv2 = new Vector2[verts]; Index = new int[idx];
            for (int i = 0; i < verts; i++) Nrm[i] = Vector3.Up;
            _arrays.Resize((int)Mesh.ArrayType.Max);
        }
        public void Begin() { Verts = 0; Idx = 0; }
        public bool Room(int verts) => Verts + verts <= Pos.Length && Idx + (verts / 2) * 6 <= Index.Length;
        /// <summary>The buffers into <paramref name="mesh"/> (one surface).</summary>
        public void Commit(ArrayMesh mesh)
        {
            if (Idx < LastIdx) Array.Clear(Index, Idx, LastIdx - Idx);
            LastIdx = Idx;
            mesh.ClearSurfaces();
            if (Idx == 0) return;
            _arrays[(int)Mesh.ArrayType.Vertex] = Pos; _arrays[(int)Mesh.ArrayType.Normal] = Nrm; _arrays[(int)Mesh.ArrayType.Color] = Col;
            _arrays[(int)Mesh.ArrayType.TexUV] = Uv; _arrays[(int)Mesh.ArrayType.TexUV2] = Uv2; _arrays[(int)Mesh.ArrayType.Index] = Index;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _arrays);
        }
    }

    /// <summary>A small repeatable random stream (xorshift; no allocation), so a bolt's forks stay put between re-rolls.</summary>
    struct Rng
    {
        uint _s;
        public Rng(int seed) { _s = (uint)seed * 2654435761u + 0x9E3779B9u; if (_s == 0) _s = 1; }
        public float NextSingle() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (_s & 0xFFFFFF) / 16777216f; }
        public int Next(int n) => n <= 1 ? 0 : (int)(NextSingle() * n) % n;
    }

    readonly Strips _bolts = new(BoltVerts, BoltIdx), _ghost = new(BoltVerts, BoltIdx), _arcs = new(ArcVerts, ArcIdx);
    readonly ArrayMesh _boltMesh = new(), _ghostMesh = new(), _arcMesh = new();
    readonly ShaderMaterial _boltMat, _ghostMat, _arcMat;
    readonly MultiMesh _flares;
    readonly OmniLight3D _handLight, _hitLight;
    readonly Random _roll = new(17);

    // The current links and hands (game space).
    readonly Vector3[] _from = new Vector3[MaxLinks], _to = new Vector3[MaxLinks];
    readonly Vector3[] _prevTo = new Vector3[MaxLinks];
    readonly float[] _flash = new float[MaxLinks];
    int _count, _prevCount;
    Vector3 _palmL, _palmR;
    bool _hands;
    float _charge;
    double _fedAt = double.NegativeInfinity, _castStart = double.NegativeInfinity, _nextJag;
    bool _fizzle;

    // Shapes: offsets per link (main + forks), re-rolled on each jag. Seeds keep forks stable between re-rolls of the frame.
    readonly float[] _offA = new float[(MaxLinks + 1) * MaxPoints], _offB = new float[(MaxLinks + 1) * MaxPoints];
    readonly int[] _seed = new int[MaxLinks + 1];
    float _restrike;
    double _ghostAt = double.NegativeInfinity;

    /// <summary>Strikes drawn since the node was made, and links drawn last frame (test aids, logs).</summary>
    public int Rejags { get; private set; }
    public int LinksDrawn { get; private set; }

    static int _lightFrame = -1, _lightsUsed;

    public LibrarianBolts()
    {
        Name = "LibrarianBolts";
        var shader = new Shader { Code = BoltShader };
        _boltMat = new ShaderMaterial { Shader = shader, RenderPriority = 4 };
        _ghostMat = new ShaderMaterial { Shader = shader, RenderPriority = 3 };
        _ghostMat.SetShaderParameter("core_energy", 0.7f);
        _ghostMat.SetShaderParameter("tint", new Vector3(0.75f, 0.55f, 1f));
        _arcMat = new ShaderMaterial { Shader = shader, RenderPriority = 4 };
        _arcMat.SetShaderParameter("core_energy", 2.2f);
        var big = new Aabb(new Vector3(-4000f, -4000f, -4000f), new Vector3(8000f, 8000f, 8000f));
        AddChild(new MeshInstance3D { Name = "Ghost", Mesh = _ghostMesh, MaterialOverride = _ghostMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = big });
        AddChild(new MeshInstance3D { Name = "Bolts", Mesh = _boltMesh, MaterialOverride = _boltMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = big });
        AddChild(new MeshInstance3D { Name = "Arcs", Mesh = _arcMesh, MaterialOverride = _arcMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = big });
        _flares = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, InstanceCount = Flares,
            Mesh = new QuadMesh { Size = Vector2.One, Material = new ShaderMaterial { Shader = new Shader { Code = FlareShader }, RenderPriority = 5 } },
        };
        _flares.VisibleInstanceCount = 0;
        AddChild(new MultiMeshInstance3D { Name = "Flares", Multimesh = _flares, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = big });
        _handLight = new OmniLight3D { Name = "HandLight", LightColor = new Color(0.55f, 0.72f, 1f), OmniRange = 7f, OmniAttenuation = 1.4f, ShadowEnabled = false, Visible = false };
        _hitLight = new OmniLight3D { Name = "HitLight", LightColor = new Color(0.6f, 0.75f, 1f), OmniRange = 8f, OmniAttenuation = 1.3f, ShadowEnabled = false, Visible = false };
        AddChild(_handLight);
        AddChild(_hitLight);
    }

    /// <summary>
    /// This frame's links (game space, at most <see cref="MaxLinks"/>): the own client's foreseen chain every frame, another caster's latest from the server. Ends that moved less than a few metres glide
    /// (a moving Ork, a new section), farther they jump (a new target). A target not among the last links flashes.
    /// </summary>
    public void Feed(ReadOnlySpan<(Vector3 From, Vector3 To)> links, double now, float delta)
    {
        int n = Math.Min(links.Length, MaxLinks);
        if (now - _fedAt > FadeSeconds) { _castStart = now; _count = 0; _prevCount = 0; _nextJag = 0; }
        float k = 1f - MathF.Exp(-delta / 0.035f);
        for (int i = 0; i < n; i++)
        {
            var (f, t) = links[i];
            bool keep = i < _count && _to[i].DistanceTo(t) < 2.5f && _from[i].DistanceTo(f) < 2.5f;
            _from[i] = keep ? _from[i].Lerp(f, k) : f;
            _to[i] = keep ? _to[i].Lerp(t, k) : t;
            bool known = false;
            for (int j = 0; j < _prevCount && !known; j++) known = _prevTo[j].DistanceSquaredTo(t) < 0.9f * 0.9f;
            if (!known) _flash[i] = 1f;
        }
        for (int i = 0; i < n; i++) _prevTo[i] = links[i].To;
        _prevCount = n;
        _count = n;
        _fizzle = n == 1 && links[0].From.DistanceTo(links[0].To) > LibrarianRules.LightningRange - 2.5f;   // nothing in the sights: the bolt crackles out to its full reach
        _fedAt = now;
    }

    /// <summary>The palms' centres (game space) when the cast pose has them, and the charge (0..1) the hand arcs build with; every frame.</summary>
    public void SetHands(bool valid, Vector3 left, Vector3 right, float charge, double now)
    {
        _hands = valid; _palmL = left; _palmR = right; _charge = Math.Clamp(charge, 0f, 1f);
        if (valid && charge > 0f && now - _fedAt > FadeSeconds && now - _castStart > FadeSeconds) _castStart = now;
    }

    public override void _Process(double delta)
    {
        double now = Now;
        float since = (float)(now - _fedAt);
        float alive = Math.Clamp(1f - since / FadeSeconds, 0f, 1f);
        float after = Math.Clamp(1f - since / AfterglowSeconds, 0f, 1f);
        bool handsOn = _hands && _charge > 0.01f;
        if (alive <= 0f && after <= 0f && !handsOn)
        {
            if (_boltsShown) Hide3();
            return;
        }
        _boltsShown = true;
        float dt = (float)delta;
        for (int i = 0; i < MaxLinks; i++) _flash[i] = MathF.Max(0f, _flash[i] - dt * 5f);
        _restrike = MathF.Max(0f, _restrike - dt * 9f);

        if (alive > 0f && now >= _nextJag)
        {
            // A re-strike: the shape now becomes the afterglow, a new one is rolled.
            if (now - _castStart > 0.02) { BuildBolts(_ghost, alive); _ghost.Commit(_ghostMesh); _ghostAt = now; }   // not the first strike: no old shape to leave behind
            for (int i = 0; i < Math.Min(_count, MaxLinks); i++) _seed[i] = _roll.Next();
            _seed[MaxLinks] = _roll.Next();
            RollShapes();
            _nextJag = now + RejagMin + (RejagMax - RejagMin) * _roll.NextSingle();
            _restrike = 1f;
            Rejags++;
        }
        float flicker = 0.85f + 0.3f * _roll.NextSingle();
        float start = (float)Math.Clamp((now - _castStart) / 0.08, 0.0, 1.0);   // the first strike flares hardest
        _boltMat.SetShaderParameter("intensity", alive * flicker * (1f + 0.6f * _restrike + 0.8f * (1f - start)));
        _ghostMat.SetShaderParameter("intensity", 0.55f * alive * Math.Clamp(1f - (float)(now - _ghostAt) / GhostSeconds, 0f, 1f));
        _arcMat.SetShaderParameter("intensity", MathF.Max(alive, handsOn ? _charge : 0f) * (0.8f + 0.35f * _roll.NextSingle()));
        if (alive > 0f) { BuildBolts(_bolts, alive); _bolts.Commit(_boltMesh); LinksDrawn = _count; }
        else { _boltMesh.ClearSurfaces(); _bolts.LastIdx = 0; LinksDrawn = 0; }
        if ((now - _ghostAt) > GhostSeconds) { _ghostMesh.ClearSurfaces(); _ghost.LastIdx = 0; }
        BuildArcs(alive, handsOn, now);
        _arcs.Commit(_arcMesh);
        UpdateFlares(alive, after, handsOn, now);
        UpdateLights(alive, handsOn, flicker);
    }

    bool _boltsShown;

    void Hide3()
    {
        _boltsShown = false;
        _boltMesh.ClearSurfaces(); _ghostMesh.ClearSurfaces(); _arcMesh.ClearSurfaces();
        _bolts.LastIdx = _ghost.LastIdx = _arcs.LastIdx = 0;
        _flares.VisibleInstanceCount = 0;
        _handLight.Visible = _hitLight.Visible = false;
    }

    // ---- shapes ----

    /// <summary>Rolls every link's main-bolt offsets by midpoint displacement: the ends fixed, each level halving the push (two side axes).</summary>
    void RollShapes()
    {
        int links = Math.Min(_count, MaxLinks);
        for (int l = 0; l <= links; l++)
        {
            int slot = l < links ? l : MaxLinks;   // the last one rolled: the second palm's bolt
            int baseAt = slot * MaxPoints;
            _offA[baseAt] = _offB[baseAt] = 0f; _offA[baseAt + MaxPoints - 1] = _offB[baseAt + MaxPoints - 1] = 0f;
            Displace(baseAt, 0, MaxPoints - 1, 1f);
        }
    }

    void Displace(int baseAt, int a, int b, float amount)
    {
        if (b - a < 2) return;
        int m = (a + b) / 2;
        _offA[baseAt + m] = (_offA[baseAt + a] + _offA[baseAt + b]) * 0.5f + (_roll.NextSingle() * 2f - 1f) * amount;
        _offB[baseAt + m] = (_offB[baseAt + a] + _offB[baseAt + b]) * 0.5f + (_roll.NextSingle() * 2f - 1f) * amount;
        Displace(baseAt, a, m, amount * 0.55f);
        Displace(baseAt, m, b, amount * 0.55f);
    }

    static void Frame(Vector3 dir, out Vector3 s, out Vector3 t)
    {
        var u = MathF.Abs(dir.Z) > 0.95f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 0f, 1f);   // game space: Z up
        s = dir.Cross(u).Normalized();
        t = dir.Cross(s).Normalized();
    }

    readonly Vector3[] _pts = new Vector3[MaxPoints];
    readonly float[] _widths = new float[MaxPoints];

    void BuildBolts(Strips into, float alive)
    {
        into.Begin();
        int n = Math.Min(_count, MaxLinks);
        for (int i = 0; i < n; i++)
        {
            float fall = MathF.Pow(0.9f, i);
            float width = MainHalfWidth * (0.35f + 0.65f * fall) * (1f + 0.5f * _flash[i]);
            var colour = new Color(Glow.R, Glow.G, Glow.B, 0.55f + 0.45f * fall);
            if (i == 0 && _hands)
            {
                // Both palms: two bolts that meet on the first target, each a little thinner.
                MainBolt(into, _palmL, _to[0], 0, width * 0.85f, colour, i);
                MainBolt(into, _palmR, _to[0], MaxLinks, width * 0.85f, colour, i);
            }
            else MainBolt(into, _from[i], _to[i], i, width, colour, i);
        }
    }

    /// <summary>One main bolt from <paramref name="a"/> to <paramref name="b"/> with the offsets of slot <paramref name="slot"/>, then its forks.</summary>
    void MainBolt(Strips into, Vector3 a, Vector3 b, int slot, float width, Color colour, int link)
    {
        var line = b - a;
        float length = line.Length();
        if (length < 0.05f) return;
        var dir = line / length;
        Frame(dir, out var s, out var t);
        float amp = Math.Clamp(0.11f * length, 0.15f, 2.2f) * (_fizzle ? 1.4f : 1f);
        int points = length < 3f ? 9 : length < 8f ? 17 : MaxPoints;
        int stride = (MaxPoints - 1) / (points - 1);
        int baseAt = slot * MaxPoints;
        for (int k = 0; k < points; k++)
        {
            int at = k * stride;
            float u = at / (float)(MaxPoints - 1);
            _pts[k] = a + line * u + (s * _offA[baseAt + at] + t * _offB[baseAt + at]) * amp;
            float taper = _fizzle ? 1f - 0.75f * u : 0.75f + 0.25f * MathF.Sin(MathF.PI * u) + 0.15f * (1f - u);
            _widths[k] = width * taper;
        }
        Strip(into, _pts, _widths, points, colour);

        // Forks: off the main bolt, leaning forward along it; the first links have more, longer ones; a fizzle sprays at its far end.
        var rnd = new Rng(_seed[Math.Min(slot, MaxLinks)] ^ (link * 7919));
        int forks = (link < 4 ? 3 : link < 14 ? 2 : 1) + (_fizzle ? 3 : 0) + (length > 12f ? 1 : 0);
        for (int f = 0; f < forks; f++)
        {
            int k0 = _fizzle && f >= forks - 3 ? points - 2 - rnd.Next(2) : 1 + rnd.Next(Math.Max(1, points - 3));
            var root = _pts[k0];
            float forkLen = Math.Clamp(length * (0.1f + 0.18f * rnd.NextSingle()), 0.4f, 3.5f);
            var side = (s * MathF.Cos(rnd.NextSingle() * MathF.Tau) + t * MathF.Sin(rnd.NextSingle() * MathF.Tau)).Normalized();
            var fdir = (dir * (0.9f + 0.6f * rnd.NextSingle()) + side * (0.5f + 0.45f * rnd.NextSingle())).Normalized();
            fdir = new Vector3(fdir.X, fdir.Y, Math.Clamp(fdir.Z, -0.45f, 0.45f)).Normalized();   // forks spread out sideways, not up into the sky
            float fw = _widths[k0] * 0.5f;
            Branch(into, root, fdir, forkLen, fw, new Color(ForkGlow.R, ForkGlow.G, ForkGlow.B, colour.A * 0.85f), ref rnd, 2);
        }
    }

    readonly Vector3[] _bpts = new Vector3[12];
    readonly float[] _bw = new float[12];

    /// <summary>A fork: a short jagged run from <paramref name="root"/> along <paramref name="dir"/>, tapering to nothing, with a sub-fork now and then (<paramref name="depth"/> levels).</summary>
    void Branch(Strips into, Vector3 root, Vector3 dir, float length, float width, Color colour, ref Rng rnd, int depth)
    {
        int points = length > 2.5f ? 9 : 6;
        Frame(dir, out var s, out var t);
        var p = root;
        float step = length / (points - 1);
        var heading = dir;
        for (int k = 0; k < points; k++)
        {
            _bpts[k] = p;
            _bw[k] = width * (1f - k / (float)(points - 1) * 0.92f);
            heading = (heading + (s * (rnd.NextSingle() * 2f - 1f) + t * (rnd.NextSingle() * 2f - 1f)) * 0.6f).Normalized();
            heading = new Vector3(heading.X, heading.Y, Math.Clamp(heading.Z, -0.55f, 0.55f)).Normalized();
            p += heading * step;
        }
        Strip(into, _bpts, _bw, points, colour);
        if (depth > 1 && rnd.NextSingle() < 0.45f)
        {
            int k0 = 1 + rnd.Next(points - 2);
            var sub = (dir + (s * (rnd.NextSingle() * 2f - 1f) + t * (rnd.NextSingle() * 2f - 1f))).Normalized();
            var root2 = _bpts[k0];
            Branch(into, root2, sub, length * 0.5f, width * 0.6f, new Color(colour.R, colour.G, colour.B, colour.A * 0.8f), ref rnd, depth - 1);
        }
    }

    /// <summary>A camera-facing strip through <paramref name="count"/> points: two vertices a point (the shader pushes them apart across the view), tangents averaged at the joints.</summary>
    static void Strip(Strips into, Vector3[] pts, float[] widths, int count, Color colour)
    {
        if (count < 2 || !into.Room(count * 2)) return;
        int v0 = into.Verts;
        float along = 0f;
        for (int k = 0; k < count; k++)
        {
            var prev = pts[Math.Max(0, k - 1)]; var next = pts[Math.Min(count - 1, k + 1)];
            var tan = next - prev;
            tan = tan.LengthSquared() > 1e-10f ? tan.Normalized() : Vector3.Up;
            if (k > 0) along += pts[k].DistanceTo(pts[k - 1]);
            for (int side = 0; side < 2; side++)
            {
                int v = into.Verts++;
                into.Pos[v] = pts[k]; into.Nrm[v] = tan; into.Col[v] = colour;
                into.Uv[v] = new Vector2(along, side); into.Uv2[v] = new Vector2(side == 0 ? -widths[k] : widths[k], 0f);
            }
        }
        for (int k = 0; k + 1 < count; k++)
        {
            int a = v0 + k * 2;
            into.Index[into.Idx++] = a; into.Index[into.Idx++] = a + 1; into.Index[into.Idx++] = a + 2;
            into.Index[into.Idx++] = a + 1; into.Index[into.Idx++] = a + 3; into.Index[into.Idx++] = a + 2;
        }
    }

    double _arcsAt = double.NegativeInfinity;
    readonly Vector3[] _apts = new Vector3[10];
    readonly float[] _aw = new float[10];

    /// <summary>The hand arcs (over and between the palms) and the impact bursts on the struck chests, re-rolled every 30 ms.</summary>
    void BuildArcs(float alive, bool handsOn, double now)
    {
        if (now - _arcsAt < 0.03 && _arcs.Idx > 0) return;
        _arcsAt = now;
        _arcs.Begin();
        if (handsOn || (alive > 0f && _hands))
        {
            float c = MathF.Max(_charge, alive);
            var mid = (_palmL + _palmR) * 0.5f;
            for (int h = 0; h < 2; h++)
            {
                var palm = h == 0 ? _palmL : _palmR;
                int arcs = 2 + (int)(3 * c);
                for (int k = 0; k < arcs; k++)
                {
                    var dir = new Vector3(_roll.NextSingle() * 2f - 1f, _roll.NextSingle() * 2f - 1f, _roll.NextSingle() * 2f - 1f).Normalized();
                    Jagged(palm, palm + dir * (0.12f + 0.28f * _roll.NextSingle() * c), 6, 0.05f, 0.022f, new Color(ForkGlow.R, ForkGlow.G, ForkGlow.B, 0.9f));
                }
            }
            for (int k = 0; k < 2; k++) Jagged(_palmL, _palmR, 8, 0.07f + 0.05f * c, 0.03f, new Color(Glow.R, Glow.G, Glow.B, 0.95f));   // between the hands
            if (_count > 0 && alive > 0f) for (int k = 0; k < 2; k++) Jagged(mid, mid + (_to[0] - mid).Normalized() * 0.6f, 6, 0.08f, 0.04f, new Color(1f, 1f, 1f, 0.8f));
        }
        if (alive > 0f && !_fizzle)
        {
            int n = Math.Min(_count, MaxLinks);
            for (int i = 0; i < n; i++)
            {
                int spikes = (i < 10 ? 3 : 1) + (_flash[i] > 0.3f ? 3 : 0);
                var incoming = (_to[i] - _from[i]).Normalized();
                for (int k = 0; k < spikes; k++)
                {
                    var dir = (new Vector3(_roll.NextSingle() * 2f - 1f, _roll.NextSingle() * 2f - 1f, _roll.NextSingle() * 2f - 1f).Normalized() + incoming * 0.6f).Normalized();
                    float len = (0.3f + 0.5f * _roll.NextSingle()) * (1f + _flash[i]);
                    Jagged(_to[i], _to[i] + dir * len, 6, 0.08f, 0.035f * (1f + _flash[i]), new Color(ForkGlow.R, ForkGlow.G, ForkGlow.B, 0.9f));
                }
            }
        }
    }

    void Jagged(Vector3 a, Vector3 b, int points, float jag, float width, Color colour)
    {
        var line = b - a;
        float length = line.Length();
        if (length < 0.02f) return;
        Frame(line / length, out var s, out var t);
        for (int k = 0; k < points; k++)
        {
            float u = k / (float)(points - 1);
            float env = MathF.Sin(MathF.PI * u);
            _apts[k] = a + line * u + (s * (_roll.NextSingle() * 2f - 1f) + t * (_roll.NextSingle() * 2f - 1f)) * (jag * env * MathF.Max(1f, length));
            _aw[k] = width * (1f - 0.7f * u);
        }
        Strip(_arcs, _apts, _aw, points, colour);
    }

    // ---- flares and lights ----

    void UpdateFlares(float alive, float after, bool handsOn, double now)
    {
        int f = 0;
        float pulse = 0.85f + 0.3f * _roll.NextSingle();
        if (_hands && (handsOn || alive > 0f))
        {
            float c = MathF.Max(_charge, alive);
            SetFlare(f++, _palmL, (0.35f + 0.35f * c) * pulse, 0.8f * c, 0.6f, Glow);
            SetFlare(f++, _palmR, (0.35f + 0.35f * c) * (1.15f - 0.3f * _roll.NextSingle()), 0.8f * c, 0.6f, Glow);
        }
        int n = Math.Min(_count, MaxLinks);
        float a = MathF.Max(alive, after * 0.6f);
        if (a > 0f && !_fizzle)
            for (int i = 0; i < n && f < Flares; i++)
            {
                float fall = MathF.Pow(0.92f, i);
                SetFlare(f++, _to[i], (0.9f + 1.2f * _flash[i]) * (0.6f + 0.4f * fall) * pulse, a * (0.7f + 0.6f * _flash[i]), 1f, i == 0 ? Glow : DeepGlow);
            }
        _flares.VisibleInstanceCount = f;
    }

    void SetFlare(int i, Vector3 at, float size, float intensity, float star, Color colour)
    {
        _flares.SetInstanceTransform(i, new Transform3D(Basis.Identity, at));
        _flares.SetInstanceColor(i, colour);
        _flares.SetInstanceCustomData(i, new Color(size, intensity, _roll.NextSingle() * MathF.Tau, star));
    }

    void UpdateLights(float alive, bool handsOn, float flicker)
    {
        int frame = (int)Engine.GetProcessFrames();
        if (frame != _lightFrame) { _lightFrame = frame; _lightsUsed = 0; }
        bool hand = _hands && (alive > 0f || handsOn) && _lightsUsed < LightBudget;
        if (hand) _lightsUsed++;
        _handLight.Visible = hand;
        if (hand) { _handLight.Position = (_palmL + _palmR) * 0.5f; _handLight.LightEnergy = 3.5f * MathF.Max(alive, _charge) * flicker; }
        bool hit = alive > 0f && _count > 0 && !_fizzle && _lightsUsed < LightBudget;
        if (hit) _lightsUsed++;
        _hitLight.Visible = hit;
        if (hit) { _hitLight.Position = _to[0]; _hitLight.LightEnergy = (3f + 3f * _flash[0]) * alive * flicker; }
    }
}
