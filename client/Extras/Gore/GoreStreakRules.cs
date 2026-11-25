using System;
using System.Linq;
using System.Numerics;
using SmSim;

namespace SmClient.ImpactRules;

/// <summary>
/// gore-streak (added content: gore; owner 2026-11-25, decisions/2026-11-25-gore-reference-sequence.md in the coordination repository): the engine-free rules of the blood streak every
/// health hit plays in Warfront and in Classic PvP with the Extended profile's gore. One streak is one flat spray on a tilted plane through the wound: frame 0 a small swipe, short on the
/// entry side and long on the exit side; it expands and spreads outward on that plane through frames 1 and 2; the outer parts begin fading at frame 3 and grow again while fading at
/// frame 4; it erodes into patches, blobs and a faint haze until it is gone at <see cref="LifeSeconds"/>.
/// <list type="bullet">
/// <item><see cref="Keys"/> and <see cref="Sample"/>: the per-frame table (an interpretation of the owner's reference frames, provisional until the owner sees the first capture sheet;
/// spec/behavior.md "Gore streak"). The shader of <c>GoreStreakView</c> carries the same table (generated from it) and the same shape (<see cref="Coverage"/> mirrors it for the tests).</item>
/// <item><see cref="Draws"/>, <see cref="Excluded"/>: who draws it (Warfront, and Classic with <see cref="ContentExtras.Gore"/>; never Faithful or Musou alone) and what never does
/// (plasma, melta and any other cone weapon such as a flamer).</item>
/// <item><see cref="WeightOf"/>, <see cref="Scale"/>: same shape, scaled by weapon (owner card answer), bigger on a kill.</item>
/// <item><see cref="Plane"/>, <see cref="CameraBias"/>, <see cref="ExplosionAxis"/>: the plane the streak lies on (along the attack's path, never axis-aligned).</item>
/// <item><see cref="GritTexels"/>: the tileable grit noise the shader erodes the spray with (made in code at startup; nothing is copied from any footage).</item>
/// <item><see cref="Composite"/>: the colour through the strip and blend passes: our darker blood red (owner card "Keep our darker red"), never white, pink or additive.</item>
/// </list>
/// Every scale, size and budget here is ours (spec kind "ours"); the per-frame table is kind "interpretation", confidence "provisional".
/// </summary>
public static class GoreStreakRules
{
    /// <summary>The command-line switch that restores repair 4's ribbons in Classic (and no streak in Warfront).</summary>
    public const string OldLookFlag = "old-gore-streak";
    /// <summary>The command-line switch that turns the streak off (Classic then draws no blood spray; Warfront keeps its Ork blood).</summary>
    public const string OffFlag = "no-gore-streak";

    /// <summary>One row of the per-frame table (lengths and widths in victim body heights H; fan in degrees; shares 0..1).</summary>
    /// <param name="T">Seconds after contact.</param>
    /// <param name="Exit">Reach of the longest exit branch (H).</param>
    /// <param name="Entry">Reach of the entry stub behind the wound (H).</param>
    /// <param name="Fan">Half-fan of the exit branches (degrees: the upper branch's angle from the exit axis).</param>
    /// <param name="Width">Half width of the main branch at its widest (H).</param>
    /// <param name="Fill">Share of the shape that is blood (the erosion threshold is 1 - Fill).</param>
    /// <param name="Opacity">Opacity of a covered texel.</param>
    /// <param name="Grit">Pinhole amount (0 none, 1 most): it grows as the spray fades.</param>
    /// <param name="Branches">Exit branches drawn (fractional: the last one thinner).</param>
    /// <param name="Outer">How much the outer part of every branch has faded (0 none).</param>
    /// <param name="Root">How solid the first third of every branch is held (1 frames 0-4: one mass leaving the wound; 0 once it breaks up).</param>
    public readonly record struct Key(float T, float Exit, float Entry, float Fan, float Width, float Fill, float Opacity, float Grit, float Branches, float Outer, float Root);

