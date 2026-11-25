using System.Numerics;
using SmClient.Extras.Warfront.Chaos.Breach;
using SmClient.Extras.Warfront.Chaos.Finale;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5c: the client's duel model (tag 51 kinds 16 and 17): names, next, last challenger, bar fraction from permille, the handoff countdown, the entry line, outcomes, telegraphs, staleness, the pins.</summary>
public class DuelHudModelTests
{
    const ushort Own = 3, Other = 4, Third = 5;
    static string Name(ushort id) => id switch { Own => "Brother Alpha", Other => "bravo", Third => " charlie ", _ => "" };

    static byte[] State(WarfrontDuelPhase phase, ushort champion, ushort next, int remaining, int attempt, float health, float max = 1000f) =>
        WarfrontChaosDuelWire.Encode(WarfrontChaosDuelWire.Of(phase, champion, next, remaining, attempt, 99, health, max));

    static byte[] Told(uint tick, byte ability, float seconds, float x = 1f) =>
        WarfrontChaosDuelWire.Encode(WarfrontChaosDuelWire.Of(new WarfrontLordTelegraph(tick, ability, new Vector3(x, 2f, 0.5f), seconds)));

    [Fact]
    public void Names_next_and_queue_for_a_spectator()
    {
        var m = new ChaosDuelModel();
        Assert.True(m.Apply(State(WarfrontDuelPhase.Duel, Other, Third, 2, 0, 1000f), 10.0));
        var l = m.Lines(Own, Name, 10.5);
        Assert.True(l.Shown);
        Assert.Equal("CHAMPION: BRAVO", l.Champion);
        Assert.Equal("NEXT: CHARLIE", l.Next);   // trimmed, upper case: one style per level
        Assert.Equal("CHALLENGERS WAITING: 2", l.Queue);
        Assert.Equal("SPECTATING BRAVO", l.Spectating);
        Assert.Equal("", l.Centre);   // the entry line is the duelist's only
        Assert.True(l.Bar);
        Assert.Equal(1f, l.BarFraction);
        Assert.Equal("100%", l.BarPercent);
        Assert.True(m.Watching(Own, 10.5));
        Assert.False(m.Watching(Other, 10.5));
    }

    [Fact]
    public void Last_challenger_and_the_viewer_named_you()
    {
        var m = new ChaosDuelModel();
        m.Apply(State(WarfrontDuelPhase.Duel, Other, 0, 0, 1, 500f), 1.0);
        Assert.Equal(ChaosDuelLook.LastChallenger, m.Lines(Own, Name, 1.0).Next);
        m.Apply(State(WarfrontDuelPhase.Duel, Other, Own, 1, 1, 500f), 1.2);
        Assert.Equal("NEXT: YOU", m.Lines(Own, Name, 1.2).Next);
        // an unknown or blank name falls back to P<id>
        m.Apply(State(WarfrontDuelPhase.Duel, 7, 0, 0, 1, 500f), 1.3);
        Assert.Equal("CHAMPION: P7", m.Lines(Own, _ => "  ", 1.3).Champion);
    }

    [Theory]
    [InlineData(1000, 1f, "100%")]
    [InlineData(400, 0.4f, "40%")]
    [InlineData(1, 0.001f, "1%")]   // a living Lord never shows 0 %
    [InlineData(0, 0f, "0%")]
    [InlineData(999, 0.999f, "100%")]
    [InlineData(621, 0.621f, "62%")]   // 62.0 % of the Lord: the permille rounds up to 621, the percent to the nearest
    [InlineData(4, 0.004f, "1%")]
    public void Bar_fraction_from_permille(int permille, float fraction, string percent)
    {
        Assert.Equal(fraction, ChaosDuelModel.BarFraction((ushort)permille), 5);
        Assert.Equal(percent, ChaosDuelModel.Percent((ushort)permille));
    }

