using System.Globalization;
using SmContent;
using SmFormats;

namespace SmTool;

/// <summary>
/// Offline renders of one Wwise event post to PCM (shared by <c>smtool audio-render</c> and <c>smtool melee-hit-sounds --render</c>). The post is resolved by
/// <see cref="AudioCatalog"/> (random and switch containers by the catalogue's seed and the material), each voice is mixed by <see cref="VoiceMixer"/> at its
/// resolved volume (the node chain and the bus chain up to the master), pitch and delay, decoded from the install's media and summed by <see cref="OfflineMixer"/>.
/// Limits: no 3D attenuation or positioning (a flat mono or centred mix), no low-pass, no bus effects, no loops or repeats, no later Set/Reset property actions,
/// the speed RTPC fixed at 0.
/// </summary>
static class AudioRender
{
    /// <summary>The impact state group the client sets around a body hit (<c>PostPlayerHitSound</c>).</summary>
    public const uint ImpactStateGroup = 0x6f0f7f7c;

    /// <summary>The materials the hit sounds are rendered for: armour = the data's <c>ceramite_armour</c> case with the armour impact state, flesh = <c>flesh</c> with the broken-armour state.</summary>
    public static bool TryMaterial(string name, out string materialType, out string state)
    {
        switch (name.ToLowerInvariant())
        {
            case "armour": case "armor": case "ceramite_armour": materialType = "ceramite_armour"; state = "set_state_impact_armor"; return true;
            case "flesh": materialType = "flesh"; state = "set_state_impact_broken_armor"; return true;
            default: materialType = state = ""; return false;
        }
    }

    public sealed record Result(bool Found, int Voices, int Wems, int Missing, float[] Mix, int Rate, int Channels, float PeakDb, IReadOnlyList<string> VoiceLines);

    /// <summary>Resolves one post of <paramref name="eventName"/> for the material and renders it.</summary>
    public static Result Render(AudioCatalog catalog, string eventName, string material, bool local, int channels, RtpcVolumeCap cap = RtpcVolumeCaps.Default)
    {
        if (!TryMaterial(material, out string materialType, out string state)) throw new ArgumentException($"material '{material}' is not armour or flesh");
        return Render(catalog, [new PostSpec(eventName, materialType, state, null, local)], channels, cap);
    }

    /// <summary>
    /// One post to render: the event, the material switch (null: none set), an impact-state event to post first (the client's body-hit pair), a death state (group
    /// <see cref="ImpactSounds.DeathStateGroup"/>; null: none/cleared), the local-player switch, and the offset the client adds (<see cref="VoiceOffset"/>).
    /// </summary>
    public sealed record PostSpec(string Event, string? MaterialType = null, string? ImpactState = null, uint? DeathState = null, bool Local = false, VoiceOffset Offset = default);

    /// <summary>Resolves each post (in order, so random containers keep drawing) and mixes all their voices (and the extra tracks) into one buffer.</summary>
    public static Result Render(AudioCatalog catalog, IReadOnlyList<PostSpec> specs, int channels, RtpcVolumeCap cap = RtpcVolumeCaps.Default, IReadOnlyList<MixTrack>? extra = null)
    {
        var parameters = new Dictionary<uint, float> { [VoiceMixer.Speed] = 0f };
        var tracks = new List<MixTrack>(extra ?? []);
        var lines = new List<string>();
        int missing = 0, voices = 0;
        foreach (var spec in specs)
        {
            var beforeImpact = catalog.State(ImpactStateGroup);
            var beforeDeath = catalog.State(ImpactSounds.DeathStateGroup);
            if (spec.ImpactState is not null) catalog.Post(spec.ImpactState, new SoundSwitches());
            catalog.SetState(ImpactSounds.DeathStateGroup, spec.DeathState ?? AudioCatalog.NoneState);
            var sw = new SoundSwitches();
            if (spec.MaterialType is not null) sw.Set("material_type", spec.MaterialType);
            if (spec.Local) sw.Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue);
            SoundPlay play;
            try { play = catalog.Post(spec.Event, sw, 1); }
            finally
            {
                catalog.SetState(ImpactStateGroup, beforeImpact ?? AudioCatalog.NoneState);
                catalog.SetState(ImpactSounds.DeathStateGroup, beforeDeath ?? AudioCatalog.NoneState);
            }
            if (!play.Found) return new Result(false, 0, 0, 0, [], OfflineMixer.DefaultRate, channels, float.NegativeInfinity, []);
            voices += play.Voices.Count;
            foreach (var v in play.Voices)
            {
                var mix = VoiceMixer.Evaluate(v, null, 1, 0, null, parameters, cap).With(spec.Offset);
                var pcm = Decode(catalog, v.WemId, out int ch, out int rate);
                if (pcm is null) { missing++; lines.Add($"wem {v.WemId}: no media / decode failed"); continue; }
                tracks.Add(new MixTrack(pcm, ch, rate, v.Delay, mix.VolumeDb, mix.PitchCents, mix.Lpf));
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{spec.Event}: wem {v.WemId} {ch} ch {rate} Hz {pcm.Length / ch / (double)rate:0.000} s, delay {v.Delay:0.###} s, volume {mix.VolumeDb:0.0} dB, pitch {mix.PitchCents:0} c, lpf {mix.Lpf:0.#}, {(v.Is3D ? "3D (attenuation not applied)" : "2D")}{(v.LoopCount != 1 ? $", loop {v.LoopCount} (played once)" : "")}"));
            }
        }
        var mixed = OfflineMixer.Mix(tracks, OfflineMixer.DefaultRate, channels, 0.05f);
        float peak = OfflineMixer.Peak(mixed);
        return new Result(true, voices, tracks.Count, missing, mixed, OfflineMixer.DefaultRate, channels, peak > 0 ? 20f * MathF.Log10(peak) : float.NegativeInfinity, lines);
    }

