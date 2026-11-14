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
        AddChild(_tuner = new SoundTunerPanel(() => MusouActive));
        if (SoundTunerArg) _tuner.SetOpen(true);
    }

    /// <summary>The panel is up: the game takes no input (<see cref="PickerBusy"/> includes it) and the mouse is free.</summary>
    bool TunerOpen => _tuner?.IsOpen == true;

    /// <summary>
    /// F7 shows or hides the panel; Esc closes it. It does not open over the Esc menu, the customizer, the spawn choice or a Musou modal (<see cref="PickerBusy"/>), or while dead.
    /// While it is open every key and click that no slider took is swallowed here, so a click beside the panel cannot recapture the mouse.
    /// </summary>
    bool SoundTunerInput(InputEvent e)
    {
        if (_tuner is null) return false;
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F7 })
        {
            if (!_tuner.IsOpen && (PickerBusy || OwnDead)) return true;
            _tuner.Toggle();
            return true;
        }
        if (!_tuner.IsOpen) return false;
        if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }) _tuner.SetOpen(false);
        return e is InputEventKey or InputEventMouse or InputEventJoypadButton or InputEventJoypadMotion;
    }
}
