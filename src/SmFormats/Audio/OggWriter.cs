using System.Buffers.Binary;

namespace SmFormats.Audio;

/// <summary>
/// Writes Vorbis packets as an Ogg bitstream (RFC 3533): the identification header alone on the first page,
/// comment and setup headers on the next, audio packets packed into pages of about 4 KB with the granule position
/// of the last packet that ends on each page, and the end-of-stream flag on the last page.
/// </summary>
public static class OggWriter
{
    const int TargetPageBody = 4096;
    static readonly uint[] CrcTable = BuildCrcTable();

    public static void Write(VorbisStream vorbis, Stream output, uint serial = 1)
    {
        var w = new PageWriter(output, serial);
        w.Packet(vorbis.Identification, 0, flush: true, last: false);
        w.Packet(vorbis.Comment, 0, flush: false, last: false);
        bool noAudio = vorbis.AudioPackets.Count == 0;
        w.Packet(vorbis.Setup, 0, flush: true, last: noAudio);
        for (int i = 0; i < vorbis.AudioPackets.Count; i++)
        {
            bool last = i == vorbis.AudioPackets.Count - 1;
            w.Packet(vorbis.AudioPackets[i], vorbis.Granules[i], flush: last, last: last);
        }
    }

    /// <summary>The Ogg CRC-32: polynomial 0x04C11DB7, initial value 0, no reflection, no final xor.</summary>
    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data) crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
        return crc;
    }

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            t[i] = r;
        }
        return t;
    }

    sealed class PageWriter(Stream output, uint serial)
    {
        readonly List<byte> _lacing = new();
        readonly MemoryStream _body = new();
        uint _sequence;
        bool _first = true, _continued, _hasCompleted;
        long _granule;

        public void Packet(byte[] packet, long granule, bool flush, bool last)
        {
            int pos = 0;
            while (true)
            {
                bool done = false;
                while (_lacing.Count < 255)
                {
                    int seg = Math.Min(255, packet.Length - pos);
                    _lacing.Add((byte)seg);
                    _body.Write(packet, pos, seg);
                    pos += seg;
                    if (seg < 255) { done = true; break; }
                }
                if (done) break;
                Flush(nextContinued: true, last: false);                    // page full in the middle of the packet
            }
            _hasCompleted = true;
            _granule = granule;
            if (flush || last || _body.Length >= TargetPageBody || _lacing.Count == 255) Flush(nextContinued: false, last: last);
        }

        void Flush(bool nextContinued, bool last)
        {
            if (_lacing.Count == 0) return;
            int headerSize = 27 + _lacing.Count;
            var page = new byte[headerSize + _body.Length];
            "OggS"u8.CopyTo(page);
            page[4] = 0;
            page[5] = (byte)((_continued ? 1 : 0) | (_first ? 2 : 0) | (last ? 4 : 0));
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), _hasCompleted ? _granule : -1);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _sequence++);
            page[26] = (byte)_lacing.Count;
            _lacing.CopyTo(page, 27);
            _body.Position = 0;
            _body.ReadExactly(page.AsSpan(headerSize));
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
            output.Write(page);

            _lacing.Clear();
            _body.SetLength(0);
            _first = false;
            _continued = nextContinued;
            _hasCompleted = false;
        }
    }
}
