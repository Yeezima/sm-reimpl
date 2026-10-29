using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using SmNet;
using SmSim;

namespace SmServer;

/// <summary>The dedicated server's command line (Program.cs), parsed strictly: unknown flags and flags without their value are errors.</summary>
public sealed record ServerArguments
{
    public int Port { get; init; } = 27015;
    /// <summary>The address to listen on: every interface by default (a dedicated server); 127.0.0.1 for test runs.</summary>
    public IPAddress Bind { get; init; } = IPAddress.Any;
    public string? GameDir { get; init; }
    /// <summary>A map name, "none" for the flat placeholder ground, or null for the default (mp_tdm_09 when installed).</summary>
    public string? Map { get; init; }
    public GameMode Mode { get; init; } = GameMode.Annihilation;
    /// <summary>False for --mode ffa (no teams).</summary>
    public bool Teams { get; init; } = true;
    public int? ScoreLimit { get; init; }
    public float? TimeLimitMinutes { get; init; }
    public bool? FriendlyFire { get; init; }
    public bool CustomEmblems { get; init; } = true;
    public UnlockMode Unlocks { get; init; } = UnlockMode.All;
    public int MaxPerIp { get; init; } = ServerOptions.DefaultMaxPlayersPerAddress;
    public TimeSpan JoinInterval { get; init; } = ServerOptions.DefaultJoinInterval;
    public TimeSpan IdleKick { get; init; } = ServerOptions.DefaultIdleKick;
    public string? BanFile { get; init; }
    public bool LimitLoopback { get; init; }
    /// <summary>--countdown: seconds of "MATCH BEGINS" before play (null: <see cref="MatchRules.DefaultCountdown"/>).</summary>
    public float? CountdownSeconds { get; init; }
    /// <summary>--pickup-test (test aid): seconds between test drops in front of every living player; 0 off.</summary>
    public float PickupTestSeconds { get; init; }
    /// <summary>--bot-damage-scale (test aid, owner 2026-10-29: "reduce all bot damage by 66% temporarily for testing purposes"): every hit a test bot deals (shots, projectiles,
    /// blasts, melee, the charge; health and shield) is scaled by this; 1 (default) off. Bots say they are bots in their Hello (protocol 37).</summary>
    public float BotDamageScale { get; init; } = 1f;
    /// <summary>--name-seed (names/bot-lore): the seed of the test bots' names (<see cref="ServerOptions.NameSeed"/>); null: random at start.</summary>
    public uint? NameSeed { get; init; }
    /// <summary>--honor-side-requests (M44a test aid, ours): a requested side is kept up to the per-side limit; the balance applies only to joins that name no side.</summary>
    public bool HonorSideRequests { get; init; }
    /// <summary>--ork-data (Ork O5 test aid): the Ork Boy with the data's values (<c>OrkBalanceMode.Data</c>) instead of the owner's Parity; clients need the same flag (config hash).</summary>
    public bool OrkData { get; init; }
    /// <summary>--content (docs/added-content.md): the added content this server runs (<see cref="ContentProfiles"/>; default <see cref="ContentProfiles.Default"/>); clients need the same (config hash).</summary>
    public ContentExtras Content { get; init; } = ContentProfiles.Default;
    /// <summary>--no-player-collision (sim/player-collision test aid): players pass through each other as before protocol 43 (<see cref="SimConfig.PlayerBodies"/>; config hash: clients need it too).</summary>
    public bool NoPlayerCollision { get; init; }
    /// <summary>--log-overlaps (sim/player-collision test aid): every 10 s, how close the living players' capsules came and how often they overlapped.</summary>
    public bool LogOverlaps { get; init; }
    public bool Help { get; init; }
}

public static class ServerCommandLine
{
    /// <summary>Exit code for a command-line error.</summary>
    public const int UsageError = 2;

