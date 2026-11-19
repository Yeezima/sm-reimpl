using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>
/// What one squad Marine wears and carries, as the host works it out from the roster entry and its owner: the catalogue class, the appearance code (the owner's own, or only the owner's colours;
/// <see cref="WarfrontSquadModel.LookCode"/>), the gear names the resolver's class overrides ask for, and the weapons to hang. <see cref="Key"/> names the look for the caches.
/// </summary>
public sealed record WarfrontMarineDress(string ClassKey, AppearanceCode Code, IReadOnlyList<string> Carried, string? Primary, string? Pistol, string? Melee, string? Pack = null)
{
    public string Key => $"{ClassKey}|{Code}|{string.Join(",", Carried)}";
}

/// <summary>Works out a Marine's dress (the networked view: from the config's class lists and the owner's appearance; the demo: from a table).</summary>
public interface IWarfrontSquadDresser
{
    /// <summary>The dress of <paramref name="marine"/>; null while it cannot be known yet (the catalogue or the owner's look is not here).</summary>
    WarfrontMarineDress? Dress(in WarfrontSquadShown marine);
}

/// <summary>
/// Builds and keeps the models of the squad Marines (slice W10): the owner's look through the customizer's own resolver and builder (<see cref="AppearanceResolver"/>, <see cref="AppearanceAssets"/>, the one players
/// use), a few milliseconds a frame (<see cref="TryModel"/> never waits for a file). Looks are shared: nine Marines of one owner are one build. **Original assets loaded** when built.
/// </summary>
public sealed class WarfrontSquadLooks
{
    readonly AppearanceAssets _assets;
    readonly CustomizationCatalog _catalog;
    readonly Dictionary<string, ResolvedAppearance> _resolved = new();
    readonly Dictionary<string, MarineAssets> _built = new();
    readonly HashSet<string> _failed = new();

    public int Built => _built.Count;
    public int Failures => _failed.Count;
    public List<string> Notes { get; } = new();
    public AppearanceAssets Assets => _assets;

    public WarfrontSquadLooks(AppearanceAssets assets, CustomizationCatalog catalog) { _assets = assets; _catalog = catalog; }

    /// <summary>The model <paramref name="dress"/> calls for, or null while it is being read and built (called again next frame; at most two new pieces a call).</summary>
    public MarineAssets? TryModel(WarfrontMarineDress dress)
    {
        string key = dress.Key;
        if (_built.TryGetValue(key, out var done)) return done;
        if (_failed.Contains(key)) return null;
        try
        {
            if (!_resolved.TryGetValue(key, out var look))
            {
                look = AppearanceResolver.Resolve(dress.Code, dress.ClassKey, _catalog, _assets.ResolveOptions(dress.Carried.ToList()));
                _resolved[key] = look;
                _assets.Prefetch(look);
            }
            if (_assets.TryBuild(look) is { } model) return _built[key] = model;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _failed.Add(key);
            Notes.Add($"squad look {dress.ClassKey}: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    /// <summary>The catalogue class (key) for a class name or role, or null.</summary>
    public static string? ClassKeyFor(CustomizationCatalog catalog, string? name, string? role, bool chaos)
    {
        if (name is not null && catalog.Class(name) is { } byName) return byName.Key;
        string race = chaos ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine;
        return catalog.Classes.FirstOrDefault(c => c.Race == race && c.Role.Equals(role ?? "tactical", StringComparison.OrdinalIgnoreCase))?.Key;
    }
}
