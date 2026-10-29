using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SmContent;

namespace SmClient.FrontEnd;

/// <summary>
/// What the bots of a hosted match need: the executable (and the Godot project folder when it is the editor binary), the port, the content profile, the human's side, the
/// plan, and the folder their own log files go to (<see cref="BotLogs"/>; null: Godot's default, which is the player's log folder; never passed by the front end).
/// </summary>
public sealed record BotLaunch(string Exe, string? ProjectPath, int Port, string? Content, bool HumanChaos, IReadOnlyList<BotSlot> Plan, string? LogFolder = null);

/// <summary>
/// The bots of the match this front end hosts (fix/connect-timeout, the owner's requests of 2026-10-29; rules in <see cref="HostBots"/>): headless, silent test-bot
/// clients started as child processes once the server listens, <see cref="HostBots.StaggerSeconds"/> apart, at most <see cref="HostBots.MaxBots"/> (fix/hosting-bots-logs:
/// the number is chosen before hosting; none is added or removed while the server runs). Each writes its own log file (<see cref="BotLogs"/>), never the player's.
/// Only the processes started here are ever stopped (held by their handles, so a reused process id cannot be hit): by STOP SERVER, the server's exit and the front end's exit
/// (the same rule as the hosted server: they never outlive the front end; on Windows they also join the kill-on-close job). A bot also quits by itself 5 s after its
/// server is gone.
/// </summary>
public static class HostedBots
{
    static readonly List<(Process Process, BotSlot Slot)> _bots = new();
    static readonly object _lock = new();
    static BotLaunch? _launch;
    static int _generation;
    static bool _exitHooked;
    static int _started;   // bots started by this front end (numbers their log files)

    /// <summary>Bots running now.</summary>
    public static int Count { get { lock (_lock) { Prune(); return _bots.Count; } } }

    /// <summary>The planned bots of the current launch that have not been started yet (they are still being staggered in).</summary>
    public static int Pending { get; private set; }

    /// <summary>Starts the plan's bots in the background, staggered; a later <see cref="StopAll"/> or a new launch ends the staggering.</summary>
    public static void Begin(BotLaunch launch)
    {
        int generation;
        lock (_lock)
        {
            generation = ++_generation;
            _launch = launch;
            Pending = launch.Plan.Count;
            if (!_exitHooked) { AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll(); _exitHooked = true; }
        }
        if (launch.Plan.Count == 0) return;
        var thread = new Thread(() =>
        {
            foreach (var slot in launch.Plan)
            {
                lock (_lock)
                {
                    if (generation != _generation || !HostedServer.Running) { Pending = 0; return; }
                    Prune();
                    if (_bots.Count < HostBots.MaxBots) StartOne(launch, slot);
                    Pending = Math.Max(0, Pending - 1);
                }
                Thread.Sleep(TimeSpan.FromSeconds(HostBots.StaggerSeconds));
            }
        }) { IsBackground = true, Name = "hosted bots" };
        thread.Start();
    }

    /// <summary>Stops every bot started here and any staggered start still to come. Safe from any thread, repeatedly.</summary>
    public static void StopAll()
    {
        List<Process> victims;
        lock (_lock)
        {
            _generation++;
            _launch = null;
            Pending = 0;
            victims = _bots.Select(b => b.Process).ToList();
            _bots.Clear();
        }
        foreach (var p in victims) Kill(p);
    }

    static bool StartOne(BotLaunch launch, BotSlot slot)
    {
        var info = new ProcessStartInfo(launch.Exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(launch.Exe) ?? "",
        };
        string? logFile = null;
        if (launch.LogFolder is { } folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                BotLogs.Prune(folder, BotLogs.Keep - 1);   // room for this one
                logFile = Path.Combine(folder, BotLogs.FileName(++_started, slot.Side, DateTime.Now, Environment.ProcessId));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // No folder of its own: the bot is not started, so it can never write into the player's log folder.
                Godot.GD.PrintErr($"host: no bot log folder ({folder}): {e.Message}; bot not started");
                return false;
            }
        }
        foreach (var a in HostBots.ClientArguments(launch.Port, slot, launch.Content, launch.ProjectPath, logFile)) info.ArgumentList.Add(a);
        try
        {
            var p = Process.Start(info);
            if (p is null) return false;
            WindowsJob.TryAssign(p);   // dies with the front end, like the hosted server
            _bots.Add((p, slot));
            Godot.GD.Print($"host: bot started (pid {p.Id}): --side {slot.Side} --class '{slot.Class}' on UDP {launch.Port}; log {logFile ?? "(Godot's default)"}");
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Godot.GD.PrintErr($"host: a bot could not be started ({launch.Exe}): {e.Message}");
            return false;
        }
    }

    static void Prune()
    {
        for (int i = _bots.Count - 1; i >= 0; i--)
        {
            bool alive;
            try { alive = !_bots[i].Process.HasExited; } catch (InvalidOperationException) { alive = false; }
            if (!alive) { try { _bots[i].Process.Dispose(); } catch (Exception) { } _bots.RemoveAt(i); }
        }
    }

    static void Kill(Process p)
    {
        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        finally { try { p.Dispose(); } catch (Exception) { } }
    }
}

/// <summary>
/// Servers of this install that were not started by this front end (the release's "Host a match on this PC" launcher, or an earlier front end): found by process name
/// and the full path of this install's server executable, and stopped only after both are checked again. Never any other program, never another copy of the server.
/// </summary>
public static class InstallServers
{
    static string ProcessName(string exe) => Path.GetFileNameWithoutExtension(exe);

    static bool IsThisInstall(Process p, string exe)
    {
        try
        {
            if (!string.Equals(p.ProcessName, ProcessName(exe), StringComparison.OrdinalIgnoreCase) || p.HasExited) return false;
            string? path = p.MainModule?.FileName;
            return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException) { return false; }
    }

    /// <summary>The ids of this install's servers running now (any started anywhere).</summary>
    public static List<int> Running(string exe)
    {
        var ids = new List<int>();
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(ProcessName(exe)); } catch (InvalidOperationException) { return ids; }
        foreach (var p in candidates) { using (p) if (IsThisInstall(p, exe)) ids.Add(p.Id); }
        return ids;
    }

    /// <summary>Who holds a port, from its owning process ids: one of this install's servers, else another program.</summary>
    public static PortHolder HolderOf(IReadOnlyList<int> owners, string exe)
    {
        foreach (int id in owners)
        {
            try { using var p = Process.GetProcessById(id); if (IsThisInstall(p, exe)) return PortHolder.ThisInstall; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        }
        return PortHolder.Other;
    }

    /// <summary>Stops this install's servers that this front end did not start (each checked again by name and path just before); how many were stopped.</summary>
    public static int StopAll(string exe)
    {
        int stopped = 0;
        foreach (int id in Running(exe))
        {
            try
            {
                using var p = Process.GetProcessById(id);
                if (!IsThisInstall(p, exe)) continue;   // gone, or the id was reused by something else since the listing
                p.Kill();
                p.WaitForExit(3000);
                stopped++;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        return stopped;
    }
}
