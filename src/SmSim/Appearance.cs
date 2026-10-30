using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmSim;

/// <summary>
/// A player's loadout request (custom classes; docs/customizer-architecture.md section 4): the class (index into
/// <see cref="SimConfig.Classes"/>) and indices into that class's lists: <see cref="Loadouts.Choices"/> for the primary and pistol,
/// <see cref="ClassLoadout.MeleeChoices"/>, <see cref="ClassLoadout.EquipmentChoices"/> and <see cref="ClassLoadout.PerkChoices"/>
/// for both perk slots. -1 (or anything out of range) means the default: for the class, the player's current class (a change
/// request) or the server's default (a join); for a weapon, melee or equipment slot the class default; for
/// <see cref="Perk1"/> the class's <see cref="ClassLoadout.DefaultPerk"/>; for <see cref="Perk2"/> no perk.
/// Note: <c>default(LoadoutChoice)</c> and <c>new LoadoutChoice()</c> are all zeros (the first entry of every list), not the
/// default; use <see cref="Default"/> or the constructor with named arguments.
/// </summary>
public readonly record struct LoadoutChoice(int Class = -1, int Primary = -1, int Pistol = -1, int Melee = -1, int Equipment = -1, int Perk1 = -1, int Perk2 = -1, int Second = -1)
{
    /// <summary>Every field the default (-1). Protocol 44: <see cref="Second"/> indexes the class's primary choices (Weapon Versatility; -1 none).</summary>
    public static LoadoutChoice Default => new(-1, -1, -1, -1, -1, -1, -1, -1);
}

/// <summary>A perk's family (customizer tabs "CLASS PERKS" / "WEAPON PERKS"): weapon perks are the <c>mp_perk_weap_*</c> files (interpretation).</summary>
public enum PerkKind : byte { Class = 0, Weapon = 1 }

/// <summary>
/// A perk (wargear slot <c>perk</c>; docs/customizer-and-perks-data.md section 2), as the simulation knows it. No effects are
/// simulated yet: <see cref="Loadouts.UpgradeStrings"/> names what the selected perks would switch on.
/// </summary>
/// <param name="Name">The wargear file name, e.g. "mp_perk_bolter_in_place_of_pistol".</param>
/// <param name="UpgradeString">The string the perk applies (<c>apply_upgrade_string/strings/entry</c>), or null when it applies none
/// (Weapon Versatility).</param>
/// <param name="Kind">Class or weapon perk.</param>
/// <param name="Level">The file's <c>unlock_info/level_requirement/level_required</c> (weapon perks: 1; they unlock by challenges).</param>
public sealed record PerkSpec(string Name, string? UpgradeString, PerkKind Kind, int Level)
{
    internal static void Write(BinaryWriter w, PerkSpec p)
    {
        w.Write(p.Name); w.Write(p.UpgradeString is not null); w.Write(p.UpgradeString ?? ""); w.Write((byte)p.Kind); w.Write(p.Level);
    }
}

/// <summary>
/// A custom emblem's identity (docs/customizer-architecture.md section 2.5): the first 16 bytes of the SHA-256 of its
/// <see cref="BlobBytes"/> bytes of BC3 (DXT5) data, read as two little-endian 64-bit halves. (0, 0) is "no emblem".
/// </summary>
public readonly record struct EmblemHash(ulong Lo, ulong Hi)
{
    /// <summary>An emblem's side in pixels (the original badge format: 128 x 128 DXT5).</summary>
    public const int Side = 128;
    /// <summary>An emblem's data size: 128 x 128 pixels in 4 x 4 blocks of 16 bytes.</summary>
    public const int BlobBytes = Side / 4 * (Side / 4) * 16;
    /// <summary>Bytes of a hash on the wire.</summary>
    public const int Bytes = 16;

    public static EmblemHash None => default;
    public bool IsNone => Lo == 0 && Hi == 0;

    /// <summary>The hash of <paramref name="data"/> (any length; an emblem is exactly <see cref="BlobBytes"/>).</summary>
    public static EmblemHash Of(ReadOnlySpan<byte> data)
    {
        Span<byte> sha = stackalloc byte[32];
        SHA256.HashData(data, sha);
        return Read(sha);
    }

    /// <summary>The hash in its first 16 bytes of <paramref name="bytes"/>.</summary>
    public static EmblemHash Read(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));

    /// <summary>Writes the 16 bytes (Lo then Hi, little-endian: the SHA-256 bytes in their order).</summary>
    public void WriteTo(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Lo);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], Hi);
    }

    /// <summary>32 lowercase hex digits of the 16 bytes in order (the emblem file name, <c>&lt;hex&gt;.bc3</c>).</summary>
    public string ToHex()
    {
        Span<byte> b = stackalloc byte[Bytes];
        WriteTo(b);
        return Convert.ToHexStringLower(b);
    }

    /// <summary>Parses <see cref="ToHex"/>'s form (32 hex digits, either case).</summary>
    public static bool TryParseHex(string? hex, out EmblemHash hash)
    {
        hash = default;
        if (hex is not { Length: Bytes * 2 } || !hex.All(char.IsAsciiHexDigit)) return false;
        hash = Read(Convert.FromHexString(hex));
        return true;
    }

    public override string ToString() => ToHex();
}

