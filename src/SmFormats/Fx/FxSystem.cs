using System.Numerics;
using SmFormats.Bod;

namespace SmFormats.Fx;

/// <summary>One key of an effect curve: <see cref="Time"/> (interpretation: a fraction 0..1 of the element's duration) and a value.</summary>
public readonly record struct FxKey(float Time, float Value);

/// <summary>
/// A keyframed effect parameter (<c>Frame*</c> fields and the like): a BOD list of <c>[time, value]</c> arrays. One key is a
/// constant; between keys the value is interpolated linearly (interpretation: the original's interpolation is not decoded);
/// before the first and after the last key it holds. An empty curve evaluates to 0.
/// </summary>
public sealed record FxCurve(IReadOnlyList<FxKey> Keys)
{
    public static readonly FxCurve Empty = new([]);
    public bool IsEmpty => Keys.Count == 0;
    /// <summary>The value when every key has it (one key, or all equal), else null.</summary>
    public float? Constant => Keys.Count > 0 && Keys.All(k => k.Value == Keys[0].Value) ? Keys[0].Value : null;
    public float Min => Keys.Count == 0 ? 0f : Keys.Min(k => k.Value);
    public float Max => Keys.Count == 0 ? 0f : Keys.Max(k => k.Value);

    public float Evaluate(float t)
    {
        if (Keys.Count == 0) return 0f;
        if (!(t > Keys[0].Time)) return Keys[0].Value;
        for (int i = 1; i < Keys.Count; i++)
        {
            var (a, b) = (Keys[i - 1], Keys[i]);
            if (t > b.Time) continue;
            float span = b.Time - a.Time;
            return span > 0f ? a.Value + (b.Value - a.Value) * ((t - a.Time) / span) : b.Value;
        }
        return Keys[^1].Value;
    }
}

/// <summary>A colour as effect curves store it: four components 0..255 (stored as 8-byte integers, BOD type 0x0D).</summary>
public readonly record struct FxColour(byte R, byte G, byte B, byte A)
{
    /// <summary>Components as 0..1.</summary>
    public Vector4 ToVector() => new(R / 255f, G / 255f, B / 255f, A / 255f);
}

public readonly record struct FxColourKey(float Time, FxColour Colour);

/// <summary>A keyframed colour (<c>[time, [r, g, b, a]]</c>); evaluated like <see cref="FxCurve"/>, per component, as 0..1.</summary>
public sealed record FxColourCurve(IReadOnlyList<FxColourKey> Keys)
{
    public static readonly FxColourCurve Empty = new([]);
    public bool IsEmpty => Keys.Count == 0;

    /// <summary>The colour at <paramref name="t"/> as 0..1 components; opaque white for an empty curve.</summary>
    public Vector4 Evaluate(float t)
    {
        if (Keys.Count == 0) return Vector4.One;
        if (!(t > Keys[0].Time)) return Keys[0].Colour.ToVector();
        for (int i = 1; i < Keys.Count; i++)
        {
            var (a, b) = (Keys[i - 1], Keys[i]);
            if (t > b.Time) continue;
            float span = b.Time - a.Time;
            return span > 0f ? Vector4.Lerp(a.Colour.ToVector(), b.Colour.ToVector(), (t - a.Time) / span) : b.Colour.ToVector();
        }
        return Keys[^1].Colour.ToVector();
    }
}

/// <summary>
/// A sprite material: its name and the BOD string hash that names it (the <see cref="FxTexturePage"/> key; 0 for no name),
/// blend (<c>AdditiveBlend</c>: 0 alpha, 1 additive, 2 a distortion material, interpretation), soft-particle mode, and the
/// sprite sheet's cells (<c>TileX</c> × <c>TileY</c>; <c>RandomTile</c> picks one).
/// </summary>
public sealed record FxMaterial(string Name, ulong Hash, int AdditiveBlend, int SoftParticle, float TileX, float TileY, bool RandomTile, int UAddressMode)
{
    public bool IsEmpty => Name.Length == 0;
}

