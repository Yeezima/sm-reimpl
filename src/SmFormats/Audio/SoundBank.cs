using System.Buffers.Binary;
using System.Text;

namespace SmFormats.Audio;

/// <summary>An embedded media file: its ID and where it sits inside the bank's DATA chunk.</summary>
public readonly record struct BankMedia(uint Id, int Offset, int Size);

/// <summary>
/// Read-only reader for Wwise SoundBanks of bank version 56 (Wwise 2011.1), the only version in this game.
/// Chunks are {tag[4], u32 size, payload}:
/// <list type="bullet">
/// <item>BKHD {u32 version, u32 bankId, u32 languageId, u32 feedbackInBank, ...}</item>
/// <item>DIDX: 12-byte entries {u32 wemId, u32 offset (in DATA), u32 size}; DATA holds the media</item>
/// <item>HIRC {u32 count, objects {u8 type, u32 size, u32 id, payload}} (size counts the ID)</item>
/// <item>STID {u32 type, u32 count, {u32 bankId, u8 length, name}}: the bank's own name</item>
/// </list>
/// Sounds, events, actions and the random, switch, layer and actor-mixer containers are decoded
/// (layouts in docs/discovery.md, "Audio (Wwise)"); every other kind, and any object that does not parse to exactly
/// its size, is kept as <see cref="HircRaw"/>.
/// </summary>
public sealed class SoundBank
{
    public const uint SupportedVersion = 56;

    readonly ReadOnlyMemory<byte> _data;
    readonly Dictionary<uint, BankMedia> _media = new();
    readonly Dictionary<uint, HircObject> _objects = new();
    readonly int _dataChunkOffset = -1, _dataChunkSize;

    public uint Version { get; }
    public uint BankId { get; }
    public uint LanguageId { get; }
    public bool FeedbackInBank { get; }
    /// <summary>The bank's name from STID, or null.</summary>
    public string? Name { get; }
    public IReadOnlyList<(string Tag, int Offset, int Size)> Chunks { get; }
    public IReadOnlyList<BankMedia> Media { get; }
    public IReadOnlyList<HircObject> Objects { get; }
    /// <summary>Objects of a decoded kind that were kept raw because they did not parse exactly.</summary>
    public int ParseFailures { get; }

    /// <summary>False for a bank read without its DATA chunk (<see cref="ReadWithoutMedia"/>).</summary>
    public bool MediaInMemory { get; }

    SoundBank(ReadOnlyMemory<byte> data, (int Offset, int Size)? externalData = null)
    {
        _data = data;
        MediaInMemory = externalData is null;
        var d = data.Span;
        var chunks = new List<(string, int, int)>();
        int pos = 0;
        while (pos + 8 <= d.Length)
        {
            string tag = Encoding.ASCII.GetString(d.Slice(pos, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 4)..]);
            if (size > d.Length - pos - 8) throw new InvalidDataException($"Bank chunk {tag} overruns the bank.");
            chunks.Add((tag, pos + 8, (int)size));
            pos += 8 + (int)size;
        }
        Chunks = chunks;
        if (chunks.Count == 0 || chunks[0].Item1 != "BKHD") throw new InvalidDataException("Missing BKHD chunk.");

        var bkhd = d.Slice(chunks[0].Item2, chunks[0].Item3);
        if (bkhd.Length < 12) throw new InvalidDataException("BKHD too short.");
        Version = U32(bkhd, 0);
        if (Version != SupportedVersion) throw new NotSupportedException($"Bank version {Version} is not supported (only {SupportedVersion}).");
        BankId = U32(bkhd, 4);
        LanguageId = U32(bkhd, 8);
        FeedbackInBank = bkhd.Length >= 16 && (U32(bkhd, 12) & 1) != 0;

