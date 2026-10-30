namespace SmNet;

/// <summary>
/// perf/lean-bots (ours; spec "Test bots"): when the networked client loads only what a test bot's simulation, prediction, navigation and aiming
/// read. A headless bot is seen and heard by nobody, yet it used to load the whole presentation (map meshes and textures, lighting, effects, looks,
/// weapon models, ragdolls, decals: 600 to 700 MB working set each), and the menu's hosting can start up to 15 of them on the player's own PC.
/// Engine-free so it can be tested; the Godot client calls it with its display server's name and its user arguments. The simulation and the
/// protocol never use it.
/// </summary>
public static class LeanBot
{
    /// <summary>The opt-out flag (<c>--bot-full-load</c>): a bot that loads everything (tests that need its visuals).</summary>
    public const string FullLoadFlag = "bot-full-load";

    /// <summary>Godot's name for the display server of a <c>--headless</c> run.</summary>
    public const string HeadlessDisplayServer = "headless";

    /// <summary>
    /// perf/pass-1 (merged into this lean mode): a lean bot's frame cap, the server's 30 Hz tick (a bot samples one input a tick); before, a headless bot ran
    /// uncapped at about 134 frames a second. <c>--bot-fps n</c> overrides it (0 = no cap); Godot's own <c>--max-fps</c> before <c>--</c> wins. Ours.
    /// </summary>
    public const int FrameCap = 30;

    /// <summary>perf/pass-1: a lean bot's engine physics steps a second (Godot's default 60; a lean bot has no ragdolls or physics props, ours).</summary>
    public const int PhysicsTicks = 30;

    /// <summary>The frame-cap override flag (<c>--bot-fps n</c>, 0 = no cap).</summary>
    public const string FpsFlag = "bot-fps";

    /// <summary>The frame cap of a lean bot: <paramref name="overrideText"/> (<c>--bot-fps</c>'s value) when it is a whole number 0 or above, else <see cref="FrameCap"/>.</summary>
    public static int FrameCapFor(string? overrideText) =>
        overrideText is not null && int.TryParse(overrideText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int fps) && fps >= 0 ? fps : FrameCap;

    /// <summary>
    /// Test aids that measure or capture the presentation (animation, camera, looks, sound, frame-time probes, screenshots, demos): a bot started
    /// with any of them loads everything, as before, so headless probe runs keep measuring what they measured (each implies <c>--bot-full-load</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> PresentationAids = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
    {
        "anim-probe", "hip-probe", "yaw-probe", "pan-rate", "transition-probe", "transition-sweep", "camera-probe", "camera-selftest", "motion-probe",
        "frame-profile", "frame-stats", "look-churn", "look-log", "look-closeup", "picker-demo", "picker-on-death", "picker-deploy", "bot-change-loadout",
        "bot-gait", "gait-sweep", "action-sweep", "bot-turn-sweep", "turn-sweep-fire", "latency-probe", "bullet-probe", "show-shot-rays", "camera-hide-log",
        "slash-trace", "run-start-log", "log-sound", "log-footsteps", "reload-capture", "reload-probe", "ork-capture", "screenshot", "screenshot-every",
        "record-demo", "spectate", "ragdoll-debug", "damage-demo", "aim-test", "hud-demo", "hud-rewards-demo", "hud-launcher-demo", "scope-spread-demo", "pound-marker-demo",
        // review round (L2): the remaining probes, logs, captures and sweeps of the client
        "pose-pop-probe", "turn-probe", "turn-probe-events", "turn-probe-trace", "transition-probe-csv", "transition-probe-trace", "anim-probe-csv", "motion-probe-worst",
        "gait-sweep-fire", "ui-sound-probe", "log-fall", "log-falls", "log-upper", "log-sprint-edges", "log-death-fx", "bot-ledge", "ledge-charge",
        "ragdoll", "ragdoll-rest", "ragdoll-delay", "screenshot-at", "screenshot-on-effect", "demo-look", "demo-profile", "demo-no-quit", "play-demo",
        "jump-test", "slash-test", "dash-wall-test", "pound-test", "stomp-test", "log-melee-fx", "laser-test", "shot-test", "reload-test", "ork-test", "emblem-test", "appearance-test", "ui-selftest", "lock-demo",
        // the screenshot waits
        "wait-players", "wait-melee", "wait-blast", "wait-jump", "wait-match-end", "wait-death", "wait-fall", "wait-fall-land", "wait-flinch", "wait-frames", "wait-switch",
        // gamepad: the synthetic controller script (a human client; a bot takes no pad)
        "pad-test",
        // anim/melee-flow: the probe's clip labels, the foot lock's trace, the stun-strike shake's log
        "transition-probe-clips", "clip-lock-trace", "log-stun-shake",
        // fix/stow-right: the switch test view and its probe, the capture window, the bot that switches for the capture
        "switch-test", "switch-probe", "wait-switch-window", "bot-switch-every",
        // fx/character-visuals: the armour flash demo
        "armour-flash-demo",
    };

