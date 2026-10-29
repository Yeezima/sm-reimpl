using System.Numerics;
using SmFormats.Bod;

namespace SmContent;

public enum MapLightKind { Omni, Spot }

/// <summary>
/// A placed light (<c>base/omnilight</c> or <c>base/spotlight</c> in a layer). <paramref name="Power"/> is the
/// original's unit-less multiplier (0.5 to 5 in Manufactorum). <paramref name="AttenStart"/> and
/// <paramref name="AttenEnd"/> are metres: full strength inside the first, nothing beyond the second (the object's
/// <c>Scale</c> repeats <c>AttenEnd</c> in all three axes: an editor gizmo, not a geometric scale).
/// <paramref name="Direction"/> is a **provisional** reading (see <see cref="MapLighting"/>); omni lights ignore it.
/// </summary>
public sealed record MapLight(MapLightKind Kind, string Name, Vector3 Position, Vector3 Direction, Vector3 Colour, float Power,
    float AttenStart, float AttenEnd, float FovDegrees, bool CastShadows, bool Static);

/// <summary>The directional sun: <c>base/sunlight</c>, repeated by an environment region's <c>SunDesc</c>.</summary>
public sealed record SunLight(Vector3 Direction, Vector3 Colour, float Power, int ShadowSplits, float ShadowDistance);

/// <summary>
/// An environment region's <c>FogDesc</c>: <c>OutscatterColour</c> is a two-stop gradient (RGBA bytes /255) over
/// <paramref name="Range"/> metres, nearest stop first; the ground fog entries are all zero in Manufactorum.
/// </summary>
public sealed record FogDesc(Vector4 NearColour, Vector4 FarColour, float Range, float GroundHeight, float GroundRange);

/// <summary>An <c>AmbientDesc</c>: hemisphere ambient colours (lower = ground side, upper = sky side).</summary>
public sealed record AmbientDesc(Vector3 Lower, Vector3 Upper, float Saturation, float SsaoMultiplier, bool SsaoDisabled);

public sealed record HdrDesc(float BrightThresholdMin, float BrightThresholdMax, float BloomIntensity, float BloomPersistence);

