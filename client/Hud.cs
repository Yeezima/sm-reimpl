using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// The in-game HUD of the networked view, rebuilt after the original multiplayer HUD in M44 (layout measured on the owner's gameplay
/// footage, art from the install): the armour and health bar top centre (the cyan armour glow and the red health fill over the dark
/// ticked bar, the medallion at its left end), reward lines under it, the kill feed top right with weapon silhouettes, the team score
/// panel bottom left (side emblem, own and enemy score bars, the score limit in large digits, the time left above, Seize Ground's
/// point emblems), the weapon box bottom right (reserve in large digits, the weapon's picture, clip ticks, the charge mark and heat bar,
/// the winged skull on its top edge; the grenade box at its left, the jump pack meter tucked under it), the circle-and-ticks crosshair,
/// hit markers, the weapon-switch cards (pistol above the crosshair, primary at its right, the one in hand in the orange frame) and
/// Seize Ground's objective markers (clamped to the screen edges when off-screen).
/// <para>
/// The pictures are the original's (<see cref="HudArt"/>: <c>ui_ingame.oppc</c>, <see cref="HudSprites"/>); frames, fonts (Bahnschrift
/// for the original's "Bourgeois"), the crosshair and everything the art lacks are drawn by us. Everything lives in a 1080-high virtual
/// space scaled by the window height; the panels are anchored to the corners (so ultrawide windows keep them in the corners).
/// **Layout: after the original (ours, measured); art: original asset loaded; frames and fonts: placeholder.** <see cref="NetworkView"/>
/// fills <see cref="Data"/> each frame; the HUD itself keeps only timers and the kill feed.
/// </para>
/// </summary>
public sealed partial class Hud : Control
{
    /// <summary>A player's or point's relation to the viewer: none (no teams / neutral), own side (gold) or enemy (red).</summary>
    public enum Rel { None, Own, Enemy }

    /// <summary>One kill-feed line: killer (empty for the level), weapon tag, victim; <paramref name="Wargear"/> names the weapon's file for its picture.</summary>
    public sealed record Kill(string Killer, Rel KillerRel, string Weapon, string Victim, Rel VictimRel, string Suffix = "", string Wargear = "");

    /// <summary>One Seize Ground control point as the HUD needs it; <see cref="Screen"/> is in viewport pixels.</summary>
    public sealed class Point
    {
        public string Letter = "";
        public Rel Owner;
        public Team OwnerTeam;
        public Rel Side;
        public float Progress;
        public bool Contested;
        public Vector2 Screen;
        public bool Behind;
        public float Distance;
    }

