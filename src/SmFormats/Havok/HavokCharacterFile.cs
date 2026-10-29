namespace SmFormats.Havok;

/// <summary>
/// A character "hkxx" packfile (e.g. havok_sm_base_mp__hkxx): one skeleton, its animations, and behavior data,
/// combined from many source files. Animation names come from the hkCombinerTOC, whose entries list each source
/// file (name, type, count) in the order of the root container's variants after the TOC itself
/// (verified on the multiplayer Space Marine: 665 entries walk all 668 variants with no type mismatch).
/// </summary>
public sealed class HavokCharacterFile
{
    readonly Dictionary<string, int> _animationContainers;

    public HavokPackfile Pack { get; }
    public HavokSkeleton Skeleton { get; }
    /// <summary>Animation names (without the ".hkxa" extension), in file order.</summary>
    public IReadOnlyList<string> AnimationNames { get; }

    public HavokCharacterFile(byte[] file)
    {
        Pack = new HavokPackfile(file);
        var skeletons = Pack.OfClass("hkaSkeleton").Select(o => HavokSkeleton.Read(Pack, o.Offset)).ToList();
        if (skeletons.Count == 0) throw new InvalidDataException("Character packfile has no skeleton.");

        var root = Pack.OfClass("hkRootLevelContainer").FirstOrDefault()
            ?? throw new InvalidDataException("Character packfile has no root level container.");
        var (variantsAt, variantCount) = Pack.Array(root.Offset, 12);
        var variants = new List<(string? Class, int? Object)>(variantCount);
        for (int i = 0; i < variantCount; i++)
            variants.Add((Pack.String(variantsAt!.Value + 12 * i + 4), Pack.Pointer(variantsAt.Value + 12 * i + 8)));

        _animationContainers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var toc = Pack.OfClass("hkCombinerTOC").FirstOrDefault();
        if (toc is not null && variants.Count > 0 && variants[0].Class == "hkCombinerTOC")
        {
            var (entriesAt, entryCount) = Pack.Array(toc.Offset + 8, 40);
            int variant = 1;
            for (int i = 0; i < entryCount; i++)
            {
                int e = entriesAt!.Value + 40 * i;
                string name = Pack.String(e) ?? "", type = Pack.String(e + 12) ?? "";
                int count = Pack.ReadInt32(e + 32);
                if (count < 0 || (long)variant + count > variants.Count) throw new InvalidDataException($"TOC entry '{name}' runs past the root container.");
                if (type == "hkxa" && count == 1 && variants[variant].Class == "hkaAnimationContainer" && variants[variant].Object is { } container)
                {
                    _animationContainers[name] = container;
                    names.Add(name);
                }
                variant += count;
            }
        }
        AnimationNames = names;
        // The bound skeleton is the one animations refer to (the first is the main skeleton in every sample).
        Skeleton = skeletons[0];
    }

    /// <summary>Decodes one animation and its track → bone mapping.</summary>
    public HavokBoundAnimation LoadAnimation(string name)
    {
        if (!_animationContainers.TryGetValue(name, out int container)) throw new KeyNotFoundException($"No animation named '{name}'.");
        var (bindingsAt, bindingCount) = Pack.Array(container + 32);
        if (bindingCount != 1) throw new NotSupportedException($"Animation '{name}' has {bindingCount} bindings; expected 1.");
        int binding = Pack.Pointer(bindingsAt!.Value) ?? throw new InvalidDataException($"Animation '{name}': null binding.");
        int animation = Pack.Pointer(binding + 12) ?? throw new InvalidDataException($"Animation '{name}': binding without animation.");
        string? cls = Pack.Objects.FirstOrDefault(o => o.Offset == animation)?.ClassName;
        if (cls != "hkaSplineCompressedAnimation") throw new NotSupportedException($"Animation '{name}' is {cls}; only spline-compressed is supported.");
        var anim = HavokSplineAnimation.Read(Pack, animation);
        var (tracksAt, trackCount) = Pack.Array(binding + 16, 2);
        if (trackCount != 0 && trackCount != anim.TrackCount)
            throw new InvalidDataException($"Animation '{name}': binding maps {trackCount} tracks; the animation has {anim.TrackCount}.");
        var trackToBone = new short[anim.TrackCount];
        for (int t = 0; t < anim.TrackCount; t++)
        {
            trackToBone[t] = trackCount == 0 ? (short)t : Pack.ReadInt16(tracksAt!.Value + 2 * t);
            if (trackToBone[t] < 0 || trackToBone[t] >= Skeleton.BoneNames.Count)
                throw new InvalidDataException($"Animation '{name}': track {t} maps to bone {trackToBone[t]} of {Skeleton.BoneNames.Count}.");
        }
        return new HavokBoundAnimation(name, Pack.String(binding + 8), anim, trackToBone);
    }