    /// <summary>
    /// Flags that look like test aids (by <see cref="LooksLikeAid"/>) but only steer or log the bot's simulation-facing behaviour, so a lean bot keeps them
    /// (<c>--bot-trace</c>: one CSV line per input tick).
    /// </summary>
    /// <remarks>perf/pass-1's <c>--perf-probe</c> only reads the engine's and runtime's counters (frame times, renderer, memory, GC), so a lean bot keeps it: it
    /// measures the lean bot itself.</remarks>
    public static readonly IReadOnlySet<string> LeanSafeAids = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "bot-trace", "test-window", "perf-probe" };

    /// <summary>A flag name that reads like a test aid (a probe, log, trace, capture, sweep, test, screenshot, wait or demo): each must be classified (tested).</summary>
    public static bool LooksLikeAid(string name) =>
        name.Contains("probe", System.StringComparison.Ordinal) || name.StartsWith("log-", System.StringComparison.Ordinal) || name.Contains("trace", System.StringComparison.Ordinal)
        || name.Contains("capture", System.StringComparison.Ordinal) || name.Contains("sweep", System.StringComparison.Ordinal) || name.EndsWith("-test", System.StringComparison.Ordinal)
        || name.Contains("selftest", System.StringComparison.Ordinal) || name.StartsWith("screenshot", System.StringComparison.Ordinal) || name.StartsWith("wait-", System.StringComparison.Ordinal)
        || name.Contains("demo", System.StringComparison.Ordinal);

    /// <summary>
    /// True when the presentation-only loads are skipped: the client runs as a test bot (<c>--bot</c>), under the headless display server (nothing is
    /// drawn), and neither <c>--bot-full-load</c> nor a <see cref="PresentationAids">presentation test aid</see> was given. A human client, a windowed
    /// bot (screenshots, recordings) and a demo replay always load everything.
    /// </summary>
    /// <param name="bot">The client runs as a test bot (<c>--bot</c>).</param>
    /// <param name="displayServer">Godot's <c>DisplayServer.GetName()</c>.</param>
    /// <param name="userArgs">The client's user arguments (after Godot's <c>--</c>), flags spelled <c>--name</c>.</param>
    public static bool Decide(bool bot, string? displayServer, IEnumerable<string> userArgs)
    {
        if (!bot || !string.Equals(displayServer, HeadlessDisplayServer, System.StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var arg in userArgs)
        {
            if (!arg.StartsWith("--", System.StringComparison.Ordinal)) continue;
            string name = arg[2..];
            int eq = name.IndexOf('=');
            if (eq >= 0) name = name[..eq];
            if (string.Equals(name, FullLoadFlag, System.StringComparison.OrdinalIgnoreCase) || PresentationAids.Contains(name)) return false;
        }
        return true;
    }
}