    /// <summary>
    /// The per-frame table (interpretation of reference frames 0..10, provisional; design state/design-gore-streak-1125.md), calibrated (lead 40, 2026-11-25) so the drawn shape's measured
    /// exit and entry reach and covered area per body height (<see cref="Measure"/>) follow the reference's measured curves from the scoring tools (exit about 1.25 H at frame 0 rising to
    /// about 2 H, area peaking at frame 2, dipping at 3, regrowing at 4, then falling to nothing). The geometric reaches here are longer than the measured ones: the tips taper and erode.
    /// The blood hangs in place (the reach creeps a little, it never travels); the entry stub fades out after frame 8. Tunable: the shader is generated from this table.
    /// </summary>
    public static readonly Key[] Keys =
    [
        new(0.00f, 1.30f, 0.50f,    6f, 0.028f, 0.60f, 1.00f, 0.05f, 1.3f, 0.00f, 1.00f),
        new(0.05f, 1.60f, 0.70f,   22f, 0.078f, 0.62f, 1.00f, 0.08f, 3.0f, 0.00f, 1.00f),
        new(0.10f, 1.85f, 0.80f,   28f, 0.090f, 0.58f, 0.95f, 0.12f, 3.0f, 0.00f, 1.00f),
        new(0.18f, 1.85f, 0.82f,   27f, 0.100f, 0.48f, 0.85f, 0.20f, 3.0f, 0.30f, 1.00f),
        new(0.27f, 1.95f, 0.85f,   34f, 0.114f, 0.52f, 0.75f, 0.28f, 3.0f, 0.20f, 0.90f),
        new(0.37f, 2.00f, 0.86f,   32f, 0.120f, 0.46f, 0.65f, 0.36f, 3.0f, 0.18f, 0.60f),
        new(0.48f, 2.05f, 0.86f,   30f, 0.123f, 0.40f, 0.50f, 0.44f, 3.0f, 0.12f, 0.30f),
        new(0.60f, 2.05f, 0.85f,   30f, 0.126f, 0.35f, 0.40f, 0.52f, 3.0f, 0.08f, 0.10f),
        new(0.72f, 2.10f, 0.80f,   28f, 0.129f, 0.32f, 0.25f, 0.60f, 3.0f, 0.05f, 0.00f),
        new(0.85f, 2.10f, 0.50f,   26f, 0.129f, 0.26f, 0.12f, 0.66f, 3.0f, 0.05f, 0.00f),
        new(0.98f, 2.10f, 0.35f,   24f, 0.129f, 0.13f, 0.05f, 0.70f, 3.0f, 0.05f, 0.00f),
    ];

    /// <summary>A streak's whole life (s): after the last key it fades to nothing by this age (ours).</summary>
    public const float LifeSeconds = 1.1f;

    /// <summary>The table at <paramref name="age"/> s, linear between keys; before contact and after <see cref="LifeSeconds"/> nothing shows (zero opacity and fill).</summary>
    public static Key Sample(float age)
    {
        if (!(age >= 0f) || age >= LifeSeconds) return Keys[0] with { T = age, Fill = 0f, Opacity = 0f };
        var last = Keys[^1];
        if (age >= last.T)
        {
            float k = 1f - (age - last.T) / (LifeSeconds - last.T);
            return last with { T = age, Fill = last.Fill * k, Opacity = last.Opacity * k };
        }
        int i = 0;
        while (i < Keys.Length - 2 && age >= Keys[i + 1].T) i++;
        var a = Keys[i]; var b = Keys[i + 1];
        float u = (age - a.T) / (b.T - a.T);
        return new Key(age, L(a.Exit, b.Exit, u), L(a.Entry, b.Entry, u), L(a.Fan, b.Fan, u), L(a.Width, b.Width, u), L(a.Fill, b.Fill, u), L(a.Opacity, b.Opacity, u),
            L(a.Grit, b.Grit, u), L(a.Branches, b.Branches, u), L(a.Outer, b.Outer, u), L(a.Root, b.Root, u));
        static float L(float x, float y, float t) => x + (y - x) * t;
    }

    // ---- who draws it ----

    /// <summary>
    /// Whether a match draws the streak (owner card 2026-11-25: "warfront and extended, off for faithful"; Musou Mode is archived): Warfront always, Classic PvP only with
    /// <see cref="ContentExtras.Gore"/> (the Extended profile), never Faithful or Musou alone; never with <paramref name="oldLook"/> (<c>--old-gore-streak</c>) or <paramref name="off"/>
    /// (<c>--no-gore-streak</c>).
    /// </summary>
    public static bool Draws(ContentExtras extras, bool oldLook, bool off = false)
    {
        if (oldLook || off) return false;
        if (extras.HasFlag(ContentExtras.Warfront)) return true;
        return !extras.HasFlag(ContentExtras.Musou) && extras.HasFlag(ContentExtras.Gore);
    }

    /// <summary>
    /// Whether a hit by <paramref name="wargear"/> (its data name, null when not known) of <paramref name="delivery"/> plays no streak (owner 2026-11-25: "except for plasma and melta
    /// damage"; card answer "Flamers: no"): any plasma weapon (its name holds "plasma"), and every cone weapon (the melta; a flamer if one is ever added), plus a name holding "melta" or
    /// "flamer" when the delivery is not known.
    /// </summary>
    public static bool Excluded(string? wargear, DeliveryKind? delivery = null)
    {
        if (delivery == DeliveryKind.Cone) return true;
        if (string.IsNullOrEmpty(wargear)) return false;
        return wargear.Contains("plasma", StringComparison.OrdinalIgnoreCase)
            || wargear.Contains("melta", StringComparison.OrdinalIgnoreCase)
            || wargear.Contains("flamer", StringComparison.OrdinalIgnoreCase);
    }

    // ---- size ----

    /// <summary>What drew the streak: the plane rule and the default weight follow it.</summary>
    public enum Source { Sweep, Thrust, Gunfire, Explosion, Charge }
    /// <summary>The weapon's weight class (same shape, scaled; owner card "Same shape, scaled by weapon").</summary>
    public enum Weight { Light, Normal, Heavy }

    /// <summary>The scale of each weight (ours): light 0.75, normal 1, heavy 1.35.</summary>
    public const float LightScale = 0.75f, NormalScale = 1f, HeavyScale = 1.35f;
    /// <summary>A killing hit's extra scale and the largest scale (ours; owner: "bigger on death").</summary>
    public const float LethalScale = 1.4f, MaxScale = 2f;

