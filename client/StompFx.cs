using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmFormats.Fx;

namespace SmClient;

/// <summary>
/// fx/impact-pass (the owner, 2026-10-29: "make a pass where you add effects such as ground breaking/shockwaves/etc. to give the stomps impact and weight"): a stomp's or slam's impact
/// on the ground, shared by the networked view (<see cref="NetworkView"/>, NetworkView.MeleeFx.cs) and the effects test view (<c>--stomp-test</c>). It plays the data's effect for the
/// blast (<see cref="MeleeEffects"/>: <c>interrupt_stomp_dmg</c>, <c>thunderhammer_dmg_displace</c> ...; their smoke and dust stay off) at the ground point, and **ours**, sized to the
/// blast's radius as the M41 pound is: a ground shockwave (<see cref="Build"/>: a flat additive ring of the data's own ring sprite growing to the radius, and a short flash light),
/// the data's crack decal again at <see cref="CrackShare"/> of the blast's diameter, the thin dark ring at the radius (<see cref="PoundRing"/>) and ground chunks
/// (<see cref="GroundShards"/>, <see cref="ChunksPerMetre"/>). No dust or smoke (the owner removed them) and no camera shake (another owner's work).
/// </summary>
public static class StompFx
{
    /// <summary>Our shockwave system's name (built when the melee effects load).</summary>
    public const string ShockwaveName = "impact_pass_stomp_shockwave";
    /// <summary>The shockwave's run (s) and its peak opacity (ours).</summary>
    public const float ShockwaveSeconds = 0.35f, ShockwaveAlpha = 1f;
    /// <summary>The share of the blast's diameter the extra crack decal covers, and how long it lasts (s); ours (the data's own crack is 7 m on any stomp).</summary>
    public const float CrackShare = 0.85f, CrackSeconds = 10f;
    /// <summary>Chunks thrown per metre of blast radius (min, max) and their speed share (ours: a radius 7 stomp throws 14 to 21 at 0.7 of the pound's speed; the pound 28 to 45).</summary>
    public static readonly (float Min, float Max) ChunksPerMetre = (2f, 3f);
    public const float ChunkSpeed = 0.7f;
    /// <summary>The ring sprites tried for the shockwave, in order (all the data's: the hammer slam's outer rings, the Furious Stomp's far ring, the stomp's spinning ring).</summary>
    public static readonly string[] RingSprites = ["rings_outer", "plasma_ring", "turbine_blades", "emp_ball_main"];

    /// <summary>
    /// Plays the impact at <paramref name="point"/> (Godot space) on a surface facing <paramref name="normal"/>. <paramref name="near"/> false (far from the camera) plays the effects only.
    /// Returns the effects started.
    /// </summary>
    public static int Play(FxPlayer fx, IReadOnlyList<FxPlay> plays, Vector3 point, Vector3 normal, float radius, GroundShards? shards, string surface, int seed, bool near)
    {
        int started = GroundPoundFx.Play(fx, plays, point, normal, 0f, null, surface, 0).Count;
        var up = normal.LengthSquared() < 1e-6f ? Vector3.Up : normal.Normalized();
        var axes = AxesFromY(up);
        if (radius <= 0f) return started;
        if (fx.Play(ShockwaveName, point + up * 0.06f, axes, radius, null, null, 0f, 0.0, false, GroundPoundFx.Priority) is not null) started++;
        if (!near) return started;
        PoundRing.Request(radius);
        if (PoundRing.Texture(radius) is { } ring)
        {
            float size = PoundRing.SizeFor(radius);
            fx.PlaceTextureDecal(ring, point, axes, size, size, 2.0f, GroundPoundFx.RingSeconds, PoundRing.Fade);
        }
        if (CrackDecal(fx, plays) is { } crack)
        {
            float d = 2f * radius * CrackShare;
            fx.PlaceDataDecal(crack, point, axes * new Basis(Vector3.Up, Mathf.DegToRad(37f + seed * 53 % 360)), d, d, 2.0f, CrackSeconds, PoundRing.Fade);
        }
        shards?.Spawn(point, up, surface, seed, ((int)MathF.Round(radius * ChunksPerMetre.Min), (int)MathF.Round(radius * ChunksPerMetre.Max)), ChunkSpeed);
        return started;
    }

