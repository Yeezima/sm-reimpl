using System;
using Godot;
using SmClient.FrontEnd;
using SmContent.Customization;
using SmContent.Extras.Musou;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The Librarian's look and his entry in the Stormbird's class choice (Musou Mode only; docs/musou-expansion.md "Librarian").
/// <list type="bullet">
/// <item>Look: every Librarian wears his player's own Tactical look with the install's multiplayer Librarian torso and belt, right pad and psychic-hood backpack in place (<see cref="LibrarianLook"/>;
/// *original asset loaded*), so the customizer's colours carry over.</item>
/// <item>Class choice: the customizer's loadouts are by role and the Librarian has none of his own, so the Stormbird's Esc menu gets a row PLAY AS LIBRARIAN while the party is in the Stormbird
/// (and PLAY AS TACTICAL MARINE back is any loadout's DEPLOY, as before). The row sends the same Setup the DEPLOY button sends, with the Librarian's class, and the frame then sends the class change
/// (<see cref="TfwClassFrame"/>), so the smoke and the instant change are the same as for any class. No <c>--class</c> flag is needed any more.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>The Librarian's row text in the Esc menu.</summary>
    public const string LibrarianRowText = "Play as Librarian";

    partial void ExtrasLook(int playerClass, ref Func<ResolvedAppearance, ResolvedAppearance>? dress)
    {
        if (_config is not null && LibrarianRules.IsLibrarian(_config, playerClass)) dress = LibrarianLook.Dress;
    }

    /// <summary>The Librarian's class index in this match, or -1 (not Musou, or no Librarian appended).</summary>
    int LibrarianClassIndex()
    {
        if (_config is null) return -1;
        for (int i = 0; i < _config.Classes.Count; i++) if (LibrarianRules.IsLibrarian(_config, i)) return i;
        return -1;
    }

    /// <summary>A class's name for the Musou lines: "Librarian" for him (his role is the Tactical's, which <see cref="ClassDisplayName"/> would read), else the usual name.</summary>
    string MusouClassName(int playerClass) => _config is not null && LibrarianRules.IsLibrarian(_config, playerClass) ? "Librarian" : ClassDisplayName(playerClass);

    /// <summary>Whether the own player may take the Librarian now: Musou Mode, the party in the Stormbird, alive, a Space Marine, not one already.</summary>
    bool LibrarianOffered(PlayerState me, out int index)
    {
        index = LibrarianClassIndex();
        return index >= 0 && MusouActive && _client?.Match.State == MatchState.Playing && _tfwState.War is { } war && war.Phase == TfwRunPhase.Hub && me.Alive && me.Team == Team.SpaceMarines && me.Class != index;
    }

    /// <summary>The row in the Stormbird's Esc menu (called by the Esc menu's hook, <see cref="ExtrasMatchMenu"/>).</summary>
    void LibrarianMatchMenu(MatchMenu menu)
    {
        if (_client is not { } client || !LibrarianOffered(client.Predicted, out _)) return;
        menu.AddRow(LibrarianRowText, () => PickLibrarian("the Esc menu"));
    }

    /// <summary>
    /// The customizer's Librarian page in this match's picker (Musou Mode's customizer, owner 2026-11-10; client/Extras/Musou/Customizer/CreatorView.Musou.cs): its PLAY AS LIBRARIAN takes the Esc menu row's
    /// path with the look the page shows (the active loadout's, saved first as DEPLOY saves), and a Librarian's picker opens on his page. Called when the Esc menu is built (<see cref="ExtrasMatchMenu"/>).
    /// </summary>
    void LibrarianCustomizer()
    {
        if (_pickerCreator is not { } creator || !MusouActive) return;
        creator.MusouLibrarianDeploy = LibrarianFromCustomizer;
        if (_config is not null && _client is { } client && LibrarianRules.IsLibrarian(_config, client.Predicted.Class) && creator.MusouLayout)
            creator.MusouOpen(SmContent.Extras.Musou.MusouCustomizerEntry.Librarian);
    }

    /// <summary>PLAY AS LIBRARIAN on the customizer's page: null when he was picked, else why not.</summary>
    string? LibrarianFromCustomizer()
    {
        if (_client is not { Status: ClientStatus.Connected } client || !LibrarianOffered(client.Predicted, out _))
            return "The Librarian is picked in the Stormbird, alive, while the war runs" + (_client is { } c && _config is not null && LibrarianRules.IsLibrarian(_config, c.Predicted.Class) ? " (you are one now)." : ".");
        AppearanceCode? sm = null, chaos = null;
        if (_pickerServices is { Session: { } session } services)
        {
            if (session.Dirty && session.Save() is { } error) return "Could not save your looks: " + error;
            if (services.PlanFor() is { } plan) { sm = plan.SpaceMarineLook; chaos = plan.ChaosLook; }
        }
        return PickLibrarian("the customizer", sm, chaos) ? null : "The Librarian is not on offer now.";
    }

    double _coopLibrarianCycle = -1;

    /// <summary>
    /// The co-op test bot as a Librarian (test script, <see cref="ExtrasBotInput"/>): with an Ork within fire range it holds Fire (the lightning) and, on a 12 s cycle, presses Grenade (the Warp Pull) at the
    /// start and Reload (the Shockwave) 2 s later, when the pulled Orks stand close. Null for any other class (the bot's own fire and melee).
    /// </summary>
    PlayerInput? CoopLibrarianInput(PlayerState me, uint tick, float flat)
    {
        if (_config is null || !LibrarianRules.IsLibrarian(_config, me.Class)) return null;
        if (_coopLibrarianCycle < 0) _coopLibrarianCycle = _connectedFor;
        double t = (_connectedFor - _coopLibrarianCycle) % 12.0;
        var buttons = t < 0.15 && flat <= LibrarianRules.PullRange ? InputButtons.Grenade : t is >= 2.0 and < 2.15 ? InputButtons.Reload : InputButtons.Fire;
        if (buttons == InputButtons.Fire) _coopShots++;
        _coopNote = $"Librarian: {(buttons == InputButtons.Grenade ? "Warp Pull" : buttons == InputButtons.Reload ? "Shockwave" : "chain lightning")} at an Ork {flat:F0} m";
        return new PlayerInput(tick, 0f, 0f, _yaw, buttons, _pitch);
    }

    /// <summary>
    /// Sends the Librarian's class as the loadout (his sword is the first melee choice; he carries nothing else) with the player's current looks, as DEPLOY would, and lets <see cref="TfwClassFrame"/> send the
    /// class change once the server has the Setup. Returns false when he is not on offer now.
    /// </summary>
    /// <param name="spaceMarine">The look to wear (the customizer's Librarian page: the active loadout's, as it shows him); null: the look in effect now.</param>
    bool PickLibrarian(string from, AppearanceCode? spaceMarine = null, AppearanceCode? chaosLook = null)
    {
        if (_client is not { Status: ClientStatus.Connected } client || !LibrarianOffered(client.Predicted, out int index)) return false;
        var me = client.Predicted;
        var choice = new LoadoutChoice(Class: index, Melee: 0, Perk1: me.Perk1, Perk2: me.Perk2);
        var sm = spaceMarine ?? _ownLooks.Active(Team.SpaceMarines);
        var chaos = chaosLook ?? _ownLooks.Active(Team.Chaos);
        _lastSetupSequence = client.ChangeSetup(choice, sm, chaos, _ownOrkLook);
        _setupSent = true; _setupSentAt = _connectedFor; _ackShownUntil = double.MaxValue;
        _sentClass = index; _ackLogged = 0;
        _ownLooks.Request(sm, chaos);
        _pickerDeployed = true;   // TfwClassFrame: the Setup the class change waits for
        GD.Print($"t {_connectedFor:F1} s: war: the Librarian is picked from {from} (setup {_lastSetupSequence})");
        ClosePicker();
        return true;
    }
}
