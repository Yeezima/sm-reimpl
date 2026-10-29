using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmClient.Looks;

namespace SmClient.Customizer;

/// <summary>
/// The custom-class creator (docs/customizer-design.md F4, F5, section 3): DEFAULTS and CUSTOM 1 to 10, the role tabs (both sides' class names), the steps
/// Class, Weapons, Equipment and Perks, the 3D preview with the item card, and the action bar (rename, copy, reset, clear, revert, save, set active, appearance).
/// Everything edits the shared <see cref="ProfileSession"/>; nothing is written until Save. Everything is unlocked (the owner wants no level progression); the lock
/// visuals exist behind <c>--lock-demo</c>. The in-match picker (T7) opens this view with <see cref="Banner"/> and <see cref="Deploy"/> set.
/// </summary>
public sealed partial class CreatorView : Control
{
    // ui/customizer-ork: LOOK is the Ork Boy's fifth step (his look instead of the armour and colour editors); a marine's look is the appearance editor.
    enum Step { Class, Weapons, Equipment, Perks, Look }
    static readonly string[] StepNames = ["CLASS", "WEAPONS", "EQUIPMENT", "PERKS", "LOOK"];
    int StepCount => IsOrkOpen ? StepNames.Length : StepNames.Length - 1;

    readonly CustomizerServices _services;
    readonly ProfileSession _session;
    readonly CustomizationCatalog _catalog;

    // Callbacks from the shell.
    readonly Action _onBack;
    readonly Action _onAppearance;

    /// <summary>In a match (T7): the banner under the header ("Applies when you respawn - RESPAWN IN 4"); empty in the front end.</summary>
    public string Banner { get => _banner.Text; set { _banner.Text = value; _banner.Visible = value.Length > 0; } }
    /// <summary>In a match (T7): adds "DEPLOY WITH ..." to the action bar and calls this with the chosen loadout; null in the front end.</summary>
    public Action<Selection>? Deploy { get; set; }
    /// <summary>
    /// In a match (fix/class-change-in-match): SET ACTIVE also sends the loadout for the next respawn (the menu stays open) and shows the returned line; null in the
    /// front end, where SET ACTIVE only marks the profile's active loadout. Before this, SET ACTIVE in a match changed the profile and nothing else: the player kept
    /// respawning with the class the match was joined with.
    /// </summary>
    public Func<Selection, string>? Activate { get; set; }

    /// <summary>In a match (M44): the text of the button that leaves the creator ("RESUME (Esc)" instead of "BACK (Esc)").</summary>
    public string BackText { set { if (_backButton is not null) _backButton.Text = value; } }
    /// <summary>In a match (M44): shows "LEAVE MATCH" (with a confirmation) and calls this when confirmed; null hides the button.</summary>
    public Action? Leave { get => _leave; set { _leave = value; if (_leaveButton is not null) _leaveButton.Visible = value is not null; } }
    Action? _leave;

    /// <summary>LEAVE MATCH: unsaved edits go through the same Save / Discard / Cancel prompt as RESUME, then the player leaves; without edits a plain confirmation.</summary>
    void ConfirmLeave()
    {
        if (PickerRules.LeaveStep(_session.Dirty) == PickerRules.LeavePrompt.SaveFirst)
            UiKit.Dialog.Open(this, Host, "Unsaved changes", "Save your loadouts and looks before you leave the match?", ["Save", "Discard", "Cancel"], i =>
            {
                if (i == 0) { string? error = _session.Save(); if (error is null) _leave?.Invoke(); else Say("Could not save: " + error); }
                else if (i == 1) { _session.Revert(); _leave?.Invoke(); }
            });
        else
            UiKit.Dialog.Open(this, Host, "Leave the match?", "You go back to the main menu.", ["Leave", "Stay"], i => { if (i == 0) _leave?.Invoke(); });
    }

    Selection _open;
    Step _step = Step.Weapons;
    string _previewRace = CustomizationCatalog.SpaceMarine;
    int _perkSlot;
    string? _lastDefaultsRole;

    readonly SlotStrip _strip;
    readonly Label _banner, _title, _summary, _status, _unsaved, _activeLabel;
    readonly HBoxContainer _tabs = UiKit.HBox(8), _actions = UiKit.HBox(8);
    readonly VBoxContainer _steps = UiKit.VBox(8), _list = UiKit.VBox(8), _card = UiKit.VBox(6);
    readonly ScrollContainer _scroll;
    readonly PreviewStage _preview;
    readonly List<Button> _stepButtons = new(), _tabButtons = new();
    readonly HBoxContainer _sideToggle = UiKit.HBox(8);
    Button? _backButton, _leaveButton;
    Button? _save, _setActive, _revert, _rename, _copy, _reset, _clear, _look, _deployButton;
    double _statusUntil;

    public CreatorView(CustomizerServices services, Action onBack, Action onAppearance)
    {
        _services = services; _session = services.Session!; _catalog = services.Catalog!;
        _onBack = onBack; _onAppearance = onAppearance;
        _open = LoadoutEdits.Active(_session.Working, _catalog, out _);
        _open = ValidOpen();   // the Ork DEFAULTS only with the Orks (ui/customizer-ork)
        SetAnchorsPreset(LayoutPreset.FullRect);
        Name = "Creator";
        AddChild(UiKit.Background().With(n => n.SetAnchorsPreset(LayoutPreset.FullRect)));

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var (side, v) in new[] { ("left", 84), ("right", 84), ("top", 22), ("bottom", 20) }) margin.AddThemeConstantOverride("margin_" + side, v);
        AddChild(margin);
        var root = UiKit.VBox(8);
        margin.AddChild(root);

        // Header.
        var header = UiKit.HBox(16);
        _title = UiKit.Text("LOADOUTS", UiKit.HeaderSize, UiKit.Gold);
        header.AddChild(_title);
        _sideCaption = UiKit.Text("Preview side:", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center);
        header.AddChild(_sideCaption);
        header.AddChild(_sideToggle);
        // The ACTIVE line fills the space up to the buttons and is cut (full text in its tooltip) rather than pushing BACK / LEAVE MATCH off the window (fix/frontend-fit).
        _activeLabel = UiKit.Text("", UiKit.CaptionSize, UiKit.Gold, HorizontalAlignment.Right);
        _activeLabel.VerticalAlignment = VerticalAlignment.Center;
        _activeLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _activeLabel.ClipText = true;
        _activeLabel.MouseFilter = MouseFilterEnum.Pass;
        header.AddChild(_activeLabel);
        _backButton = UiKit.Button("BACK (Esc)", () => Back(), 190);
        header.AddChild(_backButton);
        _leaveButton = UiKit.Button("LEAVE MATCH", ConfirmLeave, 220);
        _leaveButton.Visible = false;
        header.AddChild(_leaveButton);
        root.AddChild(header);
        _banner = UiKit.Text("", UiKit.BodySize, UiKit.Gold);
        _banner.Visible = false;
        root.AddChild(_banner);

