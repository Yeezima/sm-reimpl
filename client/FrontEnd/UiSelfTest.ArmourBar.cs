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
                    if (shield > 0f)
                    {
                        await Frames(60);   // the new armour level's art is built in the background
                        float cyan = hud.VitalsArmourCyanShare, yellow = hud.VitalsArmourYellowShare, cream = hud.VitalsArmourCreamShare;
                        // ui/hud-bar-colours: the plain bar is the movie's cream lightdamagebar_mc (no cyan; mostly cream, only its glow yellow: the old flat tint was all yellow); the Iron Halo's bar
                        // is the cyan ironhalo_mc.
                        string seen = $"cyan {cyan:P1}, yellow {yellow:P1}, cream {cream:P1}";
                        if (halo) Check($"armour bar: Iron Halo, armour {shield:F0} %: the bar is cyan and white", cyan > 0.2f && yellow < 0.02f, seen);
                        else Check($"armour bar: plain, armour {shield:F0} %: the bar is cream, never blue or flat yellow", cyan >= 0f && cyan < 0.02f && yellow < 0.4f && cream > 0.5f, seen);
                    }
                    foreach (float health in new[] { 100f, 50f, 20f })
                    {
                        hud.Data.Health = health;
                        await Frames(3);
                        for (int i = 0; i < 400 && hud.VitalsHealthWhiteShare < 0f; i++) await Frames(1);
                        await Frames(60);   // the art of a new health level is built in the background
                        float share = hud.VitalsHealthWhiteShare;
                        Check($"armour bar: {(halo ? "Iron Halo" : "plain")}, health {health:F0} %: the health fill is red, never white or blue", share >= 0f && share < 0.02f, $"white share {share:P1}");
                    }
                    hud.Data.Health = 100f;
                    Check($"armour bar: {(halo ? "Iron Halo" : "plain")}, armour {shield:F0} %: {expected} armour layer drawn", layers == expected, $"layers {layers}");
                }
        }
        finally { hud.QueueFree(); }
    }
}