    /// <summary>What the HUD shows this frame; replaced wholesale by <see cref="NetworkView"/> every frame.</summary>
    public sealed class Frame
    {
        public bool InGame, Alive, Spectating;
        public float RespawnLeft;
        public float Health, HealthMax = 1f, Shield, ShieldMax = 1f;
        // Weapon in hand.
        public bool HasWeapon;
        public string WeaponName = "";
        /// <summary>The weapon's wargear file (its picture); empty when unknown.</summary>
        public string WeaponWargear = "";
        public int Clip, ClipMax, Reserve, ReserveMax;
        public bool HasHeat, Overheated, HasCharge;
        /// <summary>The weapon in hand is venting its heat (fidelity gap 7): the heat bar pulses pale (ours).</summary>
        public bool Venting;
        public float Heat, HeatMax = 1f;
        public string WeaponState = "";
        public int SlotKey;
        public bool HasOther;
        public string OtherName = "", OtherWargear = "";
        public int OtherClip, OtherClipMax, OtherReserve;
        /// <summary>Protocol 44 (Weapon Versatility): the second holstered weapon (<see cref="Loadouts.Third"/>), when the player has three.</summary>
        public bool HasThird;
        public string ThirdName = "", ThirdWargear = "";
        public int ThirdClip, ThirdClipMax, ThirdReserve;
        /// <summary>Charge fill 0..1 or negative when not charging, and whether a release fires the charged shot (the bar overlay and charge mark turn gold). <see cref="ChargeText"/> is not drawn since M44 (changes-from-original §3).</summary>
        public float Charge = -1f;
        public string ChargeText = "";
        public bool ChargeReady;
        // Equipment (thrown item).
        public bool HasGrenade;
        public string GrenadeName = "", GrenadeWargear = "";
        public int Grenades;
        public float GrenadeCooldown;
        public bool Throwing;
        /// <summary>perks/inert-2: seconds the combat stims still work (0: none).</summary>
        public float StimLeft;
        // Jump pack.
        public bool HasJump;
        public float JumpPower, JumpMax = 1f;
        /// <summary>Less power than a take-off costs.</summary>
        public bool JumpLow;
        /// <summary>The jump's phase in words ("rising", "pound" ...), empty on the ground.</summary>
        public string JumpPhase = "";
        public string JumpWargear = "";
        public string GrenadesOutText = "";
        /// <summary>Not drawn since M44: the original's weapon box shows no melee name (changes-from-original).</summary>
        public string MeleeName = "";
        // Match.
        public Team OwnTeam;
        public bool Teams;
        public int OwnScore, EnemyScore, ScoreLimit;
        public string Clock = "";
        /// <summary>Centre banner: warm-up or match over; empty while playing.</summary>
        public string Banner = "";
        public bool Seize;
        public readonly List<Point> Points = new();
        /// <summary>Fidelity gap 10: the world decorators over other players this frame (<see cref="Decorator"/>).</summary>
        public readonly List<Decorator> Decorators = new();
        /// <summary>ui/hud-fidelity: the interaction prompt ("Pick up Ammo"; empty: none) and its key, drawn by the HUD from the movie's prompt (NetworkView.Pickups.cs fills them).</summary>
        public string Prompt = "", PromptKey = "E";
        // Status.
        public string Status = "";
        public bool StatusIsError;
        // Crosshair.
        /// <summary>weapons/spread: how far (virtual units) the crosshair's ticks move out for the next shot's scatter; 0 without spread.</summary>
        public float SpreadRadius;
        /// <summary>feel/scope-lascannon: the scope overlay (stalker bolter, lascannon) is on screen: it has its own reticle, so the normal crosshair is not drawn.</summary>
        public bool Scoped;
        /// <summary>ui/hud-fidelity: how far the next shot's scatter is between the stance's narrowest and widest (0..1, <see cref="ReticleRules.SpreadFraction"/>): picks the reticle's spread frame.</summary>
        public float SpreadFraction;
        /// <summary>ui/hud-fidelity: aim mode (the reticle's <c>min_aim</c>..<c>max_aim</c> range) and the heavy bolter's brace (its reticle's <c>alt_fire</c> clip).</summary>
        public bool Aiming, Braced;
        public bool AimedAtEnemy;
        public string AimedName = "";
        /// <summary>Protocol 44 (Tactical Readout / Signum Link): the aimed enemy's bar is shown, from <see cref="AimedHealth"/> and <see cref="AimedShield"/> (fractions of 1). Without the perks nothing shows over an enemy: no name, no bar.</summary>
        public bool AimedBar;
        /// <summary>The aimed enemy's health and shield after the last hit on it (HUD units), or negative when not known (shown under its name).</summary>
        public float AimedHealth = -1f, AimedShield = -1f, AimedHealthMax = 1f, AimedShieldMax = 1f;
    }

    /// <summary>The current frame's data (set by <see cref="NetworkView"/>).</summary>
    public Frame Data = new();
    /// <summary>Shows the "F1: info" hint (fades after <see cref="HintSeconds"/> s of being connected).</summary>
    public bool ShowHint;
    /// <summary>The install the art is read from (set before the HUD enters the tree; null: placeholder shapes only).</summary>
    public string? GameDir { get; init; }

    const float RefHeight = 1080f;
    const double FeedSeconds = 6.0;
    const int FeedEntries = 5;
    const double EventSeconds = 3.5;
    const double PopupSeconds = 2.2;
    const double HintSeconds = 5.0;
    // Colours sampled from the reference frames (ours).
    static readonly Color Gold = new(0.98f, 0.8f, 0.12f);
    static readonly Color FeedGold = new(0.93f, 0.76f, 0.24f);
    static readonly Color ClockGold = new(0.86f, 0.74f, 0.32f);
    static readonly Color RedText = new(0.9f, 0.08f, 0.07f);
    static readonly Color RedFill = new(0.86f, 0.04f, 0.04f);
    static readonly Color NeutralGrey = new(0.62f, 0.62f, 0.64f);
    static readonly Color FrameGold = new(0.74f, 0.64f, 0.32f, 0.95f);
    static readonly Color PanelFill = new(0.02f, 0.02f, 0.025f, 0.84f);
    static readonly Color White = new(1f, 1f, 1f);
    static readonly Color DigitWhite = new(0.93f, 0.93f, 0.95f);
    /// <summary>Tint that turns the gold emblems red (enemy) or grey (neutral).</summary>
    static readonly Color EnemyTint = new(1f, 0.22f, 0.18f), NeutralTint = new(0.62f, 0.62f, 0.64f);

    readonly List<(Kill Kill, double At)> _feed = new();
    /// <summary>One reward line (ui/hud-2): its text for the M44 drawing, when it began, and the reward it is (null for a line the rules do not know).</summary>
    sealed record EventLine(string Text, double At, RewardSpec? Spec = null);

