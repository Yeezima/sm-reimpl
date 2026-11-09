using System;
using System.Collections.Generic;
using Godot;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// The detail the kit keeps inside the battle arena (kit-hi-res; owner 2026-11-09: "can you make a pass to ensure all the 3d assets used in the main play map are high res? its ok if the buildings outside
/// the main batte arena (map boundary) are low poly"). A model with at least one item inside the boundary (<see cref="AshfallMap.Half"/>) keeps the source's 2048 px textures, and its instances inside
/// the boundary draw finer LODs (<see cref="ArenaLodBias"/> against the map's 4 px threshold; not in safe mode). Instances outside, and models only placed outside, keep the earlier 1024 px and the plain threshold.
/// <c>--ashfall-kit-detail old</c> (test aid) draws as before this pass. Values ours (docs/musou-ashfall-graybox.md "Kit detail inside the arena"). **Placeholder demonstration.**
/// </summary>
public static class AshfallKitDetail
{
    /// <summary>Largest texture side kept for a model placed inside the arena (the source files' size).</summary>
    public const int ArenaTextureMax = 2048;
    /// <summary>LOD bias of an instance inside the arena: the map's 4 px threshold (<see cref="AshfallKitView.LodThreshold"/>) divided by this is the error allowed on screen, so 4 is the engine's 1 px.</summary>
    public const float ArenaLodBias = 4f;

    /// <summary><c>--ashfall-kit-detail old</c>: every model at 1024 px and the plain LOD threshold, as before the pass.</summary>
    public static bool Old => _old ??= HasOld();
    static bool? _old;
    static bool HasOld()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--ashfall-kit-detail");
        return i >= 0 && i + 1 < args.Length && args[i + 1] == "old";
    }

    /// <summary>Whether an item stands inside the battle arena (its position within the boundary square).</summary>
    public static bool InsideArena(AshfallDressItem item) => MathF.Abs(item.Position.X) <= AshfallMap.Half && MathF.Abs(item.Position.Y) <= AshfallMap.Half;

    /// <summary>Marks every model with an item inside the arena as high detail in <paramref name="library"/> (nothing with <see cref="Old"/>).</summary>
    public static void Mark(AshfallKitLibrary library, IEnumerable<AshfallDressItem> items)
    {
        if (Old) return;
        foreach (var it in items) if (InsideArena(it)) library.HighDetail.Add(it.Asset);
    }

    /// <summary>The LOD bias for an instance of <paramref name="item"/>: <see cref="ArenaLodBias"/> inside the arena, except in safe mode (the Compatibility renderer of older GPUs), where the finer LODs
    /// roughly doubled the primitives drawn (measured on the city: docs/musou-ashfall-graybox.md "Kit detail inside the arena") and the 2048 px textures carry the detail alone.</summary>
    public static float LodBiasOf(AshfallDressItem item) => !Old && !SafeMode.Active && InsideArena(item) ? ArenaLodBias : 1f;
}
