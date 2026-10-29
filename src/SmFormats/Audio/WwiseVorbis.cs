// Wwise Vorbis -> standard Vorbis packets. A C# port of ww2ogg 0.24 by Adam Gashlin (hcs),
// https://github.com/hcs64/ww2ogg (src/wwriff.cpp, src/codebook.cpp, src/Bit_stream.h), used under its BSD licence.
// _book_maptype1_quantvals and ilog come from Tremor (Xiph.org), as in ww2ogg. Changes from the original: in-memory
// input, packets returned as byte arrays (the Ogg pages are written by OggWriter with computed granule positions),
// zero-length packets passed through, the "header triad" (vorb sizes 0x28/0x2C) variant is not ported. ww2ogg's
// codebook library (packed_codebooks.bin) is not distributed here; it is downloaded on first use (SmContent.AudioCache).
//
// Original licence (ww2ogg COPYING), reproduced as its terms require:
//
// Copyright (c) 2002, Xiph.org Foundation
// Copyright (c) 2009-2016, Adam Gashlin
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
// - Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//
// - Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
//
// - Neither the name of the Xiph.org Foundation nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
// OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System.Buffers.Binary;
using System.Text;

namespace SmFormats.Audio;

/// <summary>Header fields of a Wwise RIFF/RIFX Vorbis file (.wem).</summary>
public sealed record WwiseVorbisInfo(
    bool LittleEndian,
    int Channels,
    int SampleRate,
    int AverageBytesPerSecond,
    uint ChannelLayout,
    uint SampleCount,
    uint SetupPacketOffset,
    uint FirstAudioPacketOffset,
    int Blocksize0Exponent,
    int Blocksize1Exponent,
    bool NoGranule,
    bool ModPackets,
    bool HeaderTriad,
    uint LoopCount,
    uint LoopStart,
    uint LoopEnd,
    int DataOffset,
    int DataSize,
    int VorbSize)
{
    public double Duration => SampleRate > 0 ? (double)SampleCount / SampleRate : 0;
}

/// <summary>Standard Vorbis packets rebuilt from a .wem, ready for an Ogg writer or a packet-level decoder.</summary>
public sealed record VorbisStream(
    WwiseVorbisInfo Info,
    byte[] Identification,
    byte[] Comment,
    byte[] Setup,
    IReadOnlyList<byte[]> AudioPackets,
    IReadOnlyList<long> Granules)
{
    /// <summary>Samples per channel the packets decode to (the last granule position).</summary>
    public long TotalSamples => Granules.Count > 0 ? Granules[^1] : 0;
}

/// <summary>How to read the setup packet and audio packets (ww2ogg's command-line options).</summary>
public sealed record WwiseVorbisOptions(bool InlineCodebooks = false, bool FullSetup = false, bool? ForceModPackets = null)
{
    public static WwiseVorbisOptions Default { get; } = new();
}

/// <summary>
/// Converts Wwise Vorbis (.wem, codec 0xFFFF) into standard Vorbis: rebuilds the identification, comment and setup
/// headers (expanding codebooks from the external library) and restores the packet type and window bits that Wwise
/// strips from audio packets. Port of ww2ogg 0.24 (BSD-3, see the file header).
/// </summary>
public static class WwiseVorbis
{
    public const string Vendor = "converted from Audiokinetic Wwise by sm-reimpl (port of ww2ogg 0.24)";