    readonly List<EventLine> _events = new();
    readonly RewardTracker _rewards = new();
    FontVariation? _font, _fontLight, _fontBold;
    HudArt? _art;
    double _time, _hitAt = double.NegativeInfinity, _popupAt = double.NegativeInfinity, _hintFor;
    bool _hitKill, _hitHead;
    int _lastSlotKey = -1;

    public Hud()
    {
        Name = "Hud";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Ready()
    {
        // The original's HUD font is "XBourgeoix OT" (data/config/fonts.xml; its glyphs exist only as a Scaleform font atlas);
        // Bahnschrift (Windows) is the closest system face we found: condensed, square, thin digits. Fallback: Godot's default.
        var system = new SystemFont { FontNames = ["Bahnschrift", "DIN Alternate", "Arial Narrow"], Fallbacks = [ThemeDB.FallbackFont] };
        var ts = TextServerManager.GetPrimaryInterface();
        FontVariation Variant(int weight, int width) => new()
        {
            BaseFont = system,
            VariationOpentype = new Godot.Collections.Dictionary { { ts.NameToTag("wght"), weight }, { ts.NameToTag("wdth"), width } },
        };
        _font = Variant(450, 92);
        _fontLight = Variant(330, 88);
        _fontBold = Variant(600, 92);
        _art = new HudArt(GameDir);
        MovieReady();   // ui/hud-fidelity (Hud.Movie.cs)
    }

    /// <summary>
    /// Flash of four diagonal ticks around the crosshair: white, red on a kill, a warm white for a head hit. The client calls it
    /// for every hit report of the local player (<c>GameClient.TakeHits</c>).
    /// </summary>
    public void ShowHitMarker(bool kill, bool head = false)
    {
        _hitAt = _time;
        _hitKill = kill;
        _hitHead = head && !kill;
        if (kill && head) { _headKillAt = _time; _headKillAimed = Data.AimedName; }   // ui/hud-2: a killing head hit and who was under the crosshair, for the headshot reward line
    }

    double _headKillAt = double.NegativeInfinity;
    string _headKillAimed = "";

    /// <summary>Opens the weapon-switch cards (the HUD also opens them itself when the weapon in hand changes).</summary>
    public void ShowSwitchPopup() => _popupAt = _time;

    /// <summary>Adds a kill-feed line (the newest at the bottom; at most <see cref="FeedEntries"/> for <see cref="FeedSeconds"/> s).</summary>
    public void AddKill(Kill kill)
    {
        _feed.Add((kill, _time));
        while (_feed.Count > FeedEntries) _feed.RemoveAt(0);
        NoteKillForRewards(kill);
    }

    /// <summary>
    /// Adds the reward lines of an own kill ("+ Kill P3": the client's call for every kill by the own player): the kill's own line and, from the kills the HUD has seen (<see cref="RewardTracker"/>,
    /// ui/hud-2), the headshot, revenge, avenger, grudge, victory in death, double to mega kill and killstreak lines it earned. Any other text is shown as it is.
    /// </summary>
    public void AddEvent(string text)
    {
        const string kill = "+ Kill ";
        if (text.StartsWith(kill, StringComparison.Ordinal) || text == "+ Kill")
        {
            string victim = text.Length > kill.Length ? text[kill.Length..] : "";
            // A headshot belongs to the one the head hit was on: the player under the crosshair then (when the HUD knew one), and the hit counts once.
            bool head = _time - _headKillAt <= 1.0 && (_headKillAimed.Length == 0 || victim.Length == 0 || _headKillAimed.Contains(victim, StringComparison.Ordinal));
            if (head) _headKillAt = double.NegativeInfinity;
            foreach (var earned in _rewards.OwnKill(victim, _time, head, Data.Alive))
                _events.Add(new EventLine(earned.Spec == RewardTable.Kill ? text : "+ " + earned.Spec.Name, _time, earned.Spec));
        }
        else _events.Add(new EventLine(text, _time));
        while (_events.Count > 4) _events.RemoveAt(0);
    }

    public override void _Process(double delta)
    {
        _time += delta;
        if (Data.InGame && ShowHint) _hintFor += delta;
        _feed.RemoveAll(f => _time - f.At > FeedSeconds);
        _events.RemoveAll(e => _time - e.At > EventSeconds);
        TrackRewards();
        RunRewardsDemo();
        TrackPoints();
        MovieProcess();
        if (Data.InGame)
        {
            if (_lastSlotKey >= 0 && Data.SlotKey != _lastSlotKey && Data.HasOther) _popupAt = _time;
            _lastSlotKey = Data.SlotKey;
        }
        QueueRedraw();
    }

    // ---------------------------------------------------------------- drawing helpers

    float _scale = 1f;
    Transform2D _base = Transform2D.Identity;

    Color NameColour(Rel r) => r switch { Rel.Own => FeedGold, Rel.Enemy => RedText, _ => White };

    static Color PointColour(Rel r) => r switch { Rel.Own => Gold, Rel.Enemy => RedText, _ => NeutralGrey };

    FontVariation? FontOf(int style) => style switch { 1 => _fontLight, 2 => _fontBold, _ => _font };

    float TextWidth(string s, int size, int style = 0) => FontOf(style) is { } f ? f.GetStringSize(s, HorizontalAlignment.Left, -1, size).X : 0f;

    /// <summary>Text with a dark outline; (x, y) is the baseline: left, centre (0) or right (1) aligned; style 0 regular, 1 light, 2 bold.</summary>
    void Txt(string s, float x, float y, int size, Color c, int align = -1, int outline = 4, int style = 0)
    {
        if (FontOf(style) is not { } font || s.Length == 0) return;
        float w = TextWidth(s, size, style);
        float px = align < 0 ? x : align == 0 ? x - w / 2f : x - w;
        var pos = new Vector2(px, y);
        if (outline > 0) DrawStringOutline(font, pos, s, HorizontalAlignment.Left, -1, size, outline, new Color(0f, 0f, 0f, c.A * 0.8f));
        DrawString(font, pos, s, HorizontalAlignment.Left, -1, size, c);
    }

    /// <summary>Draws in a local frame placed by <paramref name="local"/> (local → virtual); <see cref="EndLocal"/> returns to the virtual space.</summary>
    void BeginLocal(Transform2D local) { _localXf = local; DrawSetTransformMatrix(_base * local); }

    void EndLocal() { _localXf = Transform2D.Identity; DrawSetTransformMatrix(_base); }

    static Transform2D Placed(Vector2 origin, float degrees) => new(Mathf.DegToRad(degrees), origin);

    /// <summary>
    /// Draws an art sprite into <paramref name="dest"/>, cut to the left <paramref name="clip"/> (0..1) of its width; false when the art
    /// is not there (the caller then draws its placeholder).
    /// </summary>
    bool Sprite(UiSprite s, Rect2 dest, Color? modulate = null, float clip = 1f)
    {
        if (_art?.Texture(s.Texture) is not { } tex) return false;
        float f = Math.Clamp(clip, 0f, 1f);
        if (f <= 0f) return true;
        DrawTextureRectRegion(tex, new Rect2(dest.Position, new Vector2(dest.Size.X * f, dest.Size.Y)), new Rect2(s.X, s.Y, s.Width * f, s.Height), modulate ?? White);
        return true;
    }

    /// <summary>A sprite centred on <paramref name="centre"/> at <paramref name="scale"/> virtual units per texture pixel.</summary>
    bool SpriteAt(UiSprite s, Vector2 centre, float scale, Color? modulate = null) =>
        Sprite(s, new Rect2(centre - new Vector2(s.Width, s.Height) * scale / 2f, new Vector2(s.Width, s.Height) * scale), modulate);

    /// <summary>A whole weapon picture (or its white silhouette) centred on <paramref name="centre"/>; false when there is none.</summary>
    bool WeaponPicture(string wargear, Vector2 centre, float scale, Color modulate, bool silhouette = false)
    {
        if (wargear.Length == 0 || _art?.WeaponIcon(wargear) is not { } name) return false;
        var tex = silhouette ? _art.Silhouette(name) : _art.Texture(name);
        if (tex is null) return false;
        var size = tex.GetSize() * scale;
        DrawTextureRect(tex, new Rect2(centre - size / 2f, size), false, modulate);
        return true;
    }

    void Poly(Vector2[] pts, Color fill, Color? border = null, float width = 2f)
    {
        DrawColoredPolygon(pts, fill);
        if (border is { } b)
        {
            var closed = new Vector2[pts.Length + 1];
            Array.Copy(pts, closed, pts.Length);
            closed[^1] = pts[0];
            DrawPolyline(closed, b, width, true);
        }
    }

    /// <summary>A box with two chamfered corners (top right, bottom left), like the original's weapon and grenade boxes.</summary>
    static Vector2[] Chamfered(float x, float y, float w, float h, float cut) =>
        [new(x, y), new(x + w - cut, y), new(x + w, y + cut), new(x + w, y + h), new(x + cut, y + h), new(x, y + h - cut)];

    static Vector2[] Slanted(float x, float y, float w, float h, float slant) =>
        [new(x + slant, y), new(x + w, y), new(x + w - slant, y + h), new(x, y + h)];

    void Star(Vector2 c, float r, int points, float inner, float rotation, Color col)
    {
        var pts = new Vector2[points * 2];
        for (int i = 0; i < pts.Length; i++)
        {
            float a = rotation + i * MathF.PI / points;
            float rr = i % 2 == 0 ? r : r * inner;
            pts[i] = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rr;
        }
        DrawColoredPolygon(pts, col);
    }

    /// <summary>The Imperial aquila, simplified (placeholder when the art is missing).</summary>
    void AquilaShape(Vector2 c, float w, Color col)
    {
        float h = w * 0.55f;
        DrawCircle(c + new Vector2(0, -h * 0.28f), h * 0.2f, col);
        Poly([c + new Vector2(0, -h * 0.1f), c + new Vector2(h * 0.17f, h * 0.15f), c + new Vector2(0, h * 0.5f), c + new Vector2(-h * 0.17f, h * 0.15f)], col);
        for (int side = -1; side <= 1; side += 2)
            for (int i = 0; i < 4; i++)
            {
                float len = w * (0.46f - 0.07f * i);
                float y0 = -h * 0.28f + i * h * 0.19f;
                float x0 = side * w * 0.07f;
                float th = h * 0.12f;
                Poly([c + new Vector2(x0, y0), c + new Vector2(x0 + side * len, y0 + h * 0.16f), c + new Vector2(x0 + side * len, y0 + h * 0.16f + th), c + new Vector2(x0, y0 + th)], col);
            }
    }

    /// <summary>
    /// A side's emblem centred on <paramref name="c"/>, <paramref name="height"/> virtual units tall: the original's gold Chaos star or
    /// aquila (tinted red for the enemy, grey for neutral), else a placeholder shape.
    /// </summary>
    void SideEmblem(Team team, Vector2 c, float height, Rel rel, float alpha = 1f)
    {
        var tint = rel switch { Rel.Enemy => EnemyTint, Rel.None => NeutralTint, _ => White };
        tint.A = alpha;
        var sprite = team == Team.Chaos ? HudSprites.ChaosStar : HudSprites.Aquila;
        // The aquila's picture is wide and flat: it is drawn at the width the star's height gives (both read as the same size in the footage).
        float scale = team == Team.Chaos ? height / sprite.Height : height * 1.25f / sprite.Width;
        if (SpriteAt(sprite, c, scale, tint)) return;
        var col = PointColour(rel);
        if (team == Team.Chaos)
        {
            Star(c, height * 0.5f, 8, 0.45f, -MathF.PI / 2f, col);
            DrawArc(c, height * 0.19f, 0f, MathF.Tau, 20, new Color(0f, 0f, 0f, 0.75f), MathF.Max(1.5f, height * 0.06f), true);
        }
        else AquilaShape(c, height * 1.25f, col);
    }

    void Skull(Vector2 c, float r, Color col)
    {
        DrawCircle(c, r, col);
        DrawRect(new Rect2(c.X - r * 0.55f, c.Y + r * 0.55f, r * 1.1f, r * 0.8f), col);
        var dark = new Color(0f, 0f, 0f, 0.85f);
        DrawCircle(c + new Vector2(-r * 0.38f, -r * 0.05f), r * 0.26f, dark);
        DrawCircle(c + new Vector2(r * 0.38f, -r * 0.05f), r * 0.26f, dark);
    }

    // ---------------------------------------------------------------- draw

    public override void _Draw()
    {
        _adds.Clear();
        if (_font is null) return;
        _scale = Size.Y / RefHeight;
        if (_scale <= 0f) return;
        _base = new Transform2D(0f, new Vector2(_scale, _scale), 0f, Vector2.Zero);
        DrawSetTransformMatrix(_base);
        var vs = Size / _scale;
        var d = Data;

        DrawStatus(vs);
        if (!d.InGame) { DrawHint(); FlushAdditive(); return; }
        DrawPlayerDecorators(vs);   // fidelity gap 10: under the panels
        DrawVitals(vs);
        DrawEvents(vs);
        DrawKillFeed(vs);
        DrawScorePanel(vs);
        DrawWeaponBox(vs);
        DrawObjectives(vs);
        DrawCentre(vs);
        if (d.Alive && !d.Spectating) DrawMoviePrompt(vs);   // ui/hud-fidelity: the pick-up prompt
        DrawHint();
        FlushAdditive();   // ui/hud-fidelity: the movies' added pieces, this frame
    }

    /// <summary>Connection status, rejection and map errors: a centred banner (visible without the info overlay).</summary>
    void DrawStatus(Vector2 vs)
    {
        if (Data.Status.Length == 0) return;
        // fix/connect-timeout: a long reason (a refusal's detail) wraps onto more lines instead of running off the box.
        float maxText = Math.Max(200f, vs.X - 160f);
        var lines = new List<string>();
        string line = "";
        foreach (var word in Data.Status.Split(' '))
        {
            string tried = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && TextWidth(tried, 30) > maxText) { lines.Add(line); line = word; }
            else line = tried;
        }
        if (line.Length > 0) lines.Add(line);
        float widest = 0f;
        foreach (var l in lines) widest = Math.Max(widest, TextWidth(l, 30));
        float w = Math.Min(vs.X - 80f, widest + 80f);
        var box = new Rect2(vs.X / 2f - w / 2f, vs.Y * 0.4f, w, 52f + 38f * lines.Count);
        var col = Data.StatusIsError ? RedText : Gold;
        DrawRect(box, new Color(0.03f, 0.03f, 0.03f, 0.85f));
        DrawRect(box, col, false, 3f);
        Txt(Data.StatusIsError ? "ERROR" : "STATUS", vs.X / 2f, box.Position.Y + 30f, 20, col, 0, 0, 2);
        for (int i = 0; i < lines.Count; i++) Txt(lines[i], vs.X / 2f, box.Position.Y + 68f + 38f * i, 30, White, 0, 3);
    }

