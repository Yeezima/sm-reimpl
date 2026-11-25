using System;
using System.Linq;
using SmContent;
using SmContent.Customization;
using SmContent.Extras.Warfront;
using SmSim;

namespace SmClient;

/// <summary>The choices behind a marine-rig Chaos unit's PvP look (Chaos Warfront C3): which Chaos class, and which predefined Chaos set (shared by the networked view and the crowd test view).</summary>
static class WarfrontChaosLookPick
{
    /// <summary>The words a Chaos class's name or role may hold for the plan's class word.</summary>
    public static string[] ClassWords(string classWord) => classWord switch
    {
        "raptor" => ["raptor", "assault"],
        "havoc" => ["havoc", "heavy", "devastator"],
        _ => ["tactical", "chaos_marine", "chaos marine"],
    };

    /// <summary>Whether a class's name or role holds one of <paramref name="words"/>.</summary>
    public static bool Matches(string name, string? role, string[] words) =>
        words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase) || (role ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The look code: the first predefined Chaos set whose name holds one of the plan's set words, else the default (the class's own armour); <paramref name="set"/> says which.</summary>
    public static AppearanceCode Code(CustomizationCatalog catalog, WarfrontChaosMarineLook plan, out string set)
    {
        set = "default armour";
        if (plan.SetWords.Count == 0 || catalog.Race(CustomizationCatalog.Chaos) is not { } race) return AppearanceCode.Default;
        var sets = ProfileWire.PredefinedSetsOf(race).ToList();
        foreach (var word in plan.SetWords)
        {
            int at = sets.FindIndex(s => s.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (at < 0 || at >= AppearanceCode.DefaultIndex || !WarfrontChaosLooksPlan.SetAllowed(sets[at].Name)) continue;   // only the crimson Khorne sets: a set's own paint wins
            set = sets[at].Name;
            return AppearanceCode.Default.WithPredefinedSet((byte)at);
        }
        return AppearanceCode.Default;
    }

    /// <summary>The install's predefined Chaos sets by name (the log).</summary>
    public static string SetNames(CustomizationCatalog catalog) =>
        catalog.Race(CustomizationCatalog.Chaos) is { } race ? string.Join(", ", ProfileWire.PredefinedSetsOf(race).Select(s => s.Name)) : "none";
}
