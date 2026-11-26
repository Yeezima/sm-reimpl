using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd --editor --path mapmaker -- --mm-exit-sm1 mp_tdm_09 --mm-lib &lt;dir&gt; --mm-out &lt;dir&gt; [--mm-exit-wait 30]</c> (set SM_MAPMAKER_CONTROL to a
/// scratch folder): imports the SM1 map for editing, idles, and quits, so the editor's exit with a big map open can be judged by the process exit code (the 2026-11-27 review found an
/// exit crash that only a big imported map shows; the windowed self test cannot see it). Writes exit-result.txt and prints <c>EXIT READY</c> before quitting; a clean run exits 0.
/// </summary>
public sealed class ExitTest(MapmakerPlugin plugin)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-exit-sm1");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
    SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    public async Task Run()
    {
        string outDir = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-exit-" + System.Environment.ProcessId));
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, "exit-result.txt");
        void Log(string s) { GD.Print("EXIT " + s); File.AppendAllText(file, s + System.Environment.NewLine); }
        try
        {
            for (int i = 0; i < 30; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            for (int wait = 0; MapHost.Root is null && wait < 60; wait++) { plugin.EnsureSceneRoot(); for (int i = 0; i < 20; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
            MapHost.Library = new MapLibrary(Arg("--mm-lib", Path.Combine(outDir, "lib")));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            plugin.NewMap("exit-test");
            var r = await plugin.ImportSm1ForEditing(Arg("--mm-exit-sm1", "mp_tdm_09"));
            Log($"import ok={r?.Ok} objects={plugin.Session?.Document.Objects.Count} after {clock.Elapsed.TotalSeconds:0.0} s");
            double idle = double.TryParse(Arg("--mm-exit-wait", "30"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 30;
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (t.Elapsed.TotalSeconds < idle) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            Log($"idled {t.Elapsed.TotalSeconds:0.0} s");
        }
        catch (Exception e) { Log("exception " + e); }
        Log("EXIT READY (quitting; a clean exit returns code 0)");
        Tree.Quit(0);
    }
}
