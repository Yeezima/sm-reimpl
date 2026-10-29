namespace SmFormats.Audio;

/// <summary>HIRC object kinds in bank version 56 (docs/discovery.md, "Audio (Wwise)").</summary>
public enum HircType : byte
{
    State = 1, Sound = 2, Action = 3, Event = 4, RandomSequence = 5, Switch = 6, ActorMixer = 7, Bus = 8, Layer = 9,
    MusicSegment = 10, MusicTrack = 11, MusicSwitch = 12, MusicRandomSequence = 13, Attenuation = 14,
    DialogueEvent = 15, FeedbackBus = 16, FeedbackNode = 17, FxShareSet = 18, FxCustom = 19,
}

/// <summary>One HIRC object: its kind, ID and the payload after the ID (kept for every object, parsed or not).</summary>
public abstract class HircObject
{
    public HircType Type { get; }
    public uint Id { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    protected HircObject(HircType type, uint id, ReadOnlyMemory<byte> payload)
    {
        Type = type; Id = id; Payload = payload;
    }

    public override string ToString() => $"{Type} {Id:x8}";
}

/// <summary>An object whose kind is not decoded yet, or whose payload did not parse exactly (see <see cref="ParseError"/>).</summary>
public sealed class HircRaw(HircType type, uint id, ReadOnlyMemory<byte> payload, string? parseError = null)
    : HircObject(type, id, payload)
{
    /// <summary>Null for kinds that are kept raw on purpose; otherwise why the typed parse was refused.</summary>
    public string? ParseError { get; } = parseError;
}

/// <summary>Event: an ordered list of action IDs. The event ID is FNV-1 of its lower-cased name.</summary>
public sealed class HircEvent(uint id, ReadOnlyMemory<byte> payload, IReadOnlyList<uint> actionIds)
    : HircObject(HircType.Event, id, payload)
{
    public IReadOnlyList<uint> ActionIds { get; } = actionIds;
}

/// <summary>What an action does, from the high bits of the v56 action type (the low byte is its scope).</summary>
public enum HircActionKind
{
    Other, Stop, Pause, Resume, Play, SetPitch, ResetPitch, SetVolume, ResetVolume, SetState,
    // Added with M33 (the codes follow Wwise's action numbering, interpretation; seen on the install):
    Mute, Unmute, SetBusVolume, ResetBusVolume, SetLpf, ResetLpf, SetSwitch, SetGameParameter,
}

/// <summary>
/// Action (v56): u32 actionType, u32 targetId, s32 delay, s32 delayMin, s32 delayMax (milliseconds, interpretation),
/// u32 subsectionSize, then kind-specific parameters (kept raw). Play's parameters end with the ID of the bank
/// that holds the event.
/// </summary>
public sealed class HircAction(uint id, ReadOnlyMemory<byte> payload, uint actionType, uint targetId,
    int delay, int delayMin, int delayMax, ReadOnlyMemory<byte> parameters)
    : HircObject(HircType.Action, id, payload)
{
    public uint ActionType { get; } = actionType;
    public uint TargetId { get; } = targetId;
    public int Delay { get; } = delay;
    public int DelayMin { get; } = delayMin;
    public int DelayMax { get; } = delayMax;
    public ReadOnlyMemory<byte> Parameters { get; } = parameters;

    public HircActionKind Kind => KindOf(ActionType);

    /// <summary>Play only: the bank named at the end of the parameters, or null.</summary>
    public uint? PlayBankId => Kind == HircActionKind.Play && Parameters.Length >= 4
        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Parameters.Span[^4..]) : null;

    /// <summary>Kinds seen in this game's banks (types 0x4011 Play, 0x1010/0x1011 Stop, ...).</summary>
    public static HircActionKind KindOf(uint actionType) => (actionType >> 12) switch
    {
        0x1 => HircActionKind.Stop,
        0x2 => HircActionKind.Pause,
        0x3 => HircActionKind.Resume,
        0x4 => HircActionKind.Play,
        0x8 => HircActionKind.SetPitch,
        0x9 => HircActionKind.ResetPitch,
        0xA => HircActionKind.SetVolume,
        0xB => HircActionKind.ResetVolume,
        0x6 => HircActionKind.Mute,
        0x7 => HircActionKind.Unmute,
        0xC => HircActionKind.SetBusVolume,
        0xD => HircActionKind.ResetBusVolume,
        0xE => HircActionKind.SetLpf,
        0xF => HircActionKind.ResetLpf,
        0x12 => HircActionKind.SetState,
        0x13 => HircActionKind.SetGameParameter,
        0x60 => HircActionKind.SetSwitch,
        _ => HircActionKind.Other,
    };

