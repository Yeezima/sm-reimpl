using System.Runtime.CompilerServices;

namespace SmFormats.Tests;

/// <summary>Polish pass P2 review: no test writes Musou's generated layout into the owner's real cache (24 MB a run); the cache's own tests use streams.</summary>
static class TestProcessSetup
{
    [ModuleInitializer]
    internal static void Run() => SmContent.Extras.Musou.AshfallLayoutCache.Disabled = true;
}
