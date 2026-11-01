using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmFormats.Fx;

namespace SmClient;

/// <summary>The power weapons that flash on a hit (fx/power-flash).</summary>
public enum PowerKind { Sword, Axe, Hammer, Maul }

/// <summary>
/// fx/power-flash: rules and the built cloud for the power weapons' hit flash (see <c>NetworkView.PowerFlash.cs</c>). Everything here is client presentation.
/// </summary>
public static class PowerFlash
{
    /// <summary>The hammer's own hit effects (original data), played for every power weapon at its scale.</summary>
    public const string MainEffect = "thunderhammer_dmg_body", SecondaryEffect = "thunderhammer_dmg_body_secondary";
    /// <summary>The sound events of the hammer's blast, posted on a hammer or maul hit besides the weapon's hit sound (data: the hammer's displace blast and its arcs).</summary>
    public static readonly string[] BlastEvents = ["play_sm_thunderhammer_displace", "play_sm_thunderhammer_displace_arcs"];
    public const string CloudName = "power_flash_cloud";
    /// <summary>A swing's hits within this many seconds share one budget: one big burst, then at most <see cref="SmallPerSwing"/> small ones (ours).</summary>
    public const double SwingSeconds = 0.6;
    public const int SmallPerSwing = 5;
    public const float SmallScale = 0.35f;
    /// <summary>How much bigger than the data's size the effects are played (ours, from the reference clip: the burst is bigger than a body).</summary>
    public const float DataBoost = 0.85f, CloudBoost = 1.15f;

    public static PowerKind? KindOf(string? wargear)
    {
        if (wargear is null) return null;
        if (wargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase)) return PowerKind.Hammer;
        if (wargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase)) return PowerKind.Maul;
        if (wargear.Contains("power_axe", StringComparison.OrdinalIgnoreCase)) return PowerKind.Axe;
        if (wargear.Contains("powersword", StringComparison.OrdinalIgnoreCase)) return PowerKind.Sword;
        return null;
    }

    /// <summary>Scale of the effects per weapon (ours, estimates: the hammer is the reference clip).</summary>
    public static float Scale(PowerKind k) => k switch { PowerKind.Hammer => 1f, PowerKind.Maul => 1f, PowerKind.Axe => 0.75f, _ => 0.6f };

    /// <summary>The daemon maul's recolour: a true purple (ours); the others keep the data's blue.</summary>
    public static Color? Tint(PowerKind k) => k == PowerKind.Maul ? new Color(0.80f, 0.10f, 1f) : null;

    static FxCurve C(params (float T, float V)[] keys) => new(keys.Select(k => new FxKey(k.T, k.V)).ToArray());
    static FxColourCurve Col(params (float T, byte R, byte G, byte B, byte A)[] keys) => new(keys.Select(k => new FxColourKey(k.T, new FxColour(k.R, k.G, k.B, k.A))).ToArray());

    /// <summary>
    /// Ours (provisional approximation of the clip): the layered soft burst from the package's own soft sprites: an overexposed white core, round lobes of billowing cloud that
    /// swell and then thin into a blue-violet haze, a light. Additive sprites only (the owner removed smoke sprites). Null without the sprites.
    /// </summary>
    public static CSystem? Build(IEnumerable<CSystem> systems)
    {
        var rings = systems.SelectMany(s => s.Roots.SelectMany(r => r.AllNodes())).OfType<CRing>().Where(r => !r.Material.IsEmpty).ToList();
        CRing? Find(string name) => rings.FirstOrDefault(r => r.Material.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var blur = Find("big_flash_blur") ?? Find("add_point_cool");
        var volume = Find("add_volume_soft_cool") ?? blur;
        var cloud = Find("cloud_highlights_additive_02") ?? volume;
        if (blur is null || volume is null || cloud is null) return null;
        var roots = new List<CNode>();
        CRing Ring(CRing src, float r0, float r1, float seconds, float ox, float oy, float oz, FxColourCurve colour) => new()
        {
            Kind = FxElementKind.Ring, Name = "lobe", Material = src.Material, Billboard = true, Duration = seconds, Life = seconds,
            Radius = C((0f, r0), (1f, r1)), Colour = colour,
            OffX = C((0f, ox)), OffY = C((0f, oy)), OffZ = C((0f, oz)),
        };
        var core = Col((0f, 255, 255, 255, 255), (0.35f, 235, 245, 255, 255), (1f, 140, 180, 255, 0));
        var lobe = Col((0f, 230, 242, 255, 0), (0.08f, 225, 238, 255, 255), (0.5f, 150, 185, 255, 215), (1f, 100, 90, 235, 0));
        var haze = Col((0f, 140, 160, 255, 0), (0.12f, 130, 150, 255, 170), (0.6f, 110, 105, 245, 110), (1f, 85, 70, 205, 0));
        roots.Add(Ring(blur, 2.6f, 5.5f, 0.55f, 0, 0, 0, core));
        roots.Add(Ring(cloud, 3.0f, 6.0f, 0.9f, 0, 0, 0, Col((0f, 235, 245, 255, 255), (0.5f, 170, 200, 255, 230), (1f, 80, 90, 235, 0))));
        // Round lobes of billowing cloud round the core: several big soft balls (the clip shows a ring of them), not a star; they hold, then thin.
        var rnd = new Random(7);
        for (int i = 0; i < 9; i++)
        {
            float ang = i / 9f * MathF.PI * 2f + (float)rnd.NextDouble() * 0.5f;
            float d = 1.4f + (float)rnd.NextDouble() * 1.3f;
            float r = 2.4f + (float)rnd.NextDouble() * 1.0f;
            roots.Add(Ring(i % 3 == 0 ? cloud : volume, r * 0.6f, r * 1.6f, 1.0f + (float)rnd.NextDouble() * 0.5f,
                MathF.Cos(ang) * d, (float)(rnd.NextDouble() - 0.4) * 1.4f, MathF.Sin(ang) * d, lobe));
        }
        // The decay: bigger, fainter blue-violet haze that thins over a second or two.
        for (int i = 0; i < 7; i++)
        {
            float ang = i / 7f * MathF.PI * 2f + 0.4f;
            float d = 1.2f + (float)rnd.NextDouble() * 1.6f;
            roots.Add(Ring(volume, 2.6f, 4.8f, 2.0f + (float)rnd.NextDouble() * 0.6f, MathF.Cos(ang) * d, (float)(rnd.NextDouble() - 0.3f) * 1.6f, MathF.Sin(ang) * d, haze));
        }
        var light = new CLight
        {
            Kind = FxElementKind.Light, Name = "flash light", Duration = 0.9f, Life = 0.9f, Colour = Col((0f, 150, 200, 255, 255)),
            Intensity = C((0f, 5f), (1f, 0f)), AttenEnd = C((0f, 14f)), Power = C((0f, 1f)),
        };
        roots.Add(light);
        return new CSystem { Name = CloudName, Roots = roots.ToArray(), Lifetime = 3f };
    }
}
