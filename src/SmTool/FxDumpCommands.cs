using SmContent;
using SmFormats.Fx;

namespace SmTool;

/// <summary>
/// <c>smtool fx-dump &lt;game-dir|auto&gt; &lt;name|word&gt;... [--list] [--fields]</c> (fx/impact-pass): the element tree of each named effect of <c>master_fx</c> (kind, name, material,
/// duration, looping, sizes, speeds, offsets; with <c>--fields</c> every field the description carries), or with <c>--list</c> the effect names containing each word.
/// Read-only; prints names and numbers, no original bytes.
/// </summary>
static class FxDumpCommands
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        bool list = rest.Contains("--list"), fields = rest.Contains("--fields");
        int mapAt = Array.IndexOf(rest, "--map");
        string? map = mapAt >= 0 && mapAt + 1 < rest.Length ? rest[mapAt + 1] : null;
        var words = rest.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && !(i > 0 && rest[i - 1] == "--map")).ToList();
        using var library = FxLibrary.Load(gameDir);
        if (map is not null)
        {
            // The map's own looping effects (base/fxobject, auto-triggered): name, how many are placed, and whether they hold god-ray beams (GodRayFade or a sky_beam material).
            var placed = MapEffects.Load(gameDir, map).Where(o => o.AutoTrigger && o.Fx.Length > 0).GroupBy(o => o.Fx).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in placed)
            {
                var sys = library.System(g.Key);
                var beams = sys?.Elements.OfType<FxBeam>().ToList() ?? [];
                int god = beams.Count(b => b.GodRayFade), sky = beams.Count(b => b.Material.Name.Contains("sky_beam", StringComparison.OrdinalIgnoreCase));
                int others = (sys?.Elements.Count(e => e is FxRing or FxBeam or FxStreamer) ?? 0) - beams.Count(b => b.GodRayFade || b.Material.Name.Contains("sky_beam", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"  {g.Key,-44} x{g.Count(),3}  god-ray beams {god}, sky_beam beams {sky}, other sprites {others}{(sys is null ? " (not in the package)" : "")}");
                if (rest.Contains("--where")) foreach (var o in g.Take(8)) Console.WriteLine($"      at {o.Position.X:F1} {o.Position.Y:F1} {o.Position.Z:F1} (game space) scale {o.Scale:0.##}");
            }
            return 0;
        }
        if (list)
        {
            foreach (var w in words)
            {
                var hits = library.SystemNames.Where(n => n.Contains(w, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Console.WriteLine($"'{w}': {hits.Count} effect(s)");
                foreach (var h in hits) Console.WriteLine("  " + h);
            }
            return 0;
        }
        foreach (var name in words)
        {
            if (library.System(name) is not { } sys) { Console.WriteLine($"{name}: not in the package"); continue; }
            Console.WriteLine($"{name}: {sys.Emitters.Count} emitter(s), lifetime {(sys.Lifetime is { } l ? $"{l:0.###} s" : "looping")}");
            foreach (var em in sys.Emitters)
            {
                Console.WriteLine($"  emitter '{em.Name}'{(em.RefNode.Length > 0 ? $" on '{em.RefNode}'" : "")}{(em.Hide ? " hidden" : "")}");
                foreach (var e in em.Elements) Dump(e, 2, fields, "");
            }
        }
        return 0;
    }

    static string C(FxCurve c) => c.IsEmpty ? "-" : c.Constant is { } k ? $"{k:0.###}" : $"{c.Keys[0].Value:0.###}..{c.Keys[^1].Value:0.###} [{c.Min:0.###},{c.Max:0.###}]";

    static void Dump(FxElement e, int depth, bool fields, string prefix)
    {
        string pad = new(' ', depth * 2);
        string head = $"{pad}{prefix}{e.Kind} '{e.Name}' dur {e.Duration:0.###}{(e.IsLooping ? " loop" : "")}{(e.Hide ? " HIDE" : "")}";
        string more = e switch
        {
            FxRing r => $" mat {r.Material.Name} add {r.Material.AdditiveBlend} r {C(r.Radius)} bb {r.Billboard} off {C(r.OffsetY)}",
            FxBeam b => $" mat {b.Material.Name} add {b.Material.AdditiveBlend} len {C(b.Length)} w {C(b.Width)} bb {b.Billboard} mpu {b.MeterPerU:0.###} uspd {b.USpeed:0.###} god {b.GodRayFade}",
            FxStreamer s => $" mat {s.Material.Name} add {s.Material.AdditiveBlend} len {C(s.Length)}{(s.LengthAsSeconds ? " s" : " m")} w {C(s.Width)}",
            FxSpray p => $" rate {C(p.Rate)} dev {C(p.Deviation)} spd {C(p.InitialSpeed)} pscale {C(p.ParticleScale)} vol {C(p.VolumeX)}/{C(p.VolumeY)}/{C(p.VolumeZ)} inherit {p.InheritVelocity:0.##} point {p.EmitFromPoint} dropper {p.IsDropper}",
            FxCombo c => $" scale {C(c.Scale)} off {C(c.OffsetX)}/{C(c.OffsetY)}/{C(c.OffsetZ)}{(c.OneAtRandom ? " one-at-random" : "")}",
            FxDecal d => $" diffuse {d.DiffuseMap} {d.Width:0.##}x{d.Height:0.##}x{d.Depth:0.##} scale {C(d.Scale)} add {d.AdditiveBlend}",
            FxLight l => $" atten {C(l.AttenEnd)} int {C(l.Intensity)} colour {(l.Colour.IsEmpty ? "-" : l.Colour.Keys[0].ToString())}",
            FxAudio a => $" event {a.EventName}",
            FxMesh m => $" mesh {m.Mesh} ({m.PackageName})",
            _ => "",
        };
        Console.WriteLine(head + more);
        if (fields) Console.WriteLine($"{pad}    fields: {string.Join(", ", e.FieldNames.Distinct())}");
        foreach (var c in e.Children) Dump(c, depth + 1, fields, "");
        foreach (var c in e.OnCollision) Dump(c, depth + 1, fields, "on collision: ");
    }
}
