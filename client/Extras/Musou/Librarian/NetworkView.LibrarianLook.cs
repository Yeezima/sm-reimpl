using System;
using System.Collections.Generic;
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

    double _coopLibrarianCycle = -1;

    /// <summary>The position of <paramref name="item"/> in a class's choice list (equipment or perks), or -1.</summary>
    static int IndexOfEquipment(IReadOnlyList<int> choices, int item)
    {
        for (int i = 0; i < choices.Count; i++) if (choices[i] == item) return i;
        return -1;
    }

    /// <summary>
    /// The co-op test bot as a Librarian (test script, <see cref="ExtrasBotInput"/>): with an Ork within fire range it holds Fire (the lightning) and, on a 12 s cycle, presses Grenade (his equipment: the
    /// Warp Pull was removed, owner 2026-11-10) at the start and Reload (the Shockwave) 2 s later. Null for any other class (the bot's own fire and melee).
    /// </summary>
    PlayerInput? CoopLibrarianInput(PlayerState me, uint tick, float flat)
    {
        if (_config is null || !LibrarianRules.IsLibrarian(_config, me.Class)) return null;
        if (_coopLibrarianCycle < 0) _coopLibrarianCycle = _connectedFor;
        double t = (_connectedFor - _coopLibrarianCycle) % 12.0;
        var buttons = t < 0.15 && flat <= 20f ? InputButtons.Grenade : t is >= 2.0 and < 2.15 ? InputButtons.Reload : InputButtons.Fire;
        if (buttons == InputButtons.Fire) _coopShots++;
        _coopNote = $"Librarian: {(buttons == InputButtons.Grenade ? "a grenade" : buttons == InputButtons.Reload ? "Shockwave" : "chain lightning")} at an Ork {flat:F0} m";
        return new PlayerInput(tick, 0f, 0f, _yaw, buttons, _pitch);
    }

    /// <summary>
    /// Sends the Librarian's class as the loadout (his sword is the first melee choice; he carries nothing else) with the player's current looks, as DEPLOY would, and lets <see cref="TfwClassFrame"/> send the
    /// class change once the server has the Setup. Returns false when he is not on offer now.
    /// </summary>
    bool PickLibrarian(string from)
    {
        if (_client is not { Status: ClientStatus.Connected } client || !LibrarianOffered(client.Predicted, out int index)) return false;
        var me = client.Predicted;
        // His equipment (pass 3: every marine item) and perks: the player's current ones when the Librarian may have them, as positions in HIS lists (the choice indexes the class's lists:
        // the player's perk numbers used to be sent as positions, so from an Assault or a Devastator he got other perks than the player had; lead 2026-11-10), else his defaults.
        var cfg = _config!;
        int equipment = IndexOfEquipment(Loadouts.EquipmentChoices(cfg, index), me.Equipment);
        int perk1 = IndexOfEquipment(Loadouts.PerkChoices(cfg, index), me.Perk1), perk2 = IndexOfEquipment(Loadouts.PerkChoices(cfg, index), me.Perk2);
        var choice = new LoadoutChoice(Class: index, Melee: 0, Equipment: Math.Max(0, equipment), Perk1: perk1, Perk2: perk2 == perk1 ? -1 : perk2);
        var sm = _ownLooks.Active(Team.SpaceMarines);
        var chaos = _ownLooks.Active(Team.Chaos);
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