    public static bool IsWwiseVorbis(ReadOnlySpan<byte> wem)
    {
        try { ReadInfo(wem); return true; }
        catch (InvalidDataException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>Parses the RIFF chunks and the Vorbis header fields (ww2ogg's Wwise_RIFF_Vorbis constructor).</summary>
    public static WwiseVorbisInfo ReadInfo(ReadOnlySpan<byte> f, bool? forceModPackets = null)
    {
        if (f.Length < 12) throw new InvalidDataException("missing RIFF");
        bool le;
        if (f.StartsWith("RIFX"u8)) le = false;
        else if (f.StartsWith("RIFF"u8)) le = true;
        else throw new InvalidDataException("missing RIFF");
        var r = new EndianReader(f, le);
        long riffSize = (long)r.U32(4) + 8;
        if (riffSize > f.Length) throw new InvalidDataException("RIFF truncated");
        if (!f.Slice(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException("missing WAVE");

        long fmtOffset = -1, cueOffset = -1, smplOffset = -1, vorbOffset = -1, dataOffset = -1;
        long fmtSize = -1, vorbSize = -1, dataSize = -1;
        long chunk = 12;
        while (chunk < riffSize)
        {
            if (chunk + 8 > riffSize) throw new InvalidDataException("chunk header truncated");
            var type = f.Slice((int)chunk, 4);
            long size = r.U32((int)chunk + 4);
            if (type.SequenceEqual("fmt "u8)) { fmtOffset = chunk + 8; fmtSize = size; }
            else if (type.SequenceEqual("cue "u8)) cueOffset = chunk + 8;
            else if (type.SequenceEqual("smpl"u8)) smplOffset = chunk + 8;
            else if (type.SequenceEqual("vorb"u8)) { vorbOffset = chunk + 8; vorbSize = size; }
            else if (type.SequenceEqual("data"u8)) { dataOffset = chunk + 8; dataSize = size; }
            chunk = chunk + 8 + size;
        }
        if (chunk > riffSize) throw new InvalidDataException("chunk truncated");
        if (fmtOffset == -1 || dataOffset == -1) throw new InvalidDataException("expected fmt, data chunks");
        if (vorbOffset == -1 && fmtSize != 0x42) throw new InvalidDataException("expected 0x42 fmt if vorb missing");
        if (vorbOffset != -1 && fmtSize is not (0x28 or 0x18 or 0x12)) throw new InvalidDataException("bad fmt size");
        if (vorbOffset == -1 && fmtSize == 0x42) vorbOffset = fmtOffset + 0x18;   // the vorb fields sit inside fmt

        int p = (int)fmtOffset;
        if (r.U16(p) != 0xFFFF) throw new InvalidDataException("bad codec id");
        int channels = r.U16(p + 2);
        int sampleRate = (int)r.U32(p + 4);
        int avgBytes = (int)r.U32(p + 8);
        if (r.U16(p + 12) != 0) throw new InvalidDataException("bad block align");
        if (r.U16(p + 14) != 0) throw new InvalidDataException("expected 0 bps");
        if (fmtSize - 0x12 != r.U16(p + 16)) throw new InvalidDataException("bad extra fmt length");
        uint subtype = fmtSize - 0x12 >= 6 ? r.U32(p + 20) : 0;
        if (fmtSize == 0x28)
        {
            ReadOnlySpan<byte> check = [1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9b, 0x71];
            if (!f.Slice(p + 24, 16).SequenceEqual(check)) throw new InvalidDataException("expected signature in extra fmt?");
        }
        _ = cueOffset;

        uint loopCount = 0, loopStart = 0, loopEnd = 0;
        if (smplOffset != -1)
        {
            loopCount = r.U32((int)smplOffset + 0x1C);
            if (loopCount != 1) throw new InvalidDataException("expected one loop");
            loopStart = r.U32((int)smplOffset + 0x2C);
            loopEnd = r.U32((int)smplOffset + 0x30);
        }

        if (vorbSize is not (-1 or 0x28 or 0x2A or 0x2C or 0x32 or 0x34)) throw new InvalidDataException("bad vorb size");
        int v = (int)vorbOffset;
        uint sampleCount = r.U32(v);
        bool noGranule = false, modPackets = false;
        int q;
        if (vorbSize is -1 or 0x2A)
        {
            noGranule = true;
            uint modSignal = r.U32(v + 4);
            // 0x4A, 0x4B, 0x69, 0x70 mean unmodified packets; other values (seen: 0xD9, 0xCB, 0xBC, 0xB2) mean modified.
            if (modSignal is not (0x4A or 0x4B or 0x69 or 0x70)) modPackets = true;
            q = v + 0x10;
        }
        else q = v + 0x18;
        if (forceModPackets is bool force) modPackets = force;

        uint setupOffset = r.U32(q), firstAudioOffset = r.U32(q + 4);
        bool triad = vorbSize is 0x28 or 0x2C;
        int bs0 = 0, bs1 = 0;
        if (!triad)
        {
            int u = vorbSize is 0x32 or 0x34 ? v + 0x2C : v + 0x24;
            _ = r.U32(u);                                                     // uid
            if (u + 6 > f.Length) throw new InvalidDataException("file truncated");
            bs0 = f[u + 4];
            bs1 = f[u + 5];
            // Vorbis I allows block sizes 2^6 .. 2^13 with blocksize0 <= blocksize1.
            if (bs0 is < 6 or > 13 || bs1 is < 6 or > 13 || bs0 > bs1) throw new InvalidDataException($"bad block size exponents {bs0}/{bs1}");
        }
        if (channels is < 1 or > 255) throw new InvalidDataException($"bad channel count {channels}");
        if (sampleRate <= 0) throw new InvalidDataException($"bad sample rate {sampleRate}");

        if (loopCount != 0)
        {
            loopEnd = loopEnd == 0 ? sampleCount : loopEnd + 1;
            if (loopStart >= sampleCount || loopEnd > sampleCount || loopStart > loopEnd)
                throw new InvalidDataException("loops out of range");
        }
        if (dataOffset + dataSize > f.Length) throw new InvalidDataException("data chunk truncated");

        return new WwiseVorbisInfo(le, channels, sampleRate, avgBytes, subtype, sampleCount, setupOffset, firstAudioOffset,
            bs0, bs1, noGranule, modPackets, triad, loopCount, loopStart, loopEnd, (int)dataOffset, (int)dataSize, (int)vorbSize);
    }

    /// <summary>
    /// Rebuilds the three Vorbis headers and all audio packets, with granule positions per packet.
    /// <paramref name="codebooks"/> is required unless the options say the codebooks are inline. Malformed input
    /// raises <see cref="InvalidDataException"/> (or <see cref="NotSupportedException"/> for variants not ported).
    /// </summary>
    public static VorbisStream Convert(ReadOnlySpan<byte> wem, VorbisCodebookLibrary? codebooks = null, WwiseVorbisOptions? options = null)
    {
        options ??= WwiseVorbisOptions.Default;
        if (!options.InlineCodebooks && codebooks is null)
            throw new ArgumentNullException(nameof(codebooks), "External codebooks need a codebook library (ww2ogg's packed_codebooks.bin).");
        var info = ReadInfo(wem, options.ForceModPackets);
        if (info.HeaderTriad) throw new NotSupportedException("Wwise Vorbis with a header triad (vorb size 0x28/0x2C) is not supported.");
        try
        {
            byte[] ident = BuildIdentification(info);
            byte[] comment = BuildComment(info);
            byte[] setup = BuildSetup(wem, info, codebooks, options, out bool[]? modeBlockflag, out int modeBits);
            var packets = BuildAudioPackets(wem, info, modeBlockflag, modeBits, out var headerGranules);
            var granules = ComputeGranules(packets, headerGranules, info, modeBlockflag, modeBits);
            return new VorbisStream(info, ident, comment, setup, packets, granules);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"malformed Wwise Vorbis data: {ex.Message}", ex);
        }
    }

    /// <summary>Converts a .wem straight to Ogg Vorbis file bytes.</summary>
    public static byte[] ToOgg(ReadOnlySpan<byte> wem, VorbisCodebookLibrary? codebooks = null, WwiseVorbisOptions? options = null)
    {
        var stream = Convert(wem, codebooks, options);
        using var ms = new MemoryStream();
        OggWriter.Write(stream, ms);
        return ms.ToArray();
    }

    static void PacketHeader(BitWriter os, int type)
    {
        os.Write((uint)type, 8);
        foreach (byte c in "vorbis"u8) os.Write(c, 8);
    }

    static byte[] BuildIdentification(WwiseVorbisInfo info)
    {
        var os = new BitWriter();
        PacketHeader(os, 1);
        os.Write(0, 32);                                                    // version
        os.Write((uint)info.Channels, 8);
        os.Write((uint)info.SampleRate, 32);
        os.Write(0, 32);                                                    // bitrate maximum
        os.Write((uint)info.AverageBytesPerSecond * 8, 32);                 // bitrate nominal
        os.Write(0, 32);                                                    // bitrate minimum
        os.Write((uint)info.Blocksize0Exponent, 4);
        os.Write((uint)info.Blocksize1Exponent, 4);
        os.Write(1, 1);                                                     // framing
        return os.ToArray();
    }

    static byte[] BuildComment(WwiseVorbisInfo info)
    {
        var os = new BitWriter();
        PacketHeader(os, 3);
        WriteString(os, Vendor);
        if (info.LoopCount == 0) os.Write(0, 32);
        else
        {
            os.Write(2, 32);
            WriteString(os, $"LoopStart={info.LoopStart}");
            WriteString(os, $"LoopEnd={info.LoopEnd}");
        }
        os.Write(1, 1);                                                     // framing
        return os.ToArray();
    }

    static void WriteString(BitWriter os, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        os.Write((uint)bytes.Length, 32);
        foreach (byte b in bytes) os.Write(b, 8);
    }

    /// <summary>Bounds-checked RIFF (little-endian) or RIFX (big-endian) field reads.</summary>
    readonly ref struct EndianReader(ReadOnlySpan<byte> f, bool littleEndian)
    {
        readonly ReadOnlySpan<byte> _f = f;

        public uint U32(int o) => o < 0 || o + 4 > _f.Length ? throw new InvalidDataException("file truncated")
            : littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(_f[o..]) : BinaryPrimitives.ReadUInt32BigEndian(_f[o..]);

        public ushort U16(int o) => o < 0 || o + 2 > _f.Length ? throw new InvalidDataException("file truncated")
            : littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(_f[o..]) : BinaryPrimitives.ReadUInt16BigEndian(_f[o..]);
    }

    /// <summary>Wwise packet header: u16 size, then u32 granule unless the file has 2-byte headers.</summary>
    readonly record struct Packet(int Offset, int HeaderSize, int Size, uint Granule)
    {
        public int PayloadOffset => Offset + HeaderSize;
        public int NextOffset => Offset + HeaderSize + Size;

        public static Packet Read(ReadOnlySpan<byte> f, int offset, WwiseVorbisInfo info)
        {
            int header = info.NoGranule ? 2 : 6;
            if (offset < 0 || offset + header > f.Length) throw new InvalidDataException("packet header truncated");
            var h = f.Slice(offset, header);
            int size = info.LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(h) : BinaryPrimitives.ReadUInt16BigEndian(h);
            uint granule = info.NoGranule ? 0 : info.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(h[2..]) : BinaryPrimitives.ReadUInt32BigEndian(h[2..]);
            return new Packet(offset, header, size, granule);
        }
    }

    static byte[] BuildSetup(ReadOnlySpan<byte> f, WwiseVorbisInfo info, VorbisCodebookLibrary? library, WwiseVorbisOptions options,
        out bool[]? modeBlockflag, out int modeBits)
    {
        modeBlockflag = null;
        modeBits = 0;
        var os = new BitWriter();
        PacketHeader(os, 5);

        var setup = Packet.Read(f, info.DataOffset + (int)info.SetupPacketOffset, info);
        if (setup.Granule != 0) throw new InvalidDataException("setup packet granule != 0");
        if (setup.PayloadOffset + setup.Size > f.Length) throw new InvalidDataException("setup packet truncated");
        var ss = new BitReader(f.Slice(setup.PayloadOffset, setup.Size).ToArray());

        uint codebookCountLess1 = ss.Read(8);
        int codebookCount = (int)codebookCountLess1 + 1;
        os.Write(codebookCountLess1, 8);

        if (options.InlineCodebooks)
        {
            for (int i = 0; i < codebookCount; i++)
            {
                if (options.FullSetup) VorbisCodebookLibrary.Copy(ss, os);
                else VorbisCodebookLibrary.Rebuild(ss, 0, os);
            }
        }
        else
        {
            for (int i = 0; i < codebookCount; i++)
            {
                int id = (int)ss.Read(10);
                if (!library!.TryRebuild(id, os))
                {
                    if (id == 0x342 && ss.Read(14) == 0x1590)
                        throw new InvalidDataException("invalid codebook id 0x342, try full setup");   // starts with "BCV": a full setup
                    throw new InvalidDataException($"invalid codebook id {id}");
                }
            }
        }

        // Time domain transforms (placeholder).
        os.Write(0, 6);
        os.Write(0, 16);

        if (options.FullSetup)
        {
            while (ss.BitsRead < setup.Size * 8L) os.Write(ss.Read(1), 1);
        }
        else
        {
            // Floors: always type 1.
            int floorCount = (int)ss.Read(6) + 1;
            os.Write((uint)floorCount - 1, 6);
            for (int i = 0; i < floorCount; i++)
            {
                os.Write(1, 16);
                int partitions = (int)Copy(ss, os, 5);
                var partitionClass = new int[partitions];
                int maximumClass = 0;
                for (int j = 0; j < partitions; j++)
                {
                    partitionClass[j] = (int)Copy(ss, os, 4);
                    maximumClass = Math.Max(maximumClass, partitionClass[j]);
                }
                var classDimensions = new int[maximumClass + 1];
                for (int j = 0; j <= maximumClass; j++)
                {
                    classDimensions[j] = (int)Copy(ss, os, 3) + 1;
                    int subclasses = (int)Copy(ss, os, 2);
                    if (subclasses != 0)
                    {
                        uint masterbook = Copy(ss, os, 8);
                        if (masterbook >= codebookCount) throw new InvalidDataException("invalid floor1 masterbook");
                    }
                    for (int k = 0; k < 1 << subclasses; k++)
                    {
                        int subclassBook = (int)Copy(ss, os, 8) - 1;
                        if (subclassBook >= 0 && subclassBook >= codebookCount) throw new InvalidDataException("invalid floor1 subclass book");
                    }
                }
                Copy(ss, os, 2);                                            // multiplier - 1
                int rangeBits = (int)Copy(ss, os, 4);
                for (int j = 0; j < partitions; j++)
                    for (int k = 0; k < classDimensions[partitionClass[j]]; k++)
                        Copy(ss, os, rangeBits);
            }

            // Residues.
            int residueCount = (int)ss.Read(6) + 1;
            os.Write((uint)residueCount - 1, 6);
            for (int i = 0; i < residueCount; i++)
            {
                uint residueType = ss.Read(2);
                os.Write(residueType, 16);
                if (residueType > 2) throw new InvalidDataException("invalid residue type");
                Copy(ss, os, 24); Copy(ss, os, 24); Copy(ss, os, 24);       // begin, end, partition size - 1
                int classifications = (int)Copy(ss, os, 6) + 1;
                uint classbook = Copy(ss, os, 8);
                if (classbook >= codebookCount) throw new InvalidDataException("invalid residue classbook");
                var cascade = new uint[classifications];
                for (int j = 0; j < classifications; j++)
                {
                    uint low = Copy(ss, os, 3);
                    uint high = 0;
                    if (Copy(ss, os, 1) != 0) high = Copy(ss, os, 5);
                    cascade[j] = high * 8 + low;
                }
                for (int j = 0; j < classifications; j++)
                    for (int k = 0; k < 8; k++)
                        if ((cascade[j] & (1u << k)) != 0 && Copy(ss, os, 8) >= codebookCount)
                            throw new InvalidDataException("invalid residue book");
            }

            // Mappings: always type 0.
            int mappingCount = (int)ss.Read(6) + 1;
            os.Write((uint)mappingCount - 1, 6);
            for (int i = 0; i < mappingCount; i++)
            {
                os.Write(0, 16);
                int submaps = 1;
                if (Copy(ss, os, 1) != 0) submaps = (int)Copy(ss, os, 4) + 1;
                if (Copy(ss, os, 1) != 0)                                   // square polar
                {
                    int steps = (int)Copy(ss, os, 8) + 1;
                    int bits = ILog((uint)info.Channels - 1);
                    for (int j = 0; j < steps; j++)
                    {
                        uint magnitude = Copy(ss, os, bits), angle = Copy(ss, os, bits);
                        if (angle == magnitude || magnitude >= info.Channels || angle >= info.Channels)
                            throw new InvalidDataException("invalid coupling");
                    }
                }
                if (Copy(ss, os, 2) != 0) throw new InvalidDataException("mapping reserved field nonzero");
                if (submaps > 1)
                    for (int j = 0; j < info.Channels; j++)
                        if (Copy(ss, os, 4) >= submaps) throw new InvalidDataException("mapping_mux >= submaps");
                for (int j = 0; j < submaps; j++)
                {
                    Copy(ss, os, 8);                                        // time config
                    if (Copy(ss, os, 8) >= floorCount) throw new InvalidDataException("invalid floor mapping");
                    if (Copy(ss, os, 8) >= residueCount) throw new InvalidDataException("invalid residue mapping");
                }
            }

            // Modes: window and transform types are always 0.
            int modeCount = (int)ss.Read(6) + 1;
            os.Write((uint)modeCount - 1, 6);
            modeBlockflag = new bool[modeCount];
            modeBits = ILog((uint)modeCount - 1);
            for (int i = 0; i < modeCount; i++)
            {
                modeBlockflag[i] = Copy(ss, os, 1) != 0;
                os.Write(0, 16);
                os.Write(0, 16);
                if (Copy(ss, os, 8) >= mappingCount) throw new InvalidDataException("invalid mode mapping");
            }
            os.Write(1, 1);                                                 // framing
        }

        if ((ss.BitsRead + 7) / 8 != setup.Size) throw new InvalidDataException("didn't read exactly setup packet");
        if (setup.NextOffset != info.DataOffset + (int)info.FirstAudioPacketOffset)
            throw new InvalidDataException("first audio packet doesn't follow setup packet");
        return os.ToArray();
    }

    static uint Copy(BitReader ss, BitWriter os, int bits)
    {
        uint v = ss.Read(bits);
        os.Write(v, bits);
        return v;
    }

    static List<byte[]> BuildAudioPackets(ReadOnlySpan<byte> f, WwiseVorbisInfo info, bool[]? modeBlockflag, int modeBits,
        out List<uint> headerGranules)
    {
        var packets = new List<byte[]>();
        headerGranules = new List<uint>();
        int end = info.DataOffset + info.DataSize;
        int offset = info.DataOffset + (int)info.FirstAudioPacketOffset;
        bool prevBlockflag = false;
        while (offset < end)
        {
            var packet = Packet.Read(f, offset, info);
            if (offset + packet.HeaderSize > end) throw new InvalidDataException("page header truncated");
            int payload = packet.PayloadOffset, size = packet.Size;
            if (payload + size > f.Length) throw new InvalidDataException("file truncated");

            var os = new BitWriter();
            if (size > 0 && info.ModPackets)
            {
                if (modeBlockflag is null) throw new InvalidDataException("didn't load mode_blockflag");
                os.Write(0, 1);                                             // packet type: audio
                var ss = new BitReader(f.Slice(payload, 1).ToArray());
                uint mode = ss.Read(modeBits);
                os.Write(mode, modeBits);
                uint remainder = ss.Read(8 - modeBits);
                if (mode >= modeBlockflag.Length) throw new InvalidDataException("invalid mode number");
                if (modeBlockflag[mode])
                {
                    // Long window: the next packet's mode gives the next-window flag.
                    bool nextBlockflag = false;
                    int next = packet.NextOffset;
                    if (next + packet.HeaderSize <= end)
                    {
                        var np = Packet.Read(f, next, info);
                        if (np.Size > 0 && np.PayloadOffset < f.Length)
                        {
                            uint nextMode = new BitReader(f.Slice(np.PayloadOffset, 1).ToArray()).Read(modeBits);
                            nextBlockflag = nextMode < modeBlockflag.Length && modeBlockflag[nextMode];
                        }
                    }
                    os.Write(prevBlockflag ? 1u : 0u, 1);
                    os.Write(nextBlockflag ? 1u : 0u, 1);
                }
                prevBlockflag = modeBlockflag[mode];
                os.Write(remainder, 8 - modeBits);
                for (int i = 1; i < size; i++) os.Write(f[payload + i], 8);
            }
            else
            {
                for (int i = 0; i < size; i++) os.Write(f[payload + i], 8);
            }
            packets.Add(os.ToArray());
            headerGranules.Add(packet.Granule);
            offset = packet.NextOffset;
        }
        if (offset > end) throw new InvalidDataException("page truncated");
        return packets;
    }

    /// <summary>
    /// Granule positions from block sizes (each packet after the first adds prevBlock/4 + curBlock/4 samples, as
    /// libvorbis counts them). The last non-empty packet (and any empty ones after it) is clamped to the header's sample
    /// count so the decoder trims the tail, when that cut lies inside that packet as every decoder counts it.
    /// Without mode information (full setup) the granules come from 6-byte packet headers; with 2-byte headers there is
    /// no source for them and the combination is refused.
    /// </summary>
    static List<long> ComputeGranules(List<byte[]> packets, List<uint> headerGranules, WwiseVorbisInfo info, bool[]? modeBlockflag, int modeBits)
    {
        var granules = new List<long>(packets.Count);
        if (modeBlockflag is null)
        {
            if (info.NoGranule) throw new NotSupportedException("full setup with 2-byte packet headers: no granule positions");
            long g = 0;
            foreach (uint h in headerGranules)
            {
                if (h != 0xFFFFFFFF) g = Math.Max(g, h);                    // 0xFFFFFFFF: no position given
                granules.Add(g);
            }
            return granules;
        }
        long total = 0, lastIncrement = 0;
        int prevBlock = 0, lastNonEmpty = -1;
        int bs0 = 1 << info.Blocksize0Exponent, bs1 = 1 << info.Blocksize1Exponent;
        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            if (p.Length > 0)
            {
                var r = new BitReader(p);
                if (r.Read(1) != 0) throw new InvalidDataException("audio packet has a header packet type");
                uint mode = r.Read(modeBits);
                if (mode >= modeBlockflag.Length) throw new InvalidDataException("invalid mode number");
                int block = modeBlockflag[mode] ? bs1 : bs0;
                lastIncrement = 0;
                if (prevBlock != 0)
                {
                    // Trimming must stay within the last packet as every decoder counts it: libvorbis counts
                    // prevBlock/4 + block/4, NVorbis (and others that finish samples early) only block/2.
                    lastIncrement = Math.Min(prevBlock / 4 + block / 4, block / 2);
                    total += prevBlock / 4 + block / 4;
                }
                prevBlock = block;
                lastNonEmpty = i;
            }
            granules.Add(total);
        }
        // A header that would cut further back is left alone (the stream keeps a few extra tail samples).
        if (lastNonEmpty >= 0 && info.SampleCount < total && total - info.SampleCount < lastIncrement)
            for (int i = lastNonEmpty; i < granules.Count; i++) granules[i] = info.SampleCount;
        return granules;
    }

    /// <summary>Number of bits needed to hold <paramref name="v"/> (Tremor's ilog).</summary>
    internal static int ILog(uint v)
    {
        int ret = 0;
        while (v != 0) { ret++; v >>= 1; }
        return ret;
    }
}

/// <summary>
/// ww2ogg's packed codebook library: codebooks in Wwise's stripped form, concatenated, then a table of u32 offsets;
/// the file's last u32 is the table's offset. This game needs ww2ogg's standard <c>packed_codebooks.bin</c>; it is not
/// distributed with this project but downloaded on first use and checked by hash (SmContent.AudioCache).
/// </summary>
public sealed class VorbisCodebookLibrary
{
    readonly byte[] _data;
    readonly int[] _offsets;

    public VorbisCodebookLibrary(byte[] file)
    {
        if (file.Length < 4) throw new InvalidDataException("codebook library too short");
        int offsetOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(file.Length - 4));
        if (offsetOffset < 0 || offsetOffset > file.Length - 4 || (file.Length - offsetOffset) % 4 != 0)
            throw new InvalidDataException("bad codebook library offset table");
        int count = (file.Length - offsetOffset) / 4;
        _data = file[..offsetOffset];
        _offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            _offsets[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offsetOffset + 4 * i));
            if (_offsets[i] < 0 || _offsets[i] > offsetOffset || (i > 0 && _offsets[i] < _offsets[i - 1]))
                throw new InvalidDataException("bad codebook library offset");
        }
    }

