using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// One picture inside a UI texture of the install: the texture's name in <c>ui_ingame.oppc</c> or <c>ui_common.oppc</c> (without <c>.dds</c>), the
/// rectangle in its pixels and the texture's size (to check the rectangle). The <see cref="HudSprites"/> rectangles are <b>ours</b>: measured on the
/// textures (the Scaleform movie that places them, <c>Hud.gfx</c>, is not read), M44. The <see cref="FeedIcons"/> and <see cref="DecoratorSprites"/>
/// rectangles are <b>extracted</b> from their movies' sub-image records (fidelity gap 10).
/// </summary>
public sealed record UiSprite(string Texture, int X, int Y, int Width, int Height, int TextureWidth, int TextureHeight)
{
    /// <summary>A whole texture.</summary>
    public static UiSprite Whole(string texture, int width, int height) => new(texture, 0, 0, width, height, width, height);

    public bool InsideTexture => X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= TextureWidth && Y + Height <= TextureHeight;
}

/// <summary>
/// The pieces of the original in-game HUD art the client draws (M44). The textures are the bitmaps the Scaleform exporter wrote
/// next to <c>Hud.gfx</c> in <c>data/packages/ui_ingame.oppc</c> (<c>hud_i*.dds</c> atlases, one 96 x 64 picture per weapon);
/// what each rectangle is was decided by comparing them with the owner's gameplay footage (interpretation, spec "In-game HUD (M44)").
/// </summary>
public static class HudSprites
{
    // Armour and health bar, top centre: background with ticks, the cyan armour glow, the red health fill, the medallion at the left end.
    public static readonly UiSprite BarBack = UiSprite.Whole("hud_i203", 404, 28);
    public static readonly UiSprite BarArmour = UiSprite.Whole("hud_i215", 412, 36);
    public static readonly UiSprite BarHealth = UiSprite.Whole("hud_i223", 376, 16);
    public static readonly UiSprite Medallion = new("hud_i5d", 194, 186, 49, 48, 256, 256);
    // Side emblems (score panel, Seize Ground points and markers).
    public static readonly UiSprite ChaosStar = new("hud_i7", 79, 2, 103, 83, 256, 256);
    public static readonly UiSprite Aquila = new("hud_i7", 4, 170, 148, 50, 256, 256);
    public static readonly UiSprite NeutralDiamond = new("hud_i7", 188, 3, 46, 46, 256, 256);
    // Score panel bars (a white bar with pointed ends, tinted per side).
    public static readonly UiSprite ScoreBar = new("hud_i4", 6, 174, 219, 19, 256, 248);
    // Weapon box: the winged skull on the top edge, clip ticks (loaded / spent), the charge mark, the heat bar, the endless-ammo sign.
    public static readonly UiSprite BoxOrnament = new("hud_i176", 36, 0, 134, 17, 256, 184);
    public static readonly UiSprite TickLoaded = new("hud_i176", 0, 93, 7, 11, 256, 184);
    public static readonly UiSprite TickSpent = new("hud_i176", 0, 78, 7, 11, 256, 184);
    public static readonly UiSprite ChargeMark = new("hud_i4", 219, 40, 28, 25, 256, 248);
    public static readonly UiSprite HeatBar = UiSprite.Whole("hud_i1fd", 308, 36);
    public static readonly UiSprite Endless = new("hud_i7", 162, 147, 37, 17, 256, 256);
    public static readonly UiSprite Grenade = UiSprite.Whole("grenadeactive", 28, 32);
    // Weapon-switch cards: the selected card's orange frame, the chevrons pointing at the cards.
    public static readonly UiSprite CardFrame = new("hud_i7", 116, 90, 114, 56, 256, 256);
    public static readonly UiSprite ChevronGold = new("hud_i4", 161, 114, 42, 23, 256, 248);
    public static readonly UiSprite ChevronWhite = new("hud_i7", 200, 146, 40, 19, 256, 256);
    // Reward line: the skull-in-a-ring medal.
    public static readonly UiSprite KillMedal = new("hud_ie", 129, 187, 35, 37, 256, 256);

    /// <summary>Every sprite above (for loading and checks).</summary>
    public static readonly IReadOnlyList<UiSprite> All =
    [
        BarBack, BarArmour, BarHealth, Medallion, ChaosStar, Aquila, NeutralDiamond, ScoreBar, BoxOrnament, TickLoaded, TickSpent,
        ChargeMark, HeatBar, Endless, Grenade, CardFrame, ChevronGold, ChevronWhite, KillMedal,
    ];