/// <summary>The nine armour slots of the customizer, in the order an <see cref="AppearanceCode"/> carries them (torso includes the belt).</summary>
public enum ArmourSlot : byte { Head = 0, Torso = 1, LeftShoulder = 2, RightShoulder = 3, LeftArm = 4, RightArm = 5, LeftLeg = 6, RightLeg = 7, Backpack = 8 }

/// <summary>Flags of an <see cref="AppearanceCode"/>.</summary>
[Flags]
public enum AppearanceFlags : byte { None = 0, Lens = 1, PredefinedSet = 2, All = Lens | PredefinedSet }

/// <summary>
/// Catalogue sizes an <see cref="AppearanceCode"/> indexes (per race; from <c>SmContent.CustomizationCatalog</c>): the predefined
/// sets, the offered pieces per <see cref="ArmourSlot"/>, the visible palette and the visible shoulder badges.
/// </summary>
public sealed record AppearanceLimits(int PredefinedSets, IReadOnlyList<int> PiecesPerSlot, int Palette, int Badges);

/// <summary>
/// One race's appearance as the wire carries it (docs/customizer-architecture.md section 4), 62 bytes:
/// <list type="table">
/// <item><term>0</term><description>flags (<see cref="AppearanceFlags"/>: bit 0 lens set, bit 1 predefined set)</description></item>
/// <item><term>1</term><description>predefined set (index into the race's sets; 255 none)</description></item>
/// <item><term>2..10</term><description>piece per <see cref="ArmourSlot"/> (index into that slot's offered pieces; 255 the default set's piece)</description></item>
/// <item><term>11..37</term><description>palette index x3 per slot (primary, secondary, tertiary; 255 the default scheme's colour; ours)</description></item>
/// <item><term>38..39</term><description>pattern bits (u16, bit = slot: pattern on; bits 9..15 zero). For a slot whose three palette
/// entries are all 255 the scheme's own <c>pattern_enabled</c> applies and the bit is ignored (ours)</description></item>
/// <item><term>40, 41</term><description>left, right shoulder decal: 0..252 badge, 253 the default scheme's decal (ours), 254 the custom emblem, 255 none</description></item>
/// <item><term>42</term><description>fallback badge shown until (or instead of) the emblem: 0..252, or 255 the scheme's decal (ours)</description></item>
/// <item><term>43..45</term><description>lens RGB (zero unless the lens flag is set)</description></item>
/// <item><term>46..61</term><description><see cref="EmblemHash"/> (zero: none; set exactly when a pad shows the emblem)</description></item>
/// </list>
/// The default value (<c>default(AppearanceCode)</c>) is <see cref="Default"/>: the default set and scheme, lens unset. Equality
/// compares the bytes. Any 62 bytes decode; <see cref="IsWellFormed"/> and <see cref="Sanitized()"/> check and repair the
/// structure field by field, <see cref="IsWithin"/> and <see cref="Sanitized(AppearanceLimits)"/> the catalogue ranges.
/// </summary>
public readonly struct AppearanceCode : IEquatable<AppearanceCode>
{
    public const int Size = 62, Slots = 9;
    const int FlagsAt = 0, SetAt = 1, PiecesAt = 2, PaletteAt = 11, PatternAt = 38, LeftDecalAt = 40, RightDecalAt = 41, FallbackAt = 42, LensAt = 43, HashAt = 46;
    /// <summary>Piece, palette or set value meaning "the default" (set: none).</summary>
    public const byte DefaultIndex = 255;
    /// <summary>Highest badge index a decal or the fallback may name.</summary>
    public const byte MaxBadge = 252;
    public const byte DecalSchemeDefault = 253, DecalEmblem = 254, DecalNone = 255;
    /// <summary>The fallback badge value meaning the scheme's own decal.</summary>
    public const byte FallbackSchemeDefault = 255;
    const ushort PatternMask = (1 << Slots) - 1;

    static readonly byte[] DefaultBytes = MakeDefault();
    readonly byte[]? _bytes;

    AppearanceCode(byte[] bytes) => _bytes = bytes;

    static byte[] MakeDefault()
    {
        var b = new byte[Size];
        b[SetAt] = DefaultIndex;
        b.AsSpan(PiecesAt, Slots).Fill(DefaultIndex);
        b.AsSpan(PaletteAt, Slots * 3).Fill(DefaultIndex);
        b[LeftDecalAt] = b[RightDecalAt] = DecalSchemeDefault;
        b[FallbackAt] = FallbackSchemeDefault;
        return b;
    }

    /// <summary>The default look: the race's default set and scheme, the scheme's decals, lens unset.</summary>
    public static AppearanceCode Default => default;

    ReadOnlySpan<byte> B => _bytes ?? DefaultBytes;

    /// <summary>The code in <paramref name="bytes"/> (exactly <see cref="Size"/> bytes; any content).</summary>
    public static AppearanceCode FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size) throw new ArgumentException($"An appearance code is {Size} bytes, not {bytes.Length}.", nameof(bytes));
        return new AppearanceCode(bytes.ToArray());
    }

    public void WriteTo(Span<byte> destination) => B.CopyTo(destination);
    public byte[] ToArray() => B.ToArray();

    public AppearanceFlags Flags => (AppearanceFlags)B[FlagsAt];
    public bool HasLens => (B[FlagsAt] & (byte)AppearanceFlags.Lens) != 0;
    public bool HasPredefinedSet => (B[FlagsAt] & (byte)AppearanceFlags.PredefinedSet) != 0;
    /// <summary>The predefined set (255 none).</summary>
    public byte PredefinedSet => B[SetAt];
    /// <summary>The piece in <paramref name="slot"/> (255 the default set's piece).</summary>
    public byte Piece(ArmourSlot slot) => B[PiecesAt + SlotIndex(slot)];
    /// <summary>Palette index of colour <paramref name="channel"/> (0 primary, 1 secondary, 2 tertiary) in <paramref name="slot"/> (255 the scheme's).</summary>
    public byte Palette(ArmourSlot slot, int channel) => B[PaletteAt + SlotIndex(slot) * 3 + ChannelIndex(channel)];
    public ushort PatternBits => BinaryPrimitives.ReadUInt16LittleEndian(B[PatternAt..]);
    public bool PatternOn(ArmourSlot slot) => (PatternBits & (1 << SlotIndex(slot))) != 0;
    public byte LeftDecal => B[LeftDecalAt];
    public byte RightDecal => B[RightDecalAt];
    public byte FallbackBadge => B[FallbackAt];
    public (byte R, byte G, byte B) Lens => (B[LensAt], B[LensAt + 1], B[LensAt + 2]);
    public EmblemHash Emblem => EmblemHash.Read(B[HashAt..]);
    /// <summary>Whether a pad shows the custom emblem.</summary>
    public bool UsesEmblem => LeftDecal == DecalEmblem || RightDecal == DecalEmblem;
    public bool IsDefault => B.SequenceEqual(DefaultBytes);

    // ---- builders (each returns a new code; values are stored as given, see IsWellFormed) ----
    AppearanceCode Edit(Action<byte[]> edit) { var b = ToArray(); edit(b); return new AppearanceCode(b); }
    public AppearanceCode WithPredefinedSet(byte set) => Edit(b =>
    {
        b[SetAt] = set;
        b[FlagsAt] = (byte)(set == DefaultIndex ? b[FlagsAt] & ~(int)AppearanceFlags.PredefinedSet : b[FlagsAt] | (int)AppearanceFlags.PredefinedSet);
    });
    public AppearanceCode WithPiece(ArmourSlot slot, byte piece) => Edit(b => b[PiecesAt + SlotIndex(slot)] = piece);
    public AppearanceCode WithPalette(ArmourSlot slot, byte primary, byte secondary, byte tertiary) => Edit(b =>
    {
        int at = PaletteAt + SlotIndex(slot) * 3;
        b[at] = primary; b[at + 1] = secondary; b[at + 2] = tertiary;
    });
    public AppearanceCode WithPattern(ArmourSlot slot, bool on) => Edit(b =>
    {
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(PatternAt));
        bits = on ? bits | (1 << SlotIndex(slot)) : bits & ~(1 << SlotIndex(slot));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(PatternAt), (ushort)bits);
    });
    public AppearanceCode WithDecals(byte left, byte right, byte fallback = FallbackSchemeDefault) => Edit(b => { b[LeftDecalAt] = left; b[RightDecalAt] = right; b[FallbackAt] = fallback; });
    /// <summary>Sets the lens colour (null: unset, the original look).</summary>
    public AppearanceCode WithLens((byte R, byte G, byte B)? lens) => Edit(b =>
    {
        var (r, g, bl) = lens ?? (0, 0, 0);
        b[LensAt] = r; b[LensAt + 1] = g; b[LensAt + 2] = bl;
        b[FlagsAt] = (byte)(lens is null ? b[FlagsAt] & ~(int)AppearanceFlags.Lens : b[FlagsAt] | (int)AppearanceFlags.Lens);
    });
    public AppearanceCode WithEmblem(EmblemHash hash) => Edit(b => hash.WriteTo(b.AsSpan(HashAt)));

    /// <summary>
    /// Whether the structure is consistent: no unknown flag bits; the predefined-set flag set exactly when a set is named; lens
    /// bytes zero without the lens flag; pattern bits 9..15 zero; the fallback a badge (0..252) or 255; an emblem hash exactly
    /// when a pad shows the emblem. Catalogue ranges are <see cref="IsWithin"/>'s.
    /// </summary>
    public bool IsWellFormed() => Sanitized().Equals(this);

    /// <summary>The code with every inconsistent field (see <see cref="IsWellFormed"/>) set to its default, one by one.</summary>
    public AppearanceCode Sanitized()
    {
        var b = ToArray();
        b[FlagsAt] &= (byte)AppearanceFlags.All;
        if (((b[FlagsAt] & (byte)AppearanceFlags.PredefinedSet) != 0) != (b[SetAt] != DefaultIndex))
        { b[SetAt] = DefaultIndex; b[FlagsAt] &= unchecked((byte)~(int)AppearanceFlags.PredefinedSet); }
        if ((b[FlagsAt] & (byte)AppearanceFlags.Lens) == 0) b.AsSpan(LensAt, 3).Clear();
        int pattern = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(PatternAt)) & PatternMask;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(PatternAt), (ushort)pattern);
        if (b[FallbackAt] is > MaxBadge and not FallbackSchemeDefault) b[FallbackAt] = FallbackSchemeDefault;
        bool hashSet = !EmblemHash.Read(b.AsSpan(HashAt)).IsNone;
        if (!hashSet)
        {
            if (b[LeftDecalAt] == DecalEmblem) b[LeftDecalAt] = DecalSchemeDefault;
            if (b[RightDecalAt] == DecalEmblem) b[RightDecalAt] = DecalSchemeDefault;
        }
        else if (b[LeftDecalAt] != DecalEmblem && b[RightDecalAt] != DecalEmblem) b.AsSpan(HashAt, EmblemHash.Bytes).Clear();
        return new AppearanceCode(b);
    }

    /// <summary>Whether every index is in range of <paramref name="limits"/> (or its default value); see <see cref="Sanitized(AppearanceLimits)"/>.</summary>
    public bool IsWithin(AppearanceLimits limits) => Sanitized(limits).Equals(this);

    /// <summary>
    /// The structurally sanitized code (<see cref="Sanitized()"/>) with every index outside <paramref name="limits"/> set to its
    /// default, one by one: a set beyond the sets (then also the flag), a piece beyond its slot's pieces, a palette entry beyond
    /// the palette, a badge beyond the badges (decal: the scheme's; fallback: the scheme's). A slot missing from
    /// <see cref="AppearanceLimits.PiecesPerSlot"/> has no pieces.
    /// </summary>
    public AppearanceCode Sanitized(AppearanceLimits limits)
    {
        var b = Sanitized().ToArray();
        if (b[SetAt] != DefaultIndex && b[SetAt] >= limits.PredefinedSets) { b[SetAt] = DefaultIndex; b[FlagsAt] &= unchecked((byte)~(int)AppearanceFlags.PredefinedSet); }
        for (int s = 0; s < Slots; s++)
        {
            int pieces = s < limits.PiecesPerSlot.Count ? limits.PiecesPerSlot[s] : 0;
            if (b[PiecesAt + s] != DefaultIndex && b[PiecesAt + s] >= pieces) b[PiecesAt + s] = DefaultIndex;
            for (int k = 0; k < 3; k++)
                if (b[PaletteAt + s * 3 + k] != DefaultIndex && b[PaletteAt + s * 3 + k] >= limits.Palette) b[PaletteAt + s * 3 + k] = DefaultIndex;
        }
        if (b[LeftDecalAt] <= MaxBadge && b[LeftDecalAt] >= limits.Badges) b[LeftDecalAt] = DecalSchemeDefault;
        if (b[RightDecalAt] <= MaxBadge && b[RightDecalAt] >= limits.Badges) b[RightDecalAt] = DecalSchemeDefault;
        if (b[FallbackAt] <= MaxBadge && b[FallbackAt] >= limits.Badges) b[FallbackAt] = FallbackSchemeDefault;
        return new AppearanceCode(b);
    }

    static int SlotIndex(ArmourSlot slot) => (int)slot is >= 0 and < Slots ? (int)slot : throw new ArgumentOutOfRangeException(nameof(slot));
    static int ChannelIndex(int channel) => channel is >= 0 and < 3 ? channel : throw new ArgumentOutOfRangeException(nameof(channel));

    public bool Equals(AppearanceCode other) => B.SequenceEqual(other.B);
    public override bool Equals(object? obj) => obj is AppearanceCode other && Equals(other);
    public override int GetHashCode() { var h = new HashCode(); h.AddBytes(B); return h.ToHashCode(); }
    public static bool operator ==(AppearanceCode a, AppearanceCode b) => a.Equals(b);
    public static bool operator !=(AppearanceCode a, AppearanceCode b) => !a.Equals(b);
    public override string ToString() => Convert.ToHexStringLower(B);
}

