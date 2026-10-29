using SmFormats.Fx;

namespace SmContent;

/// <summary>What a sprite element of an effect is, for the owner's "no smoke" look (<see cref="SmokeRules"/>).</summary>
public enum SmokeClass
{
    /// <summary>Not a sprite (a combo, spray, light, decal, mesh, sound): never removed by the rule.</summary>
    NotSprite,
    /// <summary>Kept: an additive sprite (flash, fire, sparks, glow, tracer streak, god ray) or an alpha-blended debris, blood or solid sprite.</summary>
    Keep,
    /// <summary>Smoke, dust, mist or haze: removed unless <c>--smoke on</c>.</summary>
    Smoke,
}

/// <summary>What the rule looks at on a sprite element (ring, beam, streamer): all read from the effect data.</summary>
/// <param name="Material">The atlas sprite's material name (<c>MaterialName</c>).</param>
/// <param name="Blend">The material's <c>AdditiveBlend</c>: 0 alpha, 1 additive, 2 distortion.</param>
/// <param name="Name">The element's own name (<c>Name</c>).</param>
/// <param name="GodRay">A beam with the data's <c>GodRayFade</c> (the map's light shafts).</param>
/// <param name="SizeStart">Radius (ring) or width (beam, streamer) at the start of its curve, metres.</param>
/// <param name="SizeEnd">The same at the end of its curve.</param>
/// <param name="AlphaStart">The colour curve's alpha at its start (1 for no curve).</param>
/// <param name="AlphaEnd">The colour curve's alpha at its end (1 for no curve).</param>
/// <param name="Duration">The element's duration, seconds.</param>
public readonly record struct SmokeTraits(string Material, int Blend, string Name, bool GodRay, float SizeStart, float SizeEnd, float AlphaStart, float AlphaEnd, float Duration);

/// <summary>
/// fx/no-smoke (the owner, 2026-10-29, a deliberate change from the original: "remove basically all the 'smoke' or 'dust' we currently have as projectile impact and all effects. we
/// should only spray a few pieces of 'debris' out of the impact and leave a decal on the wall"). Classifies every sprite element of the original effects as smoke (removed by default)
/// or kept, from the effect data alone; engine-free and tested (<c>SmokeRulesTests</c>). The rule, in order (**ours**, an interpretation of what each sprite shows):
/// <list type="number">
/// <item>A god-ray beam (<c>GodRayFade</c>) is kept: light, not smoke.</item>
/// <item>An additive or distortion sprite is kept: it adds light and cannot hide what is behind it (flashes, fireballs, sparks, glows, tracer streaks). The data's additive
/// "smoke" textures (<c>smoke_hvy_01_add_tp</c>, <c>smoke_puff_chaos</c>) are used as fire, "light cracks" and glows, so blend wins over the texture's name.</item>
/// <item>An alpha-blended sprite whose texture is debris, blood or a solid object (<see cref="KeepTextures"/>: fragments, chunks, specks, pebbles, sparks, blood, glass) is kept.</item>
/// <item>An alpha-blended sprite whose texture is a smoke or dust card (<see cref="SmokeTextures"/>: smoke, dust, puffs, clouds, the soft and hard "volume" blobs, the
/// <c>alpha_trail</c> ribbons, the <c>alpha_spike</c> dirt and smoke bursts, the <c>shockwave</c> ground dust rings) is smoke.</item>
/// <item>Any other alpha-blended sprite whose element is named as smoke (<see cref="SmokeNames"/>) is smoke.</item>
/// <item>Any other alpha-blended sprite that grows (<see cref="GrowthAtLeast"/> x) to at least <see cref="SizeAtLeast"/> m while fading out (its alpha falls to
/// <see cref="FadeTo"/> of its start or less) over at least <see cref="DurationAtLeast"/> s is smoke: the shape of a puff (the bolter's <c>bolter_ignition_beam</c> trail).</item>
/// <item>Everything else is kept.</item>
/// </list>
/// </summary>
public static class SmokeRules
{
    /// <summary>Texture words of alpha sprites that are debris, blood or solid things (kept). Checked before <see cref="SmokeTextures"/>.</summary>
    public static readonly string[] KeepTextures = ["fragment", "chunk", "debris", "speck", "specs", "pebble", "bolts", "paper", "shard", "glass", "blood", "meat", "gib", "spark", "rocket", "ship", "turbine", "bugs", "shadow", "light_", "laser", "sky_beam", "ui_"];

    /// <summary>Texture words of alpha sprites that are smoke, dust, mist or haze cards (removed).</summary>
    public static readonly string[] SmokeTextures = ["smoke", "dust", "puff", "cloud", "volume", "haze", "mist", "fog", "steam", "vapo", "alpha_trail", "alpha_spike", "shockwave", "waterfall_plume"];

    /// <summary>Element-name words that say smoke (for alpha sprites whose texture says neither).</summary>
    public static readonly string[] SmokeNames = ["smoke", "dust", "puff", "cloud", "haze", "mist", "fog", "steam", "vapo", "linger", "wispy", "soot"];