    /// <summary>The size of every weapon picture (<c>bolter.dds</c> and the like).</summary>
    public const int IconWidth = 96, IconHeight = 64;
}

/// <summary>
/// The HUD's rules that need no engine (M44): which weapon picture a wargear file shows, how the clip turns into ticks, how the
/// switch cards write "clip/reserve". All <b>ours</b> except the data they read (<c>ui_data/weapon_type</c>), spec "In-game HUD (M44)".
/// </summary>
public static class HudRules
{
    /// <summary>At most this many clip ticks fit left of the weapon picture; a larger clip shows one tick per several rounds.</summary>
    public const int MaxTicks = 22;

    /// <summary>
    /// The picture (texture name) for a wargear file: its <c>ui_data/weapon_type</c> mapped by name onto the pictures in
    /// <c>ui_ingame.oppc</c> (ours: the movie's own mapping is code), the Chaos variant (<c>_chaos</c>) for <c>csm_</c> wargear when
    /// the install has one, the gold variants for <c>_gold</c> wargear. Null when nothing matches (the HUD then draws no picture).
    /// </summary>
    public static string? IconFor(string wargear, string? weaponType, Func<string, bool> exists)
    {
        string type = (weaponType is { Length: > 0 } ? weaponType : TypeFromName(wargear)).ToLowerInvariant();
        bool chaos = wargear.StartsWith("csm_", StringComparison.OrdinalIgnoreCase);
        bool gold = wargear.Contains("_gold", StringComparison.OrdinalIgnoreCase);
        string? Pick(params string[] names) => names.FirstOrDefault(n => n.Length > 0 && exists(n));
        return type switch
        {
            "bolter" => gold ? Pick(chaos ? "tainted_bolter" : "golden_bolter", chaos ? "bolter_chaos" : "bolter") : Pick(chaos ? "bolter_chaos" : "", "bolter"),
            "bolt_pistol" => Pick(chaos ? "bolt_pistol_chaos" : "", "bolt_pistol"),
            "storm_bolter" => Pick(chaos ? "stormbolter_chaos" : "", "stormbolter"),
            "stalker" => Pick(chaos ? "stalker_bolter_chaos" : "", "stalker_bolter"),
            "heavy_bolter" => Pick(chaos ? "heavy_bolter_chaos" : "", "heavy_bolter"),
            "laz_cannon" or "lascannon" => Pick(chaos ? "lascannon_chaos" : "", "lascannon"),
            "plasma_gun" => Pick(chaos ? "plasma_gun_chaos" : "", "plasma_gun"),
            "plasma_cannon" => Pick(chaos ? "plasma_cannon_chaos" : "", "plasma_cannon"),
            "plasma_pistol" => Pick(chaos ? "plasma_pistol_chaos" : "", "plasma_pistol"),
            "melta" => Pick(chaos ? "meltagun_chaos" : "", "melta_gun"),
            "grenade_launcher" => Pick("vengance_cannon"),
            "chainsword" => gold ? Pick(chaos ? "tainted_chainsword" : "golden_chainsword", "chainsword") : Pick(chaos ? "chainsword_chaos" : "", "chainsword"),
            "knife" => Pick(chaos ? "knife_chaos" : "", "knife"),
            "powersword" => Pick(chaos ? "power_sword_chaos" : "", "power_sword"),
            "power_axe" => Pick(chaos ? "chainaxe" : "", "poweraxe"),
            "thunder_hammer" => Pick(chaos ? "powermace" : "", "thunder_hammer"),
            "grenade" => Pick("frag_grenade"),
            "krakgrenades" => Pick("krak_grenade"),
            "jumppack" => Pick("jumppack"),
            "stomp" => Pick("stomp"),
            "reactiveblast" => Pick("reactiveblast"),
            "dreadnought" => Pick(chaos ? "dreadnaught_chaos" : "", "dreadnought", "dreadnaught"),
            _ => null,
        };
    }