/// <summary>
/// A player's race: which look an <see cref="AppearanceCode"/> is for (Ork O5, protocol 28; docs/ork-faction-design.md section 1.2). Space Marines
/// and Chaos have the values of their <see cref="Team"/>; <see cref="Orks"/> plays on either team (the faction is per player). On the wire as one byte
/// (the <c>Appearance</c> message); <see cref="None"/> never travels.
/// </summary>
public enum Race : byte { None = 0, SpaceMarines = 1, Chaos = 2, Orks = 3 }

/// <summary>Races and the Ork's look (Ork O5; docs/ork-faction-design.md sections 1.2 and 3).</summary>
public static class Races
{
    /// <summary>
    /// The Ork looks (ours: the install has no Ork look choice): the boy meshes <c>boy_ranged</c>, <c>boy_melee</c>, <c>boy_ranged_pro</c>,
    /// <c>boy_melee_pro</c>, <c>boy_ardboy</c>, in this order (the client's <c>OrkAssets.Looks</c>). A look travels as its index.
    /// </summary>
    public const int OrkLooks = 5;
    /// <summary>The Ork look byte meaning "automatic" (the client picks a look by player id; design section 9, question 7).</summary>
    public const byte OrkLookAutomatic = 255;

    /// <summary>Whether <paramref name="race"/> is one a code can be for (1..3).</summary>
    public static bool IsKnown(Race race) => race is Race.SpaceMarines or Race.Chaos or Race.Orks;