    /// <summary>
    /// Property actions (set / reset pitch, volume, LPF; bus volume; mute): the transition time in milliseconds, the first
    /// <c>u32</c> of the parameters (seen: Set Pitch +500 cents over 2000 for a plasma charge, Reset LPF over 4000 after a stun;
    /// interpretation). 0 when the parameters are shorter.
    /// </summary>
    public int TransitionMilliseconds => Parameters.Length >= 4
        ? (int)Math.Min(int.MaxValue, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Parameters.Span)) : 0;

    /// <summary>Property actions: the fade curve shape byte (parameters offset 12; 1..8 seen; shapes are not reproduced).</summary>
    public byte FadeCurve => Parameters.Length > 12 ? Parameters.Span[12] : (byte)0;

    /// <summary>
    /// Property actions: how the value is meant, parameters offset 13: 1 = independent (replaces the target's own value), 2 =
    /// offset (added to it) (Wwise's <c>AkValueMeaning</c> numbering, interpretation); 0 when absent.
    /// </summary>
    public byte ValueMeaning => Parameters.Length > 13 ? Parameters.Span[13] : (byte)0;

    /// <summary>Property actions: the value at parameters offset 17 (<c>f32</c>: cents, dB or LPF 0..100); 0 when absent.</summary>
    public float Value => Parameters.Length >= 21 ? System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(Parameters.Span[17..]) : 0f;

    /// <summary>SetSwitch (type 0x60001): the switch group and value (<c>u32 group, u32 value</c>), or null.</summary>
    public (uint Group, uint Value)? SwitchSetting => Kind == HircActionKind.SetSwitch && Parameters.Length >= 8
        ? (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Parameters.Span), System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Parameters.Span[4..])) : null;
}

/// <summary>A base value with a random range added on each play: final = Base + uniform(Min, Max).</summary>
public readonly record struct RangedValue(float Base, float Min, float Max)
{
    public bool HasRange => Min != 0 || Max != 0;
    public override string ToString() => HasRange ? $"{Base:0.##} [{Min:0.##}..{Max:0.##}]" : $"{Base:0.##}";
}

/// <summary>
/// Common parameters of sounds and containers (v56 node base). Volume, LFE and LPF are in the authoring tool's
/// units (dB for volume, interpretation), pitch in cents.
/// </summary>
public sealed record HircNodeBase(
    bool OverrideParentFx,
    IReadOnlyList<uint> FxIds,
    uint OutputBusId,
    uint ParentId,
    byte Priority,
    bool PriorityOverrideParent,
    bool PriorityApplyDistanceFactor,
    sbyte PriorityDistanceOffset,
    RangedValue Volume,
    RangedValue Lfe,
    RangedValue Pitch,
    RangedValue Lpf,
    bool PositioningOverrideParent,
    bool Is3D,
    uint PositionType,
    uint AttenuationId,
    ushort MaxInstances,
    int StateGroupCount,
    int RtpcCount)
{
    /// <summary>The node's RTPC bindings (game parameter → property curves), in file order; empty when it has none.</summary>
    public IReadOnlyList<HircRtpc> Rtpcs { get; init; } = [];

    /// <summary>
    /// The advanced-settings flags next to <see cref="MaxInstances"/> (v56; the order of the bytes is an interpretation of Wwise 2010's bank layout):
    /// kill the newest instead of the oldest when the limit is reached, the virtual-voice behaviour byte, ignore the parent's limit, a global limit
    /// (all emitters) instead of per game object, and the below-threshold behaviour byte (0 continue, 1 kill, 2 virtual voice in Wwise).
    /// </summary>
    public bool KillNewest { get; init; }
    public byte VirtualBehavior { get; init; }
    public bool IgnoreParentMaxInstances { get; init; }
    public bool GlobalLimit { get; init; }
    public byte BelowThreshold { get; init; }

    /// <summary>The two bytes that close the advanced settings (Wwise 2010: HDR-envelope and analysis overrides, interpretation): kept for the statistics of <c>smtool audio-rtpcs --limits</c>.</summary>
    public byte TrailingA { get; init; }
    public byte TrailingB { get; init; }
}