/// <summary>Per-element physics (<c>FXSimpleEnvironment</c>): gravity, wind, attraction, drag, bounce, collision response (units unknown).</summary>
public sealed record FxEnvironment(float Gravity, float Wind, float Attraction, float Drag, float Bounce, int Position, int Orientation, int CollisionResponse);

public enum FxElementKind { Ring, Spray, Combo, Beam, Streamer, Light, Audio, Decal, Mesh, Unknown }

/// <summary>
/// One node of an effect's description tree (an <c>FX*Description</c> struct). Fields every kind shares are typed here; the
/// subclasses type their own; <see cref="Source"/> keeps the whole struct so fields not typed yet stay readable
/// (<see cref="Curve"/>, <see cref="Float"/> and the like).
/// </summary>
public abstract record FxElement(BodStruct Source)
{
    public string Class => Source.Class.Text ?? "";
    public string Name => FxRead.Text(Source, "Name");
    public abstract FxElementKind Kind { get; }
    /// <summary>Seconds (<c>Duration</c>; for an element inside a spray, the life of each particle).</summary>
    public float Duration => FxRead.Float(Source, "Duration") ?? 0f;
    /// <summary>Random extra seconds (<c>DurationEpsilon</c>; interpretation: up to this much longer or shorter).</summary>
    public float DurationEpsilon => FxRead.Float(Source, "DurationEpsilon") ?? 0f;
    public bool IsLooping => FxRead.Bool(Source, "IsLooping") ?? false;
    public bool Hide => FxRead.Bool(Source, "Hide") ?? false;
    /// <summary><c>FXLOD</c> (3 on most elements; meaning of the levels unknown).</summary>
    public int Lod => FxRead.Int(Source, "FXLOD") ?? 0;
    public IReadOnlyList<FxEnvironment> Environment => FxRead.Environment(Source);
    /// <summary>Child elements (combos and sprays: <c>FXDescriptions</c>; what a spray emits per particle); empty for others.</summary>
    public virtual IReadOnlyList<FxElement> Children => [];
    /// <summary>
    /// What starts where a particle of this element touches the level (<c>FXCollisionDescriptions</c>, typed in M41; empty for most): the particle's <see cref="Environment"/>
    /// has a <c>CollisionResponse</c> (3 and 6 on the install). The ground pound's <c>jumppack_land_heavy</c> is a "dropper" spray (gravity 100) whose collider ring holds the whole
    /// impact here: the crater and crack decals, the flash, the dust and debris sprays, the ground shockwave sprites. Not part of <see cref="Descendants"/>
    /// (a player that does not run collisions must not draw them); see <see cref="DescendantsWithCollisions"/>.
    /// </summary>
    public IReadOnlyList<FxElement> OnCollision { get; init; } = [];

    public FxCurve Curve(string field) => FxRead.Curve(Source, field);
    public FxColourCurve Colours(string field) => FxRead.Colours(Source, field);
    public float? Float(string field) => FxRead.Float(Source, field);
    public int? Int(string field) => FxRead.Int(Source, field);
    public bool? Bool(string field) => FxRead.Bool(Source, field);
    public string Text(string field) => FxRead.Text(Source, field);
    /// <summary>Every field name the struct has, in file order.</summary>
    public IEnumerable<string> FieldNames => Source.Fields.Select(f => f.Name.Text ?? "");

    /// <summary>This element and every descendant, depth first.</summary>
    public IEnumerable<FxElement> Descendants()
    {
        yield return this;
        foreach (var c in Children) foreach (var d in c.Descendants()) yield return d;
    }

    /// <summary>As <see cref="Descendants"/>, also following <see cref="OnCollision"/> (what the particles start on touching the level).</summary>
    public IEnumerable<FxElement> DescendantsWithCollisions()
    {
        yield return this;
        foreach (var c in Children.Concat(OnCollision)) foreach (var d in c.DescendantsWithCollisions()) yield return d;
    }

    protected FxMaterial MaterialOf() => FxRead.Material(Source);
    protected Vector3 Vec(string x, string y, string z) => new(Float(x) ?? 0f, Float(y) ?? 0f, Float(z) ?? 0f);
}

