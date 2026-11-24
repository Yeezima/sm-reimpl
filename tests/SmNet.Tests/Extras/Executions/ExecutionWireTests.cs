using System.Numerics;
using SmNet.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X1: the tag-50 layouts round trip and refuse malformed payloads; the rules' tick maths; the Execute bit on the input wire and on the server.</summary>
public class ExecutionWireTests
{
    [Fact]
    public void Start_round_trips_at_its_size()
    {
        var s = new ExecutionStart(123456, 7, ExecutionTargetKind.Enemy, 4321, 17, new Vector3(-12.5f, 300.25f, 4.125f), 1.25f);
        var b = ExecutionWire.Encode(s);
        Assert.Equal(ExecutionWire.StartBytes, b.Length);
        Assert.Equal(26, b.Length);
        Assert.Equal(ExecutionWire.StartKind, ExecutionWire.KindOf(b));
        Assert.True(ExecutionWire.TryDecode(b, out ExecutionStart back));
        Assert.Equal(s with { Yaw = 0f }, back with { Yaw = 0f });
        Assert.InRange(MathF.Abs(back.Yaw - s.Yaw), 0f, 2f * MathF.PI / 65536f);
    }

    [Theory]
    [InlineData(-0.5f)]
    [InlineData(7.0f)]
    [InlineData(0f)]
    public void The_yaw_wraps_into_one_turn(float yaw)
    {
        float back = ExecutionWire.YawFromWire(ExecutionWire.YawToWire(yaw));
        float d = MathF.IEEERemainder(back - yaw, 2f * MathF.PI);
        Assert.InRange(MathF.Abs(d), 0f, 1e-3f);
        Assert.InRange(back, 0f, 2f * MathF.PI);
    }

    [Fact]
    public void End_offer_and_overshield_round_trip()
    {
        var e = new ExecutionEnd(99, 3, ExecutionEndReason.Killed);
        var eb = ExecutionWire.Encode(e);
        Assert.Equal(ExecutionWire.EndBytes, eb.Length);
        Assert.True(ExecutionWire.TryDecode(eb, out ExecutionEnd eBack));
        Assert.Equal(e, eBack);

        var o = new ExecutionOffer(12, 4000, 24);
        var ob = ExecutionWire.Encode(o);
        Assert.Equal(ExecutionWire.OfferBytes, ob.Length);
        Assert.True(ExecutionWire.TryDecode(ob, out ExecutionOffer oBack));
        Assert.Equal(o, oBack);

        OvershieldEntry[] list = [new(1, 255), new(2, 1), new(65535, 128)];
        var lb = ExecutionWire.Encode(list);
        Assert.Equal(2 + 3 * list.Length, lb.Length);
        Assert.True(ExecutionWire.TryDecode(lb, out OvershieldEntry[] lBack));
        Assert.Equal(list, lBack);
        Assert.True(ExecutionWire.TryDecode(ExecutionWire.Encode(Array.Empty<OvershieldEntry>()), out OvershieldEntry[] empty));
        Assert.Empty(empty);
    }

    [Fact]
    public void Malformed_payloads_are_refused_without_throwing()
    {
        var start = ExecutionWire.Encode(new ExecutionStart(1, 1, ExecutionTargetKind.Player, 2, 0, Vector3.Zero, 0f));
        Assert.False(ExecutionWire.TryDecode(start.AsSpan(0, 25), out ExecutionStart _));
        var badKind = (byte[])start.Clone(); badKind[7] = 2;
        Assert.False(ExecutionWire.TryDecode(badKind, out ExecutionStart _));
        var nan = (byte[])start.Clone(); BitConverter.GetBytes(float.NaN).CopyTo(nan, 16);
        Assert.False(ExecutionWire.TryDecode(nan, out ExecutionStart _));
        var end = ExecutionWire.Encode(new ExecutionEnd(1, 1, ExecutionEndReason.Done)); end[7] = 9;
        Assert.False(ExecutionWire.TryDecode(end, out ExecutionEnd _));
        var offer = ExecutionWire.Encode(new ExecutionOffer(1, 1, 1)); offer[7] = 0;
        Assert.False(ExecutionWire.TryDecode(offer, out ExecutionOffer _));
        var list = ExecutionWire.Encode([new OvershieldEntry(1, 10)]);
        Assert.False(ExecutionWire.TryDecode(list.AsSpan(0, 4), out OvershieldEntry[] _));
        list[4] = 0;
        Assert.False(ExecutionWire.TryDecode(list, out OvershieldEntry[] _));
        Assert.False(ExecutionWire.TryDecode(new byte[] { 4, 17 }.Concat(new byte[51]).ToArray(), out OvershieldEntry[] _));   // more than the cap
        Assert.Throws<ArgumentException>(() => ExecutionWire.Encode(Enumerable.Range(0, 17).Select(i => new OvershieldEntry((ushort)i, 1)).ToArray()));
        Assert.Equal(0, ExecutionWire.KindOf([]));
        Assert.Equal(0, ExecutionWire.KindOf([9]));
        // a payload of one kind is never read as another
        Assert.False(ExecutionWire.TryDecode(ExecutionWire.Encode(new ExecutionOffer(1, 1, 1)), out ExecutionEnd _));
    }

