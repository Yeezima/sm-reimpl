using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmContent.Extras.Musou;

namespace SmClient.Customizer;

/// <summary>
/// Musou Mode's customizer (owner, 2026-11-10; <see cref="MusouCustomizer"/>; docs/changes-from-original.md section 0). In Musou Mode the creator's faction row becomes the top level
/// STANDARD MARINES | LIBRARIAN | HERETIC (NOT YET):
/// <list type="bullet">
/// <item><b>Standard Marines</b>: the creator as Classic has it, on the Space Marine side alone (the class tabs with the Space Marine names, no Chaos preview; the appearance editor edits the
/// Space Marine look, <see cref="CustomizerServices.MusouLayout"/>). Since musou/loadout-slots the loadout slots are Musou's own (<see cref="MusouSlots"/>, through the session's view), apart from
/// Classic's; <c>--musou-shared-slots</c> shares them again.</item>
/// <item><b>Librarian</b>: a page of his own in the same frame: CLASS, WEAPONS, POWERS, PERKS and LOOK. Since musou/loadout-slots he has a loadout of his own (<see cref="MusouLibrarianLoadout"/>):
/// melee weapon, equipment, two perks and a look (the active loadout's, a saved one or a new one), chosen on the rows and saved with SAVE; EDIT LOOK opens the look he wears. The preview shows
/// him dressed (<see cref="PreviewStage.MusouLibrarian"/>). In a match PLAY AS LIBRARIAN takes the Esc menu row's path (<see cref="MusouLibrarianDeploy"/>).</item>
/// <item><b>Heretic</b>: a placeholder marked not yet available: no class, preview or gameplay.</item>
/// </list>
/// Nothing here acts unless the customizer serves the Musou profile (a Musou match's picker, MUSOU MODE's pages); <c>--musou-old-customizer</c> restores the shared layout. Test aid:
/// <c>--musou-entry marines|librarian|heretic</c> opens the Musou layout on that entry.
/// </summary>
public sealed partial class CreatorView
{
    static readonly MusouCustomizerEntry? MusouStartEntry = ParseMusouEntry();

    MusouCustomizerEntry _musouEntry = MusouCustomizerEntry.StandardMarines;
    bool _musouStarted, _musouChromeHidden;
    Step _musouMarineStep = Step.Weapons, _musouLibrarianStep = Step.Class;
    readonly List<(MusouCustomizerEntry Entry, Button Button)> _musouButtons = new();
    PanelContainer? _musouPlaceholder;

    /// <summary>The Musou top level shows (test aid and the shells): the profile is Musou's and the restore switch is off.</summary>
    public bool MusouLayout => _services.MusouLayout;

    /// <summary>The Musou entry open now (test aid).</summary>
    public MusouCustomizerEntry MusouEntry => _musouEntry;

    /// <summary>In a Musou match: PLAY AS LIBRARIAN on his page (null: the picker has no way to pick him now). Returns null when he was picked, else the reason for the status line.</summary>
    public Func<string?>? MusouLibrarianDeploy { get; set; }

    /// <summary>Opens a Musou entry (the shells: a Librarian's picker opens on his page; test aids).</summary>
    public void MusouOpen(MusouCustomizerEntry entry)
    {
        _musouStarted = true;
        SwitchMusouEntry(entry);
        SyncMusouButtons();
        Refresh(focusList: false);
    }

