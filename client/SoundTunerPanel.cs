using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// sound/tuning-tool (owner, 2026-11-14): the owner's in-match mixer. F7 (or <c>--sound-tuner</c>) shows a panel of sliders grouped logically (<see cref="SoundTuningGroups"/>): the master, your own
/// actions, other players' guns by class, movement, impacts and blasts, the world, and Musou Mode's own groups under Musou Mode. Each group has a volume (dB) and an optional own distance fall-off
/// (full level within a minimum distance, then dB lost per doubling, silent beyond a maximum); one reverb scale covers the map's reverb send (the mixer has a single shared reverb, no per-voice send).
/// Every change applies at once (<see cref="SoundTuning.Live"/>, read by <see cref="SoundSystem"/> each frame). SAVE keeps the changes in <c>%LOCALAPPDATA%\sm-reimpl\sound-tuning.json</c> (loaded at
/// the next start; never the install, never the repo); EXPORT DEFAULTS writes every group to <c>sound-tuning-defaults.json</c> in the same folder, for the lead to commit as
/// <c>config/sound-tuning-defaults.json</c>. Client only; all values ours.
/// </summary>
public partial class SoundTunerPanel : CanvasLayer
{
    public const string ArgName = "--sound-tuner";

    static SoundTuning _defaults = new();
    static bool _loaded;
    static string _loadNote = "";
    static string _repoDefaultsPath = "";

    /// <summary>The path of the committed defaults file under a repository root.</summary>
    public static string DefaultsPathIn(string repoRoot) => Path.Combine(repoRoot, "config", SoundTuningFile.DefaultsFileName);

    /// <summary>Loads the shipped defaults and the owner's overrides once per run into <see cref="SoundTuning.Live"/>.</summary>
    public static void EnsureLoaded(string repoRoot)
    {
        if (_loaded) return;
        _loaded = true;
        _repoDefaultsPath = DefaultsPathIn(repoRoot);
        var (defaults, current, note) = SoundTuningFile.Load(_repoDefaultsPath, SoundTuningFile.OverridesPath());
        _defaults = defaults;
        SoundTuning.Live = current;
        _loadNote = note;
        if (!current.IsDefault) GD.Print($"sound tuning: {note}");
    }

    readonly bool _musou;
    readonly List<Action> _refresh = new();
    Label? _status;
    Control? _root;

    public SoundTunerPanel(bool musou)
    {
        _musou = musou;
        Layer = 95;
        Name = "SoundTuner";
        Visible = false;
    }

