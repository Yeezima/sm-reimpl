using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmFormats.Fx;

namespace SmClient;

/// <summary>
/// pvp-hit-fx (owner, 2026-11-06, classic PvP, third reference image: a burst of red-orange fiery glow with dark grey smoke round the hit point): what a shot shows on a player whose armour is
/// already gone. Replaces the round-3 red body re-flash and the red droplet streaks (<see cref="BloodSplash"/>) for those hits; the data's own effects (the weapon's <c>broken_ceramite</c> container,
/// e.g. <c>bolter_dmg_body_tough_mp</c>, and the blueprint's <c>sm_armor_dmg_bodyglow_red</c>) still play underneath, unchanged. Built from the install's own soft sprites (the warm bloom
/// <c>add_point_warm</c>, the fire flash <c>aa_burst_00_tp</c>, the plasma's <c>billowing_fire_tp_00</c>, the smoke <c>smoke_billowing</c> / <c>lingering_smoke_cloud_03_tp</c>), as
/// <see cref="Variants"/> seeded variants that differ in the number, place, size, timing and colour of their lobes and puffs, so no two hits look alike (owner: "randomized in shape and
/// appearance"). Layout, sizes, colours and times are **ours** (provisional approximation of the reference, refine register). The smoke here is drawn although <c>--smoke</c> is off
/// (the owner asked for it in this reference).
/// </summary>
public static class HealthHitBurst
{
    public const int Variants = 8;
    public const string Prefix = "health_hit_burst_";
    /// <summary>The burst's size at scale 1 (ours: the reference's burst is about a marine's torso wide, a little more with the smoke).</summary>
    public const float Size = 1.3f;

    public static string Name(int variant) => Prefix + ((variant % Variants + Variants) % Variants).ToString(System.Globalization.CultureInfo.InvariantCulture);

    static FxCurve C(params (float T, float V)[] keys) => new(keys.Select(k => new FxKey(k.T, k.V)).ToArray());
    static FxColourCurve Col(params (float T, int R, int G, int B, int A)[] keys) =>
        new(keys.Select(k => new FxColourKey(k.T, new FxColour((byte)k.R, (byte)k.G, (byte)k.B, (byte)k.A))).ToArray());

