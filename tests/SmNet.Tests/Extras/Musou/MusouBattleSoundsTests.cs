using SmContent;
using SmFormats.Audio;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>musou/battle-ambience (owner, 2026-11-08): the far battle's schedule and shake, the Marines' bark clock and characters, the dialogue tree's leaves (synthetic, no install).</summary>
public class MusouBattleSoundsTests
{
    [Fact]
    public void Every_kind_of_the_far_battle_keeps_coming_within_its_interval_and_range()
    {
        var clock = new MusouAmbienceScheduler(7);
        clock.Reset(0);
        var all = new List<MusouAmbienceSound>();
        for (double t = 0; t <= 600; t += 0.05) all.AddRange(clock.Step(t));
        foreach (var spec in MusouAmbienceRules.Specs)
        {
            var mine = all.Where(s => s.Kind == spec.Kind).ToList();
            var starts = mine.Select(s => s.At - (spec.Kind == MusouAmbienceKind.Artillery ? s.Distance / MusouAmbienceRules.SoundSpeed : 0)).ToList();
            int sounds = mine.Count / Math.Max(1, (spec.ShotsMin + spec.ShotsMax) / 2);
            Assert.InRange(sounds, (int)(600 / spec.EveryMax * 0.6), (int)(600 / spec.EveryMin * 1.6));   // it loops: never stops, never floods
            Assert.All(mine, s => Assert.InRange(s.Distance, spec.Near, spec.Far));
            Assert.All(mine, s => Assert.Contains(s.Event, spec.Events));
            Assert.All(mine, s => Assert.Equal(spec.Kind == MusouAmbienceKind.Artillery, s.Shake > 0f));
        }
    }

    [Fact]
    public void Artillery_booms_arrive_late_by_distance_and_shake_a_tiny_bit()
    {
        var clock = new MusouAmbienceScheduler(3);
        clock.Reset(0);
        var booms = Enumerable.Range(0, 2000).SelectMany(i => clock.Step(i * 0.1)).Where(s => s.Kind == MusouAmbienceKind.Artillery).ToList();
        Assert.NotEmpty(booms);
        Assert.All(booms, b => Assert.InRange(b.Shake, MusouAmbienceRules.ArtilleryShake * 0.4f - 1e-6f, MusouAmbienceRules.ArtilleryShake + 1e-6f));
        Assert.True(MusouAmbienceRules.ArtilleryShake <= 0.01f);   // "a tiny bit": a fifth of the orbital strike's
        Assert.True(MusouAmbienceRules.IsArtillery("play_ork_rokk_distant_explosion") && !MusouAmbienceRules.IsArtillery("play_sm_bolter_fire"));
        Assert.InRange(MusouAmbienceRules.SpecOf(MusouAmbienceKind.Artillery).EveryMin, 5f, 10f);   // "semi often"
    }

    [Fact]
    public void Ork_mobs_are_several_voices_and_bursts_one_gun()
    {
        var clock = new MusouAmbienceScheduler(11);
        clock.Reset(0);
        var all = Enumerable.Range(0, 3000).SelectMany(i => clock.Step(i * 0.1)).ToList();
        var bolter = all.Where(s => s.Kind == MusouAmbienceKind.Bolter).ToList();
        Assert.All(bolter, s => Assert.Equal("play_sm_bolter_fire", s.Event));
        Assert.Contains(all, s => s.Kind == MusouAmbienceKind.OrkMob && s.Event.StartsWith("speech:"));
        Assert.Contains(all, s => s.Kind == MusouAmbienceKind.Lasgun && s.Event == "play_ig_lasgun_fire");
    }

    [Fact]
    public void The_player_is_Titus_and_the_others_Sidonus_or_Leandros()
    {
        Assert.Equal("titus", MusouMarineBarkRules.CharacterOf(true, 5));
        Assert.Equal(new[] { "sidonus", "leandros" }, new[] { MusouMarineBarkRules.CharacterOf(false, 4), MusouMarineBarkRules.CharacterOf(false, 5) });
        Assert.All(MusouMarineBarkRules.LineSets("sidonus"), set => Assert.Equal(Fnv.Hash32("sidonus"), set.Prefix[0]));
        Assert.Equal(0x44ef64c4u, MusouMarineBarkRules.StatusEvent);   // speech_status, the hash in the bank
    }

    [Fact]
    public void Barks_come_now_and_then_faster_in_a_fight_and_never_two_at_once()
    {
        var clock = new MusouBarkClock(1);
        int fight = 0, calm = 0;
        double lastAny = double.NegativeInfinity;
        for (double t = 0; t < 600; t += 0.5)
        {
            foreach (int id in new[] { 1, 2, 3 })
            {
                if (!clock.Due(id, t, fighting: id != 3)) continue;
                Assert.True(t - lastAny >= MusouMarineBarkRules.Spacing);
                lastAny = t;
                if (id == 3) calm++; else fight++;
            }
        }
        Assert.InRange(fight / 2, (int)(600 / MusouMarineBarkRules.CombatEveryMax * 0.6), (int)(600 / MusouMarineBarkRules.CombatEveryMin * 1.2));
        Assert.InRange(calm, 600 / (int)MusouMarineBarkRules.CalmEveryMax / 2, (int)(600 / MusouMarineBarkRules.CalmEveryMin * 1.2));
        Assert.True(fight / 2 > calm);
    }

    [Fact]
    public void A_dialogue_trees_leaves_under_a_path_are_found()
    {
        // root -> character a (keys x, y) and b (key x); level 2 leaves name audio nodes 100, 101, 200.
        var nodes = new List<DialogueNode>
        {
            new(0, Pack(1, 2), 0, 0),            // 0 root: children 1..2
            new(0xA, Pack(3, 2), 0, 0),          // 1 character a: children 3..4
            new(0xB, Pack(5, 1), 0, 0),          // 2 character b: child 5
            new(0x1, 100, 0, 0), new(0x2, 101, 0, 0), new(0x1, 200, 0, 0),
        };
        var d = new HircDialogueEvent(9, ReadOnlyMemory<byte>.Empty, [1u, 2u], nodes);
        Assert.Equal(new uint[] { 100, 101 }, d.LeavesUnder([0xA]));
        Assert.Equal(new uint[] { 101 }, d.LeavesUnder([0xA, 0x2]));
        Assert.Equal(new uint[] { 200 }, d.LeavesUnder([0xB]));
        Assert.Empty(d.LeavesUnder([0xC]));
        Assert.Equal(new uint[] { 100, 101, 200 }, d.LeavesUnder([]));
        static uint Pack(int first, int count) => (uint)(first | (count << 16));
    }
}
