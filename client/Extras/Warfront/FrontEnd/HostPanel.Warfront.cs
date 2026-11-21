using Godot;
using SmContent.Extras.Warfront;

namespace SmClient.FrontEnd;

// Warfront (added content): the Host panel's Warfront profile. Hosting a campaign has no game mode, map or bots, so those rows are left out (not greyed) and the note is Warfront's own.
// What stays: who can join, the port, the players' emblems, HOST WARFRONT and the server rows. The server arguments are unchanged (the mode stays Annihilation's, no bots, the profile's own map).
// A later field (the weather choice) is added to HostPanel's row list and shows on this profile by default; only Classic's rows are listed here.
public sealed partial class HostPanel
{
    partial void WarfrontHostRowShown(Control row, ref bool shown)
    {
        if (Warfront && (row == _modeRow || row == _mapRow || row == _difficultyRow || row == _addLoyalist || row == _addChaos || row == _clearBots)) shown = false;
    }

    partial void ExtrasHostNote(ref string note)
    {
        if (Warfront) note = WarfrontLocal.HostExplainer + " " + ReachText();
    }
}
