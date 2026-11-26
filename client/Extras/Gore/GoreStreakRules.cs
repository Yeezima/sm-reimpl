using System;
using System.Linq;
using System.Numerics;
using SmSim;

namespace SmClient.ImpactRules;

/// <summary>
/// gore-streak (added content: gore; owner 2026-11-25, decisions/2026-11-25-gore-reference-sequence.md in the coordination repository): the engine-free rules of the blood streak every
/// health hit plays in Warfront and in Classic PvP with the Extended profile's gore. One streak is a few thin ribbons on a tilted plane through the wound (ribbon rebuild, owner card
/// 2026-11-26): frame 0 a small swipe, short on the entry side and long on the exit side; the bands expand and spread outward on that plane through frames 1 and 2; their outer parts
/// begin fading at frame 3 and grow again while fading at frame 4; from frame 6 each band breaks into patches and blobs and fades until it is gone at <see cref="LifeSeconds"/>.
/// <list type="bullet">
/// <item><see cref="Keys"/> and <see cref="Sample"/>: the per-frame table (an interpretation of the owner's reference frames, provisional; spec/behavior.md "Gore streak"). The shader
/// of <c>GoreStreakView</c> carries the same table and band layouts (generated from them); <see cref="BandOf"/>, <see cref="RibbonPoint"/> and <see cref="TexelAt"/> mirror its vertex and
/// fragment math for the tests.</item>
/// <item><see cref="Draws"/>, <see cref="Excluded"/>: who draws it (Warfront, and Classic with <see cref="ContentExtras.Gore"/>; never Faithful or Musou alone) and what never does
/// (plasma, melta and any other cone weapon such as a flamer).</item>
/// <item><see cref="WeightOf"/>, <see cref="Scale"/>, <see cref="LengthScale"/>: same shape, scaled by weapon (owner card answer), heavy weapons wider rather than longer, bigger on a kill.</item>
/// <item><see cref="Plane"/>, <see cref="CameraBias"/>, <see cref="ExplosionAxis"/>: the plane the streak lies on (along the attack's path, never axis-aligned).</item>
/// <item><see cref="FilamentTexels"/>: the cell texture the bands are drawn with (soft cellular mottle and clumps; made in code at startup; nothing is copied from any footage).</item>
/// <item><see cref="Composite"/>: the colour through the strip and blend passes: our darker blood red (owner card "Keep our darker red"), never white, pink or additive.</item>
/// </list>
/// Every scale, size and budget here is ours (spec kind "ours"); the per-frame table and the band layouts are kind "interpretation", confidence "provisional".
/// </summary>
public static class GoreStreakRules
{
    /// <summary>The command-line switch that restores repair 4's ribbons in Classic (and no streak in Warfront).</summary>
    public const string OldLookFlag = "old-gore-streak";
    /// <summary>The command-line switch that turns the streak off (Classic then draws no blood spray; Warfront keeps its Ork blood).</summary>
    public const string OffFlag = "no-gore-streak";

    /// <summary>One row of the per-frame table (lengths and widths in victim body heights H; fan in degrees; shares 0..1).</summary>
    /// <param name="T">Seconds after contact.</param>
    /// <param name="Exit">Reach of the main band ahead of the wound (H).</param>
    /// <param name="Entry">Reach of the entry band behind the wound (H).</param>
    /// <param name="Fan">The upper band's angle from the exit axis (degrees; the other bands take their <see cref="BandShape.Angle"/> share of it).</param>
    /// <param name="Width">Half width of a band's core at its widest (H; the mist under it is <see cref="MistTo"/> times wider).</param>
    /// <param name="Fill">Share of a band the patch erosion keeps (1 whole; the erosion threshold is 1 - Fill).</param>
    /// <param name="Opacity">Opacity of a band.</param>
    /// <param name="Grit">How far the cells open up (0 none, 1 most): it grows as the spray fades.</param>
    /// <param name="Branches">1 one swipe; 2 the side bands fully grown.</param>
    /// <param name="Outer">How much the outer part of every band has faded (0 none).</param>
    /// <param name="Root">How whole the first part of every band is held (1 frames 0-4: one mass leaving the wound; 0 once it breaks up).</param>
    /// <param name="Mist">How much of the soft mist body shows round the cores (gore repair round 3: the reference's soft spray; 0 none, 1 all).</param>
    /// <param name="Red">The share of the ground's red the strip keeps (1 early; it falls as the spray fades, so the blood darkens and its red excess falls, never lightening to pink).</param>
    public readonly record struct Key(float T, float Exit, float Entry, float Fan, float Width, float Fill, float Opacity, float Grit, float Branches, float Outer, float Root, float Mist, float Red);

    /// <summary>
    /// The per-frame table (interpretation of reference frames 0..10, provisional; design state/design-gore-streak-1125.md, the ribbon rebuild and repair round 3), set so the drawn
    /// streak's measured exit and entry reach, fan, fill and area per body height follow the reference's measured curves from the scoring tools (exit about 1.25 H at frame 0 rising to
    /// about 2 H and held through frame 9; area about 0.07 H^2 at frame 0, 0.25 to 0.37 H^2 in frames 1 to 5 peaking at 2, dipping at 3, regrowing at 4, then falling to about
    /// 0.006 H^2 at frame 10; half-fan about 5 degrees at frame 0, 23 to 31 in frames 2 to 8). The blood hangs in place (the reach creeps a little, it never travels); the entry band
    /// lasts to frame 8 and is gone at frame 9. Tunable: the shader is generated from this table.
    /// </summary>
    public static readonly Key[] Keys =
    [
        //   T      Exit   Entry  Fan   Width   Fill   Opac   Grit   Br    Outer  Root   Mist   Red
        new(0.00f, 1.50f, 0.44f,  4f, 0.010f, 1.00f, 0.95f, 0.00f, 1.0f, 0.00f, 1.00f, 0.30f, 1.00f),
        new(0.05f, 1.90f, 0.81f, 12f, 0.025f, 1.00f, 0.90f, 0.05f, 1.7f, 0.00f, 1.00f, 0.80f, 1.00f),
        new(0.10f, 2.20f, 0.88f, 16f, 0.028f, 1.00f, 0.86f, 0.10f, 2.0f, 0.00f, 1.00f, 1.00f, 1.00f),
        new(0.18f, 2.20f, 0.90f, 16f, 0.028f, 0.90f, 0.76f, 0.15f, 2.0f, 0.55f, 0.90f, 0.50f, 0.88f),
        new(0.27f, 2.40f, 0.95f, 20f, 0.033f, 0.90f, 0.70f, 0.20f, 2.0f, 0.20f, 0.70f, 1.00f, 0.92f),
        new(0.37f, 2.52f, 1.02f, 19f, 0.034f, 0.78f, 0.62f, 0.28f, 2.0f, 0.12f, 0.40f, 0.85f, 0.85f),
        new(0.48f, 2.66f, 1.07f, 18f, 0.035f, 0.64f, 0.56f, 0.36f, 2.0f, 0.06f, 0.00f, 0.72f, 0.78f),
        new(0.60f, 2.74f, 1.00f, 18f, 0.035f, 0.54f, 0.50f, 0.44f, 2.0f, 0.04f, 0.00f, 0.60f, 0.72f),
        new(0.72f, 2.82f, 0.95f, 17f, 0.035f, 0.46f, 0.44f, 0.50f, 2.0f, 0.04f, 0.00f, 0.48f, 0.66f),
        new(0.85f, 2.84f, 0.13f, 16f, 0.034f, 0.33f, 0.38f, 0.56f, 2.0f, 0.08f, 0.00f, 0.30f, 0.60f),
        new(0.98f, 2.84f, 0.07f, 13f, 0.033f, 0.07f, 0.26f, 0.60f, 2.0f, 0.70f, 0.00f, 0.08f, 0.55f),
    ];

