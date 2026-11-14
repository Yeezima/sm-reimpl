using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>sound/tuning-tool: the owner's mixer panel (<see cref="SoundTunerPanel"/>): F7 or <c>--sound-tuner</c>; the owner's saved overrides load when the match's sound starts.</summary>
public partial class NetworkView
{
    static readonly bool SoundTunerArg = OS.GetCmdlineUserArgs().Contains(SoundTunerPanel.ArgName);
    SoundTunerPanel? _tuner;

    /// <summary>Loads the tuning file (once per run) and makes the panel; called when the sound system starts.</summary>
    void SetUpSoundTuner()
    {
        string repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        SoundTunerPanel.EnsureLoaded(repo);
        if (_tuner is not null) return;
        AddChild(_tuner = new SoundTunerPanel(MusouActive));
        if (SoundTunerArg) _tuner.SetOpen(true);
    }

    /// <summary>F7 shows or hides the panel (not for a bot).</summary>
    bool SoundTunerInput(InputEvent e)
    {
        if (_tuner is null || e is not InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F7 }) return false;
        _tuner.Toggle();
        return true;
    }
}