/// <summary>
/// One RTPC binding of a node (v56): game parameter <see cref="RtpcId"/> drives property <see cref="ParamId"/> through the curve
/// <see cref="Points"/> (x = parameter value, y = property value; <see cref="Scaling"/> as in the attenuation curves). Property IDs
/// seen on the install: see docs/discovery.md ("RTPCs of the multiplayer banks"); the numbering is an interpretation.
/// </summary>
public sealed record HircRtpc(uint RtpcId, uint ParamId, uint CurveId, byte Scaling, IReadOnlyList<CurvePoint> Points)
{
    /// <summary>The curve's value at a parameter value: straight lines between points, constant outside (shapes not reproduced).</summary>
    public float Evaluate(float x) => new AttenuationCurve(Scaling, Points).Evaluate(x);
}

/// <summary>A sound or container: node base plus child IDs (empty for sounds).</summary>
public abstract class HircNode(HircType type, uint id, ReadOnlyMemory<byte> payload, HircNodeBase nodeBase, IReadOnlyList<uint> children)
    : HircObject(type, id, payload)
{
    public HircNodeBase Base { get; } = nodeBase;
    public IReadOnlyList<uint> Children { get; } = children;
}

/// <summary>
/// Music segment (v56; match flow): node base, then the child IDs (its music tracks); the rest (tempo, markers, the segment's length
/// as an <c>f64</c> in milliseconds) is not read. Kept raw when the node base does not parse (the install's other music objects are
/// not played).
/// </summary>
public sealed class HircMusicSegment(uint id, ReadOnlyMemory<byte> payload, HircNodeBase nodeBase, IReadOnlyList<uint> children)
    : HircNode(HircType.MusicSegment, id, payload, nodeBase, children);

/// <summary>One audio source of a music track: as a <see cref="HircSound"/>'s, without the node base.</summary>
public readonly record struct HircMusicSource(uint PluginId, HircStreamType StreamType, uint SourceId, uint FileId)
{
    public bool IsVorbis => PluginId == 0x00040001;
}

/// <summary>
/// Music track (v56; match flow): <c>u32</c> source count, then each source (<c>u32 plugin, u32 stream type, u32 source ID, u32 file ID,
/// [u32 offset, u32 size unless streamed], u8 bits</c>); the playlist, sub-tracks and node base after it are not read (the volume and
/// bus come from the segment above).
/// </summary>
public sealed class HircMusicTrack(uint id, ReadOnlyMemory<byte> payload, IReadOnlyList<HircMusicSource> sources)
    : HircObject(HircType.MusicTrack, id, payload)
{
    public IReadOnlyList<HircMusicSource> Sources { get; } = sources;
}

/// <summary>Where a sound's media lives (v56 source stream type).</summary>
public enum HircStreamType : uint { Bank = 0, Streamed = 1, PrefetchStreamed = 2 }

/// <summary>
/// Sound (v56): u32 pluginId, u32 streamType, u32 sourceId (the .wem ID), u32 fileId (bank or file holding it),
/// [u32 offset, u32 size unless streamed], u8 sourceBits, [u32 size + plug-in params for source plug-ins],
/// node base, s16 loop count, s16 loop min, s16 loop max.
/// </summary>
public sealed class HircSound(uint id, ReadOnlyMemory<byte> payload, HircNodeBase nodeBase, uint pluginId,
    HircStreamType streamType, uint sourceId, uint fileId, uint mediaOffset, uint mediaSize, byte sourceBits, short loopCount)
    : HircNode(HircType.Sound, id, payload, nodeBase, [])
{
    /// <summary>Wwise Vorbis is 0x00040001 in this game.</summary>
    public uint PluginId { get; } = pluginId;
    public HircStreamType StreamType { get; } = streamType;
    public uint SourceId { get; } = sourceId;
    public uint FileId { get; } = fileId;
    public uint MediaOffset { get; } = mediaOffset;
    public uint MediaSize { get; } = mediaSize;
    public byte SourceBits { get; } = sourceBits;
    /// <summary>0 = infinite, 1 = play once (Wwise convention, interpretation).</summary>
    public short LoopCount { get; } = loopCount;
    public bool IsVorbis => PluginId == 0x00040001;
}

