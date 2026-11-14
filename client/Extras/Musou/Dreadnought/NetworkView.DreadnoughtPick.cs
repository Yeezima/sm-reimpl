using Godot;
using SmClient.FrontEnd;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Picking the Dreadnought (D7; Musou Mode only, as the Librarian's <c>NetworkView.LibrarianLook.cs</c>): a row PLAY AS DREADNOUGHT in the Stormbird's Esc menu and the button on his customizer page while the party is in the
/// Stormbird and the player is alive. Both send the Setup the DEPLOY button sends, with the Dreadnought's class and the player's looks, and the class change follows (<see cref="TfwClassFrame"/>). In the Stormbird he plays as the
/// Tactical stand-in (the owner's rule: no Dreadnought in the troop bay); he is the Dreadnought from the landing. <c>--class "sm - dreadnought"</c> picks him at the join for tests.
/// </summary>
public partial class NetworkView
{
    public const string DreadnoughtRowText = "Play as Dreadnought";

    int DreadnoughtClassIndex()
    {
        if (_config is null) return -1;
        for (int i = 0; i < _config.Classes.Count; i++) if (DreadnoughtRules.IsDreadnought(_config, i)) return i;
        return -1;
    }

    /// <summary>Whether the own player may pick the Dreadnought now: Musou Mode, in the Stormbird, alive, a Space Marine, not already picked.</summary>
    bool DreadnoughtOffered(PlayerState me, out int index)
    {
        index = DreadnoughtClassIndex();
        return index >= 0 && MusouActive && _client?.Match.State == MatchState.Playing && _tfwState.War is { } war && war.Phase == TfwRunPhase.Hub && me.Alive && me.Team == Team.SpaceMarines && me.Class != index;
    }

    void DreadnoughtMatchMenu(MatchMenu menu)
    {
        if (_client is not { } client || !DreadnoughtOffered(client.Predicted, out _)) return;
        menu.AddRow(DreadnoughtRowText, () => PickDreadnought("the Esc menu"));
    }

    void DreadnoughtCustomizer()
    {
        if (_pickerCreator is not { } creator || !MusouActive) return;
        creator.MusouDreadnoughtDeploy = DreadnoughtFromCustomizer;
    }

    string? DreadnoughtFromCustomizer()
    {
        if (_client is not { Status: ClientStatus.Connected } client || !DreadnoughtOffered(client.Predicted, out _))
            return "The Dreadnought is picked in the Stormbird, alive, while the war runs.";
        return PickDreadnought("the customizer") ? null : "The Dreadnought is not on offer now.";
    }

    bool PickDreadnought(string from)
    {
        if (_client is not { Status: ClientStatus.Connected } client || !DreadnoughtOffered(client.Predicted, out int index)) return false;
        var choice = new LoadoutChoice(Class: index, Melee: 0, Equipment: 0, Perk1: -1, Perk2: -1);
        var sm = _ownLooks.Active(Team.SpaceMarines);
        var chaos = _ownLooks.Active(Team.Chaos);
        _lastSetupSequence = client.ChangeSetup(choice, sm, chaos, _ownOrkLook);
        _setupSent = true; _setupSentAt = _connectedFor; _ackShownUntil = double.MaxValue;
        _sentClass = index; _ackLogged = 0;
        _ownLooks.Request(sm, chaos);
        _pickerDeployed = true;
        GD.Print($"t {_connectedFor:F1} s: war: the Dreadnought is picked from {from} (setup {_lastSetupSequence})");
        ClosePicker();
        return true;
    }
}