    /// <summary>A streak's whole life (s): after the last key it fades to nothing by this age (ours).</summary>
    public const float LifeSeconds = 1.1f;

    /// <summary>The table at <paramref name="age"/> s, linear between keys; before contact and after <see cref="LifeSeconds"/> nothing shows (zero opacity and fill).</summary>
    public static Key Sample(float age)
    {
        if (!(age >= 0f) || age >= LifeSeconds) return Keys[0] with { T = age, Fill = 0f, Opacity = 0f, Mist = 0f };
        var last = Keys[^1];
        if (age >= last.T)
        {
            float k = 1f - (age - last.T) / (LifeSeconds - last.T);
            return last with { T = age, Fill = last.Fill * k, Opacity = last.Opacity * k, Mist = last.Mist * k };
        }
        int i = 0;
        while (i < Keys.Length - 2 && age >= Keys[i + 1].T) i++;
        var a = Keys[i]; var b = Keys[i + 1];
        float u = (age - a.T) / (b.T - a.T);
        return new Key(age, L(a.Exit, b.Exit, u), L(a.Entry, b.Entry, u), L(a.Fan, b.Fan, u), L(a.Width, b.Width, u), L(a.Fill, b.Fill, u), L(a.Opacity, b.Opacity, u),
            L(a.Grit, b.Grit, u), L(a.Branches, b.Branches, u), L(a.Outer, b.Outer, u), L(a.Root, b.Root, u), L(a.Mist, b.Mist, u), L(a.Red, b.Red, u));
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
    /// <summary>A killing hit's extra scale and the largest scale (ours; owner: "bigger on death"; lead 41 for the ribbons: at most 1.5 in all).</summary>
    public const float LethalScale = 1.4f, MaxScale = 1.5f;
    /// <summary>
    /// How much of a streak's scale goes into its length (ribbon rebuild, lead 41: a heavy weapon's spray grows in width and area, its length scale capped at <see cref="LengthCap"/>, so a
    /// lascannon's frame 0 stays within about 1.3 H and frames 2 to 8 within about 2 H; a kill's at <see cref="LethalLengthCap"/>, the same layout bigger); the width takes the whole scale.
    /// </summary>
    public const float LengthCap = 1.1f, LethalLengthCap = 1.4f;

    /// <summary>A streak's scale: its weight's, times <see cref="LethalScale"/> on a kill, at most <see cref="MaxScale"/>.</summary>
    public static float Scale(Weight w, bool lethal)
    {
        float s = w switch { Weight.Light => LightScale, Weight.Heavy => HeavyScale, _ => NormalScale };
        return MathF.Min(MaxScale, lethal ? s * LethalScale : s);
    }
    /// <summary>The share of <paramref name="scale"/> its length takes (<see cref="LengthCap"/>, a kill's <see cref="LethalLengthCap"/>).</summary>
    public static float LengthScale(float scale, bool lethal) => MathF.Min(scale, lethal ? LethalLengthCap : LengthCap);
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
    /// <param name="Heavy">A heavy weapon's hit: the fourth (lower) band, as a kill.</param>
    /// <param name="WidthShare">The width share for the source and the plane's facing (<see cref="WidthShareOf"/>).</param>
    /// <param name="UnitCap">The largest metres per table unit (<see cref="MaxUnitMetres"/>, Warfront <see cref="WarfrontUnitMetres"/>).</param>
    public readonly record struct Streak(Vector3 Origin, Vector3 Axis, Vector3 Normal, float BodyHeight, float Scale, int Seed, float PreAge, float Bend, bool Lethal, bool Heavy = false, float WidthShare = 1f, float EntryShare = 1f, float UnitCap = MaxUnitMetres)
    {
        /// <summary>The length's share of the scale (<see cref="LengthScale"/>).</summary>
        public float LenScale => LengthScale(Scale, Lethal);
        /// <summary>The metres per table unit (H x the length scale, at most <see cref="UnitCap"/>).</summary>
        public float Unit => MathF.Min(BodyHeight * LenScale, UnitCap);
        /// <summary>The bands' width multiplier in table units: the width share times the scale's part beyond the length's (a heavy hit wider, not longer).</summary>
        public float WidthMul => WidthShare * Scale / MathF.Max(LenScale, 1e-3f);
        /// <summary>Whether it draws the fourth (lower) band.</summary>
        public bool Fourth => Lethal || Heavy;
        /// <summary>The plane's second axis (across the exit axis, in the plane).</summary>
        public Vector3 Across => Vector3.Normalize(Vector3.Cross(Normal, Axis));
    }

    /// <summary>
    /// The width share of a streak whose plane faces the camera by <paramref name="facing"/> (|normal . toward camera|, 0..1): full width when oblique, down to <see cref="MinFacingWidth"/>
    /// face-on (ours: a face-on band shows its whole width, an oblique one less).
    /// </summary>
    public static float FacingWidth(float facing) => Math.Clamp(1.25f - 0.75f * Math.Clamp(facing, 0f, 1f), MinFacingWidth, 1f);
    /// <summary>A streak's width share for its <paramref name="source"/> and plane facing (ours, from the captures): a sweep's oblique swing plane keeps <see cref="SweepWidth"/>, the others the facing share.</summary>
    public static float WidthShareOf(Source source, float facing, bool heavy = false) => source switch
    {
        Source.Sweep => SweepWidth,
        Source.Explosion => FacingWidth(facing) * ExplosionWidth,
        Source.Gunfire when heavy => FacingWidth(facing) * HeavyGunfireWidth,
        _ => FacingWidth(facing),
    };
    public const float HeavyGunfireWidth = 0.85f, ExplosionWidth = 0.8f, SweepWidth = 1f, MinFacingWidth = 1f;
    /// <summary>The entry band's share for a source (ours).</summary>
    public static float EntryShareOf(Source source) => source switch { Source.Explosion => 0.8f, _ => 1f };
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
    public const float CameraPull = 2f, MinFacing = 0.75f;

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

    // ---- the ribbons (mirrored by the shader) ----

    /// <summary>
    /// The ribbons of one streak (ribbon rebuild, owner card 2026-11-26; repair round 3: a bundle): every streak is a few overlapping tilted strips on its plane, each its own mesh
    /// instance with its own length, angle, width, root and timing, leaving the wound as a near-parallel bundle that opens into a fan: the main band on the attack axis (the longest), a
    /// middle and a low-middle band beside it, an upper band (the fan's top), the entry band behind the wound (short), and on a heavy or killing hit a lower band. Never one filled slab.
    /// </summary>
    public enum Band { Main = 0, Upper = 1, Entry = 2, Lower = 3, Mid = 4, LowMid = 5 }
    /// <summary>The ribbons a streak can have (the lower one only on a heavy or killing hit).</summary>
    public const int MaxBranches = 6;

    /// <summary>
    /// One band's layout (ours, from the reference's look; kind "interpretation"): its angle from the exit axis (a share of the key's <see cref="Key.Fan"/>; for the entry band degrees
    /// past 180), its reach (a share of the key's exit reach, or of the entry reach), its half width (a share of the key's width), where it leaves the axis (a share of the exit reach),
    /// how far across the axis its root sits (H: the bundle's thickness at the wound), the seeded jitter of its angle (degrees) and reach (share), how much of the melee arc's bend it
    /// takes, and how early it grows (0 with the side bands; 1 already in frame 0).
    /// </summary>
    public readonly record struct BandShape(float Angle, float Reach, float Width, float Root, float Across, float AngleJitter, float ReachJitter, float Bend, float Early = 0f);

    /// <summary>The bands' layouts, in <see cref="Band"/> order.</summary>
    public static readonly BandShape[] Bands =
    [
        new(-0.30f, 1.00f, 1.00f, 0.00f,  0.000f, 1.0f, 0.05f, 0.30f, 1f),   // main
        new( 1.00f, 0.58f, 0.80f, 0.05f,  0.155f, 1.5f, 0.06f, 0.20f),       // upper
        new( 0.50f, 1.00f, 0.85f, 0.00f,  0.000f, 1.5f, 0.08f, 0.15f, 1f),   // entry
        new(-1.00f, 0.60f, 0.75f, 0.08f, -0.135f, 1.5f, 0.06f, 0.20f),       // lower (heavy and killing hits)
        new( 0.45f, 0.90f, 0.85f, 0.02f,  0.080f, 1.5f, 0.06f, 0.25f, 0.4f), // middle
        new(-0.70f, 0.60f, 0.80f, 0.03f, -0.085f, 1.5f, 0.06f, 0.25f),       // low-middle
    ];

    /// <summary>One band at one age: its angle (radians from the exit axis), reach and half width (table units), where it leaves the axis (along, across; table units), its opacity share and its bend.</summary>
    public readonly record struct BandAt(float Angle, float Length, float HalfWidth, float Root, float Alpha, float Bend, float Across = 0f);

    /// <summary>How far the side bands have grown at key <paramref name="k"/> (0 at frame 0: one swipe; 1 from frame 2).</summary>
    public static float Grow(Key k) => Math.Clamp(k.Branches - 1f, 0f, 1f);

    /// <summary>
    /// Band <paramref name="band"/> of a streak at key <paramref name="k"/> (the vertex stage's math): its angle with the seeded jitter, its length (the exit bands from the exit reach,
    /// the entry band from the entry reach times <paramref name="entryShare"/>), its half width times <paramref name="widthMul"/> (the plane's facing and the weapon's width), where it
    /// leaves the axis, its own timing (the upper band's outer half fades first from frame 3; the entry band goes after frame 8) and its share of the arc's bend.
    /// </summary>
    public static BandAt BandOf(Band band, Key k, int seed, float bend, float widthMul = 1f, float entryShare = 1f)
    {
        var b = Bands[(int)band];
        int i = (int)band;
        float ja = (ShaderHash(seed, 20 + i) - 0.5f) * 2f * b.AngleJitter, jr = 1f + (ShaderHash(seed, 30 + i) - 0.5f) * 2f * b.ReachJitter;
        float deg, len, w = k.Width * b.Width * widthMul, alpha = 1f;
        if (band == Band.Entry)
        {
            deg = 180f + b.Angle + ja; len = k.Entry * entryShare * jr;
            alpha = 1f - Smooth(EntryFadeFrom, EntryFadeTo, k.T);
        }
        else
        {
            float g = b.Early + (1f - b.Early) * Grow(k);
            deg = b.Angle * k.Fan + ja; len = k.Exit * b.Reach * jr * (0.35f + 0.65f * g); w *= MathF.Sqrt(g);
            if (band == Band.Upper) alpha = 1f - UpperFade * Smooth(Keys[2].T, Keys[4].T, k.T);
            if (g <= 0f) { len = 0f; alpha = 0f; }
        }
        return new BandAt(deg * MathF.PI / 180f, len, w, b.Root * k.Exit, alpha, bend * b.Bend, b.Across * MathF.Min(1f, 0.3f + Grow(k)));
    }
    /// <summary>The entry band fades out between these ages (s; the reference's entry lasts to frame 8 and is gone by frame 9).</summary>
    public const float EntryFadeFrom = 0.76f, EntryFadeTo = 0.86f;
    /// <summary>How much the upper band fades between frames 2 and 4 (its outer part begins fading at frame 3, ahead of the main band).</summary>
    public const float UpperFade = 0.25f;

    /// <summary>A band's width along its length (share <paramref name="u"/> of its reach): narrow where it leaves the wound, widest past the middle, then a tapered tip.</summary>
    public static float Profile(float u) => (0.7f + 0.3f * Smooth(0f, 0.5f, u)) * (1f - 0.6f * Smooth(0.82f, 1.0f, u));
    /// <summary>The strip is this much wider than the core's nominal half width: room for the mist (out to <see cref="MistTo"/>) and its lumps.</summary>
    public const float EdgePad = 3.5f;
    /// <summary>The strip's segments along its length (vertices = 2 x (segments + 1)).</summary>
    public const int Segments = 12;

    /// <summary>
    /// The plane point (table units: x along the exit axis, y across, up-leaning) of strip vertex (<paramref name="u"/> along 0..1, <paramref name="v"/> across -1..1 of the padded
    /// width) of a band (the vertex shader's math): from its root, along its angle, bent by the arc.
    /// </summary>
    public static (float X, float Y) RibbonPoint(BandAt b, float u, float v)
    {
        float cs = MathF.Cos(b.Angle), sn = MathF.Sin(b.Angle);
        float s = u * b.Length;
        float off = b.Bend * s * s / MathF.Max(b.Length, 0.05f);
        float hw = b.HalfWidth * Profile(u) * EdgePad;
        float across = off + v * hw;
        return (b.Root + cs * s - sn * across, b.Across + sn * s + cs * across);
    }

    // ---- the cell texture (made at startup; nothing comes from footage) ----

    /// <summary>The texture's size (texels a side).</summary>
    public const int FilamentSize = 256;
    /// <summary>The cells a tile side (R channel: small cells; B channel: the large ones).</summary>
    public const int SmallCells = 16, BigCells = 6;
    /// <summary>
    /// The texture's mapping onto the plane (repair round 3: cellular mottle, not filament stripes): one tile spans <see cref="CellAlongH"/> body heights along a band and
    /// <see cref="CellAcrossH"/> across, so a small cell is about 0.03 by 0.02 H (a little longer along the spray, never a long stripe); the large cells (the mist's clumps and the
    /// dither of its edge) span <see cref="BigTileH"/> a tile.
    /// </summary>
    public const float CellAlongH = 0.5f, CellAcrossH = 0.32f, BigTileH = 0.38f;
    /// <summary>The patch noise's tile (H, isotropic on the plane): the late bands break into blobs about a fifth of H across, not full-width dashes.</summary>
    public const float ErodeTileH = 2.2f;

    /// <summary>
    /// The cell texture (RGBA8, <paramref name="size"/>^2, tileable, deterministic for <paramref name="seed"/>): R small cells (bright soft centres, dark cracks between: the mottle),
    /// G soft blotches (the patch erosion and the lumpy outline), B large cells (the mist's clumps and the dither of its feathered edge); A 255.
    /// </summary>
    public static byte[] FilamentTexels(int size = FilamentSize, int seed = 1)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                var (f1, f2, id) = Worley(u, v, SmallCells, seed + 21);
                float small = (1f - Smooth(0.05f, 0.75f, f1)) * Smooth(0.0f, 0.14f, f2 - f1) * (0.55f + 0.45f * Hash01(id, 7));
                var (g1, g2, gid) = Worley(u, v, BigCells, seed + 22);
                float big = (1f - Smooth(0.0f, 0.95f, g1)) * (0.6f + 0.4f * Smooth(0f, 0.25f, g2 - g1)) * (0.5f + 0.5f * Hash01(gid, 9));
                float blot = 0.7f * Periodic(u, v, 12, 12, seed + 14) + 0.3f * Periodic(u, v, 24, 24, seed + 15);
                int i = (y * size + x) * 4;
                px[i] = ToByte(Smooth(0.0f, 0.85f, small)); px[i + 1] = ToByte(Smooth(0.22f, 0.78f, blot)); px[i + 2] = ToByte(Smooth(0.0f, 0.8f, big)); px[i + 3] = 255;
            }
        return px;
        static byte ToByte(float f) => (byte)Math.Clamp(MathF.Round(f * 255f), 0f, 255f);
    }

    /// <summary>Cellular noise that wraps at the tile's edges: the distances (in cells) to the nearest and second-nearest jittered cell point, and the nearest cell's id.</summary>
    static (float F1, float F2, int Id) Worley(float u, float v, int cells, int seed)
    {
        float fx = u * cells, fy = v * cells;
        int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
        float d1 = 9f, d2 = 9f; int id = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = ix + dx, cy = iy + dy;
                int wx = ((cx % cells) + cells) % cells, wy = ((cy % cells) + cells) % cells;
                int h = seed * 7919 + wx * 92821 + wy * 68917;
                float px = cx + 0.15f + 0.7f * Hash01(h, 1), py = cy + 0.15f + 0.7f * Hash01(h, 2);
                float d = MathF.Sqrt((px - fx) * (px - fx) + (py - fy) * (py - fy));
                if (d < d1) { d2 = d1; d1 = d; id = h; } else if (d < d2) d2 = d;
            }
        return (d1, d2, id);
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

    /// <summary>The texture at (u, v) (wrapping, bilinear), each channel 0..1: small cells, blotch, large cells.</summary>
    public static (float Fil, float Blot, float Fine) TexSample(byte[] px, int size, float u, float v)
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

    // ---- one texel of a band (the fragment's math) ----

    /// <summary>
    /// A band texel: its shape (the core over the soft mist, before the renderer's shares), the core's own opacity (the blood red mixed at core x the renderer's core share), its
    /// coverage after the erosion, its density (lighter in the cell centres), and the dither threshold its shape must clear to be drawn (<see cref="Drawn"/>).
    /// </summary>
    public readonly record struct Texel(float Alpha, float Core, float Cov, float Density, float Cut = CutLo, float Blood = 0f);

    /// <summary>
    /// The feathered edge (repair round 3: no hard cut, never a half-stripped pink fringe): a texel is drawn where its shape clears a threshold between <see cref="CutLo"/> and
    /// <see cref="CutHi"/> set by the large cells (the brighter a cell, the lower its threshold), so the cores are solid and the mist round them breaks into fewer and smaller red clumps
    /// toward its edge: a soft, mottled body at a glance, each drawn texel fully stripped. <see cref="DrawRamp"/> is the one-pixel antialiasing step.
    /// </summary>
    public const float CutLo = 0.06f, CutHi = 0.42f, DrawRamp = 0.05f;
    /// <summary>The core's edge: it fades out between these shares of its nominal half width (plus the lumps).</summary>
    public const float EdgeFrom = 0.55f, EdgeTo = 1.05f;
    /// <summary>The lumpy outline: each edge moves in and out by up to <see cref="LumpDepth"/> of the half width over about 1 / <see cref="LumpAlong"/> body heights.</summary>
    public const float LumpDepth = 0.5f, LumpAlong = 0.9f;
    /// <summary>The mottle: a core's opacity times floor + (1 - floor) x the cells (small cells, then the large ones).</summary>
    public const float MottleFloor = 0.35f;
    /// <summary>How much more solid the swipe is before the side bands grow (frame 0 reads as one solid thin line).</summary>
    public const float SwipeSolid = 0.4f;
    /// <summary>
    /// The mist body under each core (repair round 3: a soft body 2 to 3 times the core's width): <see cref="MistAlpha"/> of the band's opacity times the key's <see cref="Key.Mist"/>,
    /// fading from <see cref="MistFrom"/> to <see cref="MistTo"/> core half widths, clumped by the large cells, eroding later than the core (a haze that outlasts it).
    /// </summary>
    public const float MistAlpha = 0.48f, MistFrom = 0.7f, MistTo = 3.2f;
    /// <summary>The soft step of the patch erosion.</summary>
    public const float ErodeSoft = 0.06f;
    /// <summary>The root (frames 0 to 4): the first <see cref="RootShare"/> of every band is held whole by the key's root strength, so the bands leave the wound as one mass.</summary>
    public const float RootShare = 0.3f;
    /// <summary>The tip hold (frames 5 to 9): the far end of a band keeps a patch while the erosion breaks the rest (the reference's exit holds near 2 H until frame 9).</summary>
    public const float TipHold = 0.3f;
    /// <summary>The core's gain over the mottle (the cell centres saturate, the cracks stay lighter).</summary>
    public const float CoreGain = 1.25f;
    /// <summary>The blood (the darker red over the strip) follows the cracks between the small cells: its opacity x (floor + (1 - floor) x (1 - cell)), so the cores read as lighter cells with darker seams (the reference's cellular grit).</summary>
    public const float BloodCrack = 0.25f;
    /// <summary>The strip red's granules: x (floor + gain x small cell), so the cell centres read a lighter red (never past a deep pure red) and the seams darker; mean about 1.</summary>
    public const float GranuleFloor = 0.75f, GranuleGain = 0.55f;

    /// <summary>
    /// The texel at (<paramref name="u"/> along 0..1, <paramref name="v"/> across: 1 at the core's nominal edge) of band <paramref name="band"/> at key <paramref name="k"/> (the fragment
    /// shader's math): the cellular mottle, the lumpy core edge and tapered tip, the outer fade, the patch erosion against 1 - Fill (isotropic blobs on the plane), the held root and tip,
    /// the cells opening up as it fades; the mist body under it.
    /// </summary>
    public static Texel TexelAt(Key k, BandAt b, Band band, float u, float v, int seed, byte[] tex, int size)
    {
        float s = u * b.Length;
        float av = MathF.Abs(v);
        float hwv = v * b.HalfWidth;   // across in table units
        float ou = ShaderHash(seed, 50 + (int)band), ov = ShaderHash(seed, 54 + (int)band);
        float small = TexSample(tex, size, s / CellAlongH + ou, hwv / CellAcrossH + ov).Fil;
        float big = TexSample(tex, size, s / BigTileH + ou * 1.7f, hwv / BigTileH + ov * 1.3f).Fine;
        float mottle = 0.6f * small + 0.4f * big;
        float lump = TexSample(tex, size, s * LumpAlong + ou * 2f, (v > 0f ? 0.3f : 0.7f) + ov).Blot;
        float edge = 1f - Smooth(EdgeFrom, EdgeTo, av + LumpDepth * (lump - 0.5f) + 0.25f * (0.5f - small) * av);
        float along = Smooth(0f, 0.05f, u) * (1f - Smooth(0.78f, 1f, u + 0.12f * (big - 0.5f)));
        float outer = 1f - k.Outer * Smooth(0.5f, 1f, u);
        float e = TexSample(tex, size, s / ErodeTileH + ou * 3.1f, hwv / ErodeTileH + ov * 2.3f).Blot;
        float thr = 1f - k.Fill;
        float er = 0.7f * e + 0.3f * mottle;
        float cov = Smooth(thr - ErodeSoft, thr + ErodeSoft, er);
        float rootHeld = k.Root * (1f - Smooth(0.6f * RootShare, RootShare, u));
        float tipHeld = TipHold * (1f - k.Root) * Smooth(0.7f, 0.85f, u) * Smooth(0.3f, 0.6f, big) * Smooth(0f, 0.1f, k.Fill);
        cov = MathF.Max(cov, MathF.Max(rootHeld, tipHeld));
        float open = Smooth(0.2f, 0.0f, small - 0.35f * k.Grit) * k.Grit * (1f - rootHeld);   // the cracks between the cells open as it fades
        cov *= 1f - 0.5f * open;
        float solid = SwipeSolid * (1f - Grow(k));
        float mf = MottleFloor + solid;
        float band0 = k.Opacity * b.Alpha * along * outer;
        float core = MathF.Min(1f, band0 * edge * cov * (mf + (1f - mf) * mottle) * CoreGain);
        float mistCov = Smooth(thr - 0.12f, thr + 0.05f, 0.6f * e + 0.4f * big);
        float mist = band0 * k.Mist * MistAlpha * (1f - Smooth(MistFrom, MistTo, av + 0.6f * (lump - 0.5f))) * mistCov * (0.35f + 0.65f * big);
        float cut = CutLo + (CutHi - CutLo) * (1f - big);
        float blood = MathF.Min(1f, band0 * edge * cov * (BloodCrack + (1f - BloodCrack) * (1f - small)));   // darker cracks, lighter cell centres
        return new Texel(MathF.Max(core, mist), core, cov, mottle * (1f - 0.4f * MathF.Min(av, 1f)), cut, blood);
    }

    /// <summary>How fully a texel of shape <paramref name="shape"/> is drawn against its threshold <paramref name="cut"/> (0 not drawn; 1 drawn and fully stripped; a one-pixel step between).</summary>
    public static float Drawn(float shape, float cut) => Smooth(cut - DrawRamp, cut + DrawRamp, shape);

    /// <summary>The bands a streak draws: all but the lower one, which only a heavy or killing hit (<paramref name="fourth"/>) draws.</summary>
    public static int BandCount(bool fourth) => fourth ? MaxBranches : MaxBranches - 1;
    /// <summary>Whether a streak draws band <paramref name="band"/>.</summary>
    public static bool BandOn(Band band, bool fourth) => band != Band.Lower || fourth;

    /// <summary>
    /// The streak's covered area (table units squared, face-on: the drawn texels), its farthest covered reach ahead of the wound and behind it, the main band's patches (runs of drawn
    /// core texels along its middle), the mean blood opacity (core x <paramref name="coreAlpha"/>) of the drawn texels, and the half-fan (half the spread between the area-weighted 5th
    /// and 95th percentile of the drawn texels' angles ahead of 0.15 table units, degrees: the scoring tools' measure, face-on), at <paramref name="age"/>.
    /// </summary>
    public static (float Area, float ExitReach, float EntryReach, int MainPatches, float MeanAlpha, float HalfFan, float Fill) Measure(float age, int seed, float bend, bool fourth, byte[] tex, int size,
        float widthMul = 1f, float coreAlpha = CoreAlpha, int nu = 160, int nv = 64)
    {
        var k = Sample(age);
        float area = 0f, fwd = 0f, back = 0f, sumA = 0f; int nA = 0, patches = 0;
        var angles = new System.Collections.Generic.List<(float A, float W)>();
        var pts = new System.Collections.Generic.List<(float X, float Y)>();
        if (k.Opacity <= 0f) return (0f, 0f, 0f, 0, 0f, 0f, 0f);
        for (int bi = 0; bi < MaxBranches; bi++)
        {
            var band = (Band)bi;
            if (!BandOn(band, fourth)) continue;
            var b = BandOf(band, k, seed, bend, widthMul);
            if (b.Length <= 1e-4f || b.Alpha <= 0f) continue;
            var mainLine = new bool[nu];
            for (int iu = 0; iu < nu; iu++)
            {
                float u = (iu + 0.5f) / nu;
                float hw = b.HalfWidth * Profile(u) * EdgePad;
                float dA = b.Length / nu * (2f * hw / nv);
                for (int iv = 0; iv < nv; iv++)
                {
                    float vv = -1f + (iv + 0.5f) * 2f / nv;            // across the padded strip
                    float v = vv * EdgePad;                             // 1 at the core's nominal edge
                    var t = TexelAt(k, b, band, u, v, seed, tex, size);
                    if (iv == nv / 2 && band == Band.Main) mainLine[iu] = Drawn(t.Core, t.Cut) >= 0.5f;
                    if (Drawn(t.Alpha, t.Cut) < 0.5f) continue;
                    area += dA; sumA += MathF.Min(1f, t.Blood * coreAlpha); nA++;
                    var (x, y) = RibbonPoint(b, u, vv);
                    if (x > fwd) fwd = x;
                    if (-x > back) back = -x;
                    if (x > 0.15f) angles.Add((MathF.Atan2(y, x) * 180f / MathF.PI, dA));
                    if ((iu + iv) % 3 == 0) pts.Add((x, y));
                }
            }
            if (band == Band.Main) patches = Patches(mainLine);
        }
        float hull = HullArea(pts);
        return (area, fwd, back, patches, nA == 0 ? 0f : sumA / nA, HalfSpread(angles), hull > 1e-6f ? MathF.Min(1f, area / hull) : 0f);
    }

    /// <summary>The area of the convex hull of <paramref name="pts"/> (monotone chain): the scoring tools' fill is the drawn area over it.</summary>
    static float HullArea(System.Collections.Generic.List<(float X, float Y)> pts)
    {
        if (pts.Count < 3) return 0f;
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var h = new (float X, float Y)[2 * pts.Count];
        int n = 0;
        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        for (int i = 0; i < pts.Count; i++) { while (n >= 2 && Cross(h[n - 2], h[n - 1], pts[i]) <= 0f) n--; h[n++] = pts[i]; }
        for (int i = pts.Count - 2, t = n + 1; i >= 0; i--) { while (n >= t && Cross(h[n - 2], h[n - 1], pts[i]) <= 0f) n--; h[n++] = pts[i]; }
        float s2 = 0f;
        for (int i = 0; i < n - 1; i++) s2 += h[i].X * h[i + 1].Y - h[i + 1].X * h[i].Y;
        return MathF.Abs(s2) / 2f;
    }

    /// <summary>Half the spread between the area-weighted 5th and 95th percentiles of <paramref name="angles"/>.</summary>
    static float HalfSpread(System.Collections.Generic.List<(float A, float W)> angles)
    {
        if (angles.Count < 15) return 0f;
        angles.Sort((a, b) => a.A.CompareTo(b.A));
        float total = 0f; foreach (var a in angles) total += a.W;
        float P(float q) { float acc = 0f, want = q * total; foreach (var a in angles) { acc += a.W; if (acc >= want) return a.A; } return angles[^1].A; }
        return (P(0.95f) - P(0.05f)) / 2f;
    }

    /// <summary>The patches along a band's middle line: runs of covered samples at least 3 long, gaps under 3 samples bridged.</summary>
    public static int Patches(bool[] line)
    {
        int n = 0, run = 0, gap = 99;
        bool counted = false;
        foreach (bool on in line)
        {
            if (on)
            {
                if (gap >= 3) { run = 0; counted = false; }
                gap = 0; run++;
                if (run >= 3 && !counted) { n++; counted = true; }
            }
            else gap++;
        }
        return n;
    }

    // ---- colour ----

    /// <summary>
    /// The streak's own blood albedo (repair round 3): our darker red with almost no green or blue (repair 4's <see cref="ClassicBloodRules.BloodRgb"/>, 0.12 green, read about a third
    /// of the red on screen through the tonemapper).
    /// </summary>
    public static readonly (float R, float G, float B) StreakBloodRgb = (1f, 0.02f, 0.015f);
    /// <summary>
    /// The strip pass (repair round 3; the capture aid's colour probe on white, grey and black grounds): both renderers blend in linear light before the scene's tonemapper (AgX), so
    /// the old Forward+ multiply, which kept the ground's red, left a bright red over a light ground that the tonemapper's shoulder bleached toward (255, 117, 98) pink, and
    /// Compatibility's display-space model was wrong (its strip red was taken as linear and its leftover green crushed by the tonemapper's toe). Now one strip on both renderers: a
    /// drawn texel's ground is replaced (<see cref="StripMax"/>) by <see cref="StripRed"/> (scene linear, times the key's <see cref="Key.Red"/>) with no green or blue, a mid-dark red
    /// whatever lies under it (about (140, 0, 0) on screen), so nothing over white, light armour or a flash can read pink; the softness comes from the dithered mist, not from
    /// translucency.
    /// </summary>
    public const float StripMax = 0.995f, StripRed = 0.17f;
    /// <summary>The blood shade (scene linear: the blood albedo x 0.3 to 0.55 by density x this), the same on both renderers: the cores a little darker than the strip red.</summary>
    public const float Shade = 0.26f;
    /// <summary>The core's blood opacity share (the strip makes the red; the blood red deepens the cores by the cells).</summary>
    public const float CoreAlpha = 0.45f;
    /// <summary>The scene's exposure on its way to the screen, as the colour model takes it (the maps' tonemap exposure 1.15 to 1.25; the model leaves out the tonemapper's toe, so its green is an upper bound).</summary>
    public const float DisplayGain = 1.2f;

    /// <summary>The blood's albedo (scene linear) for <paramref name="density"/> (repair 4's darker red, 0.3 to 0.55 of the blood red: the cell centres lighter) at <paramref name="shade"/>.</summary>
    public static (float R, float G, float B) Colour(float density, float shade = Shade)
    {
        float k = shade * (ClassicBloodRules.RibbonShadeFloor + (ClassicBloodRules.RibbonShadeTop - ClassicBloodRules.RibbonShadeFloor) * Smooth(0.15f, 0.85f, density));
        var b = StreakBloodRgb;
        return (b.R * k, b.G * k, b.B * k);
    }

    /// <summary>
    /// The colour on screen (display values 0..1) of a pixel showing <paramref name="under"/> (display) beneath a band texel drawn by <paramref name="drawn"/> (<see cref="Drawn"/>) with
    /// core opacity <paramref name="core"/>, density and the key's <paramref name="red"/>, after both passes, on either renderer (both blend in linear before the tonemapper): the strip
    /// mixes the ground toward the strip red, the blood is mixed over it at core x <see cref="CoreAlpha"/>, then the exposure and the display curve (<see cref="DisplayGain"/>, no
    /// toe: an upper bound for green and blue). The capture aid's probe (<c>--gore-streak-probe</c>) checks it against drawn pixels on white, grey and black grounds.
    /// </summary>
    public static (float R, float G, float B) Composite((float R, float G, float B) under, float drawn, float core, float density, float red = 1f)
    {
        if (drawn <= 0f) return under;
        float a = Math.Clamp(core * CoreAlpha, 0f, 1f) * drawn;
        float s = StripMax * drawn;
        float lr = ToLinear(under.R) / DisplayGain, lg = ToLinear(under.G) / DisplayGain, lb = ToLinear(under.B) / DisplayGain;   // the ground in scene light
        var l = (R: lr * (1f - s) + s * StripRed * red, G: lg * (1f - s), B: lb * (1f - s));
        var c = Colour(density);
        return (ToSrgb(DisplayGain * (a * c.R + (1f - a) * l.R)), ToSrgb(DisplayGain * (a * c.G + (1f - a) * l.G)), ToSrgb(DisplayGain * (a * c.B + (1f - a) * l.B)));
    }

    /// <summary>Linear to display (sRGB) for one channel (HDR values above 1 clip to 1).</summary>
    public static float ToSrgb(float l) { l = Math.Clamp(l, 0f, 1f); return l <= 0.0031308f ? 12.92f * l : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f; }
    /// <summary>Display (sRGB) to linear for one channel.</summary>
    public static float ToLinear(float s) { s = Math.Clamp(s, 0f, 1f); return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f); }

    // ---- pool and budget ----

    /// <summary>The streak pool (ours): <see cref="MaxBranches"/> mesh instances a streak; the oldest streak is reused beyond it.</summary>
    public const int PoolSize = 256;
    /// <summary>The slot of the <paramref name="spawned"/>-th streak (a ring: spawn order is age order, so the slot reused is the oldest).</summary>
    public static int PoolSlot(long spawned, int pool = PoolSize) => (int)(spawned % pool);
    /// <summary>Whether a streak started at <paramref name="start"/> (s on the view's clock, pre-age applied) still shows at <paramref name="now"/>.</summary>
    public static bool Live(double start, double now) => now - start < LifeSeconds && now - start > -2.0;
    /// <summary>Whether a slot started at <paramref name="start"/> is spent at <paramref name="now"/> and is retired (its instances switched off: no replay when the shader's clock wraps).</summary>
    public static bool Spent(double start, double now) => !double.IsNegativeInfinity(start) && now - start >= LifeSeconds;

    /// <summary>
    /// One spray per burst on a victim (repair round 3, reviewer: sustained fire must not restart the streak on every hit, and a hit just after a kill must not replace the kill's): a hit
    /// on a victim whose live streak was last hit less than <see cref="MergeSeconds"/> ago joins that streak instead of starting one (<see cref="Join.Merge"/>; an aged streak is held
    /// back to <see cref="HoldAge"/>, so a burst keeps one spray at its re-expanding peak and it fades once the fire stops), unless it kills a victim whose streak did not
    /// (<see cref="Join.Upgrade"/>: the kill's bigger streak takes the slot and starts over); after a pause, or with none showing, it starts its own (<see cref="Join.New"/>).
    /// </summary>
    public const double MergeSeconds = 0.25;
    /// <summary>The age (s) a merged hit holds its victim's streak back to (frame 4: the spray grows again while fading).</summary>
    public const float HoldAge = 0.27f;
    /// <summary>What a new hit does on its victim (<see cref="JoinOf"/>).</summary>
    public enum Join { New, Merge, Upgrade }
    /// <summary>
    /// What a hit at <paramref name="now"/> (killing: <paramref name="lethal"/>) does on a victim whose streak started at <paramref name="start"/>, was last hit at
    /// <paramref name="lastHit"/> and killed (<paramref name="lastLethal"/>) or not.
    /// </summary>
    public static Join JoinOf(double start, double lastHit, bool lastLethal, double now, bool lethal)
    {
        if (!Live(start, now) || !(now - lastHit < MergeSeconds)) return Join.New;
        return lethal && !lastLethal ? Join.Upgrade : Join.Merge;
    }
    /// <summary>A merged streak's start: held so its age at <paramref name="now"/> is at most <see cref="HoldAge"/> (a streak still waiting for its tracer keeps its start).</summary>
    public static double HoldStart(double start, double now) => now - start > HoldAge ? now - HoldAge : start;

    /// <summary>The largest metres per table unit in Classic (a lethal streak on a 2 m marine stays within it) and in Warfront (lead 41: about 2.2 m, so a big Ork's streak stays in proportion).</summary>
    public const float MaxUnitMetres = 3f, WarfrontUnitMetres = 2.2f;

    /// <summary>
    /// Warfront's budget (lead decisions, gate repair 9 and the ribbon rebuild; ours): a per-second token bucket for other players' hits (<see cref="WarfrontPerSecond"/>, burst
    /// <see cref="WarfrontBurst"/>; the token taken last, so a hit dropped for another reason costs none), at most one streak per enemy every <see cref="WarfrontEnemySeconds"/> (a killing hit
    /// always: it replaces that enemy's streak), the own hits first and nearest first but at most <see cref="WarfrontOwnPerWindow"/> every <see cref="WarfrontOwnWindow"/> and
    /// <see cref="WarfrontOwnLiveMax"/> showing, at most <see cref="WarfrontLiveMax"/> showing in all, within <see cref="WarfrontMetres"/>, none for a body under <see cref="MinBodyPixels"/>
    /// on screen.
    /// </summary>
    public const float WarfrontPerSecond = 40f, WarfrontBurst = 16f, WarfrontEnemySeconds = 0.15f;
    public const int WarfrontLiveMax = 64, WarfrontOwnLiveMax = 48, WarfrontOwnPerWindow = 7;
    public const double WarfrontOwnWindow = 0.15;
    public const float WarfrontMetres = 45f, MinBodyPixels = 12f;
    /// <summary>A streak closer to the camera than this fades out (m; ours): Classic, and Warfront (lead 41: its near hits filled the screen).</summary>
    public const float NearFadeFrom = 1.2f, NearFadeTo = 3.5f, WarfrontNearFadeFrom = 2.5f, WarfrontNearFadeTo = 6f;

    /// <summary>A token bucket (engine-free): <see cref="Take"/> spends one token when there is one; tokens refill at the rate up to the burst.</summary>
    public sealed class Bucket(float perSecond, float burst)
    {
        double _tokens = burst, _at = double.NaN;
        /// <summary>Whether a token is there at <paramref name="now"/> (refilled; not spent).</summary>
        public bool Has(double now) { Refill(now); return _tokens >= 1.0; }
        public bool Take(double now)
        {
            Refill(now);
            if (_tokens < 1.0) return false;
            _tokens -= 1.0;
            return true;
        }
        void Refill(double now)
        {
            if (double.IsNaN(_at)) _at = now;
            _tokens = Math.Min(burst, _tokens + Math.Max(0.0, now - _at) * perSecond);
            _at = now;
        }
    }

    /// <summary>Whether an enemy last streaked at <paramref name="last"/> may streak again at <paramref name="now"/>.</summary>
    public static bool EnemyReady(double last, double now) => now - last >= WarfrontEnemySeconds;

    /// <summary>One Warfront streak waiting for the frame's admission (<see cref="AdmitStreaks"/>): the own player's hit or another's, killing or not, its distance from the camera.</summary>
    public readonly record struct StreakAsk(bool Own, bool Lethal, float Distance, int Index);

    /// <summary>
    /// The frame's Warfront admission (finding M2, engine-free): the asks sorted own first, killing first, then nearest; a killing hit is always admitted (repair round 3: exempt from
    /// the own window, the own and total live caps and the bucket, though it counts as showing); the other own ones at most <see cref="WarfrontOwnPerWindow"/> in the current window
    /// (<paramref name="ownInWindow"/> already spent) and while fewer than <see cref="WarfrontOwnLiveMax"/> own and <see cref="WarfrontLiveMax"/> in all show (<paramref name="liveOwn"/>,
    /// <paramref name="liveAll"/>, counted up as admitted); the others under the live cap and with a token from <paramref name="bucket"/>, taken last. The admitted asks' indices go
    /// into <paramref name="admitted"/> (cleared first; no allocation a frame) in admission order.
    /// </summary>
    public static void AdmitStreaks(System.Collections.Generic.List<StreakAsk> asks, int ownInWindow, int liveOwn, int liveAll, Bucket bucket, double now, System.Collections.Generic.List<int> admitted)
    {
        asks.Sort(AskOrder);
        admitted.Clear();
        foreach (var a in asks)
        {
            if (!a.Lethal)
            {
                if (liveAll >= WarfrontLiveMax) continue;
                if (a.Own)
                {
                    if (ownInWindow >= WarfrontOwnPerWindow || liveOwn >= WarfrontOwnLiveMax) continue;
                }
                else if (!bucket.Take(now)) continue;
            }
            if (a.Own) { ownInWindow++; liveOwn++; }
            liveAll++;
            admitted.Add(a.Index);
        }
    }
    /// <summary><see cref="AdmitStreaks(System.Collections.Generic.List{StreakAsk}, int, int, int, Bucket, double, System.Collections.Generic.List{int})"/> into a new list (tests).</summary>
    public static System.Collections.Generic.List<int> AdmitStreaks(System.Collections.Generic.List<StreakAsk> asks, int ownInWindow, int liveOwn, int liveAll, Bucket bucket, double now)
    {
        var admitted = new System.Collections.Generic.List<int>();
        AdmitStreaks(asks, ownInWindow, liveOwn, liveAll, bucket, now, admitted);
        return admitted;
    }
    static readonly Comparison<StreakAsk> AskOrder = (a, b) => a.Own != b.Own ? (a.Own ? -1 : 1) : a.Lethal != b.Lethal ? (a.Lethal ? -1 : 1) : a.Distance.CompareTo(b.Distance);

    /// <summary>A body <paramref name="height"/> m tall at <paramref name="distance"/> m, seen with a vertical field of view <paramref name="fovYDegrees"/> on a <paramref name="screenHeight"/>-pixel view: its height in pixels.</summary>
    public static float ProjectedPixels(float height, float distance, float fovYDegrees, float screenHeight) =>
        distance <= 0.01f ? float.PositiveInfinity : height / (2f * distance * MathF.Tan(fovYDegrees * MathF.PI / 360f)) * screenHeight;

    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }
}
