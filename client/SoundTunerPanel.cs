using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// sound/tuning-tool (owner, 2026-11-14) and sound/mix-expand (owner, 2026-11-17): the owner's in-match mixer. F7 (or <c>--sound-tuner</c>) shows a panel with two tabs.
/// <b>Sounds</b> (the per-sound editor): a group dropdown (Weapons, Player events, Combat moves, Equipment, Voices and announcer, Ambience and map, UI sounds, Other;
/// <see cref="SoundMixCatalog"/>), a searchable list of the sounds in it with readable labels, a side switch (your own sounds or other players'), and for the selected sound its volume,
/// pitch and hearing distance, mute, solo, raw (no filters or effects of ours: <see cref="SoundMixEffect"/>), PLAY (a preview) and RESET; the global RAW switch; a count of the changes
/// against the shipped defaults; and the live list, the last sounds that fired with their source and distance (<see cref="SoundMixLive"/>), where a click selects the sound in the editor.
/// <b>Groups</b> (the older tab): a volume (dB) and an optional own distance fall-off per class of sound (<see cref="SoundTuningGroups"/>) and one reverb scale.
/// Every change applies at once (<see cref="SoundMixConfig.Live"/> and <see cref="SoundTuning.Live"/>, read by <see cref="SoundSystem"/>). SAVE keeps the changes in
/// <c>%LOCALAPPDATA%\sm-reimpl\sound-mix.json</c> and <c>sound-tuning.json</c> (loaded at the next start; never the install, never the repo); EXPORT writes the whole mix, the Groups tab
/// included, to <c>%LOCALAPPDATA%\sm-reimpl\sound-configs\&lt;timestamp&gt;.json</c> and shows the path (the lead commits one as <c>config/sound-mix-defaults.json</c>, which a release ships
/// as the defaults); IMPORT loads such a file. Client only; all values ours.
/// </summary>
public partial class SoundTunerPanel : CanvasLayer
{
    public const string ArgName = "--sound-tuner";

    static SoundTuning _defaults = new();
    static SoundMixConfig _mixDefaults = new();
    static bool _loaded;
    static string _loadNote = "";

    /// <summary>The places a shipped defaults file is read from: beside the exe (<c>assets/&lt;file&gt;</c>, where a release puts it), then the repository's <c>config/</c>.</summary>
    public static IEnumerable<string> ShippedPaths(string repoRoot, string fileName)
    {
        string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        yield return Path.Combine(exeDir, "assets", fileName);
        yield return Path.Combine(repoRoot, "config", fileName);
    }

    /// <summary>
    /// Loads the shipped defaults (<c>sound-tuning-defaults.json</c> and <c>sound-mix-defaults.json</c>; the mix file's tuning section sits on top of the first) and the owner's saved overrides once
    /// per run into <see cref="SoundTuning.Live"/> and <see cref="SoundMixConfig.Live"/>. No file anywhere: the game's own sound, as before.
    /// </summary>
    public static void EnsureLoaded(string repoRoot)
    {
        if (_loaded) return;
        _loaded = true;
        string? legacy = ShippedPaths(repoRoot, SoundTuningFile.DefaultsFileName).FirstOrDefault(File.Exists);
        var (tuningDefaults, _, tuningNote) = SoundTuningFile.Load(legacy, null);
        var (mixDefaults, mix, mixNote) = SoundMixFile.Load(ShippedPaths(repoRoot, SoundMixFile.DefaultsFileName), SoundMixFile.OverridesPath(), tuningDefaults);
        var tuning = tuningDefaults.Clone();
        string overrides = SoundTuningFile.OverridesPath();
        string userNote = "";
        try { if (File.Exists(overrides)) userNote = SoundTuningFile.TryApply(File.ReadAllText(overrides), tuning) ? " + your group changes" : "; your group file is not valid JSON (ignored)"; }
        catch (IOException) { userNote = "; your group file could not be read"; }
        _defaults = tuningDefaults;
        SoundTuning.Live = tuning;
        _mixDefaults = mixDefaults;
        SoundMixConfig.Live = mix;
        _loadNote = string.Join("; ", new[] { tuningNote == "built-in defaults" ? "" : tuningNote, mixNote }.Where(n => n.Length > 0)) + userNote;
        if (!tuning.IsDefault || !mix.IsEmpty) GD.Print($"sound mix: {(_loadNote.Length > 0 ? _loadNote : "your changes")}");
    }

