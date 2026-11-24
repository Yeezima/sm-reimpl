using System.Numerics;
using SmFormats.Havok;
using SmSim.Extras.Executions;

namespace SmContent.Extras.Executions;

/// <summary>A timed tag a sequencer that plays an execution clip carries (seconds of the playing clip: fractions × length / playback speed).</summary>
/// <param name="Tag">The tag's kind (<see cref="BehaviorTag.Kind"/>: "Event", "Branch", "Action" …).</param>
public sealed record ExecutionClipEvent(string Tag, string Name, float Start, float End);

/// <summary>A sequencer that plays an execution clip: its graph, its name, the clip's playback speed there, the playing length (seconds) and its named tags in time order.</summary>
public sealed record ExecutionClipUse(string Graph, string Sequencer, float PlaybackSpeed, float Seconds, IReadOnlyList<ExecutionClipEvent> Events);

/// <summary>
/// What <see cref="ExecutionClipReader"/> reads of one clip: its length (seconds, the clip's own), frames, the root path (<see cref="Root"/>: X right, Y forward,
/// Z up in metres from the clip's start, W the turn about up in radians, one sample per motion sample or frame) and where that path came from
/// (<see cref="RootSource"/>: "extracted motion", "root bone" or "none"), and every sequencer that plays it (<see cref="Uses"/>, graph order).
/// </summary>
public sealed record ExecutionClip(string Clip, float Seconds, int Frames, IReadOnlyList<Vector4> Root, string RootSource, IReadOnlyList<ExecutionClipUse> Uses);

/// <summary>
/// Executions X2: a read-only reader over <see cref="HavokCharacterFile"/> and <see cref="HavokSplineAnimation"/> for the execution clips. The install's clips have
/// no annotation tracks (docs/discovery.md "Events"); the timing is the behaviour graphs' sequencer tags, so the events come from every sequencer that plays the
/// clip. The root path is the clip's extracted motion when it has one (<see cref="HavokCharacterFile.LoadExtractedMotion"/>), else the track of the skeleton's
/// root bone sampled at each frame. Which tag is the sync, contact, kill or end point is the catalogue row's (X7), not this reader's; <see cref="Guess"/> is
/// only a first reading of a name (<b>interpretation</b>).
/// </summary>
public static class ExecutionClipReader
{
    /// <summary>Reads <paramref name="clip"/> from <paramref name="character"/> with the sequencers of <paramref name="graphs"/> (null: read them from the same packfile).</summary>
    /// <exception cref="KeyNotFoundException">The clip is not in the packfile.</exception>
    public static ExecutionClip Read(HavokCharacterFile character, string clip, IReadOnlyList<BehaviorGraph>? graphs = null)
    {
        var bound = character.LoadAnimation(clip);
        var anim = bound.Animation;
        IReadOnlyList<Vector4> root;
        string source;
        var motion = character.LoadExtractedMotion(clip);
        // As EvadeTuning: the samples are read in the character's frame only when the reference frame is +Z up, +Y forward.
        if (motion is { Samples.Count: > 0 } && (Vector3.Dot(Vector3.Normalize(motion.Up), Vector3.UnitZ) < 0.999f || Vector3.Dot(Vector3.Normalize(motion.Forward), Vector3.UnitY) < 0.999f))
            throw new NotSupportedException($"'{clip}': the extracted motion's frame is not +Z up, +Y forward.");
        if (motion is { Samples.Count: > 0 })
        {
            root = motion.Samples.ToArray();
            source = "extracted motion";
        }
        else if (Array.IndexOf(bound.TrackToBone, (short)0) is int track and >= 0)
        {
            int frames = Math.Clamp(anim.FrameCount, 1, HavokCharacterFile.MaxMotionSamples);
            var path = new Vector4[frames];
            float end = MathF.Max(0f, anim.Duration - 1e-4f);
            Vector3 first = default;
            for (int f = 0; f < frames; f++)
            {
                var t = anim.Sample(frames == 1 ? 0f : end * f / (frames - 1))[track];
                if (f == 0) first = t.Translation;
                var d = t.Translation - first;
                path[f] = new Vector4(d, Yaw(t.Rotation));
            }
            root = path;
            source = "root bone";
        }
        else { root = []; source = "none"; }
        graphs ??= HavokBehavior.Read(character.Pack);
        return new ExecutionClip(clip, anim.Duration, anim.FrameCount, root, source, Uses(clip, graphs, name => character.LoadAnimation(name).Animation.Duration));
    }

    /// <summary>The turn about up (Z) of a rotation, radians (0 faces +Y, positive turns left).</summary>
    public static float Yaw(Quaternion q)
    {
        var f = Vector3.Transform(Vector3.UnitY, q);
        return new Vector2(f.X, f.Y).LengthSquared() < 1e-10f ? 0f : MathF.Atan2(-f.X, f.Y);
    }