    /// <summary>A streak's scale: its weight's, times <see cref="LethalScale"/> on a kill, at most <see cref="MaxScale"/>.</summary>
    public static float Scale(Weight w, bool lethal)
    {
        float s = w switch { Weight.Light => LightScale, Weight.Heavy => HeavyScale, _ => NormalScale };
        return MathF.Min(MaxScale, lethal ? s * LethalScale : s);
    }

    /// <summary>
    /// A hit's weight (ours): explosions and charges heavy; gunfire heavy for the lascannon, heavy bolter, autocannon and missiles, light for pistols, else normal; melee heavy for a
    /// heavy or charged step and for hammers, mauls and power axes (a chain axe is the Chaos chainsword: normal), light for the knife and a light first step, else normal.
    /// </summary>
    public static Weight WeightOf(Source source, string? wargear, bool heavy = false, int step = 0)
    {
        if (source is Source.Explosion or Source.Charge) return Weight.Heavy;
        string w = wargear ?? "";
        bool Has(string s) => w.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (source == Source.Gunfire)
        {
            if (Has("lascannon") || Has("heavy_bolter") || Has("heavybolter") || Has("autocannon") || Has("missile") || Has("rocket") || Has("sniper")) return Weight.Heavy;   // sniper rifles heavy: lead 41 decision (ours, provisional)
            if (Has("pistol")) return Weight.Light;
            return Weight.Normal;
        }
        if (heavy || Has("hammer") || Has("maul") || (Has("axe") && !Has("chain_axe") && !Has("chainaxe"))) return Weight.Heavy;
        if (Has("knife") || step == 1) return Weight.Light;
        return Weight.Normal;
    }

    /// <summary>Classic PvP's body height for the streak's lengths (m; ours: a Space Marine about 2 m tall).</summary>
    public const float ClassicBodyHeight = 2f;

    // ---- the plane ----

    /// <summary>One streak to draw (Godot world, Y up): the wound, the exit axis and the plane's normal (unit, perpendicular), the body height, scale, seed, how aged it starts, the bend and whether it killed.</summary>
    /// <param name="PreAge">Seconds it starts already aged (negative: it waits that long, a tracer in flight).</param>
    /// <param name="Bend">The melee arc's curvature (-0.4..0.4; 0 straight).</param>
    public readonly record struct Streak(Vector3 Origin, Vector3 Axis, Vector3 Normal, float BodyHeight, float Scale, int Seed, float PreAge, float Bend, bool Lethal, bool Heavy = false)
    {
        /// <summary>The metres per table unit (H x scale, at most <see cref="MaxUnitMetres"/>).</summary>
        public float Unit => MathF.Min(BodyHeight * Scale, MaxUnitMetres);
        /// <summary>The plane's second axis (across the exit axis, in the plane).</summary>
        public Vector3 Across => Vector3.Normalize(Vector3.Cross(Normal, Axis));
    }

    /// <summary>A seeded hash in [0, 1) (stable across platforms).</summary>
    public static float Hash01(int seed, int salt)
    {
        uint h = unchecked((uint)seed * 0x9E3779B1u ^ (uint)salt * 0x85EBCA77u);
        h ^= h >> 15; h = unchecked(h * 0x2C1B3C6Du); h ^= h >> 12; h = unchecked(h * 0x297A2D39u); h ^= h >> 15;
        return (h & 0xFFFFFF) / 16777216f;
    }

    /// <summary>The seed as the shader receives it (instance custom data .y).</summary>
    public static float ShaderSeed(int seed) => (seed % 9973) * 0.001f + 0.5f;
    /// <summary>The shader's per-streak jitter hash (<c>h01</c>: fract(sin(seed x 12.9898 + salt x 78.233) x 43758.5453) of <see cref="ShaderSeed"/>); a GPU's sin differs in the last bits, so it matches in range, not bit for bit.</summary>
    public static float ShaderHash(int seed, float salt)
    {
        double v = Math.Sin(ShaderSeed(seed) * 12.9898 + salt * 78.233) * 43758.5453;
        return (float)(v - Math.Floor(v));
    }

    /// <summary>The largest seeded roll of the plane round the exit axis for a thrust, a shot or an explosion (degrees; ours).</summary>
    public const float RollDegrees = 40f;
    /// <summary>An explosion's streak leaves the victim lifted this far above the level (degrees; ours).</summary>
    public const float ExplosionLiftDegrees = 15f;
    /// <summary>How much the view's direction pulls the plane's normal toward the camera (ours), and the smallest |normal . toward camera| it keeps (the streak never seen edge-on).</summary>
    public const float CameraPull = 0.5f, MinFacing = 0.3f;

    /// <summary>A unit vector perpendicular to <paramref name="axis"/> (the "up-most" one: in the vertical plane through the axis; any when the axis is vertical).</summary>
    static Vector3 Perp(Vector3 axis, Vector3 up)
    {
        var p = up - axis * Vector3.Dot(up, axis);
        if (p.LengthSquared() < 1e-6f) { p = Vector3.UnitX - axis * Vector3.Dot(Vector3.UnitX, axis); if (p.LengthSquared() < 1e-6f) p = Vector3.UnitZ - axis * Vector3.Dot(Vector3.UnitZ, axis); }
        return Vector3.Normalize(p);
    }

