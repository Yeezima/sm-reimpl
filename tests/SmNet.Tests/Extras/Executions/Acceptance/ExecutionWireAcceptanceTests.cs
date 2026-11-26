using System.Buffers.Binary;
using System.Numerics;
using SmNet.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;
using static SmNet.Tests.Extras.Executions.Acceptance.ExecutionFixtures;
using WireOffer = SmNet.Extras.Executions.ExecutionOffer;

namespace SmNet.Tests.Extras.Executions.Acceptance;

/// <summary>
/// Executions X8 drop 1, the tag-50 wire (<see cref="ExecutionWire"/>), tested as a contract from the byte layout in its documentation, not from the encoder's own helpers: the 36-byte Start
/// with the distance, the slide ticks, the race-swap flag and the i16x3 slide offset; End; Classic offer; overshield list; decoders that never throw; the clip index.
/// </summary>
public class ExecutionWireAcceptanceTests
{
    static ExecutionStart SampleStart() => new(123456u, 7, ExecutionTargetKind.Player, 9, 18, new Vector3(12.5f, -3.25f, 0.75f), 1.25f, 1.3f, 13, true, new Vector3(0f, -1.7f, 0.02f));

    [Fact]
    public void Start_RoundTrip_WithDistanceSlideRaceSwap()
    {
        var s = SampleStart();
        var bytes = ExecutionWire.Encode(s);
        Assert.Equal(36, ExecutionWire.StartBytes);
        Assert.Equal(36, bytes.Length);   // 26 bytes before X3; 10 more for the distance, slide ticks, flags and the i16x3 slide
        // the layout, field by field (documented offsets)
        Assert.Equal(1, bytes[0]);
        Assert.Equal(123456u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5)));
        Assert.Equal(0, bytes[7]);
        Assert.Equal(9, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(18, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10)));
        Assert.Equal(12.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12)));
        Assert.Equal(-3.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16)));
        Assert.Equal(0.75f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(20)));
        Assert.Equal(1300, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)));   // millimetres
        Assert.Equal(13, bytes[28]);
        Assert.Equal(1, bytes[29]);   // bit 0: races swapped
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(30)));
        Assert.Equal(-1700, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(32)));
        Assert.Equal(20, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)));
        Assert.True(ExecutionWire.TryDecode(bytes, out ExecutionStart d));
        Assert.Equal((s.Tick, s.Executor, s.TargetKind, s.Target, s.Clip, s.Origin, s.SlideTicks, s.RaceSwapped), (d.Tick, d.Executor, d.TargetKind, d.Target, d.Clip, d.Origin, d.SlideTicks, d.RaceSwapped));
        Assert.Equal(1.3f, d.Distance, 3);
        Assert.Equal(s.Yaw, d.Yaw, 1e-3);
        Assert.Equal(-1.7f, d.SlideOffset.Y, 3);
        Assert.Equal(0.02f, d.SlideOffset.Z, 3);
        // a flag-off Start decodes with the swap off
        Assert.True(ExecutionWire.TryDecode(ExecutionWire.Encode(s with { RaceSwapped = false }), out ExecutionStart plain));
        Assert.False(plain.RaceSwapped);
        // the old 26-byte layout and a 37-byte one are refused
        Assert.False(ExecutionWire.TryDecode(bytes.AsSpan(0, 26), out ExecutionStart _));
        Assert.False(ExecutionWire.TryDecode([.. bytes, (byte)0], out ExecutionStart _));
        Assert.False(ExecutionWire.TryDecode(ReadOnlySpan<byte>.Empty, out ExecutionStart _));
    }

    [Fact]
    public void Start_BoundsAreEnforced()
    {
        var good = ExecutionWire.Encode(SampleStart());
        byte[] With(Action<byte[]> edit) { var b = (byte[])good.Clone(); edit(b); return b; }
        bool Ok(byte[] b) => ExecutionWire.TryDecode(b, out ExecutionStart _);
        Assert.True(Ok(good));
        Assert.False(Ok(With(b => b[0] = 2)));                                                          // another kind
        Assert.False(Ok(With(b => b[7] = 2)));                                                          // a target kind past Enemy
        Assert.True(Ok(With(b => b[7] = 1)));
        Assert.False(Ok(With(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 0))));         // distance 0
        Assert.True(Ok(With(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 20000))));      // distance 20 m
        Assert.False(Ok(With(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 20001))));
        Assert.False(Ok(With(b => b[29] = 2)));                                                          // a flag bit above bit 0
        Assert.False(Ok(With(b => b[29] = 3)));
        Assert.True(Ok(With(b => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(30), 4000))));         // slide axis +-4 m
        Assert.True(Ok(With(b => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(30), -4000))));
        Assert.False(Ok(With(b => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(30), 4001))));
        Assert.False(Ok(With(b => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), -4001))));
        Assert.False(Ok(With(b => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), short.MinValue))));
        Assert.False(Ok(With(b => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), float.NaN))));   // a non-finite origin
        Assert.False(Ok(With(b => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), float.PositiveInfinity))));
    }

    [Fact]
    public void Start_OfAQuantizedRun_RoundTripsToTheSameRun()
    {
        var run = ExecutionTrack.Quantize(new ExecutionRun(3, ExecutionTargetKind.Enemy, 41, 22, 1.2345f, new Vector3(1f, 2f, 0f), new Vector3(1f, 3.7f, 0f), 2.718f, 12, 99u, true));
        Assert.True(ExecutionWire.TryDecode(ExecutionWire.Encode(ExecutionStart.Of(run)), out ExecutionStart s));
        Assert.Equal(run, s.ToRun());
    }

    [Fact]
    public void End_Offer_Overshield_RoundTrip()
    {
        foreach (var reason in Enum.GetValues<ExecutionEndReason>())
        {
            var end = new ExecutionEnd(77u, 5, reason);
            var bytes = ExecutionWire.Encode(end);
            Assert.Equal(8, bytes.Length);
            Assert.Equal((byte)2, bytes[0]);
            Assert.True(ExecutionWire.TryDecode(bytes, out ExecutionEnd d));
            Assert.Equal(end, d);
        }
        byte[] badReason = ExecutionWire.Encode(new ExecutionEnd(1, 1, ExecutionEndReason.Done));
        badReason[7] = (byte)ExecutionEndReason.MatchEnd + 1;
        Assert.False(ExecutionWire.TryDecode(badReason, out ExecutionEnd _));

        var offer = new WireOffer(12, 4_000_000u, 24);
        var ob = ExecutionWire.Encode(offer);
        Assert.Equal(8, ob.Length);
        Assert.Equal(3, ob[0]);
        Assert.Equal(24, ob[7]);
        Assert.True(ExecutionWire.TryDecode(ob, out WireOffer od));
        Assert.Equal(offer, od);
        ob[7] = 0;
        Assert.False(ExecutionWire.TryDecode(ob, out WireOffer _));   // a zero-tick window is no offer

        foreach (int n in new[] { 0, 1, 4, ExecutionWire.MaxOvershieldEntries })
        {
            var list = Enumerable.Range(0, n).Select(i => new OvershieldEntry((ushort)(i * 7 + 1), (byte)(i * 15 + 1))).ToArray();
            var lb = ExecutionWire.Encode(list);
            Assert.Equal(2 + 3 * n, lb.Length);
            Assert.True(ExecutionWire.TryDecode(lb, out OvershieldEntry[] ld));
            Assert.Equal(list, ld);
        }
        Assert.Throws<ArgumentException>(() => ExecutionWire.Encode(Enumerable.Range(0, 17).Select(i => new OvershieldEntry((ushort)i, 1)).ToArray()));
        var seventeen = new byte[2 + 3 * 17]; seventeen[0] = 4; seventeen[1] = 17;
        Assert.False(ExecutionWire.TryDecode(seventeen, out OvershieldEntry[] _));       // more entries than the cap
        var zero = ExecutionWire.Encode([new OvershieldEntry(1, 1)]); zero[4] = 0;
        Assert.False(ExecutionWire.TryDecode(zero, out OvershieldEntry[] _));            // a zero fraction means "not in the list"
        var short1 = ExecutionWire.Encode([new OvershieldEntry(1, 1), new OvershieldEntry(2, 2)]);
        Assert.False(ExecutionWire.TryDecode(short1.AsSpan(0, short1.Length - 1), out OvershieldEntry[] _));   // a list that does not fill its payload

        // a fraction is 0 for none and never 0 for a sliver
        Assert.Equal(0, ExecutionWire.Fraction(0f, 100f));
        Assert.Equal(0, ExecutionWire.Fraction(5f, 0f));
        Assert.Equal(1, ExecutionWire.Fraction(0.0001f, 100f));
        Assert.Equal(255, ExecutionWire.Fraction(100f, 100f));
        Assert.Equal(255, ExecutionWire.Fraction(500f, 100f));
        Assert.InRange((int)ExecutionWire.Fraction(50f, 100f), 127, 128);
    }

    [Fact]
    public void KindOf_NamesOnlyTheFourKinds()
    {
        Assert.Equal(0, ExecutionWire.KindOf([]));
        Assert.Equal(0, ExecutionWire.KindOf([0]));
        for (byte k = 1; k <= 4; k++) Assert.Equal(k, ExecutionWire.KindOf([k]));
        for (int k = 5; k < 256; k++) Assert.Equal(0, ExecutionWire.KindOf([(byte)k]));
        Assert.Equal(50, ExecutionWire.Tag);
    }

    [Fact]
    public void Decoders_NeverThrow_Fuzz10000()
    {
        var rng = new Random(0x50_E8_1125);
        int startOk = 0, endOk = 0, offerOk = 0, listOk = 0;
        for (int i = 0; i < 10_000; i++)
        {
            // lengths biased to the real ones (36, 8, 2 + 3n) and kind bytes biased to 1..4, the rest anything
            int length = rng.Next(6) switch { 0 => 36, 1 => 8, 2 => 2 + 3 * rng.Next(0, 20), 3 => rng.Next(0, 5), _ => rng.Next(0, 70) };
            var data = new byte[length];
            rng.NextBytes(data);
            if (length > 0 && rng.Next(4) != 0) data[0] = (byte)rng.Next(1, 5);
            if (rng.Next(3) == 0)
            {
                // a valid Start with a few bytes corrupted, so the fuzz reaches the Start decoder's success path as well as its refusals
                var start = new ExecutionStart((uint)rng.Next(), (ushort)rng.Next(0, 70000 % 65536), (ExecutionTargetKind)rng.Next(2), (ushort)rng.Next(0, 65536), (ushort)rng.Next(0, 65536),
                    new Vector3(rng.NextSingle() * 200f - 100f, rng.NextSingle() * 200f - 100f, rng.NextSingle() * 20f), rng.NextSingle() * 6.28f, 0.001f + rng.NextSingle() * 19f,
                    (byte)rng.Next(0, 256), rng.Next(2) == 0, new Vector3(rng.NextSingle() * 6f - 3f, rng.NextSingle() * 6f - 3f, rng.NextSingle() * 2f - 1f));
                data = ExecutionWire.Encode(start);
                for (int c = rng.Next(0, 3); c > 0; c--) data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
            }
            if (length == 2 + 3 * (length > 1 ? data[1] : 0) && length > 1 && rng.Next(2) == 0) data[0] = 4;
            Exception? thrown = null;
            try
            {
                ExecutionWire.KindOf(data);
                if (ExecutionWire.TryDecode(data, out ExecutionStart s))
                {
                    startOk++;
                    // what decodes re-encodes to a payload that decodes to the same Start (the decoder keeps nothing the encoder cannot say)
                    Assert.True(ExecutionWire.TryDecode(ExecutionWire.Encode(s), out ExecutionStart again));
                    Assert.Equal(s.Tick, again.Tick);
                    Assert.Equal(s.Clip, again.Clip);
                    Assert.Equal(s.RaceSwapped, again.RaceSwapped);
                }
                if (ExecutionWire.TryDecode(data, out ExecutionEnd e)) { endOk++; Assert.Equal(data, ExecutionWire.Encode(e)); }
                if (ExecutionWire.TryDecode(data, out WireOffer o)) { offerOk++; Assert.Equal(data, ExecutionWire.Encode(o)); }
                if (ExecutionWire.TryDecode(data, out OvershieldEntry[] list)) { listOk++; Assert.Equal(data, ExecutionWire.Encode(list)); }
            }
            catch (Xunit.Sdk.XunitException) { throw; }
            catch (Exception ex) { thrown = ex; }
            Assert.True(thrown is null, $"a decoder threw on {Convert.ToHexString(data)}: {thrown}");
        }
        // the fuzz must have reached the success paths too, or it proved nothing
        Assert.True(startOk > 0 && endOk > 0 && offerOk > 0 && listOk > 0, $"decoded Start {startOk}, End {endOk}, Offer {offerOk}, List {listOk}");
    }

    [Fact]
    public void ClipIndex_IsCatalogueIndex()
    {
        // The kept rows are [R1, C1, O1] (their places 0 to 2); in the wire order of ExecutionRows.All they sit far apart. The wire carries the wire order, never the kept place or a variant's.
        var rows = ExecutionRowSet.OfAll([ChaosAltRow(), ChaosRow(), BoyRow()]);
        Assert.Equal([0, 1, 2], new[] { "R1.chainsword", "C1.chainsword", "O1.chainsword" }.Select(rows.IndexOf));
        int[] wire = new[] { "R1.chainsword", "C1.chainsword", "O1.chainsword" }.Select(rows.WireIndexOf).ToArray();
        Assert.Equal(ExecutionRows.All.Select(r => r.Id).ToList().IndexOf("R1.chainsword"), wire[0]);
        Assert.All(wire.Take(2), w => Assert.True(w > 2));
        var w1 = Arena(Config(rows), (Tactical, Vector3.Zero), (Chaos, Near));
        uint kill = KillByMelee(w1);
        Tap(w1, 1, kill + 1);
        var run = Assert.Single(w1.ExecutionsStartedThisTick);
        var row = rows.AtWire(run.Wire)!;
        Assert.Equal(rows.WireIndexOf(row.Id), run.Wire);
        Assert.NotEqual(rows.IndexOf(row.Id), run.Wire);
        // the encoded clip resolves back to the same row on a receiver with the same catalogue ...
        Assert.True(ExecutionWire.TryDecode(ExecutionWire.Encode(ExecutionStart.Of(run)), out ExecutionStart start));
        Assert.Equal(run.Wire, start.Clip);
        Assert.Same(row, rows.AtWire(start.Clip));
        // ... and a variant index of a multi-variant row never leaks onto the wire: the chosen variant of the three-variant row is not its clip number
        Assert.True(row.AttackerVariants.Count >= 2);
        Assert.True(start.Clip > row.AttackerVariants.Count);
        // a receiver whose install lacks the row (a hole) draws nothing instead of a wrong move
        var lacking = ExecutionRowSet.OfAll([BoyRow()]);
        Assert.Null(lacking.AtWire(start.Clip));
        Assert.Null(rows.AtWire(-1));
        Assert.Null(rows.AtWire(ExecutionRows.All.Count + 10));
    }
}
