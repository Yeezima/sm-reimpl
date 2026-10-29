using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// The multiplayer rig and its decoded animations, loaded once for every character whatever it wears (customizer T6): the Havok packfile (skeleton and
/// animation names), the animations decoded on first use and cached by name. The armour pieces are not here (<see cref="PieceCache"/>); a
/// <see cref="MarineAssets"/> (what a <see cref="CharacterViewer"/> shows) joins a rig with the pieces of one appearance.
/// </summary>
public sealed class RigAssets
{
    readonly Dictionary<string, HavokBoundAnimation?> _animations = new(StringComparer.OrdinalIgnoreCase);

    public HavokCharacterFile Rig { get; }
    /// <summary>Which rig this is (the Havok packfile's entry name: <see cref="CharacterModel.MarineRigEntry"/> for the Space Marine; a rig of another faction sets its own): animations decoded elsewhere are taken only from the same rig (<see cref="Preload"/>).</summary>
    public string RigKey { get; init; } = CharacterModel.MarineRigEntry;
    public List<string> Notes { get; }
    /// <summary>The skeleton's bone names (armour pieces bind to them by name).</summary>
    public IReadOnlySet<string> BoneNames { get; }

    public RigAssets(HavokCharacterFile rig, List<string>? notes = null)
    {
        Rig = rig; Notes = notes ?? new List<string>();
        BoneNames = new HashSet<string>(rig.Skeleton.BoneNames, StringComparer.Ordinal);
    }

    /// <summary>Reads the multiplayer Space Marine rig from the install.</summary>
    public static RigAssets Load(string gameDir, List<string>? notes = null) => new(CharacterModel.LoadMarineRig(gameDir), notes);

    /// <summary>Reads another character rig of the install (Ork O3: <see cref="RigProfiles.OrkBoy"/>); its animations are decoded on first use like the marine's.</summary>
    public static RigAssets Load(string gameDir, RigProfile profile, List<string>? notes = null) => new(CharacterModel.LoadRig(gameDir, profile), notes) { RigKey = profile.Entry };

    /// <summary>The named animation, decoded once; null (with a note) when it is missing or cannot be decoded.</summary>
    public HavokBoundAnimation? Animation(string name)
    {
        if (_animations.TryGetValue(name, out var cached)) return cached;
        HavokBoundAnimation? animation = null;
        try { animation = Rig.LoadAnimation(name); }
        catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or System.IO.InvalidDataException)
        {
            Notes.Add($"animation '{name}': {ex.Message}");
        }
        _animations[name] = animation;
        return animation;
    }

    /// <summary>
    /// Adds an animation decoded elsewhere (the locomotion worker's; read-only) so that <see cref="Animation"/> does not decode it again; ignored (false) when it comes
    /// from another rig (<paramref name="rigKey"/> against <see cref="RigKey"/>: a clip name on the Ork rig is another animation) or the name is already cached.
    /// </summary>
    public bool Preload(string rigKey, string name, HavokBoundAnimation animation) =>
        string.Equals(rigKey, RigKey, StringComparison.OrdinalIgnoreCase) && _animations.TryAdd(name, animation);

    /// <summary>The first animation name that exists among the candidates, else the first containing <paramref name="fallbackContains"/>.</summary>
    public string? FindAnimation(IEnumerable<string> candidates, string fallbackContains)
    {
        var names = Rig.AnimationNames;
        foreach (var c in candidates)
            if (names.Contains(c, StringComparer.OrdinalIgnoreCase)) return c;
        return names.FirstOrDefault(n => n.Contains(fallbackContains, StringComparison.OrdinalIgnoreCase));
    }
}