    public const string Usage =
        """
        smserver: headless dedicated server (UDP). Reads the game install, never writes to it.
        Options (each takes one value unless marked as a switch):
          --port <1-65535>              UDP port (default 27015)
          --bind <ip>                   listen on this address only (default every interface; 127.0.0.1 for test runs)
          --game-dir <dir>              the game install (default: the located install, SM_GAME_DIR, config/local.json)
          --map <name|none>             multiplayer map (default mp_tdm_09 when installed; none = flat placeholder ground)
          --mode annihilation|seize|ffa match rules (default annihilation)
          --score-limit <1-65535>       score limit (default per mode; provisional)
          --time-limit <minutes>        time limit, 0 = none (provisional)
          --friendly-fire on|off        friendly fire (provisional default off)
          --countdown <seconds>         "MATCH BEGINS" countdown before play, 0 = none (default 5, at most 600; ours)
          --pickup-test <seconds>       test aid: one drop of each type in front of every living player this often
          --bot-damage-scale <f>        test aid: scale all damage dealt by test bots (client --bot) by f, 0 to 1 (default 1 = off; e.g. 0.34 = 66% less)
          --name-seed <0-4294967295>    the seed of the test bots' names (per-race pools, ours; default random, printed at start): the same seed and join order give the same names
          --honor-side-requests         switch: keep the side a client asks for up to the per-side limit (test aid for bot fills; the balance applies only to joins with no side)
          --content <profile>           added content: faithful (SM1 alone), extended (every added content: the Ork, gore) or a list such as orks,gore
                                        (default extended; a build without extras runs faithful only); clients need the same --content
          --ork-data                    switch: test aid: the Ork Boy with the data's values instead of the Tactical parity (clients need --ork-data too)
          --no-player-collision         switch: test aid: players pass through each other as before protocol 43 (clients need --no-player-collision too)
          --log-overlaps                switch: test aid: every 10 s, the closest the living players' capsules came and how many ticks they overlapped
          --unlocks all                 unlock mode (only 'all': no progression yet)
          --no-custom-emblems           switch: no custom emblem is pulled, kept or served
          --max-per-ip <n>              at most n players from one IP address, 0 = no cap (default 4)
          --join-interval <seconds>     at most one new join per IP address this often, 0 = no limit (default 5)
          --idle-kick <seconds>         drop a player without movement, buttons or customizer use for this long while playing, 0 = never (default 300)
          --ban-file <path>             one IP or CIDR range per line ('#' comments); re-read when it changes; banned sources get no reply
          --limit-loopback              switch: apply --max-per-ip, --join-interval and --idle-kick to loopback (127.0.0.1) too;
                                        by default players on this machine (local bot playtests) are exempt
          --help, -h                    switch: this list
        """;

    static readonly HashSet<string> Switches = ["--no-custom-emblems", "--limit-loopback", "--honor-side-requests", "--ork-data", "--no-player-collision", "--log-overlaps", "--help", "-h"];
    static readonly HashSet<string> Valued =
        ["--port", "--bind", "--game-dir", "--map", "--mode", "--score-limit", "--time-limit", "--friendly-fire", "--unlocks", "--countdown", "--pickup-test", "--bot-damage-scale",
         "--name-seed", "--max-per-ip", "--join-interval", "--idle-kick", "--ban-file", "--content"];