    /// <summary>Number of codebooks (valid IDs are 0 .. Count - 1).</summary>
    public int Count => _offsets.Length - 1;

    /// <summary>Expands codebook <paramref name="id"/> into standard form; false for an unknown ID.</summary>
    public bool TryRebuild(int id, BitWriter os)
    {
        if (id < 0 || id >= Count) return false;
        int start = _offsets[id], size = _offsets[id + 1] - start;
        Rebuild(new BitReader(_data.AsSpan(start, size).ToArray()), size, os);
        return true;
    }

    /// <summary>
    /// Stripped codebook -> standard codebook (ww2ogg codebook_library::rebuild). <paramref name="size"/> 0 skips the
    /// exact-size check (inline codebooks).
    /// </summary>
    public static void Rebuild(BitReader bis, int size, BitWriter bos)
    {
        uint dimensions = bis.Read(4), entries = bis.Read(14);
        bos.Write(0x564342, 24);                                            // "BCV"
        bos.Write(dimensions, 16);
        bos.Write(entries, 24);
        CopyLengths(bis, bos, entries, stripped: true);
        uint lookupType = bis.Read(1);
        bos.Write(lookupType, 4);
        CopyLookup(bis, bos, lookupType, entries, dimensions);
        // If all bits of the last byte are used there is one extra zero byte.
        if (size != 0 && bis.BitsRead / 8 + 1 != size)
            throw new InvalidDataException($"codebook size mismatch: expected {size}, read {bis.BitsRead / 8 + 1}");
    }