    /// <summary>The crack decal the data's effects hold (<c>decal_standard_cracked_dif</c>), or null.</summary>
    static string? CrackDecal(FxPlayer fx, IReadOnlyList<FxPlay> plays)
    {
        foreach (var p in plays)
            if (fx.System(p.Name) is { } system)
                foreach (var d in system.Roots.SelectMany(r => r.AllNodes()).OfType<CDecal>())
                    if (d.Diffuse.Contains("crack", StringComparison.OrdinalIgnoreCase)) return d.Diffuse;
        return null;
    }

    /// <summary>
    /// The shockwave (**ours**), built from the first of <see cref="RingSprites"/> (or <paramref name="sprite"/>) that the compiled <paramref name="systems"/> draw: a flat additive ring on the
    /// ground growing from a tenth of the radius to the radius over <see cref="ShockwaveSeconds"/> while it fades, a second one just inside it (0.86 of its size, 0.8 as bright) so the band reads on light ground, and a short warm flash light.
    /// It is played at the blast's radius as its scale (the curves are for radius 1). Null when none of the sprites is found.
    /// </summary>
    public static CSystem? Build(IEnumerable<CSystem> systems, string? sprite = null)
    {
        var rings = systems.SelectMany(s => s.Roots.SelectMany(r => r.AllNodes())).OfType<CRing>().Where(r => r.Additive && !r.Material.IsEmpty).ToList();
        CRing? source = null;
        foreach (var name in sprite is null ? RingSprites : [sprite, .. RingSprites])
        {
            source = rings.FirstOrDefault(r => r.Material.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (source is not null) break;
        }
        if (source is null) return null;
        static FxCurve C(params (float T, float V)[] keys) => new(keys.Select(k => new FxKey(k.T, k.V)).ToArray());
        static FxColourCurve Col(params (float T, byte R, byte G, byte B, byte A)[] keys) => new(keys.Select(k => new FxColourKey(k.T, new FxColour(k.R, k.G, k.B, k.A))).ToArray());
        CRing Ring(float alpha) => new()
        {
            Kind = FxElementKind.Ring, Name = "shockwave", Material = source.Material, Billboard = false, Duration = ShockwaveSeconds,
            Radius = C((0f, 0.1f), (0.35f, 0.7f), (1f, 1f)),
            Colour = Col((0f, 255, 238, 214, (byte)(255 * alpha)), (0.45f, 255, 220, 180, (byte)(140 * alpha)), (1f, 255, 200, 150, 0)),
            UseStart = (true, false, true), StartAngle = (0f, 0f, 0f), Life = ShockwaveSeconds,   // flat on the ground, turned at random about the normal only
        };
        var outer = Ring(ShockwaveAlpha);
        var inner = new CCombo
        {
            Kind = FxElementKind.Combo, Name = "inner", Duration = ShockwaveSeconds, Scale = C((0f, 0.86f)), UseStart = (true, true, true),
            Children = [Ring(ShockwaveAlpha * 0.8f)], Life = ShockwaveSeconds,
        };
        var light = new CLight
        {
            Kind = FxElementKind.Light, Name = "stomp flash", Duration = 0.15f, Colour = Col((0f, 255, 214, 160, 255)), Intensity = C((0f, 3f), (1f, 0f)),
            AttenEnd = C((0f, 1.2f)), Power = C((0f, 1f)), OffY = C((0f, 0.15f)), Life = 0.15f,
        };
        return new CSystem { Name = ShockwaveName, Roots = [outer, inner, light], Lifetime = ShockwaveSeconds + 0.1f };
    }

    static Basis AxesFromY(Vector3 y)
    {
        var r = MathF.Abs(y.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        var x = r.Cross(y).Normalized();
        return new Basis(x, y, x.Cross(y));
    }
}
