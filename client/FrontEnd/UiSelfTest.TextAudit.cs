using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

// ui-core 2026-11-20 (owner: no explainer text on the screens): the text audit. Every visible label or button text on the customizer, appearance, controls, options and lobby pages is at most five words and holds none of
// "(", "click", "press" or ":", unless the control is registered with UiKit.TextKind as an error, a refusal, a progress line or an item description (the registration is explicit and small: grep TextKind).
public sealed partial class UiSelfTest
{
    /// <summary>A visible text breaks the rule when it has more than five words (a word has a letter or digit), says "click" or "press", or has five words and a bracket or colon (a sentence in disguise); short data such as "RENAME (F2)" or "PERK 1: HOMER" is not a descriptor.</summary>
    static bool AuditBreaks(string text)
    {
        var words = text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetterOrDigit));
        var lower = text.ToLowerInvariant();
        bool verbs = words >= 4 && System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(click|press)\b");   // an input's own name ("L3 (STICK CLICK)") is not an instruction
        return words > 5 || verbs || (words >= 5 && (text.Contains('(') || text.Contains(':')));
    }

    /// <summary>The audit's findings on a tree: each visible text that breaks the rule and is not registered.</summary>
    static List<string> AuditText(Node root)
    {
        var found = new List<string>();
        void Walk(Node n)
        {
            if (n is CanvasItem { Visible: false }) return;
            string? text = n switch { Label l => l.Text, Button b => b.Text, _ => null };
            if (text is { Length: > 0 } && n is Control c && c.IsVisibleInTree() && !UiKit.HasTextKind(c))
            {
                string t = text.Trim();
                if (AuditBreaks(t)) found.Add($"{n.Name}: \"{(t.Length > 60 ? t[..60] + "..." : t)}\"");
            }
            foreach (var child in n.GetChildren()) Walk(child);
        }
        Walk(root);
        return found;
    }

    async Task TextAuditChecks()
    {
        void Audit(string screen, Node? root) { if (root is null) return; var f = AuditText(root); Check($"text audit: {screen} has no explainer text", f.Count == 0, string.Join(" | ", f.Take(8))); }
        var creator = _front.Creator!;
        _front.ShowCreator(); await Frames(6);
        foreach (var step in new[] { "class", "weapons", "equipment", "perks" })
        {
            creator.OpenStep(step); await Frames(4);
            Audit("the Armoury " + step, creator);
        }
        _front.ShowControls(); await Frames(4); Audit("controls", _front.Controls);
        _front.ShowOptions(OptionsView.Graphics); await Frames(4); Audit("options", _front.Options);
        _front.ShowAppearance(); await Frames(4); Audit("appearance", _front.Appearance);
        _front.ShowMenuPage("lobby"); await Frames(4); Audit("lobby", _front.LobbyPage);
        _front.ShowCreator(); await Frames(2);
    }
}
