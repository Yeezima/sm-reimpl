using SmMapmaker;
using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// The developer tools' map-file picker (devtools-god-custommap, owner 2026-11-28: "a button in the dev tools with a dropdown that loads custom maps"; protocol 95; added content, a test aid): the F6
/// panel's CUSTOM MAP row lists the saved <c>.smmap</c> files of this PC's Mapmaker maps folder (<see cref="CustomMaps.PickList"/>) and asks for one by its index and its file name's check. The file is
/// read and compiled off the tick, exactly as a <c>--custom-map</c> start would (<see cref="GameServer.CustomMap"/>): a file that cannot be played leaves the match running here with the reason in the
/// panel; a playable one ends the match and hands the file to the supervisor, which starts the next match on it (clients rejoin as after a rotation; RotationSupervisor.cs). A map that cannot run the
/// server's mode (Seize Ground without capture points) plays in Annihilation, and the panel says so. Only for a supervised loopback <c>--dev-tools</c> server of a profile with added content and not in
/// Musou Mode (<see cref="FilePicksPossible"/>); the core gate (loopback bind and requester, no bots) has already passed.
/// </summary>
public sealed partial class GameServer
{
    sealed record FilePickResult(string File, string? Name, GameMap? Map, string? Error);

    Task<FilePickResult>? _filePick;
    int _filePickPlayer;

    /// <summary>Test seam: the folder the picker lists instead of <see cref="CustomMaps.PickDir"/> (the process-wide <c>SM_MAPS_DIR</c> is not touched by tests).</summary>
    internal string? DevPickDirForTests { get; set; }

    /// <summary>Whether this server takes map-file picks: the tools on, a supervisor to start the next match, a profile with added content (never <c>--content faithful</c>) and Classic PvP (not Musou Mode).</summary>
    bool FilePicksPossible => DevToolsOn && _options.DevTools is { CanChangeMap: true } && World.Config.Extras != ContentExtras.None && !World.Config.Extras.HasFlag(ContentExtras.Musou);

    partial void ExtrasDevCanPickFiles(ref bool can) => can = FilePicksPossible;

    partial void ExtrasDevPickFile(int player, byte index, int check, ref bool handled)
    {
        if (World.Config.Extras == ContentExtras.None) return;   // the faithful profile has no custom maps: the core refuses
        handled = true;
        if (World.Config.Extras.HasFlag(ContentExtras.Musou)) { Log?.Invoke("dev tools: map file pick refused: not in Musou Mode"); Answer(DevNote.NotInThisMode, "Custom maps are Classic PvP only."); return; }
        if (_options.DevTools is not { CanChangeMap: true }) { Log?.Invoke("dev tools: map file pick refused: this server cannot change its map (it does not run under its supervisor)"); Answer(DevNote.NoMapChange); return; }
        if (_devFilePickBusy) { Answer(DevNote.MapLoading, "A map file is still being checked; wait a moment."); return; }
        if (PickedMap is not null) { Answer(DevNote.Applied); return; }   // a pick is already on its way
        var list = CustomMaps.PickList(DevPickDirForTests);
        if (index >= list.Count || CustomMaps.PickCheck(list[index].FileName) != check)
        {
            Log?.Invoke($"dev tools: map file pick refused: entry {index} of {list.Count} does not match the panel's (the maps folder {CustomMaps.PickDir()} changed, or the panel lists another folder)");
            Answer(DevNote.MapRefused, "The maps folder changed since the panel listed it: close and reopen F6, then pick again.");
            return;
        }
        var entry = list[index];
        if (_options.DevTools.CurrentMapFile is { } current && string.Equals(Path.GetFullPath(current), entry.File, StringComparison.OrdinalIgnoreCase))
        {
            if (World.DevRestartMatch()) { Log?.Invoke($"dev tools: map file pick {entry.FileName} is the map being played: restart match, fresh scores (player {player})"); Answer(DevNote.Applied); }
            else Answer(DevNote.NotPlaying);
            return;
        }
        _devFilePickBusy = true;
        _filePickPlayer = player;
        string? install = _options.DevTools.GameDir;
        string file = entry.File;
        Log?.Invoke($"dev tools: map file pick {entry.FileName} (player {player}): reading and checking it; the match goes on meanwhile");
        // Off the tick: a map with SM1 pieces or imported models takes seconds to compile, and the clients and bots must keep hearing from the server meanwhile.
        _filePick = Task.Run(() =>
        {
            string? name = null;
            var map = CustomMap(file, ref name, out var error, install);
            return new FilePickResult(file, name, map, error);
        });
        Answer(DevNote.MapLoading, $"Loading {entry.FileName}...");
    }

    partial void ExtrasDevPoll()
    {
        if (_filePick is not { IsCompleted: true } task) return;
        _filePick = null;
        _devFilePickBusy = false;
        FilePickResult r;
        try { r = task.Result; }
        catch (AggregateException e) { r = new FilePickResult("", null, null, e.InnerException?.Message ?? e.Message); }
        string fileName = Path.GetFileName(r.File);
        if (r.Map is null)
        {
            string why = (r.Error ?? "it could not be compiled").Replace($"cannot play '{r.File}': ", "", StringComparison.Ordinal).Trim();
            if (!why.EndsWith('.')) why += ".";
            Log?.Invoke($"dev tools: map file pick {fileName} refused: {why}; the match goes on on {Map?.Name}");
            Answer(DevNote.MapRefused, $"{fileName} cannot be played: {why} The match goes on here.");
            SendDevStates();
            return;
        }
        if (PickedMap is not null) { Log?.Invoke($"dev tools: map file pick {fileName} dropped: another pick came first"); return; }
        // A map that cannot run the server's own mode loads anyway, in Annihilation (owner 2026-11-28); a map that can returns a fallen-back match to the server's mode.
        bool fallback = HomeMode == GameMode.SeizeGround && (r.Map.ControlPoints?.Count ?? 0) == 0;
        if (!World.DevPickEnd(MatchFlowWire.RotationRejoinTicks)) { Answer(DevNote.NotPlaying); SendDevStates(); return; }
        SetPick(r.File, fallback ? "annihilation" : null);
        Log?.Invoke($"dev tools: map file pick {fileName} ({r.Name}){(fallback ? " in Annihilation (it has no capture points for Seize Ground)" : "")}: the match ends now and a new one starts on it (player {_filePickPlayer}); clients rejoin as after a rotation");
        Answer(fallback ? DevNote.MapModeFallback : DevNote.Applied,
            fallback ? $"Starting {fileName} in Annihilation: it has no capture points for Seize Ground." : $"Starting {fileName}; everyone rejoins in a few seconds.");
        SendDevStates();
    }

    /// <summary>Test seam: waits for a map-file check under way to finish (the tick then completes the pick).</summary>
    internal void WaitForDevFilePick() => _filePick?.Wait(TimeSpan.FromSeconds(30));
}
