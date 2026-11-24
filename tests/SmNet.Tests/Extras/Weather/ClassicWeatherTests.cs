using System.Net;
using System.Numerics;
using SmClient.Extras.Weather;
using SmNet.Extras.Weather;
using SmServer;
using SmSim;
using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>
/// Weather W3, Classic PvP hosting (docs/added-content.md "Weather"; spec/behavior.md "Weather (Classic PvP, W3)"): weather is part of the extended content set and nothing else; the host's choice (None by default, a preset or Random)
/// reaches the server and every client, late joiners and reconnects included; the cycle is fitted to the match and follows its clock; a rotation's Random draw is reproducible; the developer-tools override obeys the F6 gate and
/// is cleared with the round. Presentation only. Annihilation and Seize Ground both run here (synthetic classes, weapons and points; no original bytes).
/// </summary>
public class ClassicWeatherTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);
    const int Marine = 0, ChaosClass = 1;
    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ClassLoadout Tac(string name, Team side) => new(name, 0, 1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
    static SimConfig Config(ContentExtras extras) => new()
    {
        Weapons = [Rifle, Sidearm], Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)],
        HealthMax = 40f, ShieldMax = 0f, HealthRegenDelay = 1e6f, RespawnTime = 60f, Extras = extras,
    };
    static readonly ControlPoint A = new("mkr_objective_1", new Vector3(0, 0, 0), 5f, 10f);
    static readonly ControlPoint B = new("mkr_objective_2", new Vector3(40, 0, 0), 5f, 10f);
    static StartArea Area(Team side, Vector3 at, float yaw) => new(side, at, new Vector3(10, 10, 2), yaw);

    /// <summary>A server transport that reports one remote address for every connection (the real UDP transport always has one).</summary>
    sealed class AddressedTransport(IServerTransport inner, IPEndPoint remote) : IServerTransport
    {
        public void Send(int connection, byte[] packet) => inner.Send(connection, packet);
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
        public IPEndPoint? EndPointOf(int connection) => remote;
    }

    static readonly MatchRules Annihilation = new() { TimeLimit = 120f, ScoreLimit = 1000, CountdownTime = 2f, RestartDelay = 3f };
    static readonly MatchRules Seize = MatchRules.For(GameMode.SeizeGround) with { TimeLimit = 240f, CountdownTime = 2f, RestartDelay = 3f };

    sealed record Rig(Harness H, List<string> Log, GameClient A, GameClient B);

    /// <summary>A Classic server of <paramref name="extras"/> with two players (one a side) and the host's <paramref name="weather"/> choice.</summary>
    static Rig Classic(string? weather, GameMode mode = GameMode.Annihilation, ContentExtras? extras = null, DevToolsSetup? dev = null, IPEndPoint? remote = null, uint seed = 1, int draw = 0, MatchRules? rules = null)
    {
        var config = Config(extras ?? ContentExtras.All);
        GameMap? map = null;
        ClientOptions? clientOptions = null;
        if (mode == GameMode.SeizeGround)
        {
            var floor = new StaticMesh([new(-100, -100, 0), new(100, -100, 0), new(100, 100, 0), new(-100, 100, 0)], [0, 1, 2, 0, 2, 3]);
            var collision = new CollisionWorld(floor, floor);
            map = new GameMap("test_seize", collision, [], floor.ContentHash, [A, B], [Area(Team.SpaceMarines, A.Center, 0f), Area(Team.Chaos, new Vector3(-30, 0, 0), MathF.PI)]);
            clientOptions = new ClientOptions { LoadMap = _ => new LoadedMap(collision, floor.ContentHash) };
        }
        var options = new ServerOptions { Match = rules ?? (mode == GameMode.SeizeGround ? Seize : Annihilation), DevTools = dev, Weather = weather, WeatherSeed = seed, WeatherDraw = draw };
        var h = new Harness(Link, 5, 0, config, options, wrapServer: remote is null ? null : (Func<IServerTransport, IServerTransport>)(t => new AddressedTransport(t, remote)), map: map, clientOptions: clientOptions);
        var log = new List<string>(); h.Server.Log += log.Add;
        var a = h.AddClient(nonce: 100, options: (clientOptions ?? new ClientOptions()) with { RequestedClass = Marine });
        var b = h.AddClient(nonce: 101, options: (clientOptions ?? new ClientOptions()) with { RequestedClass = ChaosClass });
        return new Rig(h, log, a, b);
    }

    static void Settle(Harness h, double seconds) => h.Run(TimeSpan.FromSeconds(seconds));

    static List<WeatherStateWire> States(GameClient c)
    {
        var list = new List<WeatherStateWire>();
        foreach (var s in c.TakeExtras())
            if (s.Tag == WeatherWire.StateTag && WeatherWire.TryDecode(s.Payload, out WeatherStateWire w)) list.Add(w);
        return list;
    }

    static void Send(GameClient c, WeatherCommand cmd) => Assert.True(c.SendExtrasCommand(WeatherWire.CommandTag, WeatherWire.Encode(cmd)));
    static DevToolsSetup Setup(IPAddress? bind = null) => new(bind ?? IPAddress.Loopback, null);

    // ---- the content gate: extended alone ----

    [Fact]
    public void Classic_weather_is_the_weather_flag_alone_in_extended_or_by_itself()
    {
        Assert.True(WeatherClassic.Carries(ContentExtras.All));
        Assert.False(WeatherClassic.Carries(ContentExtras.None));
        Assert.False(WeatherClassic.Carries(ContentExtras.Orks | ContentExtras.Gore));
        Assert.True(WeatherClassic.Carries(ContentExtras.All & ~ContentExtras.Autocannon));   // the weather flag is what counts
        Assert.False(WeatherClassic.Carries(ContentExtras.Musou));
        Assert.False(WeatherClassic.Carries(ContentProfiles.WarfrontProfile));
        Assert.False(WeatherClassic.Carries(ContentExtras.All | ContentExtras.Musou));
        Assert.True(ContentProfiles.TryParse("extended", out var extended, out _) && WeatherClassic.Carries(extended));
        Assert.True(ContentProfiles.TryParse("faithful", out var faithful, out _) && !WeatherClassic.Carries(faithful));
        Assert.True(ContentProfiles.TryParse("orks,gore", out var partial, out _) && !WeatherClassic.Carries(partial));
        Assert.True(ContentProfiles.TryParse("weather", out var only, out _) && only == ContentExtras.Weather && WeatherClassic.Carries(only));   // faithful gameplay plus weather
        Assert.True(ContentProfiles.TryParse("orks,weather", out var mixed, out _) && WeatherClassic.Carries(mixed));
        Assert.False(WeatherClassic.Carries(ContentProfiles.WarfrontProfile | ContentExtras.Weather));
        Assert.False(ContentProfiles.TryParse("weather,musou", out _, out _));
        Assert.Equal("weather", ContentProfiles.Name(ContentExtras.Weather));
    }

    [Fact]
    public void A_faithful_or_partial_profile_has_no_weather_on_the_server_whatever_is_asked()
    {
        foreach (var extras in new[] { ContentExtras.None, ContentExtras.Orks | ContentExtras.Gore })
        {
            var r = Classic("rain", extras: extras);
            Settle(r.H, 4);
            Assert.Null(r.H.Server.Weather);
            Assert.Empty(States(r.A));
            Assert.DoesNotContain(r.Log, l => l.StartsWith("weather:"));
        }
    }

    [Fact]
    public void The_command_gate_reads_the_whole_extended_set_and_musou_alone()
    {
        Assert.True(ContentExtras.ReadsCommands(ContentExtras.All));
        Assert.True(ContentExtras.ReadsCommands(ContentExtras.Musou));
        Assert.True(ContentExtras.ReadsCommands(ContentProfiles.WarfrontProfile));
        Assert.False(ContentExtras.ReadsCommands(ContentExtras.None));
        Assert.False(ContentExtras.ReadsCommands(ContentExtras.Orks | ContentExtras.Gore));
        Assert.True(ContentExtras.ReadsCommands(ContentExtras.Weather));
        Assert.False(ContentExtras.ReadsCommands(ContentExtras.All & ~ContentExtras.Weather));
    }

    // ---- the host's choice reaches the server and the clients ----

    [Theory]
    [InlineData(GameMode.Annihilation)]
    [InlineData(GameMode.SeizeGround)]
    public void The_hosts_choice_reaches_the_server_and_every_client_in_both_modes(GameMode mode)
    {
        var r = Classic("rain", mode);
        Settle(r.H, 3);
        var w = r.H.Server.Weather;
        Assert.NotNull(w);
        Assert.Equal(WeatherId.Rain, w!.Baseline);
        foreach (var c in new[] { r.A, r.B })
        {
            var s = States(c);
            Assert.NotEmpty(s);
            Assert.All(s, x => Assert.Equal((WeatherId.Rain, false), (x.Id, x.Overridden)));
            Assert.Equal(mode == GameMode.SeizeGround ? 240 : 120, s[^1].SpanSeconds);
        }
        Assert.Contains(r.Log, l => l.StartsWith("weather: sent rain "));
    }

    [Fact]
    public void A_server_with_no_choice_is_capable_and_shows_none_exactly_as_today()
    {
        var r = Classic(null);
        Settle(r.H, 20);
        Assert.NotNull(r.H.Server.Weather);
        Assert.Equal(WeatherId.None, r.H.Server.Weather!.Baseline);
        var s = States(r.A);
        Assert.NotEmpty(s);
        Assert.All(s, x => Assert.Equal(WeatherId.None, x.Id));
        // the client's view of None: the identity grade (no change to the map's lighting), no rig, no sound
        var model = new WeatherClientModel();
        Assert.True(model.Receive(WeatherWire.Encode(s[^1])));
        Assert.True(model.Grade.IsIdentity);
        Assert.Equal(0f, model.EffectiveIntensity);
        Assert.False(WeatherClassic.Resolve(null, 1, 0) != WeatherId.None);
        Assert.Equal(WeatherId.None, WeatherClassic.Resolve("none", 9, 3));
        Assert.Equal(WeatherId.None, WeatherClassic.Resolve("not a preset", 9, 3));
    }

    [Fact]
    public void Weather_changes_no_part_of_the_game_the_world_is_the_same_with_any_choice()
    {
        static string Run(string? weather)
        {
            var r = Classic(weather);
            r.H.Run(TimeSpan.FromSeconds(8), Harness.Scripted);
            return string.Join("|", r.H.Server.World.Players.Values.OrderBy(p => p.PlayerId).Select(p => $"{p.PlayerId}:{p.Position}:{p.Health}")) + $"|{r.H.Server.World.State}|{r.H.Server.World.Config.Hash():x}";
        }
        string none = Run(null);
        Assert.Equal(none, Run("rain"));
        Assert.Equal(none, Run("thunderstorm"));
        Assert.Equal(none, Run("random"));
    }

    // ---- the cycle follows the match ----

    [Fact]
    public void The_weather_holds_before_play_runs_with_the_match_and_a_late_joiner_hears_the_current_phase()
    {
        var r = Classic("clear", rules: Annihilation with { CountdownTime = 3f });
        var w = r.H.Server.Weather!;
        Settle(r.H, 0.3);
        Assert.True(w.Held);   // waiting for the sides / counting down: the clock stands still
        Settle(r.H, 6);
        Assert.Equal(MatchState.Playing, r.H.Server.World.State);
        Assert.False(w.Held);
        double played = w.Current.Elapsed;
        Settle(r.H, 10);
        Assert.InRange(w.Current.Elapsed - played, 9.5, 10.5);   // it runs at the match's pace
        var late = r.H.AddClient(nonce: 500, options: new ClientOptions { RequestedClass = Marine });
        Settle(r.H, 0.6);   // well inside the two-second repeat: the joiner is told when it joins
        var heard = States(late);
        Assert.NotEmpty(heard);
        var last = heard[^1];
        Assert.Equal((WeatherId.Clear, 120, false), (last.Id, last.SpanSeconds, last.Held));
        Assert.InRange(last.ElapsedMs / 1000.0, played + 9.5, played + 11.5);
        Assert.Equal(WeatherClock.At(WeatherId.Clear, last.ElapsedMs / 1000.0, 120).Phase, last.Phase);
        Assert.Equal(last.ToState().Phase, WeatherClock.At(WeatherId.Clear, last.ElapsedMs / 1000.0, last.SpanSeconds).Phase);
    }

    [Fact]
    public void A_reconnecting_player_hears_the_phase_again()
    {
        var r = Classic("low_fog");
        Settle(r.H, 12);
        var before = States(r.A)[^1];
        var again = r.H.AddClient(nonce: 600, options: new ClientOptions { RequestedClass = ChaosClass });   // a fresh connection: what a reconnect is to the server
        Settle(r.H, 0.5);
        var heard = States(again);
        Assert.NotEmpty(heard);
        Assert.Equal((WeatherId.LowFog, before.SpanSeconds), (heard[^1].Id, heard[^1].SpanSeconds));
        Assert.True(heard[^1].ElapsedMs >= before.ElapsedMs);
    }

    [Fact]
    public void A_new_round_restarts_the_weather_from_its_opening_and_a_won_match_holds_it()
    {
        var r = Classic("rain", rules: Annihilation with { RestartDelay = 3f });
        Settle(r.H, 12);
        var w = r.H.Server.Weather!;
        Assert.False(w.Held);
        Assert.True(r.H.Server.World.DevEndMatch());
        Settle(r.H, 0.5);
        Assert.True(w.Held);   // the end screen: it stands still
        double held = w.Current.Elapsed;
        Settle(r.H, 1);
        Assert.Equal(held, w.Current.Elapsed, 3);
        Settle(r.H, 5);   // the restart delay passes, the next round counts down
        Assert.NotEqual(MatchState.Ended, r.H.Server.World.State);
        Assert.True(w.Current.Elapsed < held);   // restarted
        Assert.Equal(WeatherId.Rain, w.Baseline);
    }

    // ---- fitted to the match length ----

    [Fact]
    public void The_span_fits_the_time_limit_or_a_sensible_default()
    {
        Assert.Equal(600, WeatherClassic.SpanFor(0f));          // no limit: ten minutes
        Assert.Equal(600, WeatherClassic.SpanFor(-5f));
        Assert.Equal(600, WeatherClassic.SpanFor(float.NaN));
        Assert.Equal(600, WeatherClassic.SpanFor(600f));        // the server's default limit
        Assert.Equal(1200, WeatherClassic.SpanFor(1200f));
        Assert.Equal(WeatherClassic.MinSpan, WeatherClassic.SpanFor(5f));
        Assert.Equal(WeatherClassic.MaxSpan, WeatherClassic.SpanFor(24 * 3600f));
        Assert.Equal(new MatchRules().TimeLimit, (float)WeatherClassic.DefaultSpan);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    [InlineData(600)]
    [InlineData(1800)]
    [InlineData(7200)]
    public void The_phase_timeline_fits_the_match_length_for_every_preset(int span)
    {
        foreach (var def in WeatherCatalog.All.Where(d => d.Id != WeatherId.None))
        {
            Assert.Equal(WeatherPhase.Opening, WeatherClock.At(def.Id, 0, span).Phase);
            Assert.Equal(1f, WeatherClock.At(def.Id, 0, span).Intensity);   // the weather finish: full at once
            Assert.Equal(WeatherPhase.Peak, WeatherClock.At(def.Id, span * 0.5, span).Phase);   // the peak is mid-match
            Assert.Equal(1f, WeatherClock.At(def.Id, span * 0.5, span).Intensity);
            var late = WeatherClock.At(def.Id, span * 0.99, span);
            Assert.Equal(WeatherPhase.Recovery, late.Phase);                                    // it eases before the end
            Assert.True(late.Intensity <= WeatherClock.Lull(def) + 0.02f && late.Intensity >= WeatherClock.ActiveFloor, $"{def.Key} at 99 %: {late.Intensity}");
            // the whole match: opening, buildup, peak, recovery in order, each once
            var order = new List<WeatherPhase>();
            for (double t = 0; t < span; t += span / 400.0)
            {
                var p = WeatherClock.At(def.Id, t, span).Phase;
                if (order.Count == 0 || order[^1] != p) order.Add(p);
            }
            Assert.Equal([WeatherPhase.Opening, WeatherPhase.Buildup, WeatherPhase.Peak, WeatherPhase.Recovery], order);
            Assert.Equal(span, WeatherClock.At(def.Id, 3, span).Span);
            Assert.Equal(3.0, WeatherClock.At(def.Id, 3, span).Elapsed);                          // elapsed stays real seconds
        }
    }

    [Fact]
    public void A_span_of_zero_is_the_catalogs_own_clock_and_a_fitted_clock_never_goes_back_to_the_opening()
    {
        for (double t = 0; t < 700; t += 7.3)
            Assert.Equal(WeatherClock.At(WeatherId.Rain, t), WeatherClock.At(WeatherId.Rain, t, 0));
        for (double t = 600; t < 6000; t += 11.1)
            Assert.NotEqual(WeatherPhase.Opening, WeatherClock.At(WeatherId.Rain, t, 600).Phase);   // a match with no limit goes on in the cycle
    }

    // ---- Random: deterministic per map load ----

    [Fact]
    public void Random_is_deterministic_per_map_load_may_draw_none_and_never_repeats_back_to_back()
    {
        for (uint seed = 0; seed < 50; seed++)
        {
            WeatherId? previous = null;
            for (int draw = 0; draw < 40; draw++)
            {
                var id = WeatherClassic.Resolve("random", seed, draw);
                Assert.Equal(id, WeatherClassic.Resolve("RANDOM", seed, draw));   // the same load, the same preset

                Assert.True(WeatherCatalog.Of(id) is not null);
                if (previous is { } p) Assert.NotEqual(p, id);
                previous = id;
            }
        }
        var seen = new HashSet<WeatherId>();
        for (int draw = 0; draw < 60; draw++) seen.Add(WeatherClassic.Resolve("random", 12345, draw));
        Assert.Equal(7, seen.Count);   // every preset comes up, None too
        Assert.NotEqual(Enumerable.Range(0, 12).Select(i => WeatherClassic.Resolve("random", 1, i)), Enumerable.Range(0, 12).Select(i => WeatherClassic.Resolve("random", 2, i)));   // another seed, another order
    }

    [Fact]
    public void A_server_started_for_random_plays_the_draw_of_its_map_load()
    {
        for (int draw = 0; draw < 4; draw++)
        {
            var r = Classic("random", seed: 77, draw: draw);
            Settle(r.H, 1);
            Assert.Equal(WeatherClassic.Resolve("random", 77, draw), r.H.Server.Weather!.Baseline);
        }
    }

    [Fact]
    public void The_supervisor_tells_each_child_which_map_load_it_is()
    {
        var args = RotationSupervisor.WithWeatherSeed(["--bind", "127.0.0.1", "--maps", "a,b", "--weather", "random"], 99);
        Assert.Contains("--weather-seed", args);
        var one = RotationSupervisor.ChildArguments(args, "mp_a", 0);
        var two = RotationSupervisor.ChildArguments(args, "mp_b", 1);
        Assert.Equal(["--weather-draw", "0"], one.Skip(one.IndexOf("--weather-draw")).Take(2));
        Assert.Equal(["--weather-draw", "1"], two.Skip(two.IndexOf("--weather-draw")).Take(2));
        Assert.Single(two, "--weather-draw");
        // no --weather: the children's arguments are exactly as before
        Assert.Equal(["--bind", "127.0.0.1", "--port", "27300", "--map", "mp_a", "--rotation-child"], RotationSupervisor.ChildArguments(["--bind", "127.0.0.1", "--port", "27300", "--maps", "x,y"], "mp_a", 5));
        Assert.True(ServerCommandLine.TryParse(two, out var parsed, out var error), error);
        Assert.Equal(("random", 1, 99u), (parsed.Weather, parsed.WeatherDraw, parsed.WeatherSeed ?? 0));
    }


    [Fact]
    public void The_command_line_takes_the_weather_flags_and_refuses_nonsense()
    {
        Assert.True(ServerCommandLine.TryParse(["--weather", "rain"], out var a, out _));
        Assert.Equal(("rain", 0), (a.Weather, a.WeatherDraw));
        Assert.True(ServerCommandLine.TryParse([], out var none, out _));
        Assert.Null(none.Weather);                                                            // the default: nothing asked, None
        Assert.True(ServerCommandLine.TryParse(["--weather", "random", "--weather-seed", "42", "--weather-draw", "3"], out var b, out _));
        Assert.Equal(("random", 42u, 3), (b.Weather, b.WeatherSeed ?? 0, b.WeatherDraw));
        Assert.False(ServerCommandLine.TryParse(["--weather"], out _, out _));
        Assert.False(ServerCommandLine.TryParse(["--weather-draw", "-1"], out _, out _));
        Assert.False(ServerCommandLine.TryParse(["--weather-seed", "x"], out _, out _));
        Assert.Equal("rain", WeatherClassic.NormalizeKey(" Rain "));
        Assert.Equal("low_fog", WeatherClassic.NormalizeKey("low-fog"));
        Assert.Null(WeatherClassic.NormalizeKey("snow"));
        Assert.Contains("--weather <preset>", ServerCommandLine.Usage);
        // every key of the host selector is a preset of the catalog or random, in the owner's order
        Assert.Equal(["none", "clear", "low_fog", "rain", "thunderstorm", "ash_squall", "ember_wind", "random"], WeatherClassic.HostChoices.Select(c => c.Key));
        foreach (var c in WeatherClassic.HostChoices.Where(c => c.Key != "random")) Assert.NotNull(WeatherCatalog.ByKey(c.Key));
    }

    // ---- the wire's fitted form ----

    [Fact]
    public void The_fitted_state_round_trips_with_its_span_and_hold_and_the_plain_form_is_unchanged()
    {
        var d = new WeatherDirector(WeatherId.Rain, 600) { Held = true };
        var wire = WeatherWire.Of(d);
        var bytes = WeatherWire.Encode(wire);
        Assert.Equal(WeatherWire.FittedStateBytes, bytes.Length);
        Assert.True(WeatherWire.TryDecode(bytes, out WeatherStateWire back));
        Assert.Equal(wire, back);
        Assert.True(back.Held);
        Assert.Equal(600, back.SpanSeconds);
        d.Held = false; d.Advance(100);
        wire = WeatherWire.Of(d);
        Assert.True(WeatherWire.TryDecode(WeatherWire.Encode(wire), out back));
        Assert.Equal((WeatherPhase.Buildup, false, 600), (back.Phase, back.Held, back.SpanSeconds));
        Assert.Equal(WeatherClock.At(WeatherId.Rain, 100, 600).Phase, back.ToState().Phase);
        // a catalog clock (Warfront, an override) is the eight bytes of W1, and a held flag never rides on it
        var plain = WeatherWire.Encode(WeatherWire.Of(new WeatherDirector(WeatherId.Rain)));
        Assert.Equal(WeatherWire.StateBytes, plain.Length);
        var bad = (byte[])plain.Clone(); bad[6] = 2;
        Assert.False(WeatherWire.TryDecode(bad, out WeatherStateWire _));
        // garbage never decodes: a zero span, a flag out of range, a phase that is not the fitted clock's
        var good = WeatherWire.Encode(WeatherWire.Of(new WeatherDirector(WeatherId.Clear, 300)));
        bad = (byte[])good.Clone(); bad[8] = 0; bad[9] = 0;
        Assert.False(WeatherWire.TryDecode(bad, out WeatherStateWire _));
        bad = (byte[])good.Clone(); bad[6] = 4;
        Assert.False(WeatherWire.TryDecode(bad, out WeatherStateWire _));
        bad = (byte[])good.Clone(); bad[1] = (byte)WeatherPhase.Peak;
        Assert.False(WeatherWire.TryDecode(bad, out WeatherStateWire _));
        var rng = new Random(11);
        for (int i = 0; i < 3000; i++) { var junk = new byte[rng.Next(0, 14)]; rng.NextBytes(junk); WeatherWire.TryDecode(junk, out WeatherStateWire _); }
    }

    [Fact]
    public void The_client_clock_stands_still_while_the_server_holds_and_runs_with_it_after()
    {
        var model = new WeatherClientModel();
        var held = new WeatherDirector(WeatherId.Rain, 600) { Held = true };
        Assert.True(model.Receive(WeatherWire.Encode(WeatherWire.Of(held))));
        model.Advance(30);
        Assert.Equal(0.0, model.State.Elapsed);
        Assert.True(model.Held);
        held.Held = false;
        Assert.True(model.Receive(WeatherWire.Encode(WeatherWire.Of(held))));
        model.Advance(30);
        Assert.Equal(30.0, model.State.Elapsed, 3);
        Assert.Equal(WeatherPhase.Opening, model.State.Phase);
    }

    // ---- the F6 gate ----

    [Fact]
    public void Under_the_gate_the_developer_tools_pick_none_and_follow_match_on_a_classic_server_and_every_client_sees_it()
    {
        var r = Classic("rain", dev: Setup());
        Settle(r.H, 12);
        States(r.A); States(r.B);
        Send(r.A, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Thunderstorm));
        Settle(r.H, 0.3);
        foreach (var c in new[] { r.A, r.B })
        {
            var s = States(c)[^1];
            Assert.Equal((WeatherId.Thunderstorm, WeatherPhase.Opening, true, 0), (s.Id, s.Phase, s.Overridden, s.SpanSeconds));   // an override starts at its opening phase, on the catalog's own lengths
        }
        Send(r.A, new WeatherCommand(WeatherCommandKind.Set, WeatherId.None));
        Settle(r.H, 0.3);
        Assert.Equal((WeatherId.None, true), (States(r.B)[^1].Id, true));
        Send(r.B, new WeatherCommand(WeatherCommandKind.Follow));
        Settle(r.H, 0.3);
        var back = States(r.A)[^1];
        Assert.Equal((WeatherId.Rain, false, 120), (back.Id, back.Overridden, back.SpanSeconds));
        Assert.True(back.ElapsedMs > 5000);   // the match's own clock kept running underneath: Follow Match resumes its current phase
        Assert.Equal(3, r.H.Server.WeatherCommandsApplied);
    }

    [Fact]
    public void An_ordinary_hosted_client_cannot_set_the_active_preset()
    {
        // a hosted match without --dev-tools (LAN / Internet host), and one with it on a non-loopback bind
        foreach (var dev in new DevToolsSetup?[] { null, Setup(new IPAddress(new byte[4])) })
        {
            var r = Classic("rain", dev: dev);
            Settle(r.H, 4);
            Send(r.A, new WeatherCommand(WeatherCommandKind.Set, WeatherId.None));
            Send(r.B, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
            Settle(r.H, 0.4);
            Assert.Null(r.H.Server.Weather!.Override);
            Assert.Equal(WeatherId.Rain, States(r.A)[^1].Id);
            Assert.Equal((0, 2), (r.H.Server.WeatherCommandsApplied, r.H.Server.WeatherCommandsRefused));
        }
    }

    [Fact]
    public void A_non_loopback_client_is_refused_even_under_the_gate_and_still_hears_the_weather()
    {
        var r = Classic("clear", dev: Setup(), remote: new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000));
        Settle(r.H, 4);
        Assert.Equal(WeatherId.Clear, States(r.A)[^1].Id);
        Send(r.A, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Rain));
        Settle(r.H, 0.3);
        Assert.True(r.H.Server.DevToolsOn);
        Assert.Null(r.H.Server.Weather!.Override);
        Assert.Equal((0, 1), (r.H.Server.WeatherCommandsApplied, r.H.Server.WeatherCommandsRefused));
        Assert.Contains(r.Log, l => l.Contains("not a loopback address"));
    }

    [Fact]
    public void An_extended_server_reads_the_weather_command_and_no_other_tag()
    {
        var r = Classic("clear", dev: Setup());
        Settle(r.H, 3);
        var before = r.H.Server.World.Players.Values.OrderBy(p => p.PlayerId).Select(p => (p.PlayerId, p.Position, p.Health)).ToList();
        Assert.True(r.A.SendExtrasCommand(7, [1, 2, 3]));
        Assert.True(r.A.SendExtrasCommand(16, [0, 1]));   // a tag of Musou Mode: dropped here, never reaches its handlers
        Settle(r.H, 0.2);
        Assert.Equal(0, r.H.Server.Faults);
        Assert.Equal(0, r.H.Server.WeatherCommandsApplied);
        Assert.Null(r.H.Server.Weather!.Override);
        Assert.Equal(ClientStatus.Connected, r.A.Status);
    }

    // ---- the override ends with the round ----

    [Fact]
    public void An_override_is_cleared_by_a_match_restart_and_by_the_next_round_and_never_enters_the_lobby_config()
    {
        var r = Classic("rain", dev: Setup(), rules: Annihilation with { RestartDelay = 2f });
        Settle(r.H, 12);
        var w = r.H.Server.Weather!;
        ulong hash = r.H.Server.World.Config.Hash();
        Send(r.A, new WeatherCommand(WeatherCommandKind.Set, WeatherId.AshSquall));
        Settle(r.H, 0.3);
        Assert.True(w.Overridden);
        Assert.Equal(hash, r.H.Server.World.Config.Hash());   // the config hash (the lobby's agreement) is the same with an override
        Assert.True(r.H.Server.World.DevRestartMatch());
        Settle(r.H, 0.5);
        Assert.False(w.Overridden);
        Assert.Equal(WeatherId.Rain, w.Baseline);
        Assert.Equal(WeatherId.Rain, States(r.B)[^1].Id);
        Settle(r.H, 6);   // played again
        Send(r.A, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(r.H, 0.3);
        Assert.True(w.Overridden);
        Assert.True(r.H.Server.World.DevEndMatch());
        Settle(r.H, 6);   // the end screen, the delay, the next round
        Assert.False(w.Overridden);
        Assert.Equal(hash, r.H.Server.World.Config.Hash());
        Assert.Equal(WeatherId.Rain, States(r.A)[^1].Id);
    }

    [Fact]
    public void A_second_map_starts_its_weather_from_the_opening_with_the_same_choice()
    {
        // a rotation is one server process per map: the next map's server is a fresh one given the same flag (and the draw of its load)
        var first = Classic("thunderstorm", draw: 0);
        Settle(first.H, 30);
        var second = Classic("thunderstorm", draw: 1);
        Settle(second.H, 1);
        Assert.Equal(WeatherId.Thunderstorm, second.H.Server.Weather!.Baseline);
        Assert.True(second.H.Server.Weather.Current.Elapsed < first.H.Server.Weather!.Current.Elapsed);
        Assert.Equal(WeatherPhase.Opening, second.H.Server.Weather.Current.Phase);
    }

    // ---- the weather flag by itself: faithful gameplay plus weather ----

    [Fact]
    public void A_weather_only_server_has_weather_and_plays_exactly_as_the_faithful_game()
    {
        static string Run(ContentExtras extras, string? weather)
        {
            var r = Classic(weather, extras: extras);
            r.H.Run(TimeSpan.FromSeconds(8), Harness.Scripted);
            return string.Join("|", r.H.Server.World.Players.Values.OrderBy(p => p.PlayerId).Select(p => $"{p.PlayerId}:{p.Position}:{p.Health}:{p.Class}")) + $"|{r.H.Server.World.State}";
        }
        var r = Classic("rain", extras: ContentExtras.Weather);
        Settle(r.H, 3);
        Assert.Equal(WeatherId.Rain, r.H.Server.Weather!.Baseline);
        Assert.Equal(WeatherId.Rain, States(r.A)[^1].Id);
        Assert.Equal(Run(ContentExtras.None, null), Run(ContentExtras.Weather, "rain"));   // same players, positions and health as no added content at all
        Assert.Equal(Run(ContentExtras.None, null), Run(ContentExtras.Weather, null));
        Assert.Equal(Run(ContentExtras.None, null), Run(ContentExtras.Orks | ContentExtras.Weather, "thunderstorm"));
        var quiet = Classic("none", extras: ContentExtras.Weather);   // capable, preset None: it sends None, the client keeps the map as it is
        Settle(quiet.H, 3);
        Assert.Equal(WeatherId.None, States(quiet.A)[^1].Id);
        Assert.NotNull(quiet.H.Server.Weather);
    }

    [Fact]
    public void Weather_never_decides_who_joins_a_faithful_client_joins_a_weather_only_server_and_gets_the_weather()
    {
        // Weather is presentation only: its bit is left out of the config hash, so a weather server accepts exactly the clients it would without it.
        Assert.Equal(Config(ContentExtras.None).Hash(), Config(ContentExtras.Weather).Hash());
        Assert.Equal(Config(ContentExtras.Orks).Hash(), Config(ContentExtras.Orks | ContentExtras.Weather).Hash());
        Assert.Equal(Config(ContentExtras.All & ~ContentExtras.Weather).Hash(), Config(ContentExtras.All).Hash());
        var h = new Harness(Link, 5, 0, Config(ContentExtras.Weather), new ServerOptions { Match = Annihilation, Weather = "rain" });
        var faithful = h.AddClient(config: Config(ContentExtras.None), nonce: 1, options: new ClientOptions { RequestedClass = Marine });
        var same = h.AddClient(config: Config(ContentExtras.Weather), nonce: 3, options: new ClientOptions { RequestedClass = ChaosClass });
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(ClientStatus.Connected, faithful.Status);
        Assert.Equal(ClientStatus.Connected, same.Status);
        Assert.Equal(WeatherId.Rain, States(faithful)[^1].Id);   // the faithful client takes the weather state from the envelope (a client built with extras renders it)
        Assert.Equal(WeatherId.Rain, States(same)[^1].Id);
    }

    [Fact]
    public void An_extended_client_is_still_refused_by_a_weather_only_server_because_the_orks_and_gore_differ_and_the_other_pairings_join()
    {
        var weatherOnly = new Harness(Link, 5, 0, Config(ContentExtras.Weather), new ServerOptions { Match = Annihilation });
        var extended = weatherOnly.AddClient(config: Config(ContentExtras.All), nonce: 2);
        weatherOnly.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(RejectReason.ConfigMismatch, extended.RejectedBecause);
        // an orks,weather server takes an orks client (and refuses a faithful one); an extended server takes a client of the extended set with or without the weather bit
        var orks = new Harness(Link, 5, 0, Config(ContentExtras.Orks | ContentExtras.Weather), new ServerOptions { Match = Annihilation });
        var orksClient = orks.AddClient(config: Config(ContentExtras.Orks), nonce: 4);
        var plain = orks.AddClient(config: Config(ContentExtras.None), nonce: 5);
        orks.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(ClientStatus.Connected, orksClient.Status);
        Assert.Equal(RejectReason.ConfigMismatch, plain.RejectedBecause);
        var ext = new Harness(Link, 5, 0, Config(ContentExtras.All), new ServerOptions { Match = Annihilation });
        var noWeatherBit = ext.AddClient(config: Config(ContentExtras.All & ~ContentExtras.Weather), nonce: 6);
        ext.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(ClientStatus.Connected, noWeatherBit.Status);
    }

    [Fact]
    public void A_weather_client_on_a_server_without_weather_shows_nothing()
    {
        var h = new Harness(Link, 5, 0, Config(ContentExtras.None), new ServerOptions { Match = Annihilation });
        var weatherClient = h.AddClient(config: Config(ContentExtras.Weather), nonce: 1, options: new ClientOptions { RequestedClass = Marine });
        h.Run(TimeSpan.FromSeconds(4));
        Assert.Equal(ClientStatus.Connected, weatherClient.Status);
        Assert.Null(h.Server.Weather);
        Assert.Empty(States(weatherClient));
        var model = new WeatherClientModel();
        Assert.False(model.Has);
        Assert.True(model.Grade.IsIdentity);
    }

    [Fact]
    public void A_faithful_content_client_can_use_the_f6_weather_on_a_local_weather_server_and_a_server_without_weather_drops_the_command()
    {
        var h = new Harness(Link, 5, 0, Config(ContentExtras.Weather), new ServerOptions { Match = Annihilation, Weather = "rain", DevTools = Setup() });
        var a = h.AddClient(config: Config(ContentExtras.None), nonce: 1, options: new ClientOptions { RequestedClass = Marine });
        var b = h.AddClient(config: Config(ContentExtras.None), nonce: 2, options: new ClientOptions { RequestedClass = ChaosClass });
        Settle(h, 4);
        Send(a, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(h, 0.4);
        Assert.Equal(WeatherId.Clear, h.Server.Weather!.Override);
        var s = States(b)[^1];
        Assert.Equal((WeatherId.Clear, true), (s.Id, s.Overridden));
        // a server without weather drops the command whatever the client's content; any other tag is still refused at the client
        var plain = new Harness(Link, 5, 0, Config(ContentExtras.None), new ServerOptions { Match = Annihilation, DevTools = Setup() });
        var c = plain.AddClient(config: Config(ContentExtras.None), nonce: 1, options: new ClientOptions { RequestedClass = Marine });
        Settle(plain, 3);
        Send(c, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(plain, 0.3);
        Assert.Null(plain.Server.Weather);
        Assert.True(plain.Server.ExtrasCommandsDropped >= 1);
        Assert.False(c.SendExtrasCommand(7, [1]));
    }

    [Fact]
    public void Seize_ground_holds_runs_and_restarts_its_weather_with_the_round()
    {
        var r = Classic("rain", GameMode.SeizeGround, dev: Setup(), rules: Seize with { RestartDelay = 3f });
        var w = r.H.Server.Weather!;
        Settle(r.H, 0.3);
        Assert.True(w.Held);
        Settle(r.H, 10);
        Assert.Equal(MatchState.Playing, r.H.Server.World.State);
        Assert.False(w.Held);
        Assert.Equal(240, w.Span);
        double played = w.Current.Elapsed;
        Assert.True(played > 2);
        Send(r.A, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(r.H, 0.3);
        Assert.True(w.Overridden);
        Assert.True(r.H.Server.World.DevRestartMatch());   // a restart: a new round
        Settle(r.H, 0.5);
        Assert.False(w.Overridden);
        Assert.True(w.Current.Elapsed < played);
        Settle(r.H, 6);
        Assert.True(r.H.Server.World.DevEndMatch());
        Settle(r.H, 0.5);
        Assert.True(w.Held);
        Settle(r.H, 6);   // the next round
        Assert.NotEqual(MatchState.Ended, r.H.Server.World.State);
        Assert.Equal(WeatherId.Rain, w.Baseline);
    }
}