    /// <summary><paramref name="v"/> turned round the unit <paramref name="axis"/> by <paramref name="radians"/>.</summary>
    public static Vector3 Rotate(Vector3 v, Vector3 axis, float radians) => Vector3.Transform(v, Quaternion.CreateFromAxisAngle(axis, radians));

    /// <summary>
    /// The plane's normal for a streak along unit <paramref name="axis"/>: a melee sweep's swing plane from the weapon's tangents before and after the contact
    /// (<paramref name="before"/>, <paramref name="after"/>; their cross), else the plane through the axis and the vertical (cross(axis, up)); a thrust, a shot, a charge and an
    /// explosion take that vertical-plane normal rolled round the axis by a seeded angle up to <see cref="RollDegrees"/>, so no two lie alike and none is axis-aligned. Always
    /// perpendicular to the axis.
    /// </summary>
    public static Vector3 Plane(Source source, Vector3 axis, Vector3 up, int seed, Vector3? before = null, Vector3? after = null)
    {
        axis = Vector3.Normalize(axis);
        var side = Vector3.Cross(axis, up);
        Vector3 n = side.LengthSquared() > 1e-6f ? Vector3.Normalize(side) : Vector3.Normalize(Vector3.Cross(axis, Perp(axis, up)));
        if (source == Source.Sweep && before is { } b && after is { } a)
        {
            var c = Vector3.Cross(b, a);
            var cp = c - axis * Vector3.Dot(c, axis);
            if (c.LengthSquared() > 1e-8f && cp.LengthSquared() > 0.04f * c.LengthSquared()) return Vector3.Normalize(cp);
        }
        if (source == Source.Sweep) return n;
        float roll = (Hash01(seed, 11) * 2f - 1f) * RollDegrees * MathF.PI / 180f;
        // at least a third of the roll either way (no plane exactly through the vertical)
        if (MathF.Abs(roll) < RollDegrees * MathF.PI / 540f) roll = MathF.CopySign(RollDegrees * MathF.PI / 540f, roll == 0f ? 1f : roll);
        return Vector3.Normalize(Rotate(n, axis, roll));
    }

    /// <summary>
    /// An explosion's exit axis: from the blast centre <paramref name="blast"/> out through the victim's chest <paramref name="chest"/> (its level part), lifted
    /// <see cref="ExplosionLiftDegrees"/>; a seeded level direction when the blast is under him.
    /// </summary>
    public static Vector3 ExplosionAxis(Vector3 chest, Vector3 blast, Vector3 up, int seed)
    {
        var d = chest - blast;
        d -= up * Vector3.Dot(d, up);
        if (d.LengthSquared() < 1e-4f)
        {
            var any = Perp(up, Vector3.UnitX);
            d = Rotate(any, up, Hash01(seed, 5) * MathF.Tau);
        }
        d = Vector3.Normalize(d);
        float lift = ExplosionLiftDegrees * MathF.PI / 180f;
        return Vector3.Normalize(d * MathF.Cos(lift) + up * MathF.Sin(lift));
    }

    /// <summary>
    /// The normal turned toward the camera round the exit axis (it stays perpendicular to the axis, so the streak still runs along the attack): pulled by <see cref="CameraPull"/> of
    /// the view direction's part across the axis, then turned further until |normal . toward camera| is at least <see cref="MinFacing"/> (unless the camera looks straight down the axis).
    /// </summary>
    public static Vector3 CameraBias(Vector3 normal, Vector3 axis, Vector3 toCamera)
    {
        axis = Vector3.Normalize(axis);
        if (toCamera.LengthSquared() < 1e-8f) return normal;
        var tc = Vector3.Normalize(toCamera);
        var v = tc - axis * Vector3.Dot(tc, axis);
        if (v.LengthSquared() < 1e-6f) return normal;
        v = Vector3.Normalize(v);
        var n = normal - axis * Vector3.Dot(normal, axis);
        n = n.LengthSquared() < 1e-8f ? v : Vector3.Normalize(n);
        if (Vector3.Dot(n, v) < 0f) n = -n;
        n = Vector3.Normalize(n + CameraPull * v);
        // the most facing a normal perpendicular to the axis can have is |v . tc|
        float reach = MathF.Abs(Vector3.Dot(v, tc));
        float want = MathF.Min(MinFacing, reach * 0.999f);
        for (int i = 0; i < 16 && MathF.Abs(Vector3.Dot(n, tc)) < want; i++) n = Vector3.Normalize(n + 0.25f * v);
        return n;
    }

    // ---- the shape (mirrored by the shader) ----

    /// <summary>The most exit branches (the fourth only on a heavy or killing hit).</summary>
    public const int MaxBranches = 4;
    /// <summary>The grit noise's tiles per table unit across a branch, its stretch along the branch (4:1, the speckle smeared along the spray), and the second octave's scale (ours; gate repair: fine streaky speckle, not blocky notches).</summary>
    public const float GritTiles = 3f, GritStretch = 4f, GritOctave = 2.37f;

