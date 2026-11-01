using SmContent;
using SmFormats;
using SmFormats.Audio;
using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmTool;

/// <summary>
/// <c>smtool musou-sounds [auto] [--files a,b,c]</c>: what the data binds for Musou Mode's enemies, resolved against the banks: each type's cues as the client plays them (<see cref="MusouSoundSet"/>), or
/// with <c>--files</c> every sound and speech command of those blueprints and wargear files. Read-only; decodes nothing.
/// </summary>
static class MusouSoundCommands
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto" || gameDir.StartsWith("--")) { rest = gameDir.StartsWith("--") ? [gameDir, .. rest] : rest; gameDir = GameInstall.Locate(null, RepoRoot()); }
        using var catalog = AudioCatalog.Open(gameDir, null, repoRoot: RepoRoot(), log: Console.Error);
        int at = Array.IndexOf(rest, "--files");
        if (at < 0 || at + 1 >= rest.Length) return Sets(gameDir, catalog);
        var names = rest[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Dump(gameDir, catalog, names);
        return 0;
    }

    /// <summary>Each enemy type's cues as the client builds them, with what one post resolves to; exit 1 when a type has no steps, voice or death.</summary>
    static int Sets(string gameDir, AudioCatalog catalog)
    {
        var triggers = SoundTriggers.Load(gameDir, MusouSoundSet.Wargear, MusouSoundSet.Blueprints, (IReadOnlyList<SmFormats.Havok.BehaviorGraph>?)null);
        foreach (var n in triggers.Notes) Console.WriteLine($"note: {n}");
        int gaps = 0;
        foreach (var files in MusouSoundSet.Files)
        {
            var set = MusouSoundSet.Build(files, triggers, catalog.HasEvent);
            Console.WriteLine($"\n== {files.Type}: {files.Blueprint}, {files.Melee}{(files.Gun is null ? "" : ", " + files.Gun)}{(files.Charge is null ? "" : ", " + files.Charge)}; character {set.Character ?? "(none)"}");
            foreach (var (name, cue) in set.Cues)
            {
                if (cue.Empty) { Console.WriteLine($"  {name,-13} -"); continue; }
                var options = cue.Options.Select(o => string.Join(" + ", o.Events.Select(b => $"{b.StartEvent}({catalog.Post(Fnv.Hash32(b.StartEvent), SoundSwitches.None).Voices.Count}v{(catalog.Post(Fnv.Hash32(b.StartEvent), SoundSwitches.None).Voices.Exists(v => v.LoopCount == 0) ? " LOOP" : "")})")
                    .Concat(o.Lines.Select(l => $"{(l.Type.Length > 0 ? l.Type : l.State)}:{Line(catalog, set.Character, l)}"))));
                Console.WriteLine($"  {name,-13} {string.Join(" | ", options)}");
            }
            if (set.Missing.Count > 0) Console.WriteLine($"  not in any loaded bank: {string.Join(", ", set.Missing)}");
            if (set.StepLeft.Empty || set.DeathYell.Empty || set.Grunt.Empty && set.Bark.Empty) gaps++;
        }
        return gaps == 0 ? 0 : 1;
    }

    static string Line(AudioCatalog catalog, string? character, SpeechBinding l)
    {
        if (character is null) return "no character";
        var play = catalog.PostSpeech(l.Kind, character, l.Type, l.State, l.Threat.Length == 0 || l.Threat == "autodetect" ? "high" : l.Threat, new SoundSwitches());
        return !play.Found ? "no bank" : play.Voices.Count == 0 ? "NO LINE" : $"{catalog.Duration(play.Voices[0].WemId):0.0}s";
    }

    static void Dump(string gameDir, AudioCatalog catalog, string[] names)
    {
        var triggers = SoundTriggers.Load(gameDir, names, names, (IReadOnlyList<SmFormats.Havok.BehaviorGraph>?)null);
        var characters = Characters(gameDir, names);
        string? voice = null;
        foreach (var name in names)
        {
            if (triggers.File(name) is not { } file) { Console.WriteLine($"\n{name}: not in the install"); continue; }
            voice = characters.GetValueOrDefault(name) ?? voice;
            Console.WriteLine($"\n== {name}: character {characters.GetValueOrDefault(name) ?? "(none)"}; {file.Bindings.Count} sound commands, {file.Speech.Count} speech commands");
            foreach (var group in file.Bindings.Where(b => b.StartEvent.Length > 0).GroupBy(b => b.StartEvent, StringComparer.OrdinalIgnoreCase))
            {
                var play = catalog.Post(Fnv.Hash32(group.Key), SoundSwitches.None);
                var triggersOf = group.Select(b => b.Trigger + (b.Ability.Length > 0 ? $"({b.Ability})" : "")).Distinct().ToList();
                Console.WriteLine($"  sound  {group.Key,-38} {(play.Found ? play.Voices.Count + " voices" : "NOT IN BANKS"),-14} {string.Join(" ", triggersOf.Take(5))}{(triggersOf.Count > 5 ? $" +{triggersOf.Count - 5}" : "")}");
            }
            foreach (var s in file.Speech)
            {
                string line = "no character";
                if (voice is not null)
                {
                    var play = catalog.PostSpeech(s.Kind, voice, s.Type, s.State, s.Threat.Length == 0 || s.Threat == "autodetect" ? "high" : s.Threat, new SoundSwitches());
                    line = !play.Found ? "NO SPEECH BANK" : play.Voices.Count == 0 ? "NO LINE" : $"{play.Voices.Count} voice(s), {string.Join(", ", play.Voices.Select(v => $"{catalog.Duration(v.WemId):0.00} s"))}";
                }
                Console.WriteLine($"  speech {s.Trigger,-38} {s.Kind} {(s.Type.Length > 0 ? s.Type : $"{s.State}/{s.Threat}"),-26} {line}");
            }
        }
    }

    static Dictionary<string, string> Characters(string gameDir, string[] names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage));
        archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) =>
        {
            if (OrkSounds.SpeechCharacter(BafDocument.Read(d)) is { } c) found.TryAdd(e.FileName, c);
        });
        return found;
    }

    static string? RepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        return null;
    }
}