    [Fact]
    public void The_bar_carries_the_wire_health_across_a_handoff_bit_for_bit()
    {
        var m = new ChaosDuelModel();
        float h = 4321.0078f;
        m.Apply(State(WarfrontDuelPhase.Duel, Own, Other, 1, 0, h, 10000f), 0.0);
        m.Apply(State(WarfrontDuelPhase.Handoff, 0, Other, 1, -1, h, 10000f), 1.0);
        m.Apply(State(WarfrontDuelPhase.Duel, Other, 0, 0, 1, h, 10000f), 4.0);
        Assert.Equal(BitConverter.SingleToInt32Bits(h), BitConverter.SingleToInt32Bits(m.State!.Value.Health));
        Assert.Equal(433, m.State.Value.Permille);   // ceil(432.1)
        Assert.Equal(0.433f, m.Lines(Own, Name, 4.0).BarFraction, 5);
    }

    [Fact]
    public void Handoff_counts_down_three_seconds()
    {
        var m = new ChaosDuelModel();
        m.Apply(State(WarfrontDuelPhase.Duel, Own, Other, 1, 0, 600f), 4.5);   // within the 3 s staleness of the handoff's first state
        m.Apply(State(WarfrontDuelPhase.Handoff, 0, Other, 1, -1, 600f), 5.0);
        Assert.Equal(3, m.HandoffCountdown(5.0));
        Assert.Equal(3, m.HandoffCountdown(5.4));
        Assert.Equal(2, m.HandoffCountdown(6.2));
        Assert.Equal(1, m.HandoffCountdown(7.9));
        Assert.Equal(0, m.HandoffCountdown(8.0));
        m.Apply(State(WarfrontDuelPhase.Handoff, 0, Other, 1, -1, 600f), 5.5);   // the 2 Hz repeat does not restart the pause
        Assert.Equal(2, m.HandoffCountdown(6.2));
        var mine = m.Lines(Own, Name, 6.2);
        Assert.Equal("YOU HAVE FALLEN", mine.Champion);
        Assert.Equal("THE NEXT CHALLENGER ENTERS IN 2", mine.Centre);
        var next = m.Lines(Other, Name, 6.2);
        Assert.Equal("BROTHER ALPHA HAS FALLEN", next.Champion);
        Assert.Equal("YOUR DUEL BEGINS IN 2", next.Centre);
        Assert.True(next.CentreWarn);
        Assert.Equal("NEXT: YOU", next.Next);
    }

    [Fact]
    public void The_duelist_sees_the_entry_line_for_three_seconds_each_entry()
    {
        var m = new ChaosDuelModel();
        m.Apply(State(WarfrontDuelPhase.Duel, Own, Other, 1, 0, 1000f), 2.0);
        Assert.Equal(ChaosDuelLook.EntryLine, m.Lines(Own, Name, 2.1).Centre);
        Assert.Equal("", m.Lines(Own, Name, 2.1).Spectating);
        m.Apply(State(WarfrontDuelPhase.Duel, Own, Other, 1, 0, 900f), 2.5);   // a repeat keeps the entry's time
        Assert.Equal(ChaosDuelLook.EntryLine, m.Lines(Own, Name, 4.9).Centre);
        Assert.Equal("", m.Lines(Own, Name, 5.0).Centre);
        Assert.Equal("", m.Lines(Other, Name, 2.1).Centre);
        // the next challenger's own entry
        m.Apply(State(WarfrontDuelPhase.Handoff, 0, Other, 1, -1, 900f), 6.0);
        m.Apply(State(WarfrontDuelPhase.Duel, Other, 0, 0, 1, 900f), 9.0);
        Assert.Equal(ChaosDuelLook.EntryLine, m.Lines(Other, Name, 9.2).Centre);
        Assert.Equal("SPECTATING BRAVO", m.Lines(Own, Name, 9.2).Spectating);
    }

