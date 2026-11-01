using System;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The score's panels, the reward cards and the slot keys on the client (M6, M6b). A map's panel, then its cards, then the run's panel stand where the war map stands (<c>_pickerLayer</c>, forced), so the view's rules for a menu
/// hold unchanged. Every shown score comes from tag 12, never from the client's own tracker. Keys 3, 4 and 5 are read here (keyboard only: owner's item 27) and keys 1 and 2 only bring the own weapon back while an extra stands
/// in its field; the core's own weapon switching is unchanged.
/// </summary>
public partial class NetworkView
{
    /// <summary>A standing offer repeats every second: unheard for this many ticks it is gone (a new run).</summary>
    const uint TfwOfferSilenceTicks = 90;
    /// <summary>How long the run's panel waits for the last map's row (s).</summary>
    const double TfwRunRowsSeconds = 2.0;

    TfwPagePanel? _tfwModal;
    TfwMapScore? _tfwLastMap;
    int _tfwMapsSeen;
    ushort _tfwOfferDone;
    bool _tfwRunShown;
    double _tfwRunEndedAt = -1;
    readonly bool[] _tfwSlotKeys = new bool[6];
    string _tfwSlotsText = "";
    TfwArsenalView _tfwSlotsFor;
    int _tfwSlotsSecond = -2, _tfwSlotsBest = -1;

    T TfwOpenModal<T>(T panel) where T : TfwPagePanel
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiKit.EnterVirtualSpace(GetWindow());
        _pickerForced = true;
        _pickerLayer = new CanvasLayer { Name = "TfwModal", Layer = 60 };
        var root = new Control { Name = "TfwModalRoot", Theme = UiKit.Theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(panel);
        _pickerLayer.AddChild(root);
        AddChild(_pickerLayer);
        UiSounds.Current?.Attach(_pickerLayer);
        _tfwModal = panel;
        return panel;
    }

    void TfwCloseModal()
    {
        if (_tfwModal is null) return;
        _tfwModal = null;
        _pickerLayer?.QueueFree();
        _pickerLayer = null;
        _pickerForced = false;
        PadMenuClosed();
        UiKit.LeaveVirtualSpace(GetWindow());
        RestoreMouse(leaving: false);
    }

    /// <summary>Tag 12: a map's panel in the Stormbird, the run's at its end.</summary>
    void TfwScoreFrame(in TfwWarView war, PlayerState me)
    {
        if (_bot || Lean) return;
        if (_tfwModal is not null && _pickerLayer is null) _tfwModal = null;   // the view closed it (the connection went)
        var history = _tfwState.ScoreHistory;
        if (history.Count < _tfwMapsSeen) { _tfwMapsSeen = history.Count; _tfwLastMap = null; }   // a new run
        bool newMap = history.Count > 0 && (history.Count != _tfwMapsSeen || _tfwLastMap != history[^1]);

        if (war.Phase is TfwRunPhase.Won or TfwRunPhase.Lost)
        {
            if (_tfwRunShown) return;
            if (_tfwRunEndedAt < 0) _tfwRunEndedAt = _connectedFor;
            uint total = _tfwState.ScoreOwn?.RunTotal ?? 0;
            long summed = history.Sum(m => (long)m.Points);
            if ((summed != total && history.Count < TfwWire.MaxScoreMaps || _tfwState.ScoreOwn is null) && _connectedFor - _tfwRunEndedAt < TfwRunRowsSeconds) return;
            if (_tfwModal is not null) TfwCloseModal();
            if (PickerBusy) return;   // the Esc menu is up: after it
            _tfwRunShown = true;
            _tfwMapsSeen = history.Count;
            _tfwLastMap = history.Count > 0 ? history[^1] : null;
            TfwOpenRunPanel(war.Phase == TfwRunPhase.Won, total);
            return;
        }
        _tfwRunShown = false;
        _tfwRunEndedAt = -1;
        if (!newMap || war.Phase != TfwRunPhase.Hub || !me.Alive || PickerBusy) return;
        _tfwMapsSeen = history.Count;
        _tfwLastMap = history[^1];
        TfwOpenModal(new TfwScorePanel(history[^1], _tfwState.ScoreOwn?.RunTotal ?? 0, TfwCloseModal)).Focus();
        GD.Print($"t {_connectedFor:F1} s: war: the map's score shows");
    }

