using System;
using System.Collections.Generic;
using System.Numerics;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient.Extras.Warfront.Dress;

// Warfront dressing, part 1 (slice "dress"): the engine-free half. Everything here is a plain list of pieces in game space (X east, Y north, Z up, metres): client scenery only, no collision, in no hash, in no wire message.
// The Godot half (WarfrontFortressDress.cs, WarfrontDressBake.cs) turns the lists into meshes. Kept free of Godot so the kit tables and the "no part left on the default grey" rule can be tested (tests/SmNet.Tests links these files).
// Every size, colour and count is ours (placeholder demonstration, refine register, spec/behavior.md "Warfront dressing").

/// <summary>What a part of a map's level is, for the dressing: every part of the fortress has one (a test walks them all). Ground and Boundary are drawn by the Ashfall kit's own remainder (terrain and masonry); the others by this slice's plated looks.</summary>
public enum DressRule : byte
{
    /// <summary>The ground: the kit's terrain material (the bands' own).</summary>
    Ground,
    /// <summary>The 64 m wall round the map: the kit's masonry (the bands' own).</summary>
    Boundary,
    /// <summary>The 40 m rock beside the compound: strata rock.</summary>
    RockCliff,
    /// <summary>The compound's walls (south wall with its gate and breach, the north wall behind the arena): Ork scrap plating.</summary>
    CompoundWall,
    /// <summary>The 2 m lintel over the south gate: plating with a hazard band.</summary>
    GateLintel,
    /// <summary>The street blocks: patched scrap with glyph boards.</summary>
    StreetBlock,
    /// <summary>The arena shell's walls: heavy riveted plating with a hazard band.</summary>
    ArenaShell,
    /// <summary>The plaza's four pillars: scrap towers.</summary>
    ScrapTower,
    /// <summary>The low cover walls: wreck pieces of their collision size.</summary>
    WreckCover,
    /// <summary>HQ only: the keep's, the tunnels', the cross passage's and the chamber's parts: not dressed in this pass (W13d may move a door or passage there); the kit look draws them in its own stone.</summary>
    NotDressed,
    /// <summary>HQ only: the fortress wall and the wall-foot chamfers: Imperial masonry.</summary>
    ImperialWall,
    /// <summary>HQ only: the parapets on the wall top: masonry.</summary>
    Parapet,
    /// <summary>HQ only: the two stairs to the parapet: masonry.</summary>
    ParapetStair,
    /// <summary>HQ only: the courtyard's cover walls: the kit's own sandbag and masonry barrier fitted to each one's size.</summary>
    CourtCover,
}

/// <summary>The material a piece is baked with (<c>WarfrontDressBake</c>): the plated looks are shaders, the rest plain colours.</summary>
public enum DressMat : byte { Plated, Wreck, Iron, Bone, ClothRed, ClothBlack, Glow, Dais, Stone, Masonry, Gold, ClothBlue, Smoke, Rock }

/// <summary>The shape of a piece.</summary>
public enum DressShape : byte
{
    /// <summary>A box turned by <see cref="DressPiece.Yaw"/> about Z: <see cref="DressPiece.Size"/> is (length along X, depth along Y, height), <see cref="DressPiece.Base"/> the centre of its bottom.</summary>
    Box,
    /// <summary>A five-sided cone along <see cref="DressPiece.Dir"/>: base diameter (Size.X), length (Size.Z).</summary>
    Spike,
    /// <summary>An eight-sided cylinder along <see cref="DressPiece.Dir"/>: diameter (Size.X), length (Size.Z).</summary>
    Pole,
    /// <summary>A flat 24-sided disc: diameter (Size.X), thickness (Size.Z), standing on the ground.</summary>
    Disc,
}

