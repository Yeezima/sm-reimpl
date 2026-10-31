using System.Runtime.InteropServices;

namespace SmLauncher;

/// <summary>
/// Runs test windows on a private Win32 desktop of the interactive window station (WinSta0\sm-reimpl-test, the one tools\quiet-launch.ps1 uses), so they
/// are never on the desktop the owner sees, never take the foreground and never get his mouse or keyboard (owner, 2026-10-31: the off-screen test windows
/// sometimes took the foreground despite ShowWithoutActivation). Windows there still lay out, paint and answer PrintWindow and DrawToBitmap. Used by
/// --layout-check, --row-check, --screenshot and SmLauncher.UiTests. When the desktop cannot be created or entered the windows are made on the current
/// desktop as before (off-screen, not activated), and the caller is told.
/// </summary>
static class PrivateDesktop
{
    public const string Name = "sm-reimpl-test";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateThread(IntPtr security, nuint stack, IntPtr start, IntPtr param, uint flags, out uint id);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    const uint GENERIC_ALL = 0x10000000, COINIT_APARTMENTTHREADED = 0x2, INFINITE = 0xFFFFFFFF;

    delegate uint ThreadStart(IntPtr param);

    /// <summary>
    /// Runs <paramref name="body"/> on a new thread that is on the private desktop and a single-threaded apartment (what Windows Forms needs), waits for it
    /// and passes its exception on. The thread is created natively: a thread must enter the desktop before it owns any window, and a .NET thread started as
    /// STA can already own COM's hidden apartment window (in the test host SetThreadDesktop then fails with ERROR_BUSY), so the desktop comes first and the
    /// apartment after it. When the desktop is not available, <paramref name="note"/> is told and the body runs on the current desktop.
    /// </summary>
    public static T Run<T>(Func<T> body, Action<string>? note = null)
    {
        T result = default!;
        Exception? error = null;
        ThreadStart start = _ =>
        {
            try
            {
                IntPtr desk = CreateDesktopW(Name, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);       // opens it when it exists
                if (desk == IntPtr.Zero || !SetThreadDesktop(desk))
                {
                    int code = Marshal.GetLastWin32Error();
                    if (desk != IntPtr.Zero) CloseDesktop(desk);
                    note?.Invoke($"private desktop {Name} not available (error {code}): the test windows are made off-screen on the current desktop");
                }
                // (a desktop handle in use by a thread cannot be closed; it goes when the process ends, the desktop when nothing uses it any more)
                int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                try { result = body(); }
                finally { if (hr >= 0) CoUninitialize(); }
            }
            catch (Exception e) { error = e; }
            return 0;
        };
        IntPtr thread = CreateThread(IntPtr.Zero, 0, Marshal.GetFunctionPointerForDelegate(start), IntPtr.Zero, 0, out _);
        if (thread == IntPtr.Zero) throw new InvalidOperationException($"could not start the test window thread (error {Marshal.GetLastWin32Error()})");
        WaitForSingleObject(thread, INFINITE);
        CloseHandle(thread);
        GC.KeepAlive(start);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}