    /// <summary>The race of a side's own classes: <see cref="Team.Chaos"/> Chaos, anything else Space Marines.</summary>
    public static Race OfSide(Team side) => side == Team.Chaos ? Race.Chaos : Race.SpaceMarines;

    /// <summary>
    /// The race of a player of <paramref name="playerClass"/> on <paramref name="team"/>: the class's own race (<see cref="ClassLoadout.Race"/>, the
    /// Ork Boy) when it has one; else its team when that is a side; else the class's side; else Space Marines.
    /// </summary>
    public static Race Of(SimConfig config, int playerClass, Team team)
    {
        var info = playerClass >= 0 && playerClass < config.Classes.Count ? config.Classes[playerClass] : null;
        if (info is not null && info.Race != Race.None) return info.Race;
        if (team is Team.SpaceMarines or Team.Chaos) return OfSide(team);
        return OfSide(info?.Side ?? Team.None);
    }

    /// <summary>
    /// An Ork's code (design section 3): the default code naming look <paramref name="look"/> as its predefined set; automatic (any value from
    /// <see cref="OrkLooks"/> up) is the default code. No pieces, palette, decals, lens or emblem.
    /// </summary>
    public static AppearanceCode OrkCode(byte look) => look < OrkLooks ? AppearanceCode.Default.WithPredefinedSet(look) : AppearanceCode.Default;

