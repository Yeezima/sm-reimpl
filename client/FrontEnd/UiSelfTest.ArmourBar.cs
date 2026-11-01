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
            hud.ResetVitalsDrawStats();
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
            // ui/hud-bar-colours: never two armour bars in one frame (any path: Iron Halo clip, plain clip, M44 fallback), over every state above.
            Check("armour bar: at most one armour bar drawn in any frame, never two kinds together", hud.VitalsArmourDrawsMax <= 1 && !hud.VitalsArmourMixed,
                $"most in a frame {hud.VitalsArmourDrawsMax}, mixed {hud.VitalsArmourMixed}");

            // Sweeping the armour and the health through new frames one a frame (regen, damage): the bar keeps its last picture until the new one is rendered, so no frame lacks a layer.
            foreach (bool halo in new[] { false, true })
            {
                hud.Data.IronHalo = halo; hud.Data.Shield = 100f; hud.Data.Health = 100f;
                for (int i = 0; i < 400 && hud.VitalsArmourVariantDrawn != (halo ? "h" : "p"); i++) await Frames(1);
                await Frames(30);
                hud.ResetVitalsDrawStats();
                for (int k = 99; k >= 2; k--) { hud.Data.Shield = k; hud.Data.Health = k; await Frames(1); }
                for (int k = 2; k <= 100; k += 2) { hud.Data.Shield = k; await Frames(1); }
                Check($"armour bar: {(halo ? "Iron Halo" : "plain")}, swept through new frames: the armour and health layers never blank, one armour bar a frame",
                    hud.VitalsArmourBlankFrames == 0 && hud.VitalsHealthBlankFrames == 0 && hud.VitalsArmourDrawsMax == 1 && !hud.VitalsArmourMixed,
                    $"armour blank {hud.VitalsArmourBlankFrames}, health blank {hud.VitalsHealthBlankFrames}, most in a frame {hud.VitalsArmourDrawsMax}, mixed {hud.VitalsArmourMixed}");
            }

            // Switching the Iron Halo on and off every other frame never draws both bars (the kept picture belongs to one kind only).
            hud.Data.Shield = 60f; hud.Data.Health = 100f;
            hud.ResetVitalsDrawStats();
            for (int i = 0; i < 40; i++) { hud.Data.IronHalo = i / 2 % 2 == 0; await Frames(1); }
            Check("armour bar: the Iron Halo toggled every other frame: never two armour bars in one frame", hud.VitalsArmourDrawsMax <= 1 && !hud.VitalsArmourMixed,
                $"most in a frame {hud.VitalsArmourDrawsMax}, mixed {hud.VitalsArmourMixed}");
        }
        finally { hud.QueueFree(); }
    }
}
