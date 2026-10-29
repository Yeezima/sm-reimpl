using System.Text;

namespace SmFormats.Audio;

/// <summary>
/// Builds synthetic AKPK packages, v56 SoundBanks and Wwise Vorbis (.wem) files following our documented reading of
/// the layouts (docs/discovery.md, "Audio (Wwise)"). For tests only: no original bytes. Passing tests against these
/// prove internal consistency; compatibility with the game's files is checked separately with <c>smtool audio-verify</c>.
/// </summary>
public static class SyntheticWwiseWriter
{
    public sealed record Item(uint Id, byte[] Data, uint LanguageId = 0);

    /// <summary>An AKPK package; <paramref name="wideEntries"/> picks the 24-byte LUT entries (64-bit sizes) of this game.</summary>
    public static byte[] Package(IReadOnlyList<Item> banks, IReadOnlyList<Item> streams, bool wideEntries = true,
        IReadOnlyList<(uint Id, string Name)>? languages = null, uint blockSize = 16)
    {
        languages ??= [(0, "sfx"), (1, "english(us)")];
        var lang = new MemoryStream();
        var lw = new BinaryWriter(lang);
        lw.Write((uint)languages.Count);
        int stringOffset = 4 + 8 * languages.Count;
        var strings = new MemoryStream();
        foreach (var (id, name) in languages)
        {
            lw.Write((uint)(stringOffset + strings.Length));
            lw.Write(id);
            strings.Write(Encoding.Unicode.GetBytes(name + "\0"));
        }
        lw.Write(strings.ToArray());
        while (lang.Length % 4 != 0) lw.Write((byte)0);

        int entrySize = wideEntries ? 24 : 20;
        int bankLutSize = 4 + entrySize * banks.Count, streamLutSize = 4 + entrySize * streams.Count;
        int headerSize = 16 + (int)lang.Length + bankLutSize + streamLutSize;     // bytes after offset 8
        long next = Align(8 + headerSize, blockSize);
        var placed = new List<(Item Item, long Offset)>();
        foreach (var it in banks.Concat(streams))
        {
            placed.Add((it, next));
            next = Align(next + it.Data.Length, blockSize);
        }

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("AKPK"u8);
        w.Write((uint)headerSize);
        w.Write(1u);
        w.Write((uint)lang.Length);
        w.Write((uint)bankLutSize);
        w.Write((uint)streamLutSize);
        w.Write(lang.ToArray());
        void Lut(IEnumerable<(Item Item, long Offset)> items, int count)
        {
            w.Write((uint)count);
            foreach (var (it, off) in items)
            {
                w.Write(it.Id);
                w.Write(blockSize);
                if (wideEntries) w.Write((ulong)it.Data.Length); else w.Write((uint)it.Data.Length);
                w.Write((uint)(off / blockSize));
                w.Write(it.LanguageId);
            }
        }
        Lut(placed.Take(banks.Count), banks.Count);
        Lut(placed.Skip(banks.Count), streams.Count);
        foreach (var (it, off) in placed)
        {
            while (ms.Length < off) w.Write((byte)0);
            w.Write(it.Data);
        }
        return ms.ToArray();
    }

    static long Align(long v, uint block) => (v + block - 1) / block * block;

    /// <summary>A HIRC object: kind, ID and payload (everything after the ID).</summary>
    public sealed record Obj(HircType Type, uint Id, byte[] Payload);