    /// <summary>The weapon type from the wargear name when the data has none (our fallback; "default" is the bolter).</summary>
    public static string TypeFromName(string wargear)
    {
        string n = wargear.ToLowerInvariant();
        if (n == "default") return "bolter";
        (string Part, string Type)[] parts =
        [
            ("storm_bolter", "storm_bolter"), ("heavy_bolter", "heavy_bolter"), ("sniper", "stalker"), ("stalker", "stalker"), ("bolt_pistol", "bolt_pistol"),
            ("plasma_pistol", "plasma_pistol"), ("plasma_cannon", "plasma_cannon"), ("plasma_gun", "plasma_gun"), ("las_cannon", "laz_cannon"),
            ("lascannon", "laz_cannon"), ("melta", "melta"), ("grenade_launcher", "grenade_launcher"), ("frag", "grenade"), ("krak", "krakgrenades"),
            ("jumppack", "jumppack"), ("chainsword", "chainsword"), ("knife", "knife"), ("powersword", "powersword"), ("bolter", "bolter"),
        ];
        foreach (var (part, type) in parts) if (n.Contains(part)) return type;
        return "";
    }

    /// <summary>
    /// The kill feed's picture for a weapon (fidelity gap 10): the label of the feed movie's <c>weapon_icon_mc</c> frame (<see cref="FeedIcons"/>).
    /// The labels are the wargear's <c>ui_data/weapon_type</c> values; <c>killed_by_weapon_type</c> overrides it where the file sets one (the
    /// perks' blast files, the dreadnought's weapons; it is empty on every ordinary weapon). Which field the engine reads is an
    /// <b>interpretation</b> (the names match; the code is not read). Null when no label fits (the feed then draws the weapon's HUD picture, or its name).
    /// </summary>
    public static string? FeedLabel(string wargear, string? weaponType, string? killedBy = null)
    {
        static string? Known(string? label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            string l = label.ToLowerInvariant();
            if (l == "lascannon") l = "laz_cannon";
            return FeedIcons.Find(l) is not null ? l : null;
        }
        // A file that names a type the feed has no picture for (the melta bomb's "plasma_grenade") gets none; the name is a fallback for files without one.
        if (!string.IsNullOrEmpty(weaponType)) return Known(killedBy) ?? Known(weaponType);
        return Known(killedBy) ?? Known(TypeFromName(wargear));
    }

    /// <summary>
    /// The clip as ticks: how many ticks the row has and how many of them are loaded. One tick per round up to <see cref="MaxTicks"/>
    /// rounds; a larger clip shows <see cref="MaxTicks"/> ticks, each standing for an equal share (a part-loaded tick counts as loaded).
    /// </summary>
    public static (int Ticks, int Loaded) Ticks(int clip, int clipMax)
    {
        if (clipMax <= 0) return (0, 0);
        clip = Math.Clamp(clip, 0, clipMax);
        if (clipMax <= MaxTicks) return (clipMax, clip);
        int loaded = (int)Math.Ceiling(clip * (double)MaxTicks / clipMax);
        return (MaxTicks, Math.Clamp(loaded, 0, MaxTicks));
    }