    /// <summary>A branch's angle share of the half-fan, reach share, width share (seeded jitter added by <see cref="Branch"/>): upper, middle, lower, the heavy fourth (gate repair: visibly diverging, one upward).</summary>
    public static readonly (float Angle, float Reach, float Width)[] BranchShares = [(1.25f, 1f, 1f), (0.45f, 0.9f, 0.85f), (-0.3f, 0.8f, 0.72f), (-0.85f, 0.7f, 0.6f)];

    /// <summary>The soft layer under the cores: its width as a multiple of the core's, its opacity share, and the core's edge feather (table units; gate repair: a wide un-eroded low-alpha halo, edges feathered at least 0.03 H).</summary>
    public const float HaloWidth = 1.6f, HaloAlpha = 0.22f, Feather = 0.035f;
    /// <summary>The root (gate repair 1): the first <see cref="RootShare"/> of every branch is held solid by <see cref="RootBoost"/> x the key's root strength, so the branches and the entry stub leave the wound as one mass.</summary>
    public const float RootShare = 0.35f, RootBoost = 0.6f;

    /// <summary>The quad's extent in table units (gate repair, finding M2: tightened to the shape's bounds at the widest key, halo included): behind the wound, ahead of it, and to each side.</summary>
    public static readonly float QuadBack = Keys.Max(k => k.Entry) * 1.12f + 0.12f;
    public static readonly float QuadFwd = Keys.Max(k => k.Exit) * 1.12f + 0.05f;
    public static readonly float QuadSide = Keys.Max(k => MathF.Sin((BranchShares.Max(b => MathF.Abs(b.Angle)) * 1.15f + 0.1f) * k.Fan * MathF.PI / 180f) * k.Exit + k.Width * HaloWidth + Feather) + 0.03f;

    /// <summary>Exit branch <paramref name="k"/>'s angle (radians from the exit axis), reach and width shares for <paramref name="seed"/>.</summary>
    public static (float Angle, float Reach, float Width) Branch(int k, int seed, float fanDeg, float bias)
    {
        var b = BranchShares[k];
        float ja = 0.85f + 0.3f * ShaderHash(seed, 20 + k), jr = 0.9f + 0.1f * ShaderHash(seed, 30 + k);
        return ((b.Angle * ja + bias) * fanDeg * MathF.PI / 180f, b.Reach * jr, b.Width);
    }

    /// <summary>Along-branch width profile at share <paramref name="u"/> of its reach: already broad at the wound (the root), widest past the middle, tapering to a tip.</summary>
    public static float Profile(float u) => (0.7f + 0.3f * Smooth(0f, 0.4f, u)) * (1f - Smooth(0.78f, 1.06f, u));

    /// <summary>One sampled point of the shape: the core field (1 on a branch's middle line, 0 at its edge, feathered), the halo field, the root term, and the nearest branch's coordinates (s along, c across) for the grit.</summary>
    public readonly record struct ShapePoint(float Field, float Halo, float Root, float S, float C, float U);

    /// <summary>
    /// The shape at plane point (<paramref name="x"/> along the exit axis, <paramref name="y"/> across; table units) for key <paramref name="k"/>: the exit branches fanned by the half-fan,
    /// the entry stub behind the wound (its near end at the wound), each a feathered tapered stroke; the halo is the same strokes <see cref="HaloWidth"/> wide.
    /// </summary>
    public static ShapePoint Field(Key k, float x, float y, int seed, float bend, float bias, int maxBranches)
    {
        float best = 0f, halo = 0f, root = 0f, bs = 0f, bc = 0f, bu = 0f;
        int n = Math.Min(maxBranches, MaxBranches);
        for (int i = 0; i < n; i++)
        {
            float weight = Math.Clamp(k.Branches - Math.Min(i, 2), 0f, 1f);   // the heavy fourth comes and goes with the third
            if (weight <= 0f) continue;
            var (ang, reach, wid) = Branch(i, seed, k.Fan, bias);
            Eval(ang, k.Exit * reach, k.Width * wid * MathF.Sqrt(weight));
        }
        if (k.Entry > 0.01f)
        {
            float ang = MathF.PI + (ShaderHash(seed, 40) - 0.5f) * 0.3f * k.Fan * MathF.PI / 180f;
            Eval(ang, k.Entry, k.Width * 0.8f);
        }
        return new ShapePoint(best, halo, root, bs, bc, bu);

        void Eval(float ang, float L, float w0)
        {
            float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
            float s = x * cs + y * sn;
            float c = -x * sn + y * cs;
            c -= bend * s * s / MathF.Max(L, 0.05f);
            float u = s / MathF.Max(L, 1e-3f);
            if (u < -0.08f || u > 1.12f) return;
            float w = MathF.Max(w0 * Profile(u), 1e-4f);
            float along = Smooth(-0.08f, 0.0f, u);
            float f = (1f - Smooth(w - Feather, w + Feather, MathF.Abs(c))) * along;
            f *= 1f - MathF.Abs(c) / (w + Feather) * 0.5f;   // densest on the middle line
            f *= 1f - k.Outer * Smooth(0.55f, 1.0f, u);
            float h = (1f - Smooth(0f, w * HaloWidth + Feather, MathF.Abs(c))) * along * (1f - Smooth(0.85f, 1.12f, u));
            float r = (1f - Smooth(0.7f * RootShare, 1.15f * RootShare, u)) * (1f - Smooth(w - Feather, w + Feather, MathF.Abs(c))) * along;
            if (h > halo) halo = h;
            if (r > root) root = r;
            if (f > best) { best = f; bs = s; bc = c; bu = u; }
        }
    }

