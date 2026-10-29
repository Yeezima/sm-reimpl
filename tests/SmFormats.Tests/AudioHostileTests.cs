using System.Buffers.Binary;
using SmContent;
using SmFormats.Audio;
using static SmFormats.Audio.SyntheticWwiseWriter;

namespace SmFormats.Tests;

/// <summary>M33 review: hostile or odd input for the new readers (RTPC tails, dialogue trees, buses) and the modifier scopes.</summary>
public class AudioHostileTests
{
    static AudioCatalog Catalog(IReadOnlyList<Obj> objects)
    {
        var media = new[] { new Item(101, [1, 2, 3, 4]) };
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(1, Bank(1, "Hostile", media, objects))], []))), "synthetic");
        return new AudioCatalog(lib, null, null, 5);
    }

    static SoundBank BankOf(params Obj[] objects) => SoundBank.Read(Bank(1, null, [], objects));

    [Fact]
    public void Rtpc_tails_that_are_truncated_unsorted_or_not_finite_leave_the_node_raw()
    {
        var good = Sound(0x10, 1, new Node(0, RtpcCurves: [new RtpcSpec(7, 0, [(0f, 0f, 4), (10f, -6f, 4)])]));
        var unsorted = Sound(0x11, 1, new Node(0, RtpcCurves: [new RtpcSpec(7, 0, [(10f, 0f, 4), (5f, -6f, 4)])]));
        var nan = Sound(0x12, 1, new Node(0, RtpcCurves: [new RtpcSpec(7, 0, [(0f, float.NaN, 4)])]));
        var truncated = new Obj(HircType.Sound, 0x14, good.Payload[..^8]);
        var bank = BankOf(good, unsorted, nan, truncated);
        Assert.IsType<HircSound>(bank.Find(0x10));
        Assert.IsType<HircRaw>(bank.Find(0x11));
        Assert.IsType<HircRaw>(bank.Find(0x12));
        Assert.IsType<HircRaw>(bank.Find(0x14));
        Assert.Equal(3, bank.ParseFailures);
    }

    static Obj RawDialogue(uint id, byte[] bytes) => new(HircType.DialogueEvent, id, bytes);

    [Fact]
    public void Dialogue_trees_with_no_nodes_or_backward_children_resolve_to_nothing_without_throwing()
    {
        // depth 1, one argument, an empty tree.
        var empty = new List<byte>();
        empty.AddRange(BitConverter.GetBytes(1u)); empty.AddRange(BitConverter.GetBytes(0xC1u)); empty.AddRange(BitConverter.GetBytes(0u)); empty.AddRange(BitConverter.GetBytes((ushort)100));
        // A tree whose root points at itself (first child 0, count 1).
        var loop = Dialogue(0xD2, [0xC1], ([5u], 0x501)).Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(4 + 4 + 4 + 2 + 4), 0u | (1u << 16));
        var bank = BankOf(RawDialogue(0xD1, [.. empty]), RawDialogue(0xD2, loop));
        var d1 = Assert.IsType<HircDialogueEvent>(bank.Find(0xD1));
        Assert.Empty(d1.Nodes);
        Assert.Empty(d1.KeysAt(1));
        Assert.Null(d1.Resolve([5u]));
        var d2 = Assert.IsType<HircDialogueEvent>(bank.Find(0xD2));
        Assert.Null(d2.Resolve([5u]));                                          // a child at or before its parent ends the walk
        Assert.NotNull(d2.KeysAt(3).ToList());
    }

    [Fact]
    public void Bus_cycles_short_payloads_and_unknown_buses_do_not_loop_or_throw()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x11, 0, 1), Sound(0x11, 101, new Node(0, Bus: 0xB1)),
            Event(0xE2, 0xA2), Play(0xA2, 0x12, 0, 1), Sound(0x12, 101, new Node(0, Bus: 0xDEAD)),   // a bus no bank has
            Bus(0xB1, 0xB2, -3f), Bus(0xB2, 0xB1, -4f),                                              // a cycle, no master
            new Obj(HircType.Bus, 0xB3, [1, 0, 0, 0, 0, 0]),                                                // too short to read
        };
        using var c = Catalog(objects);
        Assert.Equal(new[] { 0xB1u, 0xB2u }, Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices).Buses.Select(b => b.Id));
        Assert.Empty(c.BusChain(0xB3));
        Assert.Equal(0u, c.MasterBusId());                                                          // every bus has a parent here
        Assert.Empty(Assert.Single(c.Post(0xE2, SoundSwitches.None).Voices).Buses);
    }

    [Fact]
    public void An_unknown_bus_goes_to_the_master()
    {
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1), Play(0xA1, 0x11, 0, 1), Sound(0x11, 101, new Node(0, Bus: 0xDEAD)),
            Bus(0xB1, 0, 0f, busVolume: -7f),
        };
        using var c = Catalog(objects);
        var v = Assert.Single(c.Post(0xE1, SoundSwitches.None).Voices);
        Assert.Equal(-7f, v.BusVolumeDb, 3);
        Assert.Equal(new[] { 0xB1u }, v.Buses.Select(b => b.Id));
    }

    [Fact]
    public void A_reset_clears_the_emitter_and_the_global_entry_of_its_target()
    {
        var m = new SoundModifiers();
        m.Set(SoundProperty.Lpf, 5, 0, 40f, false, 0, 0);          // a global Set
        m.Set(SoundProperty.Lpf, 5, 7, 70f, false, 0, 0);          // and an emitter-scoped one by emitter 7
        // A global Reset posted by emitter 7 clears both.
        m.Apply(new SoundPropertyAction(SoundProperty.Lpf, 5, true, 0, false, 0, 0, false), 7, 1);
        Assert.Equal(0, m.Count);
        // An emitter-scoped Reset clears the global entry too.
        m.Set(SoundProperty.Pitch, 5, 0, 100f, true, 0, 0);
        m.Set(SoundProperty.Pitch, 5, 7, 50f, true, 0, 0);
        m.Apply(new SoundPropertyAction(SoundProperty.Pitch, 5, true, 0, false, 0, 0, true), 7, 1);
        Assert.Equal(0, m.Count);
        // Other emitters' scoped entries stay.
        m.Set(SoundProperty.Pitch, 5, 8, 50f, true, 0, 0);
        m.Apply(new SoundPropertyAction(SoundProperty.Pitch, 5, true, 0, false, 0, 0, false), 7, 1);
        Assert.Equal(1, m.Count);
    }
}
