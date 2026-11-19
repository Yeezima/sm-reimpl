using System.Diagnostics;
using System.Globalization;
using SmContent.Extras.Warfront;
using SmSim;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Maps;

namespace SmServer;

/// <summary>
/// The dedicated server's map warm-up child (<c>--warfront-prebuild</c>, started by <see cref="WarfrontSupervisor"/> at low priority after the first stage has run for a while): builds every Warfront map slot's layout and walking graph
/// into the shared cache (<see cref="WarfrontMaps.Prebuild"/>, the travel wait's own work), one map at a time, so the first deployment to a map reads a warm cache instead of waiting 100 to 180 s. A warm map costs a read of its files.
/// It binds nothing, ends with 0 when every map is done (1 when some failed; the supervisor never restarts it) and stops between maps when the supervisor is gone or asks to stop. Cache writes are temp file plus rename, so a stage
/// preparing the same map at the same moment can never leave a reader a partial file (either side may win).
/// </summary>
static class WarfrontPrebuildRun
{
    /// <summary>The map names the warm-up covers: every Warfront map: the nine lane slots, the HQ and the fortress (11).</summary>
    public static IReadOnlyList<string> Names => WarfrontMaps.AllNames;

    public static int Run(SimConfig config)
    {
        bool leave = false;
        try
        {
            System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; leave = true; });
        }
        catch (Exception e) when (e is PlatformNotSupportedException or NotSupportedException) { }
        string stopFile = Path.Combine(WarfrontRunFiles.Dir, WarfrontRunFiles.StopFile);
        return Run(Names, (name, progress) => WarfrontMaps.Prebuild(name, config, progress),
            () => leave || RotationSupervisor.SupervisorGone() || File.Exists(stopFile), Console.WriteLine,
            () => { using var p = Process.GetCurrentProcess(); return p.PeakWorkingSet64; });
    }

    /// <summary>The loop, injectable for the tests: <paramref name="prebuild"/> builds one map and returns its note (a throw is logged and the next map goes on), <paramref name="shouldStop"/> is asked before each map.</summary>
    public static int Run(IReadOnlyList<string> names, Func<string, Action<int, string>?, string> prebuild, Func<bool> shouldStop, Action<string> log, Func<long>? peakBytes = null)
    {
        var total = Stopwatch.StartNew();
        int done = 0, failed = 0;
        foreach (string name in names)
        {
            if (shouldStop()) { log(WarfrontSupervisor.Stamp($"map warm-up: stopped before {name} ({done} of {names.Count} done)")); return failed == 0 ? 0 : 1; }
            var watch = Stopwatch.StartNew();
            try
            {
                string note = prebuild(name, null);
                done++;
                log(WarfrontSupervisor.Stamp(string.Create(CultureInfo.InvariantCulture, $"map warm-up [{done}/{names.Count}] {note}; {watch.Elapsed.TotalSeconds:0.#} s")));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failed++;
                log(WarfrontSupervisor.Stamp($"map warm-up: {name} failed ({e.GetType().Name}: {e.Message}); the first deployment there builds it"));
            }
        }
        string peak = peakBytes is null ? "" : string.Create(CultureInfo.InvariantCulture, $", peak working set {peakBytes() / (1024.0 * 1024.0):0} MB");
        log(WarfrontSupervisor.Stamp(string.Create(CultureInfo.InvariantCulture, $"map warm-up finished: {done} of {names.Count} maps, {failed} failed, {total.Elapsed.TotalSeconds:0} s in all{peak}")));
        return failed == 0 ? 0 : 1;
    }
}
