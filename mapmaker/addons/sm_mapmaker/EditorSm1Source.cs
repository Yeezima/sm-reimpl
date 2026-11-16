using System;
using System.Collections.Generic;
using System.Linq;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// The core's view of SM1 maps (<see cref="ISm1Source"/>) answered from the maps the editor already holds in memory (<see cref="Sm1Source"/>), so a map
/// is read from the install once. A map the editor has not loaded falls back to the core's own reader.
/// </summary>
public sealed class EditorSm1Source : ISm1Source
{
    readonly Func<string, Sm1Source?> _loaded;
    readonly ISm1Source? _fallback;
    readonly Dictionary<string, Sm1Map> _cache = new(StringComparer.OrdinalIgnoreCase);

    public EditorSm1Source(Func<string, Sm1Source?> loaded, ISm1Source? fallback) { _loaded = loaded; _fallback = fallback; }

    /// <summary>Forget what was derived (after a map was reloaded).</summary>
    public void Invalidate() => _cache.Clear();

    public Sm1Map LoadMap(string map)
    {
        if (_cache.TryGetValue(map, out var cached)) return cached;
        if (_loaded(map) is not { } src)
            return _fallback?.LoadMap(map) ?? throw new MapException("missing-install", "No SM1 install was found.");
        var definitions = new Dictionary<string, Sm1Definition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in src.Scene.Definitions.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            Bounds? b = src.BoundsOf(d.Name) is { } x ? new Bounds(x.Min, x.Max) : null;
            definitions[d.Name] = new Sm1Definition(d.Name, d.Visuals.Count, b, InstalledSm1Source.PartBounds(src.Scene, d), d.NearestLod().Select(v => v.RefNode).ToList());
        }
        var objects = src.Scene.Objects.Select(o => new Sm1Object(o.Name, o.Definition, o.Layer, o.Transform, o.Depth)).ToList();
        return _cache[map] = new Sm1Map(map, objects, definitions, src.Scene.Notes);
    }
}