    /// <summary>A standard codebook copied bit for bit (ww2ogg codebook_library::copy, for full setups).</summary>
    public static void Copy(BitReader bis, BitWriter bos)
    {
        uint id = bis.Read(24), dimensions = bis.Read(16), entries = bis.Read(24);
        if (id != 0x564342) throw new InvalidDataException("invalid codebook identifier");
        bos.Write(id, 24);
        bos.Write(dimensions, 16);
        bos.Write(entries, 24);
        CopyLengths(bis, bos, entries, stripped: false);
        uint lookupType = bis.Read(4);
        bos.Write(lookupType, 4);
        CopyLookup(bis, bos, lookupType, entries, dimensions);
    }

    static void CopyLengths(BitReader bis, BitWriter bos, uint entries, bool stripped)
    {
        uint ordered = bis.Read(1);
        bos.Write(ordered, 1);
        if (ordered != 0)
        {
            bos.Write(bis.Read(5), 5);                                      // initial length
            uint current = 0;
            while (current < entries)
            {
                int bits = WwiseVorbis.ILog(entries - current);
                uint number = bis.Read(bits);
                bos.Write(number, bits);
                current += number;
            }
            if (current > entries) throw new InvalidDataException("current_entry out of range");
            return;
        }
        int lengthBits = 5;
        if (stripped)
        {
            lengthBits = (int)bis.Read(3);
            if (lengthBits is 0 or > 5) throw new InvalidDataException("nonsense codeword length");
        }
        uint sparse = bis.Read(1);
        bos.Write(sparse, 1);
        for (uint i = 0; i < entries; i++)
        {
            bool present = true;
            if (sparse != 0)
            {
                uint p = bis.Read(1);
                bos.Write(p, 1);
                present = p != 0;
            }
            if (present) bos.Write(bis.Read(lengthBits), 5);               // codeword length - 1
        }
    }

