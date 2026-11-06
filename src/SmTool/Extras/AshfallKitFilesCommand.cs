using SmSim.Extras.Musou.Ashfall;

namespace SmTool;

/// <summary>
/// <c>smtool ashfall-kit-files</c>: prints every model file (and the storm sky image) the Ashfall kit look loads (one path per line, relative to the library's <c>source\</c> root, forward slashes).
/// tools/release/release.ps1 copies exactly these files from the owner's private library into the tester build's <c>game\ashfall-kit\</c>
/// (owner, 2026-11-06: the release carries Ashfall the same as the local playtests).
/// </summary>
static class AshfallKitFilesCommand
{
    public static int Run()
    {
        foreach (var file in AshfallKit.Models.Select(m => m.File).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            Console.WriteLine(file);
        Console.WriteLine(AshfallKit.SkyFile);   // the storm sky panorama, last
        return 0;
    }
}