    static MusouCustomizerEntry? ParseMusouEntry()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--musou-entry");
        if (i < 0 || i + 1 >= args.Length) return null;
        string v = args[i + 1].ToLowerInvariant();
        return v.StartsWith("lib") ? MusouCustomizerEntry.Librarian : v.StartsWith("her") ? MusouCustomizerEntry.Heretic : MusouCustomizerEntry.StandardMarines;
    }

    /// <summary>The entry has its own page (the Librarian, the Heretic) in the Musou layout.</summary>
    bool MusouOwnPage => MusouLayout && _musouEntry != MusouCustomizerEntry.StandardMarines;

    Control StepsPanel => (Control)_steps.GetParent();

    // ---- the top level ----

    partial void ExtrasTopLevel(ref bool built)
    {
        _musouButtons.Clear();
        if (!MusouLayout) return;
        built = true;
        if (!_musouStarted) { _musouStarted = true; if (MusouStartEntry is { } start) SwitchMusouEntry(start); }
        _previewRace = CustomizationCatalog.SpaceMarine;   // Musou Mode's players are Space Marines
        if (_sideCaption is not null) _sideCaption.Text = MusouCustomizer.Caption;
        foreach (var info in MusouCustomizer.Entries)
        {
            var entry = info.Entry;
            var b = UiKit.Toggle(info.Label, _musouEntry == entry, on => { if (on) ChooseMusouEntry(entry); else SyncMusouButtons(); }, info.Width);
            b.Name = "Musou" + entry;
            b.TooltipText = info.Tooltip;
            if (!info.Available) b.AddThemeColorOverride("font_color", UiKit.Grey);
            _sideToggle.AddChild(b);
            _musouButtons.Add((entry, b));
        }
    }

    void ChooseMusouEntry(MusouCustomizerEntry entry)
    {
        SwitchMusouEntry(entry);
        SyncMusouButtons();
        Refresh(focusList: false);
    }

    /// <summary>Changes the entry; each page keeps its own step (the Marines' WEAPONS, the Librarian's CLASS to start).</summary>
    void SwitchMusouEntry(MusouCustomizerEntry entry)
    {
        if (entry == _musouEntry) return;
        if (_musouEntry == MusouCustomizerEntry.StandardMarines) _musouMarineStep = _step;
        else if (_musouEntry == MusouCustomizerEntry.Librarian) _musouLibrarianStep = _step;
        _musouEntry = entry;
        _step = entry == MusouCustomizerEntry.StandardMarines ? _musouMarineStep : entry == MusouCustomizerEntry.Librarian ? _musouLibrarianStep : Step.Class;
        if (entry != MusouCustomizerEntry.Librarian) LeaveLibrarianSlot();
        SyncLibrarianView();
    }

    /// <summary>
    /// Outside his page the open loadout is never the Librarian's slot (<see cref="MusouSlots.LibrarianSlot"/>; review of musou/loadout-slots: after his look's EDIT LOOK, back on the
    /// Standard Marines the creator showed "CUSTOM 11 - empty", a class chosen there was dropped and DEPLOY said the slot was empty): the active loadout opens instead.
    /// </summary>
    void LeaveLibrarianSlot()
    {
        if (_open.Kind == SelectionKind.Custom && _open.Slot > CustomizationProfile.MaxCustomClasses)
            _open = LoadoutEdits.Active(_session.Working, _catalog, out _);
    }

    void SyncMusouButtons()
    {
        foreach (var (entry, button) in _musouButtons) if (GodotObject.IsInstanceValid(button)) button.SetPressedNoSignal(entry == _musouEntry);
    }

    partial void ExtrasRoleLabel(string role, ref string label)
    {
        if (!MusouLayout) return;
        label = LoadoutRows.ClassName(_catalog, role, CustomizationCatalog.SpaceMarine);   // the Space Marine side alone
    }

    partial void ExtrasOwnPage(ref bool own)
    {
        if (MusouOwnPage) own = true;   // the Librarian's and the Heretic's pages: no slot actions, step keys on the page's own steps
    }

    // ---- the support ability (owner, 2026-11-12; docs/drop-pod-assault.md) ----

    /// <summary><c>--no-drop-pod</c> (restore switch, owner 2026-11-12): the customizer has no SUPPORT step and B keeps calling the artillery strike (the same flag NetworkView reads); the pages look as before the step.</summary>
    static readonly bool MusouNoSupportStep = OS.GetCmdlineUserArgs().Contains("--no-drop-pod");

    /// <summary>The Librarian's steps shown: all of them, or without SUPPORT under the restore switch.</summary>
    static int LibrarianStepCount => MusouCustomizer.LibrarianSteps.Count - (MusouNoSupportStep ? 1 : 0);

    /// <summary>The marines' page shows SUPPORT as its fifth step (after PERKS: LOOK is the Ork's, and the Ork has no Musou page); the Librarian's own count already holds it.</summary>
    partial void ExtrasStepShown(int step, ref bool shown)
    {
        if (MusouLayout && !MusouOwnPage && !IsOrkOpen) shown = step <= (int)Step.Perks || (step == (int)Step.Support && !MusouNoSupportStep);
    }

    /// <summary>The support the open loadout calls on B: its entry in the Musou extras (the Librarian's own key on his page).</summary>
    MusouSupport OpenSupport() => MusouSlots.SupportOf(_session.Working, _musouEntry == MusouCustomizerEntry.Librarian ? MusouSlots.LibrarianSupportKey : MusouSlots.SupportKey(_open));

    /// <summary>The SUPPORT step of the marines' page: the same two rows the Librarian's page has; the choice is stored per loadout (a DEFAULTS loadout by its role).</summary>
    partial void ExtrasSupportRows(List<Button> rowButtons, Action<string> heading, Action<string, Color?> note)
    {
        var chosen = OpenSupport();
        foreach (var section in MusouCustomizer.SupportRows(chosen))
        {
            heading(section.Heading);
            foreach (var item in section.Rows)
            {
                var r = item;
                var row = new UiKit.Row(r.Title, r.Selected ? ["CHOSEN"] : [], r.Selected, true, r.Selected ? UiKit.Glyph.Kind.Check : null) { Focused = () => ShowTextCard(r.Title, r.Description, r.Selected ? "Chosen for this loadout. SAVE keeps it." : "Choose it for this loadout.", UiKit.Grey) };
                row.Pressed += () => ChooseSupport(r);
                _list.AddChild(row);
                rowButtons.Add(row);
            }
        }
        note(IsDefaults ? "DEFAULTS: the choice is kept for this class's defaults. Every loadout and the Librarian have their own." : "Every loadout has its own support.", null);
    }

    /// <summary>A SUPPORT row pressed: the open loadout's entry (the Librarian's on his page), kept by SAVE.</summary>
    void ChooseSupport(MusouRow row)
    {
        if (row.Action is not { } action || !action.StartsWith("support:")) return;
        var next = action["support:".Length..] == MusouSlots.SupportName(MusouSupport.DropPod) ? MusouSupport.DropPod : MusouSupport.Artillery;
        string key = _musouEntry == MusouCustomizerEntry.Librarian ? MusouSlots.LibrarianSupportKey : MusouSlots.SupportKey(_open);
        if (next != OpenSupport()) _session.Edit(p => MusouSlots.WithSupport(p, key, next));
        Say($"{row.Title}: B calls it for this loadout. SAVE keeps it.", 4.0);
        Refresh(focusList: false);
    }

    partial void ExtrasStepCount(ref int count)
    {
        if (!MusouOwnPage) return;
        count = _musouEntry == MusouCustomizerEntry.Librarian ? LibrarianStepCount : 1;
    }

    // ---- the pages ----

    /// <summary>Whether the Librarian's page is the one open, and so whether the session's view shows his loadout as a class (<see cref="MusouSlots.LibrarianSlot"/>).</summary>
    void SyncLibrarianView()
    {
        bool shown = MusouLayout && _musouEntry == MusouCustomizerEntry.Librarian;
        if (_services.MusouLibrarianShown == shown) return;
        _services.MusouLibrarianShown = shown;
        _services.ApplyProfileView();
    }

    /// <summary>He has his own loadout here: Musou's own slots are in use (not <c>--musou-shared-slots</c>) and the view shows him.</summary>
    bool LibrarianOwnLoadout => _services.MusouOwnSlots && _session.Working.ClassAt(MusouSlots.LibrarianSlot) is not null;

    partial void ExtrasRefresh(bool focusList, ref bool handled)
    {
        SyncLibrarianView();
        if (!MusouOwnPage)
        {
            LeaveLibrarianSlot();
            MusouShowChrome();
            _preview.MusouLibrarian = false;
            if (MusouLayout && !MusouNoSupportStep && _stepButtons.Count > (int)Step.Support) _stepButtons[(int)Step.Support].Text = $"{(int)Step.Look + 1}  {StepNames[(int)Step.Support]}";   // 5  SUPPORT (the hidden LOOK is the Ork's)
            return;
        }
        handled = true;
        MusouHideChrome();
        var profile = _session.Working;
        var active = LoadoutEdits.Active(profile, _catalog, out _);
        _activeLabel.Text = "ACTIVE: " + ActiveName(profile, active);
        _activeLabel.TooltipText = _activeLabel.Text;
        _title.Text = "LOADOUTS";
        bool dirty = _session.Dirty;
        _unsaved.Text = dirty ? "* UNSAVED" : "";
        _save!.Visible = true; _save.Disabled = !dirty || _session.ReadOnly;
        _revert!.Visible = true; _revert.Disabled = !dirty;
        if (_musouEntry == MusouCustomizerEntry.Librarian) RefreshLibrarian(profile, active, focusList);
        else RefreshHeretic(focusList);
    }

    /// <summary>
    /// The look slot the Librarian wears: his own (<see cref="MusouSlots.LibrarianSlot"/>) when his loadout names a look, else the active loadout's when it is a custom one (a DEFAULTS loadout wears the
    /// default look, which cannot be edited).
    /// </summary>
    int? LibrarianLookSlot(CustomizationProfile profile, Selection active)
    {
        if (profile.ClassAt(MusouSlots.LibrarianSlot) is { Appearance.SpaceMarine: { } own } && profile.Appearance(own) is not null) return MusouSlots.LibrarianSlot;
        return active.Kind == SelectionKind.Custom && profile.ClassAt(active.Slot) is not null ? active.Slot : null;
    }

    /// <summary>His own loadout as the page offers it (null when he has none here: <c>--musou-shared-slots</c>).</summary>
    MusouLibrarianPicks? LibrarianPicks(CustomizationProfile profile)
    {
        if (!LibrarianOwnLoadout || profile.ClassAt(MusouSlots.LibrarianSlot) is not { } c) return null;
        var data = LibrarianData;
        var options = LoadoutRows.Options(_catalog, MusouSlots.LibrarianRole, CustomizationCatalog.SpaceMarine);
        var rows = LoadoutRows.Perks(_catalog, MusouSlots.LibrarianRole, CustomizationCatalog.SpaceMarine, c, _services.Policy, _services.PerkActivities);
        // His class's perks (the simulation's list), in the creator's order and words; without the data's list, the Tactical's from the catalogue. A weapon perk shows only for a weapon he can
        // carry (he has no gun: the Tactical's bolter, melta and plasma perks would do nothing for him).
        bool Carried(PerkRow r) => !r.WeaponPerk || PerkWeapons.Needs(r.Name) is { } needs && needs.Any(n => data.MeleeOptions.Any(m => m.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        var perks = rows.Where(r => (data.PerkNames.Count == 0 || data.PerkNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)) && Carried(r))
            .Select(r => (r.Name, r.Title, r.Description)).ToList();
        var looks = profile.AppearancesFor(CustomizationCatalog.SpaceMarine).Select(a => (a.Id, a.Name)).ToList();
        return new MusouLibrarianPicks(MusouSlots.LibrarianLoadoutOf(c), perks, options?.DefaultPerk?.Name, looks);
    }

    LibrarianLoadout? _librarianData;

    /// <summary>
    /// The Librarian's loadout from the data (<see cref="LibrarianLoadout.Of"/>: his class as MusouLoadout appends it to this run's configuration), named as the catalogue names wargear; read once. So the
    /// page follows his class (weapons, melee, equipment) and LibrarianRules' powers without an edit here.
    /// </summary>
    LibrarianLoadout LibrarianData => _librarianData ??= _services.Config is { } config ? LibrarianLoadout.Of(config, WargearTitle) : LibrarianLoadout.None;

    /// <summary>A wargear name's display name: the catalogue's item of that name on any Space Marine class, else a readable form of the name.</summary>
    string WargearTitle(string wargear)
    {
        string neutral = ProfileWire.Neutral(wargear);
        foreach (var role in ProfileStore.Roles(_catalog))
        {
            if (LoadoutRows.Options(_catalog, role, CustomizationCatalog.SpaceMarine) is not { } options) continue;
            foreach (var key in new[] { "primary", "pistol", "melee", "equipment" })
                if (options.For(key).Items.FirstOrDefault(i => ProfileWire.Neutral(i.Name).Equals(neutral, StringComparison.OrdinalIgnoreCase)) is { } item)
                    return key == "equipment" ? LoadoutRows.EquipmentTitle(item) : item.DisplayName;
        }
        string tail = wargear[(wargear.LastIndexOf('_') + 1)..];
        return tail.Length == 0 ? wargear : char.ToUpperInvariant(tail[0]) + tail[1..];
    }

    /// <summary>The Librarian's body is the Tactical Marine's (MusouLoadout.AppendLibrarian).</summary>
    string TacticalRole => ProfileStore.Roles(_catalog).FirstOrDefault(r => r.Equals("tactical", StringComparison.OrdinalIgnoreCase)) ?? ProfileStore.FirstRole(_catalog);

    void RefreshLibrarian(CustomizationProfile profile, Selection active, bool focusList)
    {
        string activeName = ActiveName(profile, active);
        var picks = LibrarianPicks(profile);
        _summary.Visible = true;
        _summary.Text = MusouCustomizer.LibrarianSummary(activeName, LibrarianData, picks);
        _summary.TooltipText = _summary.Text;

        int count = LibrarianStepCount;
        if ((int)_step >= count) _step = Step.Class;
        StepsPanel.Visible = true;
        for (int i = 0; i < _stepButtons.Count; i++)
        {
            _stepButtons[i].Visible = i < count;
            if (i < count) _stepButtons[i].Text = $"{i + 1}  {MusouCustomizer.LibrarianSteps[i]}";
            _stepButtons[i].SetPressedNoSignal((int)_step == i);
        }
        int? lookSlot = LibrarianLookSlot(profile, active);
        if (_lookCaption is not null) _lookCaption.Visible = true;
        if (_appearanceStepButton is not null) { _appearanceStepButton.Visible = true; _appearanceStepButton.Text = "Edit look..."; }
        _scroll.Visible = true;
        _preview.Visible = true;
        if (_cardPanel is not null) _cardPanel.Visible = true;
        if (_musouPlaceholder is not null) _musouPlaceholder.Visible = false;

        _look!.Visible = true;
        _look.Text = "EDIT LOOK...";
        _look.Disabled = lookSlot is null;
        _look.TooltipText = lookSlot is null ? MusouCustomizer.DefaultsLookNote
            : lookSlot == MusouSlots.LibrarianSlot ? "The look he wears (his loadout's)" : "The look of your active loadout: the Librarian wears it";
        _deployButton!.Visible = Deploy is not null;   // in a match only
        _deployButton.Text = "PLAY AS LIBRARIAN";
        _deployButton.Disabled = MusouLibrarianDeploy is null;
        _deployButton.TooltipText = MusouLibrarianDeploy is null ? "Pick him in the Stormbird: Esc, PLAY AS LIBRARIAN" : "Become the Librarian now (in the Stormbird)";

        MusouList(MusouCustomizer.LibrarianStep((int)_step, activeName, LibrarianData, picks, MusouSlots.SupportOf(profile, MusouSlots.LibrarianSupportKey)), focusList, picks is null ? MusouCustomizer.LibrarianNote : MusouCustomizer.LibrarianOwnNote);

        _preview.MusouLibrarian = true;
        _preview.MusouLibrarianMelee = picks?.Chosen.Melee is { } melee ? "sm_" + melee : null;   // his own melee choice (stored without the side prefix), else the force sword
        var look = lookSlot is { } slot && profile.ClassAt(slot) is { } c ? profile.Appearance(c.Appearance.For(CustomizationCatalog.SpaceMarine)) : null;   // his own look, else the active loadout's
        _preview.SetSubject(new PreviewSubject(CustomizationCatalog.SpaceMarine, TacticalRole, null, look));
        _preview.SetFraming(Framing.Body);
    }

    void RefreshHeretic(bool focusList)
    {
        _summary.Visible = true;
        _summary.Text = MusouCustomizer.HereticSummary;
        _summary.TooltipText = "";
        StepsPanel.Visible = false;
        _scroll.Visible = true;
        _preview.MusouLibrarian = false;
        _preview.Visible = false;
        MusouPlaceholder().Visible = true;
        if (_cardPanel is not null) _cardPanel.Visible = true;
        _look!.Visible = false;
        _deployButton!.Visible = false;
        var info = MusouCustomizer.Info(MusouCustomizerEntry.Heretic);
        MusouList([new MusouSection(MusouCustomizer.HereticHeading, [new MusouRow(info.Name, MusouCustomizer.HereticText, MusouRowKind.NotYet)])], focusList, null);
    }

    /// <summary>The Heretic's panel in the preview's place (made once).</summary>
    PanelContainer MusouPlaceholder()
    {
        if (_musouPlaceholder is not null) return _musouPlaceholder;
        var box = UiKit.VBox(12);
        box.Alignment = BoxContainer.AlignmentMode.Center;
        box.AddChild(UiKit.Text("HERETIC", UiKit.HeaderSize, UiKit.Grey, HorizontalAlignment.Center));
        box.AddChild(UiKit.Text("NOT YET AVAILABLE", UiKit.BodySize, UiKit.LightRed, HorizontalAlignment.Center));
        _musouPlaceholder = UiKit.Panel(box, 24);
        _musouPlaceholder.Name = "MusouHereticPlaceholder";
        _musouPlaceholder.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _musouPlaceholder.SizeFlagsVertical = SizeFlags.ExpandFill;
        var column = (Control)_preview.GetParent();
        column.AddChild(_musouPlaceholder);
        column.MoveChild(_musouPlaceholder, _preview.GetIndex());
        return _musouPlaceholder;
    }

    /// <summary>The rows of a page, with the creator's own row look and keyboard order (the card shows the focused row's text).</summary>
    void MusouList(IReadOnlyList<MusouSection> sections, bool focusList, string? note)
    {
        int keep = -1;
        if (GetViewport()?.GuiGetFocusOwner() is Button focusedButton) keep = _list.GetChildren().OfType<Button>().ToList().IndexOf(focusedButton);
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        var rows = new List<Button>();
        foreach (var section in sections)
        {
            string heading = section.Heading;
            _list.AddChild(UiKit.Text(heading, UiKit.CaptionSize, UiKit.Gold).With(l => { l.CustomMinimumSize = new Vector2(0, 30); l.ClipText = true; l.TooltipText = heading; }));
            foreach (var r in section.Rows)
            {
                var item = r;
                bool real = item.Kind != MusouRowKind.NotYet;
                UiKit.Glyph.Kind? glyph = item.Kind switch
                {
                    MusouRowKind.Fixed => UiKit.Glyph.Kind.Fixed, MusouRowKind.CarriedOver => UiKit.Glyph.Kind.Check,
                    MusouRowKind.Choice => item.Selected ? UiKit.Glyph.Kind.Check : null, _ => UiKit.Glyph.Kind.Lock,
                };
                bool selected = item.Kind == MusouRowKind.Choice ? item.Selected : real;
                var row = new UiKit.Row(item.Title, item.Tags, selected, real, glyph, real ? null : UiKit.LightRed) { Focused = () => MusouCard(item) };
                row.Pressed += () => MusouRowPressed(item);
                _list.AddChild(row);
                rows.Add(row);
            }
        }
        if (note is not null) _list.AddChild(UiKit.Text(note, UiKit.CaptionSize, UiKit.Grey, HorizontalAlignment.Left, true));
        if (rows.Count == 0) { ShowTextCard("", ""); return; }
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) rows[i].FocusNeighborTop = rows[i].GetPathTo(rows[i - 1]);
            if (i + 1 < rows.Count) rows[i].FocusNeighborBottom = rows[i].GetPathTo(rows[i + 1]);
        }
        int target = Math.Clamp(keep >= 0 && !focusList ? keep : 0, 0, rows.Count - 1);
        if (focusList || keep >= 0)
        {
            rows[target].FocusLater();
            if (target == 0) Callable.From(() => _scroll.ScrollVertical = 0).CallDeferred();
        }
        if (rows[target] is UiKit.Row first) first.Focused?.Invoke();
    }

    void MusouCard(MusouRow row)
    {
        string extra = row.Kind switch
        {
            MusouRowKind.Fixed => "",   // the row says FIXED
            MusouRowKind.CarriedOver => "From your Standard Marines loadout: EDIT LOOK opens that look (shared with the loadout).",
            MusouRowKind.Choice => row.Selected ? "Chosen for the Librarian. SAVE keeps it." : "Choose it for the Librarian.",
            _ => "Not available yet.",
        };
        ShowTextCard(row.Title, row.Description, extra, row.Kind == MusouRowKind.NotYet ? UiKit.LightRed : UiKit.Grey);
    }

    void MusouRowPressed(MusouRow row)
    {
        switch (row.Kind)
        {
            case MusouRowKind.Choice: LibrarianChoose(row); break;
            case MusouRowKind.CarriedOver: OpenAppearance(); break;
            case MusouRowKind.NotYet: Say($"{row.Title}: not available yet.", 4.0); break;
            default: Say($"{row.Title} is fixed for this class."); break;
        }
    }

    /// <summary>A choice row of the Librarian's own loadout pressed: the edit of his class in <see cref="MusouSlots.LibrarianSlot"/> (the session folds it back into his loadout).</summary>
    void LibrarianChoose(MusouRow row)
    {
        if (row.Action is not { } action || action.IndexOf(':') is not (> 0 and var colon)) return;
        string what = action[..colon], arg = action[(colon + 1)..];
        string? name = arg.Length == 0 ? null : arg;
        if (what == "support") { ChooseSupport(row); return; }   // his own key, whatever his loadout holds
        const int slot = MusouSlots.LibrarianSlot;
        if (_session.Working.ClassAt(slot) is not { } current) return;
        CustomizationProfile Put(CustomizationProfile p, CustomClass next) =>
            p with { Classes = p.Classes.Where(x => x.Slot != slot).Append(next).OrderBy(x => x.Slot).ToList() };
        switch (what)
        {
            case "melee": _session.Edit(p => Put(p, current with { Melee = name })); break;
            case "equipment": _session.Edit(p => Put(p, current with { Equipment = name })); break;
            case "perk1" or "perk2":
            {
                var result = LoadoutEdits.SetPerk(_session.Working, slot, what == "perk1" ? 0 : 1, name, _catalog);
                if (!result.Changed) { if (result.Notice is { Length: > 0 } n) Say(n); return; }
                _session.Edit(_ => result.Profile);
                if (result.Notice is { Length: > 0 } notice) Say(notice);
                break;
            }
            case "look" when name == "new":
            {
                // A new look of his own, starting as a copy of the one he wears now (his own or the active loadout's), named after him.
                var profile = _session.Working;
                var worn = LibrarianLookSlot(profile, LoadoutEdits.Active(profile, _catalog, out _)) is { } wornSlot && profile.ClassAt(wornSlot) is { } wearer
                    ? profile.Appearance(wearer.Appearance.For(CustomizationCatalog.SpaceMarine)) : null;
                var (added, id) = AppearanceEdits.AddAppearance(profile, CustomizationCatalog.SpaceMarine);
                if (id is null) { Say($"You can keep {CustomizationProfile.MaxAppearancesPerRace} looks per side. Delete one first."); return; }
                string lookName = MusouSlots.LibrarianName;
                for (int n = 2; added.AppearancesFor(CustomizationCatalog.SpaceMarine).Any(a => a.Id != id && a.Name.Equals(lookName, StringComparison.OrdinalIgnoreCase)); n++) lookName = $"{MusouSlots.LibrarianName} {n}";
                var look = (worn ?? added.Appearance(id)!) with { Id = id, Name = lookName };
                var withLook = AppearanceEdits.Assign(AppearanceEdits.ReplaceAppearance(added, look), slot, CustomizationCatalog.SpaceMarine, id);
                _session.Edit(_ => withLook);
                Say($"New look \"{lookName}\" for the Librarian: EDIT LOOK paints it; SAVE keeps it.", 5.0);
                break;
            }
            case "look": _session.Edit(p => Put(p, current with { Appearance = new AppearanceRefs(name, null) })); break;
        }
    }

    /// <summary>The Classic chrome the Musou pages hide: the class tabs, the slot strip and the slot buttons.</summary>
    void MusouHideChrome()
    {
        _musouChromeHidden = true;
        _tabs.Visible = false;
        _strip.Visible = false;
        foreach (var b in new[] { _rename, _copy, _reset, _clear, _setActive }) if (b is not null) b.Visible = false;
    }

    /// <summary>Everything the Musou pages changed, back as the creator's own refresh expects it (Standard Marines, and Classic in the same front end).</summary>
    void MusouShowChrome()
    {
        if (!_musouChromeHidden) return;
        _musouChromeHidden = false;
        _tabs.Visible = true;
        _strip.Visible = true;
        _summary.Visible = true;
        _summary.TooltipText = "";
        foreach (var b in new[] { _rename, _copy, _reset, _clear, _setActive, _save, _revert, _look }) if (b is not null) b.Visible = true;
        if (_look is not null) _look.TooltipText = "";
        if (_deployButton is not null) _deployButton.TooltipText = "";
        StepsPanel.Visible = true;
        _scroll.Visible = true;
        _preview.Visible = true;
        if (_cardPanel is not null) _cardPanel.Visible = true;
        if (_musouPlaceholder is not null) _musouPlaceholder.Visible = false;
        for (int i = 0; i < _stepButtons.Count && i < StepNames.Length; i++) _stepButtons[i].Text = $"{i + 1}  {StepNames[i]}";
        if (_appearanceStepButton is not null) _appearanceStepButton.Text = "Appearance...";
    }

    // ---- the actions ----

    partial void ExtrasOpenAppearance(ref bool handled)
    {
        if (!MusouOwnPage) return;
        handled = true;
        if (_musouEntry != MusouCustomizerEntry.Librarian) return;
        var profile = _session.Working;
        if (LibrarianLookSlot(profile, LoadoutEdits.Active(profile, _catalog, out _)) is not { } slot) { Say(MusouCustomizer.DefaultsLookNote, 6.0); return; }
        _open = Selection.CustomSlot(slot);   // the shells open the look of the creator's open loadout: the one he wears
        _onAppearance();
    }

    partial void ExtrasDeploy(ref bool handled)
    {
        if (!MusouOwnPage) return;
        handled = true;
        if (_musouEntry != MusouCustomizerEntry.Librarian) return;
        if (MusouLibrarianDeploy is not { } deploy) { Say("Pick the Librarian in the Stormbird: Esc, PLAY AS LIBRARIAN.", 6.0); return; }
        if (deploy() is { Length: > 0 } problem) Say(problem, 8.0);
    }
}
