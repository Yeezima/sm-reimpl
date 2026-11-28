using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmMapmaker;
using SmNet;

namespace SmClient;

/// <summary>
/// The F6 panel's CUSTOM MAP row (devtools-god-custommap, owner 2026-11-28: "a button in the dev tools with a dropdown that loads custom maps"; protocol 95; added content, a test aid), under the Classic
/// MAP row: a list of the saved <c>.smmap</c> files of this PC's Mapmaker maps folder (<see cref="CustomMaps.PickList"/>, listed again each time the panel opens) and GO, which asks the server to end the
/// match and start the next one on that file (<see cref="DevCommandKind.PickMapFile"/>: the index and the file name's check). The server reads and checks the file first: one it cannot play leaves the match
/// running and the status line says why; a map that cannot run the server's mode plays in Annihilation, and the status line says so. The row shows only on a server that takes map-file picks
/// (<see cref="DevState.CanPickFiles"/>: a supervised loopback <c>--dev-tools</c> server with added content, not Musou Mode). Script words: <c>cmap:N</c> (the index) or <c>cmap:&lt;file name&gt;</c>.
/// </summary>
public partial class DevToolsPanel
{
    VBoxContainer? _cmBox;
    OptionButton? _cmPick;
    Label? _cmNote;
    IReadOnlyList<CustomMaps.PickEntry> _cmFiles = [];
    string _cmMarked = "";
    bool _cmListed;

    void MapmakerPanelRows(VBoxContainer column)
    {
        _cmBox = new VBoxContainer { Name = "CustomMapPick", Visible = false };
        _cmBox.AddThemeConstantOverride("separation", 4);
        column.AddChild(_cmBox);
        _cmBox.AddChild(new Label { Text = "CUSTOM MAP (a saved Mapmaker map; ends this match now)", Modulate = new Color(0.55f, 0.8f, 1f) });
        var row = new HBoxContainer { Name = "CustomMapRow" };
        row.AddThemeConstantOverride("separation", 8);
        _cmBox.AddChild(row);
        _cmPick = new OptionButton { Name = "CustomMapList", CustomMinimumSize = new Vector2(300, 0), TooltipText = "The .smmap files in the Mapmaker maps folder of this PC (listed again each time F6 opens)." };
        row.AddChild(_cmPick);
        var go = new Button { Text = "GO", TooltipText = "Ends the current match at once; everyone rejoins on the chosen map in a few seconds. A map without capture points plays Annihilation; a map that cannot be loaded leaves this match running." };
        go.Pressed += PickSelectedFile;
        row.AddChild(go);
        _cmNote = new Label { Text = "", Modulate = new Color(0.7f, 0.7f, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _cmBox.AddChild(_cmNote);
    }

    /// <summary>
    /// Lists the maps folder again (each opening of the panel: a map saved meanwhile shows up), keeping the chosen file when it is still there. Only on a server that takes map-file picks: elsewhere the
    /// folder is never read.
    /// </summary>
    void MapmakerPanelOpened()
    {
        if (_cmPick is null || _state()?.CanPickFiles != true) { _cmListed = false; return; }
        _cmListed = true;
        string? kept = _cmPick.Selected >= 0 && _cmPick.Selected < _cmFiles.Count ? _cmFiles[_cmPick.Selected].File : null;
        _cmFiles = CustomMaps.PickList();
        _cmMarked = "";
        FillFiles(kept);
    }

    void FillFiles(string? keep)
    {
        if (_cmPick is null) return;
        string? current = NetworkView.CurrentCustomMapFile;
        _cmMarked = current ?? "";
        _cmPick.Clear();
        int select = 0;
        for (int i = 0; i < _cmFiles.Count; i++)
        {
            bool now = current is not null && string.Equals(_cmFiles[i].File, current, StringComparison.OrdinalIgnoreCase);
            _cmPick.AddItem(_cmFiles[i].Label + (now ? "  (playing now)" : ""));
            if (keep is not null ? string.Equals(_cmFiles[i].File, keep, StringComparison.OrdinalIgnoreCase) : now) select = i;
        }
        if (_cmFiles.Count > 0) _cmPick.Select(select);
        if (_cmNote is not null)
        {
            _cmNote.Visible = _cmFiles.Count == 0;
            _cmNote.Text = $"No saved maps in {CustomMaps.PickDir()} (the Mapmaker saves there).";
        }
    }

    void PickSelectedFile()
    {
        int i = _cmPick?.Selected ?? -1;
        if (i >= 0 && i < _cmFiles.Count) SendFilePick(i);
    }

    void SendFilePick(int index) => Send(DevCommandKind.PickMapFile, (byte)index, true, CustomMaps.PickCheck(_cmFiles[index].FileName));

    void MapmakerPanelPress(string name, string arg, ref bool handled)
    {
        if (name != "cmap" || arg.Length == 0) return;
        if (_state()?.CanPickFiles != true) { GD.Print("devtools: this server takes no map-file picks"); handled = true; return; }
        _cmFiles = CustomMaps.PickList();   // a script word picks from the folder as it is now (the panel need not be open)
        int at = int.TryParse(arg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n
            : _cmFiles.ToList().FindIndex(f => f.FileName.Equals(arg, StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileNameWithoutExtension(f.FileName).Equals(arg, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at >= _cmFiles.Count) { GD.Print($"devtools: no map file '{arg}' in {CustomMaps.PickDir()} ({_cmFiles.Count} listed)"); return; }
        GD.Print($"devtools: map file pick {at}: {_cmFiles[at].FileName}");
        SendFilePick(at);
        FillFiles(_cmFiles[at].File);
        handled = true;
    }

    void MapmakerPanelSync()
    {
        if (_cmBox is null) return;
        bool on = _state()?.CanPickFiles == true;
        _cmBox.Visible = on;
        if (on && !_cmListed && IsOpen) MapmakerPanelOpened();   // the server's state said so only after the panel opened
        else if (on && (NetworkView.CurrentCustomMapFile ?? "") != _cmMarked) FillFiles(null);   // the map being played changed: mark it
    }
}
