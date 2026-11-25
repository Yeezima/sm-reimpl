using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using SmContent;
using SmContent.Extras.Executions;
using SmFormats.Havok;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X5: the client's execution catalogue and decoded clips, read once from the player's own install (read-only) on a worker thread at the first executions frame.
/// The catalogue is <see cref="ExecutionCatalogueLoader.Load"/>'s (never <see cref="ExecutionRows.All"/> as it is), resolved by the Start's wire index with
/// <see cref="ExecutionCatalogue.AtWire"/> (null: play nothing). The attacker clips come from the campaign player rig and the victim clips from the victim kind's rig
/// (<see cref="ExecutionCatalogueLoader.AttackerProfile"/>, <see cref="ExecutionCatalogueLoader.VictimProfile"/>), decoded here and added to a character's rig under
/// <see cref="Alias"/> names (<see cref="Prepare"/>), so a clip of the same name already on that rig (O8's <c>SM_MEL_CK_XATK_HVY_3.5m</c> exists on the MP rig too) is never played
/// in its place. A clip is added only to a rig whose skeleton has the source's bones in the same order (no retargeting).
/// </summary>
public sealed class ExecutionClipLibrary
{
    /// <summary>One decoded clip: the animation, its source skeleton's bones, whether its root travel is extracted motion (the skeleton plays in place, the node carries the path) and the path.</summary>
    public sealed record Clip(HavokBoundAnimation Animation, IReadOnlyList<string> Bones, bool InPlace, IReadOnlyList<Vector4> Root);

    /// <summary>The prefix of the names the clips are added to a rig under.</summary>
    public const string AliasPrefix = "exec:";
    public static string Alias(string clip) => AliasPrefix + clip;

    readonly Dictionary<string, Clip?> _attacker = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string Kind, string Clip), Clip?> _victim = new();
    readonly HashSet<(RigAssets, string)> _prepared = new();
    readonly HashSet<(RigAssets, string)> _refused = new();
    Task? _load;

    /// <summary>The install's catalogue (null until <see cref="Ready"/>; empty without an install).</summary>
    public ExecutionCatalogue? Catalogue { get; private set; }
    public bool Ready => _load is { IsCompleted: true };
    /// <summary>Notes of the load (rows left out, clips missing), printed once.</summary>
    public List<string> Notes { get; } = new();

    public void Start(string gameDir)
    {
        if (_load is not null) return;
        _load = Task.Run(() => Load(gameDir));
    }

    /// <summary>For tests and the self-test: a library over rows already filled, no install.</summary>
    public void UseCatalogue(ExecutionCatalogue catalogue)
    {
        Catalogue = catalogue;
        _load = Task.CompletedTask;
    }

    void Load(string gameDir)
    {
        try
        {
            var catalogue = ExecutionCatalogueLoader.Load(gameDir, Notes);
            HavokCharacterFile? attacker = null;
            try { attacker = ExecutionCatalogueLoader.AttackerProfile.LoadCharacter(gameDir); }
            catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or KeyNotFoundException or UnauthorizedAccessException) { Notes.Add($"executions: attacker rig unreadable ({e.Message})"); }
            var victims = new Dictionary<string, HavokCharacterFile?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in catalogue.Rows)
            {
                if (attacker is not null)
                {
                    Decode(_attacker, row.AttackerClip, attacker);
                    foreach (var v in row.AttackerVariants) Decode(_attacker, v.Clip, attacker);
                }
                if (!victims.TryGetValue(row.Target, out var victim))
                {
                    victim = null;
                    if (ExecutionCatalogueLoader.VictimProfile(row.Target) is { } profile)
                        try { victim = profile.LoadCharacter(gameDir); }
                        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or KeyNotFoundException or UnauthorizedAccessException) { Notes.Add($"executions: victim rig '{row.Target}' unreadable ({e.Message})"); }
                    victims[row.Target] = victim;
                }
                if (victim is not null && !_victim.ContainsKey((row.Target.ToLowerInvariant(), row.VictimClip)))
                    _victim[(row.Target.ToLowerInvariant(), row.VictimClip)] = Read(victim, row.VictimClip);
            }
            Catalogue = catalogue;
            Notes.Add($"executions: {catalogue.Rows.Count} of {ExecutionRows.All.Count} rows in this install ({string.Join(", ", catalogue.Rows.Select(r => r.Id))}); {_attacker.Count(a => a.Value is not null)} attacker and {_victim.Count(v => v.Value is not null)} victim clips decoded");
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            Notes.Add($"executions: the catalogue could not be read ({e.GetType().Name}: {e.Message}); no execution is drawn");
            Catalogue = ExecutionCatalogue.Empty;
        }
    }

    void Decode(Dictionary<string, Clip?> into, string clip, HavokCharacterFile source)
    {
        if (!into.ContainsKey(clip)) into[clip] = Read(source, clip);
    }

    Clip? Read(HavokCharacterFile source, string clip)
    {
        try
        {
            var info = ExecutionClipReader.Read(source, clip, []);
            var anim = source.LoadAnimation(clip);
            return new Clip(anim, source.Skeleton.BoneNames.ToArray(), info.RootSource == ExecutionClipReader.ExtractedMotion, info.Root);
        }
        catch (Exception e) when (e is KeyNotFoundException or System.IO.InvalidDataException or NotSupportedException)
        {
            Notes.Add($"executions: clip {clip} unreadable ({e.Message})");
            return null;
        }
    }

    public Clip? Attacker(string clip) => _attacker.GetValueOrDefault(clip);
    public Clip? Victim(string kind, string clip) => _victim.GetValueOrDefault((kind.ToLowerInvariant(), clip));

    /// <summary>
    /// Adds <paramref name="clip"/> to <paramref name="rig"/> under its <see cref="Alias"/> (once); false when the clip is missing or the rig's skeleton is not the source's
    /// (the caller then plays the labelled stand-in).
    /// </summary>
    public bool Prepare(RigAssets rig, Clip? clip, string name)
    {
        if (clip is null) return false;
        var key = (rig, name);
        if (_prepared.Contains(key)) return true;
        if (_refused.Contains(key)) return false;
        var bones = rig.Rig.Skeleton.BoneNames;
        bool same = bones.Count == clip.Bones.Count && bones.SequenceEqual(clip.Bones);
        if (!same || !rig.Preload(rig.RigKey, Alias(name), clip.Animation) && !ReferenceEquals(rig.Animation(Alias(name)), clip.Animation))
        {
            _refused.Add(key);
            Notes.Add($"executions: {name} not added to rig {rig.RigKey} ({(same ? "the name is taken" : $"skeleton {bones.Count} bones, source {clip.Bones.Count}")}); stand-in");
            return false;
        }
        _prepared.Add(key);
        return true;
    }
}
