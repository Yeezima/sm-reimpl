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
    /// <summary>--custom-map (Mapmaker, added content; docs/mapmaker.md "Playing a map"): a `.smmap` file to play instead of an install map (the server names it cm_... on the wire; clients find the file by that name).</summary>
    public string? CustomMap { get; init; }
    /// <summary>--maps (classic-maps-all): a rotation, "all" (every playable competitive map the install lists) or a comma list of map names; the server plays them in turn, one match each (<see cref="RotationChild"/>).</summary>
    public IReadOnlyList<string>? Maps { get; init; }
    /// <summary>--rotation-child (internal): this process is one match's server under a rotation supervisor (Program.cs): it exits with <see cref="ServerCommandLine.RotationExit"/> a second before the next match would start.</summary>
    public bool RotationChild { get; init; }
    /// <summary>--dev-supervised (internal; devtools-map-pick): this process is a <c>--dev-tools</c> server under its supervisor (RotationSupervisor.RunDev): the tools' map pick makes it exit with <see cref="ServerCommandLine.RotationExit"/> so the supervisor starts the picked map.</summary>
    public bool DevSupervised { get; init; }
    /// <summary>--weather (weather W3, added content): the Classic PvP match's weather, a preset key or <c>random</c> (see <c>smserver --help</c>); null: none given, None. Only the extended profile has weather: any other profile ignores it with a line in the log. Checked against the catalog in Program.cs (this class never names added content).</summary>
    public string? Weather { get; init; }
    /// <summary>--weather-seed (internal; the rotation's supervisor passes it): the seed Random draws from; null: random at start, printed.</summary>
    public uint? WeatherSeed { get; init; }
    /// <summary>--weather-draw (internal; the rotation's supervisor passes it): how many map loads came before this one, so each map's Random draw differs from the last (0: the first).</summary>
    public int WeatherDraw { get; init; }
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
    /// <summary>--snapshot-rate (net/responsive): snapshots a second, 10 to 30 (codebase fix 5; default <see cref="ServerOptions.SnapshotRate"/>, 30; 20 restores the rate before).</summary>
    public int? SnapshotRate { get; init; }
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
    /// <summary>anim/melee-interaction (test aid, before / after): the M29 charge contact and stop, no lock (config hash; clients need it too).</summary>
    public bool NoChargeLock { get; init; }
    /// <summary>fix/charge-player-collision (test aid, before / after): the protocol 56 body and charge contact rules (<see cref="SimConfig.SolidBodies"/>; config hash: clients need it too).</summary>
    public bool OldBodyContact { get; init; }
    /// <summary>feel/charge-clash (test aid, before / after): the protocol 58 stop, no clash (<see cref="SimConfig.ChargeClash"/>; config hash: clients need it too).</summary>
    public bool NoChargeClash { get; init; }
    /// <summary>charge-cancel (restore switch, before / after): the protocol 65 charge cancel: a Grenade press ends the charge, S does not (<see cref="SimConfig.ChargeBackCancel"/>; config hash: clients need it too).</summary>
    public bool OldChargeCancel { get; init; }
    /// <summary>charge-ally-bump (restore switch, before / after): the protocol 65 collision stop on an ally (<see cref="SimConfig.ChargeAllyBump"/>; config hash: clients need it too).</summary>
    public bool OldAllyBump { get; init; }
    /// <summary>charge-responsive (restore switch): a charge waits for a melee attack under way to end (<see cref="SimConfig.ChargeAfterMelee"/>; config hash: clients need it too).</summary>
    public bool OldChargeAfterMelee { get; init; }
    /// <summary>charge-responsive (restore switch): the S cancel waits for the rush's stick branch (<see cref="SimConfig.ChargeInstantCancel"/>; config hash: clients need it too).</summary>
    public bool OldChargeCancelDelay { get; init; }
    /// <summary>charge-melee-chain (restore switch): the protocol 69 charge and melee rules (<see cref="SimConfig.ChargeMeleeChain"/>; config hash: clients need it too).</summary>
    public bool OldChargeMeleeChain { get; init; }
    /// <summary>sim/hop-rules (restore switch): the protocol 71 jump and hop rules (<see cref="SimConfig.HopRules"/>; config hash: clients need it too).</summary>
    public bool OldHopRules { get; init; }
    public bool OldLedgeHop { get; init; }
    /// <summary>sim/ledge-charge-air (restore switch): a Classic PvP ledge charge falls under normal gravity (protocol 89), not 2 x (protocol 92; <see cref="SimConfig.LedgeChargeHalfAir"/>; config hash: clients need it too).</summary>
    public bool OldLedgeChargeAir { get; init; }
    /// <summary>sim/stomp-release (restore switch): the stomp locks the player to the end of its clip (<see cref="SimConfig.StompRelease"/>; config hash: clients need it too).</summary>
    public bool OldStompRelease { get; init; }
    /// <summary>sim/stab-release (restore switch): the stab locks the player to the end of its clip (<see cref="SimConfig.StabRelease"/>; config hash: clients need it too).</summary>
    public bool OldStabRelease { get; init; }
    /// <summary>sim/kick-push (restore switch): the heavy classes' kick pushes with the data's full knockback (<see cref="SimConfig.KickPushCut"/>; config hash: clients need it too).</summary>
    public bool OldKickPush { get; init; }
    /// <summary>sim/sprint-melee-charge (restore switch): a light press while sprinting swings (<see cref="SimConfig.SprintMeleeCharge"/>; config hash: clients need it too).</summary>
    public bool OldSprintMelee { get; init; }
    /// <summary>sim/ledge-charge-cancel (restore switch): a light press keeps a ledge charge's momentum (<see cref="SimConfig.LedgeChargeCancel"/>; config hash: clients need it too).</summary>
    public bool OldLedgeCancel { get; init; }
    /// <summary>sim/lascannon-tap-reload (restore switch): the lascannon's full reload (<see cref="SimConfig.LascannonTapReload"/>; config hash: clients need it too).</summary>
    public bool OldLascannonReload { get; init; }
    /// <summary>sim/lascannon-shot-first (restore switch): the hop starts at once (<see cref="SimConfig.LascannonShotFirst"/>; config hash: clients need it too).</summary>
    public bool OldLascannonShotFirst { get; init; }
    /// <summary>sim/plasma-cannon-auto-release (restore switch): the cannon holds its charge until release (<see cref="SimConfig.PlasmaCannonAutoRelease"/>; config hash: clients need it too).</summary>
    public bool OldCannonHold { get; init; }
    /// <summary>musou/melee-spam (restore switch): Musou Mode keeps the melee waits (<see cref="SimConfig.MusouMeleeCooldown"/>; config hash: clients need it too).</summary>
    public bool MusouMeleeCooldown { get; init; }
    /// <summary>musou/melee-heal-cap (restore switch): no cap of 3 heals per melee swing in Musou Mode (<see cref="SimConfig.MusouUncappedMeleeHeal"/>; config hash: clients need it too).</summary>
    public bool MusouUncappedMeleeHeal { get; init; }
    /// <summary>stims-classic (restore switch): the earlier 9-tick stim press and the hit cancel before the release (<see cref="SimConfig.StimReliable"/>; config hash: clients need it too).</summary>
    public bool OldStimUse { get; init; }
    /// <summary>musou/no-aerial-tackle (restore switch): Musou Mode keeps the Assault's and Raptor's aerial tackle (SimConfig.AerialTackleRestored, added content; config hash: clients need it too).</summary>
    public bool AerialTackleRestored { get; init; }
    /// <summary>musou/ork-swing-commit (restore switch): a melee Ork's wind-up faces its target every tick up to the blow, as before 2026-11-12 (SimConfig.MusouOrkSwingTracking, added content; config hash: clients need it too).</summary>
    public bool MusouOrkSwingTracking { get; init; }
    /// <summary>remove-spear-claw (restore switch, owner 2026-11-23): Musou Mode and Warfront offer the lightning claw and the power spear again (ArsenalRules.SpearClawOffered; config hash: clients need it too).</summary>
    public bool OldArsenalSpearClaw { get; init; }
    /// <summary>--log-overlaps (sim/player-collision test aid): every 10 s, how close the living players' capsules came and how often they overlapped.</summary>
    public bool LogOverlaps { get; init; }
    /// <summary>--max-spectators (protocol 46; ours, observer tooling): spectators admitted besides the players, 0 none (default <see cref="Protocol.MaxSpectators"/>).</summary>
    public int MaxSpectators { get; init; } = Protocol.MaxSpectators;
    /// <summary>--dev-tools (protocol 80; the owner 2026-11-17): the in-game developer tools (invincible, frozen bots, infinite ammunition and jump pack, adding and removing bots). Obeyed only when --bind is a loopback address and the request comes from one; otherwise ignored with a line in the log.</summary>
    public bool DevTools { get; init; }
    /// <summary>--dev-bot-exe: the Godot console executable the tools' bots run (default: the one the repository's tools download).</summary>
    public string? DevBotExe { get; init; }
    /// <summary>--cancel-table FILE (cancel-editor, protocol 80): start with this cancel table (an export of the F8 panel) instead of the shipped default. Only with --dev-tools on a loopback bind; ignored otherwise, so a public server always plays by the shipped table.</summary>
    public string? CancelTableFile { get; init; }
    /// <summary>--dev-bot-project: the Godot project folder for those bots (default: the client folder of the checkout the server runs from; not needed for an exported game).</summary>
    public string? DevBotProject { get; init; }
    /// <summary>--warfront-stage (Warfront, added content): this process is one stage child under the Warfront supervisor (one deployment's hub and battlefield; it exits with <see cref="ServerCommandLine.WarfrontStageExit"/> when the next stage is due).</summary>
    public bool WarfrontStage { get; init; }
    /// <summary>--warfront-new (Warfront): start a new run; an unfinished one is archived as abandoned.</summary>
    public bool WarfrontNew { get; init; }
    /// <summary>--warfront-max-humans (Warfront): humans admitted, 1 to 4 (default 4).</summary>
    public int? WarfrontMaxHumans { get; init; }
    /// <summary>--warfront-faction (Warfront, Chaos Warfront C1): the enemy army of the next new run, <c>alternate</c> (null, the default: Orks and Chaos take turns, Orks first), <c>orks</c> or <c>chaos</c>. A run in progress keeps its army; the supervisor's stage children inherit the flag.</summary>
    public string? WarfrontFaction { get; init; }
    /// <summary>The keys <c>--warfront-faction</c> takes (the same list as the Warfront code's <c>WarfrontFactionPolicy.Keys</c>; a test holds them equal).</summary>
    public static readonly string[] WarfrontFactionKeys = ["alternate", "orks", "chaos"];
    /// <summary>--warfront-dedicated (Warfront, added content): the always-on shared campaign of the project's server: implies --content warfront and --warfront-max-humans 4 (unless given), refuses --warfront-new, and the supervisor restarts a crashed stage, locks the run folder and stops on SIGTERM (docs/warfront.md "Dedicated mode").</summary>
    public bool WarfrontDedicated { get; init; }
    /// <summary>--warfront-prebuild (Warfront, dedicated only): this process is the supervisor's map warm-up child: it builds every Warfront map's layout and walking graph into the cache, one at a time, and exits 0 (docs/warfront.md "Map warm-up").</summary>
    public bool WarfrontPrebuild { get; init; }
    public bool Help { get; init; }
}

