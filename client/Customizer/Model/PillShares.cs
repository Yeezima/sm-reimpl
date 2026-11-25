using System;
using System.Collections.Generic;
using System.Linq;

namespace SmClient.Customizer.Model;

/// <summary>
/// How a row of pills shares its width (the Armoury's faction / mode pills; 2026-11-25). Pure, no Godot. Ours.
/// </summary>
public static class PillShares
{
    /// <summary>
    /// The width each pill gets, or null when every pill's need (label plus plate margins, px) fits an equal share of <paramref name="room"/>: then the row keeps its equal layout unchanged.
    /// Otherwise each pill gets <paramref name="room"/> in proportion to its need (the shares add up to the room).
    /// </summary>
    public static IReadOnlyList<float>? Shares(IReadOnlyList<float> needs, float room)
    {
        if (needs.Count == 0) return null;
        float equal = room / needs.Count;
        if (needs.All(n => n <= equal)) return null;
        float sum = needs.Sum(n => Math.Max(0f, n));
        if (sum <= 0f) return null;
        return needs.Select(n => room * Math.Max(0f, n) / sum).ToList();
    }
}