/// <summary>One piece of the dressing: a shape, a material, a place and a size. <see cref="Shade"/> (0..1) picks a tone inside the material's palette.</summary>
public readonly record struct DressPiece(DressShape Shape, DressMat Mat, Vector3 Base, Vector3 Size, float Yaw = 0f, Vector3? Axis = null, float Shade = 0.5f)
{
    /// <summary>The direction a Spike or Pole points (up unless given).</summary>
    public Vector3 Dir => Axis is { } a && a.LengthSquared() > 1e-6f ? Vector3.Normalize(a) : Vector3.UnitZ;

    /// <summary>A conservative axis-aligned box round the piece (game space), for the "nothing in the breach" test and the opening checks.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Shape == DressShape.Box)
        {
            float c = MathF.Abs(MathF.Cos(Yaw)), sn = MathF.Abs(MathF.Sin(Yaw));
            float ex = c * Size.X / 2f + sn * Size.Y / 2f, ey = sn * Size.X / 2f + c * Size.Y / 2f;
            return (Base + new Vector3(-ex, -ey, 0f), Base + new Vector3(ex, ey, Size.Z));
        }
        float r = Size.X / 2f;
        if (Shape == DressShape.Disc) return (Base + new Vector3(-r, -r, 0f), Base + new Vector3(r, r, Size.Z));
        var tip = Base + Dir * Size.Z;
        return (Vector3.Min(Base, tip) - new Vector3(r, r, r), Vector3.Max(Base, tip) + new Vector3(r, r, r));
    }
}

/// <summary>The dressing of one map as plain data: what the Godot half draws.</summary>
/// <summary>
/// One of the game's own props, read from the install at run time (read only; <c>MekModels</c>): the <see cref="Key"/> names the model (<see cref="DressPropKeys"/>), it stands on <see cref="Base"/>, turned by <see cref="Yaw"/>, scaled to <see cref="Height"/> m.
/// When the install or the model is missing the plan's fallback pieces for that key are drawn instead.
/// </summary>
public readonly record struct DressProp(string Key, Vector3 Base, float Yaw, float Height);

/// <summary>The install models the dressing can draw (the loaders already in the project: <c>MekModels</c>, <c>TfwBanners</c>; docs/discovery.md "Musou capture banners"), by key.</summary>
public static class DressPropKeys
{
    /// <summary>The Ork totem with the WAAAGH glyph (<c>waaaghbanner</c>, 7.1 m).</summary>
    public const string Waaagh = "waaagh";
    /// <summary>The Space Marine banner under a winged skull (<c>sm_banner</c>).</summary>
    public const string SmBanner = "smbanner";
    /// <summary>An Ork scrap heap (<c>ork_junkpile_d_healthy</c>).</summary>
    public const string Junkpile = "junkpile";
    /// <summary>An Ork gun turret (<c>ork_turret_healthy</c>) and its wreck.</summary>
    public const string Turret = "turret", TurretWreck = "turretwreck";
    /// <summary>An Ork tesla tower (<c>ork_elektrik_tower_healthy</c>).</summary>
    public const string Tower = "tower";
    /// <summary>A spike barrier (<c>Spikes_A</c>, the DLC map's).</summary>
    public const string Spikes = "spikes";
    /// <summary>A crashed Ork rok (<c>ork_rok</c>).</summary>
    public const string Rok = "rok";
    /// <summary>All the keys.</summary>
    public static readonly string[] All = [Waaagh, SmBanner, Junkpile, Turret, TurretWreck, Tower, Spikes, Rok];
}

public sealed record DressPlan(IReadOnlyList<DressRule> Rules, IReadOnlyList<DressPiece> Pieces, IReadOnlyList<AshfallDressItem> KitItems)
{
    /// <summary>The install props (read at run time) the drawing places; each key's <see cref="Fallbacks"/> are drawn when its model does not load.</summary>
    public IReadOnlyList<DressProp> Props { get; init; } = [];

    /// <summary>What stands in place of a key's props when the install or its model is missing (the drawn totem, a plated wreck, ...).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DressPiece>> Fallbacks { get; init; } = new Dictionary<string, IReadOnlyList<DressPiece>>();

    /// <summary>The level parts the kit look must not draw itself (every part with a plated rule: this slice draws those).</summary>
    public IReadOnlyList<int> HiddenParts { get; init; } = [];
}

/// <summary>Small deterministic helpers for the plans (integer hashes: the same on every machine).</summary>
public static class DressHash
{
    /// <summary>A mixed 32-bit value for (seed, i).</summary>
    public static uint Mix(uint seed, uint i)
    {
        uint h = seed * 0x9E3779B1u ^ (i + 0x7F4A7C15u) * 0x85EBCA6Bu;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return h;
    }

    /// <summary>A value in 0..1 for (seed, i).</summary>
    public static float Unit(uint seed, uint i) => (Mix(seed, i) & 0xFFFFu) / 65535f;
}