    void DrawHint()
    {
        if (!ShowHint || _hintFor > HintSeconds) return;
        float a = (float)Math.Clamp(HintSeconds - _hintFor, 0.0, 1.0) * 0.8f;
        Txt("F1: info", 24f, 32f, 18, new Color(0.7f, 0.7f, 0.7f, a), -1, 2);
    }

    /// <summary>
    /// The armour and health bar, top centre. Measured on the footage (800 x 450 frames of a 1600 x 900 view): the cyan outline runs from
    /// x 287 to 510 and y 53 to 66, the medallion's centre at (283, 59). The cyan glow (<c>hud_i215</c>) is the <b>armour</b>: it shortens
    /// from the right as the armour drops (the low-health frame shows it cut at 70 % with the red fill full); the red fill (<c>hud_i223</c>)
    /// is the health.
    /// </summary>
    void DrawVitals(Vector2 vs)
    {
        var d = Data;
        float cx = vs.X / 2f;
        if (DrawMovieVitals(vs)) { DrawDeathText(cx); return; }   // ui/hud-2: the movie's own bar (Hud.Vitals.cs); the M44 bar below with --old-hud or without the movie
        float hf = Math.Clamp(d.Health / MathF.Max(d.HealthMax, 1f), 0f, 1f);
        float sf = Math.Clamp(d.Shield / MathF.Max(d.ShieldMax, 1f), 0f, 1f);
        float pulse = hf < 0.25f && d.Alive ? 0.7f + 0.3f * MathF.Sin((float)_time * 9f) : 1f;
        // The art's scale (virtual units per texture pixel): wider than tall in the footage.
        const float sx = 1.334f, sy = 1.53f;
        var tl = new Vector2(cx - 276.5f, 116.5f);
        Rect2 R(float tx, float ty, float tw, float th) => new(tl + new Vector2(tx * sx, ty * sy), new Vector2(tw * sx, th * sy));
        var medallion = new Vector2(cx - 280.8f, 141.6f);
        if (Sprite(HudSprites.BarBack, R(4, 4, 404, 28)))
        {
            if (d.Alive) Sprite(HudSprites.BarHealth, R(12, 10, 376, 16), new Color(pulse, pulse, pulse), hf);
            // The armour glow's lines run from texture x 4 to 405: cut it there, with a white cap at the cut (as in the footage).
            float cut = (4f + sf * 401f) / 412f;
            if (d.Alive && sf > 0f) Sprite(HudSprites.BarArmour, R(0, 0, 412, 36), null, sf >= 0.999f ? 1f : cut);
            if (d.Alive && sf > 0.005f && sf < 0.999f)
            {
                float x = tl.X + cut * 412f * sx;
                DrawLine(new Vector2(x, tl.Y + 5f * sy), new Vector2(x, tl.Y + 10f * sy), new Color(1f, 1f, 1f, 0.95f), 4f);
                DrawLine(new Vector2(x, tl.Y + 24f * sy), new Vector2(x, tl.Y + 29f * sy), new Color(1f, 1f, 1f, 0.95f), 4f);
            }
            if (!Sprite(HudSprites.Medallion, new Rect2(medallion - new Vector2(26f, 25.5f), new Vector2(52f, 51f)), d.Alive && sf > 0f ? null : new Color(0.55f, 0.6f, 0.6f)))
                Star(medallion, 24f, 8, 0.62f, MathF.PI / 8f, new Color(0.5f, 1f, 0.9f));   // placeholder when the medallion's atlas is missing
        }
        else
        {
            // Placeholder: a dark slanted bar with a cyan outline for the armour and the red health fill.
            float w = 535f, h = 30f, slant = 16f, x = tl.X + 5f, y = tl.Y + 12f;
            Poly([new(x, y), new(x + w, y), new(x + w - slant, y + h), new(x, y + h)], new Color(0.02f, 0.02f, 0.02f, 0.82f));
            if (d.Alive)
            {
                DrawRect(new Rect2(x + 6f, y + 9f, (w - 30f) * hf, h - 18f), new Color(RedFill.R * pulse, RedFill.G, RedFill.B));
                if (sf > 0f) DrawPolyline([new(x, y), new(x + w * sf, y)], new Color(0.4f, 1f, 0.9f), 3f);
                if (sf > 0f) DrawPolyline([new(x, y + h), new(x + (w - slant) * sf, y + h)], new Color(0.4f, 1f, 0.9f), 3f);
            }
            Star(medallion, 24f, 8, 0.62f, MathF.PI / 8f, new Color(0.5f, 1f, 0.9f));
        }
        // The medallion's red diamond (not in the atlas: drawn by us after the footage).
        var dark = new Color(0.25f, 0f, 0f, 0.95f);
        Poly([medallion + new Vector2(0, -13), medallion + new Vector2(13, 0), medallion + new Vector2(0, 13), medallion + new Vector2(-13, 0)], dark);
        Poly([medallion + new Vector2(0, -10), medallion + new Vector2(10, 0), medallion + new Vector2(0, 10), medallion + new Vector2(-10, 0)], new Color(0.9f, 0.08f, 0.06f));
        DrawCircle(medallion, 3.2f, new Color(0.3f, 0f, 0f));
        DrawDeathText(cx);
    }

