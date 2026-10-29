using System.Text;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Display names (names/bot-lore, protocol 41): the per-race pools (original names, no canon character), the seeded unique choice, the sanitiser, the
/// scoreboard's name field on the wire, and the server naming test bots (by race, unique, reproducible with a seed) while humans keep "P&lt;id&gt;".
/// Synthetic data only.
/// </summary>
public class PlayerNameTests(ITestOutputHelper output)
{
    /// <summary>
    /// Established named characters of Space Marine (2011), Space Marine 2 and the wider Warhammer 40,000 / Warhammer canon the pools must not use (whole
    /// words; names of 5 letters and more also not inside a word). Not exhaustive: the well-known ones and the ones a name in the style would come near.
    /// </summary>
    static readonly string[] Canon =
    [
        // Space Marine (2011) and Space Marine 2
        "Titus", "Leandros", "Sidonus", "Nemeroth", "Drogan", "Mira", "Grimskull", "Chairon", "Gadriel", "Demetrian", "Trajann", "Acheran", "Imurah", "Tsorin",
        // Ultramarines and other loyalists
        "Calgar", "Marneus", "Guilliman", "Roboute", "Tigurius", "Varro", "Sicarius", "Cato", "Agemman", "Severus", "Telion", "Chronus", "Uriel", "Ventris",
        "Pasanius", "Learchus", "Cassian", "Dracos", "Idaeus", "Galenus", "Fennion", "Numitor", "Gage", "Lysander", "Dante", "Mephiston", "Azrael", "Lemartes",
        "Gabriel", "Angelos", "Tarkus", "Avitus", "Cyrus", "Thule", "Kantor", "Helbrecht", "Grimaldus", "Loken", "Garro", "Tarvitz", "Vulkan", "Corvus", "Sanguinius",
        "Aethon", "Hadrian", "Ortan", "Cassius",
        // Chaos
        "Abaddon", "Ezekyle", "Kharn", "Lucius", "Fabius", "Typhus", "Mortarion", "Ahriman", "Huron", "Erebus", "Lorgar", "Angron", "Horus", "Perturabo",
        "Kranon", "Haarken", "Argel", "Phaeron", "Zhufor", "Doombreed", "Valkia", "Sigvald", "Archaon", "Be'lakor", "Belakor", "Fulgrim", "Magnus", "Konrad",
        "Curze", "Alpharius", "Omegon", "Vraks", "Cypher",
        // Orks
        "Ghazghkull", "Thraka", "Makari", "Grukk", "Snagrod", "Badrukk", "Nazdreg", "Zagstruk", "Wazdakka", "Snikrot", "Grotsnik", "Zogwort", "Ufthak",
        "Gorgutz", "Skarsnik", "Gorbag", "Wurrzag", "Bluddflagg", "Zodgrod", "Gork", "Mork", "Gazbag", "Grimgor", "Azhag", "Morglum",
        // Added after review (names/bot-lore): more loyalist, Chaos and Space Marine 2 characters
        "Carnac", "Decimus", "Thaddeus", "Pedro", "Ragnar", "Logan", "Ezekiel", "Eldrad", "Zahariel", "Sevatar", "Shrike", "Khan", "Aurelian",
        "Iolanthus", "Quintus", "Valoris", "Kayvaan", "Jaghatai", "Sammael", "Belial", "Asmodai", "Seth", "Tycho", "Corbulo", "Dorn", "Russ",
        "Ferrus", "Manus", "Lion", "Jonson", "Sigismund", "Tarik", "Torgaddon", "Abaddon", "Aximand", "Kor", "Vaylund", "Cal", "Bellator", "Alarik",
        // Other games and fiction the style comes near
        "Vorkath", "Xerath",
        // Words a profanity filter may catch
        "Sext", "Sextor",
    ];

    static IEnumerable<(Race Race, string Name)> AllNames() =>
        new[] { Race.SpaceMarines, Race.Chaos, Race.Orks }.SelectMany(r => PlayerNames.PoolFor(r).Select(n => (r, n)));

