namespace SmContent;

/// <summary>
/// A node's "limit sound instances to N" (the advanced settings of a sound, container or actor-mixer in the Wwise banks, v56):
/// <see cref="Max"/> instances at once (0: none, the node may still ignore its parents' limits); the oldest is killed when a new one starts, or the new one is dropped
/// with <see cref="KillNewest"/>; counted per emitter (game object), or across all emitters with <see cref="Global"/>; <see cref="IgnoreParent"/> stops the search
/// for limits at this node (the limits of its parents do not apply to what plays below it). The byte order of the flags is an interpretation of the bank layout
/// (docs/discovery.md, "Footsteps").
/// </summary>
public sealed record InstanceLimit(int Max, bool KillNewest, bool Global, bool IgnoreParent);

/// <summary>
/// What <see cref="InstanceLimiter.Decide"/> asks for: drop the new voice (then <see cref="Kill"/> is empty), or kill these playing voices. <see cref="Instances"/> names
/// the (post, node) pairs that were killed, so the caller can also cancel those posts' delayed voices below the node.
/// </summary>
public sealed record LimitDecision(bool DropNew, IReadOnlyList<int> Kill, string Reason, IReadOnlyList<(long Post, uint Node)> Instances)
{
    public static readonly LimitDecision None = new(false, [], "", []);
}

/// <summary>
/// The "limit sound instances" rule (engine-free so it is tested without Godot). **Interpretation** of Wwise's model, values from the banks:
/// a node's instance is one play of it (the voices a single post started below it count as one instance, so a layer container with two sounds and a
/// limit of 1 plays both and is replaced as a whole by the next post: the authors' evident intent; counting each sound would let a layer kill itself).
/// For a new voice, every node of its chain (the sound first, then each parent) that has a limit is checked, up to and including the first node that
/// ignores its parents' limits: the instances of that node still playing (voices of other posts whose chain holds the node; the same emitter unless the
/// limit is global) are counted. At the limit the oldest instance is killed (all of its voices below the node). Age is the post's number (posts are numbered in the
/// order they were made, not the order their voices start: a voice that decoded late must not kill a newer post's instance): when the new voice's own post is older than
/// the oldest instance it is the one to go, so it is dropped. Kill-newest drops the new voice instead. A drop cancels every kill chosen for it.
/// </summary>
public static class InstanceLimiter
{
    /// <summary>One voice that is playing (or waiting as a virtual voice): its identity for the caller, emitter, post and chain.</summary>
    public readonly record struct Playing(int Handle, ulong Emitter, long Post, IReadOnlyList<VoiceNode> Chain);

    /// <param name="chain">The new voice's chain (<see cref="SoundVoice.Chain"/>).</param>
    /// <param name="emitter">The emitter the voice plays for.</param>
    /// <param name="post">The post (or continuation) that made the voice, numbered in posting order: voices of one post never count against each other.</param>
    /// <param name="playing">The voices playing now, those fading out already left out.</param>
    public static LimitDecision Decide(IReadOnlyList<VoiceNode> chain, ulong emitter, long post, IReadOnlyList<Playing> playing)
    {
        List<int>? kill = null;
        List<(long, uint)>? killed = null;
        string reason = "";
        foreach (var node in chain)
        {
            if (node.Limit is { Max: > 0 } limit)
            {
                // The instances of this node: voices of other posts below it, grouped by post (one instance each), skipping those already chosen to die; oldest post first.
                var instances = playing
                    .Where(p => p.Post != post && (limit.Global || p.Emitter == emitter) && !(kill?.Contains(p.Handle) ?? false) && p.Chain.Any(n => n.Id == node.Id))
                    .GroupBy(p => p.Post)
                    .OrderBy(g => g.Key)
                    .Select(g => (Post: g.Key, Voices: g.ToList()))
                    .ToList();
                int count = instances.Count, next = 0;
                while (count >= limit.Max && next < instances.Count)
                {
                    if (limit.KillNewest)
                        return new LimitDecision(true, [], $"limit {limit.Max} of node {node.Id:x8} reached (kill newest): new voice dropped", []);
                    if (post < instances[next].Post)
                        return new LimitDecision(true, [], $"limit {limit.Max} of node {node.Id:x8}: this voice's post {post} is older than the oldest instance (post {instances[next].Post}): dropped", []);
                    (kill ??= []).AddRange(instances[next].Voices.Select(v => v.Handle));
                    (killed ??= []).Add((instances[next].Post, node.Id));
                    reason = $"limit {limit.Max} of node {node.Id:x8}{(limit.Global ? " (global)" : "")}: oldest instance killed";
                    next++;
                    count--;
                }
            }
            if (node.Limit is { IgnoreParent: true }) break;
        }
        return kill is null ? LimitDecision.None : new LimitDecision(false, kill, reason, killed!);
    }
}
