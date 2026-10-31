<#
.SYNOPSIS
  Diagnostic (test aid, not shipped): records every top-level window that becomes visible and every change of the foreground window
  while a script block runs, polling about every 10 ms. Used to prove that test and bot processes never show a window or take focus.
.DESCRIPTION
  . tools\window-watch.ps1; Watch-Windows -Seconds 20 -Label 'headless bot' -Action { ... start something ... } [-All]
  powershell -File tools\window-watch.ps1 -Seconds 20 -Label x -Command '<powershell to run>'

  The result lists, for each window that became visible after the start: the time (ms), the window handle, the owning process (name, id,
  parent chain), class, title, rectangle, whether the rectangle touches a monitor and whether DWM cloaks it (a window on another virtual
  desktop is cloaked). Foreground changes are listed the same way. Nothing is changed on the desktop.
#>
param([int]$Seconds = 0, [string]$Label = '', [string]$Command = '')

if (-not ('SmDiag.WindowWatch' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SmDiag {
public sealed class WindowEvent {
    public long Ms; public string Kind; public long Hwnd; public int Pid; public string Process; public string Parents;
    public string Class; public string Title; public string Rect; public bool OnMonitor; public bool Cloaked;
}
public static class WindowWatch {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PE32 {
        public int dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint f, int p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr s, ref PE32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr s, ref PE32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static Thread _thread; static volatile bool _stop; static List<WindowEvent> _events; static Stopwatch _clock;
    static HashSet<long> _baseline;

    static List<IntPtr> Visible() {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { if (IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    static Dictionary<int, Tuple<int, string>> Snapshot() {
        var map = new Dictionary<int, Tuple<int, string>>();
        IntPtr s = CreateToolhelp32Snapshot(2, 0);
        if (s == (IntPtr)(-1)) return map;
        var e = new PE32 { dwSize = Marshal.SizeOf(typeof(PE32)) };
        if (Process32FirstW(s, ref e)) do { map[e.th32ProcessID] = Tuple.Create(e.th32ParentProcessID, e.szExeFile); } while (Process32NextW(s, ref e));
        CloseHandle(s);
        return map;
    }
    static WindowEvent Describe(string kind, IntPtr h) {
        var ev = new WindowEvent { Ms = _clock.ElapsedMilliseconds, Kind = kind, Hwnd = h.ToInt64() };
        if (h == IntPtr.Zero) { ev.Process = "(none)"; return ev; }
        int pid; GetWindowThreadProcessId(h, out pid); ev.Pid = pid;
        var snap = Snapshot();
        Tuple<int, string> me; ev.Process = snap.TryGetValue(pid, out me) ? me.Item2 : "(exited)";
        var chain = new StringBuilder(); int cur = pid;
        for (int i = 0; i < 6 && snap.TryGetValue(cur, out me) && me.Item1 != 0 && snap.ContainsKey(me.Item1); i++) { cur = me.Item1; chain.Append(" < ").Append(snap[cur].Item2).Append(':').Append(cur); }
        ev.Parents = chain.ToString();
        var sb = new StringBuilder(256); GetClassName(h, sb, 256); ev.Class = sb.ToString();
        sb.Clear(); GetWindowText(h, sb, 256); ev.Title = sb.ToString();
        RECT r; if (GetWindowRect(h, out r)) { ev.Rect = r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T); ev.OnMonitor = MonitorFromRect(ref r, 0) != IntPtr.Zero; }
        int cloaked; ev.Cloaked = DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0;
        return ev;
    }
    public static void Start() {
        _events = new List<WindowEvent>(); _stop = false; _clock = Stopwatch.StartNew();
        _baseline = new HashSet<long>(); foreach (var h in Visible()) _baseline.Add(h.ToInt64());
        IntPtr fg0 = GetForegroundWindow();
        _thread = new Thread(() => {
            var seen = new HashSet<long>(_baseline); IntPtr fg = fg0; var rects = new Dictionary<long, string>();
            while (!_stop) {
                foreach (var h in Visible()) {
                    long key = h.ToInt64();
                    if (seen.Add(key)) { var ev = Describe("visible", h); rects[key] = ev.Rect; lock (_events) _events.Add(ev); continue; }
                    RECT r;
                    if (rects.ContainsKey(key) && GetWindowRect(h, out r)) {
                        string at = r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T);
                        if (at != rects[key]) { rects[key] = at; var ev = Describe("moved", h); lock (_events) _events.Add(ev); }
                    }
                }
                IntPtr now = GetForegroundWindow();
                if (now != fg) { fg = now; lock (_events) _events.Add(Describe("foreground", now)); }
                Thread.Sleep(10);
            }
        }) { IsBackground = true };
        _thread.Start();
    }
    public static WindowEvent[] Stop() { _stop = true; _thread.Join(); lock (_events) return _events.ToArray(); }
}
}
'@
}

# Ours: windows of processes started from this shell (its process tree); the rest of the desktop is listed too (other programs, other agents).
function Watch-Windows([scriptblock]$Action, [int]$Seconds = 15, [string]$Label = '', [int]$Root = $PID, [switch]$All) {
    [SmDiag.WindowWatch]::Start()
    $began = Get-Date
    try { & $Action | Out-Host } finally {
        $left = $Seconds - ((Get-Date) - $began).TotalSeconds
        if ($left -gt 0) { Start-Sleep -Milliseconds ([int]($left * 1000)) }
        $events = [SmDiag.WindowWatch]::Stop()
    }
    $vis = @($events | Where-Object Kind -eq 'visible'); $fg = @($events | Where-Object Kind -eq 'foreground')
    $onScreen = @($vis | Where-Object { $_.OnMonitor -and -not $_.Cloaked })
    $ours = { param($e) $e.Pid -eq $Root -or $e.Parents -match ":$Root(\s|$)" -or $e.Process -match '^(conhost|OpenConsole|WindowsTerminal)\.exe$' }
    $ourVis = @($vis | Where-Object { & $ours $_ }); $ourFg = @($fg | Where-Object { & $ours $_ })
    $ourOn = @($ourVis | Where-Object { $_.OnMonitor -and -not $_.Cloaked })
    Write-Host ("WATCH [{0}] {1} s: OURS (this shell's process tree or a console host): {2} visible window(s), {3} on a monitor and not cloaked, {4} foreground change(s) | whole desktop: {5} visible ({6} on a monitor), {7} foreground change(s)" -f $Label, $Seconds, $ourVis.Count, $ourOn.Count, $ourFg.Count, $vis.Count, $onScreen.Count, $fg.Count)
    $events | Where-Object { $All -or (& $ours $_) } | Format-Table Ms, Kind, Hwnd, Process, Pid, Class, Title, Rect, OnMonitor, Cloaked, Parents -AutoSize -Wrap | Out-String -Width 400 | Write-Host
}

if ($Seconds -gt 0 -and $Command) { Watch-Windows -Action ([scriptblock]::Create($Command)) -Seconds $Seconds -Label $Label }
