using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>Expansion S6 on the client: the Waaagh! planner's voice cap and spacing, and the impostors standing in the server's slots.</summary>
public class TfwArmyClientTests
{
    static TfwArmyView Wave(byte id, Vector3 at, TfwArmyOrder order = TfwArmyOrder.Attack, ushort far = 200) => new(id, TfwArmySide.Ork, order, false, far, far, 0, 0, at, 0f, 1f, 17);

    [Fact]
    public void At_most_three_waaagh_voices_sound_at_once_the_nearest_waves_first_each_4_to_8_s_apart()
    {
        var planner = new WaaaghBarkPlanner();
        var armies = Enumerable.Range(1, 6).Select(k => Wave((byte)k, new Vector3(k * 30f, 0f, 0f))).ToList();
        armies.Add(Wave(9, new Vector3(5f, 0f, 0f), TfwArmyOrder.March));   // marching: silent
        var last = new Dictionary<byte, double>();
        var heard = new List<(double T, WaaaghBark B)>();
        int lastVoice = -1;
        for (double t = 0; t < 120; t += 1.0 / 60.0)
        {
            foreach (var b in planner.Step(t, Vector3.Zero, armies))
            {
                Assert.NotEqual(9, b.Army);
                Assert.NotEqual(lastVoice, b.Voice);
                lastVoice = b.Voice;
                if (last.TryGetValue(b.Army, out double before)) Assert.InRange(t - before, WaaaghBarkRules.BurstMin - 1e-6, 30.0);
                last[b.Army] = t;
                heard.Add((t, b));
            }
            Assert.True(planner.Sounding <= WaaaghBarkRules.MaxVoices);
        }
        Assert.All(heard, h => Assert.True(heard.Count(o => o.T > h.T - WaaaghBarkRules.VoiceSeconds && o.T <= h.T) <= WaaaghBarkRules.MaxVoices));
        int near = heard.Count(h => h.B.Army == 1), farthest = heard.Count(h => h.B.Army == 6);
        Assert.True(near >= farthest, $"nearest {near}, farthest {farthest}");
        Assert.InRange(near, 120 / WaaaghBarkRules.BurstMax - 1, 120 / WaaaghBarkRules.BurstMin + 1);
        Assert.True(heard.Select(h => h.B.Voice).Distinct().Count() >= 4);   // the voices vary
    }

    [Fact]
    public void A_wave_out_of_range_never_shouts()
    {
        var planner = new WaaaghBarkPlanner();
        var far = new List<TfwArmyView> { Wave(1, new Vector3(WaaaghBarkRules.Range + 10f, 0f, 0f)) };
        for (double t = 0; t < 30; t += 0.1) Assert.Empty(planner.Step(t, Vector3.Zero, far));
    }

    [Fact]
    public void Impostors_stand_in_the_servers_slots_and_a_promotion_leaves_the_rest_in_place()
    {
        var impostors = new TfwArmyImpostors();
        var at = new Vector3(100f, 50f, 0f);
        var v = new TfwArmyView(3, TfwArmySide.Ork, TfwArmyOrder.Hold, true, 120, 120, 0, 12, at, 0.4f, 1f, 13);
        impostors.Apply([v], 10, 0.0);
        var list = new List<TfwImpostor>();
        impostors.Write(Vector3.Zero, list);
        Assert.Equal(120, list.Count);
        Assert.Equal(TfwArmyLayout.Slot(TfwArmySide.Ork, 13, at, 0.4f, 7), list[7].Position);
        var before = list.ToDictionary(i => i.Key, i => i.Position);
        impostors.Apply([v with { Far = 100, SlotBase = 20 }], 25, 0.5);   // 20 promoted: their impostors go, the others stay put
        impostors.Write(Vector3.Zero, list);
        Assert.Equal(100, list.Count);
        Assert.All(list, i => Assert.Equal(before[i.Key], i.Position));
        Assert.Empty(impostors.Deaths);
        impostors.Apply([v with { Far = 90, SlotBase = 20 }], 40, 1.0);    // a far clash took 10: bodies where the last slots stood
        Assert.Equal(TfwImpostorRules.DeathsPerSection, impostors.Deaths.Count);
        Assert.Equal(TfwArmyImpostors.Key(3, 110), impostors.Deaths[0].Key);
    }

    [Fact]
    public void Impostors_are_capped_nearest_armies_first()
    {
        var impostors = new TfwArmyImpostors();
        impostors.Apply([Wave(1, new Vector3(500f, 0f, 0f), far: 800), Wave(2, new Vector3(50f, 0f, 0f), far: 800)], 1, 0.0);
        var list = new List<TfwImpostor>();
        impostors.Write(Vector3.Zero, list);
        Assert.Equal(TfwImpostorRules.OrksMost, list.Count);
        Assert.Equal(800, list.Count(i => i.Key >> 16 == 2));
    }
}