/// <summary>A sprite quad (<c>FXRingDescription</c>): camera-facing with <see cref="Billboard"/>, else planar.</summary>
public sealed record FxRing(BodStruct Source) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Ring;
    public FxMaterial Material => MaterialOf();
    public bool Billboard => Bool("Billboard") ?? false;
    public FxCurve Radius => Curve("FrameRadius");
    public float EpsilonRadius => Float("EpsilonRadius") ?? 0f;
    public FxColourCurve Colour => Colours("FrameColour");
    public FxCurve OffsetX => Curve("FrameOffsetX");
    public FxCurve OffsetY => Curve("FrameOffsetY");
    public FxCurve OffsetZ => Curve("FrameOffsetZ");
    public FxCurve SpinX => Curve("FrameSpinX");
    public FxCurve SpinY => Curve("FrameSpinY");
    public FxCurve SpinZ => Curve("FrameSpinZ");
    /// <summary>Degrees (interpretation), with <see cref="UseStartAngle"/> saying which axes apply.</summary>
    public Vector3 StartAngle => Vec("StartAngleX", "StartAngleY", "StartAngleZ");
    public (bool X, bool Y, bool Z) UseStartAngle => (Bool("UseStartAngleX") ?? false, Bool("UseStartAngleY") ?? false, Bool("UseStartAngleZ") ?? false);
    public bool RandomSpinDirection => Bool("RandomSpinDirection") ?? false;
    public float DepthOffset => Float("DepthOffset") ?? 0f;
    public FxCurve AnimationRate => Curve("FrameAnimationRate");
}

/// <summary>One burst of a spray: <see cref="NumParticles"/> at <see cref="BurstTime"/> (s, interpretation).</summary>
public readonly record struct FxBurst(int NumParticles, float BurstTime);

/// <summary>A particle emitter (<c>FXSprayDescription</c>): each particle shows <see cref="Children"/>.</summary>
public sealed record FxSpray(BodStruct Source, IReadOnlyList<FxElement> Elements) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Spray;
    public override IReadOnlyList<FxElement> Children => Elements;
    /// <summary>
    /// A "dropper" (M41): the spray's environment has a collision response and a child holds collision descriptions (<see cref="FxElement.OnCollision"/>). The install's ground
    /// effects (<c>jumppack_land_*</c>, the grenade explosions) are one: a particle that falls and, on touching the level, starts what its collider holds.
    /// </summary>
    public bool IsDropper => Environment.Any(e => e.CollisionResponse != 0) && Children.Any(c => c.OnCollision.Count > 0);
    public IReadOnlyList<FxBurst> Bursts => FxRead.Structs(Source, "BurstList")
        .Select(b => new FxBurst(FxRead.Int(b, "NumParticles") ?? 0, FxRead.Float(b, "BurstTime") ?? 0f)).ToList();
    /// <summary>Particles per second (interpretation).</summary>
    public FxCurve Rate => Curve("EmitFrameRate");
    /// <summary>Cone half-angle in radians (1.571: a hemisphere; interpretation).</summary>
    public FxCurve Deviation => Curve("EmitFrameDeviation");
    public FxCurve InnerDeviation => Curve("EmitFrameInnerDeviation");
    public FxCurve VolumeX => Curve("EmitFrameVolumeX");
    public FxCurve VolumeY => Curve("EmitFrameVolumeY");
    public FxCurve VolumeZ => Curve("EmitFrameVolumeZ");
    public FxCurve OffsetX => Curve("EmitFrameOffsetX");
    public FxCurve OffsetY => Curve("EmitFrameOffsetY");
    public FxCurve OffsetZ => Curve("EmitFrameOffsetZ");
    public FxCurve InitialSpeed => Curve("EmitFrameInitialSpeed");
    public FxCurve ParticleScale => Curve("ParticleScaleFrames");
    public FxCurve ParticleLength => Curve("ParticleLengthFrames");
    public float InheritVelocity => Float("InheritVelocity") ?? 0f;
    public int Direction => Int("Direction") ?? 0;
    public int EmitterShape => Int("EmitterShape") ?? 0;
    public bool EmitFromPoint => Bool("EmitFromPoint") ?? false;
    public bool ScaleSpeed => Bool("ScaleSpeed") ?? false;
    public bool RandomStartTime => Bool("RandomStartTime") ?? false;
}

