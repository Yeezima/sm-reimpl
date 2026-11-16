using System.Globalization;
using System.Text;
using SmSim.Extras.Musou.Ashfall;

namespace SmTool;

/// <summary>
/// <c>smtool ashfall-kit-bake-scales [--layout city|field|draft10] [--out file.json]</c> (kit-hi-res, owner 2026-11-09: "ensure all the 3d assets used in the main play map are high res"): for every kit model the map
/// places inside the battle arena (|x|, |y| within <see cref="AshfallMap.Half"/>), the scale of its largest item there along the file's glTF X, Y and Z axes (metres drawn per raw unit), keyed by the file's stem.
/// tools/blender/ashfall_bake.py takes the file as <c>--scales</c> and bakes the weathering at that size, so its metre-based numbers land at their real size. Models only placed outside the arena, the rubble
/// props (already in metres) and files with an axis correction are left out. Nothing is read from the install.
/// </summary>
static class AshfallKitBakeScalesCommand
{
    public static int Run(string[] rest)
    {
        var layout = AshfallLayout.City;
        string? outPath = null;
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--layout" && i + 1 < rest.Length) layout = AshfallLayouts.Parse(rest[++i]);
            else if (rest[i] == "--out" && i + 1 < rest.Length) outPath = Path.GetFullPath(rest[++i]);
            else { Console.Error.WriteLine($"ashfall-kit-bake-scales: unknown argument \"{rest[i]}\" (usage: ashfall-kit-bake-scales [--layout city|field|draft10] [--out file.json])"); return 2; }
        }
        var (_, dressing) = AshfallMap.BuildWithDressing(layout);
        var rows = new SortedDictionary<string, (string Id, float X, float Y, float Z)>(StringComparer.Ordinal);
        foreach (var g in dressing.Items.Where(Inside).GroupBy(i => i.Asset))
        {
            var model = AshfallKit.Get(g.Key);
            if (model.Id.StartsWith("AR-", StringComparison.Ordinal) || model.ImportFix != AshfallKitImportFix.None) continue;
            var it = g.OrderByDescending(i => MathF.Max(i.Size.X, MathF.Max(i.Size.Y, i.Size.Z))).First();
            var raw = model.RawSize;
            // the draw contract (AshfallKitView.Fit): not swapped, glTF X -> local X, glTF Y -> up, glTF Z -> local Y; swapped, glTF Z -> local X, glTF X -> local Y
            var (sx, sy, sz) = it.SwapXZ ? (it.Size.Y / raw.X, it.Size.Z / raw.Y, it.Size.X / raw.Z) : (it.Size.X / raw.X, it.Size.Z / raw.Y, it.Size.Y / raw.Z);
            rows[Path.GetFileNameWithoutExtension(model.File)] = (model.Id, sx, sy, sz);
        }
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var sb = new StringBuilder("{\n");
        int n = 0;
        foreach (var (stem, r) in rows)
        {
            sb.Append($"  \"{stem}\": [{F(r.X)}, {F(r.Y)}, {F(r.Z)}]{(++n < rows.Count ? "," : "")}\n");
            Console.Error.WriteLine($"ashfall-kit-bake-scales: {r.Id} {stem}: x {F(r.X)}, y {F(r.Y)}, z {F(r.Z)}");
        }
        sb.Append("}\n");
        if (outPath is null) Console.Write(sb.ToString());
        else { Directory.CreateDirectory(Path.GetDirectoryName(outPath)!); File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false)); Console.WriteLine($"ashfall-kit-bake-scales: {rows.Count} models -> {outPath}"); }
        return 0;
    }

    static bool Inside(AshfallDressItem it) => MathF.Abs(it.Position.X) <= AshfallMap.Half && MathF.Abs(it.Position.Y) <= AshfallMap.Half;
}