public static class ServerCommandLine
{
    /// <summary>Exit code for a command-line error.</summary>
    public const int UsageError = 2;
    /// <summary>Exit code of a rotation child: its match is over, the supervisor starts the next map.</summary>
    public const int RotationExit = 11;
    /// <summary>Exit code of a Warfront stage child: the next stage is due, the Warfront supervisor starts it.</summary>
    public const int WarfrontStageExit = 12;

    public const string Usage =
        """
        smserver: headless dedicated server (UDP). Reads the game install, never writes to it.
        Options (each takes one value unless marked as a switch):
          --port <1-65535>              UDP port (default 27015)
          --bind <ip>                   listen on this address only (default every interface; 127.0.0.1 for test runs)
          --game-dir <dir>              the game install (default: the located install, SM_GAME_DIR, config/local.json)
          --maps all|<a,b,c>            a rotation: every competitive map the install lists (all) or those named, one match each, in order and round again (the server restarts itself between maps; clients rejoin at once); not with --map
          --map <name|none>             multiplayer map (default mp_tdm_09 when installed; none = flat placeholder ground; --content musou: tfw_war, Ashfall Citadel, generated; tfw_war_m09 restores the old Manufactorum war; both need --content musou)
          --custom-map <file.smmap>     play a Mapmaker map (added content; not with --map or --maps; clients need the same file, found by name in their maps folder, or --custom-map)
          --mode annihilation|seize|ffa match rules (default annihilation)
          --score-limit <1-65535>       score limit (default per mode; provisional)
          --time-limit <minutes>        time limit, 0 = none (provisional)
          --friendly-fire on|off        friendly fire (provisional default off)
          --countdown <seconds>         "MATCH BEGINS" countdown before play, 0 = none (default 5, at most 600; ours)
          --pickup-test <seconds>       test aid: one drop of each type in front of every living player this often
          --bot-damage-scale <f>        test aid: scale all damage dealt by test bots (client --bot) by f, 0 to 1 (default 1 = off; e.g. 0.34 = 66% less)
          --snapshot-rate <hz>          snapshots a second to every client, 10 to 30 (default 30, every tick; 20 = the rate before net/responsive; below 10 some explosions would be missed)
          --name-seed <0-4294967295>    the seed of the test bots' names (per-race pools, ours; default random, printed at start): the same seed and join order give the same names
          --dev-tools                   switch: test aid: the in-game developer tools (F6 in the client: invincible, freeze bots, infinite ammunition, infinite jump pack, add / remove a bot per side);
                                        obeyed only with --bind on a loopback address (127.0.0.1) and for requests from loopback, so a public server ignores it (clients need protocol 80 as always)
          --dev-bot-exe <file>          the Godot console executable the tools' bots run (default: the one godot.cmd / client.cmd downloaded)
          --dev-bot-project <dir>       the Godot project folder for them (default: the client folder of the checkout this server runs from)
          --honor-side-requests         switch: keep the side a client asks for up to the per-side limit (test aid for bot fills; the balance applies only to joins with no side)
          --content <profile>           added content: faithful (SM1 alone), extended (every added content: the Ork, the barks, the autocannon, weather), musou (the
                                        experimental Musou Mode, alone; Annihilation rules only) or a list such as orks,autocannon
                                        (default extended; a build without extras runs faithful only); clients need the same --content
          --weather <preset>            weather for Classic PvP (added content: needs the weather flag, --content weather or extended; none of it in faithful, Musou or Warfront): none (default), clear, low_fog, rain, thunderstorm, ash_squall, ember_wind or random
                                        (random draws a preset per map load, may draw none, never the same twice in a row, from a seed printed at start). Presentation only: it follows the match clock (builds up, peaks mid-match, eases before the end, restarts every round and map).
                                        A server with no --weather is weather capable but shows nothing; the developer tools (F6) can still preview a preset on a loopback server. Clients need no flag.
          --ork-data                    switch: test aid: the Ork Boy with the data's values instead of the Tactical parity (clients need --ork-data too)
          --no-player-collision         switch: test aid: players pass through each other as before protocol 43 (clients need --no-player-collision too)
          --no-charge-lock              switch: test aid: the charge contact and stop as before anim/melee-interaction (clients need --no-charge-lock too)
          --old-body-contact            switch: test aid: no guard against bodies passing through each other, the charge contact and stop as in protocol 56 (clients need --old-body-contact too)
          --old-charge-cancel           switch: restore: a Grenade press ends the charge from its branch and S does not, as in protocol 65 (clients need --old-charge-cancel too)
          --old-stim-use                switch: restore: a stim press is kept 9 ticks and a hit before its release cancels the use (clients need --old-stim-use too)
          --old-charge-after-melee      switch: restore: a charge waits for a melee attack under way to end (clients need --old-charge-after-melee too)
          --old-charge-cancel-delay     switch: restore: S cancels the charge only from the rush's stick branch, about 0.47 s in (clients need --old-charge-cancel-delay too)
          --old-charge-melee-chain     switch: restore: no instant melee cancel of a charge and no spacing reset by a melee attack (clients need --old-charge-melee-chain too)
          --old-sprint-melee           switch: restore: a light press while sprinting swings instead of charging (clients need --old-sprint-melee too)
          --old-ledge-hop              switch: restore: a hop out of a ledge charge drops to the hop's own start speed (clients need --old-ledge-hop too)
          --old-ledge-charge-air       switch: restore: a Classic PvP charge off a ledge falls at normal gravity, full distance, not 2 x and about 0.71 (clients need --old-ledge-charge-air too)
          --old-ledge-cancel           switch: restore: a light press keeps a ledge charge's momentum (clients need --old-ledge-cancel too)
          --old-hop-rules              switch: restore: no ledge jump after a charge, no shortening of the melee wait by a hop, no hop out of the aerial tackle (clients need --old-hop-rules too)
          --old-stomp-release          switch: restore: the Devastator's and Havoc's stomp locks them to the end of its clip instead of 1.2 s (clients need --old-stomp-release too)
          --old-stab-release           switch: restore: the power sword's stab (and Musou Mode's and Warfront's X,F thrust) locks the player to the end of its clip (clients need --old-stab-release too)
          --musou-melee-cooldown       switch: restore: Musou Mode melee keeps the combo waits instead of spam (clients need --musou-melee-cooldown too)
          --musou-uncapped-melee-heal  switch: restore: Musou Mode melee heals for every enemy struck, no cap of 3 per swing (clients need --musou-uncapped-melee-heal too)
          --old-ally-bump               switch: restore: a charge that meets an ally plays the collision stop, as in protocol 65 (clients need --old-ally-bump too)
          --log-overlaps                switch: test aid: every 10 s, the closest the living players' capsules came and how many ticks they overlapped
          --unlocks all                 unlock mode (only 'all': no progression yet)
          --no-custom-emblems           switch: no custom emblem is pulled, kept or served
          --max-spectators <n>          spectators (client.cmd --observe) admitted besides the players, 0 = none (default 4, at most 4); they take no player slot, team or score
          --max-per-ip <n>              at most n players from one IP address, 0 = no cap (default 4)
          --join-interval <seconds>     at most one new join per IP address this often, 0 = no limit (default 5)
          --idle-kick <seconds>         drop a player without movement, buttons or customizer use for this long while playing, 0 = never (default 300)
          --ban-file <path>             one IP or CIDR range per line ('#' comments); re-read when it changes; banned sources get no reply
          --limit-loopback              switch: apply --max-per-ip, --join-interval and --idle-kick to loopback (127.0.0.1) too;
                                        by default players on this machine (local bot playtests) are exempt
          --musou-aerial-tackle         switch: restore: Musou Mode keeps the Assault / Raptor aerial tackle (clients need it too)
          --warfront-stage              switch: (--content warfront) this process is one stage child of the Warfront supervisor (internal)
          --warfront-new                switch: (--content warfront) start a new campaign run; an unfinished one is archived as abandoned
          --warfront-max-humans <1-4>   (--content warfront) humans admitted (default 4; 1 for a local campaign)
          --warfront-faction <f>        (--content warfront) the enemy of the next new campaign: alternate (default; Orks first, then
                                        turn about), orks or chaos; a campaign in progress keeps its own
          --warfront-dedicated          switch: the always-on shared Warfront campaign (implies --content warfront; up to 4 humans unless --warfront-max-humans; refuses --warfront-new; the supervisor restarts a crashed stage, locks the run folder, stops on SIGTERM)
          --warfront-prebuild           switch: (--warfront-dedicated; started by the supervisor) build every Warfront map's cache at low priority, one map at a time, then exit
          --old-arsenal-spear-claw      switch: restore: Musou Mode and Warfront offer the lightning claw and the power spear again (clients need it too)
          --help, -h                    switch: this list
        """;