/// <summary>Random/sequence container (v56).</summary>
public sealed class HircRandomSequence(uint id, ReadOnlyMemory<byte> payload, HircNodeBase nodeBase, IReadOnlyList<uint> children,
    ushort loopCount, RangedValue transitionTime, ushort avoidRepeatCount, byte transitionMode, byte randomMode, byte mode,
    bool isUsingWeight, bool resetPlaylistAtEachPlay, bool isRestartBackward, bool isContinuous, bool isGlobal,
    IReadOnlyList<(uint Id, byte Weight)> playlist)
    : HircNode(HircType.RandomSequence, id, payload, nodeBase, children)
{
    public ushort LoopCount { get; } = loopCount;
    public RangedValue TransitionTime { get; } = transitionTime;
    public ushort AvoidRepeatCount { get; } = avoidRepeatCount;
    public byte TransitionMode { get; } = transitionMode;
    /// <summary>0 normal, 1 shuffle (Wwise convention, interpretation).</summary>
    public byte RandomMode { get; } = randomMode;
    /// <summary>0 random, 1 sequence.</summary>
    public byte Mode { get; } = mode;
    public bool IsUsingWeight { get; } = isUsingWeight;
    public bool ResetPlaylistAtEachPlay { get; } = resetPlaylistAtEachPlay;
    public bool IsRestartBackward { get; } = isRestartBackward;
    public bool IsContinuous { get; } = isContinuous;
    public bool IsGlobal { get; } = isGlobal;
    public IReadOnlyList<(uint Id, byte Weight)> Playlist { get; } = playlist;
    public bool IsRandom => Mode == 0;
}

/// <summary>One switch value and the child nodes it plays.</summary>
public sealed record HircSwitchCase(uint SwitchId, IReadOnlyList<uint> NodeIds);

/// <summary>Switch container (v56): the switch (or state) group, its default value and the value → nodes table.</summary>
public sealed class HircSwitch(uint id, ReadOnlyMemory<byte> payload, HircNodeBase nodeBase, IReadOnlyList<uint> children,
    uint groupType, uint groupId, uint defaultSwitch, bool continuousValidation, IReadOnlyList<HircSwitchCase> cases)
    : HircNode(HircType.Switch, id, payload, nodeBase, children)
{
    /// <summary>0 switch group, 1 state group.</summary>
    public uint GroupType { get; } = groupType;
    public uint GroupId { get; } = groupId;
    public uint DefaultSwitch { get; } = defaultSwitch;
    public bool ContinuousValidation { get; } = continuousValidation;
    public IReadOnlyList<HircSwitchCase> Cases { get; } = cases;

    /// <summary>The nodes for a switch value, falling back to the default value's case.</summary>
    public IReadOnlyList<uint> NodesFor(uint? switchValue)
    {
        if (switchValue is uint v && Cases.FirstOrDefault(c => c.SwitchId == v) is { } hit) return hit.NodeIds;
        return Cases.FirstOrDefault(c => c.SwitchId == DefaultSwitch)?.NodeIds ?? [];
    }
}

/// <summary>One layer of a layer container: the children it plays (with their crossfade curves, not decoded).</summary>
public sealed record HircLayerInfo(uint LayerId, uint RtpcId, float CrossfadeDefault, IReadOnlyList<uint> AssociatedChildIds)
{
    /// <summary>
    /// The layer's own RTPC bindings (property curves of the layer; usually none) read ahead of its crossfade parameter.
    /// </summary>
    public IReadOnlyList<HircRtpc> Rtpcs { get; init; } = [];

    /// <summary>
    /// One crossfade curve per associated child, parallel to <see cref="AssociatedChildIds"/>: x = the layer's game parameter
    /// <see cref="RtpcId"/> (0 = none: the layer plays all its children at full level), y = the child's level, the points' shape in <see cref="CurvePoint.Interpolation"/>.
    /// Layout {f32 from, f32 to, u32 interpolation}: read as (x, y, shape) like the other curves. The curve's y is a gain in dB (the values seen are 0 .. -96.3: interpretation).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<CurvePoint>> ChildCurves { get; init; } = [];
}

