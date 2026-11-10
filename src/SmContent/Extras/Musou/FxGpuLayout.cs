using System;
using System.Collections.Generic;
using System.Linq;
using SmFormats.Fx;

namespace SmContent.Extras.Musou;

/// <summary>
/// musou/gpu-particles (Musou Mode, added content): the engine-free parts of the client's GPU spray path (<c>client/Extras/Musou/FxGpuSprays.cs</c>).
/// A spray's particles are written once, at the spray's first frame, as spawn records into a pool; a shader computes each particle's place, size,
/// colour and fade from the time. This file holds what can be tested without the engine: the pools' block allocator, how a curve is laid out in the
/// curve table (and the shader's evaluation of it, mirrored), and how a 32-bit seed is carried in two exact floats.
/// </summary>
public static class FxGpuLayout
{
    /// <summary>Records per layer of a record pool's texture array (one texel row per record field).</summary>
    public const int RecordsPerLayer = 512;
    /// <summary>Texels per record: (slot, type, spawn time, slot generation), (offset xyz, 0), (direction xyz, speed), (seed low, seed high, 0, 0).</summary>
    public const int RecordTexels = 4;
    /// <summary>Spray frames (slots) per layer of the slot texture array, and texels per slot: (place, scale), (X axis, generation or -1), (Y axis, opacity), (Z axis, smoke rein), (tint, flags).</summary>
    public const int SlotsPerLayer = 64, SlotTexels = 5;
    /// <summary>Texels per leaf type (a spray and one of its ring children) in the type table, and types per row of it.</summary>
    public const int TypeTexels = 16, TypesPerRow = 64;
    /// <summary>Texels per row of the curve table.</summary>
    public const int CurveRow = 1024;

    /// <summary>
    /// The live-effect cap of the CPU path and of the GPU spray path (600 since the owner's "Drop to 600 blood spray cap" of 2026-11-11, was 1,200: the polish pass P3 measured the 200-death burst frame
    /// at 38.8 ms with 1,200 and 32.4 ms with 400).
    /// </summary>
    public const int CpuMaxEffects = 160, GpuMaxEffects = 600;

    /// <summary>
    /// Whether Musou Mode uses the GPU spray path: not with <c>--fx-cpu</c>, and not in safe mode (the Compatibility renderer or <c>--safe-mode</c>: an old GPU
    /// keeps the CPU path and its 160-effect cap, as a weak machine's CPU cannot walk the GPU path's 600 effects either).
    /// </summary>
    public static bool UseGpu(bool fxCpuFlag, bool safeMode) => !fxCpuFlag && !safeMode;

    /// <summary>The live-effect cap: <c>--fx-cap N</c> when given, else the path's own.</summary>
    public static int EffectCap(bool gpu, int? fxCapArg) => fxCapArg ?? (gpu ? GpuMaxEffects : CpuMaxEffects);

    /// <summary>The two 16-bit halves of a seed, each exact as a float (the shader rebuilds the seed from them).</summary>
    public static (float Low, float High) SplitSeed(uint seed) => (seed & 0xFFFF, seed >> 16);

    /// <summary>The seed back from its halves (what the shader does with <c>uint</c>s).</summary>
    public static uint JoinSeed(float low, float high) => (uint)low | ((uint)high << 16);

    /// <summary>
    /// A scalar curve in the curve table: one texel per key, (time, value, 0, 0); <paramref name="into"/> gets 4 floats per key. Returns the key count.
    /// </summary>
    public static int PackCurve(FxCurve curve, List<float> into)
    {
        foreach (var k in curve.Keys) { into.Add(k.Time); into.Add(k.Value); into.Add(0f); into.Add(0f); }
        return curve.Keys.Count;
    }

    /// <summary>A colour curve in the curve table: two texels per key, (time, 0, 0, 0) and (r, g, b, a) as 0..1. Returns the key count.</summary>
    public static int PackColours(FxColourCurve curve, List<float> into)
    {
        foreach (var k in curve.Keys)
        {
            var v = k.Colour.ToVector();
            into.Add(k.Time); into.Add(0f); into.Add(0f); into.Add(0f);
            into.Add(v.X); into.Add(v.Y); into.Add(v.Z); into.Add(v.W);
        }
        return curve.Keys.Count;
    }