    [Fact]
    public void Every_pool_has_at_least_30_short_ascii_names_once()
    {
        foreach (var race in new[] { Race.SpaceMarines, Race.Chaos, Race.Orks })
        {
            var pool = PlayerNames.PoolFor(race);
            Assert.True(pool.Count >= 30, $"{race}: {pool.Count} names");
            output.WriteLine($"{race}: {pool.Count} names, longest {pool.Max(n => n.Length)}: {string.Join(", ", pool)}");
        }
        var all = AllNames().Select(x => x.Name).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());   // unique across every pool
        foreach (string name in all)
        {
            Assert.InRange(name.Length, 3, PlayerNames.MaxLength);
            Assert.All(name, c => Assert.True(c is >= ' ' and <= '~', $"'{name}' is not plain ASCII"));
            Assert.Equal(name, PlayerNames.Sanitise(name));   // travels unchanged
        }
        Assert.All(PlayerNames.SpaceMarines, n => Assert.True(n.StartsWith("Brother ") || n.StartsWith("Sergeant "), n));
        Assert.Same(PlayerNames.SpaceMarines, PlayerNames.PoolFor(Race.None));
    }

    [Fact]
    public void No_pool_name_is_a_canon_character()
    {
        var canon = Canon.Select(c => c.ToLowerInvariant()).ToHashSet();
        foreach (var (race, name) in AllNames())
        {
            foreach (string word in name.Split(' ', '-', '\''))
            {
                string w = word.ToLowerInvariant();
                Assert.False(canon.Contains(w), $"{race} '{name}': '{word}' is a canon name");
                foreach (string c in canon.Where(c => c.Length >= 5))
                    Assert.False(w.Contains(c), $"{race} '{name}': '{word}' contains the canon name '{c}'");
            }
        }
    }

    /// <summary>Edit distance (insert, delete, replace one character), case-insensitive.</summary>
    static int Levenshtein(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    [Fact]
    public void No_pool_name_is_one_letter_from_a_canon_character()
    {
        Assert.Equal(1, Levenshtein("Cassus", "Cassius"));
        Assert.Equal(1, Levenshtein("Aurelan", "Aurelian"));
        foreach (var (race, name) in AllNames())
            foreach (string word in name.Split(' ', '-', '\'').Where(w => w.Length >= 5))
                foreach (string c in Canon.Where(c => c.Length >= 5))
                    Assert.True(Levenshtein(word, c) > 1, $"{race} '{name}': '{word}' is one letter from the canon name '{c}'");
    }

    [Fact]
    public void The_widest_scoreboard_row_keeps_the_whole_name_and_you()
    {
        string longest = AllNames().Select(x => x.Name).OrderByDescending(n => n.Length).First();
        Assert.StartsWith(new string('n', PlayerNames.MaxLength) + " (you) ", PlayerNames.Cell(new string('n', PlayerNames.MaxLength) + " (you)", PlayerNames.ScoreboardColumn));
        string cell = PlayerNames.Cell(longest + " (you)", PlayerNames.ScoreboardColumn);
        Assert.Equal(PlayerNames.ScoreboardColumn, cell.Length);
        Assert.Equal(longest + " (you) ", cell.TrimEnd() + " ");   // nothing cut, a space before the class column
        Assert.Equal("abcd ", PlayerNames.Cell("abcdefgh", 5));   // too long: cut, one space left
        Assert.Equal("ab   ", PlayerNames.Cell("ab", 5));
    }

    [Fact]
    public void A_client_keeps_at_most_the_capped_number_of_names()
    {
        var h = new Harness(LinkConditions.Perfect, 5, clients: 0);
        var viewer = h.AddClient(nonce: 100);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, viewer.Status);
        // Boards from the server listing ever new named players (as over a long session with many bots coming and going), each one tick newer.
        uint tick = h.Server.World.Tick + 1000;
        for (int k = 0; k < 400; k++)
        {
            var board = new Scoreboard(tick++, true, 41, 600f, false,
                [new ScoreEntry(viewer.PlayerId, Team.SpaceMarines, 0, 0, 0), new ScoreEntry(10_000 + k, Team.Chaos, 0, 0, 0, 0, -1, $"Bot {k}"), new ScoreEntry(9_999, Team.Chaos, 0, 0, 0, 0, -1, "Stays")]);
            h.Net.Server.Send(0, Protocol.Write(board));
            h.Step();
        }
        h.Run(TimeSpan.FromMilliseconds(50));
        Assert.True(viewer.KeptNames <= GameClient.MaxKeptNames, $"{viewer.KeptNames} names kept");
        Assert.Equal("Bot 399", viewer.NameOf(10_399));   // the newest kept
        Assert.Equal("Stays", viewer.NameOf(9_999));      // listed on every board: never dropped
        Assert.Equal("P10000", viewer.NameOf(10_000));    // the oldest departed one dropped
        Assert.Equal("Bot 200", viewer.NameOf(10_200));   // recent departed ones still named
    }

    [Fact]
    public void A_pick_is_reproducible_unique_and_of_the_race()
    {
        foreach (var race in new[] { Race.SpaceMarines, Race.Chaos, Race.Orks })
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var first = new List<string>();
            for (int order = 0; order < 16; order++)
            {
                string name = PlayerNames.Pick(race, 1234, order, taken.Contains);
                Assert.Contains(name, PlayerNames.PoolFor(race));
                Assert.True(taken.Add(name), $"{race}: '{name}' given twice");
                first.Add(name);
            }
            // The same seed, race, order and taken names: the same names.
            var again = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(first, Enumerable.Range(0, 16).Select(o => { string n = PlayerNames.Pick(race, 1234, o, again.Contains); again.Add(n); return n; }));
            // Another seed: another order.
            var other = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(first, Enumerable.Range(0, 16).Select(o => { string n = PlayerNames.Pick(race, 99, o, other.Contains); other.Add(n); return n; }));
        }
        // A name in use is skipped, whatever its case.
        string taken0 = PlayerNames.Pick(Race.Chaos, 7, 0, _ => false);
        Assert.NotEqual(taken0, PlayerNames.Pick(Race.Chaos, 7, 0, n => n.Equals(taken0.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase)));
        // The whole pool taken: numbered names, still unique and short.
        var full = new HashSet<string>(PlayerNames.Orks, StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < 40; k++)
        {
            string n = PlayerNames.Pick(Race.Orks, 5, k, full.Contains);
            Assert.True(full.Add(n), n);
            Assert.True(n.Length <= PlayerNames.MaxLength, n);
        }
        Assert.Contains(full, n => n.EndsWith(" II"));
        Assert.Contains(full, n => n.EndsWith(" III"));
    }

    [Fact]
    public void The_sanitiser_keeps_latin1_names_and_drops_the_rest()
    {
        Assert.Equal("", PlayerNames.Sanitise(null));
        Assert.Equal("", PlayerNames.Sanitise("   "));
        Assert.Equal("Brother Aurex", PlayerNames.Sanitise("  Brother \t\n  Aurex  "));
        Assert.Equal("colorredSkarnecolor", PlayerNames.Sanitise("[color=red]Skarne[/color]"));   // brackets and '=' and '/' gone: no BBCode survives
        Assert.Equal("Zoe Hel-Brakk O'Neil", PlayerNames.Sanitise("Zoe\u0000 Hel-Brakk\u001b O'Neil"));
        Assert.Equal("Éloïse", PlayerNames.Sanitise("Éloïse"));           // Latin-1 letters stay
        Assert.Equal("Grk", PlayerNames.Sanitise("GΩr€k"));                // outside Latin-1: dropped
        Assert.Equal(PlayerNames.MaxLength, PlayerNames.Sanitise(new string('a', 100)).Length);
        Assert.Equal("P7", PlayerNames.Display(7, ""));
        Assert.Equal("P7", PlayerNames.Display(7, null));
        Assert.Equal("Brother Aurex", PlayerNames.Display(7, "Brother Aurex"));
    }

    [Fact]
    public void Names_round_trip_in_the_scoreboard_and_a_full_board_stays_in_budget()
    {
        Assert.Equal(42, Protocol.Version);
        string longest = "Ilkarn of the Ninth Pyre";
        Assert.Equal(PlayerNames.MaxLength, longest.Length);
        var board = new Scoreboard(9, true, 41, 600f, false,
        [
            new ScoreEntry(1, Team.SpaceMarines, 0, 1, 2, 3, 0, "Brother Aurex"),
            new ScoreEntry(2, Team.Chaos, 1, 0, 0, 0, -1, longest),
            new ScoreEntry(3, Team.Chaos, 1, 0, 0),                       // a human: no name
            new ScoreEntry(4, Team.SpaceMarines, 0, 0, 0, 0, -1, "Éloïse"),  // Latin-1
        ]);
        var bytes = Protocol.Write(board);
        var back = Assert.IsType<Scoreboard>(Protocol.Read(bytes));
        Assert.Equal(board.Players, back.Players);
        Assert.Equal(1 + 4 + 1 + 2 + 4 + 1 + 4 * Protocol.ScoreRowBytes + "Brother Aurex".Length + longest.Length + "Éloïse".Length, bytes.Length);   // one byte a Latin-1 letter

        // A name the writer is handed unclean is sent clean; null (a default entry) is no name.
        var dirty = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board with { Players = [new ScoreEntry(1, Team.Chaos, 1, 0, 0, 0, -1, "[b]Skarne\u0007" + new string('x', 40))] })));
        Assert.Equal(("bSkarne" + new string('x', 40))[..PlayerNames.MaxLength], dirty.Players[0].Name);
        var none = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board with { Players = [default(ScoreEntry) with { PlayerId = 5 }] })));
        Assert.Equal("", none.Players[0].Name);

        // A row whose name length is above 24 is refused; a truncated name too.
        var one = Protocol.Write(board with { Players = [new ScoreEntry(1, Team.Chaos, 1, 0, 0, 0, -1, "Skarne")] });
        int lengthAt = one.Length - "Skarne".Length - 1;
        Assert.Equal(6, one[lengthAt]);
        var tooLong = one.ToArray(); tooLong[lengthAt] = PlayerNames.MaxLength + 1;
        Assert.Null(Protocol.Read(tooLong.Concat(new byte[PlayerNames.MaxLength + 1 - 6]).ToArray()));
        Assert.Null(Protocol.Read(one[..^1]));
        // Control bytes on the wire are dropped by the reader.
        var raw = one.ToArray(); raw[lengthAt + 1] = 0x07;
        Assert.Equal("karne", Assert.IsType<Scoreboard>(Protocol.Read(raw)).Players[0].Name);

        // 16 rows with 24-character names and the Seize Ground layout: under the 1200-byte packet.
        var seize = new SeizeGroundInfo(20f, 10f, 2, 5f, [.. Enumerable.Range(0, World.MaxControlPoints).Select(i => new ControlPointInfo(new string('p', Protocol.MaxMapName), new System.Numerics.Vector3(i, 2, 3), 5f, 2f))]);
        var full = new Scoreboard(1, true, 1, 1f, true,
            [.. Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new ScoreEntry(i, Team.Chaos, 1, 65535, 65535, 65535, 3, ("Name" + i).PadRight(PlayerNames.MaxLength, 'x')))], seize);
        int size = Protocol.Write(full).Length;
        output.WriteLine($"largest scoreboard (16 rows, 24-character names, {World.MaxControlPoints} points with {Protocol.MaxMapName}-character names): {size} bytes (budget {Protocol.SnapshotBudget})");
        Assert.True(size <= Protocol.SnapshotBudget, $"{size} bytes");
        Assert.Equal(full.Players, Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(full))).Players);
    }

    /// <summary>The custom-class fixture with an Ork Boy (class 4: no side, race Orks), the Orks on.</summary>
    static readonly SimConfig OrkConfig = CustomFixture.Config with
    {
        Classes =
        [
            .. CustomFixture.Config.Classes,
            new ClassLoadout("ork - boy", CustomFixture.Rifle, CustomFixture.Sidearm, SimConfig.PlaceholderMeleeSteps)
            {
                PrimaryChoices = [CustomFixture.Rifle], PistolChoices = [CustomFixture.Sidearm], Side = Team.None, Role = "ork",
                PerkChoices = [CustomFixture.Homer, CustomFixture.Targeter], DefaultPerk = CustomFixture.Homer, Body = 0, Race = Race.Orks,
            },
        ],
        Bodies = [BodySpec.Of(CustomFixture.Config, "ork - boy (test)") with { HealthMax = 40f }],
        Extras = ContentExtras.Orks,
    };

    static Harness Match(uint seed, out List<string> log)
    {
        var options = CustomFixture.Options with
        {
            NameSeed = seed, Match = CustomFixture.Options.Match! with { HonorSideRequests = true },
            MaxPlayersPerAddress = 0, JoinInterval = TimeSpan.Zero,   // every test client shares one loopback address
        };
        var h = new Harness(LinkConditions.Perfect, 5, clients: 0, OrkConfig, options);
        var lines = new List<string>();
        h.Server.Log += lines.Add;
        h.Server.OperatorLog += lines.Add;
        log = lines;
        return h;
    }

    static GameClient Bot(Harness h, uint nonce, Team side, int playerClass) =>
        h.AddClient(nonce: nonce, options: new ClientOptions { Bot = true, RequestedSide = side, RequestedClass = (byte)playerClass, OrkLook = 1 });

    [Fact]
    public void The_server_names_bots_by_race_and_humans_keep_their_number()
    {
        var h = Match(4242, out var log);
        var marines = Enumerable.Range(0, 3).Select(i => Bot(h, (uint)(100 + i), Team.SpaceMarines, CustomFixture.SmTac)).ToList();
        var chaos = Enumerable.Range(0, 3).Select(i => Bot(h, (uint)(200 + i), Team.Chaos, CustomFixture.CsmTac)).ToList();
        var ork = Bot(h, 300, Team.Chaos, 4);
        var human = h.AddClient(nonce: 400, options: new ClientOptions { RequestedSide = Team.SpaceMarines });
        h.Run(TimeSpan.FromSeconds(3));
        var all = h.Clients;
        Assert.All(all, c => Assert.Equal(ClientStatus.Connected, c.Status));

        string NameOf(GameClient c) => h.Server.DisplayNameOf(c.PlayerId)!;
        foreach (var c in all.Where(c => c != human))   // each bot's name is from the pool of the race it plays
        {
            var p = h.Server.World.Players[c.PlayerId];
            Assert.Contains(NameOf(c), PlayerNames.PoolFor(Races.Of(OrkConfig, p.Class, p.Team)));
        }
        Assert.All(marines, c => Assert.Contains(NameOf(c), PlayerNames.SpaceMarines));
        Assert.All(chaos, c => Assert.Contains(NameOf(c), PlayerNames.Chaos));
        Assert.Contains(NameOf(ork), PlayerNames.Orks);
        Assert.Null(h.Server.DisplayNameOf(human.PlayerId));
        var names = all.Where(c => c != human).Select(NameOf).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Every client sees the names in the scoreboard (and the human as P<id>).
        foreach (var viewer in all)
        {
            Assert.NotNull(viewer.Scoreboard);
            foreach (var c in all) Assert.Equal(c == human ? $"P{human.PlayerId}" : NameOf(c), viewer.NameOf(c.PlayerId));
        }
        Assert.Equal($"player {ork.PlayerId} ({NameOf(ork)})", h.Server.Describe(ork.PlayerId));
        Assert.Contains(log, l => l.Contains($"player {ork.PlayerId} is a test bot: named \"{NameOf(ork)}\" (Orks)"));
        Assert.Contains(log, l => l.Contains($"player {marines[0].PlayerId} ({NameOf(marines[0])}) joined"));
        output.WriteLine(string.Join("\n", all.Select(c => $"P{c.PlayerId} {h.Server.World.Players[c.PlayerId].Team}: {human.NameOf(c.PlayerId)}")));

        // The same seed and join order: the same names. Another seed: others.
        var again = Match(4242, out _);
        var same = new List<GameClient>();
        for (int i = 0; i < 3; i++) same.Add(Bot(again, (uint)(100 + i), Team.SpaceMarines, CustomFixture.SmTac));
        for (int i = 0; i < 3; i++) same.Add(Bot(again, (uint)(200 + i), Team.Chaos, CustomFixture.CsmTac));
        same.Add(Bot(again, 300, Team.Chaos, 4));
        again.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(names, same.Select(c => again.Server.DisplayNameOf(c.PlayerId)!));
        var other = Match(7, out _);
        var others = Enumerable.Range(0, 3).Select(i => Bot(other, (uint)(100 + i), Team.SpaceMarines, CustomFixture.SmTac)).ToList();
        other.Run(TimeSpan.FromSeconds(3));
        Assert.NotEqual(names.Take(3), others.Select(c => other.Server.DisplayNameOf(c.PlayerId)!));
    }

    [Fact]
    public void A_name_is_not_reused_while_its_player_is_connected()
    {
        var h = Match(1, out _);
        var bots = Enumerable.Range(0, Protocol.MaxPlayers).Select(i => Bot(h, (uint)(100 + i), Team.SpaceMarines, CustomFixture.SmTac)).ToList();
        h.Run(TimeSpan.FromSeconds(3));
        var connected = bots.Where(c => c.Status == ClientStatus.Connected).ToList();
        Assert.Equal(Protocol.MaxPlayers, connected.Count);
        var names = connected.Select(c => h.Server.DisplayNameOf(c.PlayerId)!).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // A future name source cannot take a connected player's name; a free one is set (sanitised) and travels.
        Assert.False(h.Server.SetDisplayName(connected[0].PlayerId, names[1].ToUpperInvariant()));
        Assert.True(h.Server.SetDisplayName(connected[0].PlayerId, "  Sir\tTest [x] "));
        Assert.Equal("Sir Test x", h.Server.DisplayNameOf(connected[0].PlayerId));
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal("Sir Test x", connected[1].NameOf(connected[0].PlayerId));
        Assert.True(h.Server.SetDisplayName(connected[0].PlayerId, null));
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal($"P{connected[0].PlayerId}", connected[1].NameOf(connected[0].PlayerId));
    }

    [Fact]
    public void The_seed_option_parses()
    {
        Assert.True(ServerCommandLine.TryParse(["--name-seed", "4242"], out var a, out _));
        Assert.Equal(4242u, a.NameSeed);
        Assert.True(ServerCommandLine.TryParse([], out var d, out _));
        Assert.Null(d.NameSeed);
        Assert.False(ServerCommandLine.TryParse(["--name-seed", "-1"], out _, out _));
        Assert.False(ServerCommandLine.TryParse(["--name-seed", "x"], out _, out _));
        Assert.Contains("--name-seed", ServerCommandLine.Usage);
        Assert.Equal(17u, new GameServer(new SimConfig(), 1, new LoopbackNetwork(new ManualClock(), LinkConditions.Perfect).Server, new ManualClock(), new ServerOptions { NameSeed = 17 }).NameSeed);
    }
}
