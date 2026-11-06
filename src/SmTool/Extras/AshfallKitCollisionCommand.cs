using System.Globalization;
using System.Numerics;
using System.Text;
using SmSim.Extras.Musou.Ashfall;

namespace SmTool;

/// <summary>
/// <c>smtool ashfall-kit-collision [--source dir] [--out file] [--report file] [--obj-dir dir] [--views id,id|all]</c>: reads each kit model's GLB from the owner's private library (read only),
/// fits collision boxes through the quality gate (<see cref="AshfallKitFit.FitGated"/>: per model the fewest boxes among the gate's settings that pass) and writes the numbers as <c>AshfallKitCollision.g.cs</c>
/// (committed, with each model's gate measure), a Markdown report and one OBJ of boxes per model (private cache). <c>--views</c> also writes a PNG per model (front and side: the model, the boxes, the air in the boxes).
/// Exit code 3 when a used model (every model but <see cref="AshfallKitFit.Gate.ReportOnlyIds"/>) fails the gate; the files are still written so the report can be read.
/// </summary>
static class AshfallKitCollisionCommand
{
    static readonly IReadOnlyList<string> Walkable = AshfallKitFit.WalkableIds;

    public static int Run(string[] rest)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? source = null, outPath = null, reportPath = null, objDir = null;
        var views = new HashSet<string>(StringComparer.Ordinal);
        bool allViews = false;
        string[]? only = null;
        for (int i = 0; i < rest.Length; i++)
        {
            string a = rest[i];
            bool hasVal = i + 1 < rest.Length;
            if (a == "--source" && hasVal) source = rest[++i];
            else if (a == "--out" && hasVal) outPath = rest[++i];
            else if (a == "--report" && hasVal) reportPath = rest[++i];
            else if (a == "--obj-dir" && hasVal) objDir = rest[++i];
            else if (a == "--only" && hasVal) only = [.. rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)];
            else if (a == "--views" && hasVal) { string v = rest[++i]; if (v == "all") allViews = true; else views.UnionWith(v.Split(',', StringSplitOptions.RemoveEmptyEntries)); }
            else { Console.Error.WriteLine($"ashfall-kit-collision: bad argument \"{a}\" (usage: ashfall-kit-collision [--source <dir>] [--out <file>] [--report <file>] [--obj-dir <dir>] [--views <id,id|all>] [--only <id,id> --out <file>])"); return 2; }
        }
        if (only != null && outPath == null) { Console.Error.WriteLine("ashfall-kit-collision: --only needs --out (it would write a partial AshfallKitCollision.g.cs)"); return 2; }
        source ??= Path.Combine(local, "sm-reimpl", AshfallKit.PrivateFolder, "source");
        outPath ??= Path.Combine(FindRepoRoot(), "src", "SmSim", "Extras", "Musou", "Ashfall", "AshfallKitCollision.g.cs");
        string previews = Path.Combine(local, "sm-reimpl", "cache", "previews", "ashfall-kit");
        reportPath ??= Path.Combine(previews, "collision-report.md");
        objDir ??= Path.Combine(previews, "collision-obj");
        if (!Directory.Exists(source)) { Console.Error.WriteLine($"ashfall-kit-collision: source folder not found: {source}"); return 1; }
        Directory.CreateDirectory(objDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var committed = new HashSet<string>(AshfallKitCollision.Ids, StringComparer.Ordinal);

        var rows = new List<Row>();
        foreach (var model in AshfallKit.Models.Where(m => only == null || only.Contains(m.Id)).OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            string file = Path.Combine(source, model.File.Replace('/', Path.DirectorySeparatorChar));
            // a model made for the project lives only under processed\ (the rubble props): the library root's processed folder beside source
            string processed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "processed", model.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file) && File.Exists(processed)) file = processed;
            if (!File.Exists(file)) { Console.Error.WriteLine($"ashfall-kit-collision: missing {model.Id}: {file}"); return 1; }
            var mesh = GlbReader.Read(file);
            // The model's axis correction (AC-071 lies flat): bounds, fit and measure all work in the corrected frame.
            if (model.ImportFix != AshfallKitImportFix.None)
                for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] = model.FileToModel(mesh.Positions[i]);
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
            bool walk = Walkable.Contains(model.Id);
            var gated = AshfallKitFit.FitGated(mesh.Positions, mesh.Indices, model.RawMin, model.RawMax, walk);
            // The fit committed before this run, measured the same way (for the report's before/after).
            AshfallKitFitQuality? before = committed.Contains(model.Id)
                ? AshfallKitFit.Measure(mesh.Positions, mesh.Indices, model.RawMin, model.RawMax, AshfallKitCollision.Boxes(model.Id), AshfallKitCollision.Metrics(model.Id).CellSize, gated.Openings) : null;
            int beforeBoxes = committed.Contains(model.Id) ? AshfallKitCollision.Boxes(model.Id).Count : 0;
            var reasons = Cleanup(model, gated);
            bool reportOnly = AshfallKitFit.Gate.ReportOnlyIds.Contains(model.Id);
            rows.Add(new Row(model, gated, reasons, warn, before, beforeBoxes, reportOnly));
            File.WriteAllText(Path.Combine(objDir, model.Id + "-boxes.obj"), Obj(model.Id, gated.Fit.Boxes), new UTF8Encoding(false));
            if (allViews || views.Contains(model.Id))
                File.WriteAllBytes(Path.Combine(objDir, model.Id + "-views.png"), AshfallKitFit.ViewsPng(mesh.Positions, mesh.Indices, model.RawMin, model.RawMax, gated.Fit.Boxes, 3, gated.Openings));
            var q = gated.Quality; var st = gated.Settings;
            string verdict = q.Passes ? "PASS" : reportOnly ? "fails (report only: never used)" : "FAIL";
            Console.WriteLine(Inv($"{model.Id}: {gated.Fit.Boxes.Count,3} boxes (res {st.Resolution}, fill {st.FillTolerance * 100:F0} %, cap {st.MaxBoxes}, seal {st.Seal})  {Measures(q)}  {verdict}{(q.Passes ? "" : ": " + string.Join("; ", q.Failures()))}"));
            if (before != null) Console.WriteLine(Inv($"         before: {beforeBoxes,3} boxes  {Measures(before)}  {(before.Passes ? "PASS" : "FAIL: " + string.Join("; ", before.Failures()))}"));
            if (model.DesignScale > 0f) Console.WriteLine("         " + DesignLine(model, gated));
        }
        File.WriteAllText(outPath, Generated(rows), new UTF8Encoding(false));
        File.WriteAllText(reportPath, Report(rows), new UTF8Encoding(false));
        var failed = rows.Where(r => !r.ReportOnly && !r.Gated.Quality.Passes).Select(r => r.Model.Id).ToList();
        Console.WriteLine($"ashfall-kit-collision: {rows.Count} models, {rows.Sum(r => r.Gated.Fit.Boxes.Count)} boxes -> {outPath}");
        Console.WriteLine($"  report {reportPath}\n  obj    {objDir}");
        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"ashfall-kit-collision: FAIL: {failed.Count} used model(s) fail the collision quality gate: {string.Join(", ", failed)} (see the report; the tests fail too)");
            return 3;
        }
        Console.WriteLine("ashfall-kit-collision: every used model passes the collision quality gate");
        return 0;
    }

    sealed record Row(AshfallKitModel Model, AshfallKitGatedFit Gated, List<string> Reasons, string Warn, AshfallKitFitQuality? Before, int BeforeBoxes, bool ReportOnly);

    static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A model with a design scale: its size at that scale and, for a walkable model, the fit's cell after scaling against the 0.4 m step (the largest scale that keeps it), the measured riser after scaling,
    /// the stair and ramp runs found and the walkable tops in metres above the foot.
    /// </summary>
    static string DesignLine(AshfallKitModel m, AshfallKitGatedFit g)
    {
        float s = m.DesignScale; var size = m.DesignSize;
        var sb = new StringBuilder(Inv($"design scale {s:F2} m/unit: {size.X:F1} x {size.Z:F1} m, {size.Y:F1} m high"));
        if (m.RawRiser > 0f) sb.Append(Inv($"; riser {m.RawRiser * s:F2} m{(m.RawRiser * s > AshfallKitFit.StepHeight ? " OVER the step" : "")}"));
        if (Walkable.Contains(m.Id))
        {
            float cell = g.Fit.Metrics.CellSize;
            sb.Append(Inv($"; walkable: cell {cell * s:F2} m {(cell * s <= AshfallKitFit.StepHeight + 1e-4f ? "within" : "OVER")} the {AshfallKitFit.StepHeight} m step (max walk scale {AshfallKitFit.MaxWalkScale(cell):F2}), {g.Fit.Ramps.Count} ramp(s)"));
            sb.Append("; tops at " + (g.Fit.Metrics.WalkableTops.Length == 0 ? "none" : string.Join(", ", g.Fit.Metrics.WalkableTops.Select(t => ((t - m.RawMin.Y) * s).ToString("0.0", CultureInfo.InvariantCulture)))) + " m");        }
        if (m.Flags != AshfallKitFlags.None) sb.Append($"; flags {m.Flags}");
        return sb.ToString();
    }

    static string Measures(AshfallKitFitQuality q) => Inv($"far air {q.FarAirPercent:F1} %, overhang {q.OverhangAirPercent:F1} %, worst box {q.WorstBoxAirPercent:F1} %, pocket {q.LargestPocketPercent:F1} %, deep air view {q.DeepAirViewPercent:F1} %, phantom view {q.PhantomSilhouettePercent:F1} %, holes {q.HoleSilhouettePercent:F1} %, missed {q.MissedPercent:F1} %, fine IoU {q.FineIou:F2}, openings {q.OpeningCount - q.BlockedOpenings}/{q.OpeningCount} open (least clear {q.WorstOpeningClearPercent:F0} %)");

    /// <summary>Why a model's collision wants a manual look: the gate's failures, the manifest's review flag, a walkable model with no top to stand on.</summary>
    static List<string> Cleanup(AshfallKitModel m, AshfallKitGatedFit g)
    {
        var r = new List<string>();
        foreach (var f in g.Quality.Failures()) r.Add("gate: " + f);
        if (m.Review) r.Add("manifest flags the geometry for review");
        if (Walkable.Contains(m.Id))
        {
            float h = m.RawMax.Y - m.RawMin.Y;
            if (!g.Fit.Metrics.WalkableTops.Any(t => (t - m.RawMin.Y) / h > 0.30f)) r.Add("no walkable top above 30 % of its height");
        }
        return r;
    }

    static string F(float v) { v = MathF.Round(v, 4); if (v == 0f) v = 0f; return v.ToString("0.0000", CultureInfo.InvariantCulture) + "f"; }
    static string V(Vector3 v) => $"new({F(v.X)}, {F(v.Y)}, {F(v.Z)})";
    static string D(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    static string Generated(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append("// <auto-generated>\n");
        sb.Append("// Generated by `smtool ashfall-kit-collision` from the owner's Ashfall kit library (numbers only, no asset bytes). Do not edit by hand.\n");
        sb.Append(Inv($"// Draft 6: each model is fitted with every setting of AshfallKitFit.GateLadder and keeps the passing fit with the fewest boxes (resolution, fill tolerance, cap and seal per model in Settings); walkable models ({string.Join(", ", Walkable)}) stay at resolution {AshfallKitFit.WalkableResolution} with ramps detected.\n"));
        sb.Append(Inv($"// Quality: the gate's measure against a {AshfallKitFit.MeasureResolution}-cell voxelisation (AshfallKitFit.Measure); thresholds in AshfallKitFit.Gate; AshfallKitCollisionGateTests fails when a used model does not pass.\n"));
        sb.Append(Inv($"// Window pass (2026-11-06): each model's through-openings (AshfallKitFit.FindOpenings) are carved out of the fit before merging; the gate wants every opening's core at least {AshfallKitFit.Gate.MinOpeningClearPercent} % clear (Openings per model).\n"));
        sb.Append("// Boxes are axis-aligned in each model's raw glTF frame (Y up), min/max in metres; see AshfallKitFit for how they are made.\n");
        sb.Append("// </auto-generated>\n");
        sb.Append("using System.Numerics;\n\nnamespace SmSim.Extras.Musou.Ashfall;\n\n");
        sb.Append("/// <summary>Per-model collision boxes of the Ashfall kit (generated; see the header).</summary>\npublic static partial class AshfallKitCollision\n{\n");
        sb.Append("    sealed record Entry((Vector3 Min, Vector3 Max)[] Boxes, AshfallKitFitMetrics Metrics, bool NeedsCleanup, string Reason, AshfallKitFitQuality Quality, AshfallKitFitSettings Settings, AshfallRamp[]? Ramps = null, AshfallKitOpening[]? Openings = null);\n\n");
        sb.Append("    static readonly Dictionary<string, Entry> Data = new()\n    {\n");
        foreach (var r in rows)
        {
            var m = r.Gated.Fit.Metrics; var q = r.Gated.Quality; var st = r.Gated.Settings;
            sb.Append($"        [\"{r.Model.Id}\"] = new(\n            [\n");
            foreach (var (mn, mx) in r.Gated.Fit.Boxes) sb.Append($"                (new({F(mn.X)}, {F(mn.Y)}, {F(mn.Z)}), new({F(mx.X)}, {F(mx.Y)}, {F(mx.Z)})),\n");
            sb.Append("            ],\n");
            sb.Append($"            new({m.SolidCells}, {m.CoveredCells}, {D(m.Iou)}, {D(m.DroppedPercent)}, {m.DroppedBoxes}, {m.BoxCount}, {(m.CapReached ? "true" : "false")}, {(m.OverCap ? "true" : "false")}, [{string.Join(", ", m.WalkableTops.Select(F))}], {m.Nx}, {m.Ny}, {m.Nz}, {F(m.CellSize)}),\n");
            sb.Append($"            {(r.Reasons.Count > 0 ? "true" : "false")}, \"{string.Join("; ", r.Reasons)}\",\n");
            sb.Append($"            new({q.FineResolution}, {q.BandCells}, {q.FineSolidCells}, {D(q.FineIou)}, {D(q.FarAirPercent)}, {D(q.OverhangAirPercent)}, {D(q.WorstBoxAirPercent)}, {D(q.MissedPercent)}, {q.CoveredCells}) {{ PhantomSilhouettePercent = {D(q.PhantomSilhouettePercent)}, HoleSilhouettePercent = {D(q.HoleSilhouettePercent)}, LargestPocketPercent = {D(q.LargestPocketPercent)}, DeepAirViewPercent = {D(q.DeepAirViewPercent)}, OpeningCount = {q.OpeningCount}, BlockedOpenings = {q.BlockedOpenings}, WorstOpeningClearPercent = {D(q.WorstOpeningClearPercent)} }},\n");
            sb.Append($"            new({st.Resolution}, {st.MaxBoxes}, {F(st.FillTolerance)}, {st.Seal})");
            if (r.Gated.Fit.Ramps.Count > 0)
            {
                sb.Append(",\n            [\n");
                foreach (var rp in r.Gated.Fit.Ramps) sb.Append($"                new(new({F(rp.Min.X)}, {F(rp.Min.Y)}, {F(rp.Min.Z)}), new({F(rp.Max.X)}, {F(rp.Max.Y)}, {F(rp.Max.Z)}), {rp.Dir}, {F(rp.YLow)}, {F(rp.YHigh)}),\n");
                sb.Append("            ]");
            }
            if (q.Openings.Count > 0)
            {
                sb.Append(",\n            Openings: [\n");
                foreach (var o in q.Openings)
                    sb.Append($"                new({o.Axis}, {V(o.Centre)}, {F(o.DepthMin)}, {F(o.DepthMax)}, {F(o.Width)}, {F(o.Height)}, {D(o.ClearPercent)}){(o.Wall is { } w ? $" {{ Wall = {V(w)} }}" : "")},\n");
                sb.Append("            ]");
            }
            sb.Append("),\n");
        }
        sb.Append("    };\n\n");
        sb.Append("    /// <summary>Every model id that has a box set.</summary>\n    public static IEnumerable<string> Ids => Data.Keys;\n\n");
        sb.Append("    /// <summary>The model's boxes in its raw glTF frame (min, max).</summary>\n    public static IReadOnlyList<(Vector3 Min, Vector3 Max)> Boxes(string id) => Data[id].Boxes;\n\n");
        sb.Append("    /// <summary>What the fit measured (cells, IoU, dropped dust, walkable tops).</summary>\n    public static AshfallKitFitMetrics Metrics(string id) => Data[id].Metrics;\n\n");
        sb.Append("    /// <summary>The boxes are doubtful and want a manual look (see <see cref=\"CleanupReason\"/>).</summary>\n    public static bool NeedsCleanup(string id) => Data[id].NeedsCleanup;\n\n");
        sb.Append("    public static string CleanupReason(string id) => Data[id].Reason;\n\n");
        sb.Append("    /// <summary>The collision quality gate's measure of the model's boxes (draft 6; <see cref=\"AshfallKitFitQuality.Passes\"/>).</summary>\n    public static AshfallKitFitQuality Quality(string id) => Data[id].Quality;\n\n");
        sb.Append("    /// <summary>The settings the model's boxes were fitted with (chosen by the gate).</summary>\n    public static AshfallKitFitSettings Settings(string id) => Data[id].Settings;\n\n");
        sb.Append("    /// <summary>The model's stair and ramp runs found in its top heightfield (raw frame; walkable models only).</summary>\n    public static IReadOnlyList<AshfallRamp> Ramps(string id) => Data[id].Ramps ?? [];\n\n");
        sb.Append("    /// <summary>The model's through-openings (windows, arches, door-sized holes) as the window pass found and measured them (raw frame): a shot along each one's centre ray through the wall passes, one into its wall point stops.</summary>\n    public static IReadOnlyList<AshfallKitOpening> Openings(string id) => Data[id].Openings ?? [];\n}\n");
        return sb.ToString();
    }

    static string Report(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append("# Ashfall kit collision report\n\n");
        sb.Append(Inv($"Draft 6 collision quality gate: measured against a {AshfallKitFit.MeasureResolution}-cell voxelisation (cracks up to {2 * AshfallKitFit.SealCells} cells sealed), air within {AshfallKitFit.BandCells} cells of the model not counted. "));
        sb.Append(Inv($"Thresholds: far air <= {AshfallKitFit.Gate.MaxFarAirPercent} %, overhang air <= {AshfallKitFit.Gate.MaxOverhangAirPercent} %, worst box <= {AshfallKitFit.Gate.MaxWorstBoxAirPercent} %, air pocket <= {AshfallKitFit.Gate.MaxPocketPercent} % of the longest side, deep air view <= {AshfallKitFit.Gate.MaxDeepAirViewPercent} %, "));
        sb.Append(Inv($"phantom view <= {AshfallKitFit.Gate.MaxPhantomSilhouettePercent} %, holes <= {AshfallKitFit.Gate.MaxHoleSilhouettePercent} %, missed surface <= {AshfallKitFit.Gate.MaxMissedPercent} %. Report only (never used): {string.Join(", ", AshfallKitFit.Gate.ReportOnlyIds)}.\n\n"));
        sb.Append("| id | name | gate | boxes (before) | settings | far air % | overhang % | worst box % | pocket % | deep air view % | phantom % | holes % | missed % | fine IoU | openings open / found (before: open) | before: far air / pocket / deep air view / holes / missed | walkable tops | flags |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var r in rows)
        {
            var m = r.Gated.Fit.Metrics; var q = r.Gated.Quality; var st = r.Gated.Settings; float h = r.Model.RawMax.Y - r.Model.RawMin.Y;
            string tops = m.WalkableTops.Length == 0 ? "none" : string.Join(", ", m.WalkableTops.Select(t => ((t - r.Model.RawMin.Y) / h).ToString("0.00", CultureInfo.InvariantCulture)));
            string flags = string.Join("; ", r.Reasons.Concat(r.Warn.Length > 0 ? [r.Warn] : []));
            string gate = q.Passes ? "pass" : r.ReportOnly ? "fail (report only)" : "**FAIL**";
            string before = r.Before is { } b ? Inv($"{b.FarAirPercent:F1} / {b.LargestPocketPercent:F1} / {b.DeepAirViewPercent:F1} / {b.HoleSilhouettePercent:F1} / {b.MissedPercent:F1}{(b.Passes ? " (pass)" : " (fail)")}") : "new";
            sb.Append(Inv($"| {r.Model.Id} | {r.Model.Name} | {gate} | {m.BoxCount} ({r.BeforeBoxes}) | res {st.Resolution}, fill {st.FillTolerance * 100:F0} %, cap {st.MaxBoxes}, seal {st.Seal} | {q.FarAirPercent:F1} | {q.OverhangAirPercent:F1} | {q.WorstBoxAirPercent:F1} | {q.LargestPocketPercent:F1} | {q.DeepAirViewPercent:F1} | {q.PhantomSilhouettePercent:F1} | {q.HoleSilhouettePercent:F1} | {q.MissedPercent:F1} | {q.FineIou:F2} | {q.OpeningCount - q.BlockedOpenings} / {q.OpeningCount} ({(r.Before is { } bo ? (bo.OpeningCount - bo.BlockedOpenings).ToString(CultureInfo.InvariantCulture) : "new")}) | {before} | {tops} | {flags} |\n"));
        }
        sb.Append(Inv($"\nWindow openings: {rows.Sum(r => r.Gated.Quality.OpeningCount)} found in {rows.Count(r => r.Gated.Quality.OpeningCount > 0)} models; open after the pass {rows.Sum(r => r.Gated.Quality.OpeningCount - r.Gated.Quality.BlockedOpenings)}, open with the fit before the run {rows.Where(r => r.Before != null).Sum(r => r.Before!.OpeningCount - r.Before!.BlockedOpenings)}.\n"));
        sb.Append(Inv($"\nPass: {rows.Count(r => r.Gated.Quality.Passes)} of {rows.Count}; used models failing: {rows.Count(r => !r.ReportOnly && !r.Gated.Quality.Passes)}. Boxes {rows.Sum(r => r.Gated.Fit.Boxes.Count)} (before {rows.Sum(r => r.BeforeBoxes)}).\n"));
        var design = rows.Where(r => r.Model.DesignScale > 0f).ToList();
        if (design.Count > 0)
        {
            sb.Append("\n## Design scales (models with a recorded scale)\n\n");
            foreach (var r in design) sb.Append($"- {r.Model.Id} ({r.Model.SourceId}, {r.Model.Family}): {DesignLine(r.Model, r.Gated)}\n");
        }
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
    internal static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }
}
