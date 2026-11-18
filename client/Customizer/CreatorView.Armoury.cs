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
    HFlowContainer? _armDock;
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
        Button Tab(string text, string tip, Action press) { var t = new Button { Text = text, ToggleMode = true, FocusMode = FocusModeEnum.All, TooltipText = tip, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; t.Pressed += () => press(); pills.AddChild(t); return t; }
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
        _armDock = new HFlowContainer { Name = "ArmouryDock" };
        _armDock.AddThemeConstantOverride("h_separation", 3); _armDock.AddThemeConstantOverride("v_separation", 3);
        _armCanvas.AddChild(_armDock);
        foreach (var b in new Button?[] { _rename, _copy, _reset, _clear, _revert, _save, _setActive, _look, _deployButton }) if (b is not null) { b.CustomMinimumSize = Vector2.Zero; Move(b, _armDock); }
        Move(_unsaved, _armDock); Move(_status, _armDock);
        _status.SizeFlagsHorizontal = SizeFlags.ShrinkBegin; _status.ClipText = false;
        // BACK and LEAVE MATCH (the old back button is the concept's BACK plate).
        Move(_backButton!, _armCanvas); _armBack = _backButton;
        if (_leaveButton is not null) Move(_leaveButton, _armDock);
        _armBack!.CustomMinimumSize = Vector2.Zero;
        _armCanvas.MoveChild(_armWings, _armCanvas.GetChildCount() - 1);
        Callable.From(LayoutArmoury).CallDeferred();
    }

    /// <summary>CONFIRM LOADOUT: saves and sets the open loadout active; in a match it saves and deploys; an added page (the Librarian) saves and deploys.</summary>
    public void ConfirmLoadout()
    {
        if (AddedPage) { Save(); if (Deploy is not null) PressDeploy(); return; }
        if (IsEmpty) { Say("Choose a class for this slot first."); return; }
        if (Deploy is not null) { Save(); PressDeploy(); return; }
        SetActive();
        Save();
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
        var equipRow = equipSection.Rows.FirstOrDefault(r => r.Selected);
        string equip = equipRow?.Title ?? "NONE";
        string Perk(int i)
        {
            string? name = i == 0 ? OpenClass?.Perk(0) ?? options?.DefaultPerk?.Name : OpenClass?.Perk(1);
            return name is null ? "NO PERK" : _catalog.Perks.Perk(name)?.DisplayName ?? name;
        }
        void Go(Step step, int perk) { _step = step; if (perk >= 0) _perkSlot = perk; Refresh(focusList: true); }
        bool perks = _step == Step.Perks;
        return new ArmDetail(IsEmpty ? "NO CLASS" : weapon.Length > 0 ? weapon.ToUpperInvariant() : "WEAPONS", weaponId is { Length: > 0 } ? "install:" + weaponId : null, () => Go(Step.Weapons, -1),
        [
            new ArmTile(equip, equipRow is { Id.Length: > 0 } ? "install:" + equipRow.Id : null, _step == Step.Equipment, () => Go(Step.Equipment, -1)),
            new ArmTile(Perk(0), null, perks && _perkSlot == 0, () => Go(Step.Perks, 0)),
            new ArmTile(Perk(1), null, perks && _perkSlot == 1, () => Go(Step.Perks, 1)),
        ], IsEmpty ? null : ArmClassSlots(equip, Perk));
    }

    /// <summary>The card per loadout slot of a class: each weapon section (primary, sidearm, melee, ...), the equipment, the two perks; a press opens that slot's choices in the right column.</summary>
    List<ArmSlot> ArmClassSlots(string equip, Func<int, string> perk)
    {
        var slots = new List<ArmSlot>();
        void Pick(Step step, int perkSlot) { _step = step; if (perkSlot >= 0) _perkSlot = perkSlot; _armPicking = true; Refresh(focusList: true); }
        foreach (var section in LoadoutRows.Weapons(_catalog, Role, _previewRace, OpenClass, _services.Policy))
        {
            var sel = section.Rows.FirstOrDefault(r => r.Selected); string heading = section.Heading; int cut = heading.IndexOf("  (", StringComparison.Ordinal); if (cut > 0) heading = heading[..cut];
            slots.Add(new ArmSlot(heading, sel?.Title ?? "NONE", sel is { Id.Length: > 0 } ? "install:" + sel.Id : null, () => Pick(Step.Weapons, -1)));
        }
        slots.Add(new ArmSlot("EQUIPMENT", equip, _armIds.TryGetValue(equip.ToUpperInvariant(), out var eqId) ? "install:" + eqId : null, () => Pick(Step.Equipment, -1)));
        slots.Add(new ArmSlot("PERK 1", perk(0), null, () => Pick(Step.Perks, 0)));
        slots.Add(new ArmSlot("PERK 2", perk(1), null, () => Pick(Step.Perks, 1)));
        return slots;
    }

    List<ArmClass> ArmouryClasses()
    {
        var rows = new List<ArmClass>();
        foreach (var role in _tabRoles)
        {
            string r = role; string label = OrkLoadout.IsOrk(role) ? OrkLoadout.ClassName : LoadoutRows.ClassName(_catalog, role, _previewRace); string lower = (role + " " + label).ToLowerInvariant();
            string? icon = lower.Contains("tact") ? "arm-ico-tactical" : lower.Contains("assault") || lower.Contains("raptor") ? "arm-ico-assault" : lower.Contains("dev") || lower.Contains("hav") ? "arm-ico-devastator" : lower.Contains("ork") ? "glyph-ork" : null;
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
        Put(_armOverlay!, UiTextureBuilder.ArmRail); Put(_armTitle!, UiTextureBuilder.ArmTitle); Put(_armWings!, UiTextureBuilder.ArmWings);
        Put(_armBadge!, UiTextureBuilder.ArmBadge); Put(_armBadgeText!, UiTextureBuilder.ArmBadge);
        Put(_armRows!, new Rect2(58, 122, 198, 105));
        var detail0 = ArmouryDetail();
        Put(_armWeapon!, UiTextureBuilder.ArmWeaponIcon.Merge(UiTextureBuilder.ArmWeaponName));
        Put(_armTilesHost!, new Rect2(36, 276, 238, 74));
        Put(_armTopRow!, new Rect2(296, 70, 246, 20));   /* the faction tabs stand right over the preview they switch (Opus review 1) */ _sideToggle.Position = Vector2.Zero; _sideToggle.Size = _armTopRow!.Size;
        Put(_armStripScroll!, new Rect2(40, 124, 234, 224)); _armWeapon!.Visible = !_armSlotsTab; _armTilesHost!.Visible = !_armSlotsTab; _strip.Wrap = true; _strip.SizeFlagsHorizontal = SizeFlags.ExpandFill; _armStripScroll!.Visible = _armSlotsTab; _armRows!.Visible = !_armSlotsTab; _armWell!.Visible = _armSlotsTab; Put(_armWell, new Rect2(38, 121, 238, 229)); _armCanvas.MoveChild(_armWell, _armStripScroll.GetIndex()); 
        bool hasSlots = (detail0.Slots?.Count ?? 0) > 0; bool slotMode = !_armPicking && hasSlots;
        _armSlotScroll!.Visible = slotMode; _scroll.Visible = !slotMode; _armPickBack!.Visible = !slotMode && hasSlots;
        Put(_armSlotScroll, new Rect2(549, 93, 222, 223));   /* whole cards only: three of 78 */ Put(_armPickBack, new Rect2(712, 76, 52, 14));
        Put(_armInfo!, new Rect2(296, 68, 246, 24)); _armInfo!.Visible = false;   // the "ACTIVE:" line is not in the concept; the active loadout is the badge and the summary line
        Put(_scroll, new Rect2(549, 93, 222, 223)); Put(_armColumnTitle!, new Rect2(551, 77, 212, 14));
        Put(_summary, new Rect2(272, 380, 370, 13));
        Put(_armDock!, new Rect2(272, 395, 374, 40));
        var rightBox = _preview.GetParent<Control>(); Put(rightBox, new Rect2(296, 94, 246, 284));
        Put(_armSteps!, UiTextureBuilder.ArmHeader);
        Put(_armConfirm!, UiTextureBuilder.ArmConfirm);
        Put(_armBack!, UiTextureBuilder.ArmBack);
        string badge = "CLASSIC PVP"; ExtrasArmouryBadge(ref badge);   // EXTRAS-HOOK: the mode badge
        var detail = detail0; _armDetailNow = detail; var classes = ArmouryClasses();
        string sig = string.Join("|", classes.Select(c => c.Label + c.Selected)) + "#" + detail.Weapon + string.Join("|", detail.Tiles.Select(t => t.Label + t.Selected)) + "#" + badge + "#" + string.Join("|", _stepButtons.Select(b => b.Text + b.Visible)) + "#" + string.Join("|", _list.GetChildren().Select(c => c.GetInstanceId())) + "#" + Mathf.Round(s * 1000f) + "#" + _unsaved.Text.Length + string.Join("", _strip.Chips.Select(c => c.GetInstanceId())) + _armSlotsTab + _armPicking + string.Join("|", detail.Slots?.Select(x => x.Slot + x.Value) ?? []);
        if (sig == _armSig) return;
        _armSig = sig; _armScale = s;
        StyleArmoury(s, classes, detail, badge);
    }


    /// <summary>The one lettering of the page: the row face (<see cref="UiKit.ConceptFont"/>) at <paramref name="size"/> concept pixels, light on a dark plate and dark ink on gold.</summary>
    static void ArmText(Control c, float size, float s, Color rest, Color pressed)
    {
        c.AddThemeFontOverride("font", UiKit.ConceptFont());
        c.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(size * s));
        c.AddThemeConstantOverride("outline_size", 0);
        foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_disabled_color" }) c.AddThemeColorOverride(n, rest);
        foreach (var n in new[] { "font_pressed_color", "font_hover_pressed_color" }) c.AddThemeColorOverride(n, pressed);
    }

    static void ArmLabel(Label l, float size, float s, Color colour)
    {
        l.AddThemeFontOverride("font", UiKit.ConceptFont());
        l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(size * s));
        l.AddThemeConstantOverride("outline_size", 0);
        l.AddThemeConstantOverride("line_spacing", -Mathf.RoundToInt(0.32f * size * s));
        l.AddThemeColorOverride("font_color", colour);
    }

    static void ArmPlate(Button b, string dark, string gold, PaintedButtons.Pad dp, PaintedButtons.Pad gp, float s, bool goldFocus = true)
    {
        PaintedButtons.Apply(b, dark, gold, dp, gp, s, goldFocus, paintedText: false, noGlow: true);
    }

    void StyleArmoury(float s, List<ArmClass> classes, ArmDetail detail, string badge)
    {
        // ---- the rail: class rows ----
        foreach (var c in _armRows!.GetChildren()) { _armRows.RemoveChild(c); c.QueueFree(); }
        for (int i = 0; i < classes.Count && i < 4; i++)
        {
            var row = classes[i]; var r = new Rect2(0, new[] { 2.75f, 30f, 55f, 80f }[i], 198, 23.5f);
            var b = new Button { Text = row.Real ?? row.Label, ToggleMode = true, FocusMode = FocusModeEnum.All, Alignment = HorizontalAlignment.Left, ClipText = true, Name = "ArmClass" + i };
            b.Position = r.Position * s; b.Size = r.Size * s; b.TooltipText = row.Real ?? row.Label;
            b.ButtonPressed = row.Selected;
            var press = row.Press;
            b.Pressed += () => { press(); };
            _armRows.AddChild(b);
            ArmPlate(b, "arm-row-dark", "arm-row-gold", new PaintedButtons.Pad(2, 1, 2, 1), new PaintedButtons.Pad(10, 4.25f, 9, 4.25f), s);
            ArmText(b, ArmNameSize, s, ArmLight, ArmDark);
            foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) b.AddThemeColorOverride(n, Colors.Transparent);   // the plate's name is the label below (one side's class name, upper case); the button keeps both sides' name
            var nameLabel = new Label { Text = row.Label, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, ClipText = true };
            ArmLabel(nameLabel, ArmNameSize, s, row.Selected ? ArmDark : ArmLight); nameLabel.Position = new Vector2(76f, 0f) * s; nameLabel.Size = new Vector2(116f, 23.5f) * s; b.AddChild(nameLabel);
            if (row.Icon is not null && ConceptArt.Get(row.Icon) is { } tex)
            {
                var ico = new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.LinearWithMipmaps, Modulate = row.Selected ? ArmDark : ArmLight };
                ico.Position = new Vector2(14f * s, 0.5f * s); ico.Size = new Vector2(44f * s, 22.5f * s); b.AddChild(ico);
            }
        }
        // ---- the weapon row ----
        var wb = _armWeaponBtn!; var wr = UiTextureBuilder.ArmWeaponIcon.Merge(UiTextureBuilder.ArmWeaponName);
        wb.Position = Vector2.Zero; wb.Size = wr.Size * s; wb.Text = detail.Weapon; wb.TooltipText = detail.Weapon;
        wb.AddThemeStyleboxOverride("normal", new StyleBoxEmpty()); wb.AddThemeStyleboxOverride("hover", new StyleBoxEmpty()); wb.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty()); wb.AddThemeStyleboxOverride("disabled", new StyleBoxEmpty());
        wb.AddThemeStyleboxOverride("focus", PaintedButtons.Ring(s, 0.5f));
        foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) wb.AddThemeColorOverride(n, Colors.Transparent);
        var iconRect = new Rect2(UiTextureBuilder.ArmWeaponIcon.Position - wr.Position, UiTextureBuilder.ArmWeaponIcon.Size);
        _armWeaponIcon!.Texture = ArmIcon(detail.WeaponIcon, "glyph-gun"); _armWeaponIcon.Modulate = detail.WeaponIcon?.StartsWith("install:", StringComparison.Ordinal) == true ? ArmEngrave : Colors.White;
        _armWeaponIcon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        _armWeaponIcon.StretchMode = TextureRect.StretchModeEnum.Scale; _armWeaponIcon.Position = (iconRect.Position + new Vector2(4, 4)) * s; _armWeaponIcon.Size = new Vector2(70, 28) * s;
        foreach (var old in _armWeaponIcon.GetChildren()) { _armWeaponIcon.RemoveChild(old); old.QueueFree(); }
        ArmShadow(_armWeaponIcon, s);
        _armWeaponName!.Text = detail.Weapon; ArmLabel(_armWeaponName, ArmNameSize, s, ArmLight);
        _armWeaponName.Position = new Vector2(UiTextureBuilder.ArmWeaponName.Position.X - wr.Position.X + 9f, UiTextureBuilder.ArmWeaponName.Position.Y - wr.Position.Y) * s; _armWeaponName.Size = new Vector2(UiTextureBuilder.ArmWeaponName.Size.X - 30f, UiTextureBuilder.ArmWeaponName.Size.Y) * s;
        // ---- the tiles ----
        foreach (var c in _armTilesHost!.GetChildren()) { _armTilesHost.RemoveChild(c); c.QueueFree(); }
        _armTileButtons.Clear();
        float[] tx = [7f, 84.5f, 162f];
        for (int i = 0; i < 3 && i < detail.Tiles.Count; i++)
        {
            var t = detail.Tiles[i];
            var b = new Button { Text = t.Label, ToggleMode = true, FocusMode = FocusModeEnum.All, Name = "ArmTile" + i, TooltipText = t.Label };
            b.Position = new Vector2(tx[i], 5f) * s; b.Size = new Vector2(74f, 66f) * s; b.ButtonPressed = t.Selected; b.ClipText = true; b.CustomMinimumSize = Vector2.Zero;
            var press = t.Press; b.Pressed += () => press();
            _armTilesHost.AddChild(b); _armTileButtons.Add(b);
            PaintedButtons.Apply(b, "arm-tile-dark", "arm-tile-gold", new PaintedButtons.Pad(1, 2, 1, 2), new PaintedButtons.Pad(3, 3, 3, 3), s, true, paintedText: true, noGlow: true);
            int paren = t.Label.IndexOf(" (", StringComparison.Ordinal); var l = new Label { Text = (paren > 0 ? t.Label[..paren] : t.Label).ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, AutowrapMode = TextServer.AutowrapMode.Word };
            ArmLabel(l, ArmTileSize, s, t.Selected ? new Color(1f, 0.95f, 0.8f) : ArmLight);
            l.Position = new Vector2(7f, 30f) * s; l.Size = new Vector2(60f, 31f) * s; b.AddChild(l);   // clear of the bezels
            if (ArmIcon(t.Icon, i == 0 ? "glyph-grenade" : "glyph-perk") is { } tex)
            { var ic = new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.LinearWithMipmaps, Position = new Vector2(14f, 6f) * s, Size = new Vector2(46f, 30f) * s, Modulate = ArmInk(tex) }; b.AddChild(ic); ArmShadow(ic, s); }
        }
        // ---- the header tab strip: CLASS, SLOTS, ARMOUR & PAINT ----
        _armTabClass!.SetPressedNoSignal(!_armSlotsTab); _armTabSlots!.SetPressedNoSignal(_armSlotsTab);
        foreach (var b in new[] { _armTabClass, _armTabSlots, _armTabLook! })
        {
            b.CustomMinimumSize = Vector2.Zero; b.Alignment = HorizontalAlignment.Center;
            PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, noGlow: true);
            ArmText(b, ArmPillSize + 1f, s, ArmLight, ArmDark);
        }
        PaintedButtons.Apply(_armPickBack!, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1), new PaintedButtons.Pad(1), s * 0.45f, true, paintedText: false, noGlow: true); ArmText(_armPickBack, ArmPillSize + 1f, s, ArmLight, ArmDark);
        BuildArmSlotCards(s, detail);
        // The SLOTS tab: a dark well with the chips as plates (one lettering; the open loadout's chip gold; the state words in the tooltip, the star kept).
        _armWell!.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.055f, 0.042f, 0.022f), BorderColor = new Color(0.50f, 0.37f, 0.12f), BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1 });
        var openChip = _strip.Open is { } opened ? _strip.ChipOf(opened) : null;
        foreach (var chip in _strip.Chips)
        {
            chip.CustomMinimumSize = new Vector2(70f * s, 31f * s); chip.ClipText = true;
            bool isOpen = chip == openChip;
            PaintedButtons.Apply(chip, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, goldRest: isOpen, noGlow: true);
            ArmText(chip, 8.2f, s, isOpen ? ArmDark : ArmLight, ArmDark);
            foreach (var n in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" }) if (chip.GetThemeStylebox(n) is StyleBoxTexture sb) { sb.ContentMarginLeft = 12f * s; sb.ContentMarginRight = 3f * s; }
            chip.AddThemeConstantOverride("line_spacing", -Mathf.RoundToInt(2.6f * s));
            foreach (var l in chip.GetChildren().OfType<Label>()) l.Visible = false;
        }
        // ---- CONFIRM LOADOUT, BACK, the badge ----
        ArmPlate(_armConfirm!, "arm-confirm", "arm-confirm", new PaintedButtons.Pad(0), new PaintedButtons.Pad(0), s, false);
        ArmText(_armConfirm, ArmConfirmSize, s, ArmDark, ArmDark); _armConfirm.AddThemeColorOverride("font_hover_color", new Color(0.2f, 0.12f, 0.02f));
        _armConfirm.Text = "CONFIRM LOADOUT"; _armConfirm.ClipText = false;
        ArmPlate(_armBack!, "arm-back", "arm-back", new PaintedButtons.Pad(0), new PaintedButtons.Pad(0), s, false);
        ArmText(_armBack, ArmBackSize, s, ArmLight, ArmLight);
        ArmLabel(_armBadgeText!, ArmBadgeSize, s, ArmLight); _armBadgeText.Text = badge;
        // ---- the dock's plates and the lines ----
        foreach (var c in _armDock!.GetChildren())
        {
            if (c is Button b)
            {
                b.AddThemeConstantOverride("h_separation", 0);
                b.CustomMinimumSize = new Vector2(0, 17f * s);
                PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, noGlow: true);
                ArmText(b, ArmPlateSize * 0.86f, s, ArmLight, ArmDark); b.AddThemeFontSizeOverride("font_size", Mathf.Max(11, Mathf.RoundToInt(ArmPlateSize * 0.86f * s)));
                foreach (var n in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" }) if (b.GetThemeStylebox(n) is StyleBoxTexture sb) { sb.ContentMarginLeft = 4f * s; sb.ContentMarginRight = 4f * s; }
            }
            else if (c is Label l) ArmLabel(l, ArmPlateSize, s, ArmLight);
        }
        foreach (var l in new[] { _summary, _activeLabel, _banner }) ArmLabel(l, 8.8f, s, ArmDim);
        ArmLabel(_banner, 8.8f, s, new Color(0.97f, 0.82f, 0.4f));
        _summary.ClipText = true;
        // The faction / mode pills above the preview.
        foreach (var b in _sideToggle.GetChildren().OfType<Button>())
        {
            b.CustomMinimumSize = new Vector2(0, 18f * s); b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(1, 1, 1, 1), new PaintedButtons.Pad(1, 1, 1, 1), s * 0.45f, true, paintedText: false, noGlow: true);
            ArmText(b, ArmPlateSize, s, ArmLight, ArmDark);
        }
        StyleArmCards(s);
        if (_armSlotScroll!.Visible) _armColumnTitle!.Text = "LOADOUT";
    }

    /// <summary>One card per loadout slot (owner, 2026-11-15), at the concept's card positions: the slot's name small above its content; the column scrolls when there are more cards than fit.</summary>
    void BuildArmSlotCards(float s, ArmDetail detail)
    {
        foreach (var c in _armSlotList!.GetChildren()) { _armSlotList.RemoveChild(c); c.QueueFree(); }
        _armSlotList.AddThemeConstantOverride("separation", Mathf.RoundToInt((UiTextureBuilder.ArmCardPitch - UiTextureBuilder.ArmCard.Size.Y) * s));
        if (ConceptArt.Get("arm-card") is not { } card || detail.Slots is null) return;
        foreach (var slot in detail.Slots)
        {
            var b = new Button { Text = slot.Slot + ": " + slot.Value, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, UiTextureBuilder.ArmCard.Size.Y * s), TooltipText = slot.Slot + ": " + slot.Value, ClipText = true, Name = "ArmSlot" + _armSlotList.GetChildCount() };
            var press = slot.Press; b.Pressed += () => press();
            StyleBoxTexture Box(Color mod) => new() { Texture = card, ModulateColor = mod, DrawCenter = true, AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch };
            var gold = new Color(1.3f, 1.12f, 0.78f);
            b.AddThemeStyleboxOverride("normal", Box(Colors.White)); b.AddThemeStyleboxOverride("hover", Box(ConceptArt.Warm)); b.AddThemeStyleboxOverride("pressed", Box(gold)); b.AddThemeStyleboxOverride("focus", Box(gold)); b.AddThemeStyleboxOverride("disabled", Box(Colors.Gray));
            foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) b.AddThemeColorOverride(n, Colors.Transparent);
            var cap = new Label { Text = slot.Slot.ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, ClipText = true, VerticalAlignment = VerticalAlignment.Center };
            ArmLabel(cap, ArmTileSize * 0.85f, s, new Color(0.97f, 0.82f, 0.4f)); cap.Position = new Vector2(79f, 11f) * s; cap.Size = new Vector2(126f, 11f) * s; b.AddChild(cap);
            var val = new Label { Text = slot.Value.ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.Word, ClipText = true, VerticalAlignment = VerticalAlignment.Center };
            ArmLabel(val, ArmNameSize, s, ArmLight); val.Position = new Vector2(79f, 23f) * s; val.Size = new Vector2(126f, 34f) * s; b.AddChild(val);
            if (ArmIcon(slot.Icon, EngravedGlyphs.For(slot.Slot)) is { } tex) { var ic = new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.LinearWithMipmaps, Position = new Vector2(16f, 8f) * s, Size = new Vector2(50f, 50f) * s, Modulate = ArmInk(tex) }; b.AddChild(ic); ArmShadow(ic, s); }
            _armSlotList.AddChild(b);
        }
    }

    /// <summary>The right column: every choice row as a concept card (icon tile at the left, the name, a gold wash when chosen); headings and notes in the one face.</summary>
    void StyleArmCards(float s)
    {
        // The first heading of the step is the column's title above the first card (so the first card stands where the concept's does); later headings stay in the list.
        _armColumnTitle!.Text = ""; ArmLabel(_armColumnTitle, ArmTileSize, s, new Color(0.97f, 0.82f, 0.4f));
        if (_list.GetChildCount() > 0 && _list.GetChild(0) is Label first) { _armColumnTitle.Text = first.Text; first.Visible = false; }
        _list.AddThemeConstantOverride("separation", Mathf.RoundToInt((UiTextureBuilder.ArmCardPitch - UiTextureBuilder.ArmCard.Size.Y) * s));
        if (!_list.GetChildren().Any(n => n.Name == "ArmPad")) _list.AddChild(new Control { Name = "ArmPad", CustomMinimumSize = new Vector2(0, 8f * s), MouseFilter = MouseFilterEnum.Ignore });   // room under the last card so it scrolls fully into view
        foreach (var c in _list.GetChildren())
        {
            switch (c)
            {
                case UiKit.Row row: StyleArmCard(row, s); break;
                case Label l: ArmLabel(l, ArmTileSize, s, new Color(0.97f, 0.82f, 0.4f)); l.CustomMinimumSize = new Vector2(0, 13f * s); break;
            }
        }
    }

    void StyleArmCard(UiKit.Row row, float s)
    {
        if (ConceptArt.Get("arm-card") is not { } card) return;
        row.CustomMinimumSize = new Vector2(0, UiTextureBuilder.ArmCard.Size.Y * s);
        row.AutowrapMode = TextServer.AutowrapMode.Word; row.ClipText = false;
        StyleBoxTexture Box(Color mod, float grow) => new()
        {
            Texture = card, ModulateColor = mod, DrawCenter = true, ContentMarginLeft = 79f * s, ContentMarginRight = 10f * s,
            ExpandMarginLeft = grow * s, ExpandMarginRight = grow * s, ExpandMarginTop = grow * s, ExpandMarginBottom = grow * s,
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
        };
        var gold = new Color(1.3f, 1.12f, 0.78f);   // hover and focus wash; a chosen row is marked by its check glyph in the icon tile
        row.AddThemeStyleboxOverride("normal", Box(Colors.White, 0f));
        row.AddThemeStyleboxOverride("hover", Box(ConceptArt.Warm, 0f));
        row.AddThemeStyleboxOverride("pressed", Box(gold, 0f));
        row.AddThemeStyleboxOverride("focus", Box(gold, 0f));
        row.AddThemeStyleboxOverride("disabled", Box(new Color(0.55f, 0.55f, 0.55f), 0f));
        ArmText(row, ArmNameSize, s, ArmLight, ArmLight);
        foreach (var n in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) row.AddThemeColorOverride(n, Colors.Transparent);   // the name is drawn by the label below (upper case, as the concept); the row keeps its text
        row.Alignment = HorizontalAlignment.Left;
        string lower = row.Text.ToLowerInvariant();
        string? icon = lower.Contains("force sword") || lower == "sword" ? "arm-ico-sword" : lower.Contains("chain lightning") ? "arm-ico-lightning" : lower.Contains("psychic stress") ? "arm-ico-stress" : "glyph-perk";   // no picture: the faint engraved type glyph
        foreach (var child in row.GetChildren())
        {
            if (child is ColorRect bar) bar.Visible = false;
            else if (child is Label tags) ArmLabel(tags, ArmTileSize * 0.8f, s, ArmDim);
            else if (child is Control glyph && glyph.GetType().Name == "Glyph") { glyph.Position = new Vector2(570f - 549f + 12f, 118f - 93f - 12f) * s; glyph.Size = new Vector2(26f, 26f) * s; glyph.Visible = icon is null; }
        }
        foreach (var old in row.GetChildren().OfType<Node>().Where(n => n.HasMeta("arm_card")).ToList()) { row.RemoveChild(old); old.QueueFree(); }
        if (icon == "glyph-perk" && _armIds.TryGetValue(row.Text.ToUpperInvariant(), out var wargearId)) icon = "install:" + wargearId;   // a weapon or grenade of the install: its own silhouette
        if (icon is not null && ArmIcon(icon, "glyph-perk") is { } tex)
            row.AddChild(new TextureRect { Texture = tex, MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = icon.StartsWith("install:", StringComparison.Ordinal) ? TextureRect.StretchModeEnum.KeepAspectCentered : TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.LinearWithMipmaps, Position = new Vector2(16f, 8f) * s, Size = new Vector2(50f, 50f) * s, Modulate = ArmInk(tex) }.With(t => t.SetMeta("arm_card", true)));
        var name = new Label { Text = row.Text.ToUpperInvariant(), MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.Word, ClipText = true };
        name.SetMeta("arm_card", true); ArmLabel(name, ArmNameSize, s, ArmLight);
        name.Position = new Vector2(79f, 3f) * s; name.Size = new Vector2(104f, 61f) * s; row.AddChild(name);
    }

    /// <summary>A step's rows were rebuilt or the page changed: restyle at the next layout.</summary>
    double _armPoll;
    void ArmouryPoll(double delta) { if (!_arm) return; _armPoll += delta; if (_armPoll > 0.25) { _armPoll = 0; if (_armPending) { _armPending = false; _armSig = ""; } LayoutArmoury(); } }

    void ArmouryDirty() { _armSig = ""; if (_arm) Callable.From(LayoutArmoury).CallDeferred(); }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && _arm) Callable.From(LayoutArmoury).CallDeferred();
    }
}