    [Fact]
    public void Outcomes_won_and_lost()
    {
        var m = new ChaosDuelModel();
        m.Apply(State(WarfrontDuelPhase.Duel, Other, 0, 0, 1, 10f), 0.0);
        m.Apply(State(WarfrontDuelPhase.Won, Other, 0, 0, 1, 0f), 1.0);
        var won = m.Lines(Own, Name, 2.0);
        Assert.Equal("BRAVO HAS SLAIN THE CHAOS LORD", won.Centre);
        Assert.False(won.Bar);
        Assert.Equal("YOU HAVE SLAIN THE CHAOS LORD", m.Lines(Other, Name, 2.0).Centre);
        Assert.False(m.Tick(1.0 + ChaosDuelLook.OutcomeSeconds - 0.1));   // the outcome outlives the stale rule
        Assert.True(m.Lines(Own, Name, 1.0 + ChaosDuelLook.OutcomeSeconds - 0.1).Shown);
        Assert.False(m.Lines(Own, Name, 1.0 + ChaosDuelLook.OutcomeSeconds).Shown);
        Assert.True(m.Tick(1.0 + ChaosDuelLook.OutcomeSeconds + 0.1));
        Assert.Null(m.State);

        var lost = new ChaosDuelModel();
        lost.Apply(State(WarfrontDuelPhase.Duel, Own, 0, 0, 0, 300f), 0.0);
        lost.Apply(State(WarfrontDuelPhase.Lost, 0, 0, 0, -1, 300f), 1.0);
        var l = lost.Lines(Own, Name, 1.5);
        Assert.Equal("NO CHALLENGER LEFT: THE CAMPAIGN IS LOST", l.Centre);
        Assert.True(l.CentreWarn);
        Assert.Equal("", l.Champion);
    }

    [Fact]
    public void Stale_state_hides_the_hud_and_a_new_battle_starts_clean()
    {
        var m = new ChaosDuelModel();
        m.Apply(State(WarfrontDuelPhase.Duel, Own, Other, 1, 0, 1000f), 0.0);
        Assert.True(m.Running(2.9));
        Assert.False(m.Lines(Own, Name, 3.1).Shown);
        Assert.False(m.Running(3.1));
        Assert.True(m.Tick(3.1));
        Assert.Null(m.State);
        Assert.False(m.Tick(4.0));
        // a stale old relay does not leak its entry or champion into the next state
        var n = new ChaosDuelModel();
        n.Apply(State(WarfrontDuelPhase.Duel, Own, Other, 1, 0, 1000f), 0.0);
        n.Apply(State(WarfrontDuelPhase.Handoff, 0, Other, 1, -1, 1000f), 10.0);
        Assert.Equal("", n.Lines(Own, Name, 10.1).Champion);   // the old champion was forgotten with the stale state
    }

    [Fact]
    public void Telegraphs_first_copy_only_and_expire()
    {
        var m = new ChaosDuelModel();
        Assert.True(m.Apply(Told(50, WarfrontLordTelegraph.Leap, 1.0f), 0.0));
        Assert.Single(m.NewTelegraphs);
        Assert.True(m.Apply(Told(50, WarfrontLordTelegraph.Leap, 1.0f), 0.1));
        Assert.Empty(m.NewTelegraphs);
        Assert.Equal(1, m.Repeats);
        Assert.True(m.Apply(Told(50, WarfrontLordTelegraph.Bolt, 0f), 0.1));   // same tick, another ability: its own
        Assert.Single(m.NewTelegraphs);
        Assert.Equal(2, m.Telegraphs.Count);
        var leap = m.Telegraphs[0];
        Assert.Equal(0.5f, leap.Progress(0.5), 3);
        Assert.Equal(new Vector3(1f, 2f, 0.5f), leap.Told.Position);
        m.Tick(0.3);
        Assert.Single(m.Telegraphs);   // the bolt's cue lasted its 0.15 s
        m.Tick(1.0);
        Assert.Empty(m.Telegraphs);
        for (uint t = 100; t < 100 + ChaosDuelLook.MaxTelegraphs + 5; t++) m.Apply(Told(t, WarfrontLordTelegraph.Leap, 5f), 2.0);
        Assert.Equal(ChaosDuelLook.MaxTelegraphs, m.Telegraphs.Count);
    }

