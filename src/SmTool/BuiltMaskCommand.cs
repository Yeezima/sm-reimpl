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
                if (previews is not null) pieces[w.Package] = piece;
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

        if (previews is not null) WritePreviews(gameDir, catalog, wanted, pieces, previews, Opt("--schemes"));
        return 0;
    }

    static void WritePreviews(string gameDir, CustomizationCatalog catalog, List<(string Package, string Slot, string Race)> wanted, IReadOnlyDictionary<string, CharacterPiece> pieces,
        string dir, string? schemeList)
    {
        Directory.CreateDirectory(dir);
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
                GfxDumpCommand.WritePng(stem + "_0baked.png", baked.Width, baked.Height, Opaque(baked.Rgba));
                GfxDumpCommand.WritePng(stem + "_1mask.png", mask.Width, mask.Height, Opaque(mask.Rgba));
                foreach (var scheme in schemes)
                {
                    var set = catalog.Paint.Scheme(scheme)?.For(slot);
                    if (set is null) continue;
                    GfxDumpCommand.WritePng(stem + $"_2{scheme}.png", grey.Width, grey.Height, Painted(grey, mask, set.Primary.Diffuse, set.Secondary.Diffuse, set.Tertiary.Diffuse));
                }
            }
        }
        Console.WriteLine($"previews -> {dir}");
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
