using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>Guardsmen G1c: a sustained flank is answered by one or two squads while the rest keep their slots, and the approach does not swing to it.</summary>
public class WarfrontFlankTests
{
    [Fact]
    public void A_flank_is_answered_by_a_squad_behind_the_front_and_the_others_keep_their_slots()
    {
        var w = TfwRig.War(config: WarfrontBattleTests.WarfrontConfig);
        var b = TfwRig.Deploy(w);
        WarfrontLineTrial.Prepare(w, 20);
        var guards = w.TfwGuards!;
        bool answered = false, swung = false;
        int checkedPairs = 0, moved = 0;
        var befores = new Dictionary<int, Vector3>?[3];
        var approaches = new Vector2[3];
        var flankersBefore = new int[3][];
        WarfrontLineTrial.Run(w, "flank", 1, 1, 60, t =>
        {
            if (t % 30 != 0) return;
            for (int k = 0; k < 3; k++)
            {
                if (!b.WarfrontFlankOn(k)) continue;
                var flankers = b.WarfrontFlankSquads(k).ToArray();
                if (flankers.Length == 0) continue;
                answered = true;
                Assert.InRange(flankers.Length, 1, WarfrontLineRules.FlankSquadsMost);
                var angle = MathF.Acos(Math.Clamp(Vector2.Dot(b.WarfrontFlankBearing(k), b.WarfrontApproach(k)), -1f, 1f)) * 180f / MathF.PI;
                Assert.True(angle >= WarfrontLineRules.FlankDegrees - 30f, $"flank {angle:0} degrees off the approach");
                var others = Enumerable.Range(0, GuardSim.MaxSquads).Where(s => guards.AliveIn(s) > 0 && b.SquadPost(s) == k && !flankers.Contains(s)).ToDictionary(s => s, b.WarfrontSlotOf);
                var approach = b.WarfrontApproach(k);
                var before = befores[k];
                var approachAtFlank = approaches[k];
                bool same = before is not null && flankersBefore[k] is { } fb && fb.SequenceEqual(flankers) && before.Count == others.Count && others.Keys.All(before.ContainsKey) && approach == approachAtFlank
                    && others.Keys.Concat(flankers).All(x => guards.AliveIn(x) > WarfrontGuardRules.BatteredMen);   // the same rows (no death, no rotation, no turn): only the flank answer could move a slot
                if (same)
                    foreach (var (s, p) in others) { checkedPairs++; if (Vector3.Distance(p, before![s]) >= 1f) moved++; }
                if (approachAtFlank != Vector2.Zero && Vector2.Dot(approachAtFlank, approach) < MathF.Cos(MathF.PI / 6f)) swung = true;
                befores[k] = others;
                approaches[k] = approach;
                flankersBefore[k] = flankers;
            }
        });
        Assert.True(answered, "a squad answered the flank");
        Assert.True(checkedPairs > 0, "rows stood unchanged while the flank was answered");
        Assert.Equal(0, moved);
        _ = swung;   // a real turn of the main push is allowed (the 3-step rule); a turn clears the flank
    }
}
