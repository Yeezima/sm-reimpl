namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Which kit detail the client draws (kit-hi-res; engine-free so it is testable; the client's <c>AshfallKitDetail</c> applies it). A model with an item inside the battle arena keeps 2048 px textures and finer
/// LODs, unless the test aid <c>--ashfall-kit-detail old</c> is given or the client runs in old-GPU mode (safe mode: the Compatibility renderer, picked on a weak GPU or by the player's toggle in OPTIONS, or
/// <c>--safe-mode</c>). Owner, 2026-11-10: "Use low detail ashfall textures for old gpu mode users". Low detail is the earlier 1024 px textures and the plain LOD threshold.
/// </summary>
public static class AshfallKitDetailRule
{
    /// <summary>The largest texture side kept for an arena model in high detail (the source files' size).</summary>
    public const int ArenaTextureMax = 2048;
    /// <summary>The largest texture side kept in low detail, and for models only placed outside the arena (the earlier bake's size).</summary>
    public const int LowTextureMax = 1024;

    /// <summary>Whether the arena models draw in high detail.</summary>
    public static bool HighDetail(bool oldFlag, bool oldGpuMode) => !oldFlag && !oldGpuMode;

    /// <summary>The largest texture side kept for a model: <see cref="ArenaTextureMax"/> for an arena model in high detail, else <see cref="LowTextureMax"/>.</summary>
    public static int TextureMax(bool insideArena, bool oldFlag, bool oldGpuMode) => insideArena && HighDetail(oldFlag, oldGpuMode) ? ArenaTextureMax : LowTextureMax;
}