    /// <summary>Dead: the respawn countdown (the M23 death screen draws the vignette and camera).</summary>
    void DrawDeathText(float cx)
    {
        var d = Data;
        if (d.Alive) return;
        Txt("KILLED", cx, 330f, 52, RedText, 0, 6, 2);
        Txt($"RESPAWN IN {MathF.Ceiling(d.RespawnLeft):F0}", cx, 380f, 28, White, 0, 4);
    }

    /// <summary>
    /// Reward lines under the bar (the footage: "+185 [medals] Savior", white, centred at y 222 virtual): our kill line with the original's
    /// skull medal; the warm-up / match-over banner below them.
    /// </summary>
    void DrawEvents(Vector2 vs)
    {
        var d = Data;
        float cx = vs.X / 2f + 10f;
        float y = 222f;
        bool movie = DrawMovieRewards(vs);   // ui/hud-fidelity: the movie's reward lines (Hud.Fidelity.cs)
        if (movie) y = 250f + 49f * _events.Count;   // under the movie's reward lines (32.65 stage pixels apart)
        if (!movie)
        foreach (var (text, at, _) in _events)
        {
            float a = (float)Math.Clamp(EventSeconds - (_time - at), 0.0, 0.6) / 0.6f;
            string line = text.StartsWith("+ ", StringComparison.Ordinal) ? text[2..] : text;
            float tw = TextWidth(line, 28, 2);
            float x = cx - (tw + 52f) / 2f;
            if (!SpriteAt(HudSprites.KillMedal, new Vector2(x + 20f, y - 10f), 1.15f, new Color(1f, 1f, 1f, a))) Skull(new Vector2(x + 20f, y - 12f), 13f, new Color(Gold, a));
            Txt(line, x + 52f, y, 28, new Color(1f, 1f, 1f, a), -1, 4, 2);
            y += 44f;
        }
        if (d.Banner.Length > 0) Txt(d.Banner, vs.X / 2f, d.Alive ? Math.Max(y + 12f, 275f) : 430f, 28, Gold, 0, 5, 2);
    }

