using System.Numerics;
using SmContent.Customization;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Librarian's second pass (Musou Mode only): tag 16 carries all 40 drawn links as 3-byte jumps and four full chains fit one section; a link that breaks the line or jumps too far ends the drawing there;
/// the 5-wave run sends tag 16 too; the sounds' new-target rule and voice budget; the gauge's fills; the armour dressing.
/// </summary>
public class MusouLibrarianPass2Tests
{
    static List<LibrarianLink> Chain(Vector3 hand, IReadOnlyList<Vector3> targets)
    {
        var links = new List<LibrarianLink>();
        var from = hand;
        foreach (var t in targets) { links.Add(new LibrarianLink(from, t)); from = t; }
        return links;
    }

    static LibrarianView View(ushort player, IReadOnlyList<LibrarianLink> links) => new(player, 40f, false, true, 0f, 0f, 1, 2, links);

    [Fact]
    public void Forty_links_round_trip_and_the_rounding_does_not_add_up_along_the_chain()
    {
        var rng = new Random(3);
        var targets = new List<Vector3>();
        var at = new Vector3(120.37f, -48.91f, 3.13f);
        for (int k = 0; k < LibrarianRules.MaxTargets; k++)
        {
            at += new Vector3(rng.NextSingle() * 30f - 15f, rng.NextSingle() * 30f - 15f, rng.NextSingle() * 6f - 3f);   // within the 16 m jump (an axis up to 15 m: past a decimetre byte's 12.7) and the vertical reach
            targets.Add(at);
        }
        var links = Chain(new Vector3(100.04f, -60.06f, 1.77f), targets);
        Assert.Equal(40, LibrarianWire.Drawable(links));
        var bytes = LibrarianWire.Write([View(3, links)]);
        Assert.Equal(1 + LibrarianWire.RecordBytes + LibrarianWire.FirstLinkBytes + 39 * LibrarianWire.JumpBytes, bytes.Length);
        var read = LibrarianWire.Read(bytes)!;
        var got = read[0].Links;
        Assert.Equal(40, got.Count);
        for (int k = 0; k < 40; k++)
        {
            Assert.True(Vector3.Distance(got[k].To, links[k].To) <= (k == 0 ? 0.0867f : 0.1733f), $"link {k}: {links[k].To} -> {got[k].To}");   // half a decimetre per axis for the first link's two points, a jump step's half (0.1 m) after: at the 40th as at the second
            if (k > 0) Assert.Equal(got[k - 1].To, got[k].From);   // the drawn chain is a line
        }
        Assert.True(Vector3.Distance(got[0].From, links[0].From) <= 0.0867f);
    }

    [Fact]
    public void Four_full_chains_fit_one_envelope_section()
    {
        var targets = Enumerable.Range(1, 40).Select(k => new Vector3(0f, 2f * k, 1f)).ToList();
        var views = Enumerable.Range(1, LibrarianWire.MaxLibrarians).Select(p => View((ushort)p, Chain(Vector3.Zero, targets))).ToList();
        var bytes = LibrarianWire.Write(views);
        Assert.Equal(LibrarianWire.MaxPayloadBytes, bytes.Length);
        Assert.Equal(557, bytes.Length);
        Assert.True(bytes.Length <= Protocol.MaxExtrasPayloadBytes, $"{bytes.Length} > {Protocol.MaxExtrasPayloadBytes}");
        Assert.All(LibrarianWire.Read(bytes)!, v => Assert.Equal(40, v.Links.Count));
    }

    [Fact]
    public void A_link_that_breaks_the_line_or_jumps_too_far_ends_the_drawing_there()
    {
        var broken = Chain(Vector3.Zero, [new(0f, 5f, 1f), new(0f, 10f, 1f), new(0f, 15f, 1f)]);
        broken[2] = new LibrarianLink(new Vector3(9f, 9f, 9f), broken[2].To);   // does not start where the last one ended
        Assert.Equal(2, LibrarianWire.Drawable(broken));
        var far = Chain(Vector3.Zero, [new(0f, 5f, 1f), new(0f, 31f, 1f)]);   // 26 m: more than a byte's 25.4 (0.2 m steps since MusouSim 42; 13 m before, past a decimetre byte's 12.7)
        Assert.Equal(1, LibrarianWire.Drawable(far));
        var read = LibrarianWire.Read(LibrarianWire.Write([View(1, far)]))!;
        Assert.Single(read[0].Links);
        Assert.Equal(0, LibrarianWire.Drawable([]));
        // The bolt that crackles out (nothing in the sights): one long link, sent whole.
        Assert.Single(LibrarianWire.Read(LibrarianWire.Write([View(1, [new(Vector3.Zero, new Vector3(0f, 30f, 1f))])]))![0].Links);
    }

