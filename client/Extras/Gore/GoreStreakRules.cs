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
/// <item><see cref="FilamentTexels"/>: the flow-warped filament texture the bands are drawn with (made in code at startup; nothing is copied from any footage).</item>
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
    /// <param name="Fan">The upper band's angle from the main band (degrees; the main band leans the other way by 0.12 of it, the lower band by 0.7).</param>
    /// <param name="Width">Half width of the main band at its widest (H).</param>
    /// <param name="Fill">Share of a band the patch erosion keeps (1 whole; the erosion threshold is 1 - Fill).</param>
    /// <param name="Opacity">Opacity of a band.</param>
    /// <param name="Grit">Pinhole amount (0 none, 1 most): it grows as the spray fades.</param>
    /// <param name="Branches">1 one swipe; 2 the upper (and on a heavy or killing hit the lower) band fully grown.</param>
    /// <param name="Outer">How much the outer part of every band has faded (0 none).</param>
    /// <param name="Root">How whole the first part of every band is held (1 frames 0-4: one mass leaving the wound; 0 once it breaks up).</param>
    public readonly record struct Key(float T, float Exit, float Entry, float Fan, float Width, float Fill, float Opacity, float Grit, float Branches, float Outer, float Root);

    /// <summary>
    /// The per-frame table (interpretation of reference frames 0..10, provisional; design state/design-gore-streak-1125.md and the ribbon rebuild), set so the drawn bands' measured exit and
    /// entry reach and area per body height follow the reference's measured curves from the scoring tools (exit about 1.25 H at frame 0 rising to about 2 H, area about 0.07 H^2 at
    /// frame 0, 0.25 to 0.37 H^2 in frames 1 to 5 peaking at 2, dipping at 3, regrowing at 4, then falling to about 0.006 H^2 at frame 10). The blood hangs in place (the reach creeps a
    /// little, it never travels); the entry band fades out after frame 8. Tunable: the shader is generated from this table.
    /// </summary>
    public static readonly Key[] Keys =
    [
        //   T      Exit   Entry  Fan   Width   Fill   Opac   Grit   Br    Outer  Root
        new(0.00f, 1.50f, 0.45f,  6f, 0.030f, 1.00f, 0.95f, 0.00f, 1.0f, 0.00f, 1.00f),
        new(0.05f, 1.75f, 0.65f, 12f, 0.063f, 1.00f, 0.90f, 0.05f, 1.7f, 0.00f, 1.00f),
        new(0.10f, 2.02f, 0.72f, 17f, 0.076f, 1.00f, 0.85f, 0.10f, 2.0f, 0.00f, 1.00f),
        new(0.18f, 2.04f, 0.78f, 17f, 0.078f, 0.95f, 0.75f, 0.15f, 2.0f, 0.75f, 0.90f),
        new(0.27f, 2.22f, 0.86f, 20f, 0.090f, 0.92f, 0.66f, 0.20f, 2.0f, 0.30f, 0.70f),
        new(0.37f, 2.32f, 0.92f, 20f, 0.092f, 0.80f, 0.58f, 0.28f, 2.0f, 0.20f, 0.40f),
        new(0.48f, 2.45f, 0.92f, 19f, 0.092f, 0.62f, 0.50f, 0.36f, 2.0f, 0.10f, 0.00f),
        new(0.60f, 2.52f, 0.88f, 19f, 0.092f, 0.45f, 0.42f, 0.44f, 2.0f, 0.10f, 0.00f),
        new(0.72f, 2.60f, 0.84f, 19f, 0.092f, 0.35f, 0.36f, 0.50f, 2.0f, 0.10f, 0.00f),
        new(0.85f, 2.62f, 0.20f, 19f, 0.090f, 0.22f, 0.30f, 0.56f, 2.0f, 0.12f, 0.00f),
        new(0.98f, 2.62f, 0.10f, 19f, 0.085f, 0.05f, 0.18f, 0.60f, 2.0f, 0.15f, 0.00f),
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
    /// <summary>A killing hit's extra scale and the largest scale (ours; owner: "bigger on death"; lead 41 for the ribbons: at most 1.5 in all).</summary>
    public const float LethalScale = 1.4f, MaxScale = 1.5f;
    /// <summary>
    /// How much of a streak's scale goes into its length (ribbon rebuild, lead 41: a heavy weapon's spray grows in width and area, its length scale capped at <see cref="LengthCap"/>, so a
    /// lascannon's frame 0 stays within about 1.3 H and frames 2 to 8 within about 2 H; a kill's at <see cref="LethalLengthCap"/>, the same layout bigger); the width takes the whole scale.
    /// </summary>
    public const float LengthCap = 1.1f, LethalLengthCap = 1.3f;

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
    /// The ribbons of one streak (ribbon rebuild, owner card 2026-11-26 "Rebuild as ribbons"): every streak is a few thin tilted strips on its plane, each its own mesh instance with
    /// its own length, angle, width and timing: the main band on the attack axis (the longest), an upper branch rising from just past the wound, the entry band behind the wound (short),
    /// and on a heavy or killing hit a fourth, lower branch. Never one filled slab.
    /// </summary>
    public enum Band { Main = 0, Upper = 1, Entry = 2, Lower = 3 }
    /// <summary>The ribbons a streak can have (the fourth only on a heavy or killing hit).</summary>
    public const int MaxBranches = 4;

    /// <summary>
    /// One band's layout (ours, from the reference's look; kind "interpretation"): its angle from the exit axis (degrees; for the upper and lower bands a share of the key's
    /// <see cref="Key.Fan"/>, for the entry band 180 plus this), its reach (a share of the key's exit reach, or of the entry reach), its half width (a share of the key's width), where it
    /// leaves the main band (a share of the exit reach), the seeded jitter of its angle (degrees) and reach (share), and how much of the melee arc's bend it takes.
    /// </summary>
    public readonly record struct BandShape(float Angle, float Reach, float Width, float Root, float AngleJitter, float ReachJitter, float Bend);

    /// <summary>The four bands' layouts: main, upper (+Fan, about 0.6 of the main), entry (behind the wound, a touch below the line), lower (killing and heavy hits only).</summary>
    public static readonly BandShape[] Bands =
    [
        new(-0.12f, 1.00f, 1.00f, 0.00f, 2f, 0.06f, 0.3f),
        new( 1.00f, 0.60f, 0.80f, 0.10f, 2f, 0.06f, 0.2f),
        new( 2.00f, 1.00f, 0.78f, 0.00f, 4f, 0.08f, 0.15f),
        new(-0.70f, 0.50f, 0.66f, 0.06f, 2f, 0.06f, 0.2f),
    ];

    /// <summary>One band at one age: its angle (radians from the exit axis), reach and half width (table units), where it leaves the main band (table units along the exit axis), its opacity share and its bend.</summary>
    public readonly record struct BandAt(float Angle, float Length, float HalfWidth, float Root, float Alpha, float Bend);

    /// <summary>How far the upper and lower bands have grown at key <paramref name="k"/> (0 at frame 0: one swipe; 1 from frame 2).</summary>
    public static float Grow(Key k) => Math.Clamp(k.Branches - 1f, 0f, 1f);

    /// <summary>
    /// Band <paramref name="band"/> of a streak at key <paramref name="k"/> (the vertex stage's math): its angle with the seeded jitter, its length (the main and side bands from the exit
    /// reach, the entry band from the entry reach times <paramref name="entryShare"/>), its half width times <paramref name="widthMul"/> (the plane's facing and the weapon's width), where
    /// it leaves the main band, its own timing (the upper band's outer half fades first from frame 3; the entry band goes after frame 8) and its share of the arc's bend.
    /// </summary>
    public static BandAt BandOf(Band band, Key k, int seed, float bend, float widthMul = 1f, float entryShare = 1f)
    {
        var b = Bands[(int)band];
        int i = (int)band;
        float ja = (ShaderHash(seed, 20 + i) - 0.5f) * 2f * b.AngleJitter, jr = 1f + (ShaderHash(seed, 30 + i) - 0.5f) * 2f * b.ReachJitter;
        float deg, len, w = k.Width * b.Width * widthMul, alpha = 1f;
        switch (band)
        {
            case Band.Entry:
                deg = 180f + b.Angle + ja; len = k.Entry * entryShare * jr;
                alpha = 1f - Smooth(EntryFadeFrom, EntryFadeTo, k.T);
                break;
            case Band.Main:
                deg = b.Angle * k.Fan + ja; len = k.Exit * jr;
                break;
            default:
                float g = Grow(k);
                deg = b.Angle * k.Fan + ja; len = k.Exit * b.Reach * jr * (0.35f + 0.65f * g); w *= MathF.Sqrt(g);
                if (band == Band.Upper) alpha = 1f - UpperFade * Smooth(Keys[2].T, Keys[4].T, k.T);
                if (g <= 0f) { len = 0f; alpha = 0f; }
                break;
        }
        return new BandAt(deg * MathF.PI / 180f, len, w, b.Root * k.Exit, alpha, bend * b.Bend);
    }
    /// <summary>The entry band fades out between these ages (s; the reference's entry is gone by frame 9).</summary>
    public const float EntryFadeFrom = 0.74f, EntryFadeTo = 0.88f;
    /// <summary>How much the upper band fades between frames 2 and 4 (its outer part begins fading at frame 3, ahead of the main band).</summary>
    public const float UpperFade = 0.25f;

    /// <summary>A band's width along its length (share <paramref name="u"/> of its reach): narrow where it leaves the wound, widest past the middle, then a tapered tip.</summary>
    public static float Profile(float u) => (0.55f + 0.45f * Smooth(0f, 0.5f, u)) * (1f - 0.75f * Smooth(0.82f, 1.0f, u));
    /// <summary>The strip is this much wider than the band's nominal half width, so the ragged edge has room (the fragment fades the band out at |v| = 1 of the nominal width).</summary>
    public const float EdgePad = 1.3f;
    /// <summary>The strip's segments along its length (vertices = 2 x (segments + 1)).</summary>
    public const int Segments = 12;

    /// <summary>
    /// The plane point (table units: x along the exit axis, y across, up-leaning) of strip vertex (<paramref name="u"/> along 0..1, <paramref name="v"/> across -1..1 of the padded
    /// width) of a band (the vertex shader's math): from where it leaves the main band, along its angle, bent by the arc.
    /// </summary>
    public static (float X, float Y) RibbonPoint(BandAt b, float u, float v)
    {
        float cs = MathF.Cos(b.Angle), sn = MathF.Sin(b.Angle);
        float s = u * b.Length;
        float off = b.Bend * s * s / MathF.Max(b.Length, 0.05f);
        float hw = b.HalfWidth * Profile(u) * EdgePad;
        float across = off + v * hw;
        return (b.Root + cs * s - sn * across, sn * s + cs * across);
    }

    // ---- the filament texture (made at startup; nothing comes from footage) ----

    /// <summary>The filament texture's size (texels a side).</summary>
    public const int FilamentSize = 256;
    /// <summary>
    /// The texture's stretch along a band (ours: the reviewer's 10 to 14 to 1): one tile across spans <see cref="FilAcrossTiles"/> of a band's width each way from its middle, and one
    /// tile along spans <see cref="FilAlongH"/> body heights, so an isotropic filament about a twelfth of a tile reads about twelve times longer than wide on a band.
    /// </summary>
    public const float FilAcrossTiles = 0.25f, FilAlongH = 4.5f;
    /// <summary>The patch noise's tiles along a band (per its whole reach) and across (per its half width): with 12 cells a tile, about 7 cells along a band, so it breaks into 5 to 8 patches.</summary>
    public const float ErodeAlong = 1.0f, ErodeAcross = 0.06f;
    /// <summary>The fine grain's tiles per filament tile (pinholes and the ragged edge).</summary>
    public const float FineScale = 3.1f;

    /// <summary>
    /// The filament texture (RGBA8, <paramref name="size"/>^2, tileable, deterministic for <paramref name="seed"/>): R thin filaments (ridges of domain-warped value noise, flow-warped mostly
    /// across so they wander like a spray's streaks), G soft blotches (the erosion that breaks a band into patches, and the flow warp), B fine grain (pinholes, the ragged edge); A 255.
    /// </summary>
    public static byte[] FilamentTexels(int size = FilamentSize, int seed = 1)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                float wu = u + 0.05f * (Periodic(u, v, 4, 4, seed + 10) - 0.5f);
                float wv = v + 0.12f * (Periodic(u, v, 3, 5, seed + 11) - 0.5f);
                float r1 = Ridge(Periodic(wu, wv, 14, 14, seed + 12)), r2 = Ridge(Periodic(wu, wv, 28, 28, seed + 13));
                float fil = Smooth(0.04f, 0.7f, 0.68f * r1 + 0.32f * r2);
                float blot = 0.7f * Periodic(u, v, 12, 12, seed + 14) + 0.3f * Periodic(u, v, 24, 24, seed + 15);
                float fine = 0.6f * Periodic(u, v, 64, 64, seed + 16) + 0.4f * Periodic(u, v, 128, 128, seed + 17);
                int i = (y * size + x) * 4;
                px[i] = ToByte(fil); px[i + 1] = ToByte(Smooth(0.22f, 0.78f, blot)); px[i + 2] = ToByte(Smooth(0.2f, 0.8f, fine)); px[i + 3] = 255;
            }
        return px;
        static byte ToByte(float f) => (byte)Math.Clamp(MathF.Round(f * 255f), 0f, 255f);
        static float Ridge(float n) { float r = 1f - MathF.Abs(2f * n - 1f); return r * r * r; }
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

    /// <summary>The texture at (u, v) (wrapping, bilinear), each channel 0..1: filament, blotch, fine.</summary>
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

    /// <summary>A band texel: its blood opacity (before the renderer's core share and the near fade), its coverage after the erosion, and its density (lighter in the core).</summary>
    public readonly record struct Texel(float Alpha, float Cov, float Density);

    /// <summary>The opacity floor of a texel between the filaments (the filaments add the rest).</summary>
    public const float FilFloor = 0.2f;
    /// <summary>The ragged edge: a texel fades out between these shares of the nominal half width (plus the fine grain and the gaps between the filaments).</summary>
    public const float EdgeFrom = 0.7f, EdgeTo = 1.05f;
    /// <summary>The lumpy outline: each edge moves in and out by up to <see cref="LumpDepth"/> of the half width over about 1 / <see cref="LumpAlong"/> body heights.</summary>
    public const float LumpDepth = 0.45f, LumpAlong = 0.9f;
    /// <summary>The density blotches along a band: the opacity times floor + gain x blotch (lighter and darker clumps, mean about 1).</summary>
    public const float BlotFloor = 0.5f, BlotGain = 1.0f;
    /// <summary>How much more solid the swipe is before the side bands grow (frame 0 reads as one solid thin line).</summary>
    public const float SwipeSolid = 0.45f;
    /// <summary>The soft step of the patch erosion (half width).</summary>
    public const float ErodeSoft = 0.07f;
    /// <summary>The root (frames 0 to 4): the first <see cref="RootShare"/> of every band is held whole by the key's root strength, so the bands leave the wound as one mass.</summary>
    public const float RootShare = 0.3f;

    /// <summary>
    /// The texel at (<paramref name="u"/> along 0..1, <paramref name="v"/> across: 1 at the nominal edge) of band <paramref name="band"/> at key <paramref name="k"/> (the fragment shader's
    /// math): the flow-aligned filaments stretched along the band, the ragged edge and tip, the outer fade, the patch erosion against 1 - Fill, the held root, the pinholes.
    /// </summary>
    public static Texel TexelAt(Key k, BandAt b, Band band, float u, float v, int seed, byte[] tex, int size)
    {
        float s = u * b.Length;
        float av = MathF.Abs(v);
        float ou = ShaderHash(seed, 50 + (int)band), ov = ShaderHash(seed, 54 + (int)band);
        float warp = (TexSample(tex, size, s * 0.35f + ou, ov * 1.7f).Blot - 0.5f) * 0.12f;
        float tu = s / FilAlongH + ou, tv = v * FilAcrossTiles + ov + warp;
        float f1 = TexSample(tex, size, tu, tv).Fil, f2 = TexSample(tex, size, tu * 2.3f + 0.37f, tv * 2.3f + 0.61f).Fil;
        float fil = 0.65f * f1 + 0.35f * f2;
        float fine = TexSample(tex, size, tu * FineScale + 0.2f, tv * FineScale + 0.7f).Fine;
        float lump = TexSample(tex, size, s * LumpAlong + ou * 2f, (v > 0f ? 0.3f : 0.7f) + ov).Blot;
        float edge = 1f - Smooth(EdgeFrom, EdgeTo, av + 0.25f * (fine - 0.5f) + 0.2f * (1f - fil) * av + LumpDepth * (lump - 0.5f));
        float blotA = TexSample(tex, size, s * 0.6f + ou, v * 0.08f + ov * 1.3f).Blot;
        float along = Smooth(0f, 0.05f, u) * (1f - Smooth(0.8f, 1f, u + 0.15f * (fine - 0.5f)));
        float outer = 1f - k.Outer * Smooth(0.5f, 1f, u);
        float e = TexSample(tex, size, u * ErodeAlong + ou * 3.1f, v * ErodeAcross + ov * 2.3f).Blot;
        float thr = 1f - k.Fill;
        float cov = Smooth(thr - ErodeSoft, thr + ErodeSoft, 0.8f * e + 0.2f * fil);
        float rootHeld = k.Root * (1f - Smooth(0.6f * RootShare, RootShare, u));
        cov = MathF.Max(cov, rootHeld);
        float hole = 1f - 0.45f * k.Grit;
        cov *= 1f - Smooth(hole - 0.04f, hole + 0.04f, fine) * (1f - rootHeld);
        float solid = SwipeSolid * (1f - Grow(k));   // frame 0: one solid swipe
        float floor = FilFloor + solid, bf = BlotFloor + solid;
        float a = k.Opacity * b.Alpha * edge * along * outer * cov * (floor + (1f - floor) * fil) * (bf + (1f - bf) * 2f * blotA);
        return new Texel(a, cov, fil * (1f - 0.45f * MathF.Min(av, 1f)));
    }

    /// <summary>The bands a streak draws: main, upper, entry, and the lower one only when <paramref name="fourth"/> (a heavy or killing hit).</summary>
    public static int BandCount(bool fourth) => fourth ? 4 : 3;

    /// <summary>
    /// The streak's covered area (table units squared, face-on: the visible texels with opacity x <paramref name="coreAlpha"/> over <paramref name="alphaMin"/>), its farthest covered
    /// reach ahead of the wound and behind it, the main band's patches (runs of covered texels along its middle) and the mean opacity of the covered texels, at <paramref name="age"/>.
    /// </summary>
    public static (float Area, float ExitReach, float EntryReach, int MainPatches, float MeanAlpha) Measure(float age, int seed, float bend, bool fourth, byte[] tex, int size, float widthMul = 1f,
        float coreAlpha = CoreAlpha, float alphaMin = 0.07f, int nu = 160, int nv = 40)
    {
        var k = Sample(age);
        float area = 0f, fwd = 0f, back = 0f, sumA = 0f; int nA = 0, patches = 0;
        if (k.Opacity <= 0f) return (0f, 0f, 0f, 0, 0f);
        for (int bi = 0; bi < BandCount(fourth); bi++)
        {
            var band = (Band)bi;
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
                    float v = vv * EdgePad;                             // 1 at the nominal edge
                    var t = TexelAt(k, b, band, u, v, seed, tex, size);
                    float a = MathF.Min(1f, t.Alpha * coreAlpha);
                    if (iv == nv / 2 && band == Band.Main) mainLine[iu] = a > alphaMin;
                    if (a <= alphaMin) continue;
                    area += dA; sumA += a; nA++;
                    var (x, _) = RibbonPoint(b, u, vv);
                    if (x > fwd) fwd = x;
                    if (-x > back) back = -x;
                }
            }
            if (band == Band.Main) patches = Patches(mainLine);
        }
        return (area, fwd, back, patches, nA == 0 ? 0f : sumA / nA);
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
    /// The shade scale per renderer (gate repair 8, owner: keep our darker red): Forward+ draws in linear HDR with the tonemapper, Compatibility straight in display space, so the same
    /// albedo read about (229, 20, 0) on Forward+ and about (150, 0, 0) on Compatibility; Forward+ takes <see cref="ForwardShade"/> of it so both show about (150, 0, 0).
    /// </summary>
    public const float ForwardShade = 0.42f, CompatibilityShade = 1f;
    /// <summary>
    /// The strip pass's strength per unit of blood opacity, Forward+ and Compatibility (ribbon rebuild: the strip takes only green and blue from the ground under the blood, never red, so a
    /// translucent band over a light ground reads red and never near black; the strength follows the opacity so a faint texel stays translucent).
    /// </summary>
    public const float StripPerAlpha = 6f, CompatibilityStripPerAlpha = 4f;
    /// <summary>The strip's largest strength (a translucent band keeps part of the ground's green and blue, as the reference's translucent red does; a faint texel is stripped as much as a dense one, so it never reads pink).</summary>
    public const float StripMax = 0.6f, CompatibilityStripMax = 0.45f;
    /// <summary>Compatibility's strip: its multiply blend blackened the ground (captures, ribbon rebuild), so it mixes the ground toward this blood red (display space) at the strip's strength instead: green and blue fall, the red level stays near a mid-grey's.</summary>
    public const float CompatibilityStripRed = 0.3f;

    /// <summary>The core's opacity share per renderer (Compatibility blends in display space: its faint texels wash out sooner, so it takes a little more).</summary>
    public const float CoreAlpha = 0.48f, CompatibilityCoreAlpha = 0.6f;

    /// <summary>The blood's albedo for <paramref name="density"/> (repair 4's darker red, 0.3 to 0.55 of the blood red: the filament cores lighter) at the renderer's <paramref name="shade"/>.</summary>
    public static (float R, float G, float B) Colour(float density, float shade = CompatibilityShade)
    {
        float k = shade * (ClassicBloodRules.RibbonShadeFloor + (ClassicBloodRules.RibbonShadeTop - ClassicBloodRules.RibbonShadeFloor) * Smooth(0.15f, 0.85f, density));
        var b = ClassicBloodRules.BloodRgb;
        return (b.R * k, b.G * k, b.B * k);
    }

    /// <summary>The strip pass's strength for a texel of blood opacity <paramref name="alpha"/>.</summary>
    public static float Strip(float alpha, float perAlpha = StripPerAlpha, float max = StripMax) => Math.Clamp(perAlpha * alpha, 0f, max);

    /// <summary>
    /// The colour of a pixel <paramref name="under"/> beneath a band texel of blood opacity <paramref name="alpha"/> and density after both passes: the strip (a multiply that keeps the red
    /// and takes green and blue at <see cref="Strip"/>) then the blood mixed over it.
    /// </summary>
    public static (float R, float G, float B) Composite((float R, float G, float B) under, float alpha, float density, float shade = CompatibilityShade, float perAlpha = StripPerAlpha, float max = StripMax)
    {
        float s = Strip(alpha, perAlpha, max);
        var u = (R: under.R, G: under.G * (1f - s), B: under.B * (1f - s));
        var col = Colour(density, shade);
        float a = Math.Clamp(alpha, 0f, 1f);
        return (a * col.R + (1f - a) * u.R, a * col.G + (1f - a) * u.G, a * col.B + (1f - a) * u.B);
    }

    /// <summary>Linear to display (sRGB) for one channel.</summary>
    public static float ToSrgb(float l) => l <= 0.0031308f ? 12.92f * l : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;

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
    /// One live streak per victim (ribbon rebuild, lead 41: replace, not stack): a new streak on a victim whose last streak started less than <see cref="ReplaceSeconds"/> ago (and still
    /// shows) takes that streak's slot and starts over, so a burst of hits never stacks into a slab.
    /// </summary>
    public const double ReplaceSeconds = 0.25;
    /// <summary>Whether a victim's streak started at <paramref name="start"/> is replaced by a new one at <paramref name="now"/>.</summary>
    public static bool Replaces(double start, double now) => now - start < ReplaceSeconds && now - start > -2.0;

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
    /// The frame's Warfront admission (finding M2, engine-free): the asks sorted own first, killing first, then nearest; the own ones at most <see cref="WarfrontOwnPerWindow"/> in the
    /// current window (<paramref name="ownInWindow"/> already spent) and while fewer than <see cref="WarfrontOwnLiveMax"/> own and <see cref="WarfrontLiveMax"/> in all show
    /// (<paramref name="liveOwn"/>, <paramref name="liveAll"/>, counted up as admitted); the others under the live cap and with a token from <paramref name="bucket"/>, taken last.
    /// Returns the admitted asks' indices in admission order.
    /// </summary>
    public static System.Collections.Generic.List<int> AdmitStreaks(System.Collections.Generic.List<StreakAsk> asks, int ownInWindow, int liveOwn, int liveAll, Bucket bucket, double now)
    {
        asks.Sort((a, b) => a.Own != b.Own ? (a.Own ? -1 : 1) : a.Lethal != b.Lethal ? (a.Lethal ? -1 : 1) : a.Distance.CompareTo(b.Distance));
        var admitted = new System.Collections.Generic.List<int>();
        foreach (var a in asks)
        {
            if (liveAll >= WarfrontLiveMax) break;
            if (a.Own)
            {
                if (ownInWindow >= WarfrontOwnPerWindow || liveOwn >= WarfrontOwnLiveMax) continue;
                ownInWindow++; liveOwn++;
            }
            else if (!bucket.Take(now)) continue;
            liveAll++;
            admitted.Add(a.Index);
        }
        return admitted;
    }

    /// <summary>A body <paramref name="height"/> m tall at <paramref name="distance"/> m, seen with a vertical field of view <paramref name="fovYDegrees"/> on a <paramref name="screenHeight"/>-pixel view: its height in pixels.</summary>
    public static float ProjectedPixels(float height, float distance, float fovYDegrees, float screenHeight) =>
        distance <= 0.01f ? float.PositiveInfinity : height / (2f * distance * MathF.Tan(fovYDegrees * MathF.PI / 360f)) * screenHeight;

    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }
}