/// <summary>Layer container (v56): plays all children together.</summary>
public sealed class HircLayer(uint id, ReadOnlyMemory<byte> payload, HircNodeBase nodeBase, IReadOnlyList<uint> children,
    IReadOnlyList<HircLayerInfo> layers)
    : HircNode(HircType.Layer, id, payload, nodeBase, children)
{
    public IReadOnlyList<HircLayerInfo> Layers { get; } = layers;
}

/// <summary>Actor-mixer: a grouping node (node base and children only).</summary>
public sealed class HircActorMixer(uint id, ReadOnlyMemory<byte> payload, HircNodeBase nodeBase, IReadOnlyList<uint> children)
    : HircNode(HircType.ActorMixer, id, payload, nodeBase, children);


/// <summary>One node of a dialogue event's decision tree: a key (the argument value, 0 = any), its children or audio node, weight and probability.</summary>
public readonly record struct DialogueNode(uint Key, uint Raw, ushort Weight, ushort Probability)
{
    /// <summary>Inner nodes: the first child's index in the tree's node list (the low half of <see cref="Raw"/>).</summary>
    public int ChildIndex => (int)(Raw & 0xFFFF);
    /// <summary>Inner nodes: the number of children (the high half of <see cref="Raw"/>).</summary>
    public int ChildCount => (int)(Raw >> 16);
}

/// <summary>
/// Dialogue event (HIRC type 15, v56): <c>u32 depth, depth × u32 argument group IDs, u32 treeSize, u16 (100 seen; kept),
/// treeSize bytes</c> of 12-byte nodes <c>{u32 key, u32 (u16 firstChild, u16 childCount) or audio node ID, u16 weight, u16 probability}</c>,
/// the root first (key 0), level k holding the nodes for argument k; the last level's second field is the audio node to play
/// (0: none). The layout matches every one of the 6 events of the install's speech bank to the last byte; the reading of the
/// fields is an interpretation of Wwise's dynamic dialogue.
/// </summary>
public sealed class HircDialogueEvent(uint id, ReadOnlyMemory<byte> payload, IReadOnlyList<uint> argumentIds, IReadOnlyList<DialogueNode> nodes)
    : HircObject(HircType.DialogueEvent, id, payload)
{
    public IReadOnlyList<uint> ArgumentIds { get; } = argumentIds;
    public IReadOnlyList<DialogueNode> Nodes { get; } = nodes;

    /// <summary>The distinct keys of one level (1 = the first argument), for finding the event that handles a value.</summary>
    public IEnumerable<uint> KeysAt(int level)
    {
        if (Nodes.Count == 0) return [];
        var frontier = new List<int> { 0 };
        for (int l = 1; l <= level && frontier.Count > 0; l++)
        {
            var next = new List<int>();
            foreach (int i in frontier)
            {
                var n = Nodes[i];
                for (int k = 0; k < n.ChildCount && n.ChildIndex + k < Nodes.Count; k++) next.Add(n.ChildIndex + k);
            }
            frontier = next;
        }
        return frontier.Select(i => Nodes[i].Key).Distinct();
    }

    /// <summary>
    /// The audio node for argument values (one per argument, in order): at each level the child whose key is the value, else the
    /// child with key 0 (the "any" case); null when a level has neither, the path is shorter than the tree is deep, or the leaf
    /// names no node (0).
    /// </summary>
    public uint? Resolve(IReadOnlyList<uint> values)
    {
        if (values.Count < ArgumentIds.Count || Nodes.Count == 0) return null;
        int current = 0;
        for (int level = 1; level <= ArgumentIds.Count; level++)
        {
            var n = Nodes[current];
            int found = -1, any = -1;
            for (int k = 0; k < n.ChildCount; k++)
            {
                int index = n.ChildIndex + k;
                if (index <= current || index >= Nodes.Count) return null;     // children always follow their parent
                uint key = Nodes[index].Key;
                if (key == values[level - 1]) { found = index; break; }
                if (key == 0 && any < 0) any = index;
            }
            if (found < 0) found = any;
            if (found < 0) return null;
            current = found;
        }
        uint audio = Nodes[current].Raw;
        return audio == 0 ? null : audio;
    }
}
