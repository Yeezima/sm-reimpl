using System.Globalization;
using SmContent;
using SmFormats;
using SmFormats.Audio;

namespace SmTool;

/// <summary>
/// <c>smtool ork-sounds [game-dir|auto] [--tags] [--all-clips]</c> (Ork O8): what the data binds for the Ork Boy's sounds and barks, resolved against the banks. Per file
/// (the blueprint <c>ork_coop_boy</c> and the Ork wargear): every sound command (trigger, event, delay, voices one post resolves to) and every speech command (the voice
/// line the speech bank's dialogue tree gives for the Ork's character); then the behaviour tags of the boy rig's clips with how many clips carry each and whether a
/// file binds it to a sound (<c>sound</c>), to a line (<c>speech</c>), names it without one (<c>silent</c>) or not at all (<c>unbound</c>). Read-only; decodes nothing.
/// </summary>
static class OrkSoundCommands
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto" || gameDir.StartsWith("--")) { rest = gameDir.StartsWith("--") ? [gameDir, .. rest] : rest; gameDir = GameInstall.Locate(null, RepoRoot()); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: RepoRoot(), log: Console.Error);
        var tuning = GameplayTuning.Load(gameDir);
        var graphs = OrkSounds.ReadGraphs(gameDir);
        var triggers = SoundTriggers.Load(gameDir, OrkClass.Wargear, [OrkSounds.Blueprint], graphs);
        Console.WriteLine($"catalogue: {catalog.LoadedPackages.Count} packages, speech bank {(catalog.HasSpeech ? "loaded" : "NOT loaded")}; {triggers.Files.Count} files, " +
                          $"{triggers.ClipTags.Count} Ork clips with action tags, {sw.ElapsedMilliseconds} ms");
        foreach (var n in triggers.Notes) Console.WriteLine($"  note: {n}");
        string? character = triggers.SpeechCharacter(OrkClass.Key);
        Console.WriteLine($"speech character of the Ork class: {character ?? "(none)"}  (blueprint audio_ext/character)");

        foreach (var name in new[] { OrkSounds.Blueprint }.Concat(OrkClass.Wargear))
        {
            if (triggers.File(name) is not { } file) { Console.WriteLine($"\n{name}: not in the install"); continue; }
            Console.WriteLine($"\n== {name}: {file.Bindings.Count} sound commands, {file.Speech.Count} speech commands, {file.TriggeredActions.Count} triggered actions");
            foreach (var b in file.Bindings.Where(b => b.StartEvent.Length > 0 || b.StopEvent.Length > 0))
            {
                var ev = b.StartEvent.Length > 0 ? b.StartEvent : b.StopEvent;
                var play = catalog.Post(Fnv.Hash32(ev), SoundSwitches.None);
                string state = !play.Found ? "NOT IN BANKS" : b.StartEvent.Length == 0 ? $"stop ({play.Stops.Count} stops)" : $"{play.Voices.Count} voices";
                Console.WriteLine($"  sound  {b.Trigger,-34} {(b.IsDefault ? "" : "[" + string.Join(",", b.RequiredUpgrades) + "] ")}{ev,-34} {state}" +
                                  (b.DelayMilliseconds != 0 ? $" delay {b.DelayMilliseconds:0} ms" : "") + (b.IsFootstep ? " footstep" : "") + (b.UseTerrainMaterial ? " terrain" : "") +
                                  (b.Ability.Length > 0 ? $" ({b.Ability})" : ""));
            }
            foreach (var s in file.Speech)
            {
                string line = "no character";
                if (character is not null)
                {
                    var play = catalog.PostSpeech(s.Kind, character, s.Type, s.State, s.Threat.Length == 0 || s.Threat == "autodetect" ? "high" : s.Threat, new SoundSwitches());
                    line = !play.Found ? "NO SPEECH BANK" : play.Voices.Count == 0 ? "NO LINE IN THE TREE" : $"{play.Voices.Count} voice(s), {string.Join(", ", play.Voices.Select(v => $"{catalog.Duration(v.WemId).ToString("0.00", Inv)} s"))}";
                }
                Console.WriteLine($"  speech {s.Trigger,-34} {s.Kind} {(s.Type.Length > 0 ? s.Type : $"{s.State}/{s.Threat}"),-30} {line}" + (s.Ability.Length > 0 ? $" ({s.Ability})" : ""));
            }
        }

        // The tag table: the clips of the boy rig and the files that name each tag.
        var byTag = new SortedDictionary<string, List<(string Clip, float At)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clip, tags) in triggers.ClipTags)
            foreach (var (tag, at) in tags) { if (!byTag.TryGetValue(tag, out var l)) byTag[tag] = l = new(); l.Add((clip, at)); }
        Console.WriteLine($"\n== behaviour tags of the Ork rig's clips ({byTag.Count} tags)");
        var files = OrkClass.Wargear.Append(OrkSounds.Blueprint).Select(triggers.File).OfType<SoundFile>().ToList();
        foreach (var (tag, clips) in byTag)
        {
            string status = files.Any(f => f.Tag(tag).Any(b => b.StartEvent.Length > 0)) ? "sound" : files.Any(f => f.SpeechTag(tag).Count > 0) ? "speech"
                : files.Any(f => f.TriggeredActions.Contains(tag)) ? "silent" : "unbound";
            if (!rest.Contains("--all-tags") && status == "unbound") continue;
            string events = string.Join(" + ", files.SelectMany(f => f.Tag(tag)).Select(b => b.StartEvent).Where(e => e.Length > 0).Distinct().Concat(files.SelectMany(f => f.SpeechTag(tag)).Select(b => $"{b.Kind}:{b.Type}")));
            Console.WriteLine($"  {tag,-34} {status,-7} {clips.Count,3} clip(s)  {events}");
            if (rest.Contains("--clips")) foreach (var (clip, at) in clips.Take(6)) Console.WriteLine($"      {clip}@{at.ToString("0.###", Inv)}");
        }
        int unboundTags = byTag.Count(kv => !files.Any(f => f.TriggeredActions.Contains(kv.Key)));
        Console.WriteLine($"{byTag.Count} tags in total, {unboundTags} named by no Ork file (hidden; --all-tags lists them)");
        return 0;
    }

    static string? RepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        return null;
    }
}