    [Fact]
    public void Garbage_is_refused_and_never_throws()
    {
        var m = new ChaosDuelModel();
        var rng = new Random(5);
        for (int k = 0; k < 5000; k++)
        {
            var data = new byte[rng.Next(0, 24)];
            rng.NextBytes(data);
            if (data.Length > 0 && k % 2 == 0) data[0] = (byte)rng.Next(16, 18);
            m.Apply(data, k * 0.01);
            m.Lines(Own, Name, k * 0.01);
            m.Tick(k * 0.01);
        }
        Assert.True(m.Dropped > 0);
        Assert.False(new ChaosDuelModel().Apply([], 0.0));
        Assert.False(new ChaosDuelModel().Apply([WarfrontChaosWire.KindBreaches, 0], 0.0));
    }

    [Fact]
    public void Nothing_before_a_state_and_no_hud_for_the_bodyguard()
    {
        var m = new ChaosDuelModel();
        Assert.False(m.Lines(Own, Name, 0.0).Shown);
        Assert.False(m.Running(0.0));
        m.Apply(State(WarfrontDuelPhase.Bodyguard, 0, 0, 0, -1, 1000f), 0.0);
        Assert.False(m.Lines(Own, Name, 0.0).Shown);
        Assert.False(m.Running(0.0));
    }

    [Fact]
    public void Silent_closes_with_the_state_first_show_no_closure()
    {
        // the server sends kind 16 before the list on the snapshot tick (DuelSendOrderTests); the view sets SilentCloses on the first state
        var breaches = new ChaosBreachModel();
        breaches.Apply(WarfrontChaosWire.EncodeBreaches([new WarfrontChaosWire.Breach(1, 0, 0, 0, WarfrontBreachState.Open, 3)]), 0.0, out _);
        var duel = new ChaosDuelModel();
        duel.Apply(State(WarfrontDuelPhase.Duel, Own, 0, 0, 0, 1000f), 1.0);
        if (duel.Running(1.0)) breaches.SilentCloses = true;
        Assert.True(breaches.Apply(WarfrontChaosWire.EncodeBreaches([new WarfrontChaosWire.Breach(1, 0, 0, 0, WarfrontBreachState.Closed, 3)]), 1.0, out var shown));
        Assert.Null(shown);
        Assert.Empty(breaches.NewClosures);
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root");
    }

