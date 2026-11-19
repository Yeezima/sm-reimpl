using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;

namespace SmMapmakerEditor;

/// <summary>
/// The Library tab's SM1 definitions (owner 2026-11-19): every Classic PvP map's definitions are listed from the background index (<see cref="Sm1Catalog"/>),
/// grouped by map under a heading each, with a map filter above the list ("All maps" or one). No map has to be opened to browse or to place from it.
/// </summary>
public partial class MapmakerDock
{
    readonly OptionButton _mapFilter = new() { TooltipText = "Which original map's definitions to list. All maps groups them under a heading per map.", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(120, 0), Visible = true };
    int _catalogRevision = -1;
    string _filterMap = "";   // "" = all maps

    /// <summary>The filter row (built with the Library tab); only meaningful while "SM1 definitions" is the source.</summary>
    Control BuildMapFilterRow()
    {
        _mapFilter.ItemSelected += i =>
        {
            _filterMap = _mapFilter.GetItemMetadata((int)i).AsString();
            if (_filterMap.Length > 0) { Sm1Catalog.Want(_filterMap); _plugin.RequestSm1Load(_filterMap); }   // previews need the map's meshes; the list does not
            RebuildLibrary();
        };
        _mapFilter.AddItem("All maps"); _mapFilter.SetItemMetadata(0, "");
        return _mapFilter;
    }

    /// <summary>Fills the filter's entries from the catalog (keeping the choice), showing which maps are still being indexed.</summary>
    void RefreshMapFilter()
    {
        _mapFilter.Clear();
        _mapFilter.AddItem("All maps"); _mapFilter.SetItemMetadata(0, "");
        int selected = 0;
        foreach (var m in Sm1Catalog.Maps)
        {
            var entry = Sm1Catalog.Get(m.Name);
            string text = entry is null ? (Sm1Catalog.FailureOf(m.Name) is null ? $"{m.DisplayName} (indexing...)" : $"{m.DisplayName} (unreadable)") : $"{m.DisplayName} ({entry.Definitions.Count})";
            _mapFilter.AddItem(text);
            int idx = _mapFilter.ItemCount - 1;
            _mapFilter.SetItemMetadata(idx, m.Name);
            _mapFilter.SetItemTooltip(idx, m.Name);
            if (m.Name == _filterMap) selected = idx;
        }
        _mapFilter.Select(selected);
        if (selected == 0) _filterMap = "";
    }

    /// <summary>The rows for the SM1 source: per map in the catalog (or the chosen one), a heading and then its definitions matching the filter text.</summary>
    void FillSm1Items(Func<string, bool> match)
    {
        Sm1Catalog.Start();
        _catalogRevision = Sm1Catalog.Revision;
        RefreshMapFilter();
        foreach (var m in Sm1Catalog.Maps)
        {
            if (_filterMap.Length > 0 && !m.Name.Equals(_filterMap, StringComparison.OrdinalIgnoreCase)) continue;
            if (Sm1Catalog.Get(m.Name) is not { } entry) continue;
            var names = entry.Definitions.Where(match).ToList();
            _hiddenEmpty += entry.Empty;
            if (names.Count == 0) continue;
            if (_filterMap.Length == 0) _items.Add(new LibraryItem(LibraryKind.Sm1, "", $"{m.DisplayName}  ({names.Count})", m.Name));
            foreach (var name in names) _items.Add(new LibraryItem(LibraryKind.Sm1, name, name, m.Name));
        }
    }

    /// <summary>What to say under the list about the index: progress while it builds, or why it cannot.</summary>
    string Sm1IndexNote()
    {
        if (Sm1Catalog.Problem is { } p) return " " + p;
        var (done, total) = Sm1Catalog.Progress;
        if (total == 0) return " Reading the map list from the install...";
        return done < total ? $" Indexing SM1 maps in the background: {done} of {total} ready." : "";
    }

    /// <summary>Called from _Process: the background index moved, so the list is rebuilt (only while the SM1 source is the one shown).</summary>
    void PollCatalog()
    {
        if (_catalogRevision == Sm1Catalog.Revision) return;
        if (_source.Selected != 0) { _catalogRevision = Sm1Catalog.Revision; return; }
        RebuildLibrary();
    }

    /// <summary>A row whose picture cannot be made yet because its map is not in memory (the list itself needs no map).</summary>
    static bool NeedsMapLoad(LibraryItem item) => item.Kind == LibraryKind.Sm1 && !item.IsHeader && item.Map is { } m && MapHost.Sm1For(m) is null;

    /// <summary>The Map tab's undoable "Remove SM1 backdrop" button.</summary>
    Button RemoveBackdropButton() => Btn("Remove SM1 backdrop", _plugin.RemoveBackdrop, "Takes the opened original map out of the view for good (the Show box only hides it). Your imported and placed objects stay. Undo brings it back.");

    // ---- test aids (Sm1LibraryTest) ----

    /// <summary>Picks a map in the filter ("" = all maps), as choosing it in the dropdown does.</summary>
    public void TestFilterMap(string map)
    {
        _filterMap = map;
        if (map.Length > 0) { Sm1Catalog.Want(map); _plugin.RequestSm1Load(map); }
        RebuildLibrary();
    }

    /// <summary>Opens the filter's dropdown list (for a screenshot).</summary>
    public void TestOpenFilterPopup() => _mapFilter.ShowPopup();
    public void TestCloseFilterPopup() => _mapFilter.GetPopup().Hide();
    public (int Rows, int Headers, int Entries) TestFilterStats() => (_items.Count(x => !x.IsHeader), _items.Count(x => x.IsHeader), _mapFilter.ItemCount - 1);
}