/// <summary>
/// A <c>base/environmentregion</c>: a box (<paramref name="Centre"/>, full extent <paramref name="Size"/>) with five
/// descriptions. The highest <paramref name="Priority"/> wins where regions overlap (assumption).
/// </summary>
public sealed record MapEnvironment(string Name, int Priority, Vector3 Centre, Vector3 Size, FogDesc? Fog, AmbientDesc? Ambient,
    SunLight? Sun, HdrDesc? Hdr)
{
    /// <summary>0 outside the box; inside, rising smoothly (smoothstep) from 0 at a face to 1 at <paramref name="feather"/> metres in.</summary>
    public float Weight(Vector3 p, float feather)
    {
        var d = Size * 0.5f - Vector3.Abs(p - Centre);
        float inside = MathF.Min(d.X, MathF.Min(d.Y, d.Z));
        if (inside <= 0f) return 0f;
        float t = feather <= 0f ? 1f : Math.Clamp(inside / feather, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public bool Contains(Vector3 p) =>
        MathF.Abs(p.X - Centre.X) <= Size.X * 0.5f && MathF.Abs(p.Y - Centre.Y) <= Size.Y * 0.5f && MathF.Abs(p.Z - Centre.Z) <= Size.Z * 0.5f;
}

/// <summary>
/// The lighting of a map, read from its <c>l_lights</c> layer: the sun, the environment regions (fog, ambient, HDR) and
/// the placed omni and spot lights. Values are read as stored; the interpretation is labelled per field:
/// <list type="bullet">
/// <item>Light direction: **provisional approximation**. Lights point along local -Z (3ds Max convention) under the same
/// rotation order as objects (degrees about X, then Y, then Z, fixed axes). Evidence: spot lights hang at height 17 m
/// with range 15 m and rotations of a few degrees about X or Y (plus 90 degrees about Z), which fits "straight down with
/// a small tilt" and not a horizontal beam; the sun, rotation (-45, 45, 0), then has 30 degrees elevation.</item>
/// <item>Power, colours and ranges are values from the file; how the engine turns Power into radiance is not known.</item>
/// </list>
/// Engine-neutral: Z up, metres.
/// </summary>
public sealed class MapLighting
{
    public SunLight? Sun { get; init; }
    public IReadOnlyList<MapEnvironment> Environments { get; init; } = [];
    public IReadOnlyList<MapLight> Lights { get; init; } = [];

    /// <summary>The map-wide region (lowest priority number): what applies where no other region does.</summary>
    public MapEnvironment? Global => Environments.OrderBy(e => e.Priority).FirstOrDefault();

    /// <summary>The region that applies at a point: the highest priority among those containing it, else <see cref="Global"/>.</summary>
    public MapEnvironment? At(Vector3 p) =>
        Environments.Where(e => e.Contains(p)).OrderByDescending(e => e.Priority).FirstOrDefault() ?? Global;

    public static MapLighting Read(BodValue root)
    {
        SunLight? sun = null;
        var environments = new List<MapEnvironment>();
        var lights = new List<MapLight>();
        Walk(root, Matrix4x4.Identity, 0, ref sun, environments, lights);
        return new MapLighting { Sun = sun, Environments = environments, Lights = lights };
    }

    static void Walk(BodValue value, Matrix4x4 parent, int depth, ref SunLight? sun, List<MapEnvironment> environments, List<MapLight> lights)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
            {
                if (s["Disabled"] is BodBool { Value: true }) return;   // a disabled object hides its children too
                var world = MapScene.Local(s) * parent;
                switch (s.Class.Text)
                {
                    case "base/sunlight":
                        sun ??= new SunLight(Forward(world), Vec3(s["Color"]) ?? Vector3.One, Num(s["Power"]) ?? 1f,
                            (int)(Num(s["NumSunSplits"]) ?? 4f), Num(s["FarSunDistance"]) ?? 400f);
                        break;
                    case "base/omnilight" or "base/spotlight" when s["Enabled"] is not BodBool { Value: false }:
                    {
                        bool spot = s.Class.Text == "base/spotlight";
                        float end = Num(s["AttenEnd"]) ?? 10f;
                        lights.Add(new MapLight(spot ? MapLightKind.Spot : MapLightKind.Omni, (s["Name"] as BodString)?.Text ?? "",
                            new Vector3(world.M41, world.M42, world.M43), Forward(world), Vec3(s["Color"]) ?? Vector3.One, Num(s["Power"]) ?? 1f,
                            Math.Clamp(Num(s["AttenStart"]) ?? 0f, 0f, end), end, spot ? Num(s["FOV"]) ?? 60f : 0f,
                            s["CastShadows"] is BodBool { Value: true }, s["Static"] is BodBool { Value: true }));
                        break;
                    }
                    case "base/environmentregion" when s["Enabled"] is not BodBool { Value: false }:
                        environments.Add(Environment(s, world));
                        break;
                }
                foreach (var f in s.Fields)
                    if (f.Name.Text is "Root" or "Objects" or "ChildObjects")
                        Walk(f.Value, f.Name.Text == "ChildObjects" ? world : parent, depth + 1, ref sun, environments, lights);
                break;
            }
            case BodList l: foreach (var item in l.Items) Walk(item.Value, parent, depth + 1, ref sun, environments, lights); break;
            case BodRef r: Walk(r.Value, parent, depth + 1, ref sun, environments, lights); break;
        }
    }

    static MapEnvironment Environment(BodStruct s, Matrix4x4 world)
    {
        FogDesc? fog = null; AmbientDesc? ambient = null; SunLight? sun = null; HdrDesc? hdr = null;
        foreach (var d in Items(s["EnvironmentDescs"]).OfType<BodStruct>())
            switch (d.Class.Text)
            {
                case "FogDesc":
                {
                    var stops = Items(d["OutscatterColour"]).Select(Gradient).OfType<Vector4>().ToList();
                    if (stops.Count >= 1)
                        fog = new FogDesc(stops[0], stops[^1], Num(d["OutscatterRange"]) ?? 0f, Num(d["GroundFogHeight"]) ?? 0f, Num(d["GroundFogRange"]) ?? 0f);
                    break;
                }
                case "AmbientDesc":
                    if (Vec3(d["LowerAmbientColor"]) is { } lower && Vec3(d["UpperAmbientColor"]) is { } upper)
                        ambient = new AmbientDesc(lower, upper, Num(d["AmbientSaturation"]) ?? 1f, Num(d["SSAO_Multiplier"]) ?? 1f,
                            d["SSAO_DisableSSAO"] is BodBool { Value: true });
                    break;
                case "SunDesc":
                {
                    var r = Floats(d["Rotation"]) ?? [];
                    var q = MapScene.Rotation(r.Length > 0 ? r[0] : 0f, r.Length > 1 ? r[1] : 0f, r.Length > 2 ? r[2] : 0f);
                    sun = new SunLight(Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, q)), Vec3(d["Colour"]) ?? Vector3.One, Num(d["Power"]) ?? 1f,
                        (int)(Num(d["NumSunSplits"]) ?? 4f), Num(d["FarSunDistance"]) ?? 400f);
                    break;
                }
                case "HDRDesc":
                    hdr = new HdrDesc(Num(d["BrightThresholdMin"]) ?? 0f, Num(d["BrightThresholdMax"]) ?? 1f, Num(d["BloomIntensity"]) ?? 1f, Num(d["BloomPersistence"]) ?? 0f);
                    break;
            }
        // The region's Scale is its box size, so only the position comes from the transform.
        var size = Vec3(s["Scale"]) ?? Vector3.One;
        return new MapEnvironment((s["Name"] as BodString)?.Text ?? "", (int)(Num(s["Priority"]) ?? 0f),
            new Vector3(world.M41, world.M42, world.M43), size, fog, ambient, sun, hdr);
    }

    /// <summary>One gradient stop: an array [position, [r, g, b, a]] with the colour as bytes (0-255) or floats.</summary>
    static Vector4? Gradient(BodValue v)
    {
        if (v is not BodArray { Items: [_, BodArray { Items: { Count: >= 4 } c }, ..] }) return null;
        float[] x = new float[4];
        for (int i = 0; i < 4; i++)
            x[i] = c[i] switch { BodUInt64 u => u.Value / 255f, BodInt n => n.Value / 255f, BodFloat f => f.Value, _ => 0f };
        return new Vector4(x[0], x[1], x[2], x[3]);
    }

    /// <summary>Local -Z through the object's rotation (see the class remarks).</summary>
    static Vector3 Forward(Matrix4x4 world)
    {
        var d = Vector3.TransformNormal(-Vector3.UnitZ, world);
        return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : -Vector3.UnitZ;
    }

    static float? Num(BodValue? v) => v switch { BodFloat f => f.Value, BodInt i => i.Value, _ => null };
    static Vector3? Vec3(BodValue? v) => Floats(v) is [var x, var y, var z, ..] ? new Vector3(x, y, z) : null;
    static float[]? Floats(BodValue? v) =>
        v is BodArray { Items: var items } && items.All(i => i is BodFloat or BodInt) ? items.Select(i => i is BodFloat f ? f.Value : ((BodInt)i).Value).ToArray() : null;
    static IEnumerable<BodValue> Items(BodValue? v) =>
        (v is BodRef r ? r.Value : v) is BodList l ? l.Items.Select(i => i.Value is BodRef ri ? ri.Value : i.Value) : [];
}
