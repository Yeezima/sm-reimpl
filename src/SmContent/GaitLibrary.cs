using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>The locomotion data the client loads in one background task: the animation graphs' sprint and relaxed-run clips and the gait (<see cref="ClipGait"/>) of every clip the blend spaces use.</summary>
public sealed record LocomotionBundle(LocomotionAnimations Locomotion, GaitLibrary Gaits);

/// <summary>
/// The measured <see cref="ClipGait"/> of the marine rig's locomotion clips (M40-T4): the relaxed run's walk and run clips (rifle and pistol families), the
/// sprint loops, and the ready (hip-fire) posture's level-aim clips of every family. Computed once on a worker thread from the rig file (read-only; the clip
/// is sampled 64 times and the toes' foot-plant motion read: <see cref="ClipGait.Analyze"/>); the client looks the numbers up by clip name. A clip whose
/// analysis fails is absent (the caller falls back to the nearest-clip pose and says so once).
/// </summary>
public sealed class GaitLibrary
{
    readonly Dictionary<string, ClipGait> _clips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clips the analysis decoded (the worker's copies; read-only, so the client's rig cache can take them instead of decoding the same clips again on the main thread).</summary>
    readonly Dictionary<string, HavokBoundAnimation> _animations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rig the clips are of (the Havok packfile's entry name): the library is the Space Marine's; a client with another rig must not take its clips.</summary>
    public string RigKey { get; init; } = CharacterModel.MarineRigEntry;

    public IReadOnlyDictionary<string, ClipGait> Clips => _clips;
    public IReadOnlyDictionary<string, HavokBoundAnimation> Animations => _animations;
    public List<string> Notes { get; } = new();

    public ClipGait? Get(string clip) => _clips.GetValueOrDefault(clip);

    /// <summary>
    /// Reads the rig file and everything the client's locomotion needs from it. The gait part is optional: whatever goes wrong in it (a clip that does not
    /// decode, a graph the hip-fire reader cannot read) leaves an empty or partial library with a note, and the sprint and relaxed-run data still load
    /// (the client then shows the nearest-clip poses).
    /// </summary>
    public static LocomotionBundle Load(string gameDir)
    {
        byte[]? rig = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, CharacterModel.MarineRigArchive)))
            archive.ReadEntries(e => e.FileName == CharacterModel.MarineRigEntry, (_, d) => rig = d.ToArray());
        if (rig is null) throw new FileNotFoundException($"'{CharacterModel.MarineRigEntry}' not found.");
        var character = new HavokCharacterFile(rig);
        var locomotion = LocomotionAnimations.From(character);
        var gaits = Guard(() =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inPlace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in locomotion.Hammer is { } hammer ? locomotion.Relaxed.Append(hammer) : locomotion.Relaxed)   // anim/melee-flow: the hammer's own run
                foreach (var direction in set.Directions)
                {
                    if (direction.Walk is { } walk) names.Add(walk.Clip);
                    if (direction.Run is { } run) names.Add(run.Clip);
                    // The walk-on-the-spot clips (the speed 0 band) step in place: their gait is for the foot-plant phase only.
                    if (direction.WalkOnSpot is { } spot) { names.Add(spot.Clip); inPlace.Add(spot.Clip); }
                }
            foreach (var set in locomotion.Sprints)
                foreach (var clip in set.Clips) names.Add(clip.Clip);
            var hip = HipFireAnimations.From(character);
            foreach (var set in hip.Sets)
                foreach (var direction in set.Directions)
                    foreach (var band in direction.Bands)
                        if (band.Speed > 0f && band.Clips.MinBy(c => MathF.Abs(c.PitchDegrees)) is { } level) names.Add(level.Clip);
            return From(character, names, inPlace);
        });
        return new LocomotionBundle(locomotion, gaits);
    }

    /// <summary>Runs <paramref name="build"/>; any failure (but an out-of-memory) gives an empty library with a note instead of an exception.</summary>
    public static GaitLibrary Guard(Func<GaitLibrary> build)
    {
        try { return build(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            var empty = new GaitLibrary();
            empty.Notes.Add($"gait: not available ({ex.GetType().Name}: {ex.Message}); the locomotion shows the nearest-clip poses");
            return empty;
        }
    }

    /// <summary>The gaits of the named clips of a character file.</summary>
    public static GaitLibrary From(HavokCharacterFile character, IEnumerable<string> clipNames, IReadOnlySet<string>? inPlace = null) => From(character, clipNames, CharacterModel.MarineRigEntry, FootBones.Marine, inPlace);

    /// <summary>The gaits of the named clips of another rig's character file (Ork O7a): <paramref name="rigKey"/> is the packfile entry (<see cref="RigProfile.Entry"/>), <paramref name="feet"/> the contact bones, <paramref name="inPlace"/> the clips that step on the spot (see <see cref="Build"/>).</summary>
    public static GaitLibrary From(HavokCharacterFile character, IEnumerable<string> clipNames, string rigKey, FootBones feet, IReadOnlySet<string>? inPlace = null)
    {
        var available = new HashSet<string>(character.AnimationNames, StringComparer.OrdinalIgnoreCase);
        var decoded = new Dictionary<string, HavokBoundAnimation>(StringComparer.OrdinalIgnoreCase);
        var library = Build(clipNames, available.Contains, name =>
        {
            var bound = character.LoadAnimation(name);
            decoded[name] = bound;
            return ClipGait.Analyze(character.Skeleton, bound, feet: feet);
        }, rigKey, inPlace);
        foreach (var name in library._clips.Keys) library._animations[name] = decoded[name];
        return library;
    }

    /// <summary>
    /// The library from an analyser (testable without an install): a clip the analyser fails on, or gives no usable stride for, is left out with a note; one
    /// bad clip does not cost the others.
    /// </summary>
    /// <param name="inPlace">Clips that step on the spot (the walk-on-the-spot clips): kept when <see cref="ClipGait.UsableInPlace"/> instead of <see cref="ClipGait.Usable"/> (they have no stride; their sync times let the cycle share the walk's foot-plant phase).</param>
    public static GaitLibrary Build(IEnumerable<string> clipNames, Func<string, bool> inRig, Func<string, ClipGait?> analyse, string? rigKey = null, IReadOnlySet<string>? inPlace = null)
    {
        var library = rigKey is null ? new GaitLibrary() : new GaitLibrary { RigKey = rigKey };
        foreach (var name in clipNames)
        {
            if (!inRig(name)) { library.Notes.Add($"gait: clip {name} is not in the rig"); continue; }
            try
            {
                var gait = analyse(name);
                if (gait is { Usable: true } || (gait is { UsableInPlace: true } && inPlace?.Contains(name) == true)) library._clips[name] = gait;
                else library.Notes.Add($"gait: clip {name}: no usable stride (planted feet not found)");
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                library.Notes.Add($"gait: clip {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return library;
    }
}