/// <summary>A group (<c>FXComboDescription</c>) with its own scale, offset and spin; <see cref="OneAtRandom"/> plays one child.</summary>
public sealed record FxCombo(BodStruct Source, IReadOnlyList<FxElement> Elements) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Combo;
    public override IReadOnlyList<FxElement> Children => Elements;
    public bool OneAtRandom => Bool("OneAtRandom") ?? false;
    public bool ShareRandomSeed => Bool("ShareRandomSeed") ?? false;
    public bool InvertDrawOrder => Bool("InvertDrawOrder") ?? false;
    public FxCurve Scale => Curve("FrameScale");
    public FxCurve OffsetX => Curve("FrameOffsetX");
    public FxCurve OffsetY => Curve("FrameOffsetY");
    public FxCurve OffsetZ => Curve("FrameOffsetZ");
    public FxCurve SpinX => Curve("FrameSpinX");
    public FxCurve SpinY => Curve("FrameSpinY");
    public FxCurve SpinZ => Curve("FrameSpinZ");
    public Vector3 StartAngle => Vec("StartAngleX", "StartAngleY", "StartAngleZ");
}

/// <summary>
/// A textured ribbon (<c>FXBeamDescription</c>), e.g. a tracer from the muzzle to the hit point: <see cref="Length"/> (1 = the
/// whole span when the effect has a target; interpretation), <see cref="Width"/> (m), texture metres per U and
/// <see cref="USpeed"/> (the texture's scroll; units not established, see spec/behavior.md).
/// </summary>
public sealed record FxBeam(BodStruct Source) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Beam;
    public FxMaterial Material => MaterialOf();
    public int NumSegments => Int("NumSegments") ?? 1;
    public bool Billboard => Bool("Billboard") ?? false;
    public FxCurve Length => Curve("FrameLength");
    public FxCurve Width => Curve("FrameWidth");
    public FxCurve WidthOverLength => Curve("WidthOverLength");
    public float EpsilonLength => Float("EpsilonLength") ?? 0f;
    public float EpsilonWidth => Float("EpsilonWidth") ?? 0f;
    public float MeterPerU => Float("MeterPerU") ?? 0f;
    public float EpsilonMeterPerU => Float("EpsilonMeterPerU") ?? 0f;
    public float USpeed => Float("USpeed") ?? 0f;
    public FxCurve Noise => Curve("Noise");
    public float NoiseSpeed => Float("NoiseSpeed") ?? 0f;
    public FxCurve TextureUOffset => Curve("FrameTextureUOffset");
    public FxCurve TextureURepeat => Curve("FrameTextureURepeat");
    public FxColourCurve Colour => Colours("FrameColour");
    public FxCurve ArcX => Curve("ArcX");
    public FxCurve ArcY => Curve("ArcY");
    public FxCurve ArcZ => Curve("ArcZ");
    public FxCurve OffsetX => Curve("FrameOffsetX");
    public FxCurve OffsetY => Curve("FrameOffsetY");
    public FxCurve OffsetZ => Curve("FrameOffsetZ");
    public FxCurve AnimationRate => Curve("FrameAnimationRate");
    public bool GodRayFade => Bool("GodRayFade") ?? false;
    public float FadeAngleCutoff => Float("FadeAngleCutoff") ?? 0f;
    public bool FadeWhenPerpendicular => Bool("FadeWhenPerpendicular") ?? false;
}