    /// <summary>A v56 bank: BKHD, DIDX/DATA when there is media, HIRC, STID when named.</summary>
    public static byte[] Bank(uint bankId, string? name, IReadOnlyList<Item> media, IReadOnlyList<Obj> objects,
        bool feedback = false, uint version = SoundBank.SupportedVersion)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        Chunk(w, "BKHD", c => { c.Write(version); c.Write(bankId); c.Write(0u); c.Write(feedback ? 1u : 0u); c.Write(0u); });
        if (media.Count > 0)
        {
            var data = new MemoryStream();
            var index = new List<(uint, int, int)>();
            foreach (var m in media)
            {
                while (data.Length % 16 != 0) data.WriteByte(0);
                index.Add((m.Id, (int)data.Length, m.Data.Length));
                data.Write(m.Data);
            }
            Chunk(w, "DIDX", c => { foreach (var (id, off, size) in index) { c.Write(id); c.Write(off); c.Write(size); } });
            Chunk(w, "DATA", c => c.Write(data.ToArray()));
        }
        Chunk(w, "HIRC", c =>
        {
            c.Write((uint)objects.Count);
            foreach (var o in objects)
            {
                c.Write((byte)o.Type);
                c.Write((uint)(o.Payload.Length + 4));
                c.Write(o.Id);
                c.Write(o.Payload);
            }
        });
        if (name is not null)
            Chunk(w, "STID", c => { c.Write(1u); c.Write(1u); c.Write(bankId); c.Write((byte)name.Length); c.Write(Encoding.ASCII.GetBytes(name)); });
        return ms.ToArray();
    }

    static void Chunk(BinaryWriter w, string tag, Action<BinaryWriter> body)
    {
        var ms = new MemoryStream();
        body(new BinaryWriter(ms));
        w.Write(Encoding.ASCII.GetBytes(tag));
        w.Write((uint)ms.Length);
        w.Write(ms.ToArray());
    }

    static byte[] Build(Action<BinaryWriter> body)
    {
        var ms = new MemoryStream();
        body(new BinaryWriter(ms));
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ HIRC payloads

    public static Obj Event(uint id, params uint[] actionIds) =>
        new(HircType.Event, id, Build(w => { w.Write((uint)actionIds.Length); foreach (var a in actionIds) w.Write(a); }));

    /// <summary>Play (type 0x4011) with the v56 parameter block, ending with the bank ID.</summary>
    public static Obj Play(uint id, uint target, int delay, uint bankId, int delayMin = 0, int delayMax = 0) =>
        new(HircType.Action, id, Build(w =>
        {
            w.Write(0x4011u); w.Write(target); w.Write(delay); w.Write(delayMin); w.Write(delayMax);
            w.Write(1u);                                                    // subsection size (non-zero: parameters follow)
            w.Write(0); w.Write(0); w.Write(0);                             // transition time and range
            w.Write((byte)4);                                               // fade curve
            w.Write(new byte[16]);                                          // reserved
            w.Write(0u);                                                    // exception list
            w.Write(bankId);
        }));

    public static Obj Stop(uint id, uint target) =>
        new(HircType.Action, id, Build(w =>
        {
            w.Write(0x1011u); w.Write(target); w.Write(0); w.Write(0); w.Write(0);
            w.Write(1u);
            w.Write(0); w.Write(0); w.Write(0); w.Write((byte)4); w.Write(new byte[16]); w.Write(0u);
        }));

    /// <summary>One RTPC binding of a synthetic node: game parameter, property (0 volume, 2 pitch, 3 LPF) and curve points.</summary>
    public sealed record RtpcSpec(uint Rtpc, uint Param, (float X, float Y, uint Shape)[] Points);

    /// <summary>Options for the v56 node base. <paramref name="RtpcCurves"/> (when set) replaces the generic <paramref name="Rtpcs"/> count.</summary>
    public sealed record Node(uint Parent, float Volume = 0, float VolumeMin = 0, float VolumeMax = 0,
        float Pitch = 0, float PitchMin = 0, float PitchMax = 0, uint AttenuationId = 0, uint[]? FxIds = null,
        int StateGroups = 0, int Rtpcs = 0, bool Feedback = false, uint Bus = 0,
        float Lpf = 0, float LpfMin = 0, float LpfMax = 0, IReadOnlyList<RtpcSpec>? RtpcCurves = null,
        ushort MaxInstances = 8, bool KillNewest = false, bool GlobalLimit = false, bool IgnoreParentMax = false, byte BelowThreshold = 0);

    static void NodeBase(BinaryWriter w, Node n)
    {
        var fx = n.FxIds ?? [];
        w.Write((byte)0);
        w.Write((byte)fx.Length);
        if (fx.Length > 0)
        {
            w.Write((byte)0);
            for (int i = 0; i < fx.Length; i++) { w.Write((byte)i); w.Write(fx[i]); w.Write((byte)1); w.Write((byte)0); }
        }
        w.Write(n.Bus);                                                     // output bus
        w.Write(n.Parent);
        w.Write((byte)50); w.Write((byte)0); w.Write((byte)0); w.Write(unchecked((byte)-10));
        foreach (float f in new[] { n.Volume, n.VolumeMin, n.VolumeMax, 0f, 0f, 0f, n.Pitch, n.PitchMin, n.PitchMax, n.Lpf, n.LpfMin, n.LpfMax }) w.Write(f);
        if (n.AttenuationId != 0)
        {
            w.Write((byte)1);                                               // positioning overrides parent
            w.Write(100); w.Write(0f); w.Write(0f);
            w.Write((byte)1);                                               // 3D
            w.Write(3u);                                                    // game-defined position
            w.Write(n.AttenuationId);
            w.Write((byte)1);                                               // spatialized
            w.Write((byte)0);                                               // dynamic
        }
        else w.Write((byte)0);
        w.Write((byte)(n.KillNewest ? 1 : 0)); w.Write((byte)0); w.Write((byte)(n.IgnoreParentMax ? 1 : 0)); w.Write(n.MaxInstances); w.Write((byte)(n.GlobalLimit ? 1 : 0)); w.Write(n.BelowThreshold); w.Write((byte)0); w.Write((byte)0);
        w.Write((uint)n.StateGroups);
        for (int g = 0; g < n.StateGroups; g++) { w.Write(0x1000u + (uint)g); w.Write((byte)0); w.Write((ushort)1); w.Write(1u); w.Write(2u); }
        if (n.RtpcCurves is { } curves)
        {
            w.Write((ushort)curves.Count);
            for (int r = 0; r < curves.Count; r++)
            {
                w.Write(curves[r].Rtpc); w.Write(curves[r].Param); w.Write(0x3000u + (uint)r); w.Write((byte)0); w.Write((ushort)curves[r].Points.Length);
                foreach (var (x, y, shape) in curves[r].Points) { w.Write(x); w.Write(y); w.Write(shape); }
            }
        }
        else
        {
            w.Write((ushort)n.Rtpcs);
            for (int r = 0; r < n.Rtpcs; r++)
            {
                w.Write(0x2000u + (uint)r); w.Write(0u); w.Write(0x3000u); w.Write((byte)0); w.Write((ushort)2);
                w.Write(0f); w.Write(0f); w.Write(4u); w.Write(1f); w.Write(-6f); w.Write(4u);
            }
        }
        if (n.Feedback) w.Write(0u);
    }

    /// <summary>A Vorbis sound (plugin 0x00040001); embedded media carries offset and size, streamed media does not.</summary>
    public static Obj Sound(uint id, uint sourceId, Node node, HircStreamType stream = HircStreamType.Bank, uint fileId = 0,
        uint mediaOffset = 0, uint mediaSize = 0, short loop = 1) =>
        new(HircType.Sound, id, Build(w =>
        {
            w.Write(0x00040001u); w.Write((uint)stream); w.Write(sourceId); w.Write(fileId);
            if (stream != HircStreamType.Streamed) { w.Write(mediaOffset); w.Write(mediaSize); }
            w.Write((byte)0);
            NodeBase(w, node);
            w.Write(loop); w.Write((short)0); w.Write((short)0);
        }));

    /// <summary>
    /// A random (or sequence) container. <paramref name="continuous"/> with <paramref name="transitionMode"/> (5 trigger rate,
    /// 2 cross-fade, 3 delay, 4 sample accurate) and <paramref name="transitionMs"/>; <paramref name="loop"/> 0 = endless;
    /// <paramref name="weights"/> (one per child) sets the weight flag; <paramref name="shuffle"/> random mode 1.
    /// </summary>
    public static Obj Random(uint id, Node node, uint[] children, ushort avoidRepeat = 1, bool sequence = false, bool continuous = false,
        byte transitionMode = 0, float transitionMs = 0, ushort loop = 1, byte[]? weights = null, bool shuffle = false, bool global = false) =>
        new(HircType.RandomSequence, id, Build(w =>
        {
            NodeBase(w, node);
            w.Write(loop);
            w.Write(transitionMs); w.Write(0f); w.Write(0f);
            w.Write(avoidRepeat);
            w.Write(transitionMode); w.Write((byte)(shuffle ? 1 : 0)); w.Write((byte)(sequence ? 1 : 0));
            w.Write((byte)(weights is null ? 0 : 1)); w.Write((byte)1); w.Write((byte)0); w.Write((byte)(continuous ? 1 : 0)); w.Write((byte)(global ? 1 : 0));
            w.Write((uint)children.Length); foreach (var c in children) w.Write(c);
            w.Write((ushort)children.Length);
            for (int i = 0; i < children.Length; i++) { w.Write(children[i]); w.Write(weights is null ? (byte)50 : weights[i]); }
        }));

    /// <summary>
    /// An attenuation object (v56 layout, see <see cref="WwiseAttenuation"/>): no cone, one curve (dry volume, dB scaling 2)
    /// with the given points; the other curve slots unused (-1); an empty RTPC tail.
    /// </summary>
    public static Obj Attenuation(uint id, params (float X, float Y, uint Interpolation)[] dryVolume) =>
        new(HircType.Attenuation, id, Build(w =>
        {
            w.Write((byte)0);                                               // no cone
            w.Write((sbyte)0); w.Write((sbyte)0); w.Write((sbyte)-1); w.Write((sbyte)-1); w.Write((sbyte)-1);
            w.Write((byte)1);                                               // one curve
            w.Write((byte)2); w.Write((ushort)dryVolume.Length);
            foreach (var (x, y, interp) in dryVolume) { w.Write(x); w.Write(y); w.Write(interp); }
            w.Write((ushort)0);                                             // RTPCs (not read)
        }));

    /// <summary>A music segment as the reader takes it: node base, the track IDs, then 24 bytes of tail that are not read (match flow).</summary>
    public static Obj MusicSegment(uint id, Node node, uint[] tracks) =>
        new(HircType.MusicSegment, id, Build(w =>
        {
            NodeBase(w, node);
            w.Write((uint)tracks.Length); foreach (var t in tracks) w.Write(t);
            w.Write(new byte[24]);
        }));

    /// <summary>A music track as the reader takes it: one Vorbis source, then a tail that is not read (match flow).</summary>
    public static Obj MusicTrack(uint id, uint sourceId, HircStreamType stream = HircStreamType.Streamed, uint fileId = 0) =>
        new(HircType.MusicTrack, id, Build(w =>
        {
            w.Write(1u);
            w.Write(0x00040001u); w.Write((uint)stream); w.Write(sourceId); w.Write(fileId);
            if (stream != HircStreamType.Streamed) { w.Write(0u); w.Write(4u); }
            w.Write((byte)0);
            w.Write(new byte[40]);
        }));

    /// <summary>An actor-mixer: node base and children.</summary>
    public static Obj ActorMixer(uint id, Node node, uint[] children) =>
        new(HircType.ActorMixer, id, Build(w =>
        {
            NodeBase(w, node);
            w.Write((uint)children.Length); foreach (var c in children) w.Write(c);
        }));

    /// <summary>A bus as the reader interprets it: u32 parent bus, f32 volume (voice), f32 bus volume, then 41 bytes not read.</summary>
    public static Obj Bus(uint id, uint parent, float volume, float busVolume = 0f) =>
        new(HircType.Bus, id, Build(w => { w.Write(parent); w.Write(volume); w.Write(busVolume); w.Write(new byte[41]); }));

    /// <summary>
    /// A property action (Set/Reset pitch 0x8010.., volume 0xA010.., LPF 0xE010.., mute 0x6010.., bus volume 0xC010..): the v56
    /// 33-byte parameter block read by <see cref="HircAction"/>: transition time (ms) at 0, fade curve at 12, value meaning at 13
    /// (1 independent, 2 offset), the f32 value at 17.
    /// </summary>
    public static Obj PropertyAction(uint id, uint actionType, uint target, float value, int transitionMs = 0, byte meaning = 1, int delay = 0) =>
        new(HircType.Action, id, Build(w =>
        {
            w.Write(actionType); w.Write(target); w.Write(delay); w.Write(0); w.Write(0);
            w.Write(1u);
            w.Write((uint)transitionMs); w.Write(0u); w.Write(0u);
            w.Write((byte)4); w.Write(meaning); w.Write(new byte[3]);
            w.Write(value);
            w.Write(new byte[12]);
        }));

    /// <summary>A SetSwitch action (type 0x60001): parameters u32 switch group, u32 switch value.</summary>
    public static Obj SetSwitch(uint id, uint group, uint value) =>
        new(HircType.Action, id, Build(w =>
        {
            w.Write(0x60001u); w.Write(0u); w.Write(0); w.Write(0); w.Write(0);
            w.Write(0u);
            w.Write(group); w.Write(value);
        }));

    /// <summary>
    /// A dialogue event (v56 layout, see <see cref="HircDialogueEvent"/>): <paramref name="arguments"/> are the argument group IDs,
    /// each leaf a path of one key per argument and the audio node it plays. The tree is written level by level, children contiguous.
    /// </summary>
    public static Obj Dialogue(uint id, uint[] arguments, params (uint[] Path, uint Audio)[] leaves) =>
        new(HircType.DialogueEvent, id, Build(w =>
        {
            var nodes = new List<(uint Key, uint Raw)> { (0, 0) };
            var level = new List<(int Index, IEnumerable<(uint[] Path, uint Audio)> Leaves)> { (0, leaves) };
            for (int depth = 0; depth < arguments.Length; depth++)
            {
                var next = new List<(int, IEnumerable<(uint[], uint)>)>();
                foreach (var (index, set) in level)
                {
                    var groups = set.GroupBy(l => l.Path[depth]).ToList();
                    int first = nodes.Count;
                    foreach (var g in groups)
                    {
                        bool leaf = depth == arguments.Length - 1;
                        nodes.Add((g.Key, leaf ? g.First().Audio : 0));
                        if (!leaf) next.Add((nodes.Count - 1, g));
                    }
                    nodes[index] = (nodes[index].Key, (uint)first | ((uint)groups.Count << 16));
                }
                level = next;
            }
            w.Write((uint)arguments.Length);
            foreach (var a in arguments) w.Write(a);
            w.Write((uint)(nodes.Count * 12));
            w.Write((ushort)100);
            foreach (var (key, raw) in nodes) { w.Write(key); w.Write(raw); w.Write((ushort)50); w.Write((ushort)100); }
        }));

    /// <summary>A SetState action (type 0x12020): parameters u32 state group, u32 state.</summary>
    public static Obj SetState(uint id, uint group, uint state) =>
        new(HircType.Action, id, Build(w =>
        {
            w.Write(0x12020u); w.Write(0u); w.Write(0); w.Write(0); w.Write(0);
            w.Write(0u);
            w.Write(group); w.Write(state);
        }));

    public static Obj Switch(uint id, Node node, uint group, uint defaultSwitch, IReadOnlyList<(uint Switch, uint[] Nodes)> cases, uint groupType = 0) =>
        new(HircType.Switch, id, Build(w =>
        {
            NodeBase(w, node);
            w.Write(groupType); w.Write(group); w.Write(defaultSwitch); w.Write((byte)0);
            var children = cases.SelectMany(c => c.Nodes).Distinct().ToArray();
            w.Write((uint)children.Length); foreach (var c in children) w.Write(c);
            w.Write((uint)cases.Count);
            foreach (var (sw, nodes) in cases) { w.Write(sw); w.Write((uint)nodes.Length); foreach (var n in nodes) w.Write(n); }
            w.Write((uint)children.Length);
            foreach (var c in children) { w.Write(c); w.Write((byte)0); w.Write((byte)0); w.Write(0u); w.Write(0); w.Write(0); }
        }));

    public static Obj Layer(uint id, Node node, uint[] children) =>
        new(HircType.Layer, id, Build(w =>
        {
            NodeBase(w, node);
            w.Write((uint)children.Length); foreach (var c in children) w.Write(c);
            w.Write(1u);                                                    // one layer
            w.Write(0x4000u);
            w.Write((ushort)0);                                             // no RTPCs
            w.Write(0u); w.Write(0f);
            w.Write((uint)children.Length);
            foreach (var c in children) { w.Write(c); w.Write(0u); }
        }));

    // ------------------------------------------------------------------ Wwise Vorbis

    /// <summary>
    /// A minimal Wwise Vorbis file (fmt 0x42 with the vorb fields inside, 2-byte packet headers, modified packets):
    /// one 2-entry codebook (inline or as external ID 0), one floor-1, an empty residue, two modes (short, long).
    /// Each audio packet is one byte: the mode bit, then "floor unused" for every channel, so it decodes to silence.
    /// <paramref name="longBlocks"/> gives each packet's mode.
    /// </summary>
    public static byte[] Wem(int channels, int sampleRate, IReadOnlyList<bool> longBlocks, uint sampleCount, bool inlineCodebook,
        int blocksize0Exponent = 8, int blocksize1Exponent = 11, int emptyTailPackets = 0)
    {
        var setup = new BitWriter();
        setup.Write(0, 8);                                                  // one codebook
        if (inlineCodebook) WriteStrippedCodebook(setup);
        else setup.Write(0, 10);                                            // external codebook 0
        setup.Write(0, 6);                                                  // one floor
        setup.Write(1, 5);                                                  // one partition
        setup.Write(0, 4);                                                  //   of class 0
        setup.Write(0, 3);                                                  // class 0: dimensions 1
        setup.Write(0, 2);                                                  //   no subclasses
        setup.Write(0, 8);                                                  //   subclass book -1
        setup.Write(1, 2);                                                  // multiplier 2
        setup.Write(8, 4);                                                  // range bits 8
        setup.Write(100, 8);                                                // X = 100
        setup.Write(0, 6);                                                  // one residue
        setup.Write(2, 2);                                                  //   type 2
        setup.Write(0, 24); setup.Write(0, 24); setup.Write(15, 24);        //   begin 0, end 0, partition 16
        setup.Write(0, 6);                                                  //   one classification
        setup.Write(0, 8);                                                  //   classbook 0
        setup.Write(0, 3); setup.Write(0, 1);                               //   cascade 0
        setup.Write(0, 6);                                                  // one mapping
        setup.Write(0, 1);                                                  //   one submap
        setup.Write(0, 1);                                                  //   no coupling
        setup.Write(0, 2);                                                  //   reserved
        setup.Write(0, 8); setup.Write(0, 8); setup.Write(0, 8);            //   time 0, floor 0, residue 0
        setup.Write(1, 6);                                                  // two modes
        setup.Write(0, 1); setup.Write(0, 8);                               //   short, mapping 0
        setup.Write(1, 1); setup.Write(0, 8);                               //   long, mapping 0
        byte[] setupBytes = setup.ToArray();

        var data = new MemoryStream();
        var dw = new BinaryWriter(data);
        dw.Write((ushort)setupBytes.Length);
        dw.Write(setupBytes);
        uint firstAudio = (uint)data.Length;
        foreach (bool isLong in longBlocks)
        {
            dw.Write((ushort)1);
            dw.Write((byte)(isLong ? 1 : 0));                               // mode bit; the floor-unused bits are 0
        }
        for (int i = 0; i < emptyTailPackets; i++) dw.Write((ushort)0);    // zero-length packets after the audio

        var fmt = new MemoryStream();
        var fw = new BinaryWriter(fmt);
        fw.Write((ushort)0xFFFF); fw.Write((ushort)channels); fw.Write(sampleRate); fw.Write(sampleRate / 8);
        fw.Write((ushort)0); fw.Write((ushort)0); fw.Write((ushort)0x30);
        fw.Write((ushort)0); fw.Write(channels == 1 ? 4u : 3u);             // extra: unknown, channel layout
        // vorb fields at fmt + 0x18
        fw.Write(sampleCount);
        fw.Write(0xD9u);                                                    // "modified packets" signal
        fw.Write(0u); fw.Write(0u);
        fw.Write(0u);                                                       // setup packet offset
        fw.Write(firstAudio);
        fw.Write(0u); fw.Write(0u); fw.Write(0u);
        fw.Write(0x12345678u);                                              // uid
        fw.Write((byte)blocksize0Exponent); fw.Write((byte)blocksize1Exponent);
        while (fmt.Length < 0x42) fw.Write((byte)0);

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write((uint)(4 + 8 + fmt.Length + 8 + data.Length));
        w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write((uint)fmt.Length); w.Write(fmt.ToArray());
        w.Write("data"u8); w.Write((uint)data.Length); w.Write(data.ToArray());
        return ms.ToArray();
    }

    /// <summary>The stripped (Wwise) form of a 1-dimension, 2-entry codebook with codeword lengths 1, 1, no lookup.</summary>
    public static void WriteStrippedCodebook(BitWriter w)
    {
        w.Write(1, 4);                                                      // dimensions
        w.Write(2, 14);                                                     // entries
        w.Write(0, 1);                                                      // not ordered
        w.Write(1, 3);                                                      // 1-bit codeword lengths
        w.Write(0, 1);                                                      // not sparse
        w.Write(0, 1); w.Write(0, 1);                                       // lengths - 1
        w.Write(0, 1);                                                      // no lookup
    }

    /// <summary>A codebook library in ww2ogg's packed format holding <see cref="WriteStrippedCodebook"/> as ID 0.</summary>
    public static byte[] CodebookLibrary()
    {
        var cb = new BitWriter();
        WriteStrippedCodebook(cb);
        byte[] book = cb.ToArray();
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(book);
        w.Write(0u);                                                        // offset of codebook 0
        w.Write((uint)book.Length);                                         // end of codebook 0 = start of the table
        return ms.ToArray();
    }
}
