using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmFormats.Fx;

namespace SmClient;

/// <summary>
/// M25b: an effect system read once into plain fields (the reader's typed properties re-parse the BOD tree on every access, too slow for
/// a per-frame player). Nothing here is interpreted beyond reading the typed values; the meaning lives in <see cref="FxPlayer"/>.
/// </summary>
public abstract class CNode
{
    public required FxElementKind Kind { get; init; }
    public string Name = "";
    public float Duration, Epsilon;
    public bool Looping, Hide;
    public CNode[] Children = [];
    /// <summary>What starts where this node's particle touches the level (<see cref="SmFormats.Fx.FxElement.OnCollision"/>; M41); played only by effects that run collisions (<see cref="FxEffect.Collisions"/>).</summary>
    public CNode[] OnCollision = [];
    /// <summary>The longest time any descendant (or this node) shows, seconds (<see cref="FxSystem.Lifetime"/>'s rule).</summary>
    public float Life;
    /// <summary>fx/no-smoke: a sprite the data shows as smoke, dust, mist or haze (<see cref="SmContent.SmokeRules"/>); not drawn unless <c>--smoke on</c>.</summary>
    public bool Smoke;
    /// <summary>fx/no-smoke: this node draws nothing but smoke (a smoke sprite, or a combo or spray whose every child and collision description is smoke only); skipped whole unless <c>--smoke on</c>.</summary>
    public bool SmokeOnly;
    /// <summary>This node and every node under it, the collision descriptions included.</summary>
    public IEnumerable<CNode> AllNodes()
    {
        yield return this;
        foreach (var c in Children.Concat(OnCollision)) foreach (var d in c.AllNodes()) yield return d;
    }
}

public sealed class CCombo : CNode
{
    public bool OneAtRandom;
    public FxCurve Scale = FxCurve.Empty, OffX = FxCurve.Empty, OffY = FxCurve.Empty, OffZ = FxCurve.Empty;
    public (float X, float Y, float Z) StartAngle;
    public (bool X, bool Y, bool Z) UseStart;
}

public sealed class CSpray : CNode
{
    public (int N, float T)[] Bursts = [];
    public FxCurve Rate = FxCurve.Empty, Deviation = FxCurve.Empty, Inner = FxCurve.Empty, VolX = FxCurve.Empty, VolY = FxCurve.Empty, VolZ = FxCurve.Empty;
    public FxCurve OffX = FxCurve.Empty, OffY = FxCurve.Empty, OffZ = FxCurve.Empty, Speed = FxCurve.Empty, ParticleScale = FxCurve.Empty;
    public bool FromPoint, ScaleSpeed;
    public float Gravity, Drag;
    /// <summary>A "dropper" (M41): the environment has a collision response and a child holds collision descriptions. With collisions on, each particle touches the level where it starts (the effect is placed on the ground) and starts those.</summary>
    public bool Collides;
    /// <summary>Particle spawn times in seconds from the spray's start (bursts, then rate emission), at most <see cref="FxPlayer.MaxParticles"/>.</summary>
    public float[] Spawns = [];
    public float ChildLife;
}

public class CSprite : CNode
{
    public FxMaterial Material = new("", 0, 0, 0, 1, 1, false, 0);
    public bool Billboard;
    public int Cells => Math.Max(1, (int)MathF.Round(Material.TileX)) * Math.Max(1, (int)MathF.Round(Material.TileY));
    public bool Additive => Material.AdditiveBlend == 1;
    public bool Distortion => Material.AdditiveBlend == 2;
    public FxColourCurve Colour = FxColourCurve.Empty;
    public FxCurve OffX = FxCurve.Empty, OffY = FxCurve.Empty, OffZ = FxCurve.Empty, Anim = FxCurve.Empty;
}

public sealed class CRing : CSprite
{
    public FxCurve Radius = FxCurve.Empty, SpinX = FxCurve.Empty, SpinY = FxCurve.Empty, SpinZ = FxCurve.Empty;
    public float EpsRadius;
    public (float X, float Y, float Z) StartAngle;
    public (bool X, bool Y, bool Z) UseStart;
    public bool RandomSpinDirection;
}

public sealed class CBeam : CSprite
{
    public FxCurve Length = FxCurve.Empty, Width = FxCurve.Empty, WidthOverLength = FxCurve.Empty, UOffset = FxCurve.Empty, URepeat = FxCurve.Empty;
    public float EpsLength, EpsWidth, MeterPerU, EpsMeterPerU, USpeed;
    public bool GodRayFade;
    public float FadeAngle;
}

public sealed class CStreamer : CSprite
{
    public FxCurve Length = FxCurve.Empty, Width = FxCurve.Empty;
    public bool LengthAsSeconds;
    public float MeterPerU;
    public FxColourCurve MasterColour = FxColourCurve.Empty;
}

public sealed class CLight : CNode
{
    public FxColourCurve Colour = FxColourCurve.Empty;
    public FxCurve Intensity = FxCurve.Empty, AttenEnd = FxCurve.Empty, Power = FxCurve.Empty, OffX = FxCurve.Empty, OffY = FxCurve.Empty, OffZ = FxCurve.Empty;
}