/// <summary>A trail behind a moving point (<c>FXStreamerDescription</c>).</summary>
public sealed record FxStreamer(BodStruct Source) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Streamer;
    public FxMaterial Material => MaterialOf();
    /// <summary>Trail length (metres, or seconds with <see cref="LengthAsSeconds"/>; interpretation).</summary>
    public FxCurve Length => Curve("Length");
    public bool LengthAsSeconds => Bool("LengthAsSeconds") ?? false;
    public float EpsilonLength => Float("EpsilonLength") ?? 0f;
    public float Step => Float("Step") ?? 0f;
    public float Speed => Float("Speed") ?? 0f;
    public float Gravity => Float("Gravity") ?? 0f;
    public float Drag => Float("Drag") ?? 0f;
    public float Wind => Float("Wind") ?? 0f;
    public FxColourCurve Colour => Colours("Colour");
    public FxColourCurve MasterColour => Colours("MasterColour");
    public FxCurve Width => Curve("FrameWidth");
    public FxCurve EpsilonWidth => Curve("FrameEpsilonWidth");
    public float MeterPerU => Float("MeterPerU") ?? 0f;
    public float USpeed => Float("USpeed") ?? 0f;
    public int Billboard => Int("Billboard") ?? 0;
    public float FadeOutOnDeath => Float("FadeOutOnDeath") ?? 0f;
    public int Direction => Int("Direction") ?? 0;
}

/// <summary>A light flash (<c>FXLightDescription</c>): colour, intensity, attenuation start and end (m), optional spot and mask.</summary>
public sealed record FxLight(BodStruct Source) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Light;
    public FxColourCurve Colour => Colours("FrameColour");
    public FxCurve Intensity => Curve("FrameIntensity");
    public FxCurve AttenStart => Curve("AttenStart");
    public FxCurve AttenEnd => Curve("AttenEnd");
    public FxCurve Power => Curve("Power");
    public bool IsSpotLight => Bool("IsSpotLight") ?? false;
    public FxCurve Fov => Curve("FOV");
    public FxCurve OffsetX => Curve("FrameOffsetX");
    public FxCurve OffsetY => Curve("FrameOffsetY");
    public FxCurve OffsetZ => Curve("FrameOffsetZ");
    public bool CastsShadows => Bool("CastsShadows") ?? false;
    public bool UseMask => Bool("UseMask") ?? false;
    public string MaskName => Text("MaskName");
}

/// <summary>A sound (<c>FXAudioDescription</c>): a one-off audio event name.</summary>
public sealed record FxAudio(BodStruct Source) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Audio;
    public string EventName => Text("OneOffAudioEventName");
    public bool UseParticleLevelGameObject => Bool("UseParticleLevelGameObject") ?? false;
}

/// <summary>
/// A projected decal box (<c>FXScreenSpaceDecalDescription</c>): textures (loose DDS entries, not the atlas), box size (m),
/// normal threshold (degrees), fade by <see cref="Colour"/> alpha over the duration.
/// </summary>
public sealed record FxDecal(BodStruct Source) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Decal;
    public string DiffuseMap => Text("DiffuseMap");
    public string NormalMap => Text("NormalMap");
    public string SpecularMap => Text("SpecularMap");
    public string MapsPackageName => Text("MapsPackageName");
    public float Gloss => Float("Gloss") ?? 0f;
    public float Width => Float("Width") ?? 0f;
    /// <summary>The box height (files spell it <c>Height</c> or <c>height</c>).</summary>
    public float Height => Float("Height") ?? Float("height") ?? 0f;
    public float Depth => Float("Depth") ?? 0f;
    public float NormalThreshold => Float("NormalThreshold") ?? 0f;
    public float EpsilonScale => Float("EpsilonScale") ?? 0f;
    public FxCurve Scale => Curve("Scale");
    public FxColourCurve Colour => Colours("FrameColour");
    public FxCurve OffsetX => Curve("FrameOffsetX");
    public FxCurve OffsetY => Curve("FrameOffsetY");
    public FxCurve OffsetZ => Curve("FrameOffsetZ");
    public FxCurve SpinZ => Curve("FrameSpinZ");
    public bool AdditiveBlend => Bool("AdditiveBlend") ?? false;
    public float TileU => Float("TileU") ?? 1f;
    public float TileV => Float("TileV") ?? 1f;
    public bool RandomTile => Bool("RandomTile") ?? false;
    public bool RandomSpinDirection => Bool("RandomSpinDirection") ?? false;
}