    readonly Func<bool> _musou;
    readonly SoundSystem? _sound;
    readonly Action<string, bool>? _preview;
    readonly List<Control> _musouRows = new();
    Input.MouseModeEnum _previousMouse = Input.MouseModeEnum.Captured;
    readonly List<Action> _refresh = new();
    Label? _status, _diff, _selTitle, _selInfo;
    OptionButton? _groupBox;
    LineEdit? _search;
    ItemList? _list, _live;
    VBoxContainer? _editor;
    Godot.Button? _ownBtn, _othersBtn, _playBtn;
    CheckBox? _mute, _solo, _rawOne;
    CheckButton? _rawAll;
    FileDialog? _importDialog;
    List<SoundMixInfo> _items = new();
    List<SoundMixLive.Row> _liveRows = new();
    bool _own = true, _building;
    string _sel = "";
    double _liveAt;

    SoundMixConfig Cfg => SoundMixConfig.Live;

    public SoundTunerPanel(Func<bool> musou, SoundSystem? sound = null, Action<string, bool>? preview = null)
    {
        _musou = musou;
        _sound = sound;
        _preview = preview;
        Layer = 95;
        Name = "SoundTuner";
        Visible = false;
    }

    public override void _Ready()
    {
        var panel = new PanelContainer { Name = "Panel", MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.04f, 0.045f, 0.06f, 0.94f), BorderColor = new Color(0.85f, 0.7f, 0.25f), BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10 });
        panel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        panel.OffsetLeft = 12; panel.OffsetTop = 12; panel.OffsetBottom = -12; panel.OffsetRight = 700;
        AddChild(panel);
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        panel.AddChild(outer);

        outer.AddChild(new Label { Text = "SOUND MIX   (F7 or Esc closes)  -  changes are live", Modulate = new Color(0.95f, 0.8f, 0.3f) });
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        outer.AddChild(tabs);
        var soundsTab = new VBoxContainer { Name = "Sounds" };
        soundsTab.AddThemeConstantOverride("separation", 4);
        tabs.AddChild(soundsTab);
        BuildSoundsTab(soundsTab);
        var groupsTab = new VBoxContainer { Name = "Groups" };
        tabs.AddChild(groupsTab);
        BuildGroupsTab(groupsTab);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        outer.AddChild(buttons);
        Btn("SAVE", Save, buttons, "Keep these changes for the next start (sound-mix.json and sound-tuning.json in the sm-reimpl folder).");
        Btn("RESET ALL", ResetAll, buttons, "Every sound and group back to the shipped defaults (not saved until SAVE).");
        Btn("EXPORT", Export, buttons, "Write the whole mix to sound-configs\\<timestamp>.json in the sm-reimpl folder; the path is shown below.");
        Btn("IMPORT", Import, buttons, "Load a sound-mix file (an export or a defaults file) in place of the current mix.");
        Btn("OPEN FOLDER", OpenFolder, buttons, "Show the exports folder.");
        Btn("CLOSE", () => SetOpen(false), buttons);
        _status = new Label { Text = _loadNote.Length > 0 ? "Loaded: " + _loadNote : "Loaded: the game's own sound (no defaults file, no saved changes)", Modulate = new Color(0.75f, 0.75f, 0.75f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        outer.AddChild(_status);
    }

    // ---- the Sounds tab ----

    void BuildSoundsTab(VBoxContainer tab)
    {
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 8);
        tab.AddChild(top);
        top.AddChild(new Label { Text = "Group" });
        _groupBox = new OptionButton { CustomMinimumSize = new Vector2(220, 0), TooltipText = "Weapons, player events, combat moves, equipment, voices, ambience, interface: every sound the client can play is in one group." };
        foreach (var g in SoundMixCatalog.Groups) _groupBox.AddItem(SoundMixCatalog.GroupLabel(g), (int)g);
        _groupBox.ItemSelected += _ => RebuildList();
        top.AddChild(_groupBox);
        top.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _rawAll = new CheckButton { Text = "RAW (all sounds)", TooltipText = "Play every sound as authored: no filters, effects, mix passes or randomisation of ours or the data's. The docs list exactly what it skips." };
        _rawAll.Toggled += on => { Cfg.GlobalRaw = on; UpdateDiff(); };
        top.AddChild(_rawAll);

        var side = new HBoxContainer();
        side.AddThemeConstantOverride("separation", 6);
        tab.AddChild(side);
        var sideGroup = new ButtonGroup();
        _ownBtn = new Godot.Button { Text = "YOUR sounds", ToggleMode = true, ButtonGroup = sideGroup, ButtonPressed = true, TooltipText = "What you do and hear from yourself (your own gun, steps, hit confirms)." };
        _othersBtn = new Godot.Button { Text = "OTHERS' sounds", ToggleMode = true, ButtonGroup = sideGroup, TooltipText = "Other players, projectiles and the world (the map, pickups, the match)." };
        _ownBtn.Pressed += () => SetSide(true);
        _othersBtn.Pressed += () => SetSide(false);
        side.AddChild(_ownBtn); side.AddChild(_othersBtn);
        _search = new LineEdit { PlaceholderText = "search (label or event name)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => RebuildList();
        side.AddChild(_search);

        _list = new ItemList { CustomMinimumSize = new Vector2(0, 190), SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectMode = ItemList.SelectModeEnum.Single };
        _list.ItemSelected += i => { if (!_building && i >= 0 && i < _items.Count) Select(_items[(int)i].Event); };
        tab.AddChild(_list);

        _selTitle = new Label { Text = "", Modulate = new Color(0.95f, 0.8f, 0.3f), ClipText = true };
        tab.AddChild(_selTitle);
        _editor = new VBoxContainer();
        tab.AddChild(_editor);
        _selInfo = new Label { Modulate = new Color(0.7f, 0.7f, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _editor.AddChild(_selInfo);
        Slider(_editor, "Volume", SoundOverride.MinVolumeDb, SoundOverride.MaxVolumeDb, 0.5f, () => Cfg.Get(_sel, _own).VolumeDb, v => Edit(o => o with { VolumeDb = v }), v => $"{v:+0.0;-0.0;0.0} dB", () => 0f, "Added to the sound's level.");
        Slider(_editor, "Pitch", SoundOverride.MinPitch, SoundOverride.MaxPitch, 0.25f, () => Cfg.Get(_sel, _own).PitchSemitones, v => Edit(o => o with { PitchSemitones = v }), v => $"{v:+0.00;-0.00;0.00} st", () => 0f, "Semitones; playback speed scales by 2^(n/12).");
        Slider(_editor, "Hearing distance", SoundOverride.MinScale, SoundOverride.MaxScale, 0.05f, () => Cfg.Get(_sel, _own).DistanceScale, v => Edit(o => o with { DistanceScale = v }), v => $"x{v:0.00}", () => 1f, "How far the sound carries: x2 is heard twice as far as the game's own curve, x0.5 half as far. Own sounds sit at the player, so it barely matters for them.");
        var flags = new HBoxContainer();
        flags.AddThemeConstantOverride("separation", 10);
        _editor.AddChild(flags);
        _mute = new CheckBox { Text = "Mute" };
        _mute.Toggled += on => Edit(o => o with { Mute = on });
        _solo = new CheckBox { Text = "Solo", TooltipText = "Hear only soloed sounds (not saved or exported)." };
        _solo.Toggled += on => { if (_sel.Length > 0) { Cfg.SetSolo(_sel, _own, on); MarkSelected(); UpdateDiff(); } };
        _rawOne = new CheckBox { Text = "Raw", TooltipText = "This sound plays as authored: no filters, effects or processing of ours." };
        _rawOne.Toggled += on => Edit(o => o with { Raw = on });
        _playBtn = new Godot.Button { Text = "PLAY", TooltipText = "Play this sound now (as the side selected) to hear your changes." };
        _playBtn.Pressed += () => { if (_sel.Length > 0) { _preview?.Invoke(_sel, _own); } };
        var reset = new Godot.Button { Text = "RESET SOUND", TooltipText = "This sound back to the shipped default." };
        reset.Pressed += ResetSelected;
        flags.AddChild(_mute); flags.AddChild(_solo); flags.AddChild(_rawOne); flags.AddChild(_playBtn); flags.AddChild(reset);
        _refresh.Add(UpdateEditor);

        _diff = new Label { Modulate = new Color(0.55f, 0.8f, 1f) };
        tab.AddChild(_diff);
        tab.AddChild(new Label { Text = "LIVE: the last sounds heard (click one to adjust it; the list holds still while the mouse is over it)", Modulate = new Color(0.55f, 0.8f, 1f) });
        _live = new ItemList { CustomMinimumSize = new Vector2(0, 150), SelectMode = ItemList.SelectModeEnum.Single };
        _live.ItemSelected += i => { if (i >= 0 && i < _liveRows.Count) SelectFromLive(_liveRows[(int)i]); };
        tab.AddChild(_live);
        UpdateEditor();
    }

    void SetSide(bool own)
    {
        if (_own == own) return;
        _own = own;
        RebuildList();
    }

    SoundMixGroup CurrentGroup => _groupBox is null ? SoundMixGroup.Weapons : (SoundMixGroup)_groupBox.GetItemId(_groupBox.Selected);

    void RebuildList()
    {
        if (_list is null) return;
        _building = true;
        _list.Clear();
        string[] words = (_search?.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extra = Cfg.Entries().Select(e => e.Event).Concat(_mixDefaults.Entries().Select(e => e.Event));
        _items = SoundMixCatalog.InGroup(CurrentGroup, extra)
            .Where(i => words.All(w => i.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Event.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        int selected = -1;
        for (int i = 0; i < _items.Count; i++)
        {
            _list.AddItem(RowText(_items[i]));
            if (string.Equals(_items[i].Event, _sel, StringComparison.OrdinalIgnoreCase)) selected = i;
        }
        if (selected >= 0) { _list.Select(selected); _list.EnsureCurrentIsVisible(); }
        _building = false;
        UpdateEditor();
    }

    string RowText(SoundMixInfo info)
    {
        var o = Cfg.Get(info.Event, _own);
        string flags = (o.IsDefault ? "" : "*") + (o.Mute ? "M" : "") + (Cfg.IsSolo(info.Event, _own) ? "S" : "") + (o.Raw ? "R" : "");
        return (flags.Length > 0 ? $"[{flags}] " : "") + info.Label;
    }

    void Select(string evt)
    {
        _sel = evt;
        UpdateEditor();
    }

    void SelectFromLive(SoundMixLive.Row row)
    {
        if (_groupBox is null || _search is null) return;
        _own = row.Source == SoundSource.Own;
        _ownBtn?.SetPressedNoSignal(_own);
        _othersBtn?.SetPressedNoSignal(!_own);
        _sel = row.Event;
        var group = SoundMixCatalog.Classify(row.Event).Group;
        _groupBox.Select(Array.IndexOf(SoundMixCatalog.Groups.ToArray(), group));
        _search.Text = "";
        SoundMixCatalog.Register(row.Event);
        RebuildList();
    }

    void MarkSelected()
    {
        if (_list is null || _sel.Length == 0) return;
        for (int i = 0; i < _items.Count && i < _list.ItemCount; i++)
            if (string.Equals(_items[i].Event, _sel, StringComparison.OrdinalIgnoreCase)) { _list.SetItemText(i, RowText(_items[i])); return; }
    }

    void Edit(Func<SoundOverride, SoundOverride> change)
    {
        if (_sel.Length == 0 || _building) return;
        Cfg.Set(_sel, _own, change(Cfg.Get(_sel, _own)));
        MarkSelected();
        UpdateDiff();
    }

    void ResetSelected()
    {
        if (_sel.Length == 0) return;
        Cfg.Reset(_sel, _own);
        var shipped = _mixDefaults.Get(_sel, _own);
        if (!shipped.IsDefault) Cfg.Set(_sel, _own, shipped);
        MarkSelected();
        UpdateEditor();
        UpdateDiff();
    }

    void UpdateEditor()
    {
        if (_editor is null || _selTitle is null) return;
        bool any = _sel.Length > 0;
        _editor.Visible = any;
        if (!any) { _selTitle.Text = "Select a sound from the list, or click one in the live list below."; UpdateDiff(); return; }
        var info = SoundMixCatalog.Classify(_sel);
        _selTitle.Text = $"{info.Label}   [{SoundMixCatalog.GroupLabel(info.Group)}, {(_own ? "your own" : "others'")}]   {_sel}";
        float? reach = _sound?.AttenuationOf(SoundMixCatalog.IsAlias(_sel) ? "play_evade_start" : _sel) is { MaxDistance: > 0f } att ? att.MaxDistance : null;
        bool playable = SoundMixCatalog.IsAlias(_sel) || _sel.StartsWith("play_", StringComparison.OrdinalIgnoreCase) || _sel.StartsWith("Play_", StringComparison.Ordinal);
        if (_selInfo is not null) _selInfo.Text = (reach is float r ? $"The game's own hearing distance: {r:0} m. " : "") + (playable ? "" : "This one cannot be previewed (a stop, a state change or a voice line). ");
        if (_playBtn is not null) _playBtn.Disabled = !playable || _preview is null;
        var o = Cfg.Get(_sel, _own);
        _building = true;
        _mute?.SetPressedNoSignal(o.Mute);
        _solo?.SetPressedNoSignal(Cfg.IsSolo(_sel, _own));
        _rawOne?.SetPressedNoSignal(o.Raw);
        _rawAll?.SetPressedNoSignal(Cfg.GlobalRaw);
        _building = false;
        foreach (var r2 in _refresh) if (r2 != (Action)UpdateEditor) r2();
        UpdateDiff();
    }

    void UpdateDiff()
    {
        if (_diff is null) return;
        int n = SoundMixFile.Describe(_mixDefaults, _defaults, Cfg, SoundTuning.Live).Count;
        _diff.Text = n == 0 ? "No changes from the shipped defaults." : $"{n} change{(n == 1 ? "" : "s")} from the shipped defaults ({Cfg.Count} sound override{(Cfg.Count == 1 ? "" : "s")}{(Cfg.GlobalRaw ? ", global raw on" : "")}{(Cfg.AnySolo ? ", solo active" : "")}).";
    }

    void RefreshLive()
    {
        if (_live is null) return;
        if (_live.GetGlobalRect().HasPoint(_live.GetGlobalMousePosition())) return;   // holds still under the mouse so a click lands on the row that was seen
        RebuildLive();
    }

    void RebuildLive()
    {
        if (_live is null) return;
        _liveRows = SoundMixLive.Snapshot();
        double now = _sound?.Now ?? 0;
        _live.Clear();
        foreach (var r in _liveRows)
        {
            string who = r.Source switch { SoundSource.Own => "YOUR ", SoundSource.Other => "OTHER", _ => "WORLD" };
            _live.AddItem($"{Math.Max(0, now - r.At),5:0.0}s  {who}  {r.Distance,5:0.0} m  {(r.Count > 1 ? "x" + r.Count : "  ")}  {SoundMixCatalog.Classify(r.Event).Label}", null, true);
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _liveAt -= delta;
        if (_liveAt > 0) return;
        _liveAt = 0.25;
        RefreshLive();
    }

    // ---- the Groups tab (the older per-class tuner) ----

    void BuildGroupsTab(VBoxContainer tab)
    {
        tab.AddChild(new Label { Text = "A group's own distance fall-off replaces the game's distance curve and the Classic soundscape's ranging for that group. A sound's own settings (Sounds tab) apply on top.", Modulate = new Color(0.7f, 0.7f, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tab.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(list);

        Slider(list, "Reverb send (map)", 0f, 3f, 0.05f, () => SoundTuning.Live.ReverbScale, v => { SoundTuning.Live.ReverbScale = SoundTuning.ClampReverb(v); UpdateDiff(); }, v => $"x{v:0.00}", () => _defaults.ReverbScale);
        string section = "";
        for (int i = 0; i < SoundTuningGroups.All.Count; i++)
        {
            var group = SoundTuningGroups.All[i];
            if (group.Section != section)
            {
                section = group.Section;
                var header = new Label { Text = section.ToUpperInvariant(), Modulate = new Color(0.55f, 0.8f, 1f) };
                list.AddChild(header);
                if (group.MusouOnly) _musouRows.Add(header);
            }
            var box = AddGroup(list, i, group);
            if (group.MusouOnly) _musouRows.Add(box);
        }
    }

    VBoxContainer AddGroup(VBoxContainer list, int index, SoundTuningGroup group)
    {
        var box = new VBoxContainer();
        list.AddChild(box);
        Slider(box, group.Label, GroupTuning.MinVolumeDb, GroupTuning.MaxVolumeDb, 0.5f,
            () => SoundTuning.Live[index].VolumeDb,
            v => { SoundTuning.Live[index] = SoundTuning.Live[index] with { VolumeDb = v }; UpdateDiff(); },
            v => $"{v:+0.0;-0.0;0.0} dB", () => _defaults[index].VolumeDb, group.Help);
        if (index == 0) return box;   // the master has a volume only: a fall-off on "everything" would override every group's
        var fall = new VBoxContainer { Visible = SoundTuning.Live[index].CustomFalloff };
        var check = new CheckBox { Text = "own distance fall-off", ButtonPressed = SoundTuning.Live[index].CustomFalloff, TooltipText = "Off: the game's own distance curve. On: full level within the minimum distance, then the roll-off, silent past the maximum." };
        check.Toggled += on => { SoundTuning.Live[index] = SoundTuning.Live[index] with { CustomFalloff = on }; fall.Visible = on; UpdateDiff(); };
        _refresh.Add(() => { check.SetPressedNoSignal(SoundTuning.Live[index].CustomFalloff); fall.Visible = SoundTuning.Live[index].CustomFalloff; });
        var row = new HBoxContainer();
        row.AddChild(new Control { CustomMinimumSize = new Vector2(24, 0) });
        row.AddChild(check);
        box.AddChild(row);
        box.AddChild(fall);
        Slider(fall, "   full level within", 0.5f, 100f, 0.5f, () => SoundTuning.Live[index].MinDistance, v => SoundTuning.Live[index] = SoundTuning.Live[index] with { MinDistance = v }, v => $"{v:0.0} m", () => _defaults[index].MinDistance);
        Slider(fall, "   roll-off", 0f, 24f, 0.5f, () => SoundTuning.Live[index].RolloffDb, v => SoundTuning.Live[index] = SoundTuning.Live[index] with { RolloffDb = v }, v => $"{v:0.0} dB/x2", () => _defaults[index].RolloffDb);
        Slider(fall, "   silent beyond", 5f, 300f, 1f, () => SoundTuning.Live[index].MaxDistance, v => SoundTuning.Live[index] = SoundTuning.Live[index] with { MaxDistance = v }, v => $"{v:0} m", () => _defaults[index].MaxDistance);
        return box;
    }

    // ---- shared helpers ----

    void Slider(VBoxContainer parent, string label, float min, float max, float step, Func<float> get, Action<float> set, Func<float, string> format, Func<float> reset, string? tip = null)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var name = new Label { Text = label, CustomMinimumSize = new Vector2(150, 0), ClipText = true, TooltipText = tip ?? "", MouseFilter = Control.MouseFilterEnum.Pass };
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = get(), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(150, 18), TooltipText = tip ?? "" };
        var value = new Label { Text = format(get()), CustomMinimumSize = new Vector2(78, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var zero = new Godot.Button { Text = "0", CustomMinimumSize = new Vector2(26, 0), TooltipText = "back to the default" };
        slider.ValueChanged += v => { if (!_building) set((float)v); value.Text = format((float)v); };
        zero.Pressed += () => { float d = reset(); set(d); slider.SetValueNoSignal(d); value.Text = format(d); };
        _refresh.Add(() => { float v = get(); slider.SetValueNoSignal(v); value.Text = format(v); });
        row.AddChild(name); row.AddChild(slider); row.AddChild(value); row.AddChild(zero);
        parent.AddChild(row);
    }

    static void Btn(string text, Action pressed, Container into, string? tip = null)
    {
        var b = new Godot.Button { Text = text, TooltipText = tip ?? "" };
        b.Pressed += pressed;
        into.AddChild(b);
    }

    void Refresh() { foreach (var r in _refresh) r(); }

    void Status(string text) { if (_status is not null) _status.Text = text; }

    // ---- save, reset, export, import ----

    void Save()
    {
        try
        {
            SoundTuningFile.SaveOverrides(SoundTuningFile.OverridesPath(), SoundTuning.Live, _defaults);
            SoundMixFile.Write(SoundMixFile.OverridesPath(), Cfg, null, _mixDefaults);   // only his differences from the shipped defaults, so later shipped defaults still reach him
            Status(@"Saved to %LOCALAPPDATA%\sm-reimpl\ (" + SoundMixFile.OverridesFileName + " and " + SoundTuningFile.OverridesFileName + "); loaded at the next start.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("Could not save: " + ex.Message); }
    }

    void ResetAll()
    {
        SoundTuning.Live.CopyFrom(_defaults);
        Cfg.CopyFrom(_mixDefaults);
        _rawAll?.SetPressedNoSignal(Cfg.GlobalRaw);
        Refresh();
        RebuildList();
        Status("Every sound and group back to the shipped defaults (not saved until SAVE).");
    }

    void Export()
    {
        try
        {
            string path = SoundMixFile.NewExportPath(DateTime.UtcNow);
            SoundMixFile.Write(path, Cfg, SoundTuning.Live);
            Status($"Exported {SoundMixFile.Describe(new SoundMixConfig(), new SoundTuning(), Cfg, SoundTuning.Live).Count} change(s) to {path}. The lead ships one as config/{SoundMixFile.DefaultsFileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("Could not export: " + ex.Message); }
    }

    void Import()
    {
        if (_importDialog is null)
        {
            _importDialog = new FileDialog { Title = "Import a sound mix file", FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = false, Filters = ["*.json ; Sound mix files"] };
            _importDialog.FileSelected += ImportFrom;
            AddChild(_importDialog);
        }
        string dir = SoundMixFile.ExportDirectory();
        try { Directory.CreateDirectory(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _importDialog.CurrentDir = dir;
        _importDialog.PopupCentered(new Vector2I(760, 460));
    }

    /// <summary>Replaces the current mix with a file's (its Groups-tab section too, on top of the shipped group defaults).</summary>
    public void ImportFrom(string path)
    {
        try
        {
            var mix = new SoundMixConfig();
            var tuning = _defaults.Clone();
            if (!SoundMixFile.TryApply(File.ReadAllText(path), mix, tuning)) { Status("Not a sound mix file: " + path); return; }
            Cfg.CopyFrom(mix);
            SoundTuning.Live.CopyFrom(tuning);
            _rawAll?.SetPressedNoSignal(Cfg.GlobalRaw);
            Refresh();
            RebuildList();
            Status($"Imported {Cfg.Count} sound override(s) from {path} (not saved until SAVE).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("Could not import: " + ex.Message); }
    }

    static void OpenFolder()
    {
        string dir = SoundMixFile.ExportDirectory();
        try { Directory.CreateDirectory(dir); OS.ShellOpen(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public bool IsOpen => Visible;

    /// <summary>Shows or hides the panel; the mouse is free while it is open and captured again when it closes.</summary>
    public void SetOpen(bool open)
    {
        if (open == Visible) return;
        Visible = open;
        SoundMixLive.Enabled = open;
        if (open)
        {
            bool musou = _musou();
            foreach (var row in _musouRows) row.Visible = musou;   // Musou Mode's groups show only under Musou Mode (checked at every opening)
            if (_sound is not null) SoundMixCatalog.Register(_sound.ByEvent.Keys);
            if (_groupBox is not null)
                foreach (var g in SoundMixCatalog.Groups)
                    _groupBox.SetItemText((int)g, $"{SoundMixCatalog.GroupLabel(g)} ({SoundMixCatalog.InGroup(g, Cfg.Entries().Select(e => e.Event)).Count})");
            Refresh();
            RebuildList();
            _liveAt = 0;
            _previousMouse = Input.MouseMode;
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else Input.MouseMode = _previousMouse;   // what it was before: a free mouse stays free
    }

    public void Toggle() => SetOpen(!Visible);
}
