using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using SmContent;

namespace SmMapmakerEditor;

/// <summary>
/// Restart on request (owner, 2026-11-05: "when you close the map editor on your own to apply a fix, start it again for me automatically").
/// <c>mapmaker.cmd --restart</c> builds the plugin and drops <c>restart.request</c> in the control folder; the running editor saves the open map
/// (to its own file, or to <c>maps/autosave.smmap</c> when it has none), notes the map and the SM1 map in <c>resume.json</c> and quits; the script
/// starts the new editor, which reopens both. <c>editor.json</c> (process id) tells the script whether an editor is running.
/// Control folder: <c>%LOCALAPPDATA%\sm-reimpl\mapmaker\editor</c>, or <c>SM_MAPMAKER_CONTROL</c> (test runs, so they never touch the owner's editor).
/// Self-test runs take no part.
/// </summary>
public sealed partial class EditorControl : Node
{
    readonly MapmakerPlugin _plugin;
    double _wait;

    public EditorControl(MapmakerPlugin plugin) { _plugin = plugin; Name = "MapmakerEditorControl"; }
    public EditorControl() : this(null!) { }

    public static string Dir => System.Environment.GetEnvironmentVariable("SM_MAPMAKER_CONTROL") is { Length: > 0 } d ? d : Path.Combine(GameInstall.DataRoot, "mapmaker", "editor");
    static string EditorFile => Path.Combine(Dir, "editor.json");
    static string RequestFile => Path.Combine(Dir, "restart.request");
    static string ResumeFile => Path.Combine(Dir, "resume.json");
    static bool Active => !Selftest.Wanted && !FlowTest.Wanted;

    public override void _Ready()
    {
        if (!Active) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.Delete(RequestFile); // a request older than this editor is stale
            File.WriteAllText(EditorFile, new JsonObject { ["pid"] = OS.GetProcessId(), ["started"] = DateTime.UtcNow.ToString("o") }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { GD.PushWarning($"Mapmaker: control folder: {e.Message}"); }
    }

    public override void _ExitTree()
    {
        if (!Active) return;
        try { if (File.Exists(EditorFile) && File.ReadAllText(EditorFile).Contains($"\"pid\":{OS.GetProcessId()}")) File.Delete(EditorFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public override void _Process(double delta)
    {
        if (!Active || (_wait += delta) < 1.0) return;
        _wait = 0;
        if (!File.Exists(RequestFile)) return;
        try { File.Delete(RequestFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Restart();
    }

    void Restart()
    {
        var resume = new JsonObject();
        if (_plugin.Session is { } s)
        {
            string? path = s.Path;
            if (path is null && s.Document.Objects.Count > 0) path = _plugin.UniqueMapPath("autosave", "");
            if (path is not null && (_plugin.Dirty || s.Path is null) && !_plugin.SaveMap(path)) { _plugin.SetStatus("Restart cancelled: the map could not be saved."); return; }
            if (path is not null) resume["map"] = path;
        }
        if (MapHost.Sm1 is { } src) { resume["sm1"] = src.Map; resume["sm1Shown"] = MapHost.Root?.BackdropVisible ?? true; }
        try { File.WriteAllText(ResumeFile, resume.ToJsonString()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { GD.PushWarning($"Mapmaker: resume file: {e.Message}"); }
        // Godot's own RestartEditor hung on a hidden "save changes?" prompt (2026-11-05 test); the map is already saved and the Mapmaker scene holds
        // no content of its own, so the editor quits and mapmaker-restart.ps1 starts the new one once this process has ended.
        GD.Print("Mapmaker: closing for a restart on request");
        GetTree().Quit(0);
    }

    /// <summary>After a restart: reopens the map and the SM1 map that were open.</summary>
    public void Resume()
    {
        if (!Active || !File.Exists(ResumeFile)) return;
        JsonObject? r = null;
        try { r = JsonNode.Parse(File.ReadAllText(ResumeFile)) as JsonObject; File.Delete(ResumeFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return; }
        if (r is null || !_plugin.EnsureSceneRoot()) return;
        if (r["map"]?.GetValue<string>() is { } map && File.Exists(map)) _plugin.OpenMap(map);
        if (r["sm1"]?.GetValue<string>() is { } sm1)
            _plugin.Dock?.OpenSm1(sm1, r["sm1Shown"]?.GetValue<bool>() ?? true);
        _plugin.SetStatus("Restarted with the latest fix" + (r["map"] is null ? "." : $"; reopened {Path.GetFileName(r["map"]!.GetValue<string>())}."));
    }
}