/// <summary>A spawned mesh (<c>FXMeshDescription</c>), e.g. a shell casing: the mesh entry and the package holding it.</summary>
public sealed record FxMesh(BodStruct Source) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Mesh;
    public string Mesh => Text("Mesh");
    public string PackageName => Text("PackageName");
    public FxCurve Scale => Curve("Scale");
    public float EpsilonScale => Float("EpsilonScale") ?? 0f;
    public FxColourCurve Colour => Colours("FrameColour");
    public FxCurve OffsetX => Curve("FrameOffsetX");
    public FxCurve OffsetY => Curve("FrameOffsetY");
    public FxCurve OffsetZ => Curve("FrameOffsetZ");
    public FxCurve SpinX => Curve("FrameSpinX");
    public FxCurve SpinY => Curve("FrameSpinY");
    public FxCurve SpinZ => Curve("FrameSpinZ");
    public bool CastsShadows => Bool("CastsShadows") ?? false;
    public bool EnableLighting => Bool("EnableLighting") ?? false;
}

/// <summary>A description class not typed yet: kept with its name and fields (and any children it lists) for later.</summary>
public sealed record FxUnknownElement(BodStruct Source, IReadOnlyList<FxElement> Elements) : FxElement(Source)
{
    public override FxElementKind Kind => FxElementKind.Unknown;
    public override IReadOnlyList<FxElement> Children => Elements;
}

/// <summary>An emitter (<c>FXEmitter</c>): a root of descriptions, optionally hung on a node (<see cref="RefNode"/>) or on screen.</summary>
public sealed record FxEmitter(string Name, bool Hide, bool Screenspace, Vector2 ScreenspacePosition, float LodDistanceMedium, float LodDistanceFar,
    float SmoothMoveDistance, string RefNode, IReadOnlyList<FxElement> Elements);