public sealed class CDecal : CNode
{
    public string Diffuse = "", Normal = "";
    public float Width, Height, Depth, EpsScale, Gloss;
    /// <summary>The sprite sheet's cells (<c>TileU</c> x <c>TileV</c>); a sheet of more than one cell is played as an animation over the decal's duration (M38).</summary>
    public int TileU = 1, TileV = 1;
    public FxCurve Scale = FxCurve.Empty;
    public FxColourCurve Colour = FxColourCurve.Empty;
    public bool Additive, RandomSpinDirection;
}

public sealed class CMesh : CNode
{
    public string Mesh = "";
    public FxCurve Scale = FxCurve.Empty;
}

public sealed class CPlain : CNode { }

/// <summary>A compiled <see cref="FxSystem"/>.</summary>
public sealed class CSystem
{
    public required string Name { get; init; }
    public required CNode[] Roots { get; init; }
    /// <summary>The system shows this long unless it loops (null).</summary>
    public float? Lifetime { get; init; }
    public bool HasStreamer { get; init; }
    public bool HasScreenspace { get; init; }
    /// <summary>How long what the collision descriptions start shows, decals not counted (<see cref="FxSystem.CollisionLife"/>); an effect that runs collisions lives at least this long.</summary>
    public float CollisionLife { get; init; }

    /// <summary>
    /// Compiles a system. <paramref name="zeroDuration"/> (F-D1, **interpretation**; 0: off, the default): the seconds an element with <c>Duration</c> 0 lasts. The death effects
    /// (<c>plasma_limb_vanish</c>, <c>jumppack_dmg_burn_limb</c>, <c>jumppack_death_mp</c>, <c>jumppack_predeath</c>) have no element with a duration at all, yet their <c>play_fx</c> names a
    /// <c>length</c> (1 s, 0.6 s): the length they are played over, so a duration of 0 reads as "the length of the play". The system's lifetime is then recomputed from the elements.
    /// Only a system in which <b>every</b> element has a duration of 0 is read so (the effects that other hits and weapons share keep their own timing).
    /// </summary>
    public static CSystem From(FxSystem sys, float zeroDuration = 0f)
    {
        zeroDuration = SmContent.FxDurations.LengthDriven(sys, zeroDuration);   // engine-free rule, tested: only a system whose elements all have a duration of 0
        var roots = sys.Emitters.Where(e => !e.Hide && !e.Screenspace).SelectMany(e => e.Elements).Select(e => Compile(e, zeroDuration)).ToArray();
        float? lifetime = SmContent.FxDurations.Lifetime(sys, zeroDuration);
        return new CSystem
        {
            Name = sys.Name, Roots = roots, Lifetime = lifetime,
            HasStreamer = sys.Elements.Any(e => e is FxStreamer), HasScreenspace = sys.Emitters.Any(e => e.Screenspace), CollisionLife = sys.CollisionLife,
        };
    }

