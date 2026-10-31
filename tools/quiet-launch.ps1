<#
.SYNOPSIS
  Starts test processes so they never show a window or take focus on the owner's desktop (tools/no-focus-steal, owner's request of
  2026-10-31). Dot-source it: . "$RepoRoot\tools\quiet-launch.ps1"; then Start-QuietProcess. Used by godot.cmd, bots.cmd, client.cmd's
  test runs, movie.cmd's test recordings and release.cmd's smoke tests and playtest bots. Test aid, not shipped in the game.
.DESCRIPTION
  Start-QuietProcess -FilePath <exe> -ArgumentList <string[]> | -RawArguments <command line> [-WorkingDirectory <dir>] -StdOut <file> [-StdErr <file>] [-Desktop <name>]
    Starts <exe> with CREATE_NO_WINDOW (no console window, not even for a console program such as Godot's _console.exe wrapper, whose
    child inherits the window-less console), stdin from NUL and stdout / stderr written to files (-StdErr may be the same file). The
    process is created suspended and resumed only after a Process object holds a handle, so .ExitCode, .StartTime and .HasExited work even
    for a process that ends at once. Returns that [System.Diagnostics.Process].
    -Desktop <name>: the process (and every child it starts) runs on a private desktop of the interactive window station
    (CreateDesktop, WinSta0\<name>, created when missing). Its windows exist and render (Vulkan and the movie writer work; screenshots
    are read back from the GPU), but they are never on the desktop the owner sees, never get the foreground and never get his mouse or
    keyboard. The desktop goes away by itself after its last process ends. Measured with tools\window-watch.ps1 (docs/milestones.md).
    With -Desktop, Start-QuietProcess returns once the process tree has a window on that desktop or has ended, and then closes its own
    handle to the desktop (the window holds it from then on); after -WindowWaitSeconds (60) without a window it returns anyway and keeps the
    handle until Wait-QuietProcess sees the process end (or this shell exits). Only the three std handles are inherited by the child
    (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so a caller's pipes never stay open in a long-lived child. Without -Desktop the first window is
    hidden (SW_HIDE); with it windows are shown normally, on the private desktop only.
  Wait-QuietProcess <process> [-Log <file>] [-TimeoutSeconds n]
    Waits for the process and, with -Log, copies the log's new lines to the console as they are written. Returns the exit code
    ($null after a timeout; the process is left running).
  ConvertTo-ArgumentLine <string[]>
    One command line from separate arguments (Windows quoting rules), for the rare caller that needs it.
#>

if (-not ('SmTools.QuietLaunch' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SmTools {
public static class QuietLaunch {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PE32 {
        public int dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint f, int p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr s, ref PE32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr s, ref PE32 e);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktopW(string name, IntPtr dev, IntPtr mode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr desk, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);

    const uint CREATE_NO_WINDOW = 0x08000000, CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, EXTENDED_STARTUPINFO_PRESENT = 0x80000;
    const int STARTF_USESHOWWINDOW = 1, STARTF_USESTDHANDLES = 0x100;
    const uint GENERIC_ALL = 0x10000000, GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;
    static readonly IntPtr INVALID = (IntPtr)(-1);

    static IntPtr Inheritable(string path, bool write) {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true };
        // write: CREATE_ALWAYS; read (NUL): OPEN_EXISTING. Shared for read, write and delete so a tail can follow the log.
        IntPtr h = CreateFileW(path, write ? GENERIC_WRITE : GENERIC_READ, 7, ref sa, write ? 2u : 3u, 0x80, IntPtr.Zero);
        if (h == INVALID) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        return h;
    }

    /// <summary>Creates the process suspended with no console window: stdin NUL, stdout / stderr into files, and ONLY those three handles
    /// inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST: a caller's pipes never leak into a long-lived child, so `bots.cmd | Out-Null` returns).
    /// Without a desktop the first window is hidden (SW_HIDE); with one the process runs on that private desktop (created when missing)
    /// and shows its windows normally there. Returns the id with thread, process and desktop handles; nothing leaks when it throws.</summary>
    public static int Create(string exe, string args, string dir, string outFile, string errFile, string desktop, out IntPtr thread, out IntPtr process, out IntPtr desk) {
        thread = IntPtr.Zero; process = IntPtr.Zero; desk = IntPtr.Zero;
        IntPtr o = IntPtr.Zero, e = IntPtr.Zero, i = IntPtr.Zero, list = IntPtr.Zero, handles = IntPtr.Zero;
        bool listReady = false, ok = false;
        try {
            if (!string.IsNullOrEmpty(desktop)) {
                desk = CreateDesktopW(desktop, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
                if (desk == IntPtr.Zero) {
                    var w = new Win32Exception(Marshal.GetLastWin32Error());
                    throw new InvalidOperationException("Could not create the private test desktop 'WinSta0\\" + desktop + "' (Win32 error " + w.NativeErrorCode + ": " + w.Message + "). Windowed test runs need it; nothing was started.");
                }
            }
            o = Inheritable(outFile, true);
            e = string.Equals(errFile, outFile, StringComparison.OrdinalIgnoreCase) ? o : Inheritable(errFile, true);
            i = Inheritable("NUL", false);
            var own = new List<IntPtr> { i, o }; if (e != o) own.Add(e);   // distinct handles only
            handles = Marshal.AllocHGlobal(IntPtr.Size * own.Count);
            for (int k = 0; k < own.Count; k++) Marshal.WriteIntPtr(handles, k * IntPtr.Size, own[k]);
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            listReady = true;
            if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (IntPtr)(IntPtr.Size * own.Count), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");
            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = i; si.StartupInfo.hStdOutput = o; si.StartupInfo.hStdError = e;
            if (string.IsNullOrEmpty(desktop)) { si.StartupInfo.dwFlags |= STARTF_USESHOWWINDOW; si.StartupInfo.wShowWindow = 0; /* SW_HIDE */ }
            else si.StartupInfo.lpDesktop = "WinSta0\\" + desktop;   // shown normally there: never on the owner's desktop
            si.lpAttributeList = list;
            var cmd = new StringBuilder("\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args), 32767);
            PROCESS_INFORMATION pi;
            if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, true, CREATE_NO_WINDOW | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, string.IsNullOrEmpty(dir) ? null : dir, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess " + exe);
            thread = pi.hThread; process = pi.hProcess; ok = true;
            return pi.dwProcessId;
        } finally {
            if (listReady) DeleteProcThreadAttributeList(list);
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (i != IntPtr.Zero) CloseHandle(i);
            if (e != IntPtr.Zero && e != o) CloseHandle(e);
            if (o != IntPtr.Zero) CloseHandle(o);
            if (!ok && desk != IntPtr.Zero) { CloseDesktop(desk); desk = IntPtr.Zero; }
        }
    }
    /// <summary>Resumes the suspended process; when that fails the process is killed and an error thrown. The thread handle is closed either way.</summary>
    public static void Resume(IntPtr thread, IntPtr process) {
        uint r = ResumeThread(thread);
        int err = Marshal.GetLastWin32Error();
        CloseHandle(thread);
        if (r == 0xFFFFFFFF) { TerminateProcess(process, 1); throw new Win32Exception(err, "ResumeThread"); }
    }
    public static void Abort(IntPtr thread, IntPtr process) { TerminateProcess(process, 1); CloseHandle(thread); }
    public static void Close(IntPtr handle) { if (handle != IntPtr.Zero) CloseHandle(handle); }
    public static void CloseDesk(IntPtr desk) { if (desk != IntPtr.Zero) CloseDesktop(desk); }
    /// <summary>The process and its descendants.</summary>
    public static HashSet<int> Tree(int root) {
        var parent = new Dictionary<int, int>();
        IntPtr s = CreateToolhelp32Snapshot(2, 0);
        if (s != (IntPtr)(-1)) {
            var e = new PE32 { dwSize = Marshal.SizeOf(typeof(PE32)) };
            if (Process32FirstW(s, ref e)) do { parent[e.th32ProcessID] = e.th32ParentProcessID; } while (Process32NextW(s, ref e));
            CloseHandle(s);
        }
        var tree = new HashSet<int> { root };
        for (bool grew = true; grew; ) { grew = false; foreach (var kv in parent) if (!tree.Contains(kv.Key) && tree.Contains(kv.Value) && kv.Key != kv.Value) { tree.Add(kv.Key); grew = true; } }
        return tree;
    }
    /// <summary>True once a window of the process tree exists on the desktop.</summary>
    public static bool HasWindow(IntPtr desk, int root) {
        var tree = Tree(root); bool found = false;
        EnumDesktopWindows(desk, (h, l) => { int pid; GetWindowThreadProcessId(h, out pid); if (tree.Contains(pid)) { found = true; return false; } return true; }, IntPtr.Zero);
        return found;
    }
}
}
'@
}

function ConvertTo-ArgumentLine([string[]]$Arguments) {
    # CommandLineToArgvW rules: quote an argument with spaces, tabs or quotes; double the backslashes before a quote.
    ($Arguments | ForEach-Object {
        $a = [string]$_
        if ($a.Length -gt 0 -and $a -notmatch '[\s"]') { $a }
        else { '"' + ($a -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
    }) -join ' '
}

# Private desktops this shell still holds open, by process id (a desktop whose process tree has no window yet: kept until the process ends).
if (-not $script:QuietDesktops) { $script:QuietDesktops = @{} }

function Start-QuietProcess {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$ArgumentList = @(), [string]$WorkingDirectory = '', [Parameter(Mandatory)][string]$StdOut,
          [string]$StdErr = '', [string]$Desktop = '', [int]$WindowWaitSeconds = 60, [string]$RawArguments)
    $exe = (Resolve-Path -LiteralPath $FilePath).ProviderPath
    if (-not $StdErr) { $StdErr = $StdOut }
    foreach ($f in $StdOut, $StdErr) { $d = Split-Path -Parent ([IO.Path]::GetFullPath($f)); if ($d) { New-Item -ItemType Directory -Force -Path $d | Out-Null } }
    $out = [IO.Path]::GetFullPath($StdOut); $err = [IO.Path]::GetFullPath($StdErr)
    $dir = if ($WorkingDirectory) { (Resolve-Path -LiteralPath $WorkingDirectory).ProviderPath } else { (Get-Location).ProviderPath }
    $thread = [IntPtr]::Zero; $handle = [IntPtr]::Zero; $desk = [IntPtr]::Zero
    $line = if ($PSBoundParameters.ContainsKey('RawArguments')) { $RawArguments } else { ConvertTo-ArgumentLine $ArgumentList }   # -RawArguments: a command line as is (cmd.exe /c "...")
    try { $id = [SmTools.QuietLaunch]::Create($exe, $line, $dir, $out, $err, $Desktop, [ref]$thread, [ref]$handle, [ref]$desk) }
    catch { throw ($_.Exception.InnerException, $_.Exception | Where-Object { $_ } | Select-Object -First 1).Message }
    try {
        $p = [Diagnostics.Process]::GetProcessById($id)
        $null = $p.Handle        # a handle of its own: ExitCode and StartTime stay readable after the process ends
    } catch { [SmTools.QuietLaunch]::Abort($thread, $handle); [SmTools.QuietLaunch]::Close($handle); [SmTools.QuietLaunch]::CloseDesk($desk); throw }
    try { [SmTools.QuietLaunch]::Resume($thread, $handle) }       # kills the process when it cannot be resumed
    catch { [SmTools.QuietLaunch]::Close($handle); [SmTools.QuietLaunch]::CloseDesk($desk); throw }
    [SmTools.QuietLaunch]::Close($handle)
    if ($desk -ne [IntPtr]::Zero) {
        # Keep the private desktop open until the process tree has put a window on it (the window then holds the desktop) or has ended.
        # After -WindowWaitSeconds this returns anyway but the handle stays open (Wait-QuietProcess, or this shell's exit, closes it).
        $until = (Get-Date).AddSeconds($WindowWaitSeconds)
        while (-not $p.HasExited -and (Get-Date) -lt $until -and -not [SmTools.QuietLaunch]::HasWindow($desk, $id)) { Start-Sleep -Milliseconds 100 }
        if ($p.HasExited -or [SmTools.QuietLaunch]::HasWindow($desk, $id)) { [SmTools.QuietLaunch]::CloseDesk($desk) }
        else { $script:QuietDesktops[$id] = $desk; Write-Warning "pid ${id}: no window on the private desktop after $WindowWaitSeconds s; its desktop stays open until it ends" }
    }
    return $p
}

function Wait-QuietProcess {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process, [string]$Log = '', [int]$TimeoutSeconds = 0)
    $state = @{ Pos = 0L; Decoder = [Text.Encoding]::UTF8.GetDecoder() }   # one decoder: a character split between two reads stays whole
    $copy = {
        if (-not $Log -or -not (Test-Path -LiteralPath $Log)) { return }
        try {
            $fs = [IO.File]::Open($Log, 'Open', 'Read', 'ReadWrite, Delete')
            try {
                if ($fs.Length -gt $state.Pos) {
                    $null = $fs.Seek($state.Pos, 'Begin')
                    $buf = New-Object byte[] ($fs.Length - $state.Pos)
                    $n = $fs.Read($buf, 0, $buf.Length); $state.Pos += $n
                    $chars = New-Object char[] ($state.Decoder.GetCharCount($buf, 0, $n))
                    $c = $state.Decoder.GetChars($buf, 0, $n, $chars, 0)
                    [Console]::Out.Write($chars, 0, $c)
                }
            } finally { $fs.Dispose() }
        } catch { }
    }
    $until = if ($TimeoutSeconds -gt 0) { (Get-Date).AddSeconds($TimeoutSeconds) } else { [datetime]::MaxValue }
    while (-not $Process.WaitForExit(250)) {
        & $copy
        if ((Get-Date) -gt $until) { return $null }
    }
    $Process.WaitForExit()
    & $copy
    if ($script:QuietDesktops.ContainsKey($Process.Id)) { [SmTools.QuietLaunch]::CloseDesk($script:QuietDesktops[$Process.Id]); $script:QuietDesktops.Remove($Process.Id) }
    return $Process.ExitCode
}