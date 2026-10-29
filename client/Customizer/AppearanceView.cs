using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmFormats.Dds;
using SmSim;

namespace SmClient.Customizer;

/// <summary>
/// The character customizer (docs/customizer-design.md F6): the look of the open custom loadout for one race, in five sections. CHAPTER (presets, and the whole
/// armour sets), ARMOUR (nine parts; the class's forced parts are locked and say so), COLOURS (part or all, primary / pattern / detail, pattern on or off, the
/// palette in named groups), BADGES (left and right pad: a chapter badge, none, your own image, "My emblems") and EYES (lens colour: our addition). Edits go to the shared
/// <see cref="ProfileSession"/>; Undo steps back through them. The 3D preview dresses the model per part and per colour through <see cref="AppearanceAssets"/> (T6); focusing
/// or hovering a choice previews it (Enter commits, leaving it puts the look back).
/// </summary>
public sealed partial class AppearanceView : Control
{
    enum Section { Chapter, Armour, Colours, Badges, Eyes }

    readonly CustomizerServices _services;
    readonly ProfileSession _session;
    readonly CustomizationCatalog _catalog;
    readonly Action _onDone;

    int _slot = 1;
    string _race = CustomizationCatalog.SpaceMarine;
    Section _section = Section.Chapter;
    string? _armourSlot;                 // ARMOUR: the part whose pieces are listed (null: the list of parts)
    string? _part;                       // COLOURS: the part edited (null: all parts)
    PaintChannel _channel = PaintChannel.Primary;
    string _pad = "leftshoulder";
    readonly Stack<CustomizationProfile> _undo = new();

    readonly Label _title, _status, _unsaved, _classLabel, _colourName;
    readonly HBoxContainer _sideToggle = UiKit.HBox(8), _lookRow = UiKit.HBox(8);
    readonly VBoxContainer _lookTools = UiKit.VBox(8);
    readonly VBoxContainer _sections = UiKit.VBox(8), _panel = UiKit.VBox(8), _card = UiKit.VBox(6);
    readonly ScrollContainer _scroll;
    readonly PreviewStage _preview;
    readonly List<Button> _sectionButtons = new();
    OptionButton? _lookPicker;
    Button? _undoButton, _resetButton, _saveButton;
    double _statusUntil;
    readonly PadProbe _probe;
    readonly Dictionary<string, Texture2D?> _thumbnails = new(StringComparer.Ordinal);

    public PreviewStage Preview => _preview;
    Control Host => (Control)(GetParent() ?? this);

    public AppearanceView(CustomizerServices services, Action onDone)
    {
        _services = services; _session = services.Session!; _catalog = services.Catalog!; _onDone = onDone;
        _probe = new PadProbe(services);
        SetAnchorsPreset(LayoutPreset.FullRect);
        Name = "Appearance";
        AddChild(UiKit.Background().With(n => n.SetAnchorsPreset(LayoutPreset.FullRect)));
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var (side, v) in new[] { ("left", 84), ("right", 84), ("top", 22), ("bottom", 20) }) margin.AddThemeConstantOverride("margin_" + side, v);
        AddChild(margin);
        var root = UiKit.VBox(8);
        margin.AddChild(root);

        var header = UiKit.HBox(16);
        _title = UiKit.Text("APPEARANCE", UiKit.HeaderSize, UiKit.Gold);
        header.AddChild(_title);
        header.AddChild(_lookRow);
        header.AddChild(UiKit.Text("Side:", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center));
        header.AddChild(_sideToggle);
        // Fills the space up to DONE and is cut (full text in its tooltip) rather than pushing DONE off the window (fix/frontend-fit).
        _classLabel = UiKit.Text("", UiKit.CaptionSize, UiKit.White, HorizontalAlignment.Right);
        _classLabel.VerticalAlignment = VerticalAlignment.Center;
        _classLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _classLabel.ClipText = true;
        _classLabel.MouseFilter = MouseFilterEnum.Pass;
        header.AddChild(_classLabel);
        header.AddChild(UiKit.Button("DONE (Esc)", Done, 190));
        root.AddChild(header);

        var main = UiKit.HBox(16);
        main.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(main);
        var sectionsColumn = UiKit.VBox(24);
        sectionsColumn.AddChild(_sections);
        sectionsColumn.AddChild(_lookTools);
        var sectionsPanel = UiKit.Panel(sectionsColumn, 10);
        sectionsPanel.CustomMinimumSize = new Vector2(230, 0);
        main.AddChild(sectionsPanel);
        _scroll = UiKit.Scroll(_panel);
        _scroll.CustomMinimumSize = new Vector2(660, 0);
        _scroll.SizeFlagsHorizontal = SizeFlags.Fill;
        main.AddChild(_scroll);
        var right = UiKit.VBox(10);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _preview = new PreviewStage(services);
        right.AddChild(_preview);
        _colourName = UiKit.Text("", UiKit.BodySize, UiKit.White);
        var cardPanel = UiKit.Panel(_card, 12);
        cardPanel.CustomMinimumSize = new Vector2(0, 200);
        right.AddChild(cardPanel);
        main.AddChild(right);

        var bar = UiKit.HBox(8);
        _resetButton = UiKit.Button("Reset section", ResetSection, 190);
        _undoButton = UiKit.Button("Undo", Undo, 110);
        bar.AddChild(_resetButton);
        bar.AddChild(_undoButton);
        _unsaved = UiKit.Text("", UiKit.BodySize, UiKit.LightRed);
        _unsaved.VerticalAlignment = VerticalAlignment.Center;
        bar.AddChild(_unsaved);
        _status = UiKit.Text("", UiKit.CaptionSize, UiKit.White);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.ClipText = true;
        bar.AddChild(_status);
        _saveButton = UiKit.Button("SAVE (Ctrl+S)", Save, 200);
        bar.AddChild(_saveButton);
        bar.AddChild(UiKit.Button("DONE - BACK TO LOADOUTS", Done, 320));
        root.AddChild(bar);