    [Fact]
    public void The_fraction_never_sends_a_sliver_as_none()
    {
        Assert.Equal(0, ExecutionWire.Fraction(0f, 100f));
        Assert.Equal(1, ExecutionWire.Fraction(0.01f, 100f));
        Assert.Equal(255, ExecutionWire.Fraction(100f, 100f));
        Assert.Equal(255, ExecutionWire.Fraction(500f, 100f));
        Assert.Equal(0, ExecutionWire.Fraction(10f, 0f));
    }

    [Fact]
    public void The_rules_give_the_owners_values()
    {
        var c = new SimConfig();
        Assert.Equal(1, ExecutionRules.Version);
        Assert.Equal(0.8f, c.ExecutionWindowSeconds);
        Assert.Equal(24, ExecutionRules.WindowTicks(c));   // 30 Hz
        Assert.Equal(300, ExecutionRules.CooldownTicks(c));
        Assert.Equal(0.8f, ExecutionRules.DeathPoseHoldSeconds(c));
        Assert.Equal(200f, ExecutionRules.RewardShield(100f));   // 200 % armour, not 300 %
        Assert.Equal(100f, ExecutionRules.OvershieldCap(100f));
        Assert.Equal(40f, ExecutionRules.Overshield(140f, 100f));
        Assert.Equal(0f, ExecutionRules.Overshield(80f, 100f));
        Assert.Equal(100f, ExecutionRules.Overshield(250f, 100f));
        // the window: W ticks after the kill is in, W + 1 is out; a press's view tick never reaches back past the rewind cap
        Assert.True(ExecutionRules.InWindow(c, 1000, 1024));
        Assert.False(ExecutionRules.InWindow(c, 1000, 1025));
        Assert.False(ExecutionRules.InWindow(c, 1000, 999));
        Assert.Equal(1000u - (uint)c.MaxRewindTicks, ExecutionRules.PressTick(c, 1000, 10));
        Assert.Equal(1000u, ExecutionRules.PressTick(c, 1000, 5000));
        Assert.Equal(990u, ExecutionRules.PressTick(c, 1000, 990));
    }

    [Fact]
    public void The_hash_moves_only_with_the_flag()
    {
        var plain = new SimConfig { Extras = ContentExtras.Orks };
        Assert.Equal(plain.Hash(), (plain with { ExecutionWindowSeconds = 1.5f }).Hash());   // not read without the flag
        var on = new SimConfig { Extras = ContentExtras.Orks | ContentExtras.Executions };
        Assert.NotEqual(plain.Hash(), on.Hash());
        Assert.NotEqual(on.Hash(), (on with { ExecutionWindowSeconds = 1.5f }).Hash());   // tuned: hashed
        var musou = new SimConfig { Extras = ContentExtras.Musou };
        Assert.Equal(musou.Hash(), (musou with { ExecutionWindowSeconds = 1.5f }).Hash());
    }

    [Fact]
    public void The_execute_bit_crosses_the_input_wire()
    {
        var input = new PlayerInput(5, 0, 0, 0, InputButtons.Execute | InputButtons.Fire);
        var batch = new InputBatch([input], 1, 0);
        var back = Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(batch)));
        Assert.Equal(InputButtons.Execute | InputButtons.Fire, back.Inputs[0].Buttons);
        Assert.Equal(InputButtons.Execute, Protocol.Quantize(input).Buttons & InputButtons.Execute);
    }

    [Theory]
    [InlineData(ContentExtras.None, false)]
    [InlineData(ContentExtras.Orks | ContentExtras.Gore, false)]
    [InlineData(ContentExtras.Musou, false)]
    [InlineData(ContentExtras.Executions, true)]
    [InlineData(ContentExtras.Orks | ContentExtras.Executions, true)]
    public void The_server_keeps_the_execute_bit_only_with_the_flag(ContentExtras extras, bool kept)
    {
        var h = new Harness(LinkConditions.Perfect, 35, 1, new SimConfig { Extras = extras });
        h.Run(TimeSpan.FromSeconds(1.5));
        h.Run(TimeSpan.FromSeconds(0.5), (_, t) => new PlayerInput(t, 0, 0, 0, InputButtons.Execute | InputButtons.Fire));
        var applied = h.Server.LastAppliedForTests(h.Clients[0].PlayerId);
        Assert.Equal(InputButtons.Fire, applied.Buttons & InputButtons.Fire);
        Assert.Equal(kept ? InputButtons.Execute : InputButtons.None, applied.Buttons & InputButtons.Execute);
    }
}
