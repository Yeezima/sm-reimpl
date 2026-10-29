using System.Buffers.Binary;
using System.Text;
using SmSim;

namespace SmNet;

/// <summary>
/// What a demo needs to be played back on the same content: the format and protocol it was recorded with, the configuration and
/// content it ran on, the connection's nonce (the replayed client must accept the recorded Challenge) and the tick rate. The map is not stored: the first\n/// packet, the Challenge, names it (the file keeps a reserved, empty map-name field).
/// </summary>
public sealed record DemoHeader(ushort FormatVersion, ushort ProtocolVersion, ulong ConfigHash, ulong ContentFingerprint, uint Nonce, int TickRate);

/// <summary>A packet the client received, with the client clock reading (seconds) it arrived at.</summary>
public readonly record struct DemoPacket(double Time, byte[] Data);

/// <summary>The view the player had at a time (radians): what the mouse / bot aimed the camera at.</summary>
public readonly record struct DemoView(double Time, float Yaw, float Pitch);

/// <summary>The input the client sampled for a tick, as <c>NetworkView</c> returned it (before <c>Sanitized</c> and the tick / view-tick fill-in).</summary>
public readonly record struct DemoInput(double Time, PlayerInput Input);

/// <summary>The identity the server gave the client, recorded once connected: the player id and the measured handshake round trip.</summary>
public readonly record struct DemoSession(double Time, int PlayerId, int RoundTripMs);

/// <summary>
/// A recorded client session (M40 T0): the packets it received with their arrival times, the inputs it sampled per tick and the view
/// it had per frame. A client replays it with its clocks driven by the host's time (<see cref="DemoPlayback"/>), so footage recorded at
/// any frame rate shows exactly real time. Engine-free.
/// <para>
/// File format (<see cref="Version"/> 1, little endian): the magic <c>SMDEMO\0\0</c>, u16 format version, u16 protocol version,
/// u64 config hash, u64 content fingerprint, u32 nonce, i32 tick rate, u16 reserved map-name length (0) + that many bytes; then records until the end of
/// the file: u8 kind, u32 payload length, payload. Kinds: 1 packet (f64 time, raw bytes), 2 input (f64 time, u32 tick, f32 moveX, f32 moveY,
/// f32 yaw, u32 buttons, f32 pitch, f32 shot yaw offset, f32 shot pitch offset, 3 x f32 shot origin), 3 view (f64 time, f32 yaw, f32 pitch),
/// 4 session (f64 time, i32 player id, i32 round trip ms), 5 end (f64 time). A record that is cut short at the end of the file (a client that
/// was killed) ends the demo there. Times are the recording client's clock in seconds.
/// </para>
/// </summary>
public sealed class Demo
{
    public const ushort Version = 1;
    /// <summary>The largest demo file read (bytes); a larger one is refused before anything is loaded.</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024 - 1;
    /// <summary>The latest record time accepted (seconds on the recording client's clock: about 115 days); a record beyond it is dropped.</summary>
    public const double MaxTimeSeconds = 1e7;
    /// <summary>The largest datagram stored (a record with more is dropped).</summary>
    public const int MaxPacketBytes = 65535;
    /// <summary>
    /// PlayerInput fields a demo stores, in file order, plus ViewTick, MeleeTarget (protocol 32) and ViewFraction (protocol 37), which the client recomputes (<see cref="GameClient"/>
    /// picks the melee lock from its view) and are not stored. A test compares this list with PlayerInput: a new field needs a format bump here.
    /// </summary>
    public static readonly string[] InputFields = ["Tick", "MoveX", "MoveY", "Yaw", "Buttons", "Pitch", "ViewTick", "ShotYawOffset", "ShotPitchOffset", "ShotOriginX", "ShotOriginY", "ShotOriginZ", "MeleeTarget", "ViewFraction"];
    static readonly byte[] Magic = "SMDEMO\0\0"u8.ToArray();