    /// <summary>The variants, or none when the sprites are not in the loaded effects (then the old look stays).</summary>
    public static IReadOnlyList<CSystem> Build(IEnumerable<CSystem> systems)
    {
        var rings = systems.SelectMany(s => s.Roots.SelectMany(r => r.AllNodes())).OfType<CSprite>().Where(r => !r.Material.IsEmpty).ToList();
        CSprite? Find(params string[] names)
        {
            foreach (var name in names)
                if (rings.FirstOrDefault(r => r.Material.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
            return null;
        }
        var bloom = Find("add_point_warm", "add_point");
        var fire = Find("aa_burst_00_tp", "billowing_fire_tp_00");
        var fire2 = Find("billowing_fire_tp_00", "aa_burst_00_tp");
        var smoke = Find("smoke_billowing", "lingering_smoke_cloud_03_tp");
        var smoke2 = Find("lingering_smoke_cloud_03_tp", "smoke_billowing");
        if (bloom is null || fire is null || fire2 is null || smoke is null || smoke2 is null) return [];
        var list = new List<CSystem>(Variants);
        for (int v = 0; v < Variants; v++) list.Add(Variant(v, bloom, fire, fire2, smoke, smoke2));
        return list;
    }

    static CSystem Variant(int v, CSprite bloom, CSprite fire, CSprite fire2, CSprite smoke, CSprite smoke2)
    {
        var rnd = new Random(9173 + v * 7919);
        float F(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
        int I(int a, int b) => rnd.Next(a, b + 1);
        var roots = new List<CNode>();
        CRing Ring(CSprite src, float r0, float r1, float seconds, Vector3 off, Vector3 drift, FxColourCurve colour, float spin = 0f) => new()
        {
            Kind = FxElementKind.Ring, Name = "burst", Material = src.Material, Billboard = true, Duration = seconds, Life = seconds,
            Radius = C((0f, r0), (0.35f, r0 + (r1 - r0) * 0.7f), (1f, r1)), Colour = colour,
            OffX = C((0f, off.X), (1f, off.X + drift.X)), OffY = C((0f, off.Y), (1f, off.Y + drift.Y)), OffZ = C((0f, off.Z), (1f, off.Z + drift.Z)),
            SpinZ = spin == 0f ? FxCurve.Empty : C((0f, 0f), (1f, spin)), RandomSpinDirection = true,
        };
        Vector3 Dir(float spreadUp)
        {
            // the effect's axis (Y) is back toward the shooter: lobes sit round the hit point, a little toward the shooter
            float a = F(0f, MathF.Tau), r = F(0.15f, 1f);
            return new Vector3(MathF.Cos(a) * r, F(0.05f, 0.45f) + spreadUp, MathF.Sin(a) * r);
        }
        // 1. the hot core: a short white-orange bloom, then a red one that lingers a little
        roots.Add(Ring(bloom, F(0.35f, 0.5f), F(0.7f, 0.95f), F(0.1f, 0.16f), Vector3.Zero, Vector3.Zero, Col((0f, 255, 250, 235, 255), (0.4f, 255, 205, 130, 220), (1f, 255, 90, 30, 0))));
        roots.Add(Ring(fire, F(0.18f, 0.26f), F(0.32f, 0.42f), F(0.1f, 0.15f), Vector3.Zero, Vector3.Zero, Col((0f, 255, 245, 215, 255), (0.5f, 255, 200, 120, 200), (1f, 255, 120, 40, 0)), F(-90f, 90f)));
        roots.Add(Ring(bloom, F(0.8f, 1.0f), F(1.3f, 1.7f), F(0.28f, 0.4f), Vector3.Zero, Vector3.Zero, Col((0f, 255, 70, 30, 0), (0.12f, 255, 60, 24, 190), (0.6f, 200, 30, 12, 90), (1f, 120, 10, 5, 0))));
        // 2. the fiery lobes: red-orange, swelling and darkening to deep red
        int lobes = I(4, 7);
        for (int i = 0; i < lobes; i++)
        {
            var d = Dir(0f) * F(0.12f, 0.38f);
            float r = F(0.22f, 0.42f), life = F(0.22f, 0.42f), delay = F(0f, 0.05f);
            int g = I(60, 140);
            var colour = Col((0f, 255, 220, 160, 0), (0.06f + delay, 255, g, 30, I(170, 215)), (0.45f, 225, g / 3, 12, I(110, 150)), (1f, 90, 8, 4, 0));
            roots.Add(Ring(i % 2 == 0 ? fire : fire2, r, r * F(1.5f, 2.1f), life, d, d * F(0.4f, 0.9f), colour, F(-60f, 60f)));
        }
        // 3. the dark smoke and blood mist: grey-brown puffs that rise, swell and thin (alpha-blended)
        int puffs = I(3, 5);
        for (int i = 0; i < puffs; i++)
        {
            var d = Dir(0.1f) * F(0.1f, 0.35f);
            float r = F(0.25f, 0.45f), life = F(0.8f, 1.4f);
            int grey = I(14, 30), red = grey + I(2, 14);
            var colour = Col((0f, red, grey, grey, 0), (0.08f, red, grey, grey, I(170, 215)), (0.5f, red, grey, grey, I(110, 150)), (1f, grey, grey, grey, 0));
            roots.Add(Ring(i % 2 == 0 ? smoke : smoke2, r, r * F(2.2f, 3.2f), life, d, d * F(0.6f, 1.2f) + new Vector3(0f, F(0.05f, 0.2f), 0f), colour, F(-40f, 40f)));
        }
        return new CSystem { Name = Name(v), Roots = roots.ToArray(), Lifetime = roots.Max(n => n.Life) };
    }
}
