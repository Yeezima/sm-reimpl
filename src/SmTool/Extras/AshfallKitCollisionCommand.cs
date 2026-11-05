using System.Globalization;
using System.Numerics;
using System.Text;
using SmSim.Extras.Musou.Ashfall;

namespace SmTool;

/// <summary>
/// <c>smtool ashfall-kit-collision [--source dir] [--res 40] [--max-boxes 32] [--out file] [--report file] [--obj-dir dir]</c>: reads each kit model's GLB from the owner's private library (read only),
/// fits collision boxes (<see cref="AshfallKitFit"/>) and writes the numbers as <c>AshfallKitCollision.g.cs</c> (committed), a Markdown report and one OBJ of boxes per model (private cache).
/// </summary>
static class AshfallKitCollisionCommand
{
    static readonly string[] Walkable = ["AC-005", "AC-007", "AC-008", "AC-009"];

    public static int Run(string[] rest)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? source = null, outPath = null, reportPath = null, objDir = null;
        int res = AshfallKitFit.DefaultResolution, maxBoxes = AshfallKitFit.DefaultMaxBoxes;
        float fill = AshfallKitFit.DefaultFillTolerance;
        for (int i = 0; i < rest.Length; i++)
        {
            string a = rest[i];
            bool hasVal = i + 1 < rest.Length;
            if (a == "--source" && hasVal) source = rest[++i];
            else if (a == "--res" && hasVal && int.TryParse(rest[i + 1], out res)) i++;
            else if (a == "--max-boxes" && hasVal && int.TryParse(rest[i + 1], out maxBoxes)) i++;
            else if (a == "--fill" && hasVal && float.TryParse(rest[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out fill)) i++;
            else if (a == "--out" && hasVal) outPath = rest[++i];
            else if (a == "--report" && hasVal) reportPath = rest[++i];
            else if (a == "--obj-dir" && hasVal) objDir = rest[++i];
            else { Console.Error.WriteLine($"ashfall-kit-collision: bad argument \"{a}\" (usage: ashfall-kit-collision [--source <dir>] [--res 40] [--max-boxes 32] [--fill 0.15] [--out <file>] [--report <file>] [--obj-dir <dir>])"); return 2; }
        }
        if (res < 4 || maxBoxes < 1) { Console.Error.WriteLine("ashfall-kit-collision: --res must be >= 4 and --max-boxes >= 1"); return 2; }
        source ??= Path.Combine(local, "sm-reimpl", AshfallKit.PrivateFolder, "source");
        outPath ??= Path.Combine(FindRepoRoot(), "src", "SmSim", "Extras", "Musou", "Ashfall", "AshfallKitCollision.g.cs");
        string previews = Path.Combine(local, "sm-reimpl", "cache", "previews", "ashfall-kit");
        reportPath ??= Path.Combine(previews, "collision-report.md");
        objDir ??= Path.Combine(previews, "collision-obj");
        if (!Directory.Exists(source)) { Console.Error.WriteLine($"ashfall-kit-collision: source folder not found: {source}"); return 1; }
        Directory.CreateDirectory(objDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        var rows = new List<Row>();
        foreach (var model in AshfallKit.Models.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            string file = Path.Combine(source, model.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) { Console.Error.WriteLine($"ashfall-kit-collision: missing {model.Id}: {file}"); return 1; }
            var mesh = GlbReader.Read(file);
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var p in mesh.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            float diff = Math.Max(Vector3.Abs(min - model.RawMin).Length(), Vector3.Abs(max - model.RawMax).Length());
            string warn = "";
            if (Vector3.Abs(min - model.RawMin).X > 0.001f || Vector3.Abs(min - model.RawMin).Y > 0.001f || Vector3.Abs(min - model.RawMin).Z > 0.001f
                || Vector3.Abs(max - model.RawMax).X > 0.001f || Vector3.Abs(max - model.RawMax).Y > 0.001f || Vector3.Abs(max - model.RawMax).Z > 0.001f)
            {
                warn = $"bounds differ from the catalogue by {diff * 1000f:F1} mm";
                Console.Error.WriteLine($"warning: {model.Id}: {warn}");
            }
            var fit = AshfallKitFit.Fit(mesh.Positions, mesh.Indices, model.RawMin, model.RawMax, res, maxBoxes, fill);
            var reasons = Cleanup(model, fit.Metrics);
            rows.Add(new Row(model, fit, reasons, warn));
            File.WriteAllText(Path.Combine(objDir, model.Id + "-boxes.obj"), Obj(model.Id, fit.Boxes), new UTF8Encoding(false));
            Console.WriteLine($"{model.Id}: {fit.Boxes.Count,2} boxes  IoU {fit.Metrics.Iou:F3}  dropped {fit.Metrics.DroppedPercent:F2} %{(reasons.Count > 0 ? "  CLEANUP: " + string.Join("; ", reasons) : "")}");
        }
        File.WriteAllText(outPath, Generated(rows, res, maxBoxes, fill), new UTF8Encoding(false));
        File.WriteAllText(reportPath, Report(rows, res, maxBoxes, fill), new UTF8Encoding(false));
        Console.WriteLine($"ashfall-kit-collision: {rows.Count} models, {rows.Sum(r => r.Fit.Boxes.Count)} boxes -> {outPath}");
        Console.WriteLine($"  report {reportPath}\n  obj    {objDir}");
        return 0;
    }

    sealed record Row(AshfallKitModel Model, AshfallKitFitResult Fit, List<string> Reasons, string Warn);

    static List<string> Cleanup(AshfallKitModel m, AshfallKitFitMetrics f)
    {
        var r = new List<string>();
        if (f.Iou < 0.80) r.Add($"IoU {f.Iou:F2} below 0.80");
        if (f.CapReached && f.Iou < 0.90) r.Add($"cap reached with IoU {f.Iou:F2} below 0.90");
        if (f.OverCap) r.Add($"{f.BoxCount} boxes, over the cap (fill tolerance or an opening kept them apart)");
        if (m.Review) r.Add("manifest flags the geometry for review");
        if (Walkable.Contains(m.Id))
        {
            float h = m.RawMax.Y - m.RawMin.Y;
            if (!f.WalkableTops.Any(t => (t - m.RawMin.Y) / h > 0.30f)) r.Add("no walkable top above 30 % of its height");
        }
        return r;
    }

    static string F(float v) { v = MathF.Round(v, 4); if (v == 0f) v = 0f; return v.ToString("0.0000", CultureInfo.InvariantCulture) + "f"; }
    static string D(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    static string Generated(List<Row> rows, int res, int maxBoxes, float fill)
    {
        var sb = new StringBuilder();
        sb.Append("// <auto-generated>\n");
        sb.Append("// Generated by `smtool ashfall-kit-collision` from the owner's Ashfall kit library (numbers only, no asset bytes). Do not edit by hand.\n");
        sb.Append($"// Parameters: resolution {res} cells on the longest axis, at most {maxBoxes} boxes, fill tolerance {fill * 100:F0} %.\n");
        sb.Append("// Boxes are axis-aligned in each model's raw glTF frame (Y up), min/max in metres; see AshfallKitFit for how they are made.\n");
        sb.Append("// </auto-generated>\n");
        sb.Append("using System.Numerics;\n\nnamespace SmSim.Extras.Musou.Ashfall;\n\n");
        sb.Append("/// <summary>Per-model collision boxes of the Ashfall kit (generated; see the header).</summary>\npublic static partial class AshfallKitCollision\n{\n");
        sb.Append("    sealed record Entry((Vector3 Min, Vector3 Max)[] Boxes, AshfallKitFitMetrics Metrics, bool NeedsCleanup, string Reason);\n\n");
        sb.Append("    static readonly Dictionary<string, Entry> Data = new()\n    {\n");
        foreach (var r in rows)
        {
            var m = r.Fit.Metrics;
            sb.Append($"        [\"{r.Model.Id}\"] = new(\n            [\n");
            foreach (var (mn, mx) in r.Fit.Boxes) sb.Append($"                (new({F(mn.X)}, {F(mn.Y)}, {F(mn.Z)}), new({F(mx.X)}, {F(mx.Y)}, {F(mx.Z)})),\n");
            sb.Append("            ],\n");
            sb.Append($"            new({m.SolidCells}, {m.CoveredCells}, {D(m.Iou)}, {D(m.DroppedPercent)}, {m.DroppedBoxes}, {m.BoxCount}, {(m.CapReached ? "true" : "false")}, {(m.OverCap ? "true" : "false")}, [{string.Join(", ", m.WalkableTops.Select(F))}], {m.Nx}, {m.Ny}, {m.Nz}, {F(m.CellSize)}),\n");
            sb.Append($"            {(r.Reasons.Count > 0 ? "true" : "false")}, \"{string.Join("; ", r.Reasons)}\"),\n");
        }
        sb.Append("    };\n\n");
        sb.Append("    /// <summary>Every model id that has a box set.</summary>\n    public static IEnumerable<string> Ids => Data.Keys;\n\n");
        sb.Append("    /// <summary>The model's boxes in its raw glTF frame (min, max).</summary>\n    public static IReadOnlyList<(Vector3 Min, Vector3 Max)> Boxes(string id) => Data[id].Boxes;\n\n");
        sb.Append("    /// <summary>What the fit measured (cells, IoU, dropped dust, walkable tops).</summary>\n    public static AshfallKitFitMetrics Metrics(string id) => Data[id].Metrics;\n\n");
        sb.Append("    /// <summary>The boxes are doubtful and want a manual look (see <see cref=\"CleanupReason\"/>).</summary>\n    public static bool NeedsCleanup(string id) => Data[id].NeedsCleanup;\n\n");
        sb.Append("    public static string CleanupReason(string id) => Data[id].Reason;\n}\n");
        return sb.ToString();
    }

    static string Report(List<Row> rows, int res, int maxBoxes, float fill)
    {
        var sb = new StringBuilder();
        sb.Append($"# Ashfall kit collision report\n\nResolution {res}, at most {maxBoxes} boxes, fill tolerance {fill * 100:F0} %.\n\n");
        sb.Append("| id | name | boxes | IoU | dropped % | cap reached | walkable tops (fraction of height) | flags |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var r in rows)
        {
            var m = r.Fit.Metrics; float h = r.Model.RawMax.Y - r.Model.RawMin.Y;
            string tops = m.WalkableTops.Length == 0 ? "none" : string.Join(", ", m.WalkableTops.Select(t => ((t - r.Model.RawMin.Y) / h).ToString("0.00", CultureInfo.InvariantCulture)));
            string flags = string.Join("; ", r.Reasons.Concat(r.Warn.Length > 0 ? [r.Warn] : []));
            sb.Append($"| {r.Model.Id} | {r.Model.Name} | {m.BoxCount} | {m.Iou:F3} | {m.DroppedPercent:F2} | {(m.CapReached ? (m.OverCap ? "yes, over" : "yes") : "no")} | {tops} | {flags} |\n");
        }
        sb.Append($"\nNeeds cleanup: {rows.Count(r => r.Reasons.Count > 0)} of {rows.Count}.\n");
        return sb.ToString();
    }

    static string Obj(string id, IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes)
    {
        var sb = new StringBuilder($"# {id} collision boxes, raw glTF frame\n");
        int b = 0;
        foreach (var (mn, mx) in boxes)
        {
            sb.Append($"o box{b}\n");
            foreach (int k in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
            {
                float x = (k & 1) == 0 ? mn.X : mx.X, y = (k & 2) == 0 ? mn.Y : mx.Y, z = (k & 4) == 0 ? mn.Z : mx.Z;
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"v {x:0.####} {y:0.####} {z:0.####}\n"));
            }
            int o = b * 8 + 1;
            foreach (var q in new[] { (0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5) })
                sb.Append($"f {o + q.Item1} {o + q.Item2} {o + q.Item3} {o + q.Item4}\n");
            b++;
        }
        return sb.ToString();
    }

    /// <summary>The repository root by walking up from the current folder to SmReimpl.slnx (falls back to the current folder).</summary>
    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }
}