    public enum RecordKind : byte { Packet = 1, Input = 2, View = 3, Session = 4, End = 5 }

    public required DemoHeader Header { get; init; }
    public List<DemoPacket> Packets { get; } = new();
    public List<DemoInput> Inputs { get; } = new();
    public List<DemoView> Views { get; } = new();
    public DemoSession? Session { get; set; }
    /// <summary>The time of the last record (or the End record): when the recording stopped.</summary>
    public double EndTime { get; set; }
    /// <summary>The file ended in the middle of a record (or had no End record): the recording was cut off.</summary>
    public bool Truncated { get; set; }
    /// <summary>Records left out because their time was not a finite number in [0, <see cref="MaxTimeSeconds"/>], went back in time, or their contents were out of range.</summary>
    public int DroppedRecords { get; private set; }

    /// <summary>Arrival time of the first packet (the Challenge): where playback starts.</summary>
    public double StartTime => Packets.Count > 0 ? Packets[0].Time : 0;
    /// <summary>Seconds a playback lasts.</summary>
    public double Duration => Math.Max(0, EndTime - StartTime);

    // ---- writing ----

    /// <summary>Streams a demo to a stream as it happens. Not thread safe.</summary>
    public sealed class Writer : IDisposable
    {
        readonly Stream _out;
        readonly bool _leaveOpen;
        readonly byte[] _scratch = new byte[64];
        double _last;
        bool _closed;

        /// <summary>Set when writing failed (disk full, file gone ...): the writer then ignores everything, so a live client keeps playing.</summary>
        public string? Error { get; private set; }
        /// <summary>Raised once, with the reason, when writing fails.</summary>
        public event Action<string>? Failed;
        public bool IsOpen => !_closed;