        root.AddChild(_tabs);
        _strip = new SlotStrip(services, showDefaults: true, chipWidth: 148, compact: true) { Picked = OnChip };
        root.AddChild(_strip);
        _summary = UiKit.Text("", UiKit.CaptionSize, UiKit.White);
        _summary.ClipText = true;
        root.AddChild(_summary);

        // Main: steps | list | preview and card.
        var main = UiKit.HBox(16);
        main.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(main);
        var stepsPanel = UiKit.Panel(_steps, 10);
        stepsPanel.CustomMinimumSize = new Vector2(230, 0);
        main.AddChild(stepsPanel);
        _scroll = UiKit.Scroll(_list);
        _scroll.CustomMinimumSize = new Vector2(590, 0);
        _scroll.SizeFlagsHorizontal = SizeFlags.Fill;
        main.AddChild(_scroll);
        var right = UiKit.VBox(10);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _preview = new PreviewStage(services);
        right.AddChild(_preview);
        var cardPanel = UiKit.Panel(_card, 12);
        cardPanel.CustomMinimumSize = new Vector2(0, CardMinHeight);
        _cardPanel = cardPanel;
        right.AddChild(cardPanel);
        main.AddChild(right);

        // Action bar.
        var bar = UiKit.HBox(8);
        _rename = UiKit.Button("Rename (F2)", RenameSlot, 150);
        _copy = UiKit.Button("Copy to...", CopySlot, 130);
        _reset = UiKit.Button("Reset", ResetSlot, 96);
        _clear = UiKit.Button("Clear", ClearSlot, 96);
        _revert = UiKit.Button("Revert", RevertAll, 110);
        foreach (var b in new[] { _rename, _copy, _reset, _clear, _revert }) bar.AddChild(b);
        _unsaved = UiKit.Text("", UiKit.BodySize, UiKit.LightRed);
        _unsaved.VerticalAlignment = VerticalAlignment.Center;
        bar.AddChild(_unsaved);
        _status = UiKit.Text("", UiKit.CaptionSize, UiKit.White);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.ClipText = true;
        bar.AddChild(_status);
        _look = UiKit.Button("APPEARANCE...", OpenAppearance, 210);
        _save = UiKit.Button("SAVE (Ctrl+S)", Save, 200);
        _setActive = UiKit.Button("SET ACTIVE", SetActive, 170);
        bar.AddChild(_look);
        bar.AddChild(_save);
        bar.AddChild(_setActive);
        _deployButton = UiKit.Button("DEPLOY", () => Deploy?.Invoke(_open), 210);
        _deployButton.Visible = false;
        bar.AddChild(_deployButton);
        root.AddChild(bar);