    /// <summary>
    /// The animation binding's blend hint (<c>hkaAnimationBinding</c> byte @40, after the float-track array @28; Havok:
    /// 0 normal, 1 additive). Layout established on the install (M27): every one of the marine's 613 bindings reads 0 or 1
    /// there and the remaining bytes of the 48-byte object are zero padding.
    /// </summary>
    public AnimationBlendHint LoadBlendHint(string name)
    {
        if (!_animationContainers.TryGetValue(name, out int container)) throw new KeyNotFoundException($"No animation named '{name}'.");
        var (bindingsAt, bindingCount) = Pack.Array(container + 32);
        if (bindingCount != 1) throw new NotSupportedException($"Animation '{name}' has {bindingCount} bindings; expected 1.");
        int binding = Pack.Pointer(bindingsAt!.Value) ?? throw new InvalidDataException($"Animation '{name}': null binding.");
        byte hint = Pack.ReadByte(binding + 40);
        return hint switch
        {
            0 => AnimationBlendHint.Normal,
            1 => AnimationBlendHint.Additive,
            _ => throw new InvalidDataException($"Animation '{name}': blend hint {hint} (expected 0 or 1)."),
        };
    }

    /// <summary>Most samples an extracted motion may have (the evade rolls have 30 to 36).</summary>
    public const int MaxMotionSamples = 4096;

    /// <summary>
    /// The animation's extracted motion (root motion), or null when it has none. Layout (hk_2010, 32-bit; inferred from the
    /// data, docs/discovery.md): <c>hkaAnimation</c> holds a pointer to an <c>hkaDefaultAnimatedReferenceFrame</c> @24; that
    /// object has the up axis @16 and the forward axis @32 (vectors of 4 floats), the duration @48 and the samples @52 (an
    /// array of 4 floats each: translation X, Y, Z in the character's frame, metres (assumed), and W, read as the turn about
    /// the up axis; evenly spaced over the duration, the first at 0).
    /// </summary>
    public HavokExtractedMotion? LoadExtractedMotion(string name)
    {
        if (!_animationContainers.TryGetValue(name, out int container)) throw new KeyNotFoundException($"No animation named '{name}'.");
        var (bindingsAt, bindingCount) = Pack.Array(container + 32);
        if (bindingCount != 1) throw new NotSupportedException($"Animation '{name}' has {bindingCount} bindings; expected 1.");
        int binding = Pack.Pointer(bindingsAt!.Value) ?? throw new InvalidDataException($"Animation '{name}': null binding.");
        int animation = Pack.Pointer(binding + 12) ?? throw new InvalidDataException($"Animation '{name}': binding without animation.");
        if (Pack.Pointer(animation + 24) is not int frame) return null;
        string? cls = Pack.Objects.FirstOrDefault(o => o.Offset == frame)?.ClassName;
        if (cls != "hkaDefaultAnimatedReferenceFrame") throw new NotSupportedException($"Animation '{name}': extracted motion is {cls}; only hkaDefaultAnimatedReferenceFrame is supported.");
        System.Numerics.Vector4 V(int at) => new(Pack.ReadSingle(at), Pack.ReadSingle(at + 4), Pack.ReadSingle(at + 8), Pack.ReadSingle(at + 12));
        var (samplesAt, count) = Pack.Array(frame + 52, 16);
        if (count > MaxMotionSamples) throw new InvalidDataException($"Animation '{name}': {count} motion samples (at most {MaxMotionSamples}).");
        var samples = new System.Numerics.Vector4[count];
        for (int i = 0; i < count; i++) samples[i] = V(samplesAt!.Value + 16 * i);
        var up = V(frame + 16); var forward = V(frame + 32);
        float duration = Pack.ReadSingle(frame + 48);
        if (!float.IsFinite(duration) || duration < 0f || samples.Any(s => !float.IsFinite(s.X) || !float.IsFinite(s.Y) || !float.IsFinite(s.Z) || !float.IsFinite(s.W)))
            throw new InvalidDataException($"Animation '{name}': extracted motion has a non-finite or negative value.");
        return new HavokExtractedMotion(new System.Numerics.Vector3(up.X, up.Y, up.Z), new System.Numerics.Vector3(forward.X, forward.Y, forward.Z), duration, samples);
    }
}

public sealed record HavokBoundAnimation(string Name, string? SkeletonName, HavokSplineAnimation Animation, short[] TrackToBone);

/// <summary>
/// An animation's extracted motion (<see cref="HavokCharacterFile.LoadExtractedMotion"/>): the frame's up and forward axes,
/// the duration in seconds, and the samples (X, Y, Z translation from the clip's start in that frame; W the turn about up),
/// evenly spaced over the duration.
/// </summary>
public sealed record HavokExtractedMotion(System.Numerics.Vector3 Up, System.Numerics.Vector3 Forward, float Duration, IReadOnlyList<System.Numerics.Vector4> Samples);

/// <summary>How an animation is meant to be blended (<c>hkaAnimationBinding</c> blend hint).</summary>
public enum AnimationBlendHint { Normal = 0, Additive = 1 }
