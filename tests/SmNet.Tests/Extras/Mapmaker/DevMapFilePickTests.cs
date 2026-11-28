using System.Net;
using SmMapmaker;
using SmNet;
using SmServer;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>
/// devtools-god-custommap (protocol 95; the owner 2026-11-28: "a button in the dev tools with a dropdown that loads custom maps"; added content): the F6 panel's CUSTOM MAP row. The list is the saved
/// <c>.smmap</c> files of the picker's folder by file name; a pick on a supervised loopback <c>--dev-tools</c> server reads and checks the file off the tick, then ends the match and hands the file to the
/// supervisor (the clients are told its name, so they load exactly it); a map that cannot run the server's Seize Ground plays Annihilation and the panel says so; a file that cannot be loaded leaves the
/// match running with the reason in the panel; the faithful profile, Musou Mode and an unsupervised server refuse. The supervisor's arguments and pick file carry the file and the mode. Synthetic
/// maps only (the Mapmaker's own layouts), in a private folder.
/// </summary>
public sealed class DevMapFilePickTests : IDisposable
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);
    static readonly SimConfig Extended = new() { Extras = ContentExtras.Gore };   // any profile with added content (gore is presentation only)
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"sm-devpick-{Guid.NewGuid():N}");

    public DevMapFilePickTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A Mapmaker layout saved as <paramref name="file"/> in the test folder ("arena": three capture points; "blank": none, Annihilation only).</summary>
    string Save(string file, string layout, string name)
    {
        var s = MapSession.New(name, new MapLibrary(Path.Combine(_dir, "lib")));
        var r = s.Apply(MapTemplate.Find(layout)!.Ops());
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
        string path = Path.Combine(_dir, file);
        File.WriteAllText(path, s.Document.ToJson());
        return path;
    }

    static DevToolsSetup Supervised(string? current = null, GameMode? home = null) =>
        new(IPAddress.Loopback, null, null, ["mp_tdm_09"], Supervised: true, HomeMode: home, CurrentMapFile: current);

    /// <summary>The server's map for a saved file, as a <c>--custom-map</c> start builds it.</summary>
    static GameMap GameFor(string file)
    {
        var compiled = CustomMaps.Load(file);
        return new GameMap(compiled.Name, compiled.ToCollisionWorld(), compiled.Content.SpawnPoints(), compiled.Hash, compiled.Content.SimControlPoints(), compiled.Content.SimStartAreas(), compiled.Content.SimBlockers());
    }

    sealed class AddressedTransport(IServerTransport inner, IPEndPoint remote) : IServerTransport
    {
        public void Send(int connection, byte[] packet) => inner.Send(connection, packet);
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
        public IPEndPoint? EndPointOf(int connection) => remote;
    }

    (Harness H, List<string> Log) Make(DevToolsSetup? setup, SimConfig? config = null, MatchRules? match = null, GameMap? map = null, bool rules = true, IPEndPoint? remote = null)
    {
        var clientOptions = map is null ? null : new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) };
        var h = new Harness(Link, seed: 5, 1, config ?? Extended, new ServerOptions { DevTools = setup, Match = rules ? match ?? new MatchRules { TimeLimit = 0f, ScoreLimit = 1000, CountdownTime = 0f } : null }, map: map, clientOptions: clientOptions,
            wrapServer: remote is null ? null : t => new AddressedTransport(t, remote));
        h.Server.DevPickDirForTests = _dir;
        var log = new List<string>();
        h.Server.Log += log.Add;
        h.Run(TimeSpan.FromSeconds(2));
        return (h, log);
    }

    /// <summary>Picks list entry <paramref name="index"/> (the check of its file name, or <paramref name="check"/>) and lets the server finish the off-tick check.</summary>
    static void Pick(Harness h, int index, string fileName, int? check = null)
    {
        h.Clients[0].SendDevCommand(DevCommandKind.PickMapFile, (byte)index, true, check ?? CustomMaps.PickCheck(fileName));
        h.Run(TimeSpan.FromSeconds(0.1));
        h.Server.WaitForDevFilePick();
        h.Run(TimeSpan.FromSeconds(0.3));
    }

    [Fact]
    public void The_list_is_the_folders_map_files_by_file_name_with_each_maps_own_name()
    {
        Save("b-second.smmap", "blank", "Second Map");
        Save("A-first.smmap", "arena", "First Arena");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a map");
        var list = CustomMaps.PickList(_dir);
        Assert.Equal(["A-first.smmap", "b-second.smmap"], list.Select(e => e.FileName));
        Assert.Equal("First Arena  (A-first.smmap)", list[0].Label);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "A-first.smmap")), list[0].File);
        Assert.Equal(CustomMaps.PickCheck("a-FIRST.smmap"), CustomMaps.PickCheck("A-first.smmap"));   // case does not matter (Windows file names)
        Assert.NotEqual(CustomMaps.PickCheck("A-first.smmap"), CustomMaps.PickCheck("b-second.smmap"));
        Assert.Empty(CustomMaps.PickList(Path.Combine(_dir, "absent")));
        // a name from the server is only ever a plain .smmap file name in this folder
        Assert.Equal(list[1].File, CustomMaps.ResolvePick("b-second.smmap", _dir));
        Assert.Null(CustomMaps.ResolvePick("..\\b-second.smmap", _dir));
        Assert.Null(CustomMaps.ResolvePick(list[1].File, _dir));
        Assert.Null(CustomMaps.ResolvePick("notes.txt", _dir));
        Assert.Null(CustomMaps.ResolvePick("gone.smmap", _dir));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "x.smmap"), "{}");
        File.WriteAllText(Path.Combine(_dir, ".smmap"), "{}");
        string longName = new string('n', 115) + ".smmap";   // 121 characters
        File.WriteAllText(Path.Combine(_dir, longName), "{}");
        Assert.Null(CustomMaps.ResolvePick("sub/x.smmap", _dir));
        Assert.Null(CustomMaps.ResolvePick("sub\\x.smmap", _dir));
        Assert.Null(CustomMaps.ResolvePick("x.smmap:s", _dir));
        Assert.Null(CustomMaps.ResolvePick(".smmap", _dir));
        Assert.Null(CustomMaps.ResolvePick(longName, _dir));
        Assert.Null(CustomMaps.ResolvePick("", _dir));
    }

    [Fact]
    public void The_client_loads_the_picked_file_then_its_own_unless_it_holds_another_map_then_finds_it_by_name()
    {
        string a1 = Save("my-map.smmap", "arena", "My Map");
        string a2 = Save("my-map-2.smmap", "blank", "My Map");   // the same map name (cm_my_map), other content
        string b = Save("b.smmap", "arena", "Other");
        Assert.Equal(CustomMaps.WireNameOfFile(a1), CustomMaps.WireNameOfFile(a2));
        // the tools picked a2: it wins over the --custom-map file with the same map name
        Assert.Equal(a2, CustomMaps.PlayFile("cm_my_map", a2, a1, out var source, out bool spent, out var held));
        Assert.Equal((CustomMaps.PlaySource.Picked, false, (string?)null), (source, spent, held));
        // no pick: the --custom-map file holds the map
        Assert.Equal(a1, CustomMaps.PlayFile("cm_my_map", null, a1, out source, out spent, out held));
        Assert.Equal(CustomMaps.PlaySource.Given, source);
        // the server moved on to another map: the pick for cm_my_map is spent and the --custom-map file holds another map, so the client finds cm_other by its name
        Assert.Null(CustomMaps.PlayFile("cm_other", a2, a1, out source, out spent, out held));
        Assert.Equal((CustomMaps.PlaySource.ByName, true, "cm_my_map"), (source, spent, held));
        // a picked file that was deleted meanwhile is spent too
        string gone = Path.Combine(_dir, "gone.smmap");
        Assert.Equal(b, CustomMaps.PlayFile("cm_other", gone, b, out source, out spent, out _));
        Assert.Equal((CustomMaps.PlaySource.Given, true), (source, spent));
        // a missing --custom-map file is still handed on (LoadForPlay refuses it with its own message; never a lookup by name)
        Assert.Equal(gone, CustomMaps.PlayFile("cm_other", null, gone, out source, out _, out _));
        Assert.Equal(CustomMaps.PlaySource.Given, source);
        Assert.Null(CustomMaps.PlayFile("cm_other", null, null, out source, out _, out _));
        Assert.Equal(CustomMaps.PlaySource.ByName, source);
    }

    [Fact]
    public void A_pick_ends_the_match_hands_the_file_to_the_supervisor_and_names_it_to_the_clients()
    {
        string a = Save("a.smmap", "arena", "Arena A");
        string b = Save("b.smmap", "arena", "Arena B");
        var (h, log) = Make(Supervised(current: a));
        var me = h.Clients[0];
        Assert.True(me.Dev!.CanPickFiles, "the panel shows the CUSTOM MAP row");
        Pick(h, 1, "b.smmap");
        Assert.Equal(Path.GetFullPath(b), h.Server.PickedMap);
        Assert.Null(h.Server.PickedModeOverride);
        Assert.True(h.Server.RotationDue);
        Assert.True(me.Match.NextMap, "the clients rejoin as after a rotation");
        Assert.Equal("b.smmap", me.Dev!.NextMapFile);
        Assert.Equal(DevNote.Applied, me.Dev.Note);
        Assert.Contains(log, l => l.StartsWith("dev tools: map file pick b.smmap (cm_arena_b):"));

    }

    [Fact]
    public void The_map_being_played_restarts_when_unchanged_and_reloads_when_it_was_edited()
    {
        string a = Save("a.smmap", "arena", "Arena A");
        var (h, log) = Make(Supervised(current: a), map: GameFor(a));
        Pick(h, 0, "a.smmap");
        Assert.Null(h.Server.PickedMap);
        Assert.False(h.Server.RotationDue);
        Assert.Equal(DevNote.Applied, h.Clients[0].Dev!.Note);
        Assert.Contains(log, l => l.Contains("is the map being played, unchanged: restart match"));

        // edited and saved meanwhile (the same file, another layout): the pick reloads it through the supervisor
        var (h2, _) = Make(Supervised(current: a), map: GameFor(a));
        var s = MapSession.New("Arena A", new MapLibrary(Path.Combine(_dir, "lib")));
        Assert.True(s.Apply(MapTemplate.Find("open-field")!.Ops()).Ok);
        File.WriteAllText(a, s.Document.ToJson());
        Pick(h2, 0, "a.smmap");
        Assert.Equal(Path.GetFullPath(a), h2.Server.PickedMap);
        Assert.True(h2.Server.RotationDue);
        Assert.Equal("a.smmap", h2.Clients[0].Dev!.NextMapFile);
    }

    [Fact]
    public void A_pick_from_a_bot_or_a_remote_address_does_nothing()
    {
        Save("a.smmap", "arena", "Arena A");
        var (h, _) = Make(Supervised());
        var bot = h.AddClient(nonce: 77, options: new ClientOptions { Bot = true });
        h.Run(TimeSpan.FromSeconds(1));
        bot.SendDevCommand(DevCommandKind.PickMapFile, 0, true, CustomMaps.PickCheck("a.smmap"));
        h.Run(TimeSpan.FromSeconds(0.2));
        h.Server.WaitForDevFilePick();
        h.Run(TimeSpan.FromSeconds(0.2));
        Assert.Null(h.Server.PickedMap);
        Assert.Equal(DevNote.None, h.Clients[0].Dev!.Note);

        var (remote, log) = Make(Supervised(), remote: new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000));
        Pick(remote, 0, "a.smmap");
        Assert.Null(remote.Server.PickedMap);
        Assert.Equal(MatchState.Warmup, remote.Server.World.State);
        Assert.Contains(log, l => l.Contains("not a loopback address"));
    }

    [Fact]
    public void While_a_check_runs_a_second_pick_and_a_Classic_pick_wait_and_a_check_finishing_after_the_match_ended_still_switches()
    {
        Save("a.smmap", "arena", "Arena A");
        Save("b.smmap", "arena", "Arena B");
        var (h, _) = Make(Supervised());
        using var hold = new ManualResetEventSlim(false);
        h.Server.DevFilePickHoldForTests = hold;
        var me = h.Clients[0];
        me.SendDevCommand(DevCommandKind.PickMapFile, 0, true, CustomMaps.PickCheck("a.smmap"));
        h.Run(TimeSpan.FromSeconds(0.3));
        Assert.Equal(DevNote.MapLoading, me.Dev!.Note);
        me.SendDevCommand(DevCommandKind.PickMapFile, 1, true, CustomMaps.PickCheck("b.smmap"));
        h.Run(TimeSpan.FromSeconds(0.3));
        Assert.Equal((DevNote.MapLoading, "A map file is still being checked; wait a moment."), (me.Dev!.Note, me.Dev.Text));
        me.SendDevCommand(DevCommandKind.PickMap, 0, true);
        h.Run(TimeSpan.FromSeconds(0.3));
        Assert.Equal((DevNote.MapLoading, "A map file is still being checked; wait a moment."), (me.Dev!.Note, me.Dev.Text));
        Assert.Null(h.Server.PickedMap);
        Assert.True(h.Server.World.DevEndMatch() || h.Server.World.DevPickEnd(900), "the match ends while the check runs");
        Assert.Equal(MatchState.Ended, h.Server.World.State);
        hold.Set();
        h.Server.WaitForDevFilePick();
        h.Run(TimeSpan.FromSeconds(0.3));
        Assert.EndsWith("a.smmap", h.Server.PickedMap);   // the first pick, onto the end screen already showing
        Assert.True(h.Server.RotationDue);
    }

    [Fact]
    public void A_refusal_names_the_file_never_a_folder_of_this_PC()
    {
        string path = Path.Combine(_dir, "deep", "x.smmap");
        string why = GameServer.RefusalReason($"cannot play '{path}': Cannot read the map file '{path}': locked by '{Path.Combine(_dir, "deep")}'", path, "x.smmap");
        Assert.DoesNotContain(_dir, why, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Cannot read the map file 'x.smmap': locked by 'the maps folder'.", why);
        Assert.Equal("it could not be compiled.", GameServer.RefusalReason(null, null, "x.smmap"));
    }

    [Fact]
    public void A_map_without_capture_points_plays_Annihilation_on_a_Seize_Ground_server_and_says_so()
    {
        string arena = Save("arena.smmap", "arena", "Seize Arena");
        Save("open.smmap", "blank", "No Points");
        var compiled = CustomMaps.Load(arena);
        var game = new GameMap(compiled.Name, compiled.ToCollisionWorld(), compiled.Content.SpawnPoints(), compiled.Hash, compiled.Content.SimControlPoints(), compiled.Content.SimStartAreas(), compiled.Content.SimBlockers());
        var seize = new MatchRules { Mode = GameMode.SeizeGround, TimeLimit = 0f, ScoreLimit = 1000, CountdownTime = 0f };
        var (h, log) = Make(Supervised(current: arena), match: seize, map: game);
        Assert.Equal(GameMode.SeizeGround, h.Server.World.Rules!.Mode);
        Pick(h, 1, "open.smmap");
        Assert.EndsWith("open.smmap", h.Server.PickedMap);
        Assert.Equal("annihilation", h.Server.PickedModeOverride);
        var dev = h.Clients[0].Dev!;
        Assert.Equal(DevNote.MapModeFallback, dev.Note);
        Assert.Equal("Starting open.smmap in Annihilation: it has no capture points for Seize Ground.", dev.Text);
        Assert.Contains(log, l => l.Contains("in Annihilation (it has no capture points for Seize Ground)"));
    }

    [Fact]
    public void A_fallen_back_match_returns_to_the_servers_mode_on_a_map_that_offers_it()
    {
        Save("arena.smmap", "arena", "Seize Arena");
        Save("open.smmap", "blank", "No Points");
        // a child that plays Annihilation because its map had no points; the server was started in Seize Ground
        var (h, _) = Make(Supervised(home: GameMode.SeizeGround));
        Pick(h, 0, "arena.smmap");
        Assert.EndsWith("arena.smmap", h.Server.PickedMap);
        Assert.Null(h.Server.PickedModeOverride);   // Seize Ground again (the supervisor's own --mode)
        var (h2, _) = Make(Supervised(home: GameMode.SeizeGround));
        Pick(h2, 1, "open.smmap");
        Assert.Equal("annihilation", h2.Server.PickedModeOverride);
    }

    [Fact]
    public void A_file_that_cannot_be_loaded_leaves_the_match_running_and_says_why()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.smmap"), "{ \"format\": \"smmap\", \"name\": \"Broken\", garbage garbage");
        var (h, log) = Make(Supervised());
        var state = h.Server.World.State;
        Pick(h, 0, "broken.smmap");
        Assert.Null(h.Server.PickedMap);
        Assert.False(h.Server.RotationDue);
        Assert.Equal(state, h.Server.World.State);
        var dev = h.Clients[0].Dev!;
        Assert.Equal(DevNote.MapRefused, dev.Note);
        Assert.StartsWith("The match goes on here. broken.smmap cannot be played: ", dev.Text);   // the fixed part first: a long reason is cut at its end
        Assert.DoesNotContain(_dir, dev.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log, l => l.StartsWith("dev tools: map file pick broken.smmap refused:"));

        // a folder that changed since the panel listed it (another file at that index) is refused, nothing else is played
        Save("zz.smmap", "arena", "Late");
        Pick(h, 1, "something-else.smmap");
        Assert.Null(h.Server.PickedMap);
        Assert.Equal(DevNote.MapRefused, h.Clients[0].Dev!.Note);
        Assert.Contains("maps folder changed", h.Clients[0].Dev!.Text);
        Pick(h, 9, "zz.smmap");
        Assert.Null(h.Server.PickedMap);
    }

    [Fact]
    public void The_faithful_profile_Musou_Mode_and_an_unsupervised_server_refuse_and_hide_the_row()
    {
        Save("a.smmap", "arena", "Arena A");
        var (faithful, log) = Make(Supervised(), config: new SimConfig());
        Assert.False(faithful.Clients[0].Dev!.CanPickFiles);
        Pick(faithful, 0, "a.smmap");
        Assert.Null(faithful.Server.PickedMap);
        Assert.Equal(DevNote.NotInThisMode, faithful.Clients[0].Dev!.Note);
        Assert.Contains(log, l => l.Contains("has no custom maps"));

        var (alone, _) = Make(new DevToolsSetup(IPAddress.Loopback, null));
        Assert.False(alone.Clients[0].Dev!.CanPickFiles);
        Pick(alone, 0, "a.smmap");
        Assert.Null(alone.Server.PickedMap);
        Assert.Equal(DevNote.NoMapChange, alone.Clients[0].Dev!.Note);

        var (musou, _) = Make(Supervised(), config: new SimConfig { Extras = ContentExtras.Musou }, rules: false);
        Assert.False(musou.Clients[0].Dev!.CanPickFiles);
        Pick(musou, 0, "a.smmap");
        Assert.Null(musou.Server.PickedMap);
        Assert.Equal(DevNote.NotInThisMode, musou.Clients[0].Dev!.Note);
    }

    [Fact]
    public void The_supervisor_starts_the_picked_file_with_custom_map_and_its_mode_for_that_match_only()
    {
        string b = Save("b.smmap", "blank", "B");
        var args = new[] { "--bind", "127.0.0.1", "--dev-tools", "--custom-map", "a.smmap", "--mode", "seize" };
        Assert.Equal(["--bind", "127.0.0.1", "--dev-tools", "--mode", "seize", "--custom-map", b, "--dev-supervised"], RotationSupervisor.DevChildArguments(args, b));
        Assert.Equal(["--bind", "127.0.0.1", "--dev-tools", "--custom-map", b, "--mode", "annihilation", "--dev-supervised"], RotationSupervisor.DevChildArguments(args, b, modeOverride: "annihilation"));
        Assert.Equal(["--bind", "127.0.0.1", "--dev-tools", "--mode", "seize", "--map", "mp_tdm_17", "--dev-supervised"], RotationSupervisor.DevChildArguments(args, "mp_tdm_17"));   // custom to Classic
        Assert.Equal(["--bind", "127.0.0.1", "--dev-tools", "--custom-map", b, "--dev-supervised"], RotationSupervisor.DevChildArguments(["--bind", "127.0.0.1", "--dev-tools", "--map", "mp_tdm_09"], b));   // Classic to custom
        Assert.True(ServerCommandLine.TryParse(RotationSupervisor.DevChildArguments(args, b, modeOverride: "annihilation"), out var child, out var error), error);
        Assert.Equal((b, GameMode.Annihilation, true), (child.CustomMap, child.Mode, child.DevSupervised));
        var rotation = RotationSupervisor.ChildArguments(["--maps", "all", "--mode", "seize"], b, 0, "annihilation");
        Assert.Equal(["--custom-map", b, "--rotation-child", "--mode", "annihilation"], rotation);

        string file = Path.Combine(_dir, "pick.txt");
        File.WriteAllText(file, b + "\nannihilation");
        Assert.Equal(b, RotationSupervisor.TakePick(file, out string? mode));
        Assert.Equal("annihilation", mode);
        Assert.False(File.Exists(file));
        File.WriteAllText(file, b);
        Assert.Equal(b, RotationSupervisor.TakePick(file, out mode));
        Assert.Null(mode);
        File.WriteAllText(file, Path.Combine(_dir, "gone.smmap"));
        Assert.Null(RotationSupervisor.TakePick(file));
        File.WriteAllText(file, "relative.smmap");
        Assert.Null(RotationSupervisor.TakePick(file));
    }
}
