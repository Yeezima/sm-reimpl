using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmContent.Customization;

namespace SmClient.Customizer;

/// <summary>
/// Phase 4d of the UI concept redesign (owner, 2026-11-15; panel 03, docs/ui-concept-textures.md "Phase 4d Armoury"): the creator drawn on the concept's Armoury. The page maps panel 03's own 780 x 440 screen onto the
/// window (scale min(h / 440, w / 780), centred); the painted overlay (title plate, the rail's frame) stands behind the live parts. The creator's own controls are moved, not replaced:
/// the steps (CLASS, WEAPONS, ...) are the pills in the rail's header bar; the class list is the four plate rows; the weapon row and the three tiles show the open loadout's weapon, equipment and perks (a press opens that step);
/// the step's choices are the cards of the right column (scrolling); CONFIRM LOADOUT saves and sets active (in a match: saves and deploys), BACK as before. DEFAULTS and CUSTOM 1 to 10, the faction row, the item card and the slot
/// actions (rename, copy, reset, clear, revert, save, set active, appearance, deploy, leave) keep their place around the preview and in a dock under it. <c>--old-armoury</c> restores the earlier layout. Ours.
/// </summary>
public sealed partial class CreatorView
{
    /// <summary>A class list row: label, icon art name, whether it is the open class and what a press does.</summary>
    public sealed record ArmClass(string Label, string? Icon, bool Selected, Action Press, string? Real = null);
    /// <summary>The weapon row and the three tiles: a label, an icon art name (or null), whether it is the step shown, and what a press does.</summary>
    public sealed record ArmTile(string Label, string? Icon, bool Selected, Action Press);
    public sealed record ArmDetail(string Weapon, string? WeaponIcon, Action WeaponPress, IReadOnlyList<ArmTile> Tiles, IReadOnlyList<ArmSlot>? Slots = null);
    /// <summary>One loadout slot's card (owner, 2026-11-15: one card per loadout slot): the slot's name, what it holds now, an icon art name and what a press does (opens that slot's choices).</summary>
    public sealed record ArmSlot(string Slot, string Value, string? Icon, Action Press);

    bool _arm;
    Control? _armCanvas;
    TextureRect? _armOverlay, _armTitle, _armWings, _armBadge;
    Label? _armBadgeText;
    Control? _armRows, _armWeapon, _armTilesHost, _armInfo, _armTopRow;
    Button? _armConfirm, _armBack, _armWeaponBtn;
    ScrollContainer? _armStripScroll;
    Button? _armDeploy;
    Panel? _armFrame;
    readonly List<Button> _armTools = new();
    readonly List<Button> _armTileButtons = new();
    string _armSig = "";
    float _armScale;
    Label? _armWeaponName;
    Control? _armSteps;
    Label? _armColumnTitle;
    Panel? _armWell;
    Button? _armTabClass, _armTabSlots, _armTabLook, _armPickBack;
    ScrollContainer? _armSlotScroll; VBoxContainer? _armSlotList;
    bool _armSlotsTab, _armPicking;
    TextureRect? _armWeaponIcon;

    static readonly Color ArmLight = new(0.88f, 0.87f, 0.83f), ArmDark = new(0.10f, 0.06f, 0.02f), ArmDim = new(0.72f, 0.70f, 0.64f);
    // One size per level, in the concept's pixels (times the window's scale): row names, tile labels, step pills, plates.
    const float ArmNameSize = 13.2f, ArmTileSize = 10.4f, ArmPillSize = 7.6f, ArmPlateSize = 9.4f, ArmConfirmSize = 19f, ArmBackSize = 13f, ArmBadgeSize = 13f;

    /// <summary>EXTRAS-HOOK: the class list's rows (an added mode puts its own class first).</summary>
    partial void ExtrasArmouryClasses(List<ArmClass> rows);
    /// <summary>EXTRAS-HOOK: the weapon row and tiles of an added page (null: the creator's own).</summary>
    partial void ExtrasArmouryDetail(ref ArmDetail? detail);
    /// <summary>EXTRAS-HOOK: the mode badge's text.</summary>
    partial void ExtrasArmouryBadge(ref string text);

    static bool ArmouryWanted() => ConceptArt.Get("overlay-armoury") is not null && Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-armoury") < 0;

    /// <summary>Shows the CLASS or the SLOTS tab of the Armoury (screenshot aid: <c>--arm-tab class|slots</c>).</summary>
    public void ShowArmTab(bool slots) { _armSlotsTab = slots; ArmouryDirty(); }

    /// <summary>Whether the concept layout is on (test aid).</summary>
    public bool ArmouryOn => _arm;