    /// <summary>
    /// Kill feed, top right (footage: "killer [white weapon silhouette] victim", own side gold, enemy red, right edge 187 virtual units from
    /// the right, first baseline at 127). The picture is the feed movie's own weapon icon (white with blood splatter, fidelity gap 10: <c>HudEventLog.gfx</c>);
    /// without it the weapon's HUD picture drawn white, and the tag text when there is none.
    /// </summary>
    void DrawKillFeed(Vector2 vs)
    {
        if (DrawMovieKillFeed(vs)) return;   // ui/hud-fidelity: the feed movie's layout (Hud.Fidelity.cs)
        float xr = vs.X - 187f, y = 127f;
        const int size = 22;
        foreach (var (k, at) in _feed)
        {
            float a = (float)Math.Clamp(FeedSeconds - (_time - at), 0.0, 1.0);
            var vc = NameColour(k.VictimRel); vc.A = a;
            string victim = k.Victim + k.Suffix;
            Txt(victim, xr, y, size, vc, 1, 3, 2);
            float x = xr - TextWidth(victim, size, 2) - 10f;
            if (k.Weapon.Length > 0 || k.Wargear.Length > 0)
            {
                const float iconW = 56f;
                // Fidelity gap 10: the feed movie's own pictures (white silhouette with blood), 48 units square; else the HUD picture's silhouette; else the name.
                const float feedScale = 0.85f;   // 41 units: about the icon's height in the footage (ours)
                if (FeedPicture(k.Wargear, new Vector2(x - FeedIcons.Size * feedScale / 2f, y - 8f), feedScale, new Color(1f, 1f, 1f, 0.95f * a))) x -= FeedIcons.Size * feedScale + 8f;
                else if (WeaponPicture(k.Wargear, new Vector2(x - iconW / 2f, y - 8f), iconW / HudSprites.IconWidth, new Color(1f, 1f, 1f, 0.95f * a), silhouette: true)) x -= iconW + 10f;
                else
                {
                    float pw = TextWidth(k.Weapon, 16) + 18f;
                    DrawRect(new Rect2(x - pw, y - 19f, pw, 24f), new Color(0.06f, 0.06f, 0.06f, 0.7f * a));
                    Txt(k.Weapon, x - pw / 2f, y - 2f, 16, new Color(0.92f, 0.92f, 0.92f, a), 0, 2);
                    x -= pw + 10f;
                }
            }
            if (k.Killer.Length > 0)
            {
                var kc = NameColour(k.KillerRel); kc.A = a;
                Txt(k.Killer, x, y, size, kc, 1, 3, 2);
            }
            y += 34f;
        }
    }

