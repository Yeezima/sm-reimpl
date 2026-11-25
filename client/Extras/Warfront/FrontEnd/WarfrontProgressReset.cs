using System;
using System.IO;
using Godot;
using SmClient.Extras.Warfront.Stage;

namespace SmClient.FrontEnd;

/// <summary>
/// RESET PROGRESSION's rules and texts (codebase fix 1; owner decisions 2, 4 and 8), shared by the row in OPTIONS, INTERFACE (owner 2026-11-25: "remove the reset progression button from the warfront menu
/// and put it in the options, interface section. below screen flashes."; <c>OptionsView.WarfrontReset.cs</c>) and the Warfront page's progress note. What it touches is unchanged: only this PC's Warfront
/// progress file (<see cref="WarfrontProgressStore.Reset"/>: the safety copy, a level-1 file with its reset time, the <c>.bak</c> deleted), never the run, the stats, the customizer or any other save.
/// </summary>
public static class WarfrontProgressReset
{
    public const string Row = "Reset progression";
    public const string Value = "RESET";
    public const string Title = "RESET PROGRESSION?";
    public const string Body = "THIS CLEARS YOUR LEVEL, XP AND CARDS: BACK TO LEVEL 1. THE RUN, YOUR STATS AND YOUR TWO RETAINERS STAY. THE OLD PROGRESS IS KEPT AS ONE SAFETY COPY.";   // T0 retainer rules (every player has two retainers), moved here by the front-end tidy
    public const string Hint = "Clears your Warfront level, XP and cards on this PC: back to level 1. The run, your stats and your two retainers stay.";
    public const string RunningHint = "A server of this game is running. Stop it first.";
    public const string NoKeyHint = "Nothing to reset yet: this PC has not played Warfront.";
    public const string NewerHint = "Your progression needs a newer build.";
    public const string UnreadableHint = "Your progress file could not be read. It is left alone.";
    public const string InMatchHint = "Leave the match first: progression is reset from the main menu's Options.";
    public const string Done = "PROGRESSION RESET: BACK TO LEVEL 1";

    /// <summary>Self-test: stands in for "a server of this game runs on this PC" (null: <see cref="HostedServer.Running"/>).</summary>
    public static bool? ServerRunningForTest { get; set; }

    public static bool ServerRunning => ServerRunningForTest ?? HostedServer.Running;

    /// <summary>The key file <c>--warfront-key-file</c> names (null or a following flag: the default key file).</summary>
    public static string? KeyFileArg(string? value) => !string.IsNullOrEmpty(value) && !value.StartsWith("--", StringComparison.Ordinal) ? value : null;

    /// <summary>The key file named on the command line, read the way <c>NetworkView.WarfrontIdentity</c> reads it.</summary>
    public static string? KeyFileFromCommandLine()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--warfront-key-file") return KeyFileArg(args[i + 1]);
        return null;
    }

    /// <summary>This PC's progress file (read now), or null without a key file: nothing can belong to a key that does not exist yet. Never creates a key.</summary>
    public static WarfrontProgressStore? OpenStore(string? keyFileArg)
    {
        string? keyFile = KeyFileArg(keyFileArg);
        string keyPath = keyFile ?? WarfrontClientKey.DefaultPath();
        ulong key;
        try
        {
            if (!File.Exists(keyPath) || !WarfrontClientKey.TryParse(File.ReadAllText(keyPath), out key)) return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var store = new WarfrontProgressStore(WarfrontProgressStore.PathFor(keyFile), key) { Log = GD.Print };
        store.Load();
        return store;
    }

    /// <summary>Why the reset cannot be used now, or null when it can (the order the old plate checked: a running server, no key, a newer file, an unreadable file).</summary>
    public static string? Blocked(bool inMatch, bool serverRunning, WarfrontProgressStore? store) =>
        inMatch ? InMatchHint : serverRunning ? RunningHint : store is null ? NoKeyHint : store.Newer ? NewerHint : store.Unreadable ? UnreadableHint : null;

    /// <summary>Yes was pressed: the reset itself. Returns null on success, else the reason in a sentence (nothing was written).</summary>
    public static string? ResetNow(string? keyFileArg)
    {
        if (ServerRunning) return RunningHint;   // a campaign started meanwhile: the host would have the old progression
        var store = OpenStore(keyFileArg);
        if (store is null || !store.Reset(DateTime.UtcNow))
            return store?.LastProblem is { Length: > 0 } p ? char.ToUpperInvariant(p[0]) + p[1..] + "." : NoKeyHint;
        GD.Print("warfront: progression reset on this PC (safety copy kept)");
        return null;
    }

    /// <summary>After the dialog's own first focus: No is the default answer.</summary>
    public static void FocusNo(Control? dialog)
    {
        Callable.From(() =>
        {
            if (dialog is null || !GodotObject.IsInstanceValid(dialog) || !dialog.IsInsideTree()) return;
            foreach (var n in dialog.FindChildren("*", "Button", true, false))
                if (n is Button b && b.Text.Equals("No", StringComparison.OrdinalIgnoreCase)) { b.GrabFocus(); return; }
        }).CallDeferred();
    }
}
