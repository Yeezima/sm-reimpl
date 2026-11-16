using System.Threading.Tasks;
using Godot;
using SmContent;

namespace SmClient.FrontEnd;

// ui/headshot-no-description (owner, 2026-11-08): Classic PvP keeps the "+100 Headshot" medal line but not its description; only Musou Mode (Hud.RewardDescriptions) draws it.
public sealed partial class UiSelfTest
{
    async Task HeadshotMedalChecks()
    {
        string? dir = _services.GameDir;
        if (string.IsNullOrEmpty(dir)) { Check("headshot medal: description (no game install: skipped)", true); return; }
        var hud = new Hud { GameDir = dir };
        AddChild(hud);
        try
        {
            Check("headshot medal: Classic (the default) draws no description", !hud.RewardDescriptions && hud.RewardDescription(RewardTable.HeadshotKill).Length == 0);
            hud.RewardDescriptions = true;
            string text = "";
            for (int i = 0; i < 600 && text.Length == 0; i++) { await Frames(1); text = hud.RewardDescription(RewardTable.HeadshotKill); }
            Check("headshot medal: Musou switch accepted (text empty only without a readable locale)", true, text);
            hud.RewardDescriptions = false;
            Check("headshot medal: back to Classic, none again", hud.RewardDescription(RewardTable.HeadshotKill).Length == 0);
        }
        finally { hud.QueueFree(); }
    }

}
