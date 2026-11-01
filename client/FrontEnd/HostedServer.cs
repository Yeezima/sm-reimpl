using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using SmContent;

namespace SmClient.FrontEnd;

/// <summary>
/// The dedicated server this client started with "Host a match": one process at most, owned by the client. It is stopped when the client quits (<see cref="Stop"/>,
/// called from the root node's exit and registered on process exit as a backstop) and never anything else is touched: only the <see cref="Process"/> started here
/// is ever killed. Output is drained all the time (a full pipe would stall the server) and only the last lines are kept for a failure message.
/// On Windows the process also joins a Job Object that kills it when this client's last handle to the job closes, which the system does when the client dies for
/// any reason (crash, End Task, kill): no orphaned server keeps the port.
/// </summary>
public static class HostedServer
{
    static Process? _process;
    static HostOptions? _options;
    static bool _ready;
    static bool _exitHooked;
    static bool _guarded;
    static readonly Queue<string> _tail = new();
    static readonly object _lock = new();
    static BotLaunch? _bots;   // fix/connect-timeout: started once the server prints its listening line (HostedBots)
    static int? _exitCode;
    static readonly HostedServerLog Log = new();

    public static HostOptions? Options => _options;
    /// <summary>The exit code of the last server started here once it ended (null while it runs or before any); 3 = its port was in use.</summary>
    public static int? ExitCode { get { lock (_lock) { Refresh(); return _exitCode; } } }

    static void Refresh()
    {
        try { if (_process is { HasExited: true } p) _exitCode = p.ExitCode; } catch (InvalidOperationException) { }
    }
    /// <summary>The id of the server process started here while it runs (test aids and logs); null otherwise.</summary>
    public static int? ProcessId { get { lock (_lock) { try { return _process is { } p && Alive(p) ? p.Id : null; } catch (InvalidOperationException) { return null; } } } }
    public static bool Running { get { lock (_lock) return _process is { } p && Alive(p); } }
    /// <summary>The server printed that it is listening: a client can join.</summary>
    public static bool Ready { get { lock (_lock) return _ready && _process is { } p && Alive(p); } }
    /// <summary>The running server is in the kill-on-close job (Windows): it dies with the client even if the client is killed.</summary>
    public static bool Guarded { get { lock (_lock) return _guarded; } }

