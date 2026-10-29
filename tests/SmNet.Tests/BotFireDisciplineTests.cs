namespace SmNet.Tests;

/// <summary>The test bots' trigger discipline (owner's request 2026-10-28; a test aid, ours): reaction delay, bursts and pauses.</summary>
public class BotFireDisciplineTests
{
    const float Dt = 1f / 30f;

    [Fact]
    public void A_new_target_waits_for_the_reaction_then_bursts_and_pauses()
    {
        var d = new BotFireDiscipline(1);
        var held = Enumerable.Range(0, 300).Select(_ => d.Step(Dt, 5)).ToList();   // 10 s on one target
        int first = held.IndexOf(true);
        Assert.InRange(first * Dt, BotFireDiscipline.ReactionTime * (1 - BotFireDiscipline.Jitter) - Dt, BotFireDiscipline.ReactionTime * (1 + BotFireDiscipline.Jitter) + Dt);
        float duty = held.Skip(first).Count(h => h) / (float)(held.Count - first);
        float expected = BotFireDiscipline.BurstTime / (BotFireDiscipline.BurstTime + BotFireDiscipline.PauseTime);
        Assert.InRange(duty, expected - 0.12f, expected + 0.12f);
        int switches = held.Zip(held.Skip(1)).Count(p => p.First != p.Second);
        Assert.InRange(switches, 6, 20);   // bursts and pauses, not continuous fire
    }

    [Fact]
    public void Another_target_or_a_long_gap_restarts_the_reaction()
    {
        var d = new BotFireDiscipline(2);
        for (int i = 0; i < 60; i++) d.Step(Dt, 5);
        Assert.False(d.Step(Dt, 6));                                   // a new target: hold the trigger again
        for (int i = 0; i < 60; i++) d.Step(Dt, 6);
        for (int i = 0; i < 40; i++) Assert.False(d.Step(Dt, null));   // nothing aimed at: never fire
        Assert.False(d.Step(Dt, 6));                                   // more than ForgetTime later: the same target counts as new
    }

    [Fact]
    public void The_same_seed_gives_the_same_pattern()
    {
        var a = new BotFireDiscipline(9); var b = new BotFireDiscipline(9);
        Assert.Equal(Enumerable.Range(0, 200).Select(_ => a.Step(Dt, 3)), Enumerable.Range(0, 200).Select(_ => b.Step(Dt, 3)));
    }
}
