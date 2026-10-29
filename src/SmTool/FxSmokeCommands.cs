using SmContent;
using SmFormats.Fx;

namespace SmTool;

/// <summary>
/// <c>smtool fx-smoke &lt;game-dir|auto&gt; [--map mp_tdm_09] [--all] [--textures]</c> (fx/no-smoke): every effect the multiplayer client plays (the weapons', thrown items', hit
/// containers', armour hits', jump packs' and dashes', death effects', the fall landing's, and the map's own looping effects) with the sprites <see cref="SmokeRules"/> removes
/// (smoke, dust, mist, haze) and what it keeps; <c>--all</c> also lists the effects that lose nothing, <c>--textures</c> sums the sprites per texture. Read-only.
/// The set of names mirrors the client's preload (<c>NetworkView.SetUpFx</c>, <c>FxMapEffects</c>).
/// </summary>
static class FxSmokeCommands
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        string map = rest.SkipWhile(a => a != "--map").Skip(1).FirstOrDefault() ?? "mp_tdm_09";
        bool all = rest.Contains("--all"), textures = rest.Contains("--textures");
        var catalogue = EffectCatalogue.Load(gameDir);
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);   // name -> where it comes from
        void Add(string name, string from) { if (name.Length > 0) names.TryAdd(name, from); }
        void AddBindings(IEnumerable<FxBinding> bindings, string from) { foreach (var b in bindings) if (b.Play is { } p) Add(p.Name, from); }
        foreach (var w in catalogue.Weapons.Values) { AddBindings(w.Bindings, "weapon"); foreach (var pr in w.Projectiles) AddBindings(pr.Bindings, "projectile"); }
        foreach (var t in catalogue.Thrown.Values) { AddBindings(t.Bindings, "thrown"); if (t.Projectile is { } tp) AddBindings(tp.Bindings, "thrown"); }
        foreach (var table in catalogue.Containers.Values)
            foreach (var surface in table.Effects.Values) foreach (var plays in surface.Values) foreach (var p in plays) Add(p.Name, "hit");
        foreach (var p in catalogue.Player.ArmourHit.Concat(catalogue.Player.ArmourInactiveHit)) Add(p.Name, "armour hit");
        foreach (var pack in catalogue.JumpPacks.Values) { foreach (var n in pack.Names) Add(n, "jump pack ground"); foreach (var n in pack.FlightNames) Add(n, "jump pack flight"); }
        foreach (var plays in catalogue.Dashes.Values) foreach (var p in plays) Add(p.Name, "dash");
        try { foreach (var n in DeathEffects.Load(gameDir).EffectNames) Add(n, "death"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine($"(death effects not read: {ex.Message})"); }
        foreach (var p in catalogue.FinishFalling.Plays) Add(p.Name, "fall landing");
        foreach (var o in MapEffects.Load(gameDir, map).Where(o => o.AutoTrigger)) Add(o.Fx, "map " + map);

        using var library = FxLibrary.Load(gameDir);
        var failures = new List<string>();
        var systems = library.Systems(names.Keys, failures);
        int touched = 0, emptied = 0, removedSprites = 0, keptSprites = 0;
        var perTexture = new SortedDictionary<string, (int Removed, int Kept)>(StringComparer.Ordinal);
        Console.WriteLine($"fx/no-smoke: {names.Count} effects the multiplayer client plays ({systems.Count} in the package); per effect: sprites removed / sprites in it, what it keeps.");
        foreach (var (name, from) in names)
        {
            if (!systems.TryGetValue(name, out var sys)) continue;
            var sprites = sys.ElementsWithCollisions.Select(e => (Element: e, Class: SmokeRules.Classify(e))).Where(x => x.Class != SmokeClass.NotSprite).ToList();
            var removed = sprites.Where(x => x.Class == SmokeClass.Smoke).ToList();
            var kept = sprites.Where(x => x.Class == SmokeClass.Keep).ToList();
            foreach (var x in sprites)
            {
                string tex = SmokeRules.Traits(x.Element)!.Value is var t ? $"{t.Material} ({(t.Blend == 1 ? "additive" : t.Blend == 2 ? "distortion" : "alpha")})" : "";
                var v = perTexture.GetValueOrDefault(tex);
                perTexture[tex] = x.Class == SmokeClass.Smoke ? (v.Removed + 1, v.Kept) : (v.Removed, v.Kept + 1);
            }
            removedSprites += removed.Count; keptSprites += kept.Count;
            int decals = sys.ElementsWithCollisions.Count(e => e is FxDecal), lights = sys.ElementsWithCollisions.Count(e => e is FxLight);
            if (removed.Count > 0) touched++;
            if (removed.Count > 0 && kept.Count == 0 && decals == 0) emptied++;
            if (removed.Count == 0 && !all) continue;
            string keeps = kept.Count == 0 ? "no sprite" : string.Join(", ", kept.Select(x => SmokeRules.Traits(x.Element)!.Value.Material).Where(m => m.Length > 0).Distinct().Take(6));
            Console.WriteLine($"  {name,-44} {from,-18} removed {removed.Count,3} / {sprites.Count,3}  keeps {keeps}{(decals > 0 ? $"; {decals} decal(s)" : "")}{(lights > 0 ? $"; {lights} light(s)" : "")}");
        }
        Console.WriteLine($"{touched} effects lose sprites ({emptied} of them draw no sprite and no decal any more); {removedSprites} sprites removed, {keptSprites} kept.");
        // Which rule decided the less obvious cases (review S3), and smoke sprites that hold content of their own (review Q1).
        var played = systems.Values.SelectMany(s => s.ElementsWithCollisions.Select(e => (System: s.Name, Element: e))).ToList();
        string Describe(string system, FxElement e)
        {
            var t = SmokeRules.Traits(e)!.Value;
            return $"{system}: '{e.Name}' {t.Material} ({(t.Blend == 1 ? "additive" : t.Blend == 2 ? "distortion" : "alpha")}) size {t.SizeStart:0.###} -> {t.SizeEnd:0.###}, alpha {t.AlphaStart:0.##} -> {t.AlphaEnd:0.##}, {t.Duration:0.###} s";
        }
        foreach (int rule in new[] { 5, 6 })
        {
            var hits = played.Where(p => SmokeRules.Traits(p.Element) is { } t && SmokeRules.Explain(t).Rule == rule).Select(p => Describe(p.System, p.Element)).Distinct().ToList();
            Console.WriteLine($"rule {rule} ({(rule == 5 ? "element named as smoke" : "puff shape")}) removes {hits.Count} sprite(s):");
            foreach (var h in hits) Console.WriteLine("  " + h);
        }
        var keepWords = played.Where(p => SmokeRules.Traits(p.Element) is { } t && SmokeRules.Explain(t).Rule == 3)
            .GroupBy(p => SmokeRules.KeepTextures.First(w => SmokeRules.Traits(p.Element)!.Value.Material.ToLowerInvariant().Contains(w, StringComparison.Ordinal)))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        Console.WriteLine("rule 3 (alpha, kept by a texture word): word: textures (sprites)");
        foreach (var g in keepWords)
            Console.WriteLine($"  {g.Key}: {string.Join(", ", g.Select(p => SmokeRules.Traits(p.Element)!.Value.Material).Distinct())} ({g.Count()})");
        var holders = played.Where(p => SmokeRules.Classify(p.Element) == SmokeClass.Smoke && (p.Element.Children.Count > 0 || p.Element.OnCollision.Count > 0)).ToList();
        Console.WriteLine($"smoke sprites holding children or collision descriptions: {holders.Count}");
        foreach (var h in holders) Console.WriteLine($"  {Describe(h.System, h.Element)}: {h.Element.Children.Count} children, {h.Element.OnCollision.Count} on collision");
        if (failures.Count > 0) Console.WriteLine($"not parsed: {string.Join(", ", failures.Take(6))}");
        if (textures)
        {
            Console.WriteLine("per texture (removed / kept):");
            foreach (var (tex, (r, k)) in perTexture.OrderByDescending(kv => kv.Value.Removed + kv.Value.Kept)) Console.WriteLine($"  {tex,-48} {r,4} {k,4}");
        }
        return 0;
    }
}
