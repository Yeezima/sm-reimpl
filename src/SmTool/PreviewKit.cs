using System.Net;
using System.Text;
using SmClient.ImpactRules;
using SmContent;

namespace SmTool;

/// <summary>What a preview player plays: posts of the data's events (<see cref="DataSource"/>) or one of the client's procedural melee hit sounds (<see cref="SynthSource"/>).</summary>
abstract record PreviewSource
{
    public abstract string Key { get; }
    public abstract string Stem { get; }
}

sealed record DataSource(IReadOnlyList<AudioRender.PostSpec> Specs, string Tag) : PreviewSource
{
    public override string Key => "data " + string.Join("|", Specs.Select(s => s.ToString()));
    public override string Stem => string.Join("+", Specs.Select(s => s.Event)) + "__" + Tag;
}

sealed record SynthSource(HitSound Sound, bool NoBass) : PreviewSource
{
    public override string Key => $"synth {Sound} {NoBass}";
    public override string Stem => $"synth_{(Sound == HitSound.Sizzle ? "A" : "B")}_{(NoBass ? "nobass" : "bass")}";
}

/// <summary>Renders preview sources to WAV files in a folder (once per distinct source) and writes the players for them. Shared by the ranged-confirm and the listening-page commands.</summary>
sealed class PreviewKit(AudioCatalog catalog, string root, string sub, int count, int channels)
{
    readonly Dictionary<string, List<string>> _done = new();
    readonly HashSet<string> _stems = new(StringComparer.OrdinalIgnoreCase);
    public int Files { get; private set; }
    public List<string> Silent { get; } = [];

    public static HitSound? SynthOf(string wargear) => MeleeHitSynth.Replaces(wargear);

    /// <summary>One rendition of a client procedural hit sound, at the client's hit scale (-6 dB) and without its per-hit speed and level variation. Seeds as the client's four renders (1000 x sound + n).</summary>
    public static AudioRender.Result RenderSynth(HitSound sound, bool noBass, int variant, int channels)
    {
        var pcm = MeleeHitSynth.Render(sound, 1000 * (int)sound + variant, noBass);
        var mix = OfflineMixer.Mix([new MixTrack(pcm, 1, MeleeHitSynth.SampleRate, 0f, 20f * MathF.Log10(0.5f), 0f)], OfflineMixer.DefaultRate, channels, 0.05f);
        float peak = OfflineMixer.Peak(mix);
        return new AudioRender.Result(true, 1, 1, 0, mix, OfflineMixer.DefaultRate, channels, peak > 0 ? 20f * MathF.Log10(peak) : float.NegativeInfinity, ["procedural melee hit sound"]);
    }

    /// <summary>The relative paths (from the page) of the source's variants, rendering them on first use; empty when the data gives no sound.</summary>
    public List<string> Render(PreviewSource source)
    {
        if (_done.TryGetValue(source.Key, out var known)) return known;
        string stem = AudioRender.SafeName(source.Stem);
        if (stem.Length > 110) stem = stem[..110];
        string unique = stem;
        for (int k = 2; !_stems.Add(unique); k++) unique = stem + "_" + k;
        var files = new List<string>();
        for (int n = 1; n <= count; n++)
        {
            var r = source switch
            {
                DataSource d => AudioRender.Render(catalog, d.Specs, channels),
                SynthSource s => RenderSynth(s.Sound, s.NoBass, n, channels),
                _ => throw new InvalidOperationException(),
            };
            if (!r.Found || r.Wems == 0) { Silent.Add(source.Stem); files.Clear(); break; }
            string rel = $"{sub}/{unique}__v{n}.wav";
            AudioRender.WriteWav(Path.Combine(root, rel), r);
            files.Add(rel);
            Files++;
        }
        return _done[source.Key] = files;
    }

    /// <summary>A player for v1 and, behind a small "more" toggle, v2 and v3.</summary>
    public static string Player(IReadOnlyList<string> files, string caption)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"p\"><span class=\"cap\">").Append(WebUtility.HtmlEncode(caption)).Append("</span>");
        if (files.Count == 0) sb.Append("<em class=\"none\">no sound</em>");
        else
        {
            sb.Append($"<audio controls preload=\"none\" src=\"{WebUtility.HtmlEncode(files[0])}\"></audio>");
            if (files.Count > 1)
            {
                sb.Append("<details><summary>more</summary>");
                for (int i = 1; i < files.Count; i++) sb.Append($"<audio controls preload=\"none\" src=\"{WebUtility.HtmlEncode(files[i])}\"></audio>");
                sb.Append("</details>");
            }
        }
        return sb.Append("</div>").ToString();
    }

    public const string Css = "body{font:14px/1.4 system-ui,sans-serif;margin:16px auto;max-width:1180px;padding:0 16px;color:#222;background:#f6f6f4}"
        + "h1{margin:8px 0}h2{margin:0 0 4px}.note{background:#fff7d6;border:1px solid #e3d48a;padding:8px 12px;border-radius:6px}"
        + ".card{background:#fff;border:1px solid #d6d6d0;border-radius:8px;padding:12px 16px;margin:18px 0}.sub{color:#666;font-size:12px;margin-bottom:8px}"
        + ".row{display:grid;grid-template-columns:200px 1fr 1fr;gap:12px;border-top:1px solid #eee;padding:8px 0}.lab{font-weight:600}.lab small,.ev{font-weight:400;color:#666;font-size:11px;display:block}"
        + ".col h4{margin:0 0 4px;font-size:12px;letter-spacing:.05em;color:#555}.now h4{color:#a0522d}.pl h4{color:#2d6a4f}.p{margin-bottom:6px}.cap{display:block;font-size:11px;color:#555}"
        + "audio{width:230px;height:30px;display:block}summary{cursor:pointer;font-size:11px;color:#666}.none{color:#a33;font-size:12px}code{background:#eee;padding:0 3px}"
        + "table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:4px 8px;vertical-align:top;text-align:left}.m{color:#555;font-size:12px}@media(max-width:800px){.row{grid-template-columns:1fr}}";
}