    /// <summary>The puff shape (rule 6; ours): grows at least this many times, to at least this size (m), fading to at most this share of its start alpha, over at least this long (s).</summary>
    public const float GrowthAtLeast = 1.25f, SizeAtLeast = 0.25f, FadeTo = 0.5f, DurationAtLeast = 0.2f;

    /// <summary>What a sprite with these traits is (see the class remarks).</summary>
    public static SmokeClass Classify(in SmokeTraits t) => Explain(t).Class;

    /// <summary><see cref="Classify"/> with the rule that decided it (1 to 7 as in the class remarks; for <c>smtool fx-smoke</c> and the pins).</summary>
    public static (SmokeClass Class, int Rule) Explain(in SmokeTraits t)
    {
        if (t.GodRay) return (SmokeClass.Keep, 1);
        if (t.Blend != 0) return (SmokeClass.Keep, 2);
        string texture = t.Material.ToLowerInvariant();
        if (Has(texture, KeepTextures)) return (SmokeClass.Keep, 3);
        if (Has(texture, SmokeTextures)) return (SmokeClass.Smoke, 4);
        if (Has(t.Name.ToLowerInvariant(), SmokeNames)) return (SmokeClass.Smoke, 5);
        return IsPuffShaped(t) ? (SmokeClass.Smoke, 6) : (SmokeClass.Keep, 7);
    }

    /// <summary>Rule 6: grows, fades out, lasts (see <see cref="GrowthAtLeast"/>).</summary>
    public static bool IsPuffShaped(in SmokeTraits t)
    {
        float start = MathF.Max(0f, t.SizeStart), end = MathF.Max(0f, t.SizeEnd);
        bool grows = end >= SizeAtLeast && (start <= 0f || end >= start * GrowthAtLeast);
        bool fades = t.AlphaEnd <= t.AlphaStart * FadeTo;
        return grows && fades && t.Duration >= DurationAtLeast;
    }

    /// <summary>The traits of a sprite element (ring, beam, streamer); null for any other element.</summary>
    public static SmokeTraits? Traits(FxElement e) => e switch
    {
        FxRing r => Make(r.Material, r.Name, false, r.Radius, r.Colour, r.Duration),
        FxBeam b => Make(b.Material, b.Name, b.GodRayFade, b.Width, b.Colour, b.Duration),
        FxStreamer s => Make(s.Material, s.Name, false, s.Width, s.Colour, s.Duration),
        _ => null,
    };

    /// <summary><see cref="Classify"/> for an element: <see cref="SmokeClass.NotSprite"/> for anything but a ring, beam or streamer.</summary>
    public static SmokeClass Classify(FxElement e) => Traits(e) is { } t ? Classify(t) : SmokeClass.NotSprite;

    static SmokeTraits Make(FxMaterial m, string name, bool godRay, FxCurve size, FxColourCurve colour, float duration)
    {
        float s0 = size.IsEmpty ? 0f : size.Evaluate(0f), s1 = size.IsEmpty ? 0f : size.Evaluate(1f);
        float a0 = colour.IsEmpty ? 1f : colour.Evaluate(0f).W, a1 = colour.IsEmpty ? 1f : colour.Evaluate(1f).W;
        return new SmokeTraits(m.Name, m.AdditiveBlend, name, godRay, s0, s1, a0, a1, duration);
    }

    /// <summary>
    /// Whether a node draws nothing but smoke and can be skipped whole: a smoke sprite whose children and collision descriptions (if any) draw only smoke, or a combo or spray
    /// (<paramref name="container"/>) with at least one child or collision description, all of which draw only smoke (<paramref name="parts"/>). A light, decal, mesh or sound, a kept
    /// sprite, an empty container and a smoke sprite holding kept content (a dropper's collider that starts a decal or debris) are not: that content is still played.
    /// </summary>
    public static bool OnlySmoke(bool smoke, bool container, IReadOnlyCollection<bool> parts) =>
        (smoke || (container && parts.Count > 0)) && parts.All(p => p);

    /// <summary><see cref="OnlySmoke(bool, bool, IReadOnlyCollection{bool})"/> over an element's tree (children and collision descriptions).</summary>
    public static bool OnlySmoke(FxElement e) =>
        OnlySmoke(Classify(e) == SmokeClass.Smoke, e is FxCombo or FxSpray, e.Children.Concat(e.OnCollision).Select(OnlySmoke).ToList());

    /// <summary>The client switch: <c>--smoke on</c> or <c>--smoke=on</c> (also <c>true</c>, <c>1</c>) draws the smoke (the data's look, for before / after); anything else, or no flag: off (the default).</summary>
    public static bool SwitchOn(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string? value = args[i] == "--smoke" ? (i + 1 < args.Count ? args[i + 1] : null)
                : args[i].StartsWith("--smoke=", StringComparison.Ordinal) ? args[i]["--smoke=".Length..] : null;
            if (value is not null) return value.ToLowerInvariant() is "on" or "true" or "1";
        }
        return false;
    }

    static bool Has(string text, string[] words)
    {
        foreach (var w in words) if (text.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }
}