        BuildSideToggle();
        BuildTabs();
        BuildStepButtons();
        _session.Changed += OnSessionChanged;
        Refresh(focusList: false);
    }

    Control Host => (Control)(GetParent() ?? this);

    public override void _ExitTree() => _session.Changed -= OnSessionChanged;

    void OnSessionChanged()
    {
        if (!IsInsideTree()) return;
        Refresh(focusList: false);
    }

    /// <summary>The loadout the creator has open (a DEFAULTS role or a custom slot).</summary>
    public Selection Open => _open;

    /// <summary>Opens a slot or a DEFAULTS loadout (the in-match picker and <c>--loadout</c>).</summary>
    public void OpenSelection(Selection selection)
    {
        if (selection != _open) _perkSlot = 0;   // a loadout opened by the shell starts at perk 1, as a click on its chip does
        _open = selection;
        _open = ValidOpen();   // never the Ork's loadouts without the Orks (ui/customizer-ork)
        Refresh(focusList: false);
    }

    /// <summary>The side the preview shows (its class names: "Havoc" on Chaos, "Devastator" on Space Marines).</summary>
    public string PreviewRace => _previewRace;

    public void SetPreviewRace(string race)
    {
        _previewRace = race;
        BuildSideToggle();
        Refresh(focusList: false);
    }

    public void OpenStep(string name)
    {
        for (int i = 0; i < StepNames.Length; i++) if (StepNames[i].Equals(name, StringComparison.OrdinalIgnoreCase)) { _step = (Step)i; Refresh(false); return; }
    }

    /// <summary>The step shown now (test aid).</summary>
    public string CurrentStep => StepNames[(int)_step];

    /// <summary>The Ork Boy's loadout is open (a custom Ork class or the Ork DEFAULTS; ui/customizer-ork).</summary>
    public bool IsOrkOpen => !IsEmpty && OrkLoadout.IsOrk(Role);

    public PreviewStage Preview => _preview;

    /// <summary>Called when the view is shown again after the appearance editor (the working copy may have changed).</summary>
    public void Reenter()
    {
        ReturnFromRevertedCopy();
        _open = ValidOpen();
        // The content profile may have changed on the Join screen (ui/customizer-ork): the remembered DEFAULTS role, the factions and the class tabs follow it.
        if (_lastDefaultsRole is { } last && !_services.Roles().Contains(last, StringComparer.OrdinalIgnoreCase)) _lastDefaultsRole = null;
        BuildSideToggle();
        BuildTabs();
        Refresh(focusList: false);
    }

    // The DEFAULTS an editable copy was made from, and the slot it went to: a Revert or Discard that drops the copy goes back to those DEFAULTS, not to an empty slot.
    (int Slot, Selection Defaults)? _copiedFrom;

    void ReturnFromRevertedCopy()
    {
        if (_copiedFrom is not { } c) return;
        _open = LoadoutEdits.ReturnFromCopy(_session.Working, _open, c.Slot, c.Defaults);
        if (_session.Working.ClassAt(c.Slot) is not null || _open == c.Defaults) _copiedFrom = null;
    }

    // ---- what is open ----

    string Role => _open.Kind == SelectionKind.Default ? _open.Role ?? ProfileStore.FirstRole(_catalog) : _session.Working.ClassAt(_open.Slot)?.Role ?? "";
    CustomClass? OpenClass => _open.Kind == SelectionKind.Custom ? _session.Working.ClassAt(_open.Slot) : null;
    bool IsDefaults => _open.Kind == SelectionKind.Default;
    bool IsEmpty => _open.Kind == SelectionKind.Custom && OpenClass is null;

    Selection ValidOpen() =>
        Unavailable(_open) ? Selection.DefaultFor(ProfileStore.FirstRole(_catalog))   // the Ork's loadouts (DEFAULTS or a saved slot) only with the Orks
        : LoadoutEdits.IsValid(_session.Working, _open) || (_open.Kind == SelectionKind.Custom && _open.Slot is >= 1 and <= CustomizationProfile.MaxCustomClasses) ? _open : Selection.DefaultFor(ProfileStore.FirstRole(_catalog));

    /// <summary>
    /// A loadout the screens do not offer now (ui/customizer-ork review): the Ork DEFAULTS, or a saved Ork slot, while the Orks are not offered (Faithful). It is kept in the
    /// profile but never opened in the editor.
    /// </summary>
    bool Unavailable(Selection selection) =>
        !_services.OffersOrks && (selection.Kind == SelectionKind.Default ? OrkLoadout.IsOrk(selection.Role) : _session.Working.ClassAt(selection.Slot) is { } c && OrkLoadout.IsOrk(c.Role));

    void OnChip(Selection selection)
    {
        if (Unavailable(selection))
        {
            Say(LoadoutAvailability.Reason(_services.Content) + " The loadout is kept; switch Content to Extended to edit it.", 6.0);
            _strip.SetOpen(_open);
            return;
        }
        _open = selection;
        _perkSlot = 0;
        Refresh(focusList: true);
    }

    // ---- building the static parts ----

    /// <summary>
    /// The faction row (added content E2; the Ork Boy's faction step since ui/customizer-ork): Space Marines and Chaos choose the side the preview shows (a loadout
    /// serves both sides); Orks (only in a profile with the Orks) opens the Ork Boy, the one class of his faction, and choosing Space Marines or Chaos from the Ork
    /// goes back to a marine class. The pressed button is the faction of the open loadout.
    /// </summary>
    void BuildSideToggle()
    {
        foreach (var c in _sideToggle.GetChildren()) { _sideToggle.RemoveChild(c); c.QueueFree(); }
        _factionButtons.Clear();
        if (_sideCaption is not null) _sideCaption.Text = _services.OffersOrks ? "Faction:" : "Preview side:";
        foreach (var (race, text) in _services.Factions())   // added content E2: the profile's factions (no Ork in the faithful profile)
        {
            string r = race;
            var b = UiKit.Toggle(text, FactionOf() == race, on => { if (on) ChooseFaction(r); else SyncFactions(); }, 190);
            b.Name = "Faction" + text.Replace(" ", "");
            _sideToggle.AddChild(b);
            _factionButtons.Add((race, b));
        }
    }

    readonly List<(string Race, Button Button)> _factionButtons = new();
    Label? _sideCaption;

    /// <summary>The faction the open loadout belongs to: the Orks for the Ork Boy, else the side the preview shows.</summary>
    string FactionOf() => IsOrkOpen ? OrkLoadout.Race : _previewRace;

    void SyncFactions()
    {
        string faction = FactionOf();
        foreach (var (race, button) in _factionButtons) if (GodotObject.IsInstanceValid(button)) button.SetPressedNoSignal(race == faction);
    }

    // The marine class the Ork was chosen from: Space Marines or Chaos from the Ork goes back to it.
    string? _lastMarineRole;

    void ChooseFaction(string race)
    {
        if (race == OrkLoadout.Race)
        {
            if (!IsOrkOpen) { if (!IsEmpty && !OrkLoadout.IsOrk(Role)) _lastMarineRole = Role; ChooseRole(OrkLoadout.Role); }
            SyncFactions();
            return;
        }
        _previewRace = race;
        if (IsOrkOpen)
        {
            string role = _lastMarineRole is { } last && ProfileStore.Roles(_catalog).Contains(last, StringComparer.OrdinalIgnoreCase) ? last : ProfileStore.FirstRole(_catalog);
            ChooseRole(role);
        }
        SyncFactions();
        Refresh(focusList: false);
    }

    void BuildTabs()
    {
        foreach (var c in _tabs.GetChildren()) { _tabs.RemoveChild(c); c.QueueFree(); }
        _tabButtons.Clear();
        _tabRoles = _services.Roles();   // the Ork Boy's tab only in a profile with the Orks (ui/customizer-ork)
        // Four tabs (with the Ork) share the row: narrower minimums so the row fits a 1920-wide UI.
        int minimum = _tabRoles.Count > 3 ? 300 : 400;
        foreach (var role in _tabRoles)
        {
            string r = role;
            var b = new Button { Text = LoadoutRows.RoleLabel(_catalog, role), ToggleMode = true, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(minimum, UiKit.MinHit), ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            b.TooltipText = b.Text;
            b.Pressed += () => ChooseRole(r);
            b.Name = "ClassTab" + _tabButtons.Count;   // the self-test's fit check reads the whole label of these
            UiKit.ShrinkToFit(b);   // the whole "Tactical Marine / Chaos Space Marine" fits (ui/hud-fidelity review)
            _tabs.AddChild(b);
            _tabButtons.Add(b);
        }
        UiKit.ChainHorizontal(_tabButtons.Cast<Control>().ToList());
    }

    void BuildStepButtons()
    {
        foreach (var c in _steps.GetChildren()) { _steps.RemoveChild(c); c.QueueFree(); }
        _stepButtons.Clear();
        for (int i = 0; i < StepNames.Length; i++)
        {
            int index = i;
            var b = new Button { Text = $"{i + 1}  {StepNames[i]}", Alignment = HorizontalAlignment.Left, ToggleMode = true, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 56) };
            b.Pressed += () => { _step = (Step)index; Refresh(focusList: true); };
            _steps.AddChild(b);
            _stepButtons.Add(b);
        }
        _steps.AddChild(UiKit.Spacer(false));
        _lookCaption = UiKit.Text("LOOK", UiKit.CaptionSize, UiKit.Grey);
        _steps.AddChild(_lookCaption);
        _appearanceStepButton = UiKit.Button("Appearance...", OpenAppearance, 0);
        _steps.AddChild(_appearanceStepButton);
    }

    Label? _lookCaption;
    Button? _appearanceStepButton;
    IReadOnlyList<string> _tabRoles = [];

    // ---- refreshing ----

    void Refresh(bool focusList)
    {
        var profile = _session.Working;
        var active = LoadoutEdits.Active(profile, _catalog, out _);
        string role = Role;
        bool defaults = IsDefaults, empty = IsEmpty;

        _strip.DefaultsRole = defaults ? role : _lastDefaultsRole;
        if (defaults) _lastDefaultsRole = role;
        _strip.SetOpen(_open);
        _strip.Refresh();
        _activeLabel.Text = "ACTIVE: " + ActiveName(profile, active);
        _activeLabel.TooltipText = _activeLabel.Text;

        for (int i = 0; i < _tabButtons.Count && i < _tabRoles.Count; i++)
        {
            var r = _tabRoles[i];
            _tabButtons[i].SetPressedNoSignal(!empty && r.Equals(role, StringComparison.OrdinalIgnoreCase));
        }
        // The Ork Boy's LOOK step (ui/customizer-ork) replaces the appearance editor for him; a marine has the four steps and the editor.
        bool ork = IsOrkOpen;
        if (!ork && _step == Step.Look) _step = Step.Weapons;
        for (int i = 0; i < _stepButtons.Count; i++)
        {
            _stepButtons[i].SetPressedNoSignal((int)_step == i);
            _stepButtons[i].Visible = i < StepCount;
        }
        if (_lookCaption is not null) _lookCaption.Visible = !ork;
        if (_appearanceStepButton is not null) _appearanceStepButton.Visible = !ork;
        SyncFactions();

        string slotName = defaults ? $"DEFAULTS - {LoadoutRows.ClassName(_catalog, role, _previewRace)}" : empty ? $"CUSTOM {_open.Slot} - empty" : $"CUSTOM {_open.Slot} - {OpenClass!.Name}";
        _title.Text = "LOADOUTS";
        _summary.Text = empty ? $"{slotName}. Choose a class to start." :
            defaults ? $"{slotName}: {LoadoutRows.DefaultSummary(_catalog, role, _previewRace)}  (choose anything to edit a copy)" :
            $"{slotName}: {LoadoutRows.Summary(_catalog, OpenClass!, _previewRace)}";

        bool dirty = _session.Dirty;
        _unsaved.Text = dirty ? "* UNSAVED" : "";
        _save!.Disabled = !dirty || _session.ReadOnly;
        _revert!.Disabled = !dirty;
        _rename!.Disabled = OpenClass is null;
        _reset!.Disabled = OpenClass is null;
        _clear!.Disabled = OpenClass is null;
        _copy!.Disabled = empty;
        // In a match SET ACTIVE sends the loadout for the next respawn, so it stays available for the active loadout too (the match may have been joined with
        // another class, e.g. --class): only an empty slot disables it.
        _setActive!.Disabled = empty || (Activate is null && active == _open);
        _setActive.Text = Activate is null && active == _open ? "ACTIVE" : "SET ACTIVE";
        _look!.Disabled = OpenClass is null && !ork;   // the Ork's look is one choice of the profile: open on his DEFAULTS too
        _look.Text = ork ? "ORK LOOK..." : "APPEARANCE...";
        _deployButton!.Visible = Deploy is not null;
        _deployButton.Text = _open.Kind == SelectionKind.Custom ? $"DEPLOY WITH SLOT {_open.Slot}" : "DEPLOY WITH DEFAULTS";
        _deployButton.Disabled = empty;

        if (_step != Step.Look) _previewOrkLook = null;
        RebuildList(focusList);
        UpdatePreview();
    }

    string ActiveName(CustomizationProfile profile, Selection active)
    {
        if (active.Kind == SelectionKind.Custom && profile.ClassAt(active.Slot) is { } c) return $"CUSTOM {active.Slot} \"{c.Name}\"";
        return "DEFAULTS " + LoadoutRows.ClassName(_catalog, active.Role ?? "", _previewRace).ToUpperInvariant();
    }

    void UpdatePreview()
    {
        if (IsEmpty) { _preview.Visible = true; }
        var profile = _session.Working;
        var custom = OpenClass;
        var look = custom is null ? null : profile.Appearance(custom.Appearance.For(_previewRace));
        _preview.SetSubject(new PreviewSubject(_previewRace, Role.Length == 0 ? ProfileStore.FirstRole(_catalog) : Role, custom, look, _previewOrkLook ?? profile.OrkLook));
        _preview.SetFraming(Framing.Body);
    }

    // A look row with the keyboard focus shows that look in the preview (the LOOK step); null: the profile's look.
    int? _previewOrkLook;

    // ---- the list ----

    void RebuildList(bool focusList)
    {
        // Keep the keyboard position across a rebuild.
        int keep = -1;
        var focused = GetViewport()?.GuiGetFocusOwner();
        if (focused is Button focusedButton) keep = _list.GetChildren().OfType<Button>().ToList().IndexOf(focusedButton);
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        var rowButtons = new List<Button>();

        void Heading(string text) => _list.AddChild(UiKit.Text(text, UiKit.CaptionSize, UiKit.Gold).With(l => { l.CustomMinimumSize = new Vector2(0, 30); l.ClipText = true; l.TooltipText = text; }));
        void Note(string text, Color? colour = null) => _list.AddChild(UiKit.Text(text, UiKit.CaptionSize, colour ?? UiKit.Grey, HorizontalAlignment.Left, true));

        if (IsEmpty)
        {
            Heading("EMPTY SLOT: CHOOSE A CLASS");
            foreach (var role in _tabRoles)
            {
                string r = role;
                var row = new UiKit.Row(LoadoutRows.RoleLabel(_catalog, role), [], false, true) { Focused = () => ShowClassCard(r) };
                row.Pressed += () => ChooseRole(r);
                _list.AddChild(row);
                rowButtons.Add(row);
            }
            ShowEmptyCard();
        }
        else if (_step == Step.Class)
        {
            Heading("CLASS");
            foreach (var role in _tabRoles)
            {
                string r = role;
                bool selected = r.Equals(Role, StringComparison.OrdinalIgnoreCase);
                var row = new UiKit.Row(LoadoutRows.RoleLabel(_catalog, role), selected ? ["SELECTED"] : [], selected, true, selected ? UiKit.Glyph.Kind.Check : null) { Focused = () => ShowClassCard(r) };
                row.Pressed += () => ChooseRole(r);
                _list.AddChild(row);
                rowButtons.Add(row);
            }
            Note("Changing the class sets weapons, equipment and perks back to the new class's defaults.");
        }
        else if (_step == Step.Weapons)
        {
            foreach (var section in LoadoutRows.Weapons(_catalog, Role, _previewRace, OpenClass, _services.Policy))
                AddSection(section, rowButtons, Heading, Note);
        }
        else if (_step == Step.Equipment)
        {
            var section = LoadoutRows.Equipment(_catalog, Role, _previewRace, OpenClass, _services.Policy);
            AddSection(section, rowButtons, Heading, Note);
            if (!IsOrkOpen) Note("Combat stimulants are listed but not simulated yet: the server gives you the default grenade.");
        }
        else if (_step == Step.Look && IsOrkOpen) AddOrkLooks(rowButtons, Heading, Note);
        else AddPerks(rowButtons, Heading, Note);
        if (IsOrkOpen && _step is Step.Weapons or Step.Equipment) Note("The Ork Boy carries these and nothing else. He fights for either side: the Join screen's SIDE picks which.");

        if (rowButtons.Count > 0)
        {
            for (int i = 0; i < rowButtons.Count; i++)
            {
                if (i > 0) rowButtons[i].FocusNeighborTop = rowButtons[i].GetPathTo(rowButtons[i - 1]);
                if (i + 1 < rowButtons.Count) rowButtons[i].FocusNeighborBottom = rowButtons[i].GetPathTo(rowButtons[i + 1]);
            }
            int selectedAt = rowButtons.FindIndex(b => b is UiKit.Row { IsSelected: true });
            int target = Math.Clamp(keep >= 0 && !focusList ? keep : Math.Max(selectedAt, 0), 0, rowButtons.Count - 1);
            if (focusList || keep >= 0)
            {
                rowButtons[target].FocusLater();
                if (keep < 0 || focusList) Callable.From(() => { if (target == 0) _scroll.ScrollVertical = 0; }).CallDeferred();
            }
            if (rowButtons[target] is UiKit.Row row) row.Focused?.Invoke();
        }
        else if (!IsEmpty) ShowTextCard("", "");
    }

    void AddSection(OptionSection section, List<Button> rowButtons, Action<string> heading, Action<string, Color?> note)
    {
        heading(section.Heading + (section.Selectable ? "" : "  (fixed for this class)"));
        foreach (var option in section.Rows)
        {
            var o = option;
            UiKit.Glyph.Kind? glyph = o.Selected ? (o.Fixed ? UiKit.Glyph.Kind.Fixed : UiKit.Glyph.Kind.Check) : !o.Lock.IsOpen ? UiKit.Glyph.Kind.Lock : o.Fixed ? UiKit.Glyph.Kind.Fixed : null;
            var row = new UiKit.Row(o.Title, o.Tags, o.Selected, o.Selectable || o.Selected, glyph) { Focused = () => ShowItemCard(section, o) };
            row.Pressed += () => ChooseItem(section, o);
            _list.AddChild(row);
            rowButtons.Add(row);
        }
    }

    void AddPerks(List<Button> rowButtons, Action<string> heading, Action<string, Color?> note)
    {
        var custom = OpenClass;
        var options = LoadoutRows.Options(_catalog, Role, _previewRace);
        var first = options?.DefaultPerk;
        var slotRow = UiKit.HBox(8);
        slotRow.AddChild(UiKit.Text("Choosing for:", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center));
        var slotButtons = new List<Control>();
        for (int i = 0; i < 2; i++)
        {
            int slot = i;
            string? name = i == 0 ? custom?.Perk(0) ?? first?.Name : custom?.Perk(1);
            string label = name is null ? "none" : _catalog.Perks.Perk(name)?.DisplayName ?? name;
            var b = UiKit.Toggle($"Perk {i + 1}: {Trim(label, 22)}", _perkSlot == i, on => { if (on) _perkSlot = slot; Callable.From(() => RebuildList(true)).CallDeferred(); }, 0);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.ClipText = true;
            b.AddThemeFontSizeOverride("font_size", UiKit.CaptionSize);
            slotRow.AddChild(b);
            slotButtons.Add(b);
        }
        UiKit.ChainHorizontal(slotButtons);
        _list.AddChild(slotRow);
        if (IsDefaults) heading("DEFAULTS: choosing a perk edits a copy");
        var perks = LoadoutRows.Perks(_catalog, Role, _previewRace, custom, _services.Policy, _services.PerkActivities);
        string? section = null;
        foreach (var perk in perks)
        {
            string group = perk.WeaponPerk ? "WEAPON PERKS (act on their own weapon)" : "CLASS PERKS";
            if (group != section) { section = group; heading(group); }
            var p = perk;
            var tags = new List<string>();
            if (p.Slot > 0) tags.Add($"PERK {p.Slot}");
            if (p.ActivityTag.Length > 0) tags.Add(p.ActivityTag);
            if (!p.Lock.IsOpen) tags.Add(p.Lock.Tag);
            if (p.Warning.Length > 0) tags.Add("NEEDS WEAPON");
            UiKit.Glyph.Kind? glyph = p.Slot > 0 ? UiKit.Glyph.Kind.Check : !p.Lock.IsOpen ? UiKit.Glyph.Kind.Lock : p.Warning.Length > 0 ? UiKit.Glyph.Kind.Warning : null;
            var row = new UiKit.Row(p.Title, tags, p.Slot > 0, p.Lock.IsOpen, glyph, p.Warning.Length > 0 && p.Slot == 0 ? UiKit.LightRed : null) { Focused = () => ShowPerkCard(p) };
            row.Pressed += () => ChoosePerk(p);
            _list.AddChild(row);
            rowButtons.Add(row);
        }
        if (perks.Count == 0) note("This class has no perks.", null);
    }

    /// <summary>
    /// The Ork Boy's LOOK step (ui/customizer-ork): automatic or one of the boy's models (<see cref="OrkLoadout.LookChoices"/>), one choice for the whole profile. The focused row
    /// shows its look in the preview; choosing it keeps it (no copy of the DEFAULTS is needed: the look is not part of a loadout).
    /// </summary>
    void AddOrkLooks(List<Button> rowButtons, Action<string> heading, Action<string, Color?> note)
    {
        heading("ORK LOOK (all your Ork loadouts)");
        int? current = _session.Working.OrkLook;
        foreach (var look in OrkLoadout.LookChoices)
        {
            int? l = look;
            bool selected = current == look;
            var row = new UiKit.Row(OrkLoadout.LookTitle(look), selected ? ["SELECTED"] : [], selected, true, selected ? UiKit.Glyph.Kind.Check : null)
            {
                Focused = () => { ShowTextCard(OrkLoadout.LookTitle(l), OrkLoadout.LookDescription(l)); if (_previewOrkLook != l) { _previewOrkLook = l; UpdatePreview(); } },
            };
            row.Pressed += () => ChooseOrkLook(l);
            _list.AddChild(row);
            rowButtons.Add(row);
        }
        note("The Ork has no armour pieces, colours or badges: his look is one of the boy's own models, sent with your loadout when you join.", null);
    }

    void ChooseOrkLook(int? look)
    {
        var r = OrkLoadout.SetLook(_session.Working, look);
        if (!r.Changed) { if (r.Notice is { Length: > 0 } n) Say(n); return; }
        _previewOrkLook = look;
        _session.Edit(_ => r.Profile);
        Say(r.Notice ?? "");
    }

    static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + ".";

    // ---- the card ----

    // The card never outgrows its room (ui/hud-2: the perk cards with the longest descriptions pushed the action bar down): it grows with its text from CardMinHeight up to CardMaxHeight
    // virtual units, then the text shrinks a size at a time (CardTextMax down to CardTextMin, still readable) until it fits, and only below that does the text area scroll (Sure Strike's 1176
    // characters scroll; every other card fits).
    const int CardMinHeight = 176, CardMaxHeight = 340, CardTextMax = UiKit.CaptionSize + 2, CardTextMin = 16;

    PanelContainer? _cardPanel;
    Label? _cardTitle, _cardBody, _cardExtra;
    ScrollContainer? _cardScroll;
    string _cardFittedFor = "";

    /// <summary>The card's fit now (test aid): the text size in use, the height its text needs at that size and the height it has, the card panel's rectangle and whether the text scrolls.</summary>
    public static int CardSmallestText => CardTextMin;

    public (int FontSize, float Needed, float Available, Rect2 Panel, bool Scrolls, int Chars) CardFit =>
        (_cardBody?.GetThemeFontSize("font_size") ?? 0, _cardNeeded, _cardAvailable, _cardPanel?.GetGlobalRect() ?? default, _cardScroll?.GetVScrollBar().Visible ?? false,
            (_cardBody?.Text.Length ?? 0) + (_cardExtra?.Text.Length ?? 0));

    float _cardNeeded, _cardAvailable;

    void ShowTextCard(string title, string body, string extra = "", Color? extraColour = null)
    {
        foreach (var c in _card.GetChildren()) { _card.RemoveChild(c); c.QueueFree(); }
        _cardTitle = UiKit.Text(title, UiKit.HeaderSize - 4, UiKit.Gold).With(l => l.ClipText = true);
        _card.AddChild(_cardTitle);
        var text = UiKit.VBox(6);
        _cardBody = body.Length > 0 ? UiKit.Text(body, CardTextMax, UiKit.White, HorizontalAlignment.Left, true) : null;
        _cardExtra = extra.Length > 0 ? UiKit.Text(extra, CardTextMax, extraColour ?? UiKit.Grey, HorizontalAlignment.Left, true) : null;
        if (_cardBody is not null) text.AddChild(_cardBody);
        if (_cardExtra is not null) text.AddChild(_cardExtra);
        _cardScroll = UiKit.Scroll(text);
        _cardScroll.CustomMinimumSize = new Vector2(0, 20);
        _card.AddChild(_cardScroll);
        _cardFittedFor = "";
        _cardScroll.Resized += FitCard;
        Callable.From(FitCard).CallDeferred();
    }

    /// <summary>Sizes the card for its text (see <see cref="CardMinHeight"/>): the panel's height, then the largest text size that fits it.</summary>
    void FitCard()
    {
        if (_cardScroll is null || _cardPanel is null || _cardTitle is null || !GodotObject.IsInstanceValid(_cardScroll)) return;
        float width = _cardScroll.Size.X;
        if (width < 60f) return;   // not laid out yet: the next resize asks again
        string key = $"{width:F0}|{_cardTitle.Text}|{_cardBody?.Text}|{_cardExtra?.Text}";
        if (key == _cardFittedFor) return;
        _cardFittedFor = key;
        float Height(int size)
        {
            float h = 0f;
            foreach (var l in new[] { _cardBody, _cardExtra })
            {
                if (l is null) continue;
                var font = l.GetThemeFont("font");
                // A little over the measure: the label wraps at the word that does not fit, the measure at the same width.
                h += (font?.GetMultilineStringSize(l.Text, HorizontalAlignment.Left, width, size).Y ?? size * 1.3f) * 1.03f + 6f;
            }
            return h;
        }
        // The room the text has at a panel height: the panel less its margins (12 each side), the title line and the gap under it.
        float chrome = 24f + _cardTitle.GetCombinedMinimumSize().Y + 6f;
        float want = Height(CardTextMax) + chrome;
        float panel = Math.Clamp(want, CardMinHeight, CardMaxHeight);
        _cardPanel.CustomMinimumSize = new Vector2(0, panel);
        float available = panel - chrome;
        int fitted = CardTextMax;
        while (fitted > CardTextMin && Height(fitted) > available) fitted--;
        foreach (var l in new[] { _cardBody, _cardExtra }) l?.AddThemeFontSizeOverride("font_size", fitted);
        _cardNeeded = Height(fitted); _cardAvailable = available;
    }

    /// <summary>The perk rows' cards of the open class on the Perks step, each as the action that shows it (test aid: the self-test checks that every card fits).</summary>
    public IEnumerable<(string Title, Action Show)> PerkCardsForTest()
    {
        var perks = LoadoutRows.Perks(_catalog, Role, _previewRace, OpenClass, _services.Policy, _services.PerkActivities);
        foreach (var perk in perks)
        {
            var p = perk;
            yield return (p.Title, () => ShowPerkCard(p));
        }
    }

    void ShowEmptyCard() => ShowTextCard("Empty slot", "A custom loadout keeps your weapons, grenade and perks, and the armour look you give it. Pick one of the three classes to start.");

    void ShowClassCard(string role)
    {
        var options = LoadoutRows.Options(_catalog, role, _previewRace);
        ShowTextCard(LoadoutRows.RoleLabel(_catalog, role), options?.Description ?? "", options is null ? "" : "Default: " + LoadoutRows.DefaultSummary(_catalog, role, _previewRace));
    }

    void ShowItemCard(OptionSection section, OptionRow row)
    {
        var parts = new List<string>();
        if (row.Fixed) parts.Add("Fixed for this class.");
        if (row.NotSimulated) parts.Add("Not simulated yet: you can choose it, the server gives you the default until it is.");
        if (!row.Lock.IsOpen) parts.Add(row.Lock.Caption);
        if (IsDefaults && !row.Fixed && !row.Selected) parts.Add("Choosing it makes an editable copy of these defaults.");
        ShowTextCard(row.Title, row.Description, string.Join("\n", parts), row.Lock.IsOpen ? UiKit.Grey : UiKit.LightRed);
    }

    void ShowPerkCard(PerkRow perk)
    {
        var parts = new List<string>();
        if (perk.ActivityNote.Length > 0) parts.Add(perk.ActivityNote);
        if (perk.Warning.Length > 0) parts.Add(perk.Warning + ".");
        if (!perk.Lock.IsOpen) parts.Add(perk.Lock.Caption);
        ShowTextCard(perk.Title, perk.Description, string.Join("\n", parts), perk.Warning.Length > 0 || !perk.Lock.IsOpen ? UiKit.LightRed : UiKit.Grey);
    }

    // ---- actions ----

    public void Say(string text, double seconds = 2.5)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", UiKit.White);
        _statusUntil = Time.GetTicksMsec() / 1000.0 + seconds;
    }

    public override void _Process(double delta)
    {
        if (_status.Text.Length > 0 && Time.GetTicksMsec() / 1000.0 > _statusUntil) _status.Text = "";
    }

    void ChooseRole(string role)
    {
        if (IsDefaults)
        {
            _open = Selection.DefaultFor(role);
            Refresh(false);
            return;
        }
        int slot = _open.Slot;
        if (OpenClass is null)
        {
            var created = LoadoutEdits.Create(_session.Working, slot, role, _catalog);
            if (created.Changed) { _session.Edit(_ => created.Profile); Say($"Slot {slot}: {LoadoutRows.ClassName(_catalog, role, _previewRace)} created."); _step = Step.Weapons; Refresh(true); }
            return;
        }
        if (OpenClass.Role.Equals(role, StringComparison.OrdinalIgnoreCase)) return;
        bool edited = OpenClass.Primary is not null || OpenClass.Pistol is not null || OpenClass.Melee is not null || OpenClass.Equipment is not null || OpenClass.Perks.Any(p => p is not null);
        void Apply()
        {
            var r = LoadoutEdits.SetRole(_session.Working, slot, role, _catalog);
            if (r.Changed) { _session.Edit(_ => r.Profile); Say(r.Notice ?? ""); }
        }
        if (!edited) { Apply(); return; }
        UiKit.Dialog.Open(this, Host, "Change class?", "Your weapons, equipment and perks for this slot go back to the new class's defaults.", ["Change class", "Cancel"], i => { if (i == 0) Apply(); });
    }

    void ChooseItem(OptionSection section, OptionRow row)
    {
        if (IsEmpty) return;
        if (row.Fixed) { Say($"{row.Title} is fixed for this class."); return; }
        if (!row.Lock.IsOpen) { Say(row.Lock.Caption); return; }
        if (row.Selected) return;
        string copied = MakeEditable();
        if (IsDefaults) return;   // all ten slots are used: the notice is on the status line
        var r = LoadoutEdits.SetItem(_session.Working, _open.Slot, section.Key, row.Id.Length == 0 ? null : row.Id, _catalog);
        if (r.Changed) { _session.Edit(_ => r.Profile); Say(copied + (row.NotSimulated ? $"{row.Title} chosen. It is not simulated yet: you get the default for now." : $"{row.Title} chosen."), copied.Length > 0 ? 6.0 : 2.5); }
        else if (r.Notice is { Length: > 0 } n) Say(copied + n);
    }

    /// <summary>
    /// The DEFAULTS loadouts are the data's classes and cannot be changed (the original's are fixed too): the first choice made on one makes an editable copy of it in the first
    /// empty slot, opens that, and (when these defaults were the active loadout) makes it the active one, so a choice is never silently refused (the owner could not pick the
    /// power sword or a perk on the DEFAULTS the creator opens with). Returns the line to put before the choice's own, or "" when nothing was copied; when it could not
    /// (all slots used) the status line says why and <see cref="IsDefaults"/> stays true.
    /// </summary>
    string MakeEditable()
    {
        if (!IsDefaults) return "";
        var profile = _session.Working;
        string role = Role;
        bool wasActive = LoadoutEdits.Active(profile, _catalog, out _) == _open;
        var r = LoadoutEdits.CopyDefaults(profile, role, LoadoutRows.ClassName(_catalog, role, _previewRace), _catalog, wasActive, out int slot);
        if (!r.Changed) { Say(r.Notice ?? "The defaults cannot be changed.", 6.0); return ""; }
        _copiedFrom = (slot, _open);
        _open = Selection.CustomSlot(slot);
        _session.Edit(_ => r.Profile);
        return (r.Notice ?? "") + " ";
    }

    void ChoosePerk(PerkRow perk)
    {
        if (IsEmpty) return;
        if (!perk.Lock.IsOpen) { Say(perk.Lock.Caption); return; }
        if (IsDefaults && perk.Slot > 0) { Say($"{perk.Title} is already the class's default perk. Choose another perk to change it."); return; }
        string copied = MakeEditable();
        if (IsDefaults) return;   // all ten slots are used: the notice is on the status line
        var profile = _session.Working;
        int slot = _open.Slot;
        string? notice = null;
        // Already in a slot: choosing it again clears that slot (perk 1 returns to the class default).
        if (perk.Slot > 0)
        {
            var cleared = LoadoutEdits.SetPerk(profile, slot, perk.Slot - 1, null, _catalog);
            if (cleared.Changed) { _session.Edit(_ => cleared.Profile); Say(cleared.Notice ?? (perk.Slot == 1 ? "Perk 1 is back to the class default." : "Perk 2 cleared.")); }
            return;
        }
        // Move it out of the other slot first when it sits there (never both).
        var p = profile;
        var r = LoadoutEdits.SetPerk(p, slot, _perkSlot, perk.Name, _catalog);
        if (!r.Changed && r.Notice is { Length: > 0 }) { Say(copied + r.Notice); return; }
        notice = r.Notice;
        _session.Edit(_ => r.Profile);
        Say(copied + (notice ?? $"{perk.Title} chosen for perk {_perkSlot + 1}." + (perk.Warning.Length > 0 ? " " + perk.Warning + "." : "")), copied.Length > 0 ? 6.0 : 2.5);
    }

    void RenameSlot()
    {
        if (OpenClass is not { } c) return;
        int slot = _open.Slot;
        UiKit.Dialog.Prompt(this, Host, "Rename loadout", $"Up to {LoadoutEdits.MaxNameLength} characters. Empty goes back to \"{LoadoutEdits.DefaultName(slot)}\".", c.Name, ["Rename", "Cancel"], (i, text) =>
        {
            if (i != 0) return;
            var r = LoadoutEdits.Rename(_session.Working, slot, text);
            if (r.Changed) { _session.Edit(_ => r.Profile); Say("Renamed."); }
        });
    }

    void CopySlot()
    {
        if (IsEmpty) return;
        int from = _open.Slot;
        bool fromDefaults = IsDefaults;
        string role = Role;
        UiKit.Dialog.Prompt(this, Host, "Copy to a slot", "Slot number, 1 to 10.", "", ["Copy", "Cancel"], (i, text) =>
        {
            if (i != 0) return;
            if (!int.TryParse(text.Trim(), out int to) || to is < 1 or > CustomizationProfile.MaxCustomClasses) { Say("Enter a slot from 1 to 10."); return; }
            if (!fromDefaults && to == from) { Say("Choose another slot."); return; }
            void Do()
            {
                EditResult r;
                if (fromDefaults)
                {
                    var without = _session.Working.ClassAt(to) is null ? _session.Working : LoadoutEdits.Clear(_session.Working, to).Profile;
                    r = LoadoutEdits.Create(without, to, role, _catalog);
                }
                else r = LoadoutEdits.Copy(_session.Working, from, to);
                if (r.Changed) { _session.Edit(_ => r.Profile); _open = Selection.CustomSlot(to); Say($"Copied to slot {to}."); Refresh(false); }
                else if (r.Notice is { Length: > 0 } n) Say(n);
            }
            if (_session.Working.ClassAt(to) is { } existing)
                UiKit.Dialog.Open(this, Host, "Replace this loadout?", $"Slot {to} (\"{existing.Name}\") will be replaced.", ["Replace", "Cancel"], k => { if (k == 0) Do(); });
            else Do();
        });
    }

    void ResetSlot()
    {
        if (OpenClass is null) return;
        int slot = _open.Slot;
        UiKit.Dialog.Open(this, Host, "Reset this loadout?", "Weapons, equipment and perks go back to the class defaults. The name, class and look stay.", ["Reset", "Cancel"], i =>
        {
            if (i != 0) return;
            var r = LoadoutEdits.Reset(_session.Working, slot);
            if (r.Changed) { _session.Edit(_ => r.Profile); Say(r.Notice ?? "Reset."); }
        });
    }

    void ClearSlot()
    {
        if (OpenClass is not { } c) return;
        int slot = _open.Slot;
        UiKit.Dialog.Open(this, Host, "Clear this loadout?", $"\"{c.Name}\" in slot {slot} is removed.", ["Clear", "Cancel"], i =>
        {
            if (i != 0) return;
            var r = LoadoutEdits.Clear(_session.Working, slot);
            if (r.Changed) { _copiedFrom = null; _session.Edit(_ => r.Profile); Say(r.Notice ?? "Cleared."); }
        }, titleColour: UiKit.LightRed);
    }

    void RevertAll()
    {
        if (!_session.Dirty) return;
        UiKit.Dialog.Open(this, Host, "Discard unsaved changes?", "Everything since the last save is dropped (loadouts and looks).", ["Revert", "Cancel"], i =>
        {
            if (i != 0) return;
            _session.Revert();
            ReturnFromRevertedCopy();
            _open = ValidOpen();
            Say("Reverted.");
            Refresh(false);
        });
    }

    void Save()
    {
        if (!_session.Dirty) return;
        string? error = _session.Save();
        if (error is null) Say("Saved.");
        else
        {
            _status.Text = "Could not save: " + error;
            _status.AddThemeColorOverride("font_color", UiKit.LightRed);
            _statusUntil = Time.GetTicksMsec() / 1000.0 + 6;
        }
    }

    /// <summary>The SET ACTIVE button's action (test aid <c>--picker-activate</c>: the same code path as the button).</summary>
    public void PressSetActive() => SetActive();

    void SetActive()
    {
        if (IsEmpty) return;
        if (Activate is { } activate) { Say(activate(_open), 8.0); return; }   // in a match: marks it active, saves and sends it (NetworkView.Loadout.cs)
        _session.Edit(p => LoadoutEdits.Select(p, _open));
        Say(_open.Kind == SelectionKind.Custom ? $"Slot {_open.Slot} is your active loadout. Save to keep it." : "The defaults are your active loadout. Save to keep it.");
    }

    void OpenAppearance()
    {
        // The Ork Boy has no armour or colours (ui/customizer-ork): his look choice instead of the appearance editor.
        if (IsOrkOpen) { _step = Step.Look; Refresh(focusList: true); return; }
        if (OpenClass is null) { Say("Create a custom loadout first; the look belongs to a custom loadout."); return; }
        _onAppearance();
    }

    /// <summary>Back: asks about unsaved edits (Save, Discard, Cancel) and then leaves.</summary>
    public void Back()
    {
        if (!_session.Dirty) { _onBack(); return; }
        UiKit.Dialog.Open(this, Host, "Unsaved changes", "Save your loadouts and looks before you leave?", ["Save", "Discard", "Cancel"], i =>
        {
            if (i == 0) { string? error = _session.Save(); if (error is null) _onBack(); else Say("Could not save: " + error); }
            else if (i == 1) { _session.Revert(); ReturnFromRevertedCopy(); _onBack(); }
        });
    }

    // ---- input ----

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (!IsVisibleInTree() || e is not InputEventKey { Pressed: true } key) return;
        bool ctrl = key.CtrlPressed || key.MetaPressed;
        switch (key.PhysicalKeycode)
        {
            case Key.S when ctrl: Save(); break;
            case Key.F2: RenameSlot(); break;
            case Key.Pagedown: MoveStep(1); break;
            case Key.Pageup: MoveStep(-1); break;
            case Key.Tab when ctrl: MoveStep(key.ShiftPressed ? -1 : 1); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree()) return;
        if (e.IsActionPressed("ui_cancel")) { GetViewport().SetInputAsHandled(); Back(); return; }
        if (e is InputEventJoypadButton { Pressed: true } pad)
        {
            switch (pad.ButtonIndex)
            {
                case JoyButton.LeftShoulder: MoveStep(-1); break;
                case JoyButton.RightShoulder: MoveStep(1); break;
                case JoyButton.X: CopySlot(); break;
                case JoyButton.Y: RenameSlot(); break;
                case JoyButton.Start: Save(); break;
                default: return;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    void MoveStep(int delta)
    {
        if (IsEmpty) return;
        _step = (Step)(((int)_step + delta + StepCount) % StepCount);
        Refresh(focusList: false);
    }

    /// <summary>Focuses the first control (screens and tests).</summary>
    public void FocusList() => Refresh(true);
}

static class ControlExtensions
{
    /// <summary>Grabs the focus on the next idle frame, if the control is still in the visible tree then (lists are rebuilt, so a row may be gone by then).</summary>
    public static void FocusLater(this Control control) =>
        Callable.From(() => { if (GodotObject.IsInstanceValid(control) && control.IsInsideTree() && control.IsVisibleInTree()) control.GrabFocus(); }).CallDeferred();

    /// <summary>Runs <paramref name="configure"/> on the node and returns it (a short way to set a property in a build expression).</summary>
    public static T With<T>(this T node, Action<T> configure) { configure(node); return node; }
}