    /// <summary>"clip/reserve" as the switch cards write it: the clip padded with zeros to the digits of the clip size ("06/68", "4/23").</summary>
    public static string CardAmmo(int clip, int clipMax, int reserve)
    {
        int digits = Math.Max(1, Math.Max(clipMax, 0).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        return Math.Max(clip, 0).ToString("D" + digits, System.Globalization.CultureInfo.InvariantCulture) + "/" + Math.Max(reserve, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// One weapon picture of the kill feed (fidelity gap 10): a 48 x 48 white silhouette with blood splatter in the feed movie's atlas
/// (<c>hudeventlog_i3</c>, 248 x 248, a 5 x 5 grid of 50-pixel cells), possibly mirrored (the feed movie draws the knife flipped
/// vertically and the krak grenade as the frag grenade flipped horizontally).
/// </summary>
public sealed record FeedIcon(string Label, int X, int Y, bool FlipX = false, bool FlipY = false)
{
    public UiSprite Sprite { get; } = new(FeedIcons.Texture, X, Y, FeedIcons.Size, FeedIcons.Size, FeedIcons.TextureSize, FeedIcons.TextureSize);
}

/// <summary>
/// The kill feed's weapon pictures, read from <c>HudEventLog.gfx</c> in <c>data/packages/ui_ingame.oppc</c> (tags walked by hand: the
/// movie's <c>weapon_icon_mc</c> has one frame per picture, labelled with the weapon type; each frame's shape is a bitmap fill of a
/// rectangle of the atlas; no ActionScript decompiled). Rectangles, labels and flips are <b>extracted</b>; spec "Kill feed (fidelity gap 10)".
/// </summary>
public static class FeedIcons
{
    public const string Texture = "hudeventlog_i3";
    public const int TextureSize = 248, Size = 48;

    public static readonly IReadOnlyList<FeedIcon> All =
    [
        new("bolter", 50, 100), new("heavy_bolter", 50, 0), new("laz_cannon", 200, 100), new("plasma_cannon", 0, 150), new("plasma_gun", 100, 100),
        new("plasma_pistol", 50, 150), new("stalker", 150, 150), new("power_axe", 200, 150), new("thunder_hammer", 0, 200),
        new("knife", 150, 100, FlipY: true), new("chainsword", 100, 150), new("storm_bolter", 0, 100), new("bolt_pistol", 0, 50),
        new("grenade", 200, 50), new("krakgrenades", 200, 50, FlipX: true), new("melta", 100, 0), new("grenade_launcher", 150, 0),
        new("powersword", 0, 0), new("reactiveblast", 200, 0), new("jumppack", 50, 50), new("bullrush", 100, 50), new("stomp", 150, 50),
        new("dreadnought", 50, 200),
    ];

    public static FeedIcon? Find(string label)
    {
        foreach (var icon in All) if (string.Equals(icon.Label, label, StringComparison.OrdinalIgnoreCase)) return icon;
        return null;
    }
}

/// <summary>
/// The pictures of the world decorators (fidelity gap 10) in the install's <c>Decorator.gfx</c> (<c>data/packages/ui_common.oppc</c>): the
/// movie's shapes are bitmap fills of the atlases <c>decorator_i1</c>, <c>decorator_i3</c> and <c>decorator_i10</c>; the rectangles are
/// <b>extracted</b> from the movie's sub-image records. The movie names the four race icons by state (<c>ally_sm</c>, <c>ally_chaos</c>,
/// <c>enemy_sm</c>, <c>enemy_chaos</c>: gold eagle, gold Chaos star, red eagle, red Chaos star).
/// </summary>
public static class DecoratorSprites
{
    public const string Atlas1 = "decorator_i1", Atlas3 = "decorator_i3", Atlas10 = "decorator_i10";
    /// <summary>Every texture the decorators read.</summary>
    public static readonly IReadOnlyList<string> Textures = [Atlas1, Atlas3, Atlas10];

    public static readonly UiSprite EagleAlly = new(Atlas1, 0, 152, 131, 70, 244, 256);
    public static readonly UiSprite StarAlly = new(Atlas10, 0, 0, 131, 70, 232, 236);
    public static readonly UiSprite EagleEnemy = new(Atlas10, 0, 72, 131, 70, 232, 236);
    public static readonly UiSprite StarEnemy = new(Atlas1, 90, 78, 131, 70, 244, 256);
    // The pointer triangles of the point decorator (grey = neutral, yellow = ally, red = enemy), 80 x 80 on decorator_i3.
    public static readonly UiSprite TriangleNeutral = new(Atlas3, 164, 138, 80, 80, 244, 256);
    public static readonly UiSprite TriangleAlly = new(Atlas3, 82, 138, 80, 80, 244, 256);
    public static readonly UiSprite TriangleEnemy = new(Atlas3, 0, 138, 80, 80, 244, 256);

    public static readonly IReadOnlyList<UiSprite> All = [EagleAlly, StarAlly, EagleEnemy, StarEnemy, TriangleNeutral, TriangleAlly, TriangleEnemy];

    /// <summary>The race icon for a player or point: gold for a friend, red for a foe, the Chaos star or the aquila by race.</summary>
    public static UiSprite Icon(bool chaos, bool enemy) => chaos ? (enemy ? StarEnemy : StarAlly) : (enemy ? EagleEnemy : EagleAlly);
}

/// <summary>Whom a decorator or point belongs to, from the viewer: nobody (a neutral point), a friend or a foe.</summary>
public enum DecoratorRelation { None, Ally, Enemy }

/// <summary>
/// The world decorators' rules that need no engine (fidelity gap 10). The timings and state names are <b>extracted</b> from
/// <c>Decorator.gfx</c> (labels and frame counts of the movie, 30 fps); which game state selects which label, the visibility rules
/// and the sizes on screen are <b>interpretations</b> or <b>ours</b> (the code that drives the movie is in the executable), spec
/// "World decorators (fidelity gap 10)".
/// </summary>
public static class DecoratorRules
{
    /// <summary>The movie's frame rate (the stage is 1280 x 720).</summary>
    public const float FrameRate = 30f;
    /// <summary>Flash loop (<c>*_flash</c> labels): 30 frames = 1 s; the combat flash (<c>*_combat_flash</c>): 10 frames.</summary>
    public const int FlashFrames = 30, CombatFlashFrames = 10;

    // Alpha (of 256) of the overlaying icon in a flash loop, frame 1 on; the last value holds to the end of the loop.
    static readonly byte[] FlashTable =
        [255, 239, 223, 208, 193, 179, 165, 152, 140, 128, 117, 107, 97, 88, 79, 71, 64, 58, 52, 46, 41, 37, 34, 31, 29, 27, 26];
    static readonly byte[] CombatFlashTable = [255, 195, 143, 101, 68, 45, 31, 26];

    /// <summary>The overlay icon's alpha (0..1) <paramref name="seconds"/> into a flash loop: bright at the start, fading to 10 %, then the loop restarts.</summary>
    public static float FlashAlpha(double seconds, bool combat)
    {
        int frames = combat ? CombatFlashFrames : FlashFrames;
        var table = combat ? CombatFlashTable : FlashTable;
        double loop = frames / (double)FrameRate;
        double t = seconds - Math.Floor(seconds / loop) * loop;
        int frame = Math.Clamp((int)(t * FrameRate), 0, frames - 1);
        return table[Math.Min(frame, table.Length - 1)] / 256f;
    }

    /// <summary>The icon's baseline alpha while no flash plays (the movie's colour transform on the state frames: 128 of 256).</summary>
    public const float IdleIconAlpha = 0.5f;

    /// <summary>
    /// The label of the point decorator's <c>anim_mc</c> frame for a control point. Neutral: <c>neutral</c>, or the side that is capturing it
    /// (<c>neutral_flash_ally</c>, <c>neutral_flash_enemy</c>); owned: <c>ally_sm</c> / <c>ally_chaos</c> / <c>enemy_sm</c> / <c>enemy_chaos</c>,
    /// blinking when the other side is working on it (<c>_flash</c>; the game's tip: "If a capture point you own is blinking, that means an
    /// enemy is uncapping it"), and the combat variants while both sides stand in it. The state-to-label mapping is an <b>interpretation</b>
    /// of the label names; the label set is the movie's.
    /// </summary>
    /// <param name="owner">Who holds the point.</param>
    /// <param name="ownerIsChaos">The owner's race (Chaos star or aquila).</param>
    /// <param name="side">Whose progress is running (the capturing side of a neutral point).</param>
    /// <param name="working">Progress is changing, or it is not full on an owned point.</param>
    /// <param name="contested">Both sides stand in the point.</param>
    public static string PointLabel(DecoratorRelation owner, bool ownerIsChaos, DecoratorRelation side, bool working, bool contested)
    {
        // Constant strings only: this runs per point per frame.
        if (owner == DecoratorRelation.None)
        {
            bool enemy = side == DecoratorRelation.Enemy;
            if (contested) return enemy ? "neutral_flash_enemy_combat" : "neutral_flash_ally_combat";
            return working && side != DecoratorRelation.None ? (enemy ? "neutral_flash_enemy" : "neutral_flash_ally") : "neutral";
        }
        bool foe = owner == DecoratorRelation.Enemy;
        if (contested) return foe ? (ownerIsChaos ? "enemy_chaos_combat_flash" : "enemy_sm_combat_flash") : (ownerIsChaos ? "ally_chaos_combat_flash" : "ally_sm_combat_flash");
        if (working) return foe ? (ownerIsChaos ? "enemy_chaos_flash" : "enemy_sm_flash") : (ownerIsChaos ? "ally_chaos_flash" : "ally_sm_flash");
        return foe ? (ownerIsChaos ? "enemy_chaos" : "enemy_sm") : (ownerIsChaos ? "ally_chaos" : "ally_sm");
    }

    /// <summary>Whether a label is one of the point decorator's flash states.</summary>
    public static bool IsFlash(string label) => label.Contains("flash", StringComparison.Ordinal);

    /// <summary>Whether a label is one of the combat-flash states (the short loop).</summary>
    public static bool IsCombat(string label) => label.Contains("combat", StringComparison.Ordinal);

    /// <summary>The capture bar's clip has 100 frames; frame 1 is empty, frame 99 full (the sliding fill's right edge reaches the bar's end).</summary>
    public static int CaptureBarFrame(float progress) => 1 + (int)MathF.Round(Math.Clamp(progress, 0f, 1f) * 98f);

    /// <summary>The fill (0..1) a capture-bar frame shows.</summary>
    public static float CaptureBarFill(int frame) => Math.Clamp((frame - 1) / 98f, 0f, 1f);

    /// <summary>Metres within which an ally's decorator shows at all (ours).</summary>
    public const float AllyRange = 120f;
    /// <summary>
    /// Metres within which an ally is shown through walls, and the farthest an enemy is shown (in the line of sight only): the entity
    /// decorators' <c>decorator_occlusion_distance</c> of 75 m (every ordinary blueprint of the install). Applying it to player decorators is ours.
    /// </summary>
    public const float OcclusionDistance = 75f;

    /// <summary>Whether a player's decorator shows (ours, owner 2026-10-30): only the player under the crosshair (the caller passes it; the ray is the line of sight), friend or foe, within <see cref="AllyRange"/>.</summary>
    public static bool PlayerVisible(DecoratorRelation relation, float distance, bool lineOfSight) => relation switch
    {
        DecoratorRelation.Ally or DecoratorRelation.Enemy => distance <= AllyRange && lineOfSight,
        _ => false,
    };

    /// <summary>The decorator's scale by distance (ours): full size within 10 m, down to 0.55 at <see cref="AllyRange"/>.</summary>
    public static float PlayerScale(float distance) => 1f - 0.45f * Math.Clamp((distance - 10f) / (AllyRange - 10f), 0f, 1f);

    /// <summary>The decorator's fade near the edge of its range (ours): 1 up to 85 % of the range, then to 0.</summary>
    public static float PlayerAlpha(DecoratorRelation relation, float distance)
    {
        float range = AllyRange;
        return Math.Clamp((range - distance) / (range * 0.15f), 0f, 1f);
    }
}

/// <summary>
/// The original HUD art of the install (M44), read-only: the textures of <see cref="HudSprites"/> and every weapon picture from
/// <c>data/packages/ui_ingame.oppc</c> (DDS bytes, decoded by the client), and each multiplayer wargear file's
/// <c>ui_data/weapon_type</c> from <c>data/packages/wargear.oppc</c>. Nothing is written or cached.
/// </summary>
public sealed class UiArt
{
    public const string IngamePackage = "data/packages/ui_ingame.oppc";
    public const string WargearPackage = "data/packages/wargear.oppc";
    public const string CommonPackage = "data/packages/ui_common.oppc";

    readonly Dictionary<string, byte[]> _textures;
    readonly Dictionary<string, string> _weaponTypes;
    readonly Dictionary<string, string> _weaponNames;
    readonly Dictionary<string, string> _killedBy;

    public UiArt(IReadOnlyDictionary<string, byte[]> textures, IReadOnlyDictionary<string, string> weaponTypes, IReadOnlyDictionary<string, string>? weaponNames = null,
        IReadOnlyDictionary<string, string>? killedBy = null)
    {
        _killedBy = new Dictionary<string, string>(killedBy ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        _textures = new Dictionary<string, byte[]>(textures, StringComparer.OrdinalIgnoreCase);
        _weaponTypes = new Dictionary<string, string>(weaponTypes, StringComparer.OrdinalIgnoreCase);
        _weaponNames = new Dictionary<string, string>(weaponNames ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    public static UiArt Empty { get; } = new(new Dictionary<string, byte[]>(), new Dictionary<string, string>());

    public IReadOnlyCollection<string> TextureNames => _textures.Keys;

    /// <summary>A texture's DDS bytes by name (without <c>.dds</c>), or null.</summary>
    public byte[]? Texture(string name) => _textures.GetValueOrDefault(name);

    public bool Has(string texture) => _textures.ContainsKey(texture);

    /// <summary>The wargear file's <c>ui_data/weapon_type</c>, or null.</summary>
    public string? WeaponType(string wargear) => _weaponTypes.GetValueOrDefault(wargear);

    /// <summary>The wargear file's English display name (<c>ui_data/name</c> through the locale table: "Bolt Pistol", "Lascannon"), or null.</summary>
    public string? WeaponName(string wargear) => _weaponNames.GetValueOrDefault(wargear);

    /// <summary>The file's <c>ui_data/killed_by_weapon_type</c> when it sets one (perk blasts, the dreadnought's weapons), or null.</summary>
    public string? KilledByType(string wargear) => _killedBy.GetValueOrDefault(wargear);

    /// <summary>The kill feed picture's label for a wargear file (<see cref="HudRules.FeedLabel"/>), or null.</summary>
    public string? FeedLabel(string wargear) => wargear.Length == 0 ? null : HudRules.FeedLabel(wargear, WeaponType(wargear), KilledByType(wargear));

    /// <summary>The weapon picture's texture name for a wargear file (<see cref="HudRules.IconFor"/>), or null.</summary>
    public string? WeaponIcon(string wargear) => wargear.Length == 0 ? null : HudRules.IconFor(wargear, WeaponType(wargear), Has);

    /// <summary>
    /// Reads the HUD's textures (every DDS of <c>ui_ingame.oppc</c> whose name is a sprite texture or a 96 x 64 weapon picture
    /// candidate, i.e. not one of the other movies' <c>&lt;movie&gt;_i*</c> atlases) and the weapon types; read-only.
    /// </summary>
    public static UiArt Load(string gameDir)
    {
        var wanted = new HashSet<string>(HudSprites.All.Select(s => s.Texture), StringComparer.OrdinalIgnoreCase) { FeedIcons.Texture };
        var textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var stream = new FileStream(Path.Combine(gameDir, IngamePackage), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var archive = ObpkArchive.Open(stream))
            archive.ReadEntries(e => archive.GroupName(e) == "dds" && (wanted.Contains(e.FileName) || IsPictureName(e.FileName)), (e, d) => textures[e.FileName] = d.ToArray());
        // Fidelity gap 10: the world decorators' atlases (Decorator.gfx's bitmaps) live in the common UI package; missing ones only disable the decorator art.
        try
        {
            var decorator = new HashSet<string>(DecoratorSprites.Textures, StringComparer.OrdinalIgnoreCase);
            using var stream = new FileStream(Path.Combine(gameDir, CommonPackage), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = ObpkArchive.Open(stream);
            archive.ReadEntries(e => archive.GroupName(e) == "dds" && decorator.Contains(e.FileName), (e, d) => textures[e.FileName] = d.ToArray());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var killedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nameIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var stream = new FileStream(Path.Combine(gameDir, WargearPackage), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var archive = ObpkArchive.Open(stream))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && (CameraTuning.IsMultiplayerWargear(e.FileName) || e.FileName.StartsWith("mp_perk_", StringComparison.OrdinalIgnoreCase)), (e, d) =>
            {
                BafDocument doc;
                // A perk file only adds the kill feed's picture type: one that does not parse must not blank the whole HUD art. (An MP wargear file did before this and still ends the load.)
                if (!CameraTuning.IsMultiplayerWargear(e.FileName)) { try { doc = BafDocument.Read(d); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException) { return; } }
                else doc = BafDocument.Read(d);
                if (doc.Root.Table("ui_data") is not { } ui) return;
                if (ui.String("killed_by_weapon_type") is { Length: > 0 } feed) killedBy[e.FileName] = feed;
                if (!CameraTuning.IsMultiplayerWargear(e.FileName)) return;
                if (ui.String("weapon_type") is { Length: > 0 } type) types[e.FileName] = type;
                if (CatalogData.IntOf(ui, "name") is > 0 and var id) nameIds[e.FileName] = id;
            });
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (nameIds.Count > 0 && CatalogData.ReadLocale(gameDir, "english", []) is { } locale)
            foreach (var (wargear, id) in nameIds)
                if (CatalogData.Text(locale, id) is { } text) names[wargear] = text.Trim();
        return new UiArt(textures, types, names, killedBy);
    }

    /// <summary>Picture names: no Scaleform atlas (<c>name_i1f</c>), no <c>.png</c> leftovers.</summary>
    static bool IsPictureName(string name) =>
        !name.Contains(".png", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(name, @"_[if][0-9a-f]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