    static void CopyLookup(BitReader bis, BitWriter bos, uint lookupType, uint entries, uint dimensions)
    {
        if (lookupType == 0) return;
        if (lookupType == 2) throw new InvalidDataException("didn't expect lookup type 2");
        if (lookupType != 1) throw new InvalidDataException("invalid lookup type");
        bos.Write(bis.Read(32), 32);                                        // minimum
        bos.Write(bis.Read(32), 32);                                        // delta
        uint valueLength = bis.Read(4);
        bos.Write(valueLength, 4);
        bos.Write(bis.Read(1), 1);                                          // sequence flag
        uint quantvals = Maptype1Quantvals(entries, dimensions);
        for (uint i = 0; i < quantvals; i++) bos.Write(bis.Read((int)valueLength + 1), (int)valueLength + 1);
    }

    /// <summary>Tremor's _book_maptype1_quantvals: the largest v with v^dimensions &lt;= entries.</summary>
    internal static uint Maptype1Quantvals(uint entries, uint dimensions)
    {
        if (dimensions == 0 || entries == 0) throw new InvalidDataException("codebook with zero dimensions or entries");
        int bits = WwiseVorbis.ILog(entries);
        long vals = entries >> (int)((bits - 1) * (dimensions - 1) / dimensions);
        while (true)
        {
            ulong acc = 1, acc1 = 1;
            for (uint i = 0; i < dimensions; i++)
            {
                acc = checked(acc * (ulong)vals);
                acc1 = checked(acc1 * (ulong)(vals + 1));
            }
            if (acc <= entries && acc1 > entries) return (uint)vals;
            if (acc > entries) vals--; else vals++;
        }
    }
}