    /// <summary>Crosshair at the exact screen centre, the aimed enemy's name, the hit marker and the weapon-switch cards.</summary>
    void DrawCentre(Vector2 vs)
    {
        var d = Data;
        if (!d.Alive || d.Spectating) return;
        var c = vs / 2f;
        var col = d.AimedAtEnemy ? new Color(0.95f, 0.1f, 0.08f) : new Color(1f, 1f, 1f, 0.9f);
        var shadow = new Color(0f, 0f, 0f, 0.55f);
        const float r = 18f;
        // ui/hud-fidelity: the original reticle (Hud.Movie.cs); the M44 circle with --old-hud or without the movie.
        if (!d.Scoped && (OldHud || !DrawMovieReticle(c)))   // feel/scope-lascannon: the scope overlay has its own reticle (the name, hit marker and cards below stay)
        {
        DrawArc(c, r, 0f, MathF.Tau, 40, shadow, 4f, true);
        DrawArc(c, r, 0f, MathF.Tau, 40, col, 1.8f, true);
        float open = Math.Clamp(float.IsFinite(d.SpreadRadius) ? d.SpreadRadius : 0f, 0f, 120f);   // weapons/spread: the ticks open with the scatter (ours)
        foreach (var dir in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
        {
            DrawLine(c + dir * (r - 5f + open), c + dir * (r + 6f + open), shadow, 4.5f);
            DrawLine(c + dir * (r - 4f + open), c + dir * (r + 5f + open), col, 2f);
        }
        }
        // The enemy under the crosshair (owner's decision of 2026-10-30): no name; with Tactical Readout or Signum Link, its health and shield as a thin
        // two-lane bar (from the server's bars message). Without either perk nothing shows over an enemy.
        if (d.AimedAtEnemy && d.AimedBar)
        {
            if (d.AimedHealth >= 0f)
            {
                float bw = 120f, bx = c.X - bw / 2f, by = c.Y - 54f;
                DrawRect(new Rect2(bx - 2f, by - 2f, bw + 4f, 13f), new Color(0.02f, 0.02f, 0.02f, 0.75f));
                DrawRect(new Rect2(bx, by, bw * Math.Clamp(d.AimedShield / MathF.Max(d.AimedShieldMax, 1f), 0f, 1f), 4f), new Color(0.5f, 1f, 0.92f));
                DrawRect(new Rect2(bx, by + 5f, bw * Math.Clamp(d.AimedHealth / MathF.Max(d.AimedHealthMax, 1f), 0f, 1f), 4f), RedFill);
            }
        }
        // Hit marker: four diagonal ticks.
        double since = _time - _hitAt;
        double life = _hitKill ? 0.55 : 0.28;
        bool movieMarker = !OldHud && since >= 0.0 && since < 1.0 && DrawMovieHitMarker(c, since);   // ui/hud-fidelity: the movie's hit_mc
        if (!movieMarker && since >= 0.0 && since < life)
        {
            float a = (float)(1.0 - since / life);
            var hc = _hitKill ? new Color(0.95f, 0.1f, 0.08f, a) : _hitHead ? new Color(1f, 0.9f, 0.45f, a) : new Color(1f, 1f, 1f, a);
            float r0 = 12f + (float)since * 20f, r1 = r0 + 12f;
            foreach (var dir in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) })
            {
                var n = dir.Normalized();
                DrawLine(c + n * r0, c + n * r1, new Color(0f, 0f, 0f, a * 0.6f), 5f);
                DrawLine(c + n * r0, c + n * r1, hc, 2.5f);
            }
        }
        DrawSwitchCards(vs / 2f);
    }
}
