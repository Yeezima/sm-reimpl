namespace SmContent;

/// <summary>
/// The menu sounds (fidelity gap 9). The original's front-end movies (<c>MainMenu.gfx</c>, <c>OnlineMenu.gfx</c>, <c>MultiplayerLobby.gfx</c>, <c>LobbyCustomizer.gfx</c>,
/// <c>TeamSelectMenu.gfx</c>, <c>HostOptions.gfx</c>, <c>SearchSelection.gfx</c>, <c>OptionsMenu.gfx</c>) post three events through <c>funcPlaySound</c> / <c>PostSoundEvent</c>
/// (their constant pools; read-only investigation, nothing stored): <see cref="Move"/> when the highlight moves, <see cref="Select"/> when an entry is chosen and
/// <see cref="Back"/> when the screen is left; all three are in <c>In_Game_Default</c> with embedded media. Which of our buttons stands for which is **ours**: a press
/// plays Select, except a button that leaves (<see cref="IsBack"/>), which plays Back; the pointer or focus entering a button plays Move (not twice within
/// <see cref="MoveSpacing"/> s, ours). The other menu events (<c>Play_Menu_Expand_Main</c>, <c>Play_Multiplayer_Select</c>, <c>Play_Cooperative_Select</c>) belong to
/// screens we do not have in that form and are not played.
/// </summary>
public static class UiSoundRules
{
    public const string Move = "Play_Menu_Move", Select = "Play_Menu_Select", Back = "Play_Menu_Back";

    /// <summary>Least seconds between two Move sounds (ours: a sweep across a list of buttons is not a machine gun).</summary>
    public const double MoveSpacing = 0.06;

    static readonly string[] BackWords = ["back", "cancel", "close", "resume", "leave", "exit"];

    /// <summary>Whether a button's text leaves a screen (BACK (Esc), CANCEL (Esc), RESUME (Esc), a dialog's Cancel).</summary>
    public static bool IsBack(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return BackWords.Any(w => t.StartsWith(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The event a press of a button with this text plays.</summary>
    public static string ForPress(string? text) => IsBack(text) ? Back : Select;
}