        var media = new List<BankMedia>();
        var objects = new List<HircObject>();
        int failures = 0;
        foreach (var (tag, off, size) in chunks)
        {
            var c = d.Slice(off, size);
            switch (tag)
            {
                case "DATA":
                    _dataChunkOffset = off; _dataChunkSize = size;
                    break;
                case "DIDX":
                    for (int i = 0; i + 12 <= c.Length; i += 12)
                        media.Add(new BankMedia(U32(c, i), (int)U32(c, i + 4), (int)U32(c, i + 8)));
                    break;
                case "STID":
                    Name ??= ReadBankName(c, BankId);
                    break;
                case "HIRC":
                    ReadHirc(data.Slice(off, size), objects, ref failures);
                    break;
            }
        }
        if (externalData is { } ext) (_dataChunkOffset, _dataChunkSize) = ext;
        foreach (var m in media)
        {
            if (_dataChunkOffset < 0 || m.Offset < 0 || m.Size < 0 || (long)m.Offset + m.Size > _dataChunkSize)
                throw new InvalidDataException($"Media {m.Id} lies outside the DATA chunk.");
            _media.TryAdd(m.Id, m);
        }
        foreach (var o in objects) _objects.TryAdd(o.Id, o);
        Media = media;
        Objects = objects;
        ParseFailures = failures;
    }

    public static bool HasMagic(ReadOnlySpan<byte> head) => head.StartsWith("BKHD"u8);

    public static SoundBank Read(ReadOnlyMemory<byte> data) => new(data);

    /// <summary>
    /// Reads the bank at <paramref name="offset"/> in <paramref name="stream"/> but leaves the DATA chunk's payload
    /// on disk; <see cref="GetMedia"/> then returns empty and <see cref="TryGetMediaRange"/> says where each file is.
    /// </summary>
    public static SoundBank ReadWithoutMedia(Stream stream, long offset, long size)
    {
        if (offset < 0 || size < 0 || size > stream.Length - offset || size > int.MaxValue)
            throw new InvalidDataException("Bank lies outside the package.");
        var compact = new MemoryStream();
        (int, int)? data = null;
        Span<byte> head = stackalloc byte[8];
        long pos = 0;
        while (pos + 8 <= size)
        {
            stream.Position = offset + pos;
            stream.ReadExactly(head);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (chunkSize > size - pos - 8) throw new InvalidDataException($"Bank chunk {Encoding.ASCII.GetString(head[..4])} overruns the bank.");
            if (head[..4].SequenceEqual("DATA"u8))
            {
                data ??= ((int)(pos + 8), (int)chunkSize);
                compact.Write(head[..4]);
                compact.Write([0, 0, 0, 0]);                               // size 0: the payload stays on disk
            }
            else
            {
                compact.Write(head);
                var payload = new byte[chunkSize];
                stream.ReadExactly(payload);
                compact.Write(payload);
            }
            pos += 8 + chunkSize;
        }
        return new SoundBank(compact.ToArray(), data ?? (-1, 0));
    }

    public HircObject? Find(uint id) => _objects.GetValueOrDefault(id);

    public bool HasMedia(uint wemId) => _media.ContainsKey(wemId);

    /// <summary>The bytes of an embedded .wem, or empty if this bank does not embed it or was read without media.</summary>
    public ReadOnlyMemory<byte> GetMedia(uint wemId) =>
        MediaInMemory && _media.TryGetValue(wemId, out var m) ? _data.Slice(_dataChunkOffset + m.Offset, m.Size) : ReadOnlyMemory<byte>.Empty;

    /// <summary>Where an embedded .wem lies, as an offset from the start of the bank (valid with or without media).</summary>
    public bool TryGetMediaRange(uint wemId, out long offsetInBank, out int size)
    {
        if (_media.TryGetValue(wemId, out var m)) { offsetInBank = (long)_dataChunkOffset + m.Offset; size = m.Size; return true; }
        offsetInBank = 0; size = 0;
        return false;
    }

    public IEnumerable<HircEvent> Events => Objects.OfType<HircEvent>();

    static string? ReadBankName(ReadOnlySpan<byte> c, uint bankId)
    {
        if (c.Length < 8) return null;
        uint count = U32(c, 4);
        int p = 8;
        string? first = null;
        for (int i = 0; i < count && p + 5 <= c.Length; i++)
        {
            uint id = U32(c, p);
            int len = c[p + 4];
            if (p + 5 + len > c.Length) break;
            string name = Encoding.ASCII.GetString(c.Slice(p + 5, len));
            if (id == bankId) return name;
            first ??= name;
            p += 5 + len;
        }
        return first;
    }

    void ReadHirc(ReadOnlyMemory<byte> hirc, List<HircObject> objects, ref int failures)
    {
        var h = hirc.Span;
        if (h.Length < 4) return;
        uint count = U32(h, 0);
        int p = 4;
        for (int i = 0; i < count; i++)
        {
            if (p + 9 > h.Length) throw new InvalidDataException("HIRC object header overruns the chunk.");
            var type = (HircType)h[p];
            uint size = U32(h, p + 1);
            if (size < 4 || size > h.Length - p - 5) throw new InvalidDataException($"HIRC object {i} overruns the chunk.");
            uint id = U32(h, p + 5);
            var payload = hirc.Slice(p + 9, (int)size - 4);
            var obj = Parse(type, id, payload, FeedbackInBank, out string? error);
            if (error is not null) failures++;
            objects.Add(obj);
            p += 5 + (int)size;
        }
    }

    /// <summary>Decodes one object; on a layout mismatch returns <see cref="HircRaw"/> with the reason.</summary>
    public static HircObject Parse(HircType type, uint id, ReadOnlyMemory<byte> payload, bool feedbackInBank, out string? error)
    {
        error = null;
        if (type is not (HircType.Event or HircType.Action or HircType.Sound or HircType.RandomSequence
            or HircType.Switch or HircType.Layer or HircType.ActorMixer or HircType.DialogueEvent or HircType.MusicSegment or HircType.MusicTrack))
            return new HircRaw(type, id, payload);
        try
        {
            var r = new Reader(payload.Span);
            if (type == HircType.MusicSegment) return ReadMusicSegment(ref r, id, payload, feedbackInBank);   // partial reads; never a failure
            if (type == HircType.MusicTrack) return ReadMusicTrack(ref r, id, payload);
            HircObject obj = type switch
            {
                HircType.Event => ReadEvent(ref r, id, payload),
                HircType.Action => ReadAction(ref r, id, payload),
                HircType.Sound => ReadSound(ref r, id, payload, feedbackInBank),
                HircType.RandomSequence => ReadRandomSequence(ref r, id, payload, feedbackInBank),
                HircType.Switch => ReadSwitch(ref r, id, payload, feedbackInBank),
                HircType.Layer => ReadLayer(ref r, id, payload, feedbackInBank),
                HircType.DialogueEvent => ReadDialogue(ref r, id, payload),
                _ => new HircActorMixer(id, payload, ReadNodeBase(ref r, feedbackInBank), ReadIds(ref r)),
            };
            if (r.Remaining != 0) throw new InvalidDataException($"{r.Remaining} bytes left over");
            return obj;
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return new HircRaw(type, id, payload, ex.Message);
        }
    }

    /// <summary>A music segment's node base and children; any layout mismatch keeps the object raw without counting as a failure (music is read only where played).</summary>
    static HircObject ReadMusicSegment(ref Reader r, uint id, ReadOnlyMemory<byte> payload, bool feedback)
    {
        try { return new HircMusicSegment(id, payload, ReadNodeBase(ref r, feedback), ReadIds(ref r)); }
        catch (InvalidDataException) { return new HircRaw(HircType.MusicSegment, id, payload); }
    }

    /// <summary>A music track's sources; any layout mismatch keeps the object raw without counting as a failure.</summary>
    static HircObject ReadMusicTrack(ref Reader r, uint id, ReadOnlyMemory<byte> payload)
    {
        try
        {
            uint n = r.CheckedCount(r.U32(), 17);
            var sources = new HircMusicSource[n];
            for (int i = 0; i < n; i++)
            {
                uint plugin = r.U32();
                var stream = (HircStreamType)r.U32();
                uint source = r.U32(), file = r.U32();
                if (stream != HircStreamType.Streamed) { r.U32(); r.U32(); }
                r.U8();
                if ((plugin & 0x0F) is 2 or 5) r.Skip((int)r.U32());
                sources[i] = new HircMusicSource(plugin, stream, source, file);
            }
            return new HircMusicTrack(id, payload, sources);
        }
        catch (InvalidDataException) { return new HircRaw(HircType.MusicTrack, id, payload); }
    }

    static HircEvent ReadEvent(ref Reader r, uint id, ReadOnlyMemory<byte> payload) => new(id, payload, ReadIds(ref r));

    static HircAction ReadAction(ref Reader r, uint id, ReadOnlyMemory<byte> payload)
    {
        uint type = r.U32(), target = r.U32();
        int delay = r.S32(), min = r.S32(), max = r.S32();
        r.U32();                                                    // subsection size
        int start = r.Position;
        r.Skip(r.Remaining);
        return new HircAction(id, payload, type, target, delay, min, max, payload[start..]);
    }

    static HircSound ReadSound(ref Reader r, uint id, ReadOnlyMemory<byte> payload, bool feedback)
    {
        uint plugin = r.U32();
        var stream = (HircStreamType)r.U32();
        uint source = r.U32(), file = r.U32();
        uint offset = 0, size = 0;
        if (stream != HircStreamType.Streamed) { offset = r.U32(); size = r.U32(); }
        byte bits = r.U8();
        if ((plugin & 0x0F) is 2 or 5) r.Skip((int)r.U32());      // source plug-in parameters
        var nodeBase = ReadNodeBase(ref r, feedback);
        short loop = r.S16();
        r.S16(); r.S16();                                           // loop count random range
        return new HircSound(id, payload, nodeBase, plugin, stream, source, file, offset, size, bits, loop);
    }

    static HircRandomSequence ReadRandomSequence(ref Reader r, uint id, ReadOnlyMemory<byte> payload, bool feedback)
    {
        var nodeBase = ReadNodeBase(ref r, feedback);
        ushort loop = r.U16();
        var transition = new RangedValue(r.F32(), r.F32(), r.F32());
        ushort avoid = r.U16();
        byte transitionMode = r.U8(), randomMode = r.U8(), mode = r.U8();
        bool weight = r.U8() != 0, reset = r.U8() != 0, backward = r.U8() != 0, continuous = r.U8() != 0, global = r.U8() != 0;
        var children = ReadIds(ref r);
        int n = r.U16();
        var playlist = new (uint, byte)[n];
        for (int i = 0; i < n; i++) playlist[i] = (r.U32(), r.U8());
        return new HircRandomSequence(id, payload, nodeBase, children, loop, transition, avoid, transitionMode, randomMode, mode,
            weight, reset, backward, continuous, global, playlist);
    }

    static HircDialogueEvent ReadDialogue(ref Reader r, uint id, ReadOnlyMemory<byte> payload)
    {
        uint depth = r.CheckedCount(r.U32(), 4);
        var args = new uint[depth];
        for (int i = 0; i < depth; i++) args[i] = r.U32();
        uint size = r.U32();
        if (size % 12 != 0) throw new InvalidDataException($"dialogue tree size {size} is not a multiple of 12");
        r.U16();                                                    // 100 on every event of the install
        int n = (int)(size / 12);
        if ((long)n * 12 > r.Remaining) throw new InvalidDataException($"dialogue tree of {n} nodes overruns the object");
        var nodes = new DialogueNode[n];
        for (int i = 0; i < n; i++) nodes[i] = new DialogueNode(r.U32(), r.U32(), r.U16(), r.U16());
        return new HircDialogueEvent(id, payload, args, nodes);
    }

    static HircSwitch ReadSwitch(ref Reader r, uint id, ReadOnlyMemory<byte> payload, bool feedback)
    {
        var nodeBase = ReadNodeBase(ref r, feedback);
        uint groupType = r.U32(), group = r.U32(), dflt = r.U32();
        bool continuous = r.U8() != 0;
        var children = ReadIds(ref r);
        uint n = r.CheckedCount(r.U32(), 8);
        var cases = new HircSwitchCase[n];
        for (int i = 0; i < n; i++)
        {
            uint switchId = r.U32();
            cases[i] = new HircSwitchCase(switchId, ReadIds(ref r));
        }
        uint p = r.CheckedCount(r.U32(), 18);
        r.Skip((int)p * 18);                                        // {u32 node, u8 firstOnly, u8 continuePlayback, u32 onSwitchMode, s32 fadeOut, s32 fadeIn}
        return new HircSwitch(id, payload, nodeBase, children, groupType, group, dflt, continuous, cases);
    }

    static HircLayer ReadLayer(ref Reader r, uint id, ReadOnlyMemory<byte> payload, bool feedback)
    {
        var nodeBase = ReadNodeBase(ref r, feedback);
        var children = ReadIds(ref r);
        uint n = r.CheckedCount(r.U32(), 14);
        var layers = new HircLayerInfo[n];
        for (int i = 0; i < n; i++)
        {
            uint layerId = r.U32();
            var layerRtpcs = ReadRtpcs(ref r);
            uint rtpc = r.U32();
            float crossfade = r.F32();
            uint assoc = r.CheckedCount(r.U32(), 8);
            var ids = new uint[assoc];
            var curves = new IReadOnlyList<CurvePoint>[assoc];
            for (int a = 0; a < assoc; a++)
            {
                ids[a] = r.U32();
                int points = (int)r.CheckedCount(r.U32(), 12);       // crossfade curve points {f32 from, f32 to, u32 interpolation}
                var pts = new CurvePoint[points];
                for (int k = 0; k < points; k++) pts[k] = new CurvePoint(r.F32(), r.F32(), r.U32());
                curves[a] = pts;
            }
            layers[i] = new HircLayerInfo(layerId, rtpc, crossfade, ids) { Rtpcs = layerRtpcs, ChildCurves = curves };
        }
        return new HircLayer(id, payload, nodeBase, children, layers);
    }

    /// <summary>
    /// v56 node base: effects, output bus, parent, priority bytes, 12 floats (volume, LFE, pitch, LPF as value/min/max),
    /// positioning, advanced settings, state groups, RTPCs and, when the bank says so, feedback.
    /// </summary>
    static HircNodeBase ReadNodeBase(ref Reader r, bool feedback)
    {
        bool overrideFx = r.U8() != 0;
        int fxCount = r.U8();
        var fx = new uint[fxCount];
        if (fxCount > 0)
        {
            r.U8();                                                 // bypass bits
            for (int i = 0; i < fxCount; i++) { r.U8(); fx[i] = r.U32(); r.U8(); r.U8(); }   // index, fxId, isShareSet, isRendered
        }
        uint bus = r.U32(), parent = r.U32();
        byte priority = r.U8();
        bool prioOverride = r.U8() != 0, prioDistance = r.U8() != 0;
        sbyte distOffset = (sbyte)r.U8();
        var volume = new RangedValue(r.F32(), r.F32(), r.F32());
        var lfe = new RangedValue(r.F32(), r.F32(), r.F32());
        var pitch = new RangedValue(r.F32(), r.F32(), r.F32());
        var lpf = new RangedValue(r.F32(), r.F32(), r.F32());

        // Positioning.
        bool posOverride = r.U8() != 0, is3D = false;
        uint positionType = 0, attenuation = 0;
        if (posOverride)
        {
            r.S32(); r.F32(); r.F32();                              // center %, pan rear-left, pan front-right
            is3D = r.U8() != 0;
            if (!is3D) r.U8();                                      // panner enabled
            else
            {
                positionType = r.U32();
                attenuation = r.U32();
                r.U8();                                             // spatialized
                if (positionType == 2)
                {
                    r.U32(); r.U8(); r.S32(); r.U8();               // path mode, looping, transition time, follow orientation
                    r.Skip((int)r.CheckedCount(r.U32(), 16) * 16);  // vertices {x, y, z, duration}
                    uint items = r.CheckedCount(r.U32(), 16);
                    r.Skip((int)items * 8);                         // playlist items {vertex offset, vertex count}
                    r.Skip((int)items * 8);                         // automation ranges {x, y}
                }
                else if (positionType == 3) r.U8();                 // dynamic
            }
        }

        // Advanced settings: virtual queue, kill newest, use virtual, max instances, global limit, below threshold, 2 overrides.
        bool killNewest = r.U8() != 0;
        byte virtualBehavior = r.U8();
        bool ignoreParentMax = r.U8() != 0;
        ushort maxInstances = r.U16();
        bool globalLimit = r.U8() != 0;
        byte belowThreshold = r.U8();
        byte trailingA = r.U8(), trailingB = r.U8();

        // State groups.
        uint groups = r.CheckedCount(r.U32(), 7);
        for (int g = 0; g < groups; g++)
        {
            r.U32(); r.U8();
            r.Skip(r.U16() * 8);                                    // {stateId, stateInstanceId}
        }
        var rtpcs = ReadRtpcs(ref r);
        if (feedback && r.U32() != 0) r.Skip(24);                   // feedback bus volume/LPF ranges
        return new HircNodeBase(overrideFx, fx, bus, parent, priority, prioOverride, prioDistance, distOffset,
            volume, lfe, pitch, lpf, posOverride, is3D, positionType, attenuation, maxInstances, (int)groups, rtpcs.Count)
        {
            Rtpcs = rtpcs, KillNewest = killNewest, VirtualBehavior = virtualBehavior, IgnoreParentMaxInstances = ignoreParentMax, GlobalLimit = globalLimit, BelowThreshold = belowThreshold,
            TrailingA = trailingA, TrailingB = trailingB,
        };
    }

    /// <summary>u16 count, {u32 rtpcId, u32 parameterId, u32 curveId, u8 scaling, u16 points, points × 12 bytes {f32 x, f32 y, u32 shape}}.</summary>
    static List<HircRtpc> ReadRtpcs(ref Reader r)
    {
        int n = (int)r.CheckedCount(r.U16(), 15);
        var list = new List<HircRtpc>(n);
        for (int i = 0; i < n; i++)
        {
            uint rtpc = r.U32(), param = r.U32(), curve = r.U32();
            byte scaling = r.U8();
            int points = (int)r.CheckedCount(r.U16(), 12);
            var pts = new CurvePoint[points];
            for (int k = 0; k < points; k++)
            {
                var pt = new CurvePoint(r.F32(), r.F32(), r.U32());
                if (!float.IsFinite(pt.X) || !float.IsFinite(pt.Y)) throw new InvalidDataException("an RTPC curve point is not finite");
                if (k > 0 && pt.X < pts[k - 1].X) throw new InvalidDataException("RTPC curve points are not sorted");
                pts[k] = pt;
            }
            list.Add(new HircRtpc(rtpc, param, curve, scaling, pts));
        }
        return list;
    }

    static uint[] ReadIds(ref Reader r)
    {
        uint n = r.CheckedCount(r.U32(), 4);
        var ids = new uint[n];
        for (int i = 0; i < n; i++) ids[i] = r.U32();
        return ids;
    }

    static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);

    /// <summary>Bounds-checked little-endian reader; any overrun becomes an <see cref="InvalidDataException"/>.</summary>
    ref struct Reader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _d = data;
        int _p;

        public readonly int Position => _p;
        public readonly int Remaining => _d.Length - _p;

        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > Remaining) throw new InvalidDataException($"read past the end at {_p} (+{n})");
            var s = _d.Slice(_p, n);
            _p += n;
            return s;
        }

        public void Skip(int n) => Take(n);
        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public short S16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int S32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        /// <summary>Rejects counts that could not fit in the rest of the payload at <paramref name="minBytes"/> each.</summary>
        public readonly uint CheckedCount(uint n, int minBytes) =>
            (long)n * minBytes > Remaining ? throw new InvalidDataException($"count {n} too large at {_p}") : n;
    }
}
