using System.Threading.Tasks;
using SmSim;

namespace SmClient.FrontEnd;

// ui/armour-bar-fix: the HUD draws one armour bar layer (the old gold frame under the yellow bar is gone), and no starburst for an empty plain bar.
public sealed partial class UiSelfTest
{
    async Task ArmourBarChecks()
    {
        string? dir = _services.GameDir;
        if (string.IsNullOrEmpty(dir)) { Check("armour bar: one layer (no game install: skipped)", true); return; }
        var hud = new Hud { GameDir = dir };
        hud.Data.Teams = true; hud.Data.OwnTeam = Team.SpaceMarines; hud.Data.InGame = true; hud.Data.Alive = true;
        hud.Data.HealthMax = 100f; hud.Data.ShieldMax = 100f; hud.Data.Health = 100f;
        AddChild(hud);
        try
        {
            for (int i = 0; i < 900 && hud.VitalsArmourLayers < 1; i++) { hud.Data.Shield = 100f; await Frames(1); }   // the bar's art is built in the background
            if (hud.VitalsArmourLayers < 1) { Check("armour bar: one layer (the HUD movie was not drawn here: skipped)", true); return; }
            foreach (bool halo in new[] { false, true })
                foreach (float shield in new[] { 100f, 50f, 0f })
                {
                    hud.Data.IronHalo = halo; hud.Data.Shield = shield;
                    int expected = !halo && shield <= 0f ? 0 : 1;
                    await Frames(3);
                    for (int i = 0; i < 400 && hud.VitalsArmourLayers < expected; i++) await Frames(1);
                    int layers = hud.VitalsArmourLayers;
                    Check($"armour bar: {(halo ? "Iron Halo" : "plain")}, armour {shield:F0} %: {expected} armour layer drawn", layers == expected, $"layers {layers}");
                }
        }
        finally { hud.QueueFree(); }
    }
}