    /// <summary>The shader's <c>curve()</c> on a packed scalar curve (texels of 4 floats from <paramref name="start"/>): <see cref="FxCurve.Evaluate"/>'s rule, with the empty curve's fallback.</summary>
    public static float EvaluatePacked(IReadOnlyList<float> table, int start, int count, float t, float fallback)
    {
        if (count == 0) return fallback;
        float Time(int i) => table[(start + i) * 4];
        float Value(int i) => table[(start + i) * 4 + 1];
        if (!(t > Time(0))) return Value(0);
        for (int i = 1; i < count; i++)
        {
            if (t > Time(i)) continue;
            float span = Time(i) - Time(i - 1);
            return span > 0f ? Value(i - 1) + (Value(i) - Value(i - 1)) * ((t - Time(i - 1)) / span) : Value(i);
        }
        return Value(count - 1);
    }

    /// <summary>The shader's <c>colour_curve()</c> on a packed colour curve: <see cref="FxColourCurve.Evaluate"/>'s rule (opaque white when empty).</summary>
    public static System.Numerics.Vector4 EvaluatePackedColours(IReadOnlyList<float> table, int start, int count, float t)
    {
        if (count == 0) return System.Numerics.Vector4.One;
        float Time(int i) => table[(start + 2 * i) * 4];
        System.Numerics.Vector4 C(int i) { int o = (start + 2 * i + 1) * 4; return new(table[o], table[o + 1], table[o + 2], table[o + 3]); }
        if (!(t > Time(0))) return C(0);
        for (int i = 1; i < count; i++)
        {
            if (t > Time(i)) continue;
            float span = Time(i) - Time(i - 1);
            return span > 0f ? System.Numerics.Vector4.Lerp(C(i - 1), C(i), (t - Time(i - 1)) / span) : C(i);
        }
        return C(count - 1);
    }
}

/// <summary>
/// musou/gpu-particles: an allocator of ranges in a pool of <see cref="Capacity"/> entries (records or spray frames), in constant time: a request is rounded up to a power of
/// two, taken from that size's stack of freed blocks (the most recently freed first) or else from the top (<see cref="HighWater"/>, one past the last entry ever handed out
/// since the pool was last empty: what the GPU draws). A freed block goes back on its size's stack. When nothing is live the pool starts over from 0. At most half the
/// handed-out space is padding; a burst of many effects stays cheap (no search through free ranges).
/// </summary>
public sealed class FxGpuBlocks(int capacity)
{
    readonly List<int>[] _free = Enumerable.Range(0, 32).Select(_ => new List<int>()).ToArray();
    public int Capacity { get; } = capacity;
    public int HighWater { get; private set; }
    /// <summary>Entries in live blocks (each block counted at its rounded size).</summary>
    public int Used { get; private set; }
    public int Live { get; private set; }

    /// <summary>The size class of a request: the power of two that holds it.</summary>
    public static int ClassOf(int length) => length <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(length - 1));

    /// <summary>The start of a free block of at least <paramref name="length"/> entries, or -1 when none is left.</summary>
    public int Allocate(int length)
    {
        if (length <= 0) return -1;
        int c = ClassOf(length), size = 1 << c;
        var stack = _free[c];
        int start;
        if (stack.Count > 0) { start = stack[^1]; stack.RemoveAt(stack.Count - 1); }
        else
        {
            if (size > Capacity - HighWater) return -1;
            start = HighWater;
            HighWater += size;
        }
        Used += size; Live++;
        return start;
    }

    /// <summary>Gives back a block that <see cref="Allocate"/> handed out for <paramref name="length"/> entries.</summary>
    public void Free(int start, int length)
    {
        if (length <= 0) return;
        int c = ClassOf(length);
        Used -= 1 << c; Live--;
        if (Live == 0)
        {
            foreach (var s in _free) s.Clear();
            HighWater = 0;
            return;
        }
        _free[c].Add(start);
    }

    public int FreeBlocks => _free.Sum(s => s.Count);
}