    /// <summary>The wem decoded to interleaved float PCM through the catalogue's Ogg conversion (cached), or null.</summary>
    static float[]? Decode(AudioCatalog catalog, uint wem, out int channels, out int rate)
    {
        channels = 0; rate = 0;
        var ogg = catalog.Ogg(wem, out _);
        if (ogg is null) return null;
        try
        {
            using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), closeOnDispose: true);
            channels = reader.Channels; rate = reader.SampleRate;
            var all = new List<float>();
            var buffer = new float[4096 * Math.Max(1, channels)];
            int n;
            while ((n = reader.ReadSamples(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.AsSpan(0, n));
            return all.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException) { return null; }
    }

    public static void WriteWav(string path, Result r)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, OfflineMixer.WavBytes(OfflineMixer.ToPcm16(r.Mix), r.Channels, r.Rate));
    }

    public static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---------------------------------------------------------------- smtool audio-render

    public const string Help = """
        smtool audio-render <event> [--out <dir>] [--seed n] [--count k] [--material armour|flesh] [--local] [--mono] [--cap peak|node|voice|off] [--game <dir>]
          Renders what one post of a Wwise event sounds like, k variants (default 4), to <dir>\<event>__<material>__v<n>.wav (16-bit, 48 kHz, stereo or --mono).
          The post is resolved as the client does for a body hit: switch material_type = ceramite_armour (armour, the default) or flesh, with the matching impact state
          (set_state_impact_armor / set_state_impact_broken_armor); random and sequence containers pick by --seed (the k variants are k successive posts of one seeded catalogue, so a same-seed run repeats exactly); --local sets the local-player case.
          Each voice is mixed at its resolved volume (node chain + bus chain to the master, RTPC speed 0), pitch (cents, as a resample) and delay.
          Limits: NO 3D attenuation or positioning, low-pass values applied only by an offline two-pole approximation of the client filters (not exact), no bus effects (reverb, ducking), no loops or repeats, no later Set/Reset property actions; the
          sum is not normalised and hard-clipped. Default folder: %LOCALAPPDATA%\sm-reimpl\cache\previews\render (never inside the install or the repository).
        """;

    public static int Command(string eventName, string[] rest, string? repoRoot)
    {
        if (rest.Contains("--help")) { Console.WriteLine(Help); return 0; }
        string gameDir = GameInstall.Locate(Opt(rest, "--game"), repoRoot);
        string outDir = Path.GetFullPath(Opt(rest, "--out") ?? Path.Combine(AudioCache.Root, "previews", "render"));
        if (AudioCache.RefuseOutputDir(outDir, gameDir, repoRoot) is { } refusal) { Console.Error.WriteLine($"Refusing: {refusal}."); return 1; }
        string material = Opt(rest, "--material") ?? "armour";
        if (!TryMaterial(material, out _, out _)) { Console.Error.WriteLine("--material: armour or flesh"); return 2; }
        int seed = int.TryParse(Opt(rest, "--seed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 1;
        int count = int.TryParse(Opt(rest, "--count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c > 0 ? Math.Min(c, 64) : 4;
        int channels = rest.Contains("--mono") ? 1 : 2;
        var cap = RtpcVolumeCaps.Default;
        if (Opt(rest, "--cap") is { } capText && !RtpcVolumeCaps.TryParse(capText, out cap, out string? error)) { Console.Error.WriteLine($"--cap: {error}"); return 2; }
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: repoRoot, log: Console.Error, seed: seed);
        for (int n = 0; n < count; n++)
        {
            var r = Render(catalog, eventName, material, rest.Contains("--local"), channels, cap);
            if (!r.Found) { Console.Error.WriteLine($"event {eventName} is not in the loaded banks"); return 1; }
            string path = Path.Combine(outDir, $"{SafeName(eventName)}__{material}__v{n + 1}.wav");
            WriteWav(path, r);
            Console.WriteLine($"{path}: {r.Voices} voices ({r.Wems} rendered, {r.Missing} without media), peak {r.PeakDb:0.0} dBFS, {r.Mix.Length / r.Channels / (double)r.Rate:0.00} s");
            if (n == 0) foreach (var l in r.VoiceLines) Console.WriteLine("    " + l);
        }
        return 0;
    }

    static string? Opt(string[] rest, string name)
    {
        int i = Array.IndexOf(rest, name);
        return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
    }
}