    /// <summary>Parses the arguments; false with a one-line <paramref name="error"/> for an unknown flag, a missing or invalid value, or a stray word.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out ServerArguments result, out string error)
    {
        var a = new ServerArguments();
        result = a; error = "";
        for (int i = 0; i < args.Count; i++)
        {
            string flag = args[i];
            if (Switches.Contains(flag))
            {
                a = flag switch
                {
                    "--no-custom-emblems" => a with { CustomEmblems = false },
                    "--limit-loopback" => a with { LimitLoopback = true },
                    "--honor-side-requests" => a with { HonorSideRequests = true },
                    "--ork-data" => a with { OrkData = true },
                    "--no-player-collision" => a with { NoPlayerCollision = true },
                    "--log-overlaps" => a with { LogOverlaps = true },
                    _ => a with { Help = true },
                };
                continue;
            }
            if (!Valued.Contains(flag))
            {
                error = flag.StartsWith('-') ? $"unknown option '{flag}'" : $"unexpected argument '{flag}' (options start with --)";
                return false;
            }
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{flag} needs a value";
                return false;
            }
            string value = args[++i];
            string? bad = null;
            switch (flag)
            {
                case "--port":
                    if (Int(value) is { } port && port is >= 1 and <= 65535) a = a with { Port = port }; else bad = "a port from 1 to 65535";
                    break;
                case "--bind":
                    if (IPAddress.TryParse(value, out var address)) a = a with { Bind = address }; else bad = "an IP address such as 127.0.0.1";
                    break;
                case "--game-dir": a = a with { GameDir = value }; break;
                case "--map": a = a with { Map = value }; break;
                case "--mode":
                    if (value is "annihilation" or "seize" or "ffa")
                        a = a with { Teams = value != "ffa", Mode = value == "seize" ? GameMode.SeizeGround : GameMode.Annihilation };
                    else bad = "annihilation, seize or ffa";
                    break;
                case "--score-limit":
                    if (Int(value) is { } limit && limit is > 0 and <= ushort.MaxValue) a = a with { ScoreLimit = limit }; else bad = "a whole number from 1 to 65535";
                    break;
                case "--time-limit":
                    if (Number(value) is { } minutes && minutes >= 0f && minutes <= 24 * 60) a = a with { TimeLimitMinutes = minutes }; else bad = "minutes from 0 (none) to 1440";
                    break;
                case "--friendly-fire":
                    if (value is "on" or "off") a = a with { FriendlyFire = value == "on" }; else bad = "on or off";
                    break;
                case "--unlocks":
                    if (value == "all") a = a with { Unlocks = UnlockMode.All }; else bad = "'all' (no progression yet)";
                    break;
                case "--max-per-ip":
                    if (Int(value) is { } cap && cap is >= 0 and <= Protocol.MaxPlayers) a = a with { MaxPerIp = cap }; else bad = $"a whole number from 0 (no cap) to {Protocol.MaxPlayers}";
                    break;
                case "--join-interval":
                    if (Number(value) is { } interval && interval is >= 0f and <= 3600f) a = a with { JoinInterval = TimeSpan.FromSeconds(interval) }; else bad = "seconds from 0 (no limit) to 3600";
                    break;
                case "--idle-kick":
                    if (Number(value) is { } idle && idle is >= 0f and <= 86400f) a = a with { IdleKick = TimeSpan.FromSeconds(idle) }; else bad = "seconds from 0 (never) to 86400";
                    break;
                case "--ban-file": a = a with { BanFile = value }; break;
                case "--content":
                    if (!ContentProfiles.TryParse(value, out var extras, out string contentError)) { error = contentError; return false; }
                    a = a with { Content = extras };
                    break;
                case "--countdown":
                    if (Number(value) is { } countdown && countdown >= 0f && countdown <= MatchRules.MaxCountdown) a = a with { CountdownSeconds = countdown };
                    else bad = $"seconds from 0 (none) to {MatchRules.MaxCountdown:0}";
                    break;
                case "--pickup-test":
                    if (Number(value) is { } every && every > 0f && every <= 3600f) a = a with { PickupTestSeconds = every }; else bad = "seconds above 0 (at most 3600)";
                    break;
                case "--name-seed":
                    if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint seed)) a = a with { NameSeed = seed }; else bad = "a whole number from 0 to 4294967295";
                    break;
                case "--bot-damage-scale":
                    if (Number(value) is { } scale && scale >= 0f && scale <= 1f) a = a with { BotDamageScale = scale }; else bad = "a factor from 0 to 1 (1 = off; a bot's flag is its own word, so it may only lower damage)";
                    break;
            }
            if (bad is not null)
            {
                error = $"{flag} '{value}': expected {bad}";
                return false;
            }
        }
        result = a;
        return true;
    }

    static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    static float? Number(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : null;

    /// <summary>
    /// The Steam build of the install, for refusals ("Steam build 7412325"): read (read-only) from the library's
    /// <c>appmanifest_55150.acf</c> two folders above the install, when it is there; null otherwise.
    /// </summary>
    public static string? BuildHint(string install)
    {
        try
        {
            string manifest = Path.GetFullPath(Path.Combine(install, "..", "..", "appmanifest_55150.acf"));
            if (!File.Exists(manifest)) return null;
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var match = Regex.Match(reader.ReadToEnd(), "\"buildid\"\\s+\"(\\d{1,12})\"");
            return match.Success ? $"Steam build {match.Groups[1].Value}" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