    /// <summary>
    /// A texel's core coverage (0..1, a soft step: no derivative of a discontinuous score, gate repair 7) and density at plane point (x, y) and key <paramref name="k"/> (the shader's
    /// fragment math): the score of the field, the two octaves of blotch and smear, and the root, against the erosion threshold 1 - Fill; pinholes where the fine grain beats the grit threshold.
    /// </summary>
    public static (float Cov, float Density, float Halo) Coverage(Key k, float x, float y, int seed, float bend, float bias, int maxBranches, byte[] grit, int size)
    {
        if (k.Opacity <= 0f || k.Fill <= 0f) return (0f, 0f, 0f);
        var p = Field(k, x, y, seed, bend, bias, maxBranches);
        if (p.Halo <= 0f && p.Field <= 0f) return (0f, 0f, 0f);
        var (coarse, fine, smear) = GritAt(grit, size, p.S, p.C, seed);
        float score = Score(p.Field, coarse, smear) + RootBoost * p.Root * k.Root;
        float cov = Smooth(1f - k.Fill - CovSoft, 1f - k.Fill + CovSoft, score);
        float holes = Smooth(HoleThreshold(k.Grit) - 0.04f, HoleThreshold(k.Grit) + 0.04f, fine) * (1f - p.Root * k.Root);
        cov *= 1f - holes;
        return (cov, Density(coarse), p.Halo);
    }

    /// <summary>The two-octave grit at branch point (s, c): stretched <see cref="GritStretch"/> along the branch, offset by the seed; the second octave <see cref="GritOctave"/> finer.</summary>
    public static (float Coarse, float Fine, float Smear) GritAt(byte[] grit, int size, float s, float c, int seed)
    {
        var (u, v) = GritUv(s, c, seed);
        var a = GritSample(grit, size, u, v);
        var b = GritSample(grit, size, u * GritOctave + 0.37f, v * GritOctave + 0.61f);
        return (0.62f * a.Coarse + 0.38f * b.Coarse, 0.5f * a.Fine + 0.5f * b.Fine, 0.6f * a.Smear + 0.4f * b.Smear);
    }

    /// <summary>The grit texture coordinate of branch point (s, c).</summary>
    public static (float U, float V) GritUv(float s, float c, int seed) => (s * GritTiles / GritStretch + ShaderHash(seed, 50), c * GritTiles + ShaderHash(seed, 51));
    /// <summary>The erosion score of a texel: continuous in the field (it fades in over the field's first fifth), the blotches and the smear (the shader's <c>score</c>).</summary>
    public static float Score(float field, float coarse, float smear) => (ScoreField * field + ScoreCoarse * coarse + ScoreSmear * smear) * Smooth(0f, 0.2f, field);
    /// <summary>The score's weights and the soft step's half width (ours).</summary>
    public const float ScoreField = 0.3f, ScoreCoarse = 0.55f, ScoreSmear = 0.15f, CovSoft = 0.05f;
    /// <summary>The fine-grain value above which a texel is a pinhole for <paramref name="grit"/> (0..1).</summary>
    public static float HoleThreshold(float grit) => 1f - 0.45f * grit;
    /// <summary>A covered texel's density (its opacity share) from the coarse blotch noise: brighter cores, thinner rims.</summary>
    public static float Density(float coarse) => 0.45f + 0.55f * coarse;

    /// <summary>A texel's blood opacity: the core at opacity x coverage x density over the halo at <see cref="HaloAlpha"/> x opacity (the shader's ALPHA).</summary>
    public static float Alpha(Key k, float cov, float density, float halo) => k.Opacity * MathF.Max(cov * density, HaloAlpha * halo);

    /// <summary>The covered core area (table units squared), the area with any visible blood (halo included, alpha over 0.05), and the farthest covered reach ahead of and behind the wound at <paramref name="age"/>, on a grid of <paramref name="step"/>.</summary>
    public static (float Area, float ExitReach, float EntryReach, float VisibleArea) Measure(float age, int seed, float bend, float bias, int maxBranches, byte[] grit, int size, float step = 0.02f)
    {
        var k = Sample(age);
        float area = 0f, vis = 0f, fwd = 0f, back = 0f;
        for (float y = -QuadSide; y <= QuadSide; y += step)
            for (float x = -QuadBack; x <= QuadFwd; x += step)
            {
                var (cov, dens, halo) = Coverage(k, x, y, seed, bend, bias, maxBranches, grit, size);
                if (Alpha(k, cov, dens, halo) > 0.05f) vis += step * step;
                if (cov <= 0.5f) continue;
                area += step * step;
                if (x > fwd) fwd = x;
                if (-x > back) back = -x;
            }
        return (area, fwd, back, vis);
    }