    /// <summary>The Ork look a code names (its predefined set when below <see cref="OrkLooks"/>), else <see cref="OrkLookAutomatic"/>.</summary>
    public static byte OrkLookOf(AppearanceCode code) => code.HasPredefinedSet && code.PredefinedSet < OrkLooks ? code.PredefinedSet : OrkLookAutomatic;

    /// <summary>What an Ork's code may hold, as <see cref="AppearanceLimits"/>: the looks as predefined sets, nothing else.</summary>
    public static AppearanceLimits OrkLimits { get; } = new(OrkLooks, [], 0, 0);
}

/// <summary>
/// What the server accepts in a player's appearance (protocol 22; a <see cref="Race"/> since protocol 28): it gets the code a client sent for the
/// race the player plays and returns the code to use, every bad field set to its default one by one (never a refusal of the whole appearance).
/// Pure: the server applies it, and the profile side's tests check its codes pass unchanged.
/// </summary>
public interface IAppearanceRules
{
    AppearanceCode Apply(AppearanceCode code, Race race, int playerClass);
}

/// <summary>
/// The Ork's rules (Ork O5; design section 3): the code is rebuilt from scratch from the look it names (<see cref="Races.OrkCode"/>): no pieces,
/// palette, patterns, decals, lens or emblem; a look out of range is automatic. The server applies them to every Ork, with or without catalogue rules.
/// </summary>
public static class OrkAppearanceRules
{
    public static AppearanceCode Apply(AppearanceCode code) => Races.OrkCode(Races.OrkLookOf(code.Sanitized()));
}