    [Fact]
    public void SpectateForwardIsPinned()
    {
        string root = RepoRoot();
        var hooks = File.ReadAllLines(Path.Combine(root, "client/Extras/Warfront/NetworkView.WarfrontClientHooks.cs"));
        int at = Array.FindIndex(hooks, l => l.Contains("partial void WarfrontSpectateView(PlayerState me, ref (Vector3 Eye, Vector3 Target)? view)", StringComparison.Ordinal));
        Assert.True(at >= 0);
        Assert.Contains("bool duel = false; WarfrontChaosDuelSpectate(me, ref view, ref duel); if (duel) return;   // WARFRONT-HOOK", hooks[at + 2]);
        Assert.Equal(1, hooks.Count(l => l.Contains("WarfrontChaosDuelSpectate", StringComparison.Ordinal)));
        string duel = File.ReadAllText(Path.Combine(root, "client/Extras/Warfront/Chaos/Finale/NetworkView.WarfrontChaosDuel.cs"));
        Assert.Contains("partial void WarfrontChaosDuelSpectate(PlayerState me, ref (Vector3 Eye, Vector3 Target)? view, ref bool taken);", duel);
        Assert.Contains("partial void WarfrontChaosDuelSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled)", duel);
        Assert.Contains("_wfBreach.SilentCloses = true;", duel);
        // lead 40: the fortress flag from the Battle section, the per-frame call from C3's Chaos frame (after its relabels)
        var battle = File.ReadAllLines(Path.Combine(root, "client/Extras/Warfront/Battle/NetworkView.WarfrontBattle.cs"));
        Assert.Equal(1, battle.Count(l => l.Contains("WarfrontDuelBattleSeen(section.IsFortress, _wfBattleAt);   // WARFRONT-HOOK", StringComparison.Ordinal)));
        var chaos = File.ReadAllLines(Path.Combine(root, "client/Extras/Warfront/Chaos/NetworkView.WarfrontChaos.cs"));
        int frame = Array.FindIndex(chaos, l => l.Contains("WarfrontChaosDuelFrame();", StringComparison.Ordinal));
        int relabel = Array.FindIndex(chaos, l => l.Contains("hud.WarfrontRelabel = chaos ?", StringComparison.Ordinal));
        Assert.True(frame > relabel && relabel >= 0, "the duel frame runs after C3's relabel, so the shielded word wins");
        Assert.Equal(1, chaos.Count(l => l.Contains("WarfrontChaosDuelFrame()", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_shielded_lord_reads_shielded_and_the_other_words_stay_chaos()
    {
        Assert.Equal("CHAOS LORD: SHIELDED", ChaosDuelLook.ShieldedRelabel("ORK WARBOSS"));
        Assert.Equal("CHAOS LORD: SHIELDED", ChaosDuelLook.ShieldedRelabel("WARBOSS"));
        Assert.Equal("CHOSEN", ChaosDuelLook.ShieldedRelabel("BOSS NOB"));
        Assert.Equal("CHAOS PREVAILS", ChaosDuelLook.ShieldedRelabel("THE HORDE PREVAILS"));
    }

    [Fact]
    public void Clear_resets_silent_closes_and_a_closure_of_no_listed_breach_is_ignored()
    {
        var m = new ChaosBreachModel { SilentCloses = true };
        m.Clear();
        Assert.False(m.SilentCloses);
        Assert.True(m.Apply(WarfrontChaosWire.EncodeClosure(new WarfrontChaosWire.Closure(9, 1, 2)), 0.0, out var none));   // no list yet
        Assert.Null(none);
        Assert.Equal(1, m.Ignored);
        m.Apply(WarfrontChaosWire.EncodeBreaches([new WarfrontChaosWire.Breach(1, 0, 0, 0, WarfrontBreachState.Open, 0)]), 1.0, out _);
        Assert.True(m.Apply(WarfrontChaosWire.EncodeClosure(new WarfrontChaosWire.Closure(9, 7, 2)), 1.1, out var unknown));   // an id the list does not show
        Assert.Null(unknown);
        Assert.Equal(2, m.Ignored);
        Assert.True(m.Apply(WarfrontChaosWire.EncodeClosure(new WarfrontChaosWire.Closure(9, 1, 2)), 1.2, out var real));
        Assert.NotNull(real);
    }

    [Fact]
    public void A_lost_finale_is_not_the_hq_falling()
    {
        var finale = new SmClient.Extras.Warfront.Hub.WarfrontHubModel();
        finale.Apply(new SmClient.Extras.Warfront.Hub.WarfrontHubView { Phase = SmSim.Extras.Warfront.Campaign.WarfrontPhase.Lost, Fronts = [4, 4, 4], Faction = SmSim.Extras.Warfront.Faction.WarfrontFaction.Chaos });
        Assert.True(finale.LostAtFinale);
        Assert.Equal("STANDING", finale.HqState);
        Assert.Equal("THE CHAOS LORD STANDS: NO CHALLENGER LEFT", finale.Note);
        var hq = new SmClient.Extras.Warfront.Hub.WarfrontHubModel();
        hq.Apply(new SmClient.Extras.Warfront.Hub.WarfrontHubView { Phase = SmSim.Extras.Warfront.Campaign.WarfrontPhase.Lost, Fronts = [0, 4, 4], Faction = SmSim.Extras.Warfront.Faction.WarfrontFaction.Chaos });
        Assert.False(hq.LostAtFinale);
        Assert.Equal("FALLEN", hq.HqState);
        Assert.Equal("THE IMPERIAL HQ HAS FALLEN", hq.Note);
        Assert.Equal("Finale lost (no challenger left)", SmSim.Extras.Warfront.Results.WarfrontResults.OutcomeText(SmSim.Extras.Warfront.Campaign.WarfrontOutcome.FinaleLost));
        Assert.Equal("Win", SmSim.Extras.Warfront.Results.WarfrontResults.OutcomeText(SmSim.Extras.Warfront.Campaign.WarfrontOutcome.Win));
    }
}