    // ---- grit ----

    /// <summary>The grit texture's size (texels a side).</summary>
    public const int GritSize = 256;

    /// <summary>
    /// The tileable grit noise (RGBA8, <paramref name="size"/>^2, deterministic for <paramref name="seed"/>): R coarse blotches (about a twelfth of a tile), G fine grain (a few texels),
    /// B a streaky smear (stretched 8:1 along U); A 255. Made in code at startup; nothing in it comes from footage.
    /// </summary>
    public static byte[] GritTexels(int size = GritSize, int seed = 1)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                float coarse = 0.65f * Periodic(u, v, 12, 12, seed) + 0.35f * Periodic(u, v, 24, 24, seed + 1);
                float fine = 0.7f * Periodic(u, v, 64, 64, seed + 2) + 0.3f * Periodic(u, v, 128, 128, seed + 3);
                float smear = 0.7f * Periodic(u, v, 8, 64, seed + 4) + 0.3f * Periodic(u, v, 16, 128, seed + 5);
                int i = (y * size + x) * 4;
                px[i] = ToByte(Contrast(coarse)); px[i + 1] = ToByte(Contrast(fine)); px[i + 2] = ToByte(Contrast(smear)); px[i + 3] = 255;
            }
        return px;
        static byte ToByte(float f) => (byte)Math.Clamp(MathF.Round(f * 255f), 0f, 255f);
        static float Contrast(float f) => Smooth(0.2f, 0.8f, f);
    }

    /// <summary>Value noise on a lattice of <paramref name="cx"/> by <paramref name="cy"/> cells that wraps at the tile's edges.</summary>
    static float Periodic(float u, float v, int cx, int cy, int seed)
    {
        float fx = u * cx, fy = v * cy;
        int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
        float tx = fx - ix, ty = fy - iy;
        tx = tx * tx * (3f - 2f * tx); ty = ty * ty * (3f - 2f * ty);
        float H(int a, int b) => Hash01(seed * 7919 + ((a % cx + cx) % cx) * 92821 + ((b % cy + cy) % cy) * 68917, 3);
        float a0 = H(ix, iy) + (H(ix + 1, iy) - H(ix, iy)) * tx;
        float a1 = H(ix, iy + 1) + (H(ix + 1, iy + 1) - H(ix, iy + 1)) * tx;
        return a0 + (a1 - a0) * ty;
    }

    /// <summary>The grit at texture coordinate (u, v) (wrapping, bilinear), each channel 0..1: coarse, fine, smear.</summary>
    public static (float Coarse, float Fine, float Smear) GritSample(byte[] px, int size, float u, float v)
    {
        float fx = (u - MathF.Floor(u)) * size - 0.5f, fy = (v - MathF.Floor(v)) * size - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        float C(int ch) {
            float T(int x, int y) => px[((((y % size) + size) % size) * size + (((x % size) + size) % size)) * 4 + ch] / 255f;
            float a = T(x0, y0) + (T(x0 + 1, y0) - T(x0, y0)) * tx;
            float b = T(x0, y0 + 1) + (T(x0 + 1, y0 + 1) - T(x0, y0 + 1)) * tx;
            return a + (b - a) * ty;
        }
        return (C(0), C(1), C(2));
    }

    // ---- colour ----

    /// <summary>
    /// The shade scale per renderer (gate repair 8, owner: keep our darker red): Forward+ draws in linear HDR with the tonemapper, Compatibility straight in display space, so the same
    /// albedo read about (229, 20, 0) on Forward+ and about (150, 0, 0) on Compatibility; Forward+ takes <see cref="ForwardShade"/> of it so both show about (150, 0, 0).
    /// </summary>
    public const float ForwardShade = 0.42f, CompatibilityShade = 1f;
    /// <summary>The strip pass's strength per unit of blood opacity, Forward+ and Compatibility (Compatibility blends in display space, where a fully stripped ground under a faint texel read near black and left the red mask) (gate repair 6: the strip fades with the opacity, so a faint texel keeps the ground's own tone, stripped toward red, and never goes near black on Compatibility).</summary>
    public const float StripPerAlpha = 5f, CompatibilityStripPerAlpha = 2.5f;
    /// <summary>Compatibility's strip: a blend_mix toward this dark blood red (display space) at the strip's strength, not a multiply (its multiply blend darkened even where it multiplied by 1).</summary>
    public const float CompatibilityStripRed = 0.0625f;

    /// <summary>The blood's albedo for <paramref name="density"/> (repair 4's darker red, 0.3 to 0.55 of the blood red) at the renderer's <paramref name="shade"/>.</summary>
    public static (float R, float G, float B) Colour(float density, float shade = CompatibilityShade)
    {
        float k = shade * (ClassicBloodRules.RibbonShadeFloor + (ClassicBloodRules.RibbonShadeTop - ClassicBloodRules.RibbonShadeFloor) * Smooth(0.15f, 0.85f, density));
        var b = ClassicBloodRules.BloodRgb;
        return (b.R * k, b.G * k, b.B * k);
    }

    /// <summary>The strip pass's strength for a texel of blood opacity <paramref name="alpha"/> and core coverage <paramref name="cov"/>.</summary>
    public static float Strip(float alpha, float cov, float perAlpha = StripPerAlpha) => Math.Clamp(perAlpha * alpha, 0f, 1f) * (0.75f + 0.25f * Math.Clamp(cov, 0f, 1f));

    /// <summary>
    /// The colour of a pixel <paramref name="under"/> beneath a streak texel of blood opacity <paramref name="alpha"/>, core coverage <paramref name="cov"/> and density after both passes:
    /// the strip (blend_mul: the red under it kept at <see cref="ClassicBloodRules.RibbonStripRed"/>, green and blue removed, at <see cref="Strip"/>) then the blood mixed over it.
    /// </summary>
    public static (float R, float G, float B) Composite((float R, float G, float B) under, float alpha, float cov, float density, float shade = CompatibilityShade, float perAlpha = StripPerAlpha, bool compat = false)
    {
        float s = Strip(alpha, cov, perAlpha);
        var u = compat
            ? (R: under.R * (1f - s) + s * CompatibilityStripRed, G: under.G * (1f - s), B: under.B * (1f - s))
            : (R: under.R * (1f - s + s * ClassicBloodRules.RibbonStripRed), G: under.G * (1f - s), B: under.B * (1f - s));
        var col = Colour(density, shade);
        float a = Math.Clamp(alpha, 0f, 1f);
        return (a * col.R + (1f - a) * u.R, a * col.G + (1f - a) * u.G, a * col.B + (1f - a) * u.B);
    }

    /// <summary>Linear to display (sRGB) for one channel.</summary>
    public static float ToSrgb(float l) => l <= 0.0031308f ? 12.92f * l : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;

    // ---- pool and budget ----

    /// <summary>The streak pool (MultiMesh instances; ours): the oldest is reused beyond it.</summary>
    public const int PoolSize = 512;
    /// <summary>The slot of the <paramref name="spawned"/>-th streak (a ring: spawn order is age order, so the slot reused is the oldest).</summary>
    public static int PoolSlot(long spawned, int pool = PoolSize) => (int)(spawned % pool);
    /// <summary>Whether a streak started at <paramref name="start"/> (s on the view's clock, pre-age applied) still shows at <paramref name="now"/>.</summary>
    public static bool Live(double start, double now) => now - start < LifeSeconds && now - start > -2.0;
    /// <summary>Whether a slot started at <paramref name="start"/> is spent at <paramref name="now"/> and is retired (its instance switched off: finding M1, no replay when the shader's clock wraps).</summary>
    public static bool Spent(double start, double now) => !double.IsNegativeInfinity(start) && now - start >= LifeSeconds;

    /// <summary>The largest metres per table unit (gate repair 9 and finding M2: a heavy lethal streak on a tall body stays within about 3 m a unit).</summary>
    public const float MaxUnitMetres = 3f;

    /// <summary>
    /// Warfront's budget (lead decision, gate repair 9; ours): a per-second token bucket for other players' hits (<see cref="WarfrontPerSecond"/>, burst <see cref="WarfrontBurst"/>), at most
    /// one streak per enemy every <see cref="WarfrontEnemySeconds"/>, at most <see cref="WarfrontLiveMax"/> showing (the own hits always, first), within <see cref="WarfrontMetres"/>, none for a body
    /// under <see cref="MinBodyPixels"/> on screen.
    /// </summary>
    public const float WarfrontPerSecond = 60f, WarfrontBurst = 24f, WarfrontEnemySeconds = 0.15f;
    public const int WarfrontLiveMax = 150;
    public const float WarfrontMetres = 45f, MinBodyPixels = 12f;
    /// <summary>A streak closer to the camera than this fades out (m; ours: Warfront's near hits filled the screen with wedges).</summary>
    public const float NearFadeFrom = 1.2f, NearFadeTo = 3.5f;

    /// <summary>A token bucket (engine-free): <see cref="Take"/> spends one token when there is one; tokens refill at the rate up to the burst.</summary>
    public sealed class Bucket(float perSecond, float burst)
    {
        double _tokens = burst, _at = double.NaN;
        public bool Take(double now)
        {
            if (double.IsNaN(_at)) _at = now;
            _tokens = Math.Min(burst, _tokens + Math.Max(0.0, now - _at) * perSecond);
            _at = now;
            if (_tokens < 1.0) return false;
            _tokens -= 1.0;
            return true;
        }
    }

    /// <summary>Whether an enemy last streaked at <paramref name="last"/> may streak again at <paramref name="now"/>.</summary>
    public static bool EnemyReady(double last, double now) => now - last >= WarfrontEnemySeconds;

    /// <summary>A body <paramref name="height"/> m tall at <paramref name="distance"/> m, seen with a vertical field of view <paramref name="fovYDegrees"/> on a <paramref name="screenHeight"/>-pixel view: its height in pixels.</summary>
    public static float ProjectedPixels(float height, float distance, float fovYDegrees, float screenHeight) =>
        distance <= 0.01f ? float.PositiveInfinity : height / (2f * distance * MathF.Tan(fovYDegrees * MathF.PI / 360f)) * screenHeight;

    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }
}