    /// <summary>
    /// Every sequencer of <paramref name="graphs"/> that plays <paramref name="clip"/> (case aside), with its named tags in seconds: the sequencer's playing length
    /// is its first clip's length (<paramref name="lengthOf"/>) over that clip's playback speed, as <see cref="MeleeMotion"/> reads it. A length that cannot be read leaves the sequencer out.
    /// </summary>
    public static IReadOnlyList<ExecutionClipUse> Uses(string clip, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> lengthOf)
    {
        var uses = new List<ExecutionClipUse>();
        foreach (var g in graphs)
            foreach (var seq in g.Sequencers)
            {
                int index = -1;
                for (int i = 0; i < seq.Animations.Count; i++) if (string.Equals(seq.Animations[i], clip, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                if (index < 0) continue;
                float speed = Speed(seq, index), firstSpeed = Speed(seq, 0);
                float length;
                try { length = lengthOf(seq.Animations[0]) / firstSpeed; }
                catch (Exception e) when (e is KeyNotFoundException or InvalidDataException or NotSupportedException) { continue; }
                if (!float.IsFinite(length) || length <= 0f) continue;
                var events = seq.Tags.Where(t => !string.IsNullOrEmpty(t.Name) && float.IsFinite(t.Start) && float.IsFinite(t.End))
                    .Select(t => new ExecutionClipEvent(t.Kind, t.Name!, t.Start * length, t.End * length))
                    .OrderBy(e => e.Start).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
                uses.Add(new ExecutionClipUse(g.Name, seq.Name, speed, length, events));
            }
        return uses;

        static float Speed(BehaviorSequencer seq, int i) =>
            i < seq.PlaybackSpeeds.Count && float.IsFinite(seq.PlaybackSpeeds[i]) && seq.PlaybackSpeeds[i] > 0f ? seq.PlaybackSpeeds[i] : 1f;
    }

    /// <summary>
    /// A first reading of a tag name as an execution point (<b>interpretation</b> of the names; X7 sets each row's events from the inventory): names with "kill",
    /// "death" or "die" are the kill; "hit", "impact" or "contact" the contact; a "SYNC_" name other than a foot sync the sync; "end", "exit" or "finish" the end;
    /// anything else <see cref="ExecutionEventKind.Other"/>.
    /// </summary>
    public static ExecutionEventKind Guess(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.StartsWith("sync_") && !n.Contains("foot")) return ExecutionEventKind.Sync;
        if (n.Contains("kill") || n.Contains("death") || n.Contains("dead") || n.Contains("_die")) return ExecutionEventKind.Kill;
        if (n.Contains("hit") || n.Contains("impact") || n.Contains("contact")) return ExecutionEventKind.Contact;
        if (n.Contains("end") || n.Contains("exit") || n.Contains("finish")) return ExecutionEventKind.End;
        return ExecutionEventKind.Other;
    }

    /// <summary>
    /// <paramref name="row"/> with its lengths and root paths from the clips read (<paramref name="attacker"/>, <paramref name="victim"/>), the lengths over the
    /// playback speed of the row's own sequencer when the clip names it (the victim's: of <paramref name="victimSequencer"/> when given, else 1), and the
    /// attacker's distance variants from <paramref name="variants"/>. A variant's distance, unless given: the reference clip's is the row's authored gap
    /// <see cref="ExecutionRow.Offset"/>.Y; another's is that gap plus the difference of its forward root travel from the reference clip's (each variant covers the
    /// extra gap with its own travel: the slam's 4.39 / 6.89 / 10.40 m give 1.0 / 3.5 / 7.0 m); without root travel the name's <c>…_3.5m</c>
    /// (<see cref="MeleeMotion.DistanceOf"/>). The row's events, offset, yaw and source are unchanged.
    /// </summary>
    public static ExecutionRow Fill(ExecutionRow row, ExecutionClip attacker, ExecutionClip victim, IEnumerable<(ExecutionClip Clip, float? Distance)>? variants = null,
        string? victimSequencer = null)
    {
        float Length(ExecutionClip clip, string? sequencer) =>
            clip.Seconds / (sequencer is not null && clip.Uses.FirstOrDefault(u => string.Equals(u.Sequencer, sequencer, StringComparison.OrdinalIgnoreCase)) is { } use ? use.PlaybackSpeed : 1f);
        float? Travel(ExecutionClip clip) => clip.Root.Count > 1 ? clip.Root[^1].Y - clip.Root[0].Y : null;
        float? reference = Travel(attacker);
        float DistanceOf(ExecutionClip v)
        {
            if (string.Equals(v.Clip, row.AttackerClip, StringComparison.OrdinalIgnoreCase)) return row.Offset.Y;
            return reference is { } r && Travel(v) is { } t ? row.Offset.Y + (t - r) : MeleeMotion.DistanceOf(v.Clip);
        }
        var list = variants?.Select(v => new ExecutionVariant(v.Clip.Clip, v.Distance ?? DistanceOf(v.Clip), v.Clip.Root)).ToArray();
        return row with
        {
            AttackerSeconds = Length(attacker, row.Sequencer), VictimSeconds = Length(victim, victimSequencer ?? row.VictimSequencer),
            AttackerRoot = attacker.Root, VictimRoot = victim.Root,
            AttackerVariants = list ?? row.AttackerVariants,
        };
    }
}