    static readonly HashSet<string> Switches = ["--dev-tools", "--rotation-child", "--dev-supervised", "--musou-aerial-tackle", "--no-custom-emblems", "--limit-loopback", "--honor-side-requests", "--ork-data", "--no-player-collision", "--no-charge-lock", "--old-body-contact", "--old-charge-cancel", "--old-ally-bump", "--old-stim-use", "--log-overlaps", "--help", "--old-charge-after-melee", "--old-charge-cancel-delay", "--old-charge-melee-chain", "--old-hop-rules", "--old-stab-release", "--old-ledge-hop", "--old-ledge-charge-air", "--musou-melee-cooldown", "--musou-uncapped-melee-heal", "--musou-ork-swing-tracking", "--warfront-stage", "--warfront-new", "--warfront-dedicated", "--warfront-prebuild", "--old-arsenal-spear-claw"];
    static readonly HashSet<string> Valued =
        ["--port", "--bind", "--game-dir", "--map", "--custom-map", "--maps", "--mode", "--score-limit", "--time-limit", "--friendly-fire", "--unlocks", "--countdown", "--pickup-test", "--bot-damage-scale", "--snapshot-rate",
         "--name-seed", "--weather", "--weather-seed", "--weather-draw", "--dev-bot-exe", "--dev-bot-project", "--max-per-ip", "--join-interval", "--idle-kick", "--ban-file", "--content", "--max-spectators", "--warfront-max-humans", "--warfront-faction"];

