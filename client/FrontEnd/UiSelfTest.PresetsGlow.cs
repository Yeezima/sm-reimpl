using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;

namespace SmClient.FrontEnd;

// ui/lighting-presets and ui/focus-glow (the owner's requests of 2026-11-06): the LIGHTING PRESET row (ten looks, Faithful Remaster first and the default, saved, applied live with
// every value given back on Faithful Remaster) and the one focus glow (on the focused row, following the pad's moves and the mouse, which moves the focus with it).
public sealed partial class UiSelfTest
{
    async Task PresetsGlowChecks()
    {
        var kept = _services.Settings;
        var options = _front.Options!;
        try
        {
            _front.ShowOptions(OptionsView.Graphics);
            await Frames(4);
            var row = FindButton(options, b => b.Text == "LIGHTING PRESET") as UiKit.MenuChoice;
            Check("presets: GRAPHICS has LIGHTING PRESET", row is not null);
            if (row is null) return;
            Check("presets: the ten looks in the owner's order", row.Values.SequenceEqual(LightingPresets.Labels.Select(l => l.ToUpperInvariant())) && row.Values.Count == 10, string.Join(", ", row.Values));
            Check("presets: Faithful Remaster is the default", row.Index == 0 && GameOptions.Default.Lighting == LightingPresetChoice.FaithfulRemaster && LightingPresets.For(LightingPresetChoice.FaithfulRemaster).IsIdentity);

            // The grade on a stand-alone environment and sun, as the match applies it each frame.
            var env = new Godot.Environment { TonemapExposure = 1.25f, AdjustmentEnabled = false, FogDensity = 0.2f, FogLightColor = new Color(0.6f, 0.4f, 0.2f), AmbientLightEnergy = 1f, GlowIntensity = 0.6f };
            var sun = new DirectionalLight3D { LightEnergy = 2f, LightColor = new Color(1f, 0.9f, 0.7f) };
            var grade = new LightingGradeView();
            grade.Frame(env, sun);
            Check("presets: Faithful Remaster touches nothing", env.TonemapExposure == 1.25f && !env.AdjustmentEnabled && sun.LightEnergy == 2f && env.FogLightColor == new Color(0.6f, 0.4f, 0.2f));
            row.Step(1);   // NATURAL CINEMA
            row.Step(1);   // PALE ASHFALL
            await Frames(2);
            Check("presets: a pick reaches the live options", LiveOptions.Current.Lighting == LightingPresetChoice.PaleAshfall);
            Check("presets: a pick is saved", FrontEndSettings.Load(_services.SettingsDirectory).Options.Lighting == LightingPresetChoice.PaleAshfall);
            var g = LightingPresets.For(LightingPresetChoice.PaleAshfall);
            grade.Frame(env, sun);
            grade.Frame(env, sun);   // a second frame must not compound
            Check("presets: the grade applies once, live", Mathf.IsEqualApprox(env.TonemapExposure, 1.25f * g.Exposure) && env.AdjustmentEnabled && Mathf.IsEqualApprox(env.AdjustmentSaturation, g.Saturation)
                  && Mathf.IsEqualApprox(sun.LightEnergy, 2f * g.SunEnergy) && Mathf.IsEqualApprox(env.FogDensity, 0.2f * g.FogDensity), $"exp {env.TonemapExposure} sat {env.AdjustmentSaturation} sun {sun.LightEnergy}");
            env.FogDensity = 0.1f;   // the lighting code's region blender writes a new value
            grade.Frame(env, sun);
            Check("presets: a value the lighting code changes is graded from the new value", Mathf.IsEqualApprox(env.FogDensity, 0.1f * g.FogDensity), env.FogDensity.ToString());
            _services.UpdateSettings(x => x with { Options = x.Options with { Lighting = LightingPresetChoice.FaithfulRemaster } });
            await Frames(2);
            grade.Frame(env, sun);
            Check("presets: Faithful Remaster gives every value back", env.TonemapExposure == 1.25f && !env.AdjustmentEnabled && sun.LightEnergy == 2f && env.FogDensity == 0.1f && env.FogLightColor == new Color(0.6f, 0.4f, 0.2f)
                  && sun.LightColor == new Color(1f, 0.9f, 0.7f), $"exp {env.TonemapExposure} fog {env.FogDensity}");
            sun.Free();

            // The focus glow: on the focused row, sized to its words, following the pad.
            row.GrabFocus();
            await Frames(3);
            Check("glow: the focused row glows", FocusGlow.Target == row, FocusGlow.Target?.Name ?? "none");
            var area = FocusGlow.GlowRect;
            var rowRect = row.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, row.Size);
            Check("glow: it is sized to the words, not the whole row", area.Size.X > 40f && area.Size.X < rowRect.Size.X, $"{area} in {rowRect}");
            await SendKey(Key.Down);
            await Frames(2);
            var focused = GetViewport().GuiGetFocusOwner();
            Check("glow: it follows the focus", focused != row && FocusGlow.Target == focused, focused?.Name ?? "none");

            // The mouse: moving onto a row gives it the focus, so only one row glows.
            // (A headless run's window is small and the menu rows' text is drawn very large, so the pointer goes to a probe button of known place on a layer of its own.)
            var probeLayer = new CanvasLayer { Name = "GlowProbe", Layer = 90 };
            var probe = new Button { Name = "GlowProbeButton", Text = "PROBE", FocusMode = Control.FocusModeEnum.All, Position = new Vector2(40, 40), Size = new Vector2(160, 40) };
            probeLayer.AddChild(probe);
            GetTree().Root.AddChild(probeLayer);
            await Frames(2);
            var centre = probe.GetGlobalTransformWithCanvas() * (probe.Size / 2f);
            GetViewport().PushInput(new InputEventMouseMotion { Position = centre, GlobalPosition = centre }, true);
            await Frames(3);
            Check("glow: the mouse moves the focus and the glow", probe.HasFocus() && FocusGlow.Target == probe,
                  $"focus {GetViewport().GuiGetFocusOwner()?.Name}, glow {FocusGlow.Target?.Name}, hovered {GetViewport().GuiGetHoveredControl()?.Name} at {centre}");
            GetViewport().PushInput(new InputEventMouseMotion { Position = new Vector2(-50, -50), GlobalPosition = new Vector2(-50, -50) }, true);   // the pointer leaves the window: later checks own the focus
            probeLayer.QueueFree();
            await Frames(2);
            Check("glow: a freed row leaves no glow behind", FocusGlow.Target is null || FocusGlow.Target != probe);
            var disabled = new Button { Disabled = true };
            Check("glow: a disabled row never glows", !FocusGlow.Glows(disabled));
            disabled.Free();
        }
        finally
        {
            _services.UpdateSettings(_ => kept);
            await Frames(2);
            _front.ShowMainMenu();
            await Frames(2);
        }
    }
}
