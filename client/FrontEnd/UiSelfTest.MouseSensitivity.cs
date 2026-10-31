using System.Threading.Tasks;
using Godot;
using SmClient.Customizer.Model;

namespace SmClient.FrontEnd;

// ui/mouse-sensitivity: the slider in OPTIONS, CONTROLS (the same page in the main menu and the in-match menu), its range, its persistence and its effect on the mouse look scale.
public sealed partial class UiSelfTest
{
    static HSlider? FindSlider(Node root) => root.FindChild("MouseSensitivitySlider", true, false) as HSlider;

    async Task MouseSensitivityChecks()
    {
        float kept = _services.Settings.MouseSensitivity;
        try
        {
            _front.ShowOptions(OptionsView.Controls);
            await Frames(4);
            var join = FindSlider(_front.Options!);
            Check("mouse sensitivity: OPTIONS, CONTROLS has the slider", join is not null && join.IsVisibleInTree());
            if (join is null) return;
            Check("mouse sensitivity: range 0.1 to 3.0, step 0.05", Mathf.IsEqualApprox((float)join.MinValue, 0.1f) && Mathf.IsEqualApprox((float)join.MaxValue, 3f) && Mathf.IsEqualApprox((float)join.Step, 0.05f));
            Check("mouse sensitivity: the default is 1.0x and leaves the look scale as it was", Mathf.IsEqualApprox((float)join.Value, 1f) && Mathf.IsEqualApprox(MouseSensitivity.Look(0.7f), 0.7f), $"value {join.Value}");

            join.Value = 2.0;
            await Frames(2);
            Check("mouse sensitivity: moving the slider applies at once", Mathf.IsEqualApprox(MouseSensitivity.Selected, 2f), MouseSensitivity.Selected.ToString());
            Check("mouse sensitivity: it multiplies the mouse look scale", Mathf.IsEqualApprox(MouseSensitivity.Look(1.5f), 3f) && Mathf.IsEqualApprox(MouseSensitivity.Look(0.5f), 1f));
            Check("mouse sensitivity: it is saved with the other settings", Mathf.IsEqualApprox(FrontEndSettings.Load(_services.SettingsDirectory).MouseSensitivity, 2f));

            join.Value = 0.35;
            await Frames(2);
            Check("mouse sensitivity: the value is shown as a number", ((Label?)_front.Options!.FindChild("MouseSensitivityValue", true, false))?.Text == "0.35x");

            Check("mouse sensitivity: out-of-range values are limited", Mathf.IsEqualApprox(MouseSensitivity.Clamp(9f), 3f) && Mathf.IsEqualApprox(MouseSensitivity.Clamp(0f), 0.1f) && Mathf.IsEqualApprox(MouseSensitivity.Clamp(float.NaN), 1f));
        }
        finally
        {
            _services.UpdateSettings(x => x with { MouseSensitivity = kept });
            _front.ShowMainMenu();
            await Frames(2);
        }
    }
}