        BuildSectionButtons();
        _session.Changed += OnSessionChanged;
    }

    public override void _ExitTree() => _session.Changed -= OnSessionChanged;

    void OnSessionChanged()
    {
        if (IsVisibleInTree()) Rebuild(false);
    }

    /// <summary>Opens the look of a custom loadout (its slot number) on a race; <paramref name="section"/> picks the section ("chapter", "armour", "colours", "badges", "eyes").</summary>
    public void Open(int slot, string race, string? section = null)
    {
        _slot = slot; _race = race;
        _armourSlot = null; _part = null; _channel = PaintChannel.Primary;
        _undo.Clear();
        if (section is not null && Enum.TryParse<Section>(section, true, out var s)) _section = s;
        BuildSideToggle();
        Rebuild(true);
    }

    public void OpenSection(string section, string? part = null)
    {
        if (Enum.TryParse<Section>(section, true, out var s)) _section = s;
        if (part is not null) { _armourSlot = part; _part = part; }
        Rebuild(true);
    }

    // ---- current state ----

    CustomClass? Class => _session.Working.ClassAt(_slot);
    string Role => Class?.Role ?? ProfileStore.FirstRole(_catalog);
    RaceCatalog? Race => _catalog.Race(_race);

    AppearanceProfile Look() =>
        _session.Working.Appearance(Class?.Appearance.For(_race)) is { } own && own.Race == _race ? own : AppearanceProfile.Empty("", _race, "Default look");

    ResolvedAppearance? Resolved() => AppearanceEdits.Resolve(_catalog, Look(), Role, Class, piece => _probe.Takes(piece));

    /// <summary>Runs an edit on this loadout's look (creating the look first when it has none), remembering the profile for Undo.</summary>
    void Apply(Func<AppearanceProfile, ResolvedAppearance?, AppearanceProfile> edit, string? message = null)
    {
        if (Class is null) return;
        var resolved = Resolved();
        var before = _session.Working;
        bool failed = false;
        _session.Edit(p =>
        {
            var (p2, look) = AppearanceEdits.EnsureLook(p, _slot, _race);
            if (look is null) { failed = true; return p; }
            var changed = edit(look, resolved);
            return ReferenceEquals(changed, look) ? p2 : AppearanceEdits.ReplaceAppearance(p2, changed);
        });
        if (failed) { Say($"You can keep {CustomizationProfile.MaxAppearancesPerRace} looks per side. Delete one first."); return; }
        if (!ReferenceEquals(before, _session.Working))
        {
            _undo.Push(before);
            while (_undo.Count > 60) { var keep = _undo.ToArray().Take(50).Reverse().ToArray(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
        }
        if (message is not null) Say(message);
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        var back = _undo.Pop();
        _session.Edit(_ => back);
        Say("Undone.");
    }

    // ---- static parts ----

    void BuildSideToggle()
    {
        foreach (var c in _sideToggle.GetChildren()) { _sideToggle.RemoveChild(c); c.QueueFree(); }
        foreach (var (race, text) in _services.Factions())   // added content E2: the profile's factions (no Ork in the faithful profile)
        {
            string r = race;
            _sideToggle.AddChild(UiKit.Toggle(text, _race == race, on =>
            {
                if (on && _race != r) { _race = r; _armourSlot = null; _part = null; BuildSideToggle(); Rebuild(false); }
                else if (!on) BuildSideToggle();
            }, 180));
        }
    }

    void BuildSectionButtons()
    {
        foreach (var c in _sections.GetChildren()) { _sections.RemoveChild(c); c.QueueFree(); }
        _sectionButtons.Clear();
        var names = new List<(Section, string)> { (Section.Chapter, "CHAPTER"), (Section.Armour, "ARMOUR"), (Section.Colours, "COLOURS"), (Section.Badges, "BADGES") };
        names.Add((Section.Eyes, "EYES (lens)"));
        foreach (var (section, text) in names)
        {
            var s = section;
            var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, ToggleMode = true, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 56) };
            b.Pressed += () => { _section = s; _armourSlot = null; Rebuild(true); };
            _sections.AddChild(b);
            _sectionButtons.Add(b);
        }
    }

    void RebuildLookRow()
    {
        foreach (var c in _lookRow.GetChildren()) { _lookRow.RemoveChild(c); c.QueueFree(); }
        foreach (var c in _lookTools.GetChildren()) { _lookTools.RemoveChild(c); c.QueueFree(); }
        var profile = _session.Working;
        var looks = profile.AppearancesFor(_race).ToList();
        var own = Class?.Appearance.For(_race);
        _lookPicker = new OptionButton { CustomMinimumSize = new Vector2(250, UiKit.MinHit), FocusMode = FocusModeEnum.All, Disabled = Class is null };
        _lookPicker.AddItem("Default look", 0);
        for (int i = 0; i < looks.Count; i++) _lookPicker.AddItem(looks[i].Name, i + 1);
        _lookPicker.AddItem(looks.Count < CustomizationProfile.MaxAppearancesPerRace ? "New look..." : "(library full)", looks.Count + 1);
        int selected = own is null ? 0 : looks.FindIndex(l => l.Id == own) + 1;
        _lookPicker.Select(Math.Max(selected, 0));
        _lookPicker.ItemSelected += index =>
        {
            int i = (int)index;
            if (i == 0) { _session.Edit(p => AppearanceEdits.Assign(p, _slot, _race, null)); Say("This loadout wears the default look."); }
            else if (i <= looks.Count) { _session.Edit(p => AppearanceEdits.Assign(p, _slot, _race, looks[i - 1].Id)); Say($"Wearing \"{looks[i - 1].Name}\"."); }
            else
            {
                var (added, id) = AppearanceEdits.AddAppearance(_session.Working, _race);
                if (id is null) { Say($"You can keep {CustomizationProfile.MaxAppearancesPerRace} looks per side."); Rebuild(false); return; }
                _session.Edit(_ => AppearanceEdits.Assign(added, _slot, _race, id));
                Say("A new look was created.");
            }
        };
        _lookRow.AddChild(_lookPicker);
        if (own is not null && profile.Appearance(own) is { } current)
        {
            _lookTools.AddChild(UiKit.Text("THIS LOOK", UiKit.CaptionSize, UiKit.Grey));
            _lookTools.AddChild(UiKit.Button("Rename look", () => UiKit.Dialog.Prompt(this, Host, "Rename look", $"Up to {LoadoutEdits.MaxNameLength} characters.", current.Name, ["Rename", "Cancel"], (i, text) =>
            {
                if (i == 0) _session.Edit(p => AppearanceEdits.ReplaceAppearance(p, AppearanceEdits.Rename(p.Appearance(current.Id) ?? current, text)));
            }), 0));
            _lookTools.AddChild(UiKit.Button("Delete look", () => UiKit.Dialog.Open(this, Host, "Delete this look?", $"\"{current.Name}\" is removed. Loadouts that wear it go back to the default look.", ["Delete", "Cancel"], i =>
            {
                if (i == 0) _session.Edit(p => AppearanceEdits.RemoveAppearance(p, current.Id));
            }, titleColour: UiKit.LightRed), 0));
        }
    }

    // ---- rebuilding ----

    void Rebuild(bool focusFirst)
    {
        _noPreviewUntil = _clock + 0.35;
        var focusedBefore = GetViewport()?.GuiGetFocusOwner();
        int keep = focusedBefore is Button fb ? _panel.GetChildren().OfType<Control>().SelectMany(Flatten).OfType<Button>().ToList().IndexOf(fb) : -1;
        var profile = _session.Working;
        _classLabel.Text = Class is null ? "" : $"Loadout {_slot} \"{Class.Name}\": {LoadoutRows.ClassName(_catalog, Role, _race)}";
        _classLabel.TooltipText = _classLabel.Text;
        for (int i = 0; i < _sectionButtons.Count; i++)
        {
            var expected = new[] { Section.Chapter, Section.Armour, Section.Colours, Section.Badges, Section.Eyes }[i];
            _sectionButtons[i].SetPressedNoSignal(_section == expected);
        }
        RebuildLookRow();
        foreach (var c in _panel.GetChildren()) { _panel.RemoveChild(c); c.QueueFree(); }
        var look = Look();
        var resolved = Resolved();
        var focusables = new List<Control>();
        if (Class is null) _panel.AddChild(Note("Open a custom loadout in the creator first; a look belongs to a loadout."));
        else switch (_section)
        {
            case Section.Chapter: BuildChapter(look, resolved, focusables); break;
            case Section.Armour: BuildArmour(look, resolved, focusables); break;
            case Section.Colours: BuildColours(look, resolved, focusables); break;
            case Section.Badges: BuildBadges(look, resolved, focusables); break;
            case Section.Eyes: BuildEyes(look, focusables); break;
        }
        _unsaved.Text = _session.Dirty ? "* UNSAVED" : "";
        _saveButton!.Disabled = !_session.Dirty || _session.ReadOnly;
        _undoButton!.Disabled = _undo.Count == 0;
        _resetButton!.Disabled = Class is null;
        UpdatePreview(look);
        if (focusFirst)
        {
            (focusables.FirstOrDefault(f => f is Button b && b.ButtonPressed) ?? focusables.FirstOrDefault())?.FocusLater();
            Callable.From(() => _scroll.ScrollVertical = 0).CallDeferred();
        }
        else if (keep >= 0 && keep < focusables.Count) focusables[keep].FocusLater();
        ChainVertical(focusables);
    }

    static IEnumerable<Control> Flatten(Control c)
    {
        yield return c;
        foreach (var child in c.GetChildren().OfType<Control>()) foreach (var d in Flatten(child)) yield return d;
    }

    static void ChainVertical(List<Control> controls)
    {
        // Rows stacked in the panel: Up / Down walk the list in order (grids keep Godot's spatial navigation).
        var rows = controls.Where(c => c is UiKit.Row).ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) rows[i].FocusNeighborTop = rows[i].GetPathTo(rows[i - 1]);
            if (i + 1 < rows.Count) rows[i].FocusNeighborBottom = rows[i].GetPathTo(rows[i + 1]);
        }
    }

    static Label Note(string text, Color? colour = null) => UiKit.Text(text, UiKit.CaptionSize, colour ?? UiKit.Grey, HorizontalAlignment.Left, true);
    static Label Heading(string text) => UiKit.Text(text, UiKit.CaptionSize, UiKit.Gold).With(l => { l.CustomMinimumSize = new Vector2(0, 30); l.ClipText = true; l.TooltipText = text; });

    // ---- focus previews (docs/customizer-design.md Z2): focusing or hovering a choice shows it on the model, Enter commits, leaving it puts the look back ----

    double _clock, _previewEndAt = -1, _noPreviewUntil;
    bool _previewing;

    /// <summary>Shows the look with <paramref name="edit"/> applied, without changing the profile.</summary>
    void PreviewEdit(Func<AppearanceProfile, ResolvedAppearance?, AppearanceProfile> edit)
    {
        if (Class is null || _clock < _noPreviewUntil) return;   // not for the focus the screen itself sets after a rebuild
        _previewEndAt = -1;
        _previewing = true;
        _preview.SetSubject(new PreviewSubject(_race, Role, Class, edit(Look(), Resolved())));
    }

    /// <summary>Focus left a previewing choice: the real look returns shortly, unless another choice takes the preview over first.</summary>
    void PreviewEnd() => _previewEndAt = _clock + 0.25;

    void UpdatePreview(AppearanceProfile look)
    {
        _previewEndAt = -1;
        _previewing = false;
        var framing = _section switch
        {
            Section.Armour => Framing.ForSlot(_armourSlot),
            Section.Colours => Framing.ForSlot(_part),
            Section.Badges => Framing.ForSlot(_pad),
            Section.Eyes => Framing.Helmet,
            _ => Framing.Body,
        };
        _preview.SetSubject(new PreviewSubject(_race, Role, Class, look));
        _preview.SetFraming(framing);
    }

    // ---- locked by a whole set ----

    bool LockedNotice(AppearanceProfile look, List<Control> focusables)
    {
        if (!AppearanceEdits.Locked(look)) return false;
        string name = Race?.Set(look.PredefinedSet!)?.DisplayName ?? look.PredefinedSet!;
        _panel.AddChild(Note($"A whole armour set is worn ({name}): its pieces, colours and badges are fixed.", UiKit.LightRed));
        var b = UiKit.Button("Customize instead (leave the set)", () => Apply((l, _) => AppearanceEdits.DropSet(l), "Left the armour set."), 0);
        _panel.AddChild(b);
        focusables.Add(b);
        return true;
    }

    // ---- CHAPTER ----

    void BuildChapter(AppearanceProfile look, ResolvedAppearance? resolved, List<Control> focusables)
    {
        var race = Race;
        if (race is null) return;
        _panel.AddChild(Note("A chapter copies its colours and shoulder badges into this look. Armour parts you chose stay."));
        _panel.AddChild(Heading(_race == CustomizationCatalog.Chaos ? "WARBANDS" : "CHAPTERS"));
        foreach (var preset in race.Presets)
        {
            var pr = preset;
            var set = pr.Scheme.Default ?? pr.Scheme.Slots.Values.FirstOrDefault();
            bool applied = look.PresetApplied == pr.Name && !AppearanceEdits.Locked(look);
            var swatches = set is null ? null : new[] { Colour(set.Primary), Colour(set.Secondary), Colour(set.Tertiary) };
            var row = new UiKit.Row(pr.DisplayName, applied ? ["APPLIED"] : [], applied, true, applied ? UiKit.Glyph.Kind.Check : null, null, swatches) { Focused = () => { ShowPresetCard(pr); PreviewEdit((l, _) => AppearanceEdits.SetPreset(l, pr, _catalog)); }, Unfocused = PreviewEnd };
            row.Pressed += () => Apply((l, _) => AppearanceEdits.SetPreset(l, pr, _catalog), $"{pr.DisplayName} applied.");
            _panel.AddChild(row);
            focusables.Add(row);
        }
        var sets = race.PredefinedSets.ToList();
        if (sets.Count > 0)
        {
            _panel.AddChild(Heading("WHOLE ARMOUR SETS (pieces, colours and badges fixed)"));
            if (AppearanceEdits.Locked(look))
            {
                var none = new UiKit.Row("No set - customize", [], false, true) { Focused = () => ShowTextCard("No set", "Leave the whole set and edit parts, colours and badges again.") };
                none.Pressed += () => Apply((l, _) => AppearanceEdits.DropSet(l), "Left the armour set.");
                _panel.AddChild(none);
                focusables.Add(none);
            }
            foreach (var s in sets)
            {
                var set = s;
                bool worn = look.PredefinedSet == set.Name;
                var row = new UiKit.Row(set.DisplayName, worn ? ["WORN"] : [], worn, true, worn ? UiKit.Glyph.Kind.Check : null) { Focused = () => { ShowTextCard(set.DisplayName, set.Description, "A whole set: it fixes the pieces, colours and badges."); PreviewEdit((l, _) => AppearanceEdits.SetSet(l, set.Name)); }, Unfocused = PreviewEnd };
                row.Pressed += () => Apply((l, _) => AppearanceEdits.SetSet(l, set.Name), $"{set.DisplayName} worn.");
                _panel.AddChild(row);
                focusables.Add(row);
            }
        }
        if (focusables.Count > 0 && focusables[0] is UiKit.Row first) first.Focused?.Invoke();
    }

    static Color Colour(PaintColour c) => new(c.Diffuse[0], c.Diffuse[1], c.Diffuse[2]);

    string ColourTitle(PaintColour c) => _catalog.Palette.FirstOrDefault(p => p.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? c.Name;

    void ShowPresetCard(PaintPreset preset)
    {
        var set = preset.Scheme.Default ?? preset.Scheme.Slots.Values.FirstOrDefault();
        string colours = set is null ? "" : $"Primary: {ColourTitle(set.Primary)}\nPattern: {ColourTitle(set.Secondary)}\nDetail: {ColourTitle(set.Tertiary)}";
        ShowTextCard(preset.DisplayName, colours, "Enter applies it to every part.");
    }

    // ---- ARMOUR ----

    void BuildArmour(AppearanceProfile look, ResolvedAppearance? resolved, List<Control> focusables)
    {
        if (LockedNotice(look, focusables)) { ShowTextCard("Armour", "Parts are fixed while a whole set is worn."); return; }
        if (_armourSlot is null)
        {
            _panel.AddChild(Note("Choose a part. Parts the class sets itself are locked."));
            foreach (var slot in AppearanceEdits.Slots)
            {
                string s = slot;
                bool forced = resolved?.ForcedSlots.Contains(slot) == true;
                bool hidden = resolved?.HiddenSlots.Contains(slot) == true;
                string current = PieceTitle(look, resolved, slot);
                var tags = new List<string>();
                if (forced) tags.Add("SET BY CLASS");
                if (hidden) tags.Add("NOT SHOWN");
                if (look.Pieces.ContainsKey(slot)) tags.Add("CHANGED");
                var row = new UiKit.Row($"{AppearanceEdits.SlotTitle(slot)}  -  {current}", tags, false, !forced, forced ? UiKit.Glyph.Kind.Lock : null) { Focused = () => { ShowSlotCard(look, resolved, s); _preview.SetFraming(Framing.ForSlot(s)); } };
                row.Pressed += () => { if (forced) Say($"{AppearanceEdits.SlotTitle(s)}: set by class ({LoadoutRows.ClassName(_catalog, Role, _race)})."); else { _armourSlot = s; Rebuild(true); } };
                _panel.AddChild(row);
                focusables.Add(row);
            }
            if (focusables.Count > 0 && focusables[0] is UiKit.Row first) first.Focused?.Invoke();
            return;
        }
        string slotName = _armourSlot;
        var back = UiKit.Button("< All parts", () => { _armourSlot = null; Rebuild(true); }, 0);
        _panel.AddChild(back);
        focusables.Add(back);
        _panel.AddChild(Heading(AppearanceEdits.SlotTitle(slotName).ToUpperInvariant()));
        bool isForced = resolved?.ForcedSlots.Contains(slotName) == true;
        if (isForced) _panel.AddChild(Note($"Set by class: {LoadoutRows.ClassName(_catalog, Role, _race)}.", UiKit.LightRed));
        foreach (var piece in AppearanceEdits.PieceRows(_catalog, look, slotName, resolved))
        {
            var pr = piece;
            var row = new UiKit.Row(pr.Title, pr.Selected ? ["WORN"] : [], pr.Selected, !isForced, pr.Selected ? UiKit.Glyph.Kind.Check : isForced ? UiKit.Glyph.Kind.Lock : null) { Focused = () => { ShowTextCard(pr.Title, pr.Description, "Enter wears it."); if (!isForced) PreviewEdit((l, _) => AppearanceEdits.SetPiece(l, slotName, pr.Id)); }, Unfocused = PreviewEnd };
            row.Pressed += () =>
            {
                if (isForced) { Say("Set by class."); return; }
                Apply((l, _) => AppearanceEdits.SetPiece(l, slotName, pr.Id), $"{pr.Title} worn.");
            };
            _panel.AddChild(row);
            focusables.Add(row);
        }
    }

    /// <summary>A wargear file name as words ("csm_armour_mp_raptor_01_head" gives "Raptor 01 head"): the fallback when the install has no text.</summary>
    static string Readable(string wargear)
    {
        string text = wargear;
        foreach (var prefix in new[] { "csm_armour_mp_", "sm_armour_mp_", "csm_", "sm_" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { text = text[prefix.Length..]; break; }
        text = text.Replace('_', ' ').Trim();
        return text.Length == 0 ? wargear : char.ToUpperInvariant(text[0]) + text[1..];
    }

    string PieceTitle(AppearanceProfile look, ResolvedAppearance? resolved, string slot)
    {
        string? name = resolved?.Piece(slot)?.Name;
        if (name is null) return "none";
        var race = Race;
        var piece = race?.PiecesFor(slot).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (piece is not null) return piece.DisplayName;
        // A piece the class forces is not in the offered lists: the catalogue's class override has its name.
        var forced = _catalog.Class(AppearanceEdits.ClassKey(_catalog, Role, _race) ?? "")?.Overrides.FirstOrDefault(o => o.Piece.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Piece;
        if (forced is not null && forced.DisplayName != forced.Name) return forced.DisplayName;
        return Readable(name);
    }

    void ShowSlotCard(AppearanceProfile look, ResolvedAppearance? resolved, string slot)
    {
        var parts = new List<string>();
        if (resolved?.ForcedSlots.Contains(slot) == true) parts.Add($"Set by class: {LoadoutRows.ClassName(_catalog, Role, _race)}.");
        if (resolved?.HiddenSlots.Contains(slot) == true) parts.Add("Not shown on this class (the jump pack takes the back).");
        var offered = Race?.PiecesFor(slot).Count ?? 0;
        ShowTextCard(AppearanceEdits.SlotTitle(slot), $"Worn: {PieceTitle(look, resolved, slot)}\n{offered} to choose from.", string.Join("\n", parts));
    }

    // ---- COLOURS ----

    void BuildColours(AppearanceProfile look, ResolvedAppearance? resolved, List<Control> focusables)
    {
        if (LockedNotice(look, focusables)) { ShowTextCard("Colours", "Colours are fixed while a whole set is worn."); return; }
        // Part and channel.
        var partRow = UiKit.HBox(8);
        partRow.AddChild(UiKit.Text("Part", UiKit.BodySize, UiKit.White).With(l => l.VerticalAlignment = VerticalAlignment.Center));
        var parts = new OptionButton { CustomMinimumSize = new Vector2(300, UiKit.MinHit), FocusMode = FocusModeEnum.All };
        parts.AddItem("All parts (apply to all)", 0);
        for (int i = 0; i < AppearanceEdits.Slots.Count; i++) parts.AddItem(AppearanceEdits.SlotTitle(AppearanceEdits.Slots[i]), i + 1);
        parts.Select(_part is null ? 0 : AppearanceEdits.Slots.ToList().IndexOf(_part) + 1);
        parts.ItemSelected += i => { _part = i == 0 ? null : AppearanceEdits.Slots[(int)i - 1]; Rebuild(false); };
        partRow.AddChild(parts);
        _panel.AddChild(partRow);
        focusables.Add(parts);

        var channelRow = UiKit.HBox(8);
        var channelButtons = new List<Control>();
        foreach (var (channel, text) in new[] { (PaintChannel.Primary, "Primary"), (PaintChannel.Pattern, "Pattern colour"), (PaintChannel.Detail, "Detail colour") })
        {
            var ch = channel;
            var b = UiKit.Toggle(text, _channel == channel, on => { _channel = ch; Callable.From(() => Rebuild(true)).CallDeferred(); }, 0);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            channelRow.AddChild(b);
            channelButtons.Add(b);
        }
        UiKit.ChainHorizontal(channelButtons);
        _panel.AddChild(channelRow);
        focusables.AddRange(channelButtons);

        var targets = AppearanceEdits.Targets(_part);
        bool patternOn = targets.All(t => AppearanceEdits.PatternOn(resolved, t));
        var pattern = UiKit.Toggle(patternOn ? "PATTERN: ON" : "PATTERN: OFF", patternOn, on => Apply((l, r) => AppearanceEdits.SetPattern(l, targets, on, r, _catalog), on ? "Pattern on." : "Pattern off: it shows in the primary colour."), 0);
        pattern.TooltipText = "Pattern on: the pattern colour shows. Off: the pattern is drawn in the primary colour.";
        _panel.AddChild(pattern);
        focusables.Add(pattern);

        string? current = targets.Select(t => AppearanceEdits.ColourOf(resolved, t, _channel)?.Name).Distinct().Count() == 1 ? AppearanceEdits.ColourOf(resolved, targets[0], _channel)?.Name : null;
        bool custom = targets.All(t => AppearanceEdits.IsCustomColour(look, t, _channel));
        _colourName.Text = "";
        var currentLine = UiKit.Text(current is null ? "Current: mixed" : $"Current: {DisplayOf(current)}{(custom ? "" : " (chapter paint)")}", UiKit.BodySize, UiKit.White);
        _panel.AddChild(currentLine);
        var reset = UiKit.Button("Use the chapter's colour", () => Apply((l, r) => AppearanceEdits.SetColour(l, targets, _channel, null, r), "Back to the chapter's colour."), 0);
        _panel.AddChild(reset);
        focusables.Add(reset);

        var nameLine = UiKit.Text("Focus a colour to see its name.", UiKit.BodySize, UiKit.Gold);
        foreach (var group in _catalog.PaletteGroups)
        {
            _panel.AddChild(Heading($"{PrettyGroup(group.Name)}  ({group.Colours.Count})"));
            var grid = new GridContainer { Columns = 10 };
            grid.AddThemeConstantOverride("h_separation", 6);
            grid.AddThemeConstantOverride("v_separation", 6);
            foreach (var colour in group.Colours)
            {
                var c = colour;
                bool isCurrent = current is not null && current.Equals(c.Name, StringComparison.OrdinalIgnoreCase);
                var button = new UiKit.ColourButton(new Color(c.Diffuse[0], c.Diffuse[1], c.Diffuse[2]), isCurrent, c.DisplayName);
                button.Focused = () => { nameLine.Text = c.DisplayName; ShowColourCard(c, group.Name); PreviewEdit((l, r) => AppearanceEdits.SetColour(l, targets, _channel, c.Name, r)); };
                button.Unfocused = PreviewEnd;
                button.Pressed += () => Apply((l, r) => AppearanceEdits.SetColour(l, targets, _channel, c.Name, r), $"{c.DisplayName} chosen.");
                grid.AddChild(button);
                focusables.Add(button);
            }
            _panel.AddChild(grid);
        }
        _panel.AddChild(nameLine);
        ShowTextCard("Colours", $"Part: {(_part is null ? "all parts" : AppearanceEdits.SlotTitle(_part))}\nChannel: {ChannelName(_channel)}", "Choose a swatch; its name is printed here when focused. Colour values are as stored in the install (colour space unknown).");
    }

    string DisplayOf(string paletteName) => _catalog.Palette.FirstOrDefault(p => p.Name.Equals(paletteName, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? paletteName;
    static string ChannelName(PaintChannel c) => c switch { PaintChannel.Primary => "Primary colour", PaintChannel.Pattern => "Pattern colour", _ => "Detail colour" };
    static string PrettyGroup(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' ');

    void ShowColourCard(PaletteEntry colour, string group)
    {
        ShowTextCard(colour.DisplayName, $"Group: {PrettyGroup(group)}\nFor: {(_part is null ? "all parts" : AppearanceEdits.SlotTitle(_part))} - {ChannelName(_channel)}", "Enter chooses it.");
    }

    // ---- BADGES ----

    void BuildBadges(AppearanceProfile look, ResolvedAppearance? resolved, List<Control> focusables)
    {
        if (LockedNotice(look, focusables)) { ShowTextCard("Badges", "Badges are fixed while a whole set is worn."); return; }
        var race = Race;
        if (race is null) return;
        var padRow = UiKit.HBox(8);
        var padButtons = new List<Control>();
        foreach (var pad in AppearanceEdits.Pads)
        {
            string p = pad;
            bool ok = PadOk(resolved, p);
            var b = UiKit.Toggle(ok ? AppearanceEdits.SlotTitle(p) + " pad" : AppearanceEdits.SlotTitle(p) + " (no badge)", _pad == p, on => { if (on) _pad = p; Callable.From(() => Rebuild(true)).CallDeferred(); }, 0);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            padRow.AddChild(b);
            padButtons.Add(b);
        }
        UiKit.ChainHorizontal(padButtons);
        _panel.AddChild(padRow);
        focusables.AddRange(padButtons);

        if (!PadOk(resolved, _pad))
        {
            _panel.AddChild(Note("This pad cannot show a badge.", UiKit.LightRed));
            ShowTextCard(AppearanceEdits.SlotTitle(_pad), "This pad cannot show a badge: its armour piece has no space for the picture.");
            return;
        }
        look.Decals.TryGetValue(_pad, out var current);
        string pad0 = _pad;
        UiKit.Row Choice(string title, bool selected, Action press, Action? focus = null, params string[] tags)
        {
            var row = new UiKit.Row(title, selected ? [.. tags, "CURRENT"] : tags, selected, true, selected ? UiKit.Glyph.Kind.Check : null) { Focused = focus };
            row.Pressed += () => press();
            _panel.AddChild(row);
            focusables.Add(row);
            return row;
        }
        Choice("Chapter's own badge", current is null, () => Apply((l, _) => AppearanceEdits.SetDecal(l, pad0, null), "The chapter's badge."), () => ShowTextCard("Chapter's own badge", "Whatever the chapter or warband paint carries."));
        Choice("None", current?.None == true, () => Apply((l, _) => AppearanceEdits.SetDecal(l, pad0, DecalChoice.NoDecal), "No badge."), () => ShowTextCard("None", "A plain pad."));
        var custom = UiKit.Button("Custom image...", () => StartImport(look, resolved), 0);
        _panel.AddChild(custom);
        focusables.Add(custom);
        _panel.AddChild(Note(EmblemPipeline.LimitsText));

        var library = EmblemPipeline.Library(_services.Store);
        _panel.AddChild(Heading($"MY EMBLEMS ({library.Count} of {EmblemPipeline.MaxLibrary})"));
        if (library.Count == 0) _panel.AddChild(Note("Your own images appear here after you import one."));
        foreach (var hash in library)
        {
            var h = hash;
            bool selected = current?.Emblem is { } e && e.Equals(h.ToHex(), StringComparison.OrdinalIgnoreCase);
            var line = UiKit.HBox(8);
            line.AddChild(new TextureRect { Texture = EmblemTexture(h), CustomMinimumSize = new Vector2(52, 52), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize });
            var row = new UiKit.Row("Emblem " + h.ToHex()[..6], selected ? ["CURRENT"] : [], selected, true, selected ? UiKit.Glyph.Kind.Check : null) { Focused = () => { ShowEmblemCard(h); PreviewEdit((l, _) => AppearanceEdits.ApplyEmblem(l, [pad0], h.ToHex()).Look); }, Unfocused = PreviewEnd };
            row.Pressed += () => SetEmblem([pad0], h.ToHex(), "Emblem set.");
            line.AddChild(row);
            var delete = UiKit.Button("Delete", () => UiKit.Dialog.Open(this, Host, "Delete this emblem?", "The image is removed. Pads that show it show nothing.", ["Delete", "Cancel"], i =>
            {
                if (i != 0) return;
                _session.Edit(p => EmblemPipeline.Remove(_services.Store, p, h));
                _undo.Clear();   // a deleted file cannot be brought back by Undo
                Say("Emblem deleted.");
            }, titleColour: UiKit.LightRed), 110);
            line.AddChild(delete);
            _panel.AddChild(line);
            focusables.Add(row);
            focusables.Add(delete);
        }

        _panel.AddChild(Heading(_race == CustomizationCatalog.Chaos ? "WARBAND BADGES" : "CHAPTER BADGES"));
        foreach (var (family, variants) in AppearanceEdits.BadgeFamilies(_catalog, _race))
        {
            var vs = variants;
            bool inFamily = current?.Badge is { } b && vs.Any(v => v.Name.Equals(b, StringComparison.OrdinalIgnoreCase));
            var first = vs.FirstOrDefault(v => v.Variant is "orig" or "") ?? vs[0];
            var row = new UiKit.Row(first.DisplayName, inFamily ? ["CURRENT"] : [], inFamily, true, inFamily ? UiKit.Glyph.Kind.Check : null) { Focused = () => { ShowBadgeCard(first); PreviewEdit((l, _) => AppearanceEdits.SetDecal(l, pad0, DecalChoice.ForBadge(first.Name))); }, Unfocused = PreviewEnd };
            row.Pressed += () => Apply((l, _) => AppearanceEdits.SetDecal(l, pad0, DecalChoice.ForBadge(first.Name)), $"{first.DisplayName} badge.");
            _panel.AddChild(row);
            focusables.Add(row);
            if (inFamily && vs.Count > 1)
            {
                var variantRow = UiKit.HBox(6);
                variantRow.AddChild(UiKit.Text("Variant:", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center));
                foreach (var v in vs)
                {
                    var variant = v;
                    bool isCurrent = current?.Badge?.Equals(variant.Name, StringComparison.OrdinalIgnoreCase) == true;
                    var vb = UiKit.Toggle(VariantName(variant.Variant), isCurrent, on => { if (on) Apply((l, _) => AppearanceEdits.SetDecal(l, pad0, DecalChoice.ForBadge(variant.Name)), $"{VariantName(variant.Variant)} variant."); }, 0);
                    vb.FocusEntered += () => { ShowBadgeCard(variant); PreviewEdit((l, _) => AppearanceEdits.SetDecal(l, pad0, DecalChoice.ForBadge(variant.Name))); };
                    vb.FocusExited += PreviewEnd;
                    variantRow.AddChild(vb);
                    focusables.Add(vb);
                }
                _panel.AddChild(variantRow);
            }
        }
        ShowTextCard("Badges", "Choose a badge for the " + AppearanceEdits.SlotTitle(_pad).ToLowerInvariant() + " pad, none, or your own image.");
    }

    static string VariantName(string variant) => variant switch { "" => "Standard", "orig" => "Original", "bw" => "Black and white", "bw_inverted" => "Inverted B/W", _ => char.ToUpperInvariant(variant[0]) + variant[1..] };

    bool PadOk(ResolvedAppearance? resolved, string pad) => resolved is null || resolved.Piece(pad) is not { } piece || _probe.Takes(piece);

    /// <summary>
    /// Puts an emblem on the pads (those that can show one). A look carries one emblem on the wire, so a pad outside the choice that shows another custom emblem gets this one
    /// too (<see cref="AppearanceEdits.ApplyEmblem"/>); the status line says so.
    /// </summary>
    void SetEmblem(IEnumerable<string> pads, string hex, string message)
    {
        bool replaced = false;
        Apply((l, r) =>
        {
            var (result, other) = AppearanceEdits.ApplyEmblem(l, pads.Where(p => PadOk(r, p)), hex);
            replaced = other;
            return result;
        });
        Say(replaced ? message + " A look carries one emblem, so the other pad shows it too." : message);
    }

    /// <summary>Opens the emblem importer (<c>--emblem-file</c> test aid: the file is read at once).</summary>
    public void OpenImporter() => StartImport(Look(), Resolved());

    void StartImport(AppearanceProfile look, ResolvedAppearance? resolved)
    {
        if (ModelViewer.TestWindow && ModelViewer.EmblemFile is null) { Say("No file dialog in a test window (use --emblem-file)."); return; }
        var importer = new EmblemImporter(_services, this, (hash, target) =>
        {
            var pads = AppearanceEdits.Pads.Where(pad => !(target == "left" && pad != "leftshoulder" || target == "right" && pad != "rightshoulder")).ToArray();
            SetEmblem(pads, hash.ToHex(), $"Emblem applied to {(target == "both" ? "both pads" : "the " + target + " pad")}.");
        }, () => Rebuild(true), PadOk(resolved, "leftshoulder"), PadOk(resolved, "rightshoulder"));
        importer.Begin(Host, ModelViewer.EmblemFile);
    }

    Texture2D? EmblemTexture(EmblemHash hash)
    {
        string key = "e:" + hash.ToHex();
        if (_thumbnails.TryGetValue(key, out var cached)) return cached;
        Texture2D? texture = null;
        if (_services.Store.LoadEmblem(hash) is { } bytes)
            texture = ImageTexture.CreateFromImage(Image.CreateFromData(EmblemHash.Side, EmblemHash.Side, false, Image.Format.Rgba8, Bc3.Decode(bytes, EmblemHash.Side, EmblemHash.Side)));
        return _thumbnails[key] = texture;
    }

    Texture2D? BadgeTexture(ShoulderDecal badge)
    {
        string key = "b:" + badge.Name;
        if (_thumbnails.TryGetValue(key, out var cached)) return cached;
        Texture2D? texture = null;
        try
        {
            if (PaintSchemes.ReadDecalTexture(_services.GameDir, badge.Decal) is { } dds)
            {
                var image = new Image();
                if (image.LoadDdsFromBuffer(dds) == Error.Ok) { if (image.IsCompressed()) image.Decompress(); texture = ImageTexture.CreateFromImage(image); }
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
        return _thumbnails[key] = texture;
    }

    void ShowEmblemCard(EmblemHash hash)
    {
        ShowTextCard("Your emblem", "128 x 128 pixels, stored on this computer. Other players see it unless they turned custom emblems off; until it arrives they see the chapter's badge.", "", null, EmblemTexture(hash));
    }

    void ShowBadgeCard(ShoulderDecal badge) =>
        ShowTextCard(badge.DisplayName, $"Variant: {VariantName(badge.Variant)}", "Enter puts it on the pad.", null, BadgeTexture(badge));

    // ---- EYES ----

    void BuildEyes(AppearanceProfile look, List<Control> focusables)
    {
        _panel.AddChild(Note("Eye lens colour is our addition, not in the original. Other players see it too; \"Default\" keeps the original eyes.", UiKit.Grey));
        var def = UiKit.Button("Default (original look)", () => Apply((l, _) => AppearanceEdits.SetLens(l, null), "Default eyes."), 0);
        _panel.AddChild(def);
        focusables.Add(def);
        var nameLine = UiKit.Text(look.Lens is { } cur ? $"Current: {cur.R}, {cur.G}, {cur.B}" : "Current: default", UiKit.BodySize, UiKit.White);
        _panel.AddChild(nameLine);
        foreach (var group in _catalog.PaletteGroups)
        {
            _panel.AddChild(Heading(PrettyGroup(group.Name)));
            var grid = new GridContainer { Columns = 10 };
            grid.AddThemeConstantOverride("h_separation", 6);
            grid.AddThemeConstantOverride("v_separation", 6);
            foreach (var colour in group.Colours)
            {
                var c = colour;
                var rgb = new LensColour((byte)Math.Clamp((int)MathF.Round(c.Diffuse[0] * 255f), 0, 255), (byte)Math.Clamp((int)MathF.Round(c.Diffuse[1] * 255f), 0, 255), (byte)Math.Clamp((int)MathF.Round(c.Diffuse[2] * 255f), 0, 255));
                var button = new UiKit.ColourButton(new Color(c.Diffuse[0], c.Diffuse[1], c.Diffuse[2]), look.Lens == rgb, c.DisplayName);
                button.Focused = () => { ShowTextCard(c.DisplayName, "Eye lens colour (our addition).", "Enter chooses it."); PreviewEdit((l, _) => AppearanceEdits.SetLens(l, rgb)); };
                button.Unfocused = PreviewEnd;
                button.Pressed += () => Apply((l, _) => AppearanceEdits.SetLens(l, rgb), $"{c.DisplayName} eyes.");
                grid.AddChild(button);
                focusables.Add(button);
            }
            _panel.AddChild(grid);
        }
        _panel.AddChild(Heading("FREE COLOUR"));
        var sliders = new List<HSlider>();
        var values = look.Lens is { } l0 ? new[] { (int)l0.R, l0.G, l0.B } : new[] { 255, 255, 255 };
        for (int i = 0; i < 3; i++)
        {
            int channel = i;
            var row = UiKit.HBox(8);
            row.AddChild(UiKit.Text(new[] { "Red", "Green", "Blue" }[i], UiKit.BodySize, UiKit.White).With(l => l.CustomMinimumSize = new Vector2(90, 0)));
            var slider = new HSlider { MinValue = 0, MaxValue = 255, Step = 1, Value = values[i], SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, UiKit.MinHit), FocusMode = FocusModeEnum.All };
            sliders.Add(slider);
            slider.DragEnded += changed =>
            {
                if (changed) Apply((l, _) => AppearanceEdits.SetLens(l, new LensColour((byte)sliders[0].Value, (byte)sliders[1].Value, (byte)sliders[2].Value)));
            };
            slider.ValueChanged += _ => nameLine.Text = $"Free colour: {(int)sliders[0].Value}, {(int)sliders[1].Value}, {(int)sliders[2].Value}";
            row.AddChild(slider);
            _panel.AddChild(row);
            focusables.Add(slider);
        }
        ShowTextCard("Eyes", "Pick a colour for the lens of the helmet.");
    }

    // ---- the card, status, actions ----

    void ShowTextCard(string title, string body, string extra = "", Color? extraColour = null, Texture2D? picture = null)
    {
        foreach (var c in _card.GetChildren()) { _card.RemoveChild(c); c.QueueFree(); }
        var row = UiKit.HBox(12);
        var text = UiKit.VBox(4);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.AddChild(UiKit.Text(title, UiKit.HeaderSize - 4, UiKit.Gold).With(l => l.ClipText = true));
        if (body.Length > 0) text.AddChild(UiKit.Text(body, UiKit.CaptionSize + 2, UiKit.White, HorizontalAlignment.Left, true));
        if (extra.Length > 0) text.AddChild(UiKit.Text(extra, UiKit.CaptionSize + 2, extraColour ?? UiKit.Grey, HorizontalAlignment.Left, true));
        row.AddChild(text);
        if (picture is not null) row.AddChild(new TextureRect { Texture = picture, CustomMinimumSize = new Vector2(128, 128), StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize });
        _card.AddChild(row);
    }

    void Say(string text)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", UiKit.White);
        _statusUntil = Time.GetTicksMsec() / 1000.0 + 2.5;
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_previewing && _previewEndAt >= 0 && _clock >= _previewEndAt)
        {
            _previewEndAt = -1; _previewing = false;
            _preview.SetSubject(new PreviewSubject(_race, Role, Class, Look()));
        }
        if (_status.Text.Length > 0 && Time.GetTicksMsec() / 1000.0 > _statusUntil) _status.Text = "";
    }

    void ResetSection()
    {
        if (Class is null) return;
        string what = _section switch { Section.Armour => "the armour parts", Section.Colours => "the colours", Section.Badges => "the badges", Section.Eyes => "the eyes", _ => "the whole look" };
        UiKit.Dialog.Open(this, Host, "Reset this section?", $"Back to the {(_race == CustomizationCatalog.Chaos ? "warband" : "chapter")}'s default for {what}.", ["Reset", "Cancel"], i =>
        {
            if (i != 0) return;
            Apply((l, _) => _section switch
            {
                Section.Armour => AppearanceEdits.ResetPieces(l),
                Section.Colours => AppearanceEdits.ResetPaint(l),
                Section.Badges => AppearanceEdits.ResetDecals(l),
                Section.Eyes => AppearanceEdits.SetLens(l, null),
                _ => AppearanceEdits.ResetAll(l),
            }, "Reset.");
        });
    }

    void Save()
    {
        if (!_session.Dirty) return;
        string? error = _session.Save();
        if (error is null) { Say("Saved."); _undo.Clear(); }
        else { _status.Text = "Could not save: " + error; _status.AddThemeColorOverride("font_color", UiKit.LightRed); _statusUntil = Time.GetTicksMsec() / 1000.0 + 6; }
    }

    void Done() => _onDone();

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (!IsVisibleInTree() || e is not InputEventKey { Pressed: true } key) return;
        bool ctrl = key.CtrlPressed || key.MetaPressed;
        if (key.PhysicalKeycode == Key.S && ctrl) Save();
        else if (key.PhysicalKeycode == Key.Z && ctrl) Undo();
        else if (key.PhysicalKeycode is Key.Pagedown or Key.Pageup) MoveSection(key.PhysicalKeycode == Key.Pagedown ? 1 : -1);
        else return;
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree()) return;
        if (e.IsActionPressed("ui_cancel"))
        {
            GetViewport().SetInputAsHandled();
            if (_section == Section.Armour && _armourSlot is not null) { _armourSlot = null; Rebuild(true); }
            else Done();
            return;
        }
        if (e is InputEventJoypadButton { Pressed: true } pad)
        {
            switch (pad.ButtonIndex)
            {
                case JoyButton.LeftShoulder: MoveSection(-1); break;
                case JoyButton.RightShoulder: MoveSection(1); break;
                case JoyButton.Start: Save(); break;
                case JoyButton.X: Undo(); break;
                default: return;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    void MoveSection(int delta)
    {
        int count = _sectionButtons.Count;
        _section = (Section)(((int)_section + delta + count) % count);
        _armourSlot = null;
        Rebuild(true);
    }

    /// <summary>Whether a shoulder pad's piece can show a badge: its mesh's second UV set must carry a decal layout (<see cref="PaintSchemes.HasDecalUv"/>). Read once per piece; unreadable means yes.</summary>
    sealed class PadProbe
    {
        readonly CustomizerServices _services;
        readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

        public PadProbe(CustomizerServices services) => _services = services;

        public bool Takes(ResolvedPiece piece)
        {
            if (_cache.TryGetValue(piece.Name, out var known)) return known;
            bool result = true;
            try
            {
                foreach (var package in piece.Packages)
                {
                    string path = System.IO.Path.Combine(_services.GameDir, "data", "packages", package + ".oppc");
                    if (!System.IO.File.Exists(path)) continue;
                    var model = StaticModel.Load(path, package);
                    result = model.Mesh.Blocks.Any(b => PaintSchemes.HasDecalUv(b.Uv1));
                    break;
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { result = true; }
            return _cache[piece.Name] = result;
        }
    }
}
