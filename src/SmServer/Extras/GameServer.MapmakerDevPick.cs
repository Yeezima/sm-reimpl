using SmMapmaker;
using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// The developer tools' map-file picker (devtools-god-custommap, owner 2026-11-28: "a button in the dev tools with a dropdown that loads custom maps"; protocol 95; added content, a test aid): the F6
/// panel's CUSTOM MAP row lists the saved <c>.smmap</c> files of this PC's Mapmaker maps folder (<see cref="CustomMaps.PickList"/>) and asks for one by its index and its file name's check. Off the tick,
/// the server lists the same folder, checks the entry and compiles the file exactly as a <c>--custom-map</c> start would (<see cref="GameServer.CustomMap"/>): a file that cannot be played leaves the
/// match running here with the reason in the panel; a playable one ends the match and hands the file to the supervisor, which starts the next match on it (clients rejoin as after a rotation;
/// RotationSupervisor.cs). The file being played is picked again to reload it after an edit: an unchanged file (same map hash) only restarts the match, a changed one switches as any other (lead
/// 2026-11-28). A map that cannot run the server's mode (Seize Ground without capture points) plays in Annihilation, and the panel says so. Only for a supervised loopback <c>--dev-tools</c> server
/// of a profile with added content and not in Musou Mode (<see cref="FilePicksPossible"/>); the core gate (loopback bind and requester, no bots) has already passed.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The off-tick check's answer: the file (null when the entry did not match the panel's), its name, the compiled map or the error, and the folder and its size as listed.</summary>
    sealed record FilePickResult(string? File, string? Name, GameMap? Map, string? Error, string Dir, int Listed);

    Task<FilePickResult>? _filePick;
    int _filePickPlayer;
    /// <summary>The picked file's name as soon as the check found it (set on the check's thread, read after it completed), so even a check that faulted names its file.</summary>
    volatile string? _filePickName;

    /// <summary>Test seam: the folder the picker lists instead of <see cref="CustomMaps.PickDir"/> (the process-wide <c>SM_MAPS_DIR</c> is not touched by tests).</summary>
    internal string? DevPickDirForTests { get; set; }

    /// <summary>Test seam: the check waits for this before it lists the folder (a test holds a check under way).</summary>
    internal ManualResetEventSlim? DevFilePickHoldForTests { get; set; }

    /// <summary>
    /// Test seam (review, Low 2): the check runs on the tick's own thread and is complete when the pick returns, instead of waiting for a thread-pool thread (a loaded machine left the tests' 30 s
    /// wait short). The same work and the same completed-task handling as off the tick; never with <see cref="DevFilePickHoldForTests"/> (that would hold the tick).
    /// </summary>
    internal bool DevFilePickInlineForTests { get; set; }

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
        _devFilePickBusy = true;
        _filePickPlayer = player;
        _filePickName = null;
        string dir = DevPickDirForTests ?? CustomMaps.PickDir();
        string? install = _options.DevTools.GameDir;
        var hold = DevFilePickHoldForTests;
        Log?.Invoke($"dev tools: map file pick: entry {index} of {dir} (player {player}): reading and checking it; the match goes on meanwhile");
        // Off the tick: listing the folder, and a map with SM1 pieces or imported models takes seconds to compile; the clients and bots must keep hearing from the server meanwhile.
        var work = new Task<FilePickResult>(() =>
        {
            hold?.Wait(TimeSpan.FromSeconds(30));
            var list = CustomMaps.PickList(dir);
            if (index >= list.Count || CustomMaps.PickCheck(list[index].FileName) != check) return new FilePickResult(null, null, null, null, dir, list.Count);
            var entry = list[index];
            _filePickName = entry.FileName;
            string? name = null;
            var map = CustomMap(entry.File, ref name, out var error, install);
            return new FilePickResult(entry.File, name, map, error, dir, list.Count);
        });
        _filePick = work;
        if (DevFilePickInlineForTests && hold is null) work.RunSynchronously();
        else work.Start();
        Answer(DevNote.MapLoading, "Loading the map...");
    }

    partial void ExtrasDevPoll()
    {
        if (_filePick is not { IsCompleted: true } task) return;
        _filePick = null;
        _devFilePickBusy = false;
        FilePickResult r;
        try { r = task.Result; }
        catch (AggregateException e) { r = new FilePickResult(null, null, null, e.InnerException?.Message ?? e.Message, DevPickDirForTests ?? CustomMaps.PickDir(), -1); }
        string fileName = _filePickName ?? "the map file";
        if (r.File is null && r.Error is null)
        {
            Log?.Invoke($"dev tools: map file pick refused: the entry does not match the panel's ({r.Listed} files in {r.Dir}: the folder changed, or the panel lists another folder)");
            Answer(DevNote.MapRefused, "The maps folder changed since the panel listed it: close and reopen F6, then pick again.");
            SendDevStates();
            return;
        }
        if (r.Map is null)
        {
            // a check that faulted has no file in its result: the name it found (or none) and the folder it listed are scrubbed all the same (review)
            string? path = r.File ?? (_filePickName is { } found ? Path.Combine(r.Dir, found) : null);
            string why = RefusalReason(r.Error, path, fileName, r.Dir);
            Log?.Invoke($"dev tools: map file pick {fileName} refused: {why} The match goes on on {Map?.Name ?? "this map"}.");
            Answer(DevNote.MapRefused, $"The match goes on here. {fileName} cannot be played: {why}");   // the fixed part first: a long reason is cut at its end
            SendDevStates();
            return;
        }
        if (PickedMap is not null) { Log?.Invoke($"dev tools: map file pick {fileName} dropped: another pick came first"); return; }
        if (_options.DevTools?.CurrentMapFile is { } current && string.Equals(Path.GetFullPath(current), r.File, StringComparison.OrdinalIgnoreCase) && Map?.Hash == r.Map.Hash)
        {
            // the file being played, unchanged since the server loaded it: only a fresh start (a changed file switches below and the rejoin loads the new version)
            if (World.DevRestartMatch()) { Log?.Invoke($"dev tools: map file pick {fileName} is the map being played, unchanged: restart match, fresh scores (player {_filePickPlayer})"); Answer(DevNote.Applied); }
            else Answer(DevNote.NotPlaying);
            SendDevStates();
            return;
        }
        // A map that cannot run the server's own mode loads anyway, in Annihilation (owner 2026-11-28); a map that can returns a fallen-back match to the server's mode.
        bool fallback = HomeMode == GameMode.SeizeGround && (r.Map.ControlPoints?.Count ?? 0) == 0;
        if (!World.DevPickEnd(MatchFlowWire.RotationRejoinTicks)) { Answer(DevNote.NotPlaying); SendDevStates(); return; }
        SetPick(r.File!, fallback ? "annihilation" : null);
        Log?.Invoke($"dev tools: map file pick {fileName} ({r.Name}){(fallback ? " in Annihilation (it has no capture points for Seize Ground)" : "")}: the match ends now and a new one starts on it (player {_filePickPlayer}); clients rejoin as after a rotation");
        Answer(fallback ? DevNote.MapModeFallback : DevNote.Applied,
            fallback ? $"Starting {fileName} in Annihilation: it has no capture points for Seize Ground." : $"Starting {fileName}; everyone rejoins in a few seconds.");
        SendDevStates();
    }

    /// <summary>
    /// The reason a picked file was refused, in words for the panel and the log: never a folder of this PC (the full path is replaced by the file's name), without the server's "cannot play" prefix,
    /// ending with a full stop. <paramref name="dir"/>: the folder the picker listed, scrubbed too (a check that faulted may name it without the file).
    /// </summary>
    internal static string RefusalReason(string? error, string? path, string fileName, string? dir = null)
    {
        string why = error ?? "it could not be compiled";
        if (!string.IsNullOrEmpty(path))
        {
            why = why.Replace(path, fileName, StringComparison.OrdinalIgnoreCase);
            try { why = why.Replace(Path.GetFullPath(path), fileName, StringComparison.OrdinalIgnoreCase); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder) why = why.Replace(folder, "the maps folder", StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrEmpty(dir))
        {
            why = why.Replace(dir, "the maps folder", StringComparison.OrdinalIgnoreCase);
            try { why = why.Replace(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), "the maps folder", StringComparison.OrdinalIgnoreCase); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        why = why.Replace($"cannot play '{fileName}': ", "", StringComparison.Ordinal).Trim();
        return why.EndsWith('.') ? why : why + ".";
    }

    /// <summary>Test seam: waits for a map-file check under way to finish (the tick then completes the pick); false when it is still running after two minutes.</summary>
    internal bool WaitForDevFilePick()
    {
        try { return _filePick?.Wait(TimeSpan.FromMinutes(2)) ?? true; }
        catch (AggregateException) { return true; }   // a check that faulted is finished; the tick reports it
    }
}