/// <summary>
/// The catalogue's rules: the structure (<see cref="AppearanceCode.Sanitized()"/>), the ranges of the race's lists
/// (<see cref="AppearanceCode.Sanitized(AppearanceLimits)"/>: sets, pieces per slot, the visible palette, the visible badges; the
/// catalogue lists only customizer-visible items, so visibility is covered by the ranges), and the predefined-set lock (our default for the
/// open question 8 of docs/customizer-architecture.md, section 10: a predefined set fills every slot with its own pieces and scheme, so pieces, colours and patterns go back to their
/// defaults). Not checked here: pads without a decal layout (needs the meshes; the client draws no decal there, see
/// <c>PaintSchemes.HasDecalUv</c>). An Ork's code follows <see cref="OrkAppearanceRules"/> (protocol 28).
/// </summary>
public sealed class CatalogAppearanceRules(AppearanceLimits spaceMarines, AppearanceLimits chaos) : IAppearanceRules
{
    public AppearanceLimits SpaceMarines { get; } = spaceMarines;
    public AppearanceLimits Chaos { get; } = chaos;

    public AppearanceLimits LimitsFor(Team race) => race == Team.Chaos ? Chaos : SpaceMarines;
    public AppearanceLimits LimitsFor(Race race) => race switch { Race.Chaos => Chaos, Race.Orks => Races.OrkLimits, _ => SpaceMarines };

    /// <summary>The rules for a side's own race (<see cref="Races.OfSide"/>).</summary>
    public AppearanceCode Apply(AppearanceCode code, Team side, int playerClass) => Apply(code, Races.OfSide(side), playerClass);

    public AppearanceCode Apply(AppearanceCode code, Race race, int playerClass)
    {
        if (race == Race.Orks) return OrkAppearanceRules.Apply(code);
        var c = code.Sanitized(LimitsFor(race));
        if (!c.HasPredefinedSet) return c;
        foreach (var slot in Enum.GetValues<ArmourSlot>())
            c = c.WithPiece(slot, AppearanceCode.DefaultIndex).WithPalette(slot, AppearanceCode.DefaultIndex, AppearanceCode.DefaultIndex, AppearanceCode.DefaultIndex)
                .WithPattern(slot, false);
        return c;
    }
}
