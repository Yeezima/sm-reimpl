using System.Numerics;

namespace SmFormats.Havok;

/// <summary>
/// hkaSplineCompressedAnimation (hk_2010, 32-bit). Each block stores, per transform track, a 4-byte mask
/// (quantization, position, rotation, scale flags) and then the track data: position, rotation, scale, each either
/// absent (default), static, or a B-spline over the block's frames. Layout per docs/discovery.md ("Havok animation").
/// Only the encodings seen in the game are implemented: 8/16-bit positions and POLAR32 / THREECOMP40 rotations;
/// anything else is rejected with <see cref="NotSupportedException"/>.
/// </summary>
public sealed class HavokSplineAnimation
{
    public float Duration { get; }
    public int TrackCount { get; }
    public int FrameCount { get; }
    public float FrameDuration { get; }
    public int MaxFramesPerBlock { get; }
    public float BlockDuration { get; }
    /// <summary>Per block, per track: decoded curves.</summary>
    readonly TrackCurves[][] _blocks;
    /// <summary>Bytes of block data left unread after the last track, per block (0 when the layout is understood).</summary>
    public IReadOnlyList<int> BlockSlack { get; }

    HavokSplineAnimation(float duration, int tracks, int frames, float frameDuration, int maxFramesPerBlock, float blockDuration, TrackCurves[][] blocks, int[] slack)
    {
        Duration = duration; TrackCount = tracks; FrameCount = frames; FrameDuration = frameDuration;
        MaxFramesPerBlock = maxFramesPerBlock; BlockDuration = blockDuration; _blocks = blocks; BlockSlack = slack;
    }

    public static HavokSplineAnimation Read(HavokPackfile pack, int offset)
    {
        float duration = pack.ReadSingle(offset + 12);
        int tracks = pack.ReadInt32(offset + 16), floatTracks = pack.ReadInt32(offset + 20);
        int frames = pack.ReadInt32(offset + 40), blockCount = pack.ReadInt32(offset + 44), maxFramesPerBlock = pack.ReadInt32(offset + 48);
        int maskSize = pack.ReadInt32(offset + 52);
        float blockDuration = pack.ReadSingle(offset + 56), frameDuration = pack.ReadSingle(offset + 64);
        var (blockOffsets, blockOffsetCount) = pack.Array(offset + 68, 4);
        var (data, dataLength) = pack.Array(offset + 116);
        if (tracks is < 0 or > 4096 || floatTracks != 0 && floatTracks > 4096) throw new InvalidDataException($"Implausible track counts {tracks}/{floatTracks}.");
        if (floatTracks != 0) throw new NotSupportedException("Float tracks are not supported yet.");
        if (blockCount != blockOffsetCount || blockCount < 1) throw new InvalidDataException($"Block count {blockCount} vs {blockOffsetCount} offsets.");
        if (maskSize < 4 * tracks) throw new InvalidDataException($"Mask size {maskSize} smaller than {4 * tracks}.");
        if (pack.ReadInt32(offset + 128) != 0) throw new NotSupportedException("Big-endian animation data is not supported.");
        if (!(duration >= 0f) || !(frameDuration > 0f) || !(blockDuration > 0f) || maxFramesPerBlock < 1 || frames < 0)
            throw new InvalidDataException($"Implausible timing: duration {duration}, frame {frameDuration}, block {blockDuration}, {maxFramesPerBlock} frames per block.");

        var blocks = new TrackCurves[blockCount][];
        var slack = new int[blockCount];
        for (int b = 0; b < blockCount; b++)
        {
            long start = pack.ReadUInt32(blockOffsets!.Value + 4 * b);
            long end = b + 1 < blockCount ? pack.ReadUInt32(blockOffsets.Value + 4 * (b + 1)) : dataLength;
            if (end > dataLength || start + maskSize > end) throw new InvalidDataException($"Block {b} spans [{start}, {end}) of {dataLength} data bytes.");
            var bytes = pack.ReadBytes(data!.Value, dataLength);
            var r = new Cursor(bytes, (int)start + maskSize, (int)end);
            var curves = new TrackCurves[tracks];
            for (int t = 0; t < tracks; t++)
            {
                int m = (int)start + 4 * t;
                byte quant = bytes[m], posFlags = bytes[m + 1], rotFlags = bytes[m + 2], scaleFlags = bytes[m + 3];
                var position = ReadVector(ref r, posFlags, quant & 3, 0f);
                var rotation = ReadRotation(ref r, rotFlags, (quant >> 2) & 15);
                var scale = ReadVector(ref r, scaleFlags, (quant >> 6) & 3, 1f);
                curves[t] = new TrackCurves(position, rotation, scale);
            }
            slack[b] = (int)end - r.Pos;
            if (slack[b] < 0 || slack[b] >= 16) throw new InvalidDataException($"Block {b}: decoding ended {slack[b]} bytes before the block end.");
            blocks[b] = curves;
        }
        return new HavokSplineAnimation(duration, tracks, frames, frameDuration, maxFramesPerBlock, blockDuration, blocks, slack);
    }

