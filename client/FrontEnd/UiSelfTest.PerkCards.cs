using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;

namespace SmClient.FrontEnd;

// ui/hud-2: every perk card of every class fits (the longest descriptions, with their value lines, used to push the creator's action bar down).
public sealed partial class UiSelfTest
{
    /// <summary>The window sizes of <see cref="FitSizes"/> that give the same UI space (the UI is a 1920 x 1080 space stretched with the aspect kept) lay the creator out the same: the perk cards are checked once for each.</summary>
    readonly HashSet<string> _perkCardSizesDone = new();
    bool _perkCardShotTaken;

    /// <summary>The <c>--perk-card-shot</c> file, or null; a path inside the repository or the game install (or not a .png) is refused with a message.</summary>
    static string? PerkCardShot()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--perk-card-shot");
        if (i < 0 || i + 1 >= args.Length) return null;
        string path = System.IO.Path.GetFullPath(args[i + 1]);
        static bool Inside(string file, string? root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string r = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
            return file.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        string? repo = null;
        foreach (var start in new[] { System.Environment.CurrentDirectory, System.AppContext.BaseDirectory })
            for (var d = new System.IO.DirectoryInfo(start); d is not null && repo is null; d = d.Parent)
                if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "SmReimpl.slnx"))) repo = d.FullName;
        string? install = GameInstall.TryLocate()?.Dir;
        string? why = !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "it is not a .png file" : Inside(path, install) ? "it is inside the game install" : Inside(path, repo) ? "it is inside the repository" : null;
        if (why is not null) { GD.PrintErr($"--perk-card-shot refused ({why}): {path}"); return null; }
        return path;
    }

    /// <summary>
    /// For both sides and every class: opens the DEFAULTS loadout on the Perks step, shows each perk's card and checks that the card panel is inside the window, that its text is all there for the player: it fits the room it has or the area scrolls (the text shrinks to its smallest readable size and below that the text area scrolls: a card whose text is cut off with no scroll bar fails), and (for the tallest card of the class) that every control of the creator, the action bar's buttons among them, is still
    /// inside the window. Run once for each distinct UI size of the fit sizes.
    /// </summary>
    async Task PerkCardFitChecks(Rect2 view, string at)
    {
        string uiSize = $"{view.Size.X:F0}x{view.Size.Y:F0}";
        if (!_perkCardSizesDone.Add(uiSize)) { Check($"fit {at}: perk cards (the same UI space {uiSize} as an earlier size)", true); return; }
        var catalog = _services.Catalog!;
        var creator = _front.Creator!;
        string race0 = creator.PreviewRace;
        var openBefore = creator.Open;
        int cards = 0, tinyText = 0, scrolling = 0;
        try
        {
            foreach (string race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
            {
                creator.SetPreviewRace(race);
                await Frames(2);
                foreach (string role in ProfileStore.Roles(catalog))
                {
                    if (OrkLoadout.IsOrk(role)) continue;   // the Ork Boy has no perks
                    creator.OpenSelection(Selection.DefaultFor(role));
                    creator.OpenStep("perks");
                    await Frames(3);
                    var problems = new List<string>();
                    float tallest = -1f;
                    Action? tallestShow = null;
                    int n = 0;
                    foreach (var (title, show) in creator.PerkCardsForTest())
                    {
                        show();
                        await Frames(3);
                        var fit = creator.CardFit;
                        n++;
                        if (fit.Panel.Size.Y > tallest) { tallest = fit.Panel.Size.Y; tallestShow = show; }
                        if (title == "Sure Strike" && !_perkCardShotTaken && PerkCardShot() is { } shot)
                        {
                            // --perk-card-shot <file.png> (windowed run, screenshot aid): the creator with the longest card, once.
                            _perkCardShotTaken = true;
                            await Frames(6);
                            GetViewport().GetTexture().GetImage().SavePng(shot);
                            GD.Print("perk card screenshot -> " + shot);
                        }
                        if (fit.FontSize < CreatorView.CardSmallestText) tinyText++;
                        if (fit.Needed > fit.Available + 0.5f && fit.Scrolls) scrolling++;
                        if (!Encloses(view, fit.Panel)) problems.Add($"\"{title}\": the card is outside the window ({fit.Panel.Position.X:F0},{fit.Panel.Position.Y:F0} {fit.Panel.Size.X:F0}x{fit.Panel.Size.Y:F0})");
                        else if (fit.Needed > fit.Available + 0.5f && !fit.Scrolls) problems.Add($"\"{title}\": the text needs {fit.Needed:F0} of {fit.Available:F0} at size {fit.FontSize} for {fit.Chars} characters{" (cut off: no scroll bar)"}");
                    }
                    cards += n;
                    Check($"fit {at}: {race}/{role}: all {n} perk cards fit their room", n > 0 && problems.Count == 0, string.Join("; ", problems.Take(3)));
                    // The tallest card, with the whole creator laid out around it.
                    tallestShow?.Invoke();
                    await Frames(3);
                    CheckFits($"fit {at}: {race}/{role}: creator with its tallest perk card", creator, view);
                }
            }
        }
        finally
        {
            creator.SetPreviewRace(race0);
            creator.OpenSelection(openBefore);
            creator.OpenStep("weapons");
            await Frames(2);
        }
        Check($"fit {at}: {cards} perk cards of every class checked ({scrolling} scroll their longest text), none below the smallest text size", cards > 0 && tinyText == 0, $"{cards} cards, {tinyText} below the smallest size");
    }
}