/// <summary>
/// A particle system (<c>PSystem</c> entry; BOD v2, M25a): its emitters and their description trees, typed over
/// <see cref="BodDocument"/> (layout in docs/discovery.md). Classes not typed yet become <see cref="FxUnknownElement"/>s and
/// are named in <see cref="Notes"/>. No engine types.
/// </summary>
public sealed record FxSystem(string Name, Vector3 BackgroundColor, string RefObject, string RefAnimation, int SortOverride, int GlobalSortOverride,
    IReadOnlyList<FxEmitter> Emitters, IReadOnlyList<string> Notes)
{
    /// <summary>Every element of every emitter, depth first.</summary>
    public IEnumerable<FxElement> Elements => Emitters.SelectMany(e => e.Elements).SelectMany(x => x.Descendants());

    /// <summary>Every element including those that start on a collision (<see cref="FxElement.OnCollision"/>).</summary>
    public IEnumerable<FxElement> ElementsWithCollisions => Emitters.SelectMany(e => e.Elements).SelectMany(x => x.DescendantsWithCollisions());

    /// <summary>Every sprite material named (rings, beams, streamers), distinct, in first-use order; empty names left out.</summary>
    public IReadOnlyList<FxMaterial> Materials => Elements.Select(e => e switch { FxRing r => r.Material, FxBeam b => b.Material, FxStreamer s => s.Material, _ => null })
        .OfType<FxMaterial>().Where(m => !m.IsEmpty).DistinctBy(m => m.Hash).ToList();

    /// <summary>
    /// How long a non-looping system shows (seconds; **approximation**): the longest top-level element's duration plus its
    /// epsilon, where a spray adds its children's (per-particle) longest life to its own. Null when any element loops.
    /// </summary>
    public float? Lifetime => Elements.Any(e => e.IsLooping) ? null
        : Emitters.SelectMany(e => e.Elements).Select(LifeOf).DefaultIfEmpty(0f).Max();

    /// <summary>
    /// How long what the collision descriptions start shows (seconds; **approximation**, as <see cref="Lifetime"/>): the longest of them, **decals left out** (a decal is placed once
    /// and lives on its own clock), 0 for a system without any. A player that runs collisions keeps the effect alive at least this long.
    /// </summary>
    public float CollisionLife => ElementsWithCollisions.Where(e => e.OnCollision.Count > 0)
        .SelectMany(e => e.OnCollision).Select(SpriteLifeOf).DefaultIfEmpty(0f).Max();

    static float SpriteLifeOf(FxElement e) => e is FxDecal ? 0f
        : e.Duration + MathF.Max(0f, e.DurationEpsilon) + e.Children.Concat(e.OnCollision).Select(SpriteLifeOf).DefaultIfEmpty(0f).Max();

    static float LifeOf(FxElement e) => e.Duration + MathF.Max(0f, e.DurationEpsilon) + (e is FxSpray or FxCombo or FxUnknownElement ? e.Children.Select(LifeOf).DefaultIfEmpty(0f).Max() : 0f);

    /// <summary>Most nested descriptions read; deeper ones are dropped with a note.</summary>
    public const int MaxDepth = 16;   // the install's deepest tree has 11 levels; BOD's own nesting limit allows about 19

    public static FxSystem Read(string name, ReadOnlySpan<byte> bod) => Read(name, BodDocument.Read(bod));

    public static FxSystem Read(string name, BodDocument document)
    {
        if (document.Root is not BodStruct { Class.Text: "PSystem" } root)
            throw new InvalidDataException($"'{name}' is not a PSystem (root {(document.Root as BodStruct)?.Class.Text ?? document.Root.GetType().Name}).");
        var notes = new List<string>();
        var emitters = FxRead.Structs(root, "Emitters").Select(e =>
        {
            if (e.Class.Text != "FXEmitter") notes.Add($"emitter class '{e.Class.Text}' read as an emitter");
            return new FxEmitter(FxRead.Text(e, "Name"), FxRead.Bool(e, "Hide") ?? false, FxRead.Bool(e, "Screenspace") ?? false,
                new Vector2(FxRead.Float(e, "ScreenspaceXPosition") ?? 0f, FxRead.Float(e, "ScreenspaceYPosition") ?? 0f),
                FxRead.Float(e, "LODDistanceMedium") ?? 0f, FxRead.Float(e, "LODDistanceFar") ?? 0f, FxRead.Float(e, "SmoothMoveDistance") ?? 0f,
                FxRead.Text(e, "RefNode"), Descriptions(e, notes, 0));
        }).ToList();
        var bg = FxRead.Floats(root["BackgroundColor"]);
        return new FxSystem(FxRead.Text(root, "Name") is { Length: > 0 } own ? own : name, bg is [var r, var g, var b, ..] ? new Vector3(r, g, b) : Vector3.Zero,
            FxRead.Text(root, "RefObject"), FxRead.Text(root, "RefAnimation"), FxRead.Int(root, "SortOverride") ?? 0, FxRead.Int(root, "GlobalSortOverride") ?? 0,
            emitters, notes);
    }

    static IReadOnlyList<FxElement> Descriptions(BodStruct parent, List<string> notes, int depth, string field = "FXDescriptions")
    {
        var list = FxRead.Structs(parent, field);
        if (list.Count == 0) return [];
        if (depth >= MaxDepth) { notes.Add($"descriptions nested deeper than {MaxDepth}; {list.Count} dropped"); return []; }
        return list.Select(s => Element(s, notes, depth + 1)).ToList();
    }

    static FxElement Element(BodStruct s, List<string> notes, int depth)
    {
        var element = Typed(s, notes, depth);
        var hits = Descriptions(s, notes, depth, "FXCollisionDescriptions");
        return hits.Count == 0 ? element : element with { OnCollision = hits };
    }

    static FxElement Typed(BodStruct s, List<string> notes, int depth) => s.Class.Text switch
    {
        "FXRingDescription" => new FxRing(s),
        "FXSprayDescription" => new FxSpray(s, Descriptions(s, notes, depth)),
        "FXComboDescription" => new FxCombo(s, Descriptions(s, notes, depth)),
        "FXBeamDescription" => new FxBeam(s),
        "FXStreamerDescription" => new FxStreamer(s),
        "FXLightDescription" => new FxLight(s),
        "FXAudioDescription" => new FxAudio(s),
        "FXScreenSpaceDecalDescription" => new FxDecal(s),
        "FXMeshDescription" => new FxMesh(s),
        _ => Unknown(s, notes, depth),
    };

    static FxUnknownElement Unknown(BodStruct s, List<string> notes, int depth)
    {
        notes.Add($"description class '{s.Class.Text ?? $"#{s.Class.Hash:X16}"}' ('{FxRead.Text(s, "Name")}') kept untyped");
        return new FxUnknownElement(s, Descriptions(s, notes, depth));
    }
}

/// <summary>Field readers shared by the effect model: tolerant of missing fields and of ints where floats are expected.</summary>
static class FxRead
{
    public static float? Float(BodStruct s, string field) => Number(s[field]);
    public static int? Int(BodStruct s, string field) => s[field] switch { BodInt i => i.Value, BodFloat f when float.IsFinite(f.Value) => (int)f.Value, _ => null };
    public static bool? Bool(BodStruct s, string field) => s[field] switch { BodBool b => b.Value, BodInt i => i.Value != 0, _ => null };
    public static string Text(BodStruct s, string field) => s[field] is BodString str ? str.Text ?? "" : "";

    static float? Number(BodValue? v) => v switch { BodFloat f => f.Value, BodInt i => i.Value, BodUInt64 u => u.Value, BodRef r => Number(r.Value), _ => null };

    public static float[]? Floats(BodValue? v) => v is BodArray a && a.Items.All(i => Number(i) is not null) ? a.Items.Select(i => Number(i)!.Value).ToArray() : null;

    /// <summary>The structs a list field holds (references followed); other items are skipped.</summary>
    public static IReadOnlyList<BodStruct> Structs(BodStruct s, string field) => s[field] is BodList l
        ? l.Items.Select(i => i.Value is BodRef r ? r.Value : i.Value).OfType<BodStruct>().ToList()
        : [];

    static IEnumerable<BodArray> Keys(BodStruct s, string field) => s[field] is BodList l
        ? l.Items.Select(i => i.Value is BodRef r ? r.Value : i.Value).OfType<BodArray>()
        : [];

    public static FxCurve Curve(BodStruct s, string field)
    {
        var keys = Keys(s, field).Where(a => a.Items.Count >= 2 && Number(a.Items[0]) is not null && Number(a.Items[1]) is not null)
            .Select(a => new FxKey(Number(a.Items[0])!.Value, Number(a.Items[1])!.Value)).ToList();
        return keys.Count == 0 ? FxCurve.Empty : new FxCurve(keys);
    }

    public static FxColourCurve Colours(BodStruct s, string field)
    {
        var keys = new List<FxColourKey>();
        foreach (var a in Keys(s, field))
        {
            if (a.Items.Count < 2 || Number(a.Items[0]) is not float t || Floats(a.Items[1]) is not [var r, var g, var b, var alpha, ..]) continue;
            keys.Add(new FxColourKey(t, new FxColour(Byte(r), Byte(g), Byte(b), Byte(alpha))));
        }
        return keys.Count == 0 ? FxColourCurve.Empty : new FxColourCurve(keys);
        static byte Byte(float v) => (byte)Math.Clamp(float.IsFinite(v) ? MathF.Round(v) : 0f, 0f, 255f);
    }

    public static FxMaterial Material(BodStruct s)
    {
        var name = s["MaterialName"] as BodString;
        return new FxMaterial(name?.Text ?? "", name?.Hash ?? 0, Int(s, "AdditiveBlend") ?? 0, Int(s, "SoftParticle") ?? 0,
            Float(s, "TileX") ?? 1f, Float(s, "TileY") ?? 1f, Bool(s, "RandomTile") ?? false, Int(s, "UAddressMode") ?? 0);
    }

    public static IReadOnlyList<FxEnvironment> Environment(BodStruct s) => Structs(s, "Environment")
        .Select(e => new FxEnvironment(Float(e, "Gravity") ?? 0f, Float(e, "Wind") ?? 0f, Float(e, "Attraction") ?? 0f, Float(e, "Drag") ?? 0f,
            Float(e, "Bounce") ?? 0f, Int(e, "Position") ?? 0, Int(e, "Orientation") ?? 0, Int(e, "CollisionResponse") ?? 0)).ToList();
}