    /// <summary>Called at the end of the constructor: takes the controls out of the old layout and onto the concept canvas.</summary>
    void EnterArmoury(Control oldRoot, Control right, Control stepsPanel)
    {
        if (!ArmouryWanted()) return;
        _arm = true;
        oldRoot.Visible = false;
        _armCanvas = new Control { Name = "ArmouryCanvas", MouseFilter = MouseFilterEnum.Ignore };
        _armCanvas.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_armCanvas);
        TextureRect Tex(string name, string art, Control parent) { var t = new TextureRect { Name = name, Texture = ConceptArt.Get(art), MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.LinearWithMipmaps }; parent.AddChild(t); return t; }
        // The concept shows the battlefield blurred behind the rail and the cards: two panes that blur the screen under them (a high mip of the screen texture), under the painted frame.
        var blur = new ShaderMaterial { Shader = new Shader { Code = "shader_type canvas_item;\nuniform sampler2D scr : hint_screen_texture, filter_linear_mipmap;\nvoid fragment() { vec3 c = textureLod(scr, SCREEN_UV, 4.0).rgb; COLOR = vec4(c * 0.78, 1.0); }" } };
        _armBlurRail = new ColorRect { Name = "ArmouryBlur", MouseFilter = MouseFilterEnum.Ignore, Material = blur }; _armBlurRail.SetAnchorsPreset(LayoutPreset.FullRect); _armCanvas.AddChild(_armBlurRail);   // the whole scene is out of focus behind the screen, ghosts of the video included
        _armOverlay = Tex("ArmouryOverlay", "overlay-armoury", _armCanvas);
        _armOverlay.Texture = ArmField() ?? _armOverlay.Texture;
        _armFrame = new Panel { Name = "ArmouryRailFrame", MouseFilter = MouseFilterEnum.Ignore }; _armCanvas.AddChild(_armFrame);
        _armTitle = Tex("ArmouryTitle", "arm-title", _armCanvas);
        void Move(Control c, Control to) { c.GetParent()?.RemoveChild(c); to.AddChild(c); }
        // The rail: the header bar's pills, the class rows, the weapon row and the three tiles.
        _armRows = new Control { Name = "ArmouryRows", MouseFilter = MouseFilterEnum.Ignore }; _armCanvas.AddChild(_armRows);
        _armWeapon = new Control { Name = "ArmouryWeapon", MouseFilter = MouseFilterEnum.Ignore }; _armCanvas.AddChild(_armWeapon);
        _armWeaponBtn = new Button { Name = "ArmouryWeaponRow", FocusMode = FocusModeEnum.All, Flat = true }; _armWeapon.AddChild(_armWeaponBtn);
        _armWeaponBtn.Pressed += () => { _armDetailNow?.WeaponPress(); };
        _armWeaponIcon = new TextureRect { MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.LinearWithMipmaps }; _armWeapon.AddChild(_armWeaponIcon);
        _armWeaponName = new Label { MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, ClipText = true }; _armWeapon.AddChild(_armWeaponName);
        _armTilesHost = new Control { Name = "ArmouryTiles", MouseFilter = MouseFilterEnum.Ignore }; _armCanvas.AddChild(_armTilesHost);
        Move(stepsPanel, _armCanvas);
        var holder = new Control { Name = "ArmouryHidden", Visible = false }; AddChild(holder);
        var pills = new HBoxContainer { Name = "ArmouryPills" }; pills.AddThemeConstantOverride("separation", 2);
        foreach (var sb in _stepButtons) Move(sb, holder);   // the steps stay (keys, tests, added pages) but the cards open them
        Button Tab(string text, string tip, Action press) { var t = new Button { Text = text, ToggleMode = true, FocusMode = FocusModeEnum.All, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; UiTooltip.Attach(t, tip); t.Pressed += () => press(); pills.AddChild(t); return t; }
        _armTabClass = Tab("CLASS", "The class list", () => { _armSlotsTab = false; ArmouryDirty(); });
        _armTabSlots = Tab("SLOTS", "DEFAULTS and CUSTOM 1 to 10: pick a loadout slot", () => { _armSlotsTab = true; ArmouryDirty(); });
        _armTabLook = Tab("ARMOUR & PAINT", "The armour pieces, colours, badges and eyes of this loadout (the appearance editor)", () => { _armTabLook!.ButtonPressed = false; OpenAppearance(); });
        var oldSteps = _steps; var stepsMargin = (Container)oldSteps.GetParent(); stepsMargin.RemoveChild(oldSteps); holder.AddChild(oldSteps); foreach (var side in new[] { "left", "right", "top", "bottom" }) stepsMargin.AddThemeConstantOverride("margin_" + side, 0); _armSteps = stepsPanel;   // the spacer, LOOK caption and Appearance button stay in the hidden old box
        stepsMargin.AddChild(pills); _steps = pills;
        if (stepsPanel is PanelContainer sp) sp.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        // The middle: the preview and the item card; the right: the choices as cards.
        Move(_scroll, _armCanvas);   // the list first, as in the old layout (the tests and the tree order look for the list's scroll before the item card's)
        Move(right, _armCanvas);
        _scroll.CustomMinimumSize = Vector2.Zero;
        // The cards are tall: one wheel notch moves a card's pitch (the default step is an eighth of the view).
        void Wheel(InputEvent e) { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown or MouseButton.WheelUp } w) { var sc = (ScrollContainer)(w.ButtonIndex == MouseButton.WheelDown ? _scroll : _scroll); sc.ScrollVertical += (w.ButtonIndex == MouseButton.WheelDown ? 1 : -1) * Mathf.RoundToInt(1.5f * UiTextureBuilder.ArmCardPitch * Mathf.Max(0.5f, _armScale)); sc.AcceptEvent(); } }
        _scroll.GuiInput += Wheel;
        _scroll.SizeFlagsHorizontal = SizeFlags.Fill; _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        // The top: the faction / mode row and the loadout chips (scrolling sideways), the info lines.
        _armTopRow = new Control { Name = "ArmouryTop", MouseFilter = MouseFilterEnum.Ignore }; _armCanvas.AddChild(_armTopRow);
        _sideCaption!.Visible = false;
        _strip.Wrap = true;
        Move(_sideToggle, _armTopRow);
        _armStripScroll = new ScrollContainer { Name = "ArmouryStrip", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto, FollowFocus = true };
        _armCanvas.AddChild(_armStripScroll); Move(_strip, _armStripScroll);
        _armInfo = new VBoxContainer { Name = "ArmouryInfo", MouseFilter = MouseFilterEnum.Ignore }; _armCanvas.AddChild(_armInfo);
        Move(_banner, _armInfo); Move(_activeLabel, _armInfo); Move(_summary, _armCanvas);
        _activeLabel.HorizontalAlignment = HorizontalAlignment.Left;
        // The bottom: CONFIRM LOADOUT, the dock, BACK, the mode badge.
        _armColumnTitle = new Label { Name = "ArmouryColumnTitle", MouseFilter = MouseFilterEnum.Ignore, ClipText = true, VerticalAlignment = VerticalAlignment.Center }; _armCanvas.AddChild(_armColumnTitle);
        _armWell = new Panel { Name = "ArmourySlotsWell", MouseFilter = MouseFilterEnum.Ignore, Visible = false }; _armCanvas.AddChild(_armWell);
        _armSlotScroll = new ScrollContainer { Name = "ArmourySlots", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true }; _armCanvas.AddChild(_armSlotScroll);
        _armSlotList = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill }; _armSlotScroll.AddChild(_armSlotList);
        _armPickBack = new Button { Name = "ArmouryPickBack", Text = "< SLOTS", FocusMode = FocusModeEnum.All, Visible = false }; _armPickBack.Pressed += () => { _armPicking = false; ArmouryDirty(); }; _armCanvas.AddChild(_armPickBack);
        _armWings = Tex("ArmouryWings", "arm-wings", _armCanvas);
        _armConfirm = new Button { Name = "ConfirmLoadout", Text = "CONFIRM LOADOUT", FocusMode = FocusModeEnum.All };
        _armConfirm.Pressed += ConfirmLoadout; _armCanvas.AddChild(_armConfirm);
        _armBadge = Tex("ArmouryBadge", "arm-badge", _armCanvas);
        _armBadgeText = new Label { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; _armCanvas.AddChild(_armBadgeText);
        // The dock under the preview is gone (owner, 2026-11-20): its buttons stay in the hidden box (their keys F2 and Ctrl+S, the self-test and an added page's code reach them); the slot tools are plates of the SLOTS tab,
        // DEPLOY a plate under the item card in a match, and the line under the item card says UNSAVED CHANGES.
        foreach (var b in new Button?[] { _rename, _copy, _reset, _clear, _revert, _save, _setActive, _look, _deployButton }) if (b is not null) { b.CustomMinimumSize = Vector2.Zero; Move(b, holder); }
        Move(_unsaved, holder); Move(_status, _armCanvas);
        _status.SizeFlagsHorizontal = SizeFlags.Fill; _status.ClipText = true; _status.HorizontalAlignment = HorizontalAlignment.Center; _status.MouseFilter = MouseFilterEnum.Ignore;
        foreach (var (name, text, tip, act) in new (string, string, string, Action)[] { ("ArmToolRename", "RENAME", "Rename this loadout (F2)", RenameSlot), ("ArmToolCopy", "COPY", "Copy this loadout to another slot", CopySlot), ("ArmToolReset", "RESET", "Put this loadout back to the class defaults", ResetSlot), ("ArmToolClear", "CLEAR", "Empty this slot", ClearSlot) })
        {
            var tb = new Button { Name = name, Text = text, FocusMode = FocusModeEnum.All, Visible = false }; UiTooltip.Attach(tb, tip); tb.Pressed += () => act(); _armCanvas.AddChild(tb); _armTools.Add(tb);
        }
        _armDeploy = new Button { Name = "ArmDeploy", Text = "DEPLOY", FocusMode = FocusModeEnum.All, Visible = false }; UiTooltip.Attach(_armDeploy, "Save, make this loadout active and respawn with it now"); _armDeploy.Pressed += PressDeploy; _armCanvas.AddChild(_armDeploy);
        // BACK (the old back button is the concept's BACK plate); LEAVE MATCH is the Esc menu's row.
        Move(_backButton!, _armCanvas); _armBack = _backButton;
        if (_leaveButton is not null) Move(_leaveButton, holder);
        _armBack!.CustomMinimumSize = Vector2.Zero;
        _armCanvas.MoveChild(_armWings, _armCanvas.GetChildCount() - 1);
        Callable.From(LayoutArmoury).CallDeferred();
    }

    /// <summary>
    /// CONFIRM LOADOUT (owner, 2026-11-20): saves, sets the open loadout active and returns to the CLASS tab with the LOADOUT slot cards (a choice list closes). In a match it does the same and does not deploy: the loadout
    /// applies at the next respawn (DEPLOY respawns at once). On DEFAULTS it only sets active (defaults are never saved over). An added page (the Librarian) saves and deploys as before.
    /// </summary>
    public void ConfirmLoadout()
    {
        if (AddedPage) { Save(); if (Deploy is not null) PressDeploy(); return; }
        if (IsEmpty) { Say("Choose a class for this slot first."); return; }
        ActivateFailed = false;
        SetActive();
        if (Activate is not null && ActivateFailed) return;   // the refusal is the status line; stay on the page
        // in a match this also saves and sends it for the next respawn (NetworkView.Loadout.cs)
        if (Activate is null) Save();
        _armSlotsTab = false; _armPicking = false;
        ArmouryDirty();
    }

    /// <summary>Test aids for the buttons the dock used to hold (their keys are F2 and Ctrl+S; the Armoury has no plates for save and revert): the same code paths.</summary>
    public void PressSave() => Save();
    public void PressRevert() => RevertAll();
    /// <summary>The line under the item card as shown ("UNSAVED CHANGES" or ""), test aid.</summary>
    public string UnsavedLine => _summary.Text;   // a status message may hide the line for a few seconds; the text is the state
    /// <summary>The slot tools of the page (test aid): their names are ArmToolRename, ArmToolCopy, ArmToolReset and ArmToolClear; DEPLOY is ArmDeploy, CONFIRM LOADOUT ConfirmLoadout.</summary>
    public IReadOnlyList<Button> ArmTools => _armTools;

    // The rail (owner, 2026-11-20: one continuous, symmetric border): its centre line, the frame and the field inside it, in concept pixels. The field is the concept's gold ruled interior (ArmField).
    const float ArmRailCx = 157.5f, ArmRailRing = 9f, ArmHeaderW = 236f;
    static readonly Rect2 ArmRailOuter = new(ArmRailCx - 132f, 90f, 264f, 272f);
    static readonly Rect2 ArmRailInner = ArmRailOuter.Grow(-ArmRailRing);

    static ImageTexture? _armField;

    /// <summary>
    /// The rail's interior without the concept's own broken border: the overlay's gold field cut to <see cref="ArmRailInner"/> (its edge columns and rows repeated where the picture ends), the painted CLASS bar filled with the field so the
    /// live tabs stand on one continuous ground. Built once.
    /// </summary>
    static Texture2D? ArmField()
    {
        if (_armField is not null) return _armField;
        if (ConceptArt.Get("overlay-armoury") is not { } art) return null;
        var src = (Image)art.GetImage().Duplicate(); src.Decompress(); if (src.HasMipmaps()) src.ClearMipmaps(); src.Convert(Image.Format.Rgba8);
        var o = UiTextureBuilder.ArmRail.Position;
        int w = Mathf.RoundToInt(ArmRailInner.Size.X * 4f), h = Mathf.RoundToInt(ArmRailInner.Size.Y * 4f);
        int sx = Mathf.RoundToInt((ArmRailInner.Position.X - o.X) * 4f), sy = Mathf.RoundToInt((ArmRailInner.Position.Y - o.Y) * 4f);
        int vx0 = 118, vx1 = 1048, vy0 = 124, vy1 = 1062;   // the field's clean pixels in the overlay (the steel rim and the corner brackets lie outside)
        var dst = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            int yy = Mathf.Clamp(sy + y, vy0, vy1);
            for (int x = 0; x < w; x++) dst.SetPixel(x, y, src.GetPixel(Mathf.Clamp(sx + x, vx0, vx1), yy));
        }
        // The painted CLASS bar (concept 50 to 260, 100 to 122) goes: each row carries the field's own pixel from beside the bar.
        int bx0 = Mathf.RoundToInt((50f - ArmRailInner.Position.X) * 4f) - 8, bx1 = Mathf.RoundToInt((261f - ArmRailInner.Position.X) * 4f) + 8, by0 = 0, by1 = Mathf.RoundToInt((123f - ArmRailInner.Position.Y) * 4f);
        for (int y = by0; y < by1; y++) { var c = dst.GetPixel(Mathf.Max(0, bx0 - 6), y); for (int x = Mathf.Max(0, bx0); x < Mathf.Min(w, bx1); x++) dst.SetPixel(x, y, c); }
        dst.GenerateMipmaps();
        return _armField = ImageTexture.CreateFromImage(dst);
    }

    (float S, Vector2 O) ArmMap()
    {
        float s = Mathf.Min(Size.Y / UiTextureBuilder.PanelScreen.Size.Y, Size.X / UiTextureBuilder.PanelScreen.Size.X);
        return (s, new Vector2((Size.X - UiTextureBuilder.PanelScreen.Size.X * s) / 2f, (Size.Y - UiTextureBuilder.PanelScreen.Size.Y * s) / 2f));
    }

    ArmDetail? _armDetailNow;
    ColorRect? _armBlurRail;

    // The install's own weapon pictures (ui/overhaul, review round 2): the HUD art is read once, on a worker thread, from the player's install (read-only; nothing is written anywhere); a picture is its white silhouette, drawn
    // as a faint engraving. Until it is ready, or when the install has none (perks), the engraved type glyph stands in.
    HudArt? _armArt;
    readonly Dictionary<string, string> _armIds = new();
    bool _armArtReady, _armPending;

    /// <summary>The picture of an icon name: <c>install:&lt;wargear&gt;</c> is the install's silhouette (null while not ready), anything else a painted or engraved piece.</summary>
    Texture2D? ArmIcon(string? name, string glyph)
    {
        if (name is null) return GlyphTex(glyph);
        if (!name.StartsWith("install:", StringComparison.Ordinal)) return ConceptArt.Get(name);
        // The original's own front-end picture (owner, 2026-11-18: "ported from the original game 1:1"): the wargear or perk file names it (ui_data/icon_path), the player's install holds it (read-only, read once on a worker
        // thread, never written anywhere). Only where the file names none, or the install lacks the picture, the older shapes below stand in.
        _armArt ??= new HudArt(_services.GameDir);
        if (_armArt.Ready && _armArt.Picture(name[8..], _previewRace == SmContent.CustomizationCatalog.Chaos) is { } picture) { _armArtReady = true; _armInstall.Add(picture); return picture; }
        // The install's pistol and grenade pictures are too small and soft to read as a silhouette at tile size: those two slots keep the engraved glyphs (Opus review 3).
        if (name.Contains("pistol", StringComparison.OrdinalIgnoreCase) || name.Contains("grenade", StringComparison.OrdinalIgnoreCase))
        {
            EngravedGlyphs.Register();
            if (ConceptArt.Get(name.Contains("pistol", StringComparison.OrdinalIgnoreCase) ? "glyph-pistol-fill" : "glyph-grenade-fill") is { } fill) { _armSilhouettes.Add(fill); return fill; }   // solid shapes in the same ink as the bolter's and the knife's (Opus review 4)
        }
        _armArt ??= new HudArt(_services.GameDir);
        if (_armArt.Ready && _armArt.WeaponIcon(name[8..]) is { } tex)
        {
            if (_armArt.Silhouette(tex) is { } sil) { _armArtReady = true; _armSilhouettes.Add(sil); return sil; }
            _armPending = true;   // the silhouette is being made on a worker thread: lay the page out again shortly
        }
        else if (!_armArt.Ready && _armArt.Error.Length == 0) _armPending = true;   // the art is still loading
        return GlyphTex(glyph);
    }

    /// <summary>White for painted pieces and the glyphs (they carry their own tone), the engraving ink for an install silhouette.</summary>
    readonly HashSet<Texture2D> _armSilhouettes = new();
    readonly HashSet<Texture2D> _armGlyphs = new();
    Texture2D? GlyphTex(string glyph) { var t = ConceptArt.Get(glyph); if (t is not null) _armGlyphs.Add(t); return t; }
    Color ArmInk(Texture2D tex) => _armSilhouettes.Contains(tex) ? ArmEngrave : _armGlyphs.Contains(tex) ? new Color(2.1f, 1.9f, 1.5f) : Colors.White;   // the engraved glyphs are painted dim: lifted so they read at tile size

    /// <summary>An engraved look for an install silhouette: a one-pixel dark copy behind it (inner shadow), so a thin shape reads at tile size.</summary>
    void ArmShadow(TextureRect icon, float s)
    {
        if (icon.Texture is not { } tex || !_armSilhouettes.Contains(tex)) return;
        icon.AddChild(new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = icon.StretchMode, TextureFilter = icon.TextureFilter, Position = new Vector2(Mathf.Max(1f, s), Mathf.Max(1f, s)), Size = icon.Size, SelfModulate = new Color(0f, 0f, 0f, 0.85f), ShowBehindParent = true });
    }

    /// <summary>The ink of an install silhouette: a faint engraving like the glyphs.</summary>
    static readonly Color ArmEngrave = new(0.88f, 0.80f, 0.62f, 0.95f);

    ArmDetail ArmouryDetail()
    {
        ArmDetail? added = null;
        ExtrasArmouryDetail(ref added);   // EXTRAS-HOOK: the Librarian's weapon row, tiles and slot cards
        if (added is not null) return added;
        string race = _previewRace; string weapon = ""; string? weaponId = null;
        _armIds.Clear();
        foreach (var section in LoadoutRows.Weapons(_catalog, Role, race, OpenClass, _services.Policy))
        {
            foreach (var r in section.Rows) if (r.Id.Length > 0) _armIds[r.Title.ToUpperInvariant()] = r.Id;
            var sel = section.Rows.FirstOrDefault(r => r.Selected); if (sel is not null && weapon.Length == 0) { weapon = sel.Title; weaponId = sel.Id; }
        }
        var options = LoadoutRows.Options(_catalog, Role, race);
        var equipSection = LoadoutRows.Equipment(_catalog, Role, race, OpenClass, _services.Policy);
        foreach (var r in equipSection.Rows) if (r.Id.Length > 0) _armIds[r.Title.ToUpperInvariant()] = r.Id;
        foreach (var p in _catalog.Perks.All) _armIds.TryAdd(p.DisplayName.ToUpperInvariant(), p.Name);   // a perk row's picture is its file's own icon
        var equipRow = equipSection.Rows.FirstOrDefault(r => r.Selected);
        string equip = equipRow?.Title ?? "NONE";
        string? PerkId(int i) { string? n = LoadoutRows.PerkShown(_catalog, options, OpenClass, i); return n is null ? "no-perk" : "install:" + (_catalog.Perks.Perk(n)?.Name ?? n); }   // an empty perk slot has the owner's No Perk icon (brass, untinted)
        string Perk(int i)
        {
            string? name = LoadoutRows.PerkShown(_catalog, options, OpenClass, i, out bool foreignPerk);
            string text = name is null ? "NO PERK" : _catalog.Perks.Perk(name)?.DisplayName ?? name;
            return foreignPerk ? text + " " + LoadoutRows.NotInBuild.ToUpperInvariant() : text;   // the tiles are upper case
        }
        void Go(Step step, int perk) { _step = step; if (perk >= 0) _perkSlot = perk; Refresh(focusList: true); }
        bool perks = _step == Step.Perks;
        return new ArmDetail(IsEmpty ? "NO CLASS" : weapon.Length > 0 ? weapon.ToUpperInvariant() : "WEAPONS", weaponId is { Length: > 0 } ? "install:" + weaponId : null, () => Go(Step.Weapons, -1),
        [
            new ArmTile(equip, equipRow is { Id.Length: > 0 } ? "install:" + equipRow.Id : null, _step == Step.Equipment, () => Go(Step.Equipment, -1)),
            new ArmTile(Perk(0), PerkId(0), perks && _perkSlot == 0, () => Go(Step.Perks, 0)),
            new ArmTile(Perk(1), PerkId(1), perks && _perkSlot == 1, () => Go(Step.Perks, 1)),
        ], IsEmpty ? null : ArmClassSlots(equip, Perk, PerkId));
    }

    /// <summary>The card per loadout slot of a class: each weapon section (primary, sidearm, melee, ...), the equipment, the two perks; a press opens that slot's choices in the right column.</summary>
    List<ArmSlot> ArmClassSlots(string equip, Func<int, string> perk, Func<int, string?> perkId)
    {
        var slots = new List<ArmSlot>();
        void Pick(Step step, int perkSlot) { _step = step; if (perkSlot >= 0) _perkSlot = perkSlot; _armPicking = true; Refresh(focusList: true); }
        foreach (var section in LoadoutRows.Weapons(_catalog, Role, _previewRace, OpenClass, _services.Policy))
        {
            var sel = section.Rows.FirstOrDefault(r => r.Selected); string heading = section.Heading; int cut = heading.IndexOf("  (", StringComparison.Ordinal); if (cut > 0) heading = heading[..cut];
            if (section.Key == "melee" && sel is { Id.Length: 0 }) continue;   // the Devastator and Havoc carry no melee weapon: no card that says NONE (owner, 2026-11-20)
            slots.Add(new ArmSlot(heading, sel?.Title ?? "NONE", sel is { Id.Length: > 0 } ? "install:" + sel.Id : null, () => Pick(Step.Weapons, -1)));
        }
        slots.Add(new ArmSlot("EQUIPMENT", equip, _armIds.TryGetValue(equip.ToUpperInvariant(), out var eqId) ? "install:" + eqId : null, () => Pick(Step.Equipment, -1)));
        slots.Add(new ArmSlot("PERK 1", perk(0), perkId(0), () => Pick(Step.Perks, 0)));
        slots.Add(new ArmSlot("PERK 2", perk(1), perkId(1), () => Pick(Step.Perks, 1)));
        return slots;
    }

    List<ArmClass> ArmouryClasses()
    {
        var rows = new List<ArmClass>();
        foreach (var role in _tabRoles)
        {
            string r = role; string label = OrkLoadout.IsOrk(role) ? OrkLoadout.ClassName : LoadoutRows.ClassName(_catalog, role, _previewRace); string lower = (role + " " + label).ToLowerInvariant();
            // The owner's own class emblems (2026-11-20): the loyalist set on the Space Marine side, the Chaos set on the Chaos side; the Ork Boy keeps his glyph.
            bool chaos = _previewRace == SmContent.CustomizationCatalog.Chaos;
            string? icon = lower.Contains("ork") ? "glyph-ork" : lower.Contains("tact") ? (chaos ? "emblem-chaos-tactical" : "emblem-tactical") : lower.Contains("assault") || lower.Contains("raptor") ? (chaos ? "emblem-chaos-assault" : "emblem-assault") : lower.Contains("dev") || lower.Contains("hav") ? (chaos ? "emblem-chaos-havoc" : "emblem-devastator") : null;
            rows.Add(new ArmClass(label.ToUpperInvariant(), icon, !IsEmpty && r.Equals(Role, StringComparison.OrdinalIgnoreCase), () => ChooseRole(r), RoleLabel(role)));
        }
        ExtrasArmouryClasses(rows);   // EXTRAS-HOOK: the Librarian's row first
        return rows;
    }

    /// <summary>Lays out and (when something changed) restyles the concept page. Cheap: the signature of the content decides.</summary>
    void LayoutArmoury()
    {
        if (!_arm || _armCanvas is null || !IsInsideTree() || Size.X < 50f) return;
        EngravedGlyphs.Register();
        var (s, o) = ArmMap();
        Rect2 M(Rect2 r) => new(o + r.Position * s, r.Size * s);
        void Put(Control c, Rect2 r) { var m = M(r); c.Position = m.Position; c.Size = m.Size; c.CustomMinimumSize = Vector2.Zero; }
        Put(_armOverlay!, ArmRailInner); Put(_armFrame!, ArmRailOuter); Put(_armTitle!, new Rect2(UiTextureBuilder.ArmTitle.Position + new Vector2(ArmRailCx - 167.8f, 0f), UiTextureBuilder.ArmTitle.Size)); Put(_armWings!, new Rect2(ArmRailCx - UiTextureBuilder.ArmWings.Size.X / 2f, UiTextureBuilder.ArmWings.Position.Y, UiTextureBuilder.ArmWings.Size.X, UiTextureBuilder.ArmWings.Size.Y));
        Put(_armBadge!, UiTextureBuilder.ArmBadge); Put(_armBadgeText!, UiTextureBuilder.ArmBadge);
        Put(_armRows!, new Rect2(ArmRailCx - 99f, 122, 198, 105));
        var detail0 = ArmouryDetail();
        Put(_armWeapon!, UiTextureBuilder.ArmWeaponIcon.Merge(UiTextureBuilder.ArmWeaponName));
        Put(_armTilesHost!, new Rect2(36, 276, 238, 74));
        Put(_armTopRow!, new Rect2(296, 70, 246, 20));   /* the faction tabs stand right over the preview they switch (Opus review 1) */ _sideToggle.Position = Vector2.Zero; _sideToggle.Size = _armTopRow!.Size;
        Put(_armStripScroll!, new Rect2(ArmRailCx - 117f, 124, 234, 196)); _armWeapon!.Visible = !_armSlotsTab; _armTilesHost!.Visible = !_armSlotsTab; _strip.Wrap = true; _strip.SizeFlagsHorizontal = SizeFlags.ExpandFill; _armStripScroll!.Visible = _armSlotsTab; _armRows!.Visible = !_armSlotsTab; _armWell!.Visible = _armSlotsTab; Put(_armWell, new Rect2(ArmRailCx - 119f, 121, 238, 229)); if (_armWell.GetIndex() > _armStripScroll.GetIndex()) _armCanvas.MoveChild(_armWell, _armStripScroll.GetIndex());   /* the well under the chips, once: moving it to the chips' index every layout swapped the two each time, which made the slots flash (owner, 2026-11-18) */ 
        bool hasSlots = (detail0.Slots?.Count ?? 0) > 0; bool slotMode = !_armPicking && hasSlots;
        _armSlotScroll!.Visible = slotMode; _scroll.Visible = !slotMode; _armPickBack!.Visible = !slotMode && hasSlots;
        Put(_armSlotScroll, new Rect2(549, 93, 222, 223));   /* whole cards only: three of 78 */ Put(_armPickBack, new Rect2(712, 76, 52, 14));
        Put(_armInfo!, new Rect2(296, 68, 246, 24)); _armInfo!.Visible = false;   // the "ACTIVE:" line is not in the concept; the active loadout is the badge and the summary line
        Put(_scroll, new Rect2(549, 93, 222, 223)); Put(_armColumnTitle!, new Rect2(551, 77, 212, 14));
        // The line under the item card (owner, 2026-11-20): UNSAVED CHANGES only while there are unsaved changes, else empty; a message (Saved., a refusal) takes its place for a few seconds.
        _summary.Text = _unsaved.Text.Length > 0 ? "UNSAVED CHANGES" : ""; _summary.HorizontalAlignment = HorizontalAlignment.Center;
        Put(_summary, new Rect2(272, 380, 370, 13)); Put(_status, new Rect2(272, 380, 370, 13));
        _status.Visible = _status.Text.Length > 0; _summary.Visible = !_status.Visible && _summary.Text.Length > 0;
        // RENAME, COPY, RESET, CLEAR: four plates under the chip grid, inside the well, on the highlighted slot; DEPLOY under the item card, in a match.
        for (int i = 0; i < _armTools.Count; i++)
        {
            var tool = _armTools[i]; Put(tool, new Rect2(ArmRailCx - 110f + i * 56f, 324f, 52f, 22f));
            tool.Visible = _armSlotsTab && !AddedPage;
            tool.Disabled = i == 1 ? IsEmpty : OpenClass is null;   // copy takes any filled slot; rename, reset and clear need a custom class in the slot
        }
        _armDeploy!.Visible = Deploy is not null && !AddedPage; Put(_armDeploy, new Rect2(330, 398, 178, 30)); _armDeploy.Disabled = IsEmpty;
        var rightBox = _preview.GetParent<Control>(); Put(rightBox, new Rect2(296, 94, 246, 284));
        Put(_armSteps!, new Rect2(ArmRailCx - ArmHeaderW / 2f, 101, ArmHeaderW, 21));
        Put(_armConfirm!, new Rect2(ArmRailCx - UiTextureBuilder.ArmConfirm.Size.X / 2f, UiTextureBuilder.ArmConfirm.Position.Y, UiTextureBuilder.ArmConfirm.Size.X, UiTextureBuilder.ArmConfirm.Size.Y));
        Put(_armBack!, UiTextureBuilder.ArmBack);
        string badge = "CLASSIC PVP"; ExtrasArmouryBadge(ref badge);   // EXTRAS-HOOK: the mode badge
        var detail = detail0; _armDetailNow = detail; var classes = ArmouryClasses();
        string sig = string.Join("|", classes.Select(c => c.Label + c.Selected)) + "#" + detail.Weapon + string.Join("|", detail.Tiles.Select(t => t.Label + t.Selected)) + "#" + badge + "#" + string.Join("|", _stepButtons.Select(b => b.Text + b.Visible)) + "#" + string.Join("|", _list.GetChildren().Select(c => c.GetInstanceId())) + "#" + Mathf.Round(s * 1000f) + "#" + _unsaved.Text.Length + string.Join("", _strip.Chips.Select(c => c.GetInstanceId())) + _armSlotsTab + _armPicking + string.Join("|", detail.Slots?.Select(x => x.Slot + x.Value) ?? []) + (Deploy is not null) + AddedPage;
        if (sig == _armSig) return;
        _armSig = sig; _armScale = s; _armPendingTries = 0;   // a real change of the page re-arms the wait for install art
        StyleArmoury(s, classes, detail, badge);
    }


    /// <summary>The one lettering of the page: the row face (<see cref="UiKit.ConceptFont"/>) at <paramref name="size"/> concept pixels, light on a dark plate and dark ink on gold.</summary>
    /// <summary>The page's concept-pixel size as a size of the one table (UiType): 17 and up the primary action, 11.5 and up labels and plates, 8.5 and up captions, smaller ones the small level.</summary>
    static int ArmUnit(float size) => size >= 17f ? UiType.Primary : size >= 11.5f ? UiType.Label : size >= 8.5f ? UiType.Caption : UiType.Small;
    static int ArmPx(float size) => UiType.Px(ArmUnit(size));

    /// <summary>Text on a plate that is too long steps down one level of the size table, then wraps (never an ellipsis, never clipped).</summary>
    static void ArmFit(Label l, float size, float availPx) => PaintedButtons.FitLabel(l, ArmUnit(size), availPx);

    /// <summary>Install pictures (and silhouettes) of the page: drawn whole, never blurred past their own pixels.</summary>
    readonly HashSet<Texture2D> _armInstall = new();

    /// <summary>
    /// Puts a picture in its icon well. An install picture is centred at the whole-number scale that fits the well (nearest filter, so it is not smeared) or, when the well is smaller than the picture, shrunk with its mipmaps
    /// and linear filter, keeping its aspect. Painted pieces and glyphs fill the well as before (<paramref name="glyphMode"/>).
    /// </summary>
    void ArmFitPicture(TextureRect ic, Rect2 well, TextureRect.StretchModeEnum glyphMode = TextureRect.StretchModeEnum.KeepAspectCentered)
    {
        ic.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        if (ic.Texture is not { } tex || !(_armSilhouettes.Contains(tex) || _armInstall.Contains(tex)))
        {
            ic.StretchMode = glyphMode; ic.TextureFilter = TextureFilterEnum.LinearWithMipmaps; ic.Position = well.Position; ic.Size = well.Size; return;
        }
        ic.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        float fit = Mathf.Min(well.Size.X / tex.GetWidth(), well.Size.Y / tex.GetHeight());
        // The largest whole-number scale that fits, nearest filter; where that scale would leave the picture under 60 % of the largest fit, the picture fills the well shrunk from the next whole scale (linear, mipmaps).
        if (fit >= 1f && Mathf.Floor(fit) / fit >= 0.6f)
        {
            var size = new Vector2(tex.GetWidth(), tex.GetHeight()) * Mathf.Floor(fit);
            ic.TextureFilter = TextureFilterEnum.Nearest; ic.Size = size; ic.Position = (well.Position + (well.Size - size) / 2f).Round();
        }
        else { ic.TextureFilter = TextureFilterEnum.LinearWithMipmaps; ic.Position = well.Position; ic.Size = well.Size; }
    }

    static void ArmText(Control c, float size, float s, Color rest, Color pressed)
    {
        c.AddThemeFontOverride("font", UiKit.ConceptFont());
        c.AddThemeFontSizeOverride("font_size", ArmPx(size));
        c.AddThemeConstantOverride("outline_size", 0);
        foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_disabled_color" }) c.AddThemeColorOverride(n, rest);
        foreach (var n in new[] { "font_pressed_color", "font_hover_pressed_color" }) c.AddThemeColorOverride(n, pressed);
    }

    static void ArmLabel(Label l, float size, float s, Color colour)
    {
        l.AddThemeFontOverride("font", UiKit.ConceptFont());
        l.AddThemeFontSizeOverride("font_size", ArmPx(size));
        l.AddThemeConstantOverride("outline_size", 0);
        l.AddThemeConstantOverride("line_spacing", -Mathf.RoundToInt(0.32f * size * s));
        l.AddThemeColorOverride("font_color", colour);
    }

    static void ArmPlate(Button b, string dark, string gold, PaintedButtons.Pad dp, PaintedButtons.Pad gp, float s, bool goldFocus = true)
    {
        PaintedButtons.Apply(b, dark, gold, dp, gp, s, goldFocus, paintedText: false, noGlow: true, cpx: s);
    }

    void StyleArmoury(float s, List<ArmClass> classes, ArmDetail detail, string badge)
    {
        // ---- the rail: class rows ----
        var classNames = new List<(Control, string, float)>();
        foreach (var c in _armRows!.GetChildren()) { _armRows.RemoveChild(c); c.QueueFree(); }
        for (int i = 0; i < classes.Count && i < 4; i++)
        {
            var row = classes[i]; var r = new Rect2(0, new[] { 2.75f, 30f, 55f, 80f }[i], 198, 23.5f);
            var b = new Button { Text = row.Real ?? row.Label, ToggleMode = true, FocusMode = FocusModeEnum.All, Alignment = HorizontalAlignment.Left, ClipText = true, Name = "ArmClass" + i };
            b.Position = r.Position * s; b.Size = r.Size * s; UiTooltip.Attach(b, ClassChangeHint);
            b.ButtonPressed = row.Selected;
            var press = row.Press;
            b.Pressed += () => { press(); };
            _armRows.AddChild(b);
            ArmPlate(b, "arm-row-dark", "arm-row-gold", new PaintedButtons.Pad(2, 1, 2, 1), new PaintedButtons.Pad(10, 4.25f, 9, 4.25f), s);
            ArmText(b, ArmNameSize, s, ArmLight, ArmDark);
            foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) b.AddThemeColorOverride(n, Colors.Transparent);   // the plate's name is the label below (one side's class name, upper case); the button keeps both sides' name
            var nameLabel = new Label { Text = row.Label, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, ClipText = true };
            ArmLabel(nameLabel, ArmNameSize, s, row.Selected ? ArmDark : ArmLight); nameLabel.Position = new Vector2(76f, 0f) * s; nameLabel.Size = new Vector2(112f, 23.5f) * s; b.AddChild(nameLabel); classNames.Add((nameLabel, nameLabel.Text, nameLabel.Size.X));
            if (row.Icon is not null && ConceptArt.Get(row.Icon) is { } tex)
            {
                var ico = new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.LinearWithMipmaps, Modulate = row.Selected ? ArmDark : ArmLight };
                ico.Position = new Vector2(14f * s, 0.5f * s); ico.Size = new Vector2(44f * s, 22.5f * s); b.AddChild(ico);   // a painted class emblem
            }
        }
        PaintedButtons.FitGroup(classNames, ArmUnit(ArmNameSize));   // the four rows share one size
        // ---- the weapon row ----
        var wb = _armWeaponBtn!; var wr = UiTextureBuilder.ArmWeaponIcon.Merge(UiTextureBuilder.ArmWeaponName);
        wb.Position = Vector2.Zero; wb.Size = wr.Size * s; wb.Text = detail.Weapon; UiTooltip.Attach(wb, detail.Weapon);
        wb.AddThemeStyleboxOverride("normal", new StyleBoxEmpty()); wb.AddThemeStyleboxOverride("hover", new StyleBoxEmpty()); wb.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty()); wb.AddThemeStyleboxOverride("disabled", new StyleBoxEmpty());
        wb.AddThemeStyleboxOverride("focus", PaintedButtons.Ring(s, 0.5f));
        foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) wb.AddThemeColorOverride(n, Colors.Transparent);
        var iconRect = new Rect2(UiTextureBuilder.ArmWeaponIcon.Position - wr.Position, UiTextureBuilder.ArmWeaponIcon.Size);
        _armWeaponIcon!.Texture = ArmIcon(detail.WeaponIcon, "glyph-gun"); _armWeaponIcon.Modulate = detail.WeaponIcon?.StartsWith("install:", StringComparison.Ordinal) == true ? ArmEngrave : Colors.White;
        foreach (var old in _armWeaponIcon.GetChildren()) { _armWeaponIcon.RemoveChild(old); old.QueueFree(); }
        ArmFitPicture(_armWeaponIcon, new Rect2((iconRect.Position + new Vector2(4, 4)) * s, new Vector2(70, 28) * s));   // keeps the picture's own aspect
        ArmShadow(_armWeaponIcon, s);
        _armWeaponName!.Text = detail.Weapon; ArmLabel(_armWeaponName, ArmNameSize, s, ArmLight);
        _armWeaponName.Position = new Vector2(UiTextureBuilder.ArmWeaponName.Position.X - wr.Position.X + 9f, UiTextureBuilder.ArmWeaponName.Position.Y - wr.Position.Y) * s; _armWeaponName.Size = new Vector2(UiTextureBuilder.ArmWeaponName.Size.X - 30f, UiTextureBuilder.ArmWeaponName.Size.Y) * s; ArmFit(_armWeaponName, ArmNameSize, _armWeaponName.Size.X);
        // ---- the tiles ----
        foreach (var c in _armTilesHost!.GetChildren()) { _armTilesHost.RemoveChild(c); c.QueueFree(); }
        _armTileButtons.Clear();
        float[] tx = [7f, 84.5f, 162f];
        var tileLabels = new List<(Control, string, float)>();
        for (int i = 0; i < 3 && i < detail.Tiles.Count; i++)
        {
            var t = detail.Tiles[i];
            var b = new Button { Text = t.Label, ToggleMode = true, FocusMode = FocusModeEnum.All, Name = "ArmTile" + i }; UiTooltip.Attach(b, t.Label);
            b.Position = new Vector2(tx[i], 5f) * s; b.Size = new Vector2(74f, 66f) * s; b.ButtonPressed = t.Selected; b.ClipText = true; b.CustomMinimumSize = Vector2.Zero;
            var press = t.Press; b.Pressed += () => press();
            _armTilesHost.AddChild(b); _armTileButtons.Add(b);
            PaintedButtons.Apply(b, "arm-tile-dark", "arm-tile-gold", new PaintedButtons.Pad(1, 2, 1, 2), new PaintedButtons.Pad(3, 3, 3, 3), s, true, paintedText: true, noGlow: true, cpx: s);
            string tileNote = " " + LoadoutRows.NotInBuild.ToUpperInvariant(); bool noted = t.Label.EndsWith(tileNote, StringComparison.Ordinal); string tileText = noted ? t.Label[..^tileNote.Length] : t.Label;   // a kept pick of another build keeps its note (the cut below only drops class-name suffixes)
            int paren = tileText.IndexOf(" (", StringComparison.Ordinal); var l = new Label { Text = ((paren > 0 ? tileText[..paren] : tileText) + (noted ? tileNote : "")).ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, AutowrapMode = TextServer.AutowrapMode.Word };
            ArmLabel(l, ArmTileSize, s, t.Selected ? new Color(1f, 0.95f, 0.8f) : ArmLight);
            l.Position = new Vector2(7f, 30f) * s; l.Size = new Vector2(60f, 31f) * s; b.AddChild(l); tileLabels.Add((l, l.Text, l.Size.X));   // clear of the bezels
            if (ArmIcon(t.Icon, i == 0 ? "glyph-grenade" : "glyph-perk") is { } tex)
            { var ic = new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, Modulate = ArmInk(tex) }; ArmFitPicture(ic, new Rect2(new Vector2(14f, 6f) * s, new Vector2(46f, 30f) * s)); b.AddChild(ic); ArmShadow(ic, s); }
        }
        PaintedButtons.FitGroup(tileLabels, ArmUnit(ArmTileSize));   // the three tiles share one size
        // ---- the header tab strip: CLASS, SLOTS, ARMOUR & PAINT ----
        // The three tabs keep the caption size they always had (owner, 2026-11-20): the strip's width is shared by what each label needs (stretch ratios), not in equal thirds, so ARMOUR & PAINT fits at that size.
        var tabs = new[] { _armTabClass!, _armTabSlots!, _armTabLook! };
        _armTabClass.SetPressedNoSignal(!_armSlotsTab); _armTabSlots.SetPressedNoSignal(_armSlotsTab);
        var tabNeed = new List<(Button B, float Text, float Margin)>();
        foreach (var b in tabs)
        {
            b.CustomMinimumSize = Vector2.Zero; b.Alignment = HorizontalAlignment.Center;
            PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, noGlow: true, cpx: s);
            ArmText(b, ArmPillSize + 1f, s, ArmLight, ArmDark);
            tabNeed.Add((b, PaintedButtons.Width(b.GetThemeFont("font"), b.Text, ArmUnit(ArmPillSize + 1f)), b.GetThemeStylebox("normal").GetMinimumSize().X));
        }
        float tabTotal = ArmHeaderW * s - 2f * (tabs.Length - 1), tabSum = tabNeed.Sum(n => n.Text + n.Margin);
        var tabFit = new List<(Control, string, float)>();
        foreach (var (b, text, margin) in tabNeed)
        {
            b.SizeFlagsStretchRatio = 1f;   // equal widths (owner, 2026-11-20)
            tabFit.Add((b, b.Text, tabTotal / tabs.Length - margin));
        }
        PaintedButtons.FitGroup(tabFit, ArmUnit(ArmPillSize + 1f));   // the three tabs share the size the longest needs: the caption size while the strip has the room for it
        PaintedButtons.Apply(_armPickBack!, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1), new PaintedButtons.Pad(1), s * 0.45f, true, paintedText: false, noGlow: true, cpx: s); ArmText(_armPickBack, ArmPillSize + 1f, s, ArmLight, ArmDark);
        BuildArmSlotCards(s, detail);
        // The SLOTS tab: a dark well with the chips as plates (one lettering; the open loadout's chip gold; the state words in the tooltip, the star kept).
        _armWell!.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.055f, 0.042f, 0.022f), BorderColor = new Color(0.50f, 0.37f, 0.12f), BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1 });
        var openChip = _strip.Open is { } opened ? _strip.ChipOf(opened) : null;
        var chipFit = new List<(Control, string, float)>();
        foreach (var chip in _strip.Chips)
        {
            chip.CustomMinimumSize = new Vector2(70f * s, 31f * s); chip.ClipText = false;
            bool isOpen = chip == openChip;
            PaintedButtons.Apply(chip, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, goldRest: isOpen, noGlow: true, cpx: s);
            ArmText(chip, 8.2f, s, isOpen ? ArmDark : ArmLight, ArmDark);
            foreach (var n in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" }) if (chip.GetThemeStylebox(n) is StyleBox sb) sb.ContentMarginLeft = 14f * s;   // the star's room; the other margins are the plate's own inset
            chip.AddThemeConstantOverride("line_spacing", -Mathf.RoundToInt(2.6f * s));
            foreach (var l in chip.GetChildren().OfType<Label>()) l.Visible = false;
            // Two lines inside the plate (owner, 2026-11-18: the third line was cut): the slot, then its name (or its class when the name is only the slot again); the star sits in the left margin.
            var lines = chip.Text.Split((char)10);
            if (lines.Length > 2) chip.Text = lines[0] + "\n" + (lines[1].Equals(lines[0], StringComparison.OrdinalIgnoreCase) ? lines[2] : lines[1]);
            chipFit.Add((chip, chip.Text.Split((char)10).OrderByDescending(x => x.Length).First(), 70f * s - chip.GetThemeStylebox("normal").GetMinimumSize().X));
            foreach (var star in chip.GetChildren().OfType<UiKit.Glyph>()) { star.Size = new Vector2(10f, 10f) * s; star.CustomMinimumSize = star.Size; star.Position = new Vector2(1.5f * s, 31f * s / 2f - 5f * s); }
        }
        PaintedButtons.FitGroup(chipFit, ArmUnit(8.2f));   // every chip of the strip shares one size
        // ---- the rail's frame: the options window's own ring art, one continuous border all the way round ----
        if (ConceptArt.Get("frame-options") is { } ring)
        {
            var corner = ConceptArt.OptionsCorner;
            _armFrame!.AddThemeStyleboxOverride("panel", new PaintedButtons.CapStyle { Texture = ring, Caps = new PaintedButtons.CapArt(corner.X, corner.Y, corner.X, corner.Y, 0f, 0f), FixedK = ArmRailRing * s / ConceptArt.OptionsRing.X, DrawCenter = false });
        }
        // ---- CONFIRM LOADOUT, BACK, the badge ----
        ArmPlate(_armConfirm!, "arm-confirm", "arm-confirm", default, default, s, false);
        ArmText(_armConfirm, ArmConfirmSize, s, ArmDark, ArmDark); _armConfirm.AddThemeColorOverride("font_hover_color", new Color(0.2f, 0.12f, 0.02f));
        _armConfirm.Text = "CONFIRM LOADOUT"; _armConfirm.ClipText = false;
        ArmPlate(_armBack!, "arm-back", "arm-back", default, default, s, false);
        ArmText(_armBack, ArmBackSize, s, ArmLight, ArmLight);
        ArmLabel(_armBadgeText!, ArmBadgeSize, s, ArmLight); _armBadgeText.Text = badge;
        // ---- the slot tools, DEPLOY and the lines under the item card ----
        var toolFit = new List<(Control, string, float)>();
        foreach (var tb in _armTools)
        {
            tb.AddThemeConstantOverride("h_separation", 0);
            PaintedButtons.Apply(tb, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, noGlow: true, cpx: s);
            ArmText(tb, ArmPlateSize * 0.86f, s, ArmLight, ArmDark);
            toolFit.Add((tb, tb.Text, 52f * s - tb.GetThemeStylebox("normal").GetMinimumSize().X));
        }
        PaintedButtons.FitGroup(toolFit, ArmUnit(ArmPlateSize * 0.86f));   // the four share one size
        ArmPlate(_armDeploy!, "arm-back", "arm-back", default, default, s, false);
        ArmText(_armDeploy, ArmBackSize, s, ArmLight, ArmLight); _armDeploy.ClipText = false;
        foreach (var l in new[] { _summary, _activeLabel, _banner }) ArmLabel(l, 8.8f, s, ArmDim);
        ArmLabel(_banner, 8.8f, s, new Color(0.97f, 0.82f, 0.4f));
        ArmLabel(_summary, 8.8f, s, new Color(0.97f, 0.82f, 0.4f)); ArmLabel(_status, 8.8f, s, ArmLight);
        _summary.ClipText = true;
        // The faction / mode pills above the preview.
        foreach (var b in _sideToggle.GetChildren().OfType<Button>())
        {
            b.CustomMinimumSize = new Vector2(0, 18f * s); b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, noGlow: true, cpx: s);
            ArmText(b, ArmPlateSize, s, ArmLight, ArmDark);
        }
        StyleArmCards(s);
        if (_armSlotScroll!.Visible) _armColumnTitle!.Text = "LOADOUT";
    }

    /// <summary>One card per loadout slot (owner, 2026-11-15), at the concept's card positions: the slot's name small above its content; the column scrolls when there are more cards than fit.</summary>
    void BuildArmSlotCards(float s, ArmDetail detail)
    {
        foreach (var c in _armSlotList!.GetChildren()) { _armSlotList.RemoveChild(c); c.QueueFree(); }
        if (ConceptArt.Get("arm-card") is not { } card || detail.Slots is null) return;
        // The slot cards shrink together until all of them fit the column (owner, 2026-11-18), never under 23 concept pixels pitch (a card of about 20).
        float k = detail.Slots.Count <= 0 ? 1f : Mathf.Clamp((223f * s - 8f * s) / (detail.Slots.Count * UiTextureBuilder.ArmCardPitch * s), 19f / UiTextureBuilder.ArmCardPitch, 1f);
        _armSlotList.AddThemeConstantOverride("separation", Mathf.RoundToInt((UiTextureBuilder.ArmCardPitch - UiTextureBuilder.ArmCard.Size.Y) * s * k));
        var slotCaps = new List<(Control, string, float)>(); var slotVals = new List<(Control, string, float)>();
        foreach (var slot in detail.Slots)
        {
            var b = new Button { Text = slot.Slot + ": " + slot.Value, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, UiTextureBuilder.ArmCard.Size.Y * s * k), ClipText = true, Name = "ArmSlot" + _armSlotList.GetChildCount() }; UiTooltip.Attach(b, slot.Slot + ": " + slot.Value);
            var press = slot.Press; b.Pressed += () => press();
            PaintedButtons.CapStyle Box(Color mod) { var cs = PaintedButtons.Box(card, "arm-card", default, s, mod, s); cs.ContentMarginTop = 0; cs.ContentMarginBottom = 0; return cs; }   // the card's text is the labels below: no margin may hold the card open
            var gold = new Color(1.3f, 1.12f, 0.78f);
            b.AddThemeStyleboxOverride("normal", Box(Colors.White)); b.AddThemeStyleboxOverride("hover", Box(ConceptArt.Warm)); b.AddThemeStyleboxOverride("pressed", Box(gold)); b.AddThemeStyleboxOverride("focus", Box(gold)); b.AddThemeStyleboxOverride("disabled", Box(Colors.Gray));
            foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) b.AddThemeColorOverride(n, Colors.Transparent);
            var cap = new Label { Text = slot.Slot.ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, ClipText = true, VerticalAlignment = VerticalAlignment.Center };
            ArmLabel(cap, ArmTileSize * 0.75f, s, new Color(0.97f, 0.82f, 0.4f)); cap.Position = new Vector2(ArmCardTextX(k), 4f) * s; cap.Size = new Vector2(ArmCardTextW(k), 8f) * s; b.AddChild(cap); slotCaps.Add((cap, cap.Text, cap.Size.X));   /* under the card top bevel */
            var val = new Label { Text = slot.Value.ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.Word, ClipText = true, VerticalAlignment = VerticalAlignment.Center };
            ArmLabel(val, k < 0.85f ? ArmTileSize : ArmNameSize, s, ArmLight); val.Position = new Vector2(ArmCardTextX(k), 12f) * s; val.Size = new Vector2(ArmCardTextW(k), 67f * k - 15f) * s; b.AddChild(val); slotVals.Add((val, val.Text, val.Size.X));   // caption above, the value centred in the rest, at every card count
            if (ArmIcon(slot.Icon, EngravedGlyphs.For(slot.Slot)) is { } tex) { var ic = new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, Modulate = ArmInk(tex) }; ArmFitPicture(ic, new Rect2(new Vector2(8f * s * k, 67f * s * k * 0.1f), new Vector2(62f * s * k, 67f * s * k * 0.8f))); b.AddChild(ic); ArmShadow(ic, s); }
            _armSlotList.AddChild(b);
        }
        PaintedButtons.FitGroup(slotCaps, ArmUnit(ArmTileSize * 0.75f)); PaintedButtons.FitGroup(slotVals, ArmUnit(k < 0.85f ? ArmTileSize : ArmNameSize));   // one size for all the captions, one for all the values
    }

    /// <summary>The right column: every choice row as a concept card (icon tile at the left, the name, a gold wash when chosen); headings and notes in the one face.</summary>
    void StyleArmCards(float s)
    {
        // The first heading of the step is the column's title above the first card (so the first card stands where the concept's does); later headings stay in the list.
        _armColumnTitle!.Text = ""; ArmLabel(_armColumnTitle, ArmTileSize, s, new Color(0.97f, 0.82f, 0.4f));
        if (_list.GetChildCount() > 0 && _list.GetChild(0) is Label first) { _armColumnTitle.Text = first.Text; first.Visible = false; }
        // Every option of the step fits the window without a scroll bar (owner, 2026-11-18: "scale down the options on the right so that they all fit"): the cards shrink together (art, icon well and name) until
        // the list fits the column, never under 23 concept pixels pitch (a card of about 20); a longer list scrolls as before.
        int rowsN = _list.GetChildren().OfType<UiKit.Row>().Count(), headsN = _list.GetChildren().OfType<Label>().Count(l => l.Visible && l != _list.GetChildOrNull<Label>(0));
        float areaH = 223f * s - 10f * s - headsN * 19f * s;
        _armCardK = rowsN <= 0 ? 1f : Mathf.Clamp(areaH / (rowsN * UiTextureBuilder.ArmCardPitch * s), 19f / UiTextureBuilder.ArmCardPitch, 1f);
        _list.AddThemeConstantOverride("separation", Mathf.RoundToInt((UiTextureBuilder.ArmCardPitch - UiTextureBuilder.ArmCard.Size.Y) * s * _armCardK));
        if (!_list.GetChildren().Any(n => n.Name == "ArmPad")) _list.AddChild(new Control { Name = "ArmPad", CustomMinimumSize = new Vector2(0, 8f * s), MouseFilter = MouseFilterEnum.Ignore });   // room under the last card so it scrolls fully into view
        _armCardNames.Clear();
        foreach (var c in _list.GetChildren())
        {
            switch (c)
            {
                case UiKit.Row row: StyleArmCard(row, s); break;
                case Label l: ArmLabel(l, ArmTileSize, s, new Color(0.97f, 0.82f, 0.4f)); l.CustomMinimumSize = new Vector2(0, 13f * s); break;
            }
        }
        Callable.From(() => _scroll.VerticalScrollMode = _list.GetCombinedMinimumSize().Y <= _scroll.Size.Y + 2f ? ScrollContainer.ScrollMode.ShowNever : ScrollContainer.ScrollMode.Auto).CallDeferred();   // a list that fits shows no scroll bar
        PaintedButtons.FitGroup(_armCardNames, ArmUnit(_armCardK < 0.85f ? ArmTileSize : ArmNameSize));   // every card of the list shares one size
    }

    float _armCardK = 1f;
    readonly List<(Control, string, float)> _armCardNames = new();

    /// <summary>The card's icon tile is drawn at the art's own proportion (a cap, not squeezed), so it is 79 concept px wide only at the full card height; a shrunk card's tile, and the text beside it, shrink with it (concept px; 4 px of room after the tile at least).</summary>
    static float ArmCardTextX(float k) => 75f * k + 4f;
    static float ArmCardTextW(float k) => 222f - ArmCardTextX(k) - 8f;

    void StyleArmCard(UiKit.Row row, float s)
    {
        float k = _armCardK;
        if (ConceptArt.Get("arm-card") is not { } card) return;
        row.CustomMinimumSize = new Vector2(0, UiTextureBuilder.ArmCard.Size.Y * s * k);
        row.AutowrapMode = TextServer.AutowrapMode.Word; row.ClipText = false;
        bool chosen = row.IsSelected;
        var goldArt = ConceptArt.Get("arm-row-gold");
        PaintedButtons.CapStyle Box(Color mod, float grow)
        {
            // The chosen row is a gold plate (the painted gold row of the class list), the others the dark card.
            var b = chosen && goldArt is not null ? PaintedButtons.Box(goldArt, "arm-row-gold", new PaintedButtons.Pad(grow), s, mod == Colors.White || mod == ConceptArt.Warm ? Colors.White : mod, s) : PaintedButtons.Box(card, "arm-card", new PaintedButtons.Pad(grow), s, mod, s);
            b.ContentMarginTop = 0; b.ContentMarginBottom = 0;   // the row's own text is transparent: no margin may hold a shrunk card open
            b.ContentMarginLeft = ArmCardTextX(k) * s; b.ContentMarginRight = 10f * s;   // the icon tile's width (it keeps its proportion now), then the plate's own right margin
            return b;
        }
        var gold = new Color(1.3f, 1.12f, 0.78f);   // hover and focus wash; a chosen row is marked by its check glyph in the icon tile
        row.AddThemeStyleboxOverride("normal", Box(Colors.White, 0f));
        row.AddThemeStyleboxOverride("hover", Box(ConceptArt.Warm, 0f));
        row.AddThemeStyleboxOverride("pressed", Box(gold, 0f));
        row.AddThemeStyleboxOverride("focus", Box(gold, 0f));
        row.AddThemeStyleboxOverride("disabled", Box(new Color(0.55f, 0.55f, 0.55f), 0f));
        ArmText(row, k < 0.85f ? 6f : ArmNameSize, s, ArmLight, ArmLight);   // its own text is transparent (the name label draws it): a shrunk card must not be held open by that text
        foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) row.AddThemeColorOverride(n, Colors.Transparent);   // the name is drawn by the label below (upper case, as the concept); the row keeps its text
        row.Alignment = HorizontalAlignment.Left;
        string lower = row.Text.ToLowerInvariant();
        string? icon = lower.Contains("force sword") || lower == "sword" ? "arm-ico-sword" : lower.Contains("chain lightning") ? "arm-ico-lightning" : lower.Contains("psychic stress") ? "arm-ico-stress" : "glyph-perk";   // no picture: the faint engraved type glyph
        foreach (var child in row.GetChildren())
        {
            if (child is ColorRect bar) bar.Visible = false;
            else if (child is Label tags) ArmLabel(tags, ArmTileSize * 0.8f, s, ArmDim);
            else if (child is Control rightGlyph && rightGlyph.HasMeta("row_right_glyph")) { rightGlyph.OffsetRight = -9f * s; rightGlyph.OffsetLeft = -22f * s; rightGlyph.OffsetTop = -6.5f * s * k; rightGlyph.OffsetBottom = 6.5f * s * k; rightGlyph.Modulate = chosen ? ArmDark : ArmEngrave; }
            else if (child is Control glyph && glyph.GetType().Name == "Glyph") { glyph.SetAnchorsPreset(Control.LayoutPreset.CenterRight); glyph.GrowHorizontal = Control.GrowDirection.Begin; glyph.OffsetRight = -(chosen && row.GetChildren().Any(c => c is Control r && r.HasMeta("row_right_glyph")) ? 26f : 9f) * s; glyph.OffsetLeft = glyph.OffsetRight - 13f * s; glyph.OffsetTop = -6.5f * s * k; glyph.OffsetBottom = 6.5f * s * k; glyph.Visible = true; glyph.Modulate = chosen ? ArmDark : ArmEngrave; }   // the tick of the chosen row (on the gold plate), the padlock of a locked one: at the right
        }
        foreach (var old in row.GetChildren().OfType<Node>().Where(n => n.HasMeta("arm_card")).ToList()) { row.RemoveChild(old); old.QueueFree(); }
        if (icon == "glyph-perk" && _armIds.TryGetValue(row.Text.ToUpperInvariant(), out var wargearId)) icon = "install:" + wargearId;   // a weapon or grenade of the install: its own silhouette
        if (icon is not null && ArmIcon(icon, "glyph-perk") is { } tex)
        {
            var pic = new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, Modulate = chosen && _armSilhouettes.Contains(tex) ? ArmDark : ArmInk(tex) }.With(t => t.SetMeta("arm_card", true));
            ArmFitPicture(pic, new Rect2(new Vector2(8f * s * k, 67f * s * k * 0.1f), new Vector2(62f * s * k, 67f * s * k * 0.8f)), icon.StartsWith("install:", StringComparison.Ordinal) ? TextureRect.StretchModeEnum.KeepAspectCentered : TextureRect.StretchModeEnum.Scale);
            row.AddChild(pic);
        }
        var name = new Label { Text = row.Text.ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.Word, ClipText = true };
        name.SetMeta("arm_card", true); ArmLabel(name, k < 0.85f ? ArmTileSize : ArmNameSize, s, chosen ? ArmDark : ArmLight);   // a shrunk card: the caption level, one line, centred, so the name is always whole
        if (k < 0.85f) { name.AutowrapMode = TextServer.AutowrapMode.Off; name.ClipText = true; }
        name.Position = new Vector2(ArmCardTextX(k) * s, 3f * s * k); name.Size = new Vector2(ArmCardTextW(k) * s, 61f * s * k); row.AddChild(name); _armCardNames.Add((name, name.Text, name.Size.X));   // sized with its list's other names (StyleArmCards)
    }

    /// <summary>A step's rows were rebuilt or the page changed: restyle at the next layout.</summary>
    double _armPoll;
    int _armPendingTries; int _armSilCount;
    /// <summary>
    /// Lays the page out again after a quarter second only while install art is still arriving, and styles it again only when something new has arrived (the art became ready, or one more silhouette). It used to restyle every
    /// quarter second for as long as any silhouette was missing, which redrew every tile and card constantly (owner, 2026-11-18: "the slots are flashing"); it now gives up after ten seconds.
    /// </summary>
    void ArmouryPoll(double delta)
    {
        if (!_arm) return;
        _armPoll += delta;
        if (_armPoll <= 0.25) return;
        _armPoll = 0;
        if (_armPending)
        {
            _armPending = false;
            bool arrived = (_armArt?.Ready == true) != _armArtReadySeen || _armSilhouettes.Count != _armSilCount;
            if (arrived) { _armArtReadySeen = _armArt?.Ready == true; _armSilCount = _armSilhouettes.Count; _armSig = ""; }
            else if (++_armPendingTries <= 40) _armPending = true;
        }
        LayoutArmoury();
    }
    bool _armArtReadySeen;

    void ArmouryDirty() { _armSig = ""; if (_arm) Callable.From(LayoutArmoury).CallDeferred(); }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && _arm) Callable.From(LayoutArmoury).CallDeferred();
    }
}