    /// <summary>The directory <c>frontend.json</c> lies in: beside the profile.</summary>
    string TfwBestDirectory() =>
        CmdArg("profile") is { Length: > 0 } file ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file)) ?? "." : ProjectSettings.GlobalizePath("user://");

    void TfwOpenRunPanel(bool won, uint total)
    {
        string outcome = won ? "VICTORY" : "DEFEAT";
        var now = DateTime.UtcNow;
        string directory = TfwBestDirectory();
        bool newBest = TfwBest.Note(directory, total, outcome, now);
        var best = TfwBest.Load(directory);
        var maps = _tfwState.ScoreHistory.ToArray();
        string result = TfwBest.ResultLine(total, outcome, maps.Length, now);
        ushort self = (ushort)Math.Clamp(_client?.PlayerId ?? 0, 0, ushort.MaxValue);
        var ranked = TfwBest.Ranked(self, total, _tfwState.ScoreOthers).Select(p => (TfwRankName(p.Player), p.RunTotal)).ToList();
        var panel = TfwOpenModal(new TfwRunPanel(won, maps, total, ranked, newBest, best, result, () => { DisplayServer.ClipboardSet(result); GD.Print("war: the result is on the clipboard: " + result); }, TfwCloseModal));
        panel.Focus();
        GD.Print($"t {_connectedFor:F1} s: war: the run's score shows: {result}");
    }

    string TfwRankName(int player) =>
        _client?.Scoreboard?.Players.FirstOrDefault(p => p.PlayerId == player) is { Name.Length: > 0 } entry ? entry.Name : $"PLAYER {player}";

    /// <summary>Tag 14: the cards, in the Stormbird, after the map's panel. Tag 15: the slot keys.</summary>
    void TfwRewardFrame(in TfwWarView war, PlayerState me)
    {
        if (_bot) return;
        TfwSlotKeys(me);
        if (Lean || PickerBusy || war.Phase != TfwRunPhase.Hub || !me.Alive || _tfwState.Reward is not { } offer || offer.Sequence == _tfwOfferDone) return;
        if (_tfwState.NewestTick - _tfwState.RewardTick > TfwOfferSilenceTicks) return;
        TfwOpenModal(new TfwRewardPanel(offer, TfwCardName, card => TfwPick(offer.Sequence, card))).Focus();
        GD.Print($"t {_connectedFor:F1} s: war: the reward cards show (offer {offer.Sequence})");
    }

    /// <summary>A card is picked: the command names the offer, and the panel closes. The server's slots (tag 15) confirm a weapon.</summary>
    void TfwPick(ushort offer, byte card)
    {
        _tfwOfferDone = offer;
        _tfwState.Commands.Issue(TfwCommand.ChooseReward, card, offer: offer);
        GD.Print($"t {_connectedFor:F1} s: war: card {card} picked from offer {offer}");
        TfwCloseModal();
    }

    string TfwCardName(TfwRewardCard card)
    {
        if (_config is null) return "?";
        return card.Kind switch
        {
            TfwRewardKind.Weapon => MusouDropName(MusouDropItem.Wargear(_config, _client?.Predicted.Class ?? 0, card.Item)).ToUpperInvariant(),
            TfwRewardKind.Equipment => card.Item < _config.Thrown.Count ? (_config.Thrown[card.Item].PlayerName ?? MusouDropName(_config.Thrown[card.Item].Name)).ToUpperInvariant() : "?",
            TfwRewardKind.Perk => card.Item < _config.Perks.Count ? TfwPerkName(_config.Perks[card.Item].Name) : "?",
            _ => "",
        };
    }

    /// <summary>The perk's name as the customizer shows it, when its catalogue is up; else read from the file's name.</summary>
    string TfwPerkName(string file)
    {
        return (_pickerServices?.Catalog?.Perks.Perk(file)?.DisplayName ?? file.Replace("mp_perk_", "").Replace('_', ' ')).ToUpperInvariant();
    }

    /// <summary>Keys 3 to 5, a press each; keys 1 and 2 only while an extra stands in their field (the core's press alone would show the extra).</summary>
    void TfwSlotKeys(PlayerState me)
    {
        var arsenal = _tfwState.Arsenal;
        bool live = me.Alive && !PickerBusy;
        for (int key = 1; key <= 5; key++)
        {
            bool down = key switch
            {
                1 => ControlsInput.Held(GameAction.Weapon1),
                2 => ControlsInput.Held(GameAction.Weapon2),
                3 => Input.IsPhysicalKeyPressed(Key.Key3),
                4 => Input.IsPhysicalKeyPressed(Key.Key4),
                _ => Input.IsPhysicalKeyPressed(Key.Key5),
            };
            bool press = down && !_tfwSlotKeys[key];
            _tfwSlotKeys[key] = down;
            if (!press || !live) continue;
            bool asks = key switch { 1 => arsenal.LoadedPistol != 0, 2 => arsenal.LoadedPrimary != 0, _ => arsenal.Weapon(key) >= 0 };
            if (asks) _tfwState.Commands.Issue(TfwCommand.SelectSlot, (byte)key);
        }
    }

    /// <summary>The HUD's line of keys 3 to 5, rebuilt on a change; with a second primary key 3 stays the core's and names it.</summary>
    string TfwSlotsLine(PlayerState me)
    {
        var a = _tfwState.Arsenal;
        int second = _config is null ? -1 : Loadouts.WeaponIndex(_config, me, WeaponSlot.Second);
        int best = me.Slot switch { WeaponSlot.Ranged => a.LoadedPrimary, WeaponSlot.Pistol => a.LoadedPistol, _ => 3 };
        if (a == _tfwSlotsFor && second == _tfwSlotsSecond && best == _tfwSlotsBest && _tfwSlotsText.Length > 0) return _tfwSlotsText;
        _tfwSlotsFor = a; _tfwSlotsSecond = second; _tfwSlotsBest = best;
        string Name(int weapon) => _config is { } c && weapon >= 0 && weapon < c.Weapons.Count ? MusouDropName(c.Weapons[weapon].Name).ToUpperInvariant() : "-";
        string Key(int key, int weapon) => (best == key ? $"[{key}] " : $"{key} ") + Name(weapon);
        _tfwSlotsText = $"{Key(3, second >= 0 ? second : a.Slot3)}   {Key(4, a.Slot4)}   {Key(5, a.Slot5)}";
        return _tfwSlotsText;
    }
}