    /// <summary>Local (parent-space) transforms of every track at <paramref name="time"/> seconds (clamped).</summary>
    public HavokTransform[] Sample(float time)
    {
        var result = new HavokTransform[TrackCount];
        SampleInto(time, result);
        return result;
    }

    /// <summary>
    /// <see cref="Sample"/> into a buffer of at least <see cref="TrackCount"/> entries (no allocation; the first <see cref="TrackCount"/> are written). The
    /// buffer is the caller's: the animation itself is read-only, so several threads may sample it into their own buffers.
    /// </summary>
    public void SampleInto(float time, HavokTransform[] result)
    {
        if (result.Length < TrackCount) throw new ArgumentException($"the buffer holds {result.Length} transforms; the animation has {TrackCount} tracks", nameof(result));
        time = Math.Clamp(time, 0f, Duration);
        int block = Math.Min((int)(time / BlockDuration), _blocks.Length - 1);
        float frame = (time - block * BlockDuration) / FrameDuration;
        frame = Math.Clamp(frame, 0f, MaxFramesPerBlock - 1);
        for (int t = 0; t < TrackCount; t++)
        {
            var c = _blocks[block][t];
            var q = c.Rotation.Evaluate(frame);
            var rotation = Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W));
            var p = c.Position.Evaluate(frame);
            var s = c.Scale.Evaluate(frame);
            result[t] = new HavokTransform(new Vector3(p.X, p.Y, p.Z), rotation, new Vector3(s.X, s.Y, s.Z));
        }
    }

    /// <summary>The rotation curve's value before normalization (its length shows how far control points disagree).</summary>
    public Vector4 SampleRaw(float time, int track)
    {
        time = Math.Clamp(time, 0f, Duration);
        int block = Math.Min((int)(time / BlockDuration), _blocks.Length - 1);
        float frame = Math.Clamp((time - block * BlockDuration) / FrameDuration, 0f, MaxFramesPerBlock - 1);
        return _blocks[block][track].Rotation.Evaluate(frame);
    }

    // ---- decoding ----

    static Curve ReadVector(ref Cursor r, byte flags, int quantization, float defaultValue)
    {
        var fixedValue = new Vector4(defaultValue, defaultValue, defaultValue, 0);
        if ((flags & 0x70) == 0)
        {
            // static components only (bits 0..2); absent components keep the default
            for (int k = 0; k < 3; k++) if ((flags & (1 << k)) != 0) Set(ref fixedValue, k, r.F32());
            return Curve.Constant(fixedValue);
        }
        int items = r.U16(), degree = r.U8();
        if (degree < 1 || degree > items) throw new InvalidDataException($"Spline degree {degree} with {items + 1} control points.");
        var knots = r.Bytes(items + degree + 2).ToArray();
        r.Align(4);
        var min = new float[3]; var max = new float[3];
        for (int k = 0; k < 3; k++)
        {
            if ((flags & (0x10 << k)) != 0) { min[k] = r.F32(); max[k] = r.F32(); }
            else if ((flags & (1 << k)) != 0) Set(ref fixedValue, k, r.F32());
        }
        if (quantization > 1) throw new NotSupportedException($"Vector quantization {quantization} is not supported.");
        float scale = quantization == 0 ? 1f / 255f : 1f / 65535f;
        var points = new Vector4[items + 1];
        for (int i = 0; i <= items; i++)
        {
            var v = fixedValue;
            for (int k = 0; k < 3; k++)
                if ((flags & (0x10 << k)) != 0)
                {
                    float q = quantization == 0 ? r.U8() : r.U16();
                    Set(ref v, k, min[k] + (max[k] - min[k]) * q * scale);
                }
            points[i] = v;
        }
        r.Align(4);
        return new Curve(degree, knots, points);
    }

    static Curve ReadRotation(ref Cursor r, byte flags, int quantization)
    {
        var (size, align) = quantization switch
        {
            0 => (4, 4),   // POLAR32
            1 => (5, 1),   // THREECOMP40
            _ => throw new NotSupportedException($"Rotation quantization {quantization} is not supported."),
        };
        if ((flags & 0xF0) != 0)
        {
            int items = r.U16(), degree = r.U8();
            if (degree < 1 || degree > items) throw new InvalidDataException($"Spline degree {degree} with {items + 1} control points.");
            var knots = r.Bytes(items + degree + 2).ToArray();
            r.Align(align);
            var points = new Vector4[items + 1];
            for (int i = 0; i <= items; i++) points[i] = DecodeQuaternion(r.Bytes(size), quantization);
            r.Align(4);
            return new Curve(degree, knots, points);
        }
        if ((flags & 0x0F) != 0)
        {
            r.Align(align);
            var q = DecodeQuaternion(r.Bytes(size), quantization);
            r.Align(4);
            return Curve.Constant(q);
        }
        return Curve.Constant(new Vector4(0, 0, 0, 1));
    }

    /// <summary>Returns (x, y, z, w).</summary>
    /// <remarks>Provenance: the POLAR32 and THREECOMP40 bit layouts and constants follow the community decoder in
    /// PredatorCZ/HavokLib (GPL-3.0), reimplemented here from memory of it, not copied; the bit-38 sign was then chosen
    /// on the game's data (docs/discovery.md, "Havok packfiles").</remarks>
    public static Vector4 DecodeQuaternion(ReadOnlySpan<byte> b, int quantization)
    {
        if (quantization == 0)
        {
            // POLAR32: 10-bit radius-like term, 18-bit packed polar angles, 4 sign bits.
            uint v = BitConverter.ToUInt32(b);
            const float rFrac = 1f / 1023f;
            float r = ((v >> 18) & 0x3FF) * rFrac;
            r = 1f - r * r;
            float phiTheta = v & 0x3FFFF;
            float phi = MathF.Floor(MathF.Sqrt(phiTheta));
            float theta = 0f;
            if (phi > 0f)
            {
                theta = MathF.PI / 4f * (phiTheta - phi * phi) / phi;
                phi = MathF.PI / 2f / 511f * phi;
            }
            float magnitude = MathF.Sqrt(MathF.Max(0f, 1f - r * r));
            var q = new Vector4(
                MathF.Sin(phi) * MathF.Cos(theta) * magnitude,
                MathF.Sin(phi) * MathF.Sin(theta) * magnitude,
                MathF.Cos(phi) * magnitude,
                r);
            if ((v & 0x10000000) != 0) q.X = -q.X;
            if ((v & 0x20000000) != 0) q.Y = -q.Y;
            if ((v & 0x40000000) != 0) q.Z = -q.Z;
            if ((v & 0x80000000) != 0) q.W = -q.W;
            return q;
        }
        else
        {
            // THREECOMP40: three 12-bit components, 2-bit index of the dropped (largest) one, 1 sign bit.
            ulong v = b[0] | (ulong)b[1] << 8 | (ulong)b[2] << 16 | (ulong)b[3] << 24 | (ulong)b[4] << 32;
            const float fractal = 0.000345436f;   // sqrt(0.5) / 2047
            float a0 = ((int)(v & 0xFFF) - 2047) * fractal;
            float a1 = ((int)((v >> 12) & 0xFFF) - 2047) * fractal;
            float a2 = ((int)((v >> 24) & 0xFFF) - 2047) * fractal;
            int dropped = (int)((v >> 36) & 3);
            float w = MathF.Sqrt(MathF.Max(0f, 1f - a0 * a0 - a1 * a1 - a2 * a2));
            if (((v >> 38) & 1) != 0) w = -w;
            Span<float> c = stackalloc float[4];
            float[] rest = [a0, a1, a2];
            for (int i = 0, k = 0; i < 4; i++) c[i] = i == dropped ? w : rest[k++];
            return new Vector4(c[0], c[1], c[2], c[3]);
        }
    }

    static void Set(ref Vector4 v, int k, float value) { if (k == 0) v.X = value; else if (k == 1) v.Y = value; else v.Z = value; }

    sealed record TrackCurves(Curve Position, Curve Rotation, Curve Scale);

    /// <summary>Clamped B-spline over byte knots (frame numbers); a constant when it has a single point and no knots.</summary>
    sealed record Curve(int Degree, byte[] Knots, Vector4[] Points)
    {
        public static Curve Constant(Vector4 v) => new(0, [], [v]);

        public Vector4 Evaluate(float u)
        {
            if (Points.Length == 1) return Points[0];
            int n = Points.Length - 1, p = Degree;
            int span = FindSpan(n, p, u);
            Span<float> basis = stackalloc float[p + 1];
            BasisFunctions(span, u, p, basis);
            var result = Vector4.Zero;
            for (int i = 0; i <= p; i++) result += Points[span - p + i] * basis[i];
            return result;
        }

        int FindSpan(int n, int p, float u)
        {
            if (u >= Knots[n + 1]) return n;
            if (u <= Knots[p]) return p;
            int low = p, high = n + 1, mid = (low + high) / 2;
            while (u < Knots[mid] || u >= Knots[mid + 1])
            {
                if (u < Knots[mid]) high = mid; else low = mid;
                mid = (low + high) / 2;
            }
            return mid;
        }

        void BasisFunctions(int span, float u, int p, Span<float> n)
        {
            Span<float> left = stackalloc float[p + 1], right = stackalloc float[p + 1];
            n[0] = 1f;
            for (int j = 1; j <= p; j++)
            {
                left[j] = u - Knots[span + 1 - j];
                right[j] = Knots[span + j] - u;
                float saved = 0f;
                for (int r = 0; r < j; r++)
                {
                    float denominator = right[r + 1] + left[j - r];
                    float temp = denominator == 0f ? 0f : n[r] / denominator;
                    n[r] = saved + right[r + 1] * temp;
                    saved = left[j - r] * temp;
                }
                n[j] = saved;
            }
        }
    }

    ref struct Cursor(ReadOnlySpan<byte> data, int pos, int end)
    {
        readonly ReadOnlySpan<byte> _d = data;
        readonly int _end = end;
        public int Pos = pos;

        void Need(int n) { if (n < 0 || Pos + n > _end) throw new InvalidDataException($"Animation block data truncated at {Pos} (need {n}, block ends {_end})."); }
        public byte U8() { Need(1); return _d[Pos++]; }
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(_d[Pos..]); Pos += 2; return v; }
        public float F32() { Need(4); var v = BitConverter.ToSingle(_d[Pos..]); Pos += 4; return v; }
        public ReadOnlySpan<byte> Bytes(int n) { Need(n); var s = _d.Slice(Pos, n); Pos += n; return s; }
        public void Align(int a) { int pad = (a - Pos % a) % a; Need(pad); Pos += pad; }
    }
}