    [Fact]
    public void A_payload_with_a_short_chain_or_too_many_links_is_refused()
    {
        var bytes = LibrarianWire.Write([View(1, Chain(Vector3.Zero, [new(0f, 5f, 1f), new(0f, 9f, 1f)]))]);
        Assert.Null(LibrarianWire.Read(bytes[..^1]));
        var tooMany = (byte[])bytes.Clone(); tooMany[1 + 9] = LibrarianRules.MaxDrawnLinks + 1;
        Assert.Null(LibrarianWire.Read(tooMany));
    }

    [Fact]
    public void The_simulated_chain_of_forty_is_drawn_whole()
    {
        var sim = new MusouSim(null) { TickRate = 30 };
        var psyker = new LibrarianPsyker();
        var eye = MusouSim.Chest(Vector3.Zero, sim.Body(MusouEnemyType.Boy).Height);
        for (int k = 0; k < 50; k++) sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 6f + 2f * k, 0f), 0f, 10000f);
        sim.BeginTick(1);
        psyker.Step(sim, new LibrarianCaster(1, Vector3.Zero, eye, 0f, 0f), new LibrarianPress(true, false), 1f / 30f);
        Assert.Equal(40, psyker.Links.Count);
        Assert.Equal(40, LibrarianWire.Drawable(psyker.Links));
    }

    [Fact]
    public void The_five_wave_run_sends_tag_16_for_a_librarian()
    {
        var h = MusouSendTests.MusouHarness(out _);
        // The harness's two tactical classes: the Librarian is appended under Musou, as in a match.
        var prepared = MusouLoadout.Prepare(h.Config);
        int librarian = Enumerable.Range(0, prepared.Classes.Count).First(i => LibrarianRules.IsLibrarian(prepared, i));
        var g = new Harness(LinkConditions.Perfect, 7, 0, prepared, new ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } });
        g.Server.World.MusouDirector!.Hold = true;
        g.Server.World.Musou!.Frozen = true;
        Assert.Null(g.Server.World.Tfw);   // the 5-wave run, not the war
        var client = g.AddClient(nonce: 1, options: new ClientOptions { RequestedClass = (byte)librarian });
        var heard = new List<IReadOnlyList<LibrarianView>>();
        g.Run(TimeSpan.FromSeconds(4), after: () =>
        {
            foreach (var s in client.TakeExtras())
                if (s.Tag == LibrarianWire.Tag && LibrarianWire.Read(s.Payload) is { } views) heard.Add(views);
        });
        Assert.Contains(g.Server.World.Players.Values, p => p.Class == librarian);
        Assert.NotEmpty(heard);
        Assert.Contains(heard, views => views.Count == 1 && views[0].Player == client.PlayerId);
    }

    // ---------------------------------------------------------------- sounds and the gauge

    [Fact]
    public void A_zap_is_for_a_new_target_only_and_the_budget_limits_the_voices()
    {
        var before = Chain(Vector3.Zero, [new(0f, 5f, 1f), new(0f, 10f, 1f)]);
        var now = Chain(Vector3.Zero, [new(0.3f, 5.2f, 1f), new(0f, 10f, 1f), new(0f, 15f, 1f), new(0f, 20f, 1f), new(0f, 25f, 1f), new(0f, 30f, 1f)]);
        Span<int> hits = stackalloc int[LibrarianSounds.ZapsPerSection];
        int n = LibrarianSounds.NewHits(before, now, hits, LibrarianSounds.ZapsPerSection);
        Assert.Equal(3, n);   // the two old targets (one moved 0.36 m) are not new; of the four new ones the first three
        Assert.Equal(new[] { 2, 3, 4 }, hits[..n].ToArray());
        Assert.Equal(1, LibrarianSounds.NewHits([], now[..1], hits, 3));   // a chain that just started: its first hit

        var budget = new LibrarianZapBudget();
        int taken = 0;
        for (int k = 0; k < 10; k++) if (budget.Take(0.0)) taken++;
        Assert.Equal(LibrarianSounds.ZapsPerSection, taken);   // the burst
        taken = 0;
        for (double t = 0.01; t <= 2.0; t += 0.01) if (budget.Take(t)) taken++;
        Assert.InRange(taken, 15, 17);   // 8 a second
    }

    [Fact]
    public void The_sound_events_are_the_installs_names()
    {
        Assert.Equal("play_power_conduit_elec", LibrarianSounds.Loop);
        Assert.Equal("stop_power_conduit_elec", LibrarianSounds.Tail);
        Assert.All(new[] { LibrarianSounds.Zap, LibrarianSounds.Shockwave, LibrarianSounds.Locked }, e => Assert.StartsWith("play_", e));
    }

    [Fact]
    public void The_gauge_fills_as_the_heat_bar_and_blinks_while_locked()
    {
        Assert.Equal(0.5f, LibrarianHudModel.StressFill(50f));
        Assert.Equal(1f, LibrarianHudModel.StressFill(140f));
        Assert.Equal(1f, LibrarianHudModel.CooldownFill(0f, 8f));
        Assert.Equal(0.25f, LibrarianHudModel.CooldownFill(6f, 8f), 0.0001f);
        Assert.Equal(1f, LibrarianHudModel.Blink(false, 1.234));
        Assert.InRange(LibrarianHudModel.Blink(true, 1.234), 0.1f, 1f);
        Assert.Equal("PULL  2.5", LibrarianHudModel.CellText("PULL", 2.5f, false));
        Assert.Equal("PULL  READY", LibrarianHudModel.CellText("PULL", 0f, false));
        Assert.Equal("PULL  LOCKED", LibrarianHudModel.CellText("PULL", 0f, true));
    }

    // ---------------------------------------------------------------- the look

    [Fact]
    public void The_librarian_wears_the_installs_torso_belt_right_pad_and_hood_over_his_own_look()
    {
        var pieces = new List<ResolvedPiece>
        {
            new("head", "sm_mk7aquila_head", ["sm_armour_mp_basic_helmet"], false),
            new("torso", "sm_mk7aquila_torso", ["sm_armour_mp_basic_torso", "sm_armour_mp_basic_belt"], false),
            new("leftshoulder", "sm_mk7aquila_shoulder_left", ["sm_armour_mp_basic_shldrl"], false),
            new("rightshoulder", "sm_mk7aquila_shoulder_right", ["sm_armour_mp_basic_shldrr"], false),
            new("leftleg", "sm_mk7aquila_leg_left", ["sm_armour_mp_basic_legl"], false),
        };
        var look = new ResolvedAppearance("space_marine", "sm - tactical", null, pieces, new Dictionary<string, ResolvedPaint>(), ResolvedDecal.None, ResolvedDecal.None, null,
            new HashSet<string> { "backpack" }, new HashSet<string>(), "helmet", []);
        var dressed = LibrarianLook.Dress(look);
        Assert.True(LibrarianLook.Wears(dressed));
        Assert.Equal(new[] { "head", "torso", "leftshoulder", "rightshoulder", "leftleg", "backpack" }, dressed.Pieces.Select(p => p.Slot).ToArray());
        Assert.Equal(new[] { "sm_armour_mp_librarian_torso", "sm_armour_mp_librarian_belt" }, dressed.Piece("torso")!.Packages.ToArray());
        Assert.Equal("sm_armour_mp_librarian_shldrr", dressed.Piece("rightshoulder")!.Packages[0]);
        Assert.Equal("sm_armour_mp_librarian_backpack", dressed.Piece("backpack")!.Packages[0]);
        Assert.Equal("sm_mk7aquila_head", dressed.Piece("head")!.Name);   // the helmet, left pad and legs stay his
        Assert.DoesNotContain("backpack", dressed.HiddenSlots);
        Assert.False(LibrarianLook.Wears(look));
        Assert.Equal(dressed.Pieces.Select(p => p.Name), LibrarianLook.Dress(dressed).Pieces.Select(p => p.Name));   // dressing twice changes nothing
    }
}
