using System.Diagnostics;
using SmContent;
using SmContent.Customization;
using SmFormats.Dds;

namespace SmTool;

/// <summary>
/// <c>smtool built-mask-bench &lt;game-dir|auto&gt; [--packages a,b,..] [--set &lt;predefined set&gt;] [--all] [--parallel N] [--cache &lt;dir&gt;] [--previews &lt;dir&gt;]
/// [--schemes s1,s2,..]</c> (armour-pieces-1125 repair, test aid): loads armour packages exactly as the client's piece cache does with built paint masks
/// (<see cref="CharacterModel.LoadPiece"/>, <see cref="BakedArmourPaint"/>), N at a time (default 9: a nine-piece player joining), and prints the wall time,
/// the time per package, the bytes allocated and the peak private memory sampled every 5 ms. <c>--cache</c> turns the built-mask cache on in that folder
/// (it must be outside the repository and the game folder); without it nothing is cached. <c>--previews</c> writes, per built material, the baked diffuse,
/// the mask (R/G/B = primary/secondary/tertiary) and the material painted in texture space with each scheme's colours the way the painted shader mixes them
/// (gain x luminance x colour), next to a regular masked piece of the same slot for brightness: original-derived pictures, so the folder must be outside the
/// repository (the private media or tmp folder). Read-only on the install.
/// </summary>
static class BuiltMaskCommand
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string? Opt(string name) { int i = Array.IndexOf(rest, name); return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null; }
        bool Outside(string dir) => !SmFormats.InstallPaths.IsWithin(Path.GetFullPath(dir), gameDir) && !Path.GetFullPath(dir).StartsWith(repo, StringComparison.OrdinalIgnoreCase);

        var notes = new List<string>();
        var catalog = CustomizationCatalog.Load(gameDir, notes);
        // Round 4 (finding 6 of the round-3 re-gate): region data naming a package this install lacks never applies (a misspelt name would go unseen); say so.
        foreach (var missing in BuiltMaskOverrides.MissingPackages(p => CharacterModel.PackagePath(gameDir, p) is { } path && File.Exists(path)))
            Console.WriteLine($"  warning: built-mask region data names package {missing}, which is not in this install; that entry is unused");
        if (Opt("--scheme-dump") is { } sd)
        {
            foreach (var slot in PaintSchemes.Slots)
                if (catalog.Paint.Scheme(sd)?.For(slot) is { } ps)
                    Console.WriteLine($"{sd} {slot}: P {string.Join(",", ps.Primary.Diffuse.Select(v => v.ToString("0.00")))} S {string.Join(",", ps.Secondary.Diffuse.Select(v => v.ToString("0.00")))} T {string.Join(",", ps.Tertiary.Diffuse.Select(v => v.ToString("0.00")))} pattern {ps.PatternEnabled}");
            return 0;
        }
        var wanted = new List<(string Package, string Slot, string Race)>();
        if (Opt("--packages") is { } list)
            foreach (var p in list.Split(',', StringSplitOptions.RemoveEmptyEntries)) wanted.Add((p, PaintSchemes.SlotOf(p) ?? "torso", p.StartsWith("csm", StringComparison.OrdinalIgnoreCase) ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine));
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
            foreach (var slot in CustomizationCatalog.ArmourSlots)
                foreach (var piece in catalog.Race(race)!.OfferedPiecesFor(slot))
                {
                    bool take = rest.Contains("--all") ? piece.BuiltMasks : Opt("--set") is { } set && piece.BuiltMasks && piece.SetNames.Any(n => n.Equals(set, StringComparison.OrdinalIgnoreCase));
                    if (take) foreach (var p in piece.Packages) if (!wanted.Any(w => w.Package == p)) wanted.Add((p, slot, race));
                }
        if (wanted.Count == 0) { Console.Error.WriteLine("built-mask-bench: no packages (--packages, --set <name> or --all)"); return 2; }

        if (Opt("--cache") is { } cacheDir)
        {
            if (!Outside(cacheDir)) { Console.Error.WriteLine("built-mask-bench: --cache is inside the repository or the game folder; refused"); return 2; }
            BakedArmourPaint.CacheDirectory = cacheDir;
        }
        string? previews = Opt("--previews");
        if (previews is not null && !Outside(previews)) { Console.Error.WriteLine("built-mask-bench: --previews is inside the repository or the game folder; refused"); return 2; }
        int parallel = int.TryParse(Opt("--parallel"), out var par) && par > 0 ? par : 9;

        var rig = CharacterModel.LoadMarineRig(gameDir);
        var bones = new HashSet<string>(rig.Skeleton.BoneNames, StringComparer.Ordinal);
        var positions = CharacterModel.BindPositions(rig.Skeleton);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long baseline = proc.PrivateMemorySize64, peak = baseline;
        bool sampling = true;
        var sampler = new Thread(() => { while (Volatile.Read(ref sampling)) { proc.Refresh(); peak = Math.Max(peak, proc.PrivateMemorySize64); Thread.Sleep(5); } }) { IsBackground = true };
        sampler.Start();
        long allocBefore = GC.GetTotalAllocatedBytes(true);
        BakedArmourPaint.ResetStatistics();
        var times = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();
        var pieces = new System.Collections.Concurrent.ConcurrentDictionary<string, CharacterPiece>();
        var all = Stopwatch.StartNew();
        Parallel.ForEach(wanted, new ParallelOptions { MaxDegreeOfParallelism = parallel }, w =>
        {
            var watch = Stopwatch.StartNew();
            var n = new List<string>();
            try
            {
                var piece = CharacterModel.LoadPiece(bones, CharacterModel.PackagePath(gameDir, w.Package)!, n, CharacterModel.ChaosTexturePackages, objectTreePlacement: true, positions, buildPaintMasks: true);
                if (previews is not null || rest.Contains("--mask-shares")) pieces[w.Package] = piece;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { n.Add($"{w.Package}: {ex.GetType().Name}: {ex.Message}"); }
            times[w.Package] = watch.Elapsed.TotalMilliseconds;
            lock (notes) notes.AddRange(n);
        });
        all.Stop();
        Volatile.Write(ref sampling, false);
        sampler.Join();
        long alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;
        Console.WriteLine($"built-mask-bench: {wanted.Count} packages, {parallel} at a time, cache {(BakedArmourPaint.CacheDirectory is null ? "off" : "on")}: wall {all.Elapsed.TotalMilliseconds:0} ms, "
            + $"allocated {alloc / 1048576.0:0} MB, peak private memory +{(peak - baseline) / 1048576.0:0} MB over {baseline / 1048576.0:0} MB; "
            + $"masks built {BakedArmourPaint.Built}, from the cache {BakedArmourPaint.CacheHits}, at most {BakedArmourPaint.PeakConcurrentBuilds} at once, failed {BakedArmourPaint.Failed}");
        foreach (var (p, ms) in times.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Console.WriteLine($"  {p}: {ms:0} ms");
        foreach (var n in notes.Where(n => n.Contains("mask", StringComparison.OrdinalIgnoreCase) || n.Contains("Exception", StringComparison.Ordinal)).Distinct()) Console.WriteLine("  note: " + n);

        if (rest.Contains("--mask-shares"))
            foreach (var (pkg, piece) in pieces.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                foreach (var surf in piece.Model.Surfaces)
                    if (surf.Material is { } mm && mm.Textures.TryGetValue("PaintMask", out var mn) && piece.Model.Textures.TryGetValue(mn, out var mb) && DdsImage.TryDecode(mb) is { } mi)
                    {
                        double r = 0, g = 0, b = 0; int n = 0;
                        for (int i = 0; i < mi.Width * mi.Height; i++) { int o2 = i * 4; if (mi.Rgba[o2] + mi.Rgba[o2 + 1] + mi.Rgba[o2 + 2] < 60) continue; r += mi.Rgba[o2]; g += mi.Rgba[o2 + 1]; b += mi.Rgba[o2 + 2]; n++; }
                        double t = Math.Max(1, r + g + b);
                        // Brightness check (round 3): the median luminance (sRGB 0..255) of the diffuse the paint shader reads (the grey paint diffuse of a built mask,
                        // the regular piece's own diffuse otherwise) over the texels mostly in each region, to compare a built piece with the regular piece beside it.
                        string dk = BakedArmourPaint.IsBuilt(mm) ? BakedArmourPaint.PaintDiffuseKey : "DiffuseMap";
                        string meds = "";
                        if (mm.Textures.TryGetValue(dk, out var dn) && piece.Model.Textures.TryGetValue(dn, out var db) && DdsImage.TryDecode(db) is { } di)
                        {
                            var lums = new List<float>[] { [], [], [] };
                            for (int i = 0; i < mi.Width * mi.Height; i++)
                            {
                                int o2 = i * 4, c = mi.Rgba[o2] >= 150 ? 0 : mi.Rgba[o2 + 1] >= 150 ? 1 : mi.Rgba[o2 + 2] >= 150 ? 2 : -1;
                                if (c < 0) continue;
                                int dx = Math.Min(di.Width - 1, i % mi.Width * di.Width / mi.Width), dy = Math.Min(di.Height - 1, i / mi.Width * di.Height / mi.Height), d2 = (dy * di.Width + dx) * 4;
                                lums[c].Add(0.2126f * di.Rgba[d2] + 0.7152f * di.Rgba[d2 + 1] + 0.0722f * di.Rgba[d2 + 2]);
                            }
                            // Round 4: the upper percentiles too (the highlight ceilings, BakedArmourPaint.LuminanceTargets) and the share of clipped texels (250+).
                            string Med(List<float> l)
                            {
                                if (l.Count == 0) return "-";
                                l.Sort();
                                return $"{l[l.Count / 2]:0} (p25 {l[l.Count / 4]:0}, p75 {l[l.Count * 3 / 4]:0}, p95 {l[l.Count * 95 / 100]:0}, p99 {l[l.Count * 99 / 100]:0}, 250+ {l.Count(v => v >= 250f) * 100.0 / l.Count:0.0}%)";
                            }
                            meds = $"; diffuse luminance P {Med(lums[0])} S {Med(lums[1])} T {Med(lums[2])}";
                        }
                        Console.WriteLine($"mask shares {pkg}/{mm.Name} ({mn}{(BakedArmourPaint.IsBuilt(mm) ? ", built" : "")}): R {r / t:P0} G {g / t:P0} B {b / t:P0} over {n} painted texels{meds}");
                    }
        if (previews is not null) WritePreviews(gameDir, catalog, wanted, pieces, previews, Opt("--schemes"), int.TryParse(Opt("--cell"), out var cell) ? cell : 192, rest.Contains("--full"), rest.Contains("--grid"));
        return 0;
    }

    static void WritePreviews(string gameDir, CustomizationCatalog catalog, List<(string Package, string Slot, string Race)> wanted, IReadOnlyDictionary<string, CharacterPiece> pieces,
        string dir, string? schemeList, int cell, bool full, bool grid = false)
    {
        Directory.CreateDirectory(dir);
        var rows = new List<(string Label, List<(int W, int H, byte[] Rgba)> Cells)>();
        foreach (var (package, slot, race) in wanted)
        {
            if (!pieces.TryGetValue(package, out var piece)) continue;
            string[] schemes = (schemeList ?? (race == CustomizationCatalog.Chaos ? "csm_alpha_legion,csm_word_bearers,csm_death_guard" : "sm_ultramarines,sm_blood_angels,sm_white_scars")).Split(',');
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in piece.Model.Surfaces)
            {
                if (s.Material is not { } m || !BakedArmourPaint.IsBuilt(m) || !done.Add(m.Textures["PaintMask"])) continue;
                var tex = piece.Model.Textures;
                if (DdsImage.TryDecode(tex[m.Textures["DiffuseMap"]]) is not { } baked || DdsImage.TryDecode(tex[m.Textures["PaintMask"]]) is not { } mask
                    || DdsImage.TryDecode(tex[m.Textures[BakedArmourPaint.PaintDiffuseKey]]) is not { } grey) continue;
                string stem = Path.Combine(dir, $"{package}__{Safe(m.Name ?? "m")}");
                var cells = new List<(int, int, byte[])> { (baked.Width, baked.Height, Opaque(baked.Rgba)), (mask.Width, mask.Height, Opaque(mask.Rgba)) };
                foreach (var scheme in schemes)
                {
                    var set = catalog.Paint.Scheme(scheme)?.For(slot);
                    if (set is null) continue;
                    cells.Add((grey.Width, grey.Height, Painted(grey, mask, set.Primary.Diffuse, set.Secondary.Diffuse, set.Tertiary.Diffuse)));
                }
                if (full) for (int c = 0; c < cells.Count; c++) GfxDumpCommand.WritePng($"{stem}_{c}.png", cells[c].Item1, cells[c].Item2, cells[c].Item3);
                rows.Add(($"{package}/{m.Name}", cells));
            }
        }
        // One sheet: a row per built material (baked, mask, painted per scheme), each cell box-scaled to cell x cell.
        int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Count);
        if (rows.Count > 0)
        {
            int sw = cols * cell, sh = rows.Count * cell;
            var sheet = new byte[sw * sh * 4];
            for (int i = 3; i < sheet.Length; i += 4) sheet[i] = 255;
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Cells.Count; c++)
                {
                    var (w, h, px) = rows[r].Cells[c];
                    for (int y = 0; y < cell; y++)
                    for (int x = 0; x < cell; x++)
                    {
                        int x0 = x * w / cell, x1 = Math.Max(x0 + 1, (x + 1) * w / cell), y0 = y * h / cell, y1 = Math.Max(y0 + 1, (y + 1) * h / cell);
                        int sr = 0, sg = 0, sb = 0, n = 0;
                        for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) { int o = (yy * w + xx) * 4; sr += px[o]; sg += px[o + 1]; sb += px[o + 2]; n++; }
                        int d = ((r * cell + y) * sw + c * cell + x) * 4;
                        sheet[d] = (byte)(sr / n); sheet[d + 1] = (byte)(sg / n); sheet[d + 2] = (byte)(sb / n);
                        // --grid: a line every 0.1 of U and V (yellow, white at 0.5) to read texture coordinates for BuiltMaskOverrides.
                        if (grid && ((x * 10 / cell != (x + 1) * 10 / cell) || (y * 10 / cell != (y + 1) * 10 / cell)))
                        {
                            bool half = x * 2 / cell != (x + 1) * 2 / cell || y * 2 / cell != (y + 1) * 2 / cell;
                            sheet[d] = 255; sheet[d + 1] = 255; sheet[d + 2] = (byte)(half ? 255 : 0);
                        }
                    }
                }
            GfxDumpCommand.WritePng(Path.Combine(dir, "sheet.png"), sw, sh, sheet);
            File.WriteAllLines(Path.Combine(dir, "sheet.txt"), rows.Select((r, i) => $"{i}: {r.Label}"));
        }
        Console.WriteLine($"previews -> {dir} ({rows.Count} materials)");
    }

    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

    static byte[] Opaque(byte[] rgba) { var o = (byte[])rgba.Clone(); for (int i = 3; i < o.Length; i += 4) o[i] = 255; return o; }

    /// <summary>The painted shader's albedo (MapMaterials.PaintShaderCode, gain 3.1, no dirt or lighting) in sRGB.</summary>
    static byte[] Painted(DdsImage grey, DdsImage mask, float[] p, float[] s, float[] t)
    {
        var o = new byte[grey.Rgba.Length];
        static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        static byte Enc(float v) { v = Math.Clamp(v, 0f, 1f); return (byte)Math.Round(255f * (v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f)); }
        for (int i = 0; i < grey.Width * grey.Height; i++)
        {
            int mx = Math.Min(mask.Width - 1, i % grey.Width * mask.Width / grey.Width), my = Math.Min(mask.Height - 1, i / grey.Width * mask.Height / grey.Height);
            int mi = (my * mask.Width + mx) * 4;
            float mr = mask.Rgba[mi] / 255f, mg = mask.Rgba[mi + 1] / 255f, mb = mask.Rgba[mi + 2] / 255f, w = Math.Clamp(mr + mg + mb, 0f, 1f), sum = MathF.Max(mr + mg + mb, 0.001f);
            float r = Lin(grey.Rgba[i * 4] / 255f), g = Lin(grey.Rgba[i * 4 + 1] / 255f), b = Lin(grey.Rgba[i * 4 + 2] / 255f);
            float shade = Math.Clamp((0.2126f * r + 0.7152f * g + 0.0722f * b) * 3.1f, 0f, 1.5f);
            for (int c = 0; c < 3; c++)
            {
                float col = (Lin(p[c]) * mr + Lin(s[c]) * mg + Lin(t[c]) * mb) / sum;
                float bse = c == 0 ? r : c == 1 ? g : b;
                o[i * 4 + c] = Enc(bse + (col * shade - bse) * w);
            }
            o[i * 4 + 3] = 255;
        }
        return o;
    }
}
