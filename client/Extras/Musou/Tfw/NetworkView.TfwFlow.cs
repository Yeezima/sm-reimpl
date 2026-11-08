using System.Linq;
using Godot;
using SmClient.FrontEnd;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The late batch's run flow on the client (the owner, 2026-11-06, stream D; the rules are the server's, <c>TfwRun.Flow.cs</c>): a fallen player in the battle presses Enter (A on the pad) to deploy
/// again at the landing zone for a supply (<see cref="TfwCommand.Redeploy"/>), and the host's Esc menu has BACK TO STORMBIRD during a battle (<see cref="TfwCommand.ReturnToShip"/>).
/// </summary>
public partial class NetworkView
{
    bool _tfwRedeployKey;

    /// <summary>The fallen player's deploy key, on the press; resent like every war command until echoed, and the server refuses one it does not allow.</summary>
    void TfwRedeployFrame(in TfwWarView war, PlayerState me)
    {
        if (_bot) return;
        bool key = Input.IsPhysicalKeyPressed(Key.Enter) || Input.IsPhysicalKeyPressed(Key.KpEnter) || (_pad is not null && PadHost.Active && _pad.Button(JoyButton.A));
        if (key && !_tfwRedeployKey && !PickerBusy && TfwHudModel.CanRedeploy(war, me.Alive))
        {
            _tfwState.Commands.Issue(TfwCommand.Redeploy);
            GD.Print($"t {_connectedFor:F1} s: war: deploy again at the landing zone ({TfwBattleRules.RedeployCost} supply; {war.Supplies} left before it)");
        }
        _tfwRedeployKey = key;
    }

    /// <summary>The Esc menu's Musou rows: in the Stormbird the Librarian's class; the host's row while a battle is under way (the Stormbird comes back in 20 s, then the run is in the Stormbird).</summary>
    partial void ExtrasMatchMenu(MatchMenu menu)
    {
        LibrarianMatchMenu(menu);   // Librarian/NetworkView.LibrarianLook.cs: PLAY AS LIBRARIAN in the Stormbird
        if (!MusouActive || _client is not { } client || _tfwState.War is not { } war || !TfwHudModel.InBattle(war.Phase) || !war.Own.Deployed) return;
        if (client.Scoreboard is not { } board || !TfwHudModel.IsHost(client.PlayerId, board.Players.Select(r => r.PlayerId))) return;
        menu.AddRow(TfwHudModel.ReturnToShipText, () =>
        {
            _tfwState.Commands.Issue(TfwCommand.ReturnToShip);
            GD.Print($"t {_connectedFor:F1} s: war: the host calls the Stormbird back ({TfwBattleRules.RecallSeconds:F0} s)");
            ClosePicker();
        });
    }
}