    static CNode Compile(FxElement el, float zeroDuration)
    {
        var kids = el.Children.Select(k => Compile(k, zeroDuration)).ToArray();
        var hits = el.OnCollision.Select(k => Compile(k, zeroDuration)).ToArray();
        float duration = SmContent.FxDurations.DurationOf(el, zeroDuration);
        CNode node;
        switch (el)
        {
            case FxCombo c:
                node = new CCombo
                {
                    Kind = FxElementKind.Combo, OneAtRandom = c.OneAtRandom, Scale = c.Scale, OffX = c.OffsetX, OffY = c.OffsetY, OffZ = c.OffsetZ,
                    StartAngle = (c.StartAngle.X, c.StartAngle.Y, c.StartAngle.Z),
                    UseStart = (c.Bool("UseStartAngleX") ?? true, c.Bool("UseStartAngleY") ?? true, c.Bool("UseStartAngleZ") ?? true),
                };
                break;
            case FxSpray s:
            {
                var env = s.Environment;
                node = new CSpray
                {
                    Kind = FxElementKind.Spray, Bursts = s.Bursts.Select(b => (b.NumParticles, b.BurstTime)).ToArray(),
                    Rate = s.Rate, Deviation = s.Deviation, Inner = s.InnerDeviation, VolX = s.VolumeX, VolY = s.VolumeY, VolZ = s.VolumeZ,
                    OffX = s.OffsetX, OffY = s.OffsetY, OffZ = s.OffsetZ, Speed = s.InitialSpeed, ParticleScale = s.ParticleScale,
                    FromPoint = s.EmitFromPoint, ScaleSpeed = s.ScaleSpeed, Gravity = env.Count > 0 ? env[0].Gravity : 0f, Drag = env.Count > 0 ? env[0].Drag : 0f,
                    Collides = s.IsDropper,
                };
                break;
            }
            case FxRing r:
                node = new CRing
                {
                    Kind = FxElementKind.Ring, Material = r.Material, Billboard = r.Billboard, Colour = r.Colour, OffX = r.OffsetX, OffY = r.OffsetY, OffZ = r.OffsetZ,
                    Anim = r.AnimationRate, Radius = r.Radius, SpinX = r.SpinX, SpinY = r.SpinY, SpinZ = r.SpinZ, EpsRadius = r.EpsilonRadius,
                    StartAngle = (r.StartAngle.X, r.StartAngle.Y, r.StartAngle.Z), UseStart = r.UseStartAngle, RandomSpinDirection = r.RandomSpinDirection,
                };
                break;
            case FxBeam b:
                node = new CBeam
                {
                    Kind = FxElementKind.Beam, Material = b.Material, Billboard = b.Billboard, Colour = b.Colour, OffX = b.OffsetX, OffY = b.OffsetY, OffZ = b.OffsetZ,
                    Anim = b.AnimationRate, Length = b.Length, Width = b.Width, WidthOverLength = b.WidthOverLength, UOffset = b.TextureUOffset, URepeat = b.TextureURepeat,
                    EpsLength = b.EpsilonLength, EpsWidth = b.EpsilonWidth, MeterPerU = b.MeterPerU, EpsMeterPerU = b.EpsilonMeterPerU, USpeed = b.USpeed,
                    GodRayFade = b.GodRayFade, FadeAngle = b.FadeAngleCutoff,
                };
                break;
            case FxStreamer st:
                node = new CStreamer
                {
                    Kind = FxElementKind.Streamer, Material = st.Material, Colour = st.Colour, MasterColour = st.MasterColour, Length = st.Length, Width = st.Width,
                    LengthAsSeconds = st.LengthAsSeconds, MeterPerU = st.MeterPerU,
                };
                break;
            case FxLight l:
                node = new CLight
                {
                    Kind = FxElementKind.Light, Colour = l.Colour, Intensity = l.Intensity, AttenEnd = l.AttenEnd, Power = l.Power, OffX = l.OffsetX, OffY = l.OffsetY, OffZ = l.OffsetZ,
                };
                break;
            case FxDecal d:
                node = new CDecal
                {
                    Kind = FxElementKind.Decal, Diffuse = d.DiffuseMap, Normal = d.NormalMap, Gloss = d.Gloss, TileU = Math.Max(1, (int)MathF.Round(d.TileU)), TileV = Math.Max(1, (int)MathF.Round(d.TileV)), Width = d.Width, Height = d.Height, Depth = d.Depth, EpsScale = d.EpsilonScale, Scale = d.Scale,
                    Colour = d.Colour, Additive = d.AdditiveBlend, RandomSpinDirection = d.RandomSpinDirection,
                };
                break;
            case FxMesh m:
                node = new CMesh { Kind = FxElementKind.Mesh, Mesh = m.Mesh, Scale = m.Scale };
                break;
            default:
                node = new CPlain { Kind = el.Kind };
                break;
        }
        node.Name = el.Name; node.Duration = duration; node.Epsilon = el.DurationEpsilon; node.Looping = el.IsLooping; node.Hide = el.Hide; node.Children = kids; node.OnCollision = hits;
        node.Life = duration + MathF.Max(0f, el.DurationEpsilon) + (kids.Length > 0 || hits.Length > 0 ? kids.Concat(hits).Max(k => k.Life) : 0f);
        // fx/no-smoke (the owner's change): the engine-free rule on the element's own data; a container holding only smoke is skipped whole.
        node.Smoke = SmokeRules.Classify(el) == SmokeClass.Smoke;
        node.SmokeOnly = SmokeRules.OnlySmoke(node.Smoke, node is CCombo or CSpray, kids.Concat(hits).Select(k => k.SmokeOnly).ToList());
        if (node is CSpray spray) Plan(spray);
        return node;
    }

    /// <summary>Fills a spray's spawn times (**interpretation**: burst times are fractions of the spray's duration when at most 1; the emission rate is particles per unit of normalised time, so a rate of 10 over a 0.05 s spray emits ten particles in that time).</summary>
    static void Plan(CSpray s)
    {
        float duration = MathF.Max(s.Duration, 0.001f);
        var times = new List<float>();
        foreach (var (n, t) in s.Bursts)
        {
            float at = t <= 1f ? t * duration : t;
            for (int i = 0; i < Math.Min(n, FxPlayer.MaxParticles); i++) times.Add(at);
        }
        if (!s.Rate.IsEmpty && s.Rate.Max > 0f)
        {
            const int steps = 64;
            float acc = 0f;
            for (int i = 0; i < steps && times.Count < FxPlayer.MaxParticles; i++)
            {
                float t = (i + 0.5f) / steps;
                acc += MathF.Max(0f, s.Rate.Evaluate(t)) / steps;
                while (acc >= 1f && times.Count < FxPlayer.MaxParticles) { times.Add(t * duration); acc -= 1f; }
            }
        }
        times.Sort();
        s.Spawns = times.Take(FxPlayer.MaxParticles).ToArray();
        s.ChildLife = s.Children.Length > 0 ? s.Children.Max(k => k.Life) : 0f;
    }
}