    static bool Alive(Process p)
    {
        try { return !p.HasExited; } catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Starts the server; null on success (it is not ready yet: poll <see cref="Ready"/>), else why not. <paramref name="bots"/> (fix/connect-timeout): the bots to
    /// start once it prints its listening line (<see cref="HostedBots"/>); they are stopped with it.
    /// </summary>
    public static string? Start(string exe, HostOptions options, BotLaunch? bots = null)
    {
        lock (_lock)
        {
            if (_process is { } running && Alive(running)) return "A server is already running.";
            DisposeProcess();
            _ready = false;
            _guarded = false;
            _exitCode = null;
            _bots = bots;
            _tail.Clear();
        }
        HostedBots.StopAll();   // bots of an earlier server (they would have left by themselves)
        List<string> args;
        try { args = ServerLaunch.Arguments(options); }
        catch (ArgumentException e) { return e.Message; }
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? "",
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        Process process;
        try
        {
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            // Each handler belongs to its own process: lines of an earlier server (stopped, then restarted) never reach the current one's tail or ready flag.
            process.OutputDataReceived += (_, e) => OnLine(process, e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(process, e.Data);
            process.Exited += (_, _) => { if (IsCurrent(process)) HostedBots.StopAll(); };   // the server is gone: its bots go too (they would leave after 5 s anyway)
            if (!process.Start()) return "The server did not start.";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            return "The server could not be started: " + e.Message;
        }
        bool guarded = WindowsJob.TryAssign(process);
        lock (_lock)
        {
            _process = process;
            _options = options;
            _guarded = guarded;
            if (!_exitHooked) { AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop(); _exitHooked = true; }
        }
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception e) when (e is InvalidOperationException or System.IO.IOException)
        {
            Stop();   // a server whose output cannot be read would fill its pipe and stall: kill it rather than leave it running
            return "The server's output could not be read: " + e.Message;
        }
        return null;
    }

    static bool IsCurrent(Process p) { lock (_lock) return ReferenceEquals(_process, p); }

    static void OnLine(Process source, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        BotLaunch? begin = null;
        lock (_lock)
        {
            if (!ReferenceEquals(_process, source)) return;
            if (line.StartsWith(ServerLaunch.ReadyPrefix, StringComparison.Ordinal) && !_ready)
            {
                _ready = true;
                begin = _bots;
            }
            _tail.Enqueue(line);
            while (_tail.Count > 12) _tail.Dequeue();
        }
        if (begin is not null) HostedBots.Begin(begin);   // outside the lock: HostedBots asks this class whether the server runs
        // Joins, leaves with their reasons, refusals and the server's fault notes in the client's log too (the hosted server has no console or file of its own;
        // fix/stall-keepalive: the lines carry a timestamp, which the old filter did not allow for, so no leave reached the log). HostedServerLog says which lines.
        if (Log.Filter(line) is { } shown) Godot.GD.Print("hosted server: " + shown);
    }

    /// <summary>The last lines the server printed (a reason when it died during start-up).</summary>
    public static string LastLines(int count = 3)
    {
        // Stack frames ("   at ...") are noise in a one-line message: the exception line above them says what happened.
        lock (_lock)
        {
            var lines = _tail.Where(l => !l.TrimStart().StartsWith("at ", StringComparison.Ordinal)).ToList();
            return string.Join(" | ", lines.Skip(Math.Max(0, lines.Count - count)).Select(l => l.Trim()));
        }
    }

    /// <summary>What to tell the player when the server died before it was ready: the exit code and the last lines it printed, with the port hint only for a socket error.</summary>
    /// <remarks>fix/connect-timeout: exit code 3 (its port is in use) gives the plain port message (<see cref="HostStart.PortInUseText"/>), naming who holds the port when
    /// <paramref name="holderOf"/> can tell from the owning process ids.</remarks>
    public static string FailureText(Func<IReadOnlyList<int>, PortHolder>? holderOf = null)
    {
        string code = "";
        bool socket;
        lock (_lock)
        {
            Refresh();
            if (_exitCode == HostStart.ExitPortInUse && _options is { } o)
            {
                var owners = HostStart.PortOwners(o.Port);
                return HostStart.PortInUseText(o.Port, owners.Count == 0 ? PortHolder.Unknown : holderOf?.Invoke(owners) ?? PortHolder.Other);
            }
            if (_exitCode is { } exit) code = $" (exit code {exit})";
            socket = _tail.Any(l => l.Contains("SocketException", StringComparison.Ordinal) || l.Contains("Only one usage of each socket address", StringComparison.Ordinal)
                                    || l.Contains("Address already in use", StringComparison.OrdinalIgnoreCase));
        }
        string lines = LastLines();
        return "The server stopped" + code + (lines.Length > 0 ? ": " + lines : ".") + (socket ? " Is another server already using that UDP port?" : "");
    }

    /// <summary>Stops the server this client started (if any). Safe to call repeatedly and from any thread.</summary>
    public static void Stop()
    {
        HostedBots.StopAll();   // fix/connect-timeout: the server's bots first, so none is left trying to join
        Process? p;
        lock (_lock) { Refresh(); p = _process; _process = null; _ready = false; _guarded = false; _bots = null; }
        if (p is null) return;
        try
        {
            if (Alive(p)) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        finally { try { p.Dispose(); } catch (Exception) { } }
    }

    static void DisposeProcess()
    {
        var p = _process;
        _process = null;
        try { p?.Dispose(); } catch (Exception) { }
    }
}

/// <summary>
/// A Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, created once per client process and never closed by us: the system closes the handle when the
/// client dies and kills every process in the job. Elsewhere, or when anything fails, nothing is assigned and the normal stop on exit applies.
/// </summary>
static class WindowsJob
{
    static IntPtr _job;
    static bool _failed;

    public static bool TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows() || _failed) return false;
        try
        {
            if (_job == IntPtr.Zero && !Create()) { _failed = true; return false; }
            return AssignProcessToJobObject(_job, process.Handle);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _failed = true;
            return false;
        }
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    static bool Create()
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return false;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size)) { CloseHandle(job); return false; }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        _job = job;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