        public Writer(Stream output, DemoHeader header, bool leaveOpen = false)
        {
            _out = output; _leaveOpen = leaveOpen;
            _out.Write(Magic);
            var h = new byte[2 + 2 + 8 + 8 + 4 + 4 + 2];
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(2), header.ProtocolVersion);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(4), header.ConfigHash);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(12), header.ContentFingerprint);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), header.Nonce);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), header.TickRate);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(28), 0);   // reserved: the length of a map name that is never written
            _out.Write(h);
        }

        /// <summary>Seconds of the latest record written.</summary>
        public double LastTime => _last;

        public void Packet(double time, ReadOnlySpan<byte> data)
        {
            if (_closed) return;   // nothing is allocated once recording has stopped or failed
            var payload = new byte[8 + data.Length];
            BinaryPrimitives.WriteDoubleLittleEndian(payload, time);
            data.CopyTo(payload.AsSpan(8));
            Record(RecordKind.Packet, time, payload);
        }

        public void Input(double time, PlayerInput i)
        {
            if (_closed) return;
            var p = _scratch.AsSpan(0, 8 + 4 + 4 * 3 + 4 + 4 * 6);
            int o = 0;
            BinaryPrimitives.WriteDoubleLittleEndian(p[o..], time); o += 8;
            BinaryPrimitives.WriteUInt32LittleEndian(p[o..], i.Tick); o += 4;
            F(p, ref o, i.MoveX); F(p, ref o, i.MoveY); F(p, ref o, i.Yaw);
            BinaryPrimitives.WriteUInt32LittleEndian(p[o..], (uint)i.Buttons); o += 4;
            F(p, ref o, i.Pitch); F(p, ref o, i.ShotYawOffset); F(p, ref o, i.ShotPitchOffset);
            F(p, ref o, i.ShotOriginX); F(p, ref o, i.ShotOriginY); F(p, ref o, i.ShotOriginZ);
            Record(RecordKind.Input, time, p[..o]);
        }

        public void View(double time, float yaw, float pitch)
        {
            if (_closed) return;
            var p = _scratch.AsSpan(0, 16);
            BinaryPrimitives.WriteDoubleLittleEndian(p, time);
            BinaryPrimitives.WriteSingleLittleEndian(p[8..], yaw);
            BinaryPrimitives.WriteSingleLittleEndian(p[12..], pitch);
            Record(RecordKind.View, time, p);
        }

        public void Session(double time, int playerId, int roundTripMs)
        {
            if (_closed) return;
            var p = _scratch.AsSpan(0, 16);
            BinaryPrimitives.WriteDoubleLittleEndian(p, time);
            BinaryPrimitives.WriteInt32LittleEndian(p[8..], playerId);
            BinaryPrimitives.WriteInt32LittleEndian(p[12..], roundTripMs);
            Record(RecordKind.Session, time, p);
        }

        static void F(Span<byte> p, ref int o, float v) { BinaryPrimitives.WriteSingleLittleEndian(p[o..], v); o += 4; }

        void Record(RecordKind kind, double time, ReadOnlySpan<byte> payload)
        {
            if (_closed) return;
            Span<byte> head = stackalloc byte[5];
            head[0] = (byte)kind;
            BinaryPrimitives.WriteUInt32LittleEndian(head[1..], (uint)payload.Length);
            try
            {
                _out.Write(head);
                _out.Write(payload);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
            {
                Fail(ex);
                return;
            }
            if (double.IsFinite(time) && time > _last) _last = time;
        }

        void Fail(Exception ex)
        {
            _closed = true;
            Error = $"{ex.GetType().Name}: {ex.Message}";
            Failed?.Invoke(Error);
        }

        /// <summary>Writes the End record at <paramref name="time"/> and flushes. Later writes are ignored. A write error is reported through <see cref="Failed"/>, not thrown.</summary>
        public void Close(double time)
        {
            if (_closed) return;
            var p = new byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(p, Math.Max(time, _last));
            Record(RecordKind.End, time, p);
            if (_closed) return;   // the End record failed
            _closed = true;
            try { _out.Flush(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException) { Fail(ex); }
        }

        public void Flush()
        {
            if (_closed) return;
            try { _out.Flush(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException) { Fail(ex); }
        }

        public void Dispose()
        {
            Close(_last);
            if (!_leaveOpen) { try { _out.Dispose(); } catch (IOException) { } }
        }
    }

    // ---- reading ----

    static float Rf(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadSingleLittleEndian(p[at..]);

    /// <summary>Reads a demo. Throws <see cref="InvalidDataException"/> for a file that is not a demo of this format version or is larger than <see cref="MaxFileBytes"/>.</summary>
    public static Demo Read(Stream input)
    {
        if (input.CanSeek && input.Length > MaxFileBytes) throw new InvalidDataException($"the demo is {input.Length} bytes; the limit is {MaxFileBytes}");
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > MaxFileBytes) throw new InvalidDataException($"the demo is larger than {MaxFileBytes} bytes");
            ms.Write(buffer, 0, n);
        }
        return Read(ms.ToArray());
    }

    /// <summary>A record time is usable when it is a finite number in [0, <see cref="MaxTimeSeconds"/>] that does not go back before the previous record's.</summary>
    static bool TimeOk(double t, ref double last)
    {
        if (!double.IsFinite(t) || t < 0 || t > MaxTimeSeconds || t < last) return false;
        last = t;
        return true;
    }

    public static Demo Read(ReadOnlySpan<byte> d)
    {
        if (d.Length > MaxFileBytes) throw new InvalidDataException($"the demo is {d.Length} bytes; the limit is {MaxFileBytes}");
        if (d.Length < Magic.Length + 30 || !d[..Magic.Length].SequenceEqual(Magic)) throw new InvalidDataException("not a demo file (bad magic)");
        var h = d[Magic.Length..];
        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(h);
        if (format != Version) throw new InvalidDataException($"demo format {format} is not supported (this build reads {Version})");
        ushort protocol = BinaryPrimitives.ReadUInt16LittleEndian(h[2..]);
        ulong config = BinaryPrimitives.ReadUInt64LittleEndian(h[4..]), content = BinaryPrimitives.ReadUInt64LittleEndian(h[12..]);
        uint nonce = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(h[24..]);
        int mapLength = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        if (rate <= 0 || rate > 1000) throw new InvalidDataException("demo has an invalid tick rate");
        int pos = Magic.Length + 30;
        if (pos + mapLength > d.Length) throw new InvalidDataException("demo header is cut short");
        pos += mapLength;   // reserved map name: skipped
        var demo = new Demo { Header = new DemoHeader(format, protocol, config, content, nonce, rate) };
        bool ended = false;
        double last = 0;   // the previous accepted record's time: times never go back
        while (pos < d.Length)
        {
            if (pos + 5 > d.Length) { demo.Truncated = true; break; }
            var kind = (RecordKind)d[pos];
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 1)..]);
            pos += 5;
            if (length > d.Length - pos) { demo.Truncated = true; break; }
            var p = d.Slice(pos, (int)length);
            pos += (int)length;
            switch (kind)
            {
                case RecordKind.Packet when p.Length >= 8:
                {
                    double t = BinaryPrimitives.ReadDoubleLittleEndian(p);
                    if (p.Length - 8 > MaxPacketBytes || !TimeOk(t, ref last)) { demo.DroppedRecords++; break; }
                    demo.Packets.Add(new DemoPacket(t, p[8..].ToArray()));
                    demo.EndTime = Math.Max(demo.EndTime, t);
                    break;
                }
                case RecordKind.Input when p.Length >= 8 + 4 + 12 + 4 + 24:
                {
                    double t = BinaryPrimitives.ReadDoubleLittleEndian(p);
                    if (!TimeOk(t, ref last)) { demo.DroppedRecords++; break; }
                    uint tick = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
                    var input = new PlayerInput(tick, Rf(p, 12), Rf(p, 16), Rf(p, 20), (InputButtons)BinaryPrimitives.ReadUInt32LittleEndian(p[24..]), Rf(p, 28), 0, Rf(p, 32), Rf(p, 36), Rf(p, 40), Rf(p, 44), Rf(p, 48));
                    demo.Inputs.Add(new DemoInput(t, input));
                    demo.EndTime = Math.Max(demo.EndTime, t);
                    break;
                }
                case RecordKind.View when p.Length >= 16:
                {
                    double t = BinaryPrimitives.ReadDoubleLittleEndian(p);
                    float yaw = BinaryPrimitives.ReadSingleLittleEndian(p[8..]), pitch = BinaryPrimitives.ReadSingleLittleEndian(p[12..]);
                    if (!float.IsFinite(yaw) || !float.IsFinite(pitch) || !TimeOk(t, ref last)) { demo.DroppedRecords++; break; }
                    demo.Views.Add(new DemoView(t, yaw, pitch));
                    demo.EndTime = Math.Max(demo.EndTime, t);
                    break;
                }
                case RecordKind.Session when p.Length >= 16:
                {
                    double t = BinaryPrimitives.ReadDoubleLittleEndian(p);
                    int rtt = BinaryPrimitives.ReadInt32LittleEndian(p[12..]);
                    if (rtt is < 0 or > 60_000 || !TimeOk(t, ref last)) { demo.DroppedRecords++; break; }
                    demo.Session ??= new DemoSession(t, BinaryPrimitives.ReadInt32LittleEndian(p[8..]), rtt);
                    demo.EndTime = Math.Max(demo.EndTime, t);
                    break;
                }
                case RecordKind.End when p.Length >= 8:
                {
                    double t = BinaryPrimitives.ReadDoubleLittleEndian(p);
                    if (!TimeOk(t, ref last)) { demo.DroppedRecords++; break; }   // a bad End record leaves the file flagged as cut off
                    demo.EndTime = Math.Max(demo.EndTime, t);
                    ended = true;
                    break;
                }
                // An unknown kind or a short payload is skipped: later minor additions stay readable.
            }
        }
        if (!ended) demo.Truncated = true;
        return demo;
    }
}