/// <summary>Reads bits least-significant first within each byte (ww2ogg Bit_stream).</summary>
public sealed class BitReader(byte[] data)
{
    readonly byte[] _data = data;
    long _bit;

    public long BitsRead => _bit;

    public uint Read(int bits)
    {
        if (bits is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        uint v = 0;
        for (int i = 0; i < bits; i++)
        {
            long byteIndex = _bit >> 3;
            if (byteIndex >= _data.Length) throw new InvalidDataException("out of bits");
            if ((_data[byteIndex] & (1 << (int)(_bit & 7))) != 0) v |= 1u << i;
            _bit++;
        }
        return v;
    }
}

/// <summary>Writes bits least-significant first within each byte (ww2ogg Bit_oggstream without the paging).</summary>
public sealed class BitWriter
{
    readonly List<byte> _bytes = new();
    int _buffer, _stored;

    public long BitsWritten => _bytes.Count * 8L + _stored;

    public void Write(uint value, int bits)
    {
        if (bits is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        if (bits < 32 && value >> bits != 0) throw new ArgumentOutOfRangeException(nameof(value), "value does not fit");
        for (int i = 0; i < bits; i++)
        {
            if ((value & (1u << i)) != 0) _buffer |= 1 << _stored;
            if (++_stored == 8)
            {
                _bytes.Add((byte)_buffer);
                _buffer = 0;
                _stored = 0;
            }
        }
    }

    /// <summary>The bytes written so far, the last partial byte padded with zero bits.</summary>
    public byte[] ToArray()
    {
        var result = new byte[_bytes.Count + (_stored > 0 ? 1 : 0)];
        _bytes.CopyTo(result);
        if (_stored > 0) result[^1] = (byte)_buffer;
        return result;
    }
}