    /// <summary>Parses the arguments; false with a one-line <paramref name="error"/> for an unknown flag, a missing or invalid value, or a stray word.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out ServerArguments result, out string error)
    {
        var a = new ServerArguments();
        result = a; error = "";
        bool contentGiven = false;   // --warfront-dedicated: without --content it runs the Warfront profile the clients join with
        for (int i = 0; i < args.Count; i++)
        {
            string flag = args[i];
            if (Switches.Contains(flag))
            {
                a = flag switch
                {
                    "--dev-tools" => a with { DevTools = true },
                    "--rotation-child" => a with { RotationChild = true },
                    "--dev-supervised" => a with { DevSupervised = true },
                    "--no-custom-emblems" => a with { CustomEmblems = false },
                    "--limit-loopback" => a with { LimitLoopback = true },
                    "--honor-side-requests" => a with { HonorSideRequests = true },
                    "--ork-data" => a with { OrkData = true },
                    "--no-player-collision" => a with { NoPlayerCollision = true },
                    "--no-charge-lock" => a with { NoChargeLock = true },
                    "--old-body-contact" => a with { OldBodyContact = true },
                    "--no-charge-clash" => a with { NoChargeClash = true },
                    "--old-charge-cancel" => a with { OldChargeCancel = true },
                    "--old-ally-bump" => a with { OldAllyBump = true },
                    "--old-charge-after-melee" => a with { OldChargeAfterMelee = true },
                    "--old-charge-cancel-delay" => a with { OldChargeCancelDelay = true },
                    "--old-charge-melee-chain" => a with { OldChargeMeleeChain = true },
                    "--old-hop-rules" => a with { OldHopRules = true },
                    "--old-ledge-hop" => a with { OldLedgeHop = true },
                    "--old-ledge-charge-air" => a with { OldLedgeChargeAir = true },
                    "--old-stomp-release" => a with { OldStompRelease = true },
                    "--old-stab-release" => a with { OldStabRelease = true },
                    "--old-kick-push" => a with { OldKickPush = true },
                    "--old-sprint-melee" => a with { OldSprintMelee = true },
                    "--old-ledge-cancel" => a with { OldLedgeCancel = true },
                    "--old-lascannon-reload" => a with { OldLascannonReload = true },
                    "--old-lascannon-shot-first" => a with { OldLascannonShotFirst = true },
                    "--old-cannon-hold" => a with { OldCannonHold = true },
                    "--musou-melee-cooldown" => a with { MusouMeleeCooldown = true },
                    "--musou-uncapped-melee-heal" => a with { MusouUncappedMeleeHeal = true },
                    "--old-stim-use" => a with { OldStimUse = true },
                    "--musou-aerial-tackle" => a with { AerialTackleRestored = true },
                    "--musou-ork-swing-tracking" => a with { MusouOrkSwingTracking = true },
                    "--log-overlaps" => a with { LogOverlaps = true },
                    "--warfront-stage" => a with { WarfrontStage = true },
                    "--warfront-new" => a with { WarfrontNew = true },
                    "--warfront-dedicated" => a with { WarfrontDedicated = true },
                    "--warfront-prebuild" => a with { WarfrontPrebuild = true },
                    "--old-arsenal-spear-claw" => a with { OldArsenalSpearClaw = true },
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
                case "--dev-bot-exe": a = a with { DevBotExe = value }; break;
                case "--weather": a = a with { Weather = value }; break;   // checked against the catalog in Program.cs (the extended profile's weather; ignored by any other)
                case "--weather-seed":
                    if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint weatherSeed)) a = a with { WeatherSeed = weatherSeed }; else bad = "a whole number from 0 to 4294967295";
                    break;
                case "--weather-draw":
                    if (Int(value) is { } weatherDraw && weatherDraw is >= 0 and <= 1_000_000) a = a with { WeatherDraw = weatherDraw }; else bad = "a whole number from 0 to 1000000";
                    break;
                case "--cancel-table": a = a with { CancelTableFile = value }; break;
                case "--dev-bot-project": a = a with { DevBotProject = value }; break;
                case "--map": a = a with { Map = value }; break;
                case "--custom-map": a = a with { CustomMap = value }; break;
                case "--maps": a = a with { Maps = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }; break;
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
                    if (Int(value) is { } cap && cap is >= 0 and <= Protocol.MaxMatchPlayers) a = a with { MaxPerIp = cap }; else bad = $"a whole number from 0 (no cap) to {Protocol.MaxMatchPlayers}";
                    break;
                case "--max-spectators":
                    if (Int(value) is { } spectators && spectators is >= 0 and <= Protocol.MaxSpectators) a = a with { MaxSpectators = spectators }; else bad = $"a whole number from 0 (none) to {Protocol.MaxSpectators}";
                    break;
                case "--join-interval":
                    if (Number(value) is { } interval && interval is >= 0f and <= 3600f) a = a with { JoinInterval = TimeSpan.FromSeconds(interval) }; else bad = "seconds from 0 (no limit) to 3600";
                    break;
                case "--idle-kick":
                    if (Number(value) is { } idle && idle is >= 0f and <= 86400f) a = a with { IdleKick = TimeSpan.FromSeconds(idle) }; else bad = "seconds from 0 (never) to 86400";
                    break;
                case "--ban-file": a = a with { BanFile = value }; break;
                case "--warfront-max-humans":
                    if (Int(value) is { } humans && humans is >= 1 and <= 4) a = a with { WarfrontMaxHumans = humans }; else bad = "a whole number from 1 to 4";
                    break;
                case "--warfront-faction":
                    if (Array.IndexOf(ServerArguments.WarfrontFactionKeys, value.Trim().ToLowerInvariant()) is int fi and >= 0) a = a with { WarfrontFaction = ServerArguments.WarfrontFactionKeys[fi] }; else bad = "alternate, orks or chaos";
                    break;
                case "--content":
                    if (!ContentProfiles.TryParse(value, out var extras, out string contentError)) { error = contentError; return false; }
                    a = a with { Content = extras }; contentGiven = true;
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
                case "--snapshot-rate":
                    if (Int(value) is { } rate && rate >= ServerOptions.MinSnapshotRate && rate <= 30) a = a with { SnapshotRate = rate }; else bad = $"a whole number from {ServerOptions.MinSnapshotRate} to 30 (snapshots a second; below {ServerOptions.MinSnapshotRate} some explosions would never reach the players)";
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
        if (a.WarfrontDedicated)
        {
            if (a.WarfrontNew) { error = "--warfront-dedicated refuses --warfront-new (the shared campaign is never restarted by a command line)"; return false; }
            // Without --content: the Warfront profile (ContentProfiles.WarfrontProfile), the one the client's ONLINE WARFRONT row joins with. Before 2026-11-20 this
            // added Warfront to the default (extended) profile, so the VM's config hash never matched a client and every join was refused (ConfigMismatch).
            // executions X1: a given --content (musou) gains the whole Warfront profile, executions included, so it still matches the clients' profile.
            // executions X3 (X1 review): only musou or warfront may be given; any other content (extended, a flag list) would make a superset profile no client joins with.
            if (contentGiven && (a.Content & ~ContentProfiles.WarfrontProfile) != ContentExtras.None) { error = "--warfront-dedicated takes --content musou or warfront only (or none)"; return false; }
            a = a with { Content = contentGiven ? a.Content | ContentProfiles.WarfrontProfile : ContentProfiles.WarfrontProfile, WarfrontMaxHumans = a.WarfrontMaxHumans ?? 4 };
        }
        if (a.WarfrontPrebuild && (!a.WarfrontDedicated || a.WarfrontStage)) { error = "--warfront-prebuild is the dedicated supervisor's warm-up child: it needs --warfront-dedicated and is not a stage"; return false; }
        if ((a.WarfrontStage || a.WarfrontNew || a.WarfrontMaxHumans is not null || a.WarfrontFaction is not null) && !a.Content.HasFlag(ContentExtras.Warfront))
        {
            error = "--warfront-stage, --warfront-new, --warfront-max-humans and --warfront-faction need --content warfront";
            return false;
        }
        if (a.Maps is not null && a.Content.HasFlag(ContentExtras.Warfront))
        {
            error = "--maps is Classic PvP only; Warfront runs its own stages (leave --maps out with --content warfront)";
            return false;
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
