namespace SmLauncher.Core;

/// <summary>
/// hotfix/old-gpu: the launcher's "Old GPU / safe mode". Godot has no DirectX 9, so old cards (a GTX 960M laptop) run its Compatibility renderer (OpenGL 3.3).
/// This holds the rules: which engine and game options the checkbox adds, and when a game that dies at once turns the mode on by itself (one retry, no loop).
/// </summary>
public static class SafeModePolicy
{
    /// <summary>Godot's options (before "--"): the Compatibility renderer on the native OpenGL 3.3 driver.</summary>
    public static readonly string[] EngineArgs = ["--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3"];

    /// <summary>The client's option (after "--"): low lighting, minimum quality (client/SafeMode.cs).</summary>
    public const string GameArg = "--safe-mode";

    /// <summary>A game that exits with a failure within this long after it started counts as "does not run on this card".</summary>
    public static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(8);

    /// <summary>The environment variable "2 - Play.cmd" reads for Godot's own options.</summary>
    public const string EngineArgsVariable = "SM_ENGINE_ARGS";

    /// <summary>The text for <see cref="EngineArgsVariable"/>: <paramref name="existing"/> (a tester's own) followed by the safe-mode options when <paramref name="safe"/>.</summary>
    public static string? EngineArgsText(string? existing, bool safe)
    {
        if (!safe) return existing;
        string ours = string.Join(' ', EngineArgs);
        return string.IsNullOrWhiteSpace(existing) ? ours : existing.Trim() + " " + ours;
    }

    /// <summary>Extra arguments for the game (after "--").</summary>
    public static string[] ExtraGameArgs(bool safe) => safe ? [GameArg] : [];

    /// <summary>True when the game ended with a failure so soon that the launcher should switch safe mode on and start it once more.</summary>
    public static bool ShouldRetryInSafeMode(bool safeModeWas, bool alreadyRetried, int exitCode, TimeSpan ranFor) =>
        !safeModeWas && !alreadyRetried && exitCode != 0 && ranFor <= EarlyExitWindow;

    /// <summary>The words shown to the tester when the launcher turned safe mode on.</summary>
    public const string AutoOnMessage =
        "The game closed right after it started, so it may not support your graphics card with its normal settings. " +
        "Old GPU / safe mode is now switched on (simpler graphics) and the game is starting again. Untick the box in the launcher to go back.";

    /// <summary>The words shown when the game fails even in safe mode.</summary>
    public const string StillFailsMessage =
        "The game closed right after it started even in Old GPU / safe mode. Update your graphics driver, then use Open folder to find the log files and report the problem.";
}
