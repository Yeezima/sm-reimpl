using System.Diagnostics;
using SmLauncher.Core;

namespace SmLauncher;

/// <summary>Starts the Godot editor of the MAPMAKER button (<see cref="MapmakerSetup"/>). Test runs and the release smoke test never call <see cref="StartEditor"/>: they use <c>--mapmaker-check</c> and a headless editor.</summary>
static class MapmakerStart
{
    static ProcessStartInfo Info(MapmakerLaunch l, bool hidden)
    {
        var psi = new ProcessStartInfo(l.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = hidden,
            WorkingDirectory = l.WorkingDirectory,
        };
        foreach (var a in l.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in l.Environment) psi.Environment[k] = v;
        return psi;
    }

    /// <summary>The editor in a visible window (this is for the tester, unlike the hidden test runs). The caller disposes the process.</summary>
    public static Process StartEditor(MapmakerLaunch launch) =>
        Process.Start(Info(launch, hidden: false)) ?? throw new InvalidOperationException("The Godot editor could not be started.");

    /// <summary>The first-time import: the editor with no window, quitting by itself. Returns its exit code (-1 over the time limit). Cancelling ends this process only (by its id).</summary>
    public static async Task<int> RunHiddenAsync(MapmakerLaunch launch, TimeSpan limit, CancellationToken ct)
    {
        using var p = Process.Start(Info(launch, hidden: true)) ?? throw new InvalidOperationException("The Godot editor could not be started.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(limit);
        try { await p.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return -1;      // over the time limit: carry on, the window itself imports whatever is left
        }
        return p.ExitCode;
    }
}
