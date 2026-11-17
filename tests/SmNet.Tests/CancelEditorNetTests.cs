using System.Net;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// cancel-editor (protocol 80): the cancel table's wire messages, the developer-tools gate on them, a live edit reaching the server's play and every client's prediction, the file round trip and the diff.
/// Server and clients each get a config of their own (a copy made with <c>with</c> would share the cancel table's holder and hide a missing sync).
/// </summary>
public class CancelEditorNetTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);
    static int Row(string id) => CancelLayout.RowIndex(id);
    static int Col(string id) => CancelLayout.ColIndex(id);

    static SimConfig Own() => CancelGateTests.Plays(MeleeCancelTests.Pack, null);

    static Harness Make(bool tools = true, int clients = 1)
    {
        var options = new ServerOptions { DevTools = tools ? new DevToolsSetup(IPAddress.Loopback, null) : null };
        var h = new Harness(Link, seed: 7, 0, Own(), options);
        for (int i = 0; i < clients; i++) h.AddClient(Own(), nonce: (uint)(500 + i));
        return h;
    }

    static void Settle(Harness h, double seconds = 1.5) => h.Run(TimeSpan.FromSeconds(seconds));

    static CancelChange Change(string row, string col, CancelCell cell, CancelScope? scope = null) => new(scope ?? CancelScope.Any, Row(row), Col(col), cell);

    // ---- the wire ----

    [Fact]
    public void The_cancel_messages_round_trip_and_refuse_nonsense()
    {
        var entries = new[]
        {
            new CancelWire(CancelWire.AnyClass, CancelWire.AnyWeapon, 3, 4, CancelState.Window, 250, CancelCell.Natural),
            new CancelWire(1, 0, 36, 15, CancelState.Inherit, CancelCell.Natural, CancelCell.Natural),
        };
        var edit = Assert.IsType<DevCancelEdit>(Protocol.Read(Protocol.Write(new DevCancelEdit(42u, DevCancelOp.Apply, entries))));
        Assert.Equal((42u, DevCancelOp.Apply), (edit.Session, edit.Op));
        Assert.Equal(entries, edit.Entries);
        var sync = Assert.IsType<DevCancelSync>(Protocol.Read(Protocol.Write(new DevCancelSync(0xDEADBEEF, 1, 3, entries))));
        Assert.Equal((0xDEADBEEFu, 1, 3), (sync.Hash, sync.Chunk, sync.Chunks));
        Assert.Equal(entries, sync.Entries);
        Assert.IsType<DevCancelSync>(Protocol.Read(Protocol.Write(new DevCancelSync(1u, 0, 0, []))));   // a beacon
        // refused: an unknown op, a row or state out of range, reset-all with entries, a chunk past the count, a beacon with entries
        Assert.Null(Protocol.Read(Protocol.Write(new DevCancelEdit(1u, (DevCancelOp)9, []))));
        Assert.Null(Protocol.Read(Protocol.Write(new DevCancelEdit(1u, DevCancelOp.Apply, [new CancelWire(255, 0xFFFF, 37, 0, CancelState.No, -1, -1)]))));
        Assert.Null(Protocol.Read(Protocol.Write(new DevCancelEdit(1u, DevCancelOp.ResetAll, entries))));
        Assert.Null(Protocol.Read(Protocol.Write(new DevCancelSync(1u, 3, 3, []))));
        Assert.Null(Protocol.Read(Protocol.Write(new DevCancelSync(1u, 0, 0, entries))));
        Assert.Equal(86, Protocol.Version);   // 80 was the unreleased dev-tools protocol; classic-stab-on-t made it 81
    }

    // ---- the gate ----

    [Fact]
    public void A_server_without_the_tools_ignores_edits_and_sends_no_table()
    {
        var h = Make(tools: false);
        Settle(h);
        var client = h.Clients[0];
        Assert.True(client.SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "light", new(CancelState.No))]));
        Settle(h);
        Assert.False(h.Server.CancelTable.DiffersFromBaseline);
        Assert.False(h.Server.World.Config.Cancel.Edited);
        Assert.Equal(0, client.CancelSyncs);
        Assert.False(client.CancelInSync);   // no beacon ever came
    }

    [Fact]
    public void A_server_bound_to_a_non_loopback_address_ignores_edits_even_with_the_switch()
    {
        var options = new ServerOptions { DevTools = new DevToolsSetup(IPAddress.Parse("192.168.1.20"), null) };
        var h = new Harness(Link, seed: 8, 0, Own(), options);
        h.AddClient(Own());
        Settle(h);
        h.Clients[0].SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "light", new(CancelState.No))]);
        Settle(h);
        Assert.False(h.Server.World.Config.Cancel.Edited);
    }

    // ---- live apply and agreement ----

    [Fact]
    public void An_edit_through_the_gate_reaches_the_server_and_every_clients_table_and_reset_all_undoes_it()
    {
        var h = Make(clients: 2);
        Settle(h, 2.0);
        Assert.All(h.Clients, c => Assert.True(c.CancelInSync));   // the shipped table everywhere: the beacons agree
        var ship = CancelData.Shipped.Hash;
        h.Clients[0].SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "light", new(CancelState.Window, 300, 800)), Change("charge", "back", new(CancelState.No), new CancelScope("assault", ""))]);
        Settle(h, 2.0);
        var server = h.Server.CancelTable;
        Assert.True(server.DiffersFromBaseline);
        Assert.Equal(2, server.DiffFromShipped().Count);
        Assert.NotEqual(ship, server.Hash);
        foreach (var c in h.Clients)
        {
            Assert.Equal(server.Hash, c.CancelTable.Hash);            // each client (its own config) plays by the server's table
            Assert.True(c.CancelInSync);
            Assert.Equal(new CancelCell(CancelState.Window, 300, 800), c.CancelTable.Base(Row("jp_hop"), Col("light")));
            Assert.Equal(CancelState.No, c.CancelTable.Resolve(Row("charge"), Col("back"), "assault", null).State);
        }
        h.Clients[1].SendCancelEdit(DevCancelOp.ResetAll, []);
        Settle(h, 2.0);
        Assert.Equal(ship, h.Server.CancelTable.Hash);
        Assert.False(h.Server.World.Config.Cancel.Edited);
        Assert.All(h.Clients, c => Assert.Equal(ship, c.CancelTable.Hash));
    }

    [Fact]
    public void Reset_cell_puts_one_cell_back_and_a_whole_import_of_hundreds_of_cells_arrives()
    {
        var h = Make();
        Settle(h, 2.0);
        var client = h.Clients[0];
        client.SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "light", new(CancelState.No)), Change("jp_hop", "heavy", new(CancelState.No))]);
        Settle(h, 1.5);
        client.SendCancelEdit(DevCancelOp.ResetCell, [Change("jp_hop", "light", new(CancelState.Yes))]);
        Settle(h, 1.5);
        Assert.Equal(CancelData.Shipped.Base(Row("jp_hop"), Col("light")), h.Server.CancelTable.Base(Row("jp_hop"), Col("light")));
        Assert.Equal(CancelState.No, h.Server.CancelTable.Base(Row("jp_hop"), Col("heavy")).State);
        // a file with every cell flipped (a worst-case import: 592 cells in ten datagrams)
        var flipped = CancelData.Shipped;
        var changes = new List<CancelChange>();
        for (int r = 0; r < CancelLayout.RowCount; r++)
            for (int c = 0; c < CancelLayout.ColCount; c++)
            {
                var was = CancelData.Shipped.Base(r, c);
                if (was.State == CancelState.NotApplicable) continue;
                var now = new CancelCell(was.State == CancelState.No ? CancelState.Yes : CancelState.No);
                flipped = flipped.With(CancelScope.Any, r, c, now);
                changes.Add(new CancelChange(CancelScope.Any, r, c, now));
            }
        client.SendCancelEdit(DevCancelOp.ResetAll, []);
        client.SendCancelEdit(DevCancelOp.Apply, changes);
        Settle(h, 3.0);
        Assert.Equal(flipped.Hash, h.Server.CancelTable.Hash);
        Assert.Equal(flipped.Hash, client.CancelTable.Hash);
        Assert.True(client.CancelInSync);
    }

    [Fact]
    public void A_client_whose_table_drifted_is_given_the_servers_again()
    {
        var h = Make();
        Settle(h, 2.0);
        var client = h.Clients[0];
        client.SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "light", new(CancelState.No))]);
        Settle(h, 1.5);
        Assert.True(client.CancelInSync);
        int syncs = client.CancelSyncs;
        // simulate a lost datagram: the client's table is something else now
        h.Clients[0].GetType().GetProperty("CancelTable")!.GetValue(client);   // (read only: the live holder is the config's)
        var field = typeof(GameClient).GetField("_config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((SimConfig)field.GetValue(client)!).Cancel.Set(CancelData.Shipped.With(CancelScope.Any, Row("jp_hop"), Col("light"), new(CancelState.Yes)));
        Assert.NotEqual(h.Server.CancelTable.Hash, client.CancelTable.Hash);
        Settle(h, 2.5);
        Assert.Equal(h.Server.CancelTable.Hash, client.CancelTable.Hash);
        Assert.True(client.CancelSyncs > syncs);
    }

    [Fact]
    public void A_live_edit_changes_what_the_servers_simulation_does_with_the_very_next_press()
    {
        // the same scripted play twice: a jump-pack hop, then a melee press in it. Default: the hop is cancelled (a swing); with the table edited live to "no": the hop plays on.
        (bool Swung, bool Hopped) Play(bool edit)
        {
            var h = Make();
            Settle(h, 1.0);
            var client = h.Clients[0];
            if (edit) { client.SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "light", new(CancelState.No))]); Settle(h, 1.0); }
            Assert.True(client.PlayerId > 0);
            uint jumpAt = 0;
            PlayerInput Input(int who, uint tick)
            {
                if (jumpAt == 0) jumpAt = tick + 5;
                return new(tick, 0f, 0f, 0f, who == 0 && tick == jumpAt ? InputButtons.Jump : who == 0 && tick == jumpAt + 6 ? InputButtons.Melee : InputButtons.None);
            }
            bool swung = false, hopped = false;
            for (int i = 0; i < 1500; i++)
            {
                h.Step(Input);
                if (h.Server.World.Players.TryGetValue(client.PlayerId, out var s)) { swung |= s.MeleeStep > 0; hopped |= Evade.Evading(s); }
            }
            return (swung, hopped);
        }
        var def = Play(false);
        var edited = Play(true);
        Assert.True(def.Hopped && def.Swung, "default: the melee press cancelled the hop and swung");
        Assert.True(edited.Hopped && !edited.Swung, "edited: the hop plays on and no swing starts");
    }

    // ---- the file ----

    [Fact]
    public void A_table_survives_the_file_round_trip_with_its_overrides_and_windows()
    {
        var t = CancelData.Shipped
            .With(CancelScope.Any, Row("reload"), Col("light"), new(CancelState.Yes))
            .With(CancelScope.Any, Row("jp_hop"), Col("light"), new(CancelState.Window, 250, 900))
            .With(new("assault", ""), Row("charge"), Col("back"), new(CancelState.No))
            .With(new("", "rifle"), Row("melee_light"), Col("reload"), new(CancelState.Window, 100, CancelCell.Natural))
            .With(new("assault", "rifle"), Row("evade_roll"), Col("heavy"), new(CancelState.Queued));
        var back = CancelData.Parse(t.ToJson("test"), out string error);
        Assert.NotNull(back);
        Assert.Equal("", error);
        Assert.Equal(t.Hash, back!.Hash);
        Assert.Equal(t.DiffFromShipped().Count, back.DiffFromShipped().Count);
        Assert.Equal(new CancelCell(CancelState.Window, 250, 900), back.Base(Row("jp_hop"), Col("light")));
        Assert.Equal(new CancelCell(CancelState.Window, 100, CancelCell.Natural), back.Resolve(Row("melee_light"), Col("reload"), null, "rifle"));
    }

    [Fact]
    public void Export_writes_a_timestamped_file_and_import_reads_it_back_and_a_bad_file_is_refused()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sm-cancel-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = CancelData.Shipped.With(CancelScope.Any, Row("reload"), Col("light"), new(CancelState.Yes));
            string path = CancelConfigStore.Export(t, folder, new DateTime(2026, 11, 17, 4, 5, 6));
            Assert.Equal("cancel-table-20261117-040506.json", Path.GetFileName(path));
            string second = CancelConfigStore.Export(t, folder, new DateTime(2026, 11, 17, 4, 5, 6));
            Assert.NotEqual(path, second);   // never overwrites
            Assert.Equal(2, CancelConfigStore.List(folder).Count);
            var back = CancelConfigStore.Import(path, out string error);
            Assert.Equal(t.Hash, back!.Hash);
            File.WriteAllText(Path.Combine(folder, "bad.json"), "{ \"format\": 1, \"rows\": [], \"cols\": [], \"cells\": {} }");
            Assert.Null(CancelConfigStore.Import(Path.Combine(folder, "bad.json"), out error));
            Assert.Contains("layout", error);
            File.WriteAllText(Path.Combine(folder, "worse.json"), "not json");
            Assert.Null(CancelConfigStore.Import(Path.Combine(folder, "worse.json"), out error));
            Assert.Null(CancelConfigStore.Import(Path.Combine(folder, "missing.json"), out error));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void The_diff_lists_every_change_in_plain_words()
    {
        var t = CancelData.Shipped
            .With(CancelScope.Any, Row("reload"), Col("light"), new(CancelState.Yes))
            .With(new("assault", ""), Row("jp_hop"), Col("light"), new(CancelState.Window, 300, 800));
        var diff = t.DiffFromShipped();
        Assert.Equal(2, diff.Count);
        var lines = diff.Select(d => t.Describe(d)).ToList();
        Assert.Contains(lines, l => l.Contains("Reload") && l.Contains("no -> yes"));
        Assert.Contains(lines, l => l.Contains("[class assault]") && l.Contains("window 0.3-0.8 s"));
        // a table is no different from itself
        Assert.Empty(t.DiffAgainst(t));
    }

    // ---- review fixes ----

    [Fact]
    public void A_bot_and_a_spectator_cannot_edit_the_table()
    {
        var options = new ServerOptions { DevTools = new DevToolsSetup(IPAddress.Loopback, null) };
        var h = new Harness(Link, seed: 9, 0, Own(), options);
        var bot = h.AddClient(Own(), nonce: 601, options: new ClientOptions { Bot = true });
        var watcher = h.AddClient(Own(), nonce: 602, options: new ClientOptions { Observer = true });
        Settle(h, 2.0);
        bot.SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "light", new(CancelState.No))]);
        watcher.SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "heavy", new(CancelState.No))]);   // an observer's client refuses to send; the server would too
        Settle(h, 1.5);
        Assert.False(h.Server.World.Config.Cancel.Edited);
        var human = h.AddClient(Own(), nonce: 603);
        Settle(h, 2.0);
        human.SendCancelEdit(DevCancelOp.Apply, [Change("jp_hop", "light", new(CancelState.No))]);
        Settle(h, 1.5);
        Assert.True(h.Server.World.Config.Cancel.Edited);   // a player still can
    }

    [Fact]
    public void An_import_burst_still_works_while_the_server_is_paused()
    {
        var manual = new ManualClock();
        var scalable = new ScalableClock(manual);
        var net = new LoopbackNetwork(manual, Link, 5);
        var server = new GameServer(Own(), Harness.Fingerprint, net.Server, scalable, new ServerOptions { DevTools = new DevToolsSetup(IPAddress.Loopback, null, scalable) }, null);
        var client = new GameClient(Own(), Harness.Fingerprint, net.Connect(), manual, null, nonce: 7);
        void Run(int ms) { for (int i = 0; i < ms; i++) { manual.Advance(TimeSpan.FromMilliseconds(1)); server.Update(); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None)); } }
        Run(2500);
        Assert.Equal(ClientStatus.Connected, client.Status);
        client.SendDevCommand(DevCommandKind.SetTimeScale, 4, true);   // paused: the scaled clock stands still
        Run(500);
        uint tick = server.World.Tick;
        var changes = new List<CancelChange>();
        for (int r = 0; r < CancelLayout.RowCount; r++)
            for (int c = 0; c < CancelLayout.ColCount; c++)
                if (CancelData.Shipped.Base(r, c).State != CancelState.NotApplicable) changes.Add(new(CancelScope.Any, r, c, new CancelCell(CancelState.No)));
        client.SendCancelEdit(DevCancelOp.Apply, changes);   // 10 datagrams in a burst
        Run(1500);
        Assert.Equal(tick, server.World.Tick);   // still paused
        Assert.Equal(changes.Count(c => CancelData.Shipped.Base(c.Row, c.Col).State != CancelState.No), server.World.Config.Cancel.Active.DiffFromShipped().Count);
        Assert.True(server.World.Config.Cancel.Edited);
    }

    [Fact]
    public void A_missing_malformed_or_foreign_default_table_falls_back_to_the_baseline_with_a_reason()
    {
        foreach (string bad in new[] { "", "not json", "{ \"format\": 1, \"rows\": [], \"cols\": [], \"cells\": {} }", "{ \"format\": 2 }" })
        {
            var t = CancelData.ResolveShipped(bad, out string error);
            Assert.Equal(CancelData.Baseline.Hash, t.Hash);
            Assert.False(t.DiffersFromBaseline);
            Assert.StartsWith("shipped cancel table unusable", error);
        }
        var good = CancelData.ResolveShipped(CancelData.Shipped.ToJson("x"), out string none);
        Assert.Equal("", none);
        Assert.Equal(CancelData.Shipped.Hash, good.Hash);
        Assert.Null(CancelData.ShippedError);   // the embedded file of this build is fine
    }

    [Fact]
    public void Overrides_naming_a_class_or_weapon_the_build_lacks_are_found_and_dropped()
    {
        var c = Own();
        string cls = c.Classes[0].Name, wpn = c.Weapons[0].Name;
        var t = CancelData.Shipped
            .With(new(cls, ""), Row("jp_hop"), Col("light"), new(CancelState.No))
            .With(new("no such class", ""), Row("jp_hop"), Col("heavy"), new(CancelState.No))
            .With(new("", "no such gun"), Row("reload"), Col("light"), new(CancelState.Yes))
            .With(new("", wpn), Row("reload"), Col("heavy"), new(CancelState.Yes));
        Assert.Equal(2, t.UnknownScopes(c).Count);
        var kept = t.WithoutUnknownScopes(c, out var dropped);
        Assert.Equal(2, dropped.Count);
        Assert.Empty(kept.UnknownScopes(c));
        Assert.Equal(2, kept.OverrideList().Count());
        // the sync of the kept table is what a client builds: the hashes agree
        Assert.Equal(kept.Hash, CancelData.FromShipped(kept.DiffFromShipped()).Hash);
    }

    [Fact]
    public void A_window_with_no_times_is_open_the_whole_action_that_is_it_acts_as_yes()
    {
        SimConfig c;
        c = CancelGateTests.Plays(MeleeCancelTests.Pack, CancelGateTests.Edit("reload", "light", new(CancelState.Window)));
        Assert.True(CancelGate.Active(c));   // reload x light is no by default: a timeless window now opens it
        var s = MeleeCancelTests.Fresh(c, 0) with { Clip = 5 };
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Reload), c, out _);
        Assert.True(s.ReloadLeft > 0f);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), c, out _);
        Assert.True(s.MeleeStep > 0 && s.ReloadLeft <= 0f);
    }
}