    public override void _Ready()
    {
        var panel = new PanelContainer { Name = "Panel", MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.04f, 0.045f, 0.06f, 0.92f), BorderColor = new Color(0.85f, 0.7f, 0.25f), BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10 });
        panel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        panel.OffsetLeft = 12; panel.OffsetTop = 12; panel.OffsetBottom = -12; panel.OffsetRight = 640;
        AddChild(panel);
        _root = panel;
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        panel.AddChild(outer);

        outer.AddChild(new Label { Text = "SOUND TUNER   (F7 closes)  -  changes are live", Modulate = new Color(0.95f, 0.8f, 0.3f) });
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(list);

        Slider(list, "Reverb send (map)", 0f, 3f, 0.05f, () => SoundTuning.Live.ReverbScale, v => SoundTuning.Live.ReverbScale = SoundTuning.ClampReverb(v), v => $"x{v:0.00}", () => _defaults.ReverbScale);
        string section = "";
        for (int i = 0; i < SoundTuningGroups.All.Count; i++)
        {
            var group = SoundTuningGroups.All[i];
            if (group.MusouOnly && !_musou) continue;
            if (group.Section != section)
            {
                section = group.Section;
                list.AddChild(new Label { Text = section.ToUpperInvariant(), Modulate = new Color(0.55f, 0.8f, 1f) });
            }
            AddGroup(list, i, group);
        }

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        outer.AddChild(buttons);
        Button("SAVE", Save, buttons);
        Button("RESET ALL", ResetAll, buttons);
        Button("EXPORT DEFAULTS", ExportDefaults, buttons);
        Button("CLOSE", () => SetOpen(false), buttons);
        _status = new Label { Text = _loadNote.Length > 0 ? "Loaded: " + _loadNote : "Loaded: built-in defaults", Modulate = new Color(0.75f, 0.75f, 0.75f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        outer.AddChild(_status);
    }

    void AddGroup(VBoxContainer list, int index, SoundTuningGroup group)
    {
        var box = new VBoxContainer();
        list.AddChild(box);
        Slider(box, group.Label, GroupTuning.MinVolumeDb, GroupTuning.MaxVolumeDb, 0.5f,
            () => SoundTuning.Live[index].VolumeDb,
            v => SoundTuning.Live[index] = SoundTuning.Live[index] with { VolumeDb = v },
            v => $"{v:+0.0;-0.0;0.0} dB", () => _defaults[index].VolumeDb, group.Help);
        var fall = new VBoxContainer { Visible = SoundTuning.Live[index].CustomFalloff };
        var check = new CheckBox { Text = "own distance fall-off", ButtonPressed = SoundTuning.Live[index].CustomFalloff, TooltipText = "Off: the game's own distance curve. On: full level within the minimum distance, then the roll-off, silent past the maximum." };
        check.Toggled += on => { SoundTuning.Live[index] = SoundTuning.Live[index] with { CustomFalloff = on }; fall.Visible = on; };
        _refresh.Add(() => { check.SetPressedNoSignal(SoundTuning.Live[index].CustomFalloff); fall.Visible = SoundTuning.Live[index].CustomFalloff; });
        var row = new HBoxContainer();
        row.AddChild(new Control { CustomMinimumSize = new Vector2(24, 0) });
        row.AddChild(check);
        box.AddChild(row);
        box.AddChild(fall);
        Slider(fall, "   full level within", 0.5f, 100f, 0.5f, () => SoundTuning.Live[index].MinDistance, v => SoundTuning.Live[index] = SoundTuning.Live[index] with { MinDistance = v }, v => $"{v:0.0} m", () => _defaults[index].MinDistance);
        Slider(fall, "   roll-off", 0f, 24f, 0.5f, () => SoundTuning.Live[index].RolloffDb, v => SoundTuning.Live[index] = SoundTuning.Live[index] with { RolloffDb = v }, v => $"{v:0.0} dB/x2", () => _defaults[index].RolloffDb);
        Slider(fall, "   silent beyond", 5f, 300f, 1f, () => SoundTuning.Live[index].MaxDistance, v => SoundTuning.Live[index] = SoundTuning.Live[index] with { MaxDistance = v }, v => $"{v:0} m", () => _defaults[index].MaxDistance);
    }

    void Slider(VBoxContainer parent, string label, float min, float max, float step, Func<float> get, Action<float> set, Func<float, string> format, Func<float> reset, string? tip = null)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var name = new Label { Text = label, CustomMinimumSize = new Vector2(250, 0), ClipText = true, TooltipText = tip ?? "", MouseFilter = Control.MouseFilterEnum.Pass };
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = get(), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(150, 18), TooltipText = tip ?? "" };
        var value = new Label { Text = format(get()), CustomMinimumSize = new Vector2(78, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var zero = new Godot.Button { Text = "0", CustomMinimumSize = new Vector2(26, 0), TooltipText = "back to the default" };
        slider.ValueChanged += v => { set((float)v); value.Text = format((float)v); };
        zero.Pressed += () => { float d = reset(); set(d); slider.SetValueNoSignal(d); value.Text = format(d); };
        _refresh.Add(() => { float v = get(); slider.SetValueNoSignal(v); value.Text = format(v); });
        row.AddChild(name); row.AddChild(slider); row.AddChild(value); row.AddChild(zero);
        parent.AddChild(row);
    }

    static void Button(string text, Action pressed, Container into)
    {
        var b = new Godot.Button { Text = text };
        b.Pressed += pressed;
        into.AddChild(b);
    }

    void Refresh() { foreach (var r in _refresh) r(); }

    void Status(string text) { if (_status is not null) _status.Text = text; }

    void Save()
    {
        try { SoundTuningFile.SaveOverrides(SoundTuningFile.OverridesPath(), SoundTuning.Live, _defaults); Status(@"Saved to %LOCALAPPDATA%\sm-reimpl\" + SoundTuningFile.OverridesFileName + " (loaded at the next start)."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("Could not save: " + ex.Message); }
    }

    void ResetAll()
    {
        SoundTuning.Live.CopyFrom(_defaults);
        Refresh();
        Status("All groups back to the defaults (not saved until SAVE).");
    }

    void ExportDefaults()
    {
        string path = Path.Combine(SoundTuningFile.Directory(), SoundTuningFile.DefaultsFileName);
        try { SoundTuningFile.ExportDefaults(path, SoundTuning.Live); Status(@"Exported every group to %LOCALAPPDATA%\sm-reimpl\" + SoundTuningFile.DefaultsFileName + ": the lead commits it as config/" + SoundTuningFile.DefaultsFileName + "."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("Could not export: " + ex.Message); }
    }

    public bool IsOpen => Visible;

    /// <summary>Shows or hides the panel; the mouse is free while it is open and captured again when it closes.</summary>
    public void SetOpen(bool open)
    {
        if (open == Visible) return;
        Visible = open;
        if (open) { Refresh(); Input.MouseMode = Input.MouseModeEnum.Visible; }
        else Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public void Toggle() => SetOpen(!Visible);
}
