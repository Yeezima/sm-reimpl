using System;
using System.IO;
using System.Text.Json;
using Godot;
using SmClient.Customizer;
using SmClient.Extras.Warfront.Stage;
using SmContent.Extras.Warfront;

namespace SmClient.FrontEnd;

/// <summary>
/// The Warfront page's progression parts (codebase fix 1; owner decisions 2, 4 and 8): the RESET PROGRESSION plate (enabled only while no server of this game runs on this PC), its confirmation card (the existing Yes / No style, No is the default),
/// the one-line notice the server's loader leaves in the run folder (<c>notice.json</c>: <c>{kind, utc, text}</c>, read and deleted when the page opens) and the "needs a newer build" line for a progress file of a newer build.
/// Everything is live text on the page's own plate, note and dialog; no painted labels.
/// </summary>
public sealed partial class FrontEndView
{
    internal const string WarfrontResetRow = "Reset progression";
    internal const string WarfrontResetTitle = "RESET PROGRESSION?";
    internal const string WarfrontResetBody = "THIS CLEARS YOUR LEVEL, XP AND CARDS: BACK TO LEVEL 1. THE RUN, YOUR STATS AND YOUR TWO RETAINERS STAY. THE OLD PROGRESS IS KEPT AS ONE SAFETY COPY.";
    internal const string WarfrontResetHint = "Clears your Warfront level, XP and cards on this PC: back to level 1. The run, your stats and your two retainers stay.";
    internal const string WarfrontResetRunningHint = "A server of this game is running. Stop it first.";
    internal const string WarfrontResetNoKeyHint = "Nothing to reset yet: this PC has not played Warfront.";
    internal const string WarfrontNewerNote = "YOUR PROGRESSION NEEDS A NEWER BUILD";
    internal const string WarfrontResetDone = "Progression reset: back to level 1.";
    const int WarfrontNoticeMax = 140;

    /// <summary>The notice read when the page opened and shown until the player leaves the page (the file itself is gone by then).</summary>
    string _wfNotice = "";

    /// <summary>Self-test: the progress file's store, or null when this PC has no key yet.</summary>
    internal WarfrontProgressStore? WarfrontStoreForTest => WarfrontOpenStore();

    /// <summary>This PC's progress file (read now), or null without a key file: nothing can belong to a key that does not exist yet. Never creates a key.</summary>
    WarfrontProgressStore? WarfrontOpenStore()
    {
        string? keyFile = _args.TryGetValue("warfront-key-file", out var kf) && !string.IsNullOrEmpty(kf) && !kf.StartsWith("--") ? kf : null;
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

    /// <summary>The RESET PROGRESSION plate for the page (always listed; greyed with the reason on hover when it cannot be used).</summary>
    Button WarfrontResetPlate(bool serverRunning, WarfrontProgressStore? store)
    {
        var plate = UiKit.MenuButton(WarfrontResetRow, WarfrontResetPress);
        string hint = WarfrontResetHint;
        if (serverRunning) { plate.Disabled = true; hint = WarfrontResetRunningHint; }
        else if (store is null) { plate.Disabled = true; hint = WarfrontResetNoKeyHint; }
        else if (store.Newer) { plate.Disabled = true; hint = "Your progression needs a newer build."; }
        else if (store.Unreadable) { plate.Disabled = true; hint = "Your progress file could not be read. It is left alone."; }
        UiTooltip.Attach(plate, hint);
        return plate;
    }

    /// <summary>The plate's press: the confirmation card naming what is cleared (No is the default answer).</summary>
    internal void WarfrontResetPress()
    {
        if (!_built || _wfPage is null || WarfrontServerState().Running) return;
        _wfDialog = UiKit.Dialog.Open(_wfPage, this, WarfrontResetTitle, WarfrontResetBody, ["Yes", "No"], i => { if (i == 0) WarfrontResetNow(); }, cancelIndex: 1);
        WarfrontFocusNo(_wfDialog);
    }

    void WarfrontFocusNo(Control? dialog)
    {
        Callable.From(() =>   // after the dialog's own first focus: No is the default answer
        {
            if (dialog is null || !GodotObject.IsInstanceValid(dialog) || !dialog.IsInsideTree()) return;
            foreach (var n in dialog.FindChildren("*", "Button", true, false))
                if (n is Button b && b.Text.Equals("No", StringComparison.OrdinalIgnoreCase)) { b.GrabFocus(); return; }
        }).CallDeferred();
    }

    /// <summary>The player said Yes: the safety copy, the level-1 file with its reset time; the next connection tells the host (flag 2), which honours it in the Ship phase.</summary>
    internal bool WarfrontResetNow()
    {
        if (WarfrontServerState().Running) return false;   // a campaign started meanwhile: the host would have the old progression
        var store = WarfrontOpenStore();
        if (store is null || !store.Reset(DateTime.UtcNow))
        {
            Say(store?.LastProblem is { Length: > 0 } p ? char.ToUpperInvariant(p[0]) + p[1..] + "." : WarfrontResetNoKeyHint, true);
            return false;
        }
        GD.Print("warfront: progression reset on this PC (safety copy kept)");
        ShowWarfrontPage();
        Say(WarfrontResetDone, false);
        return true;
    }

    /// <summary>Reads <c>notice.json</c> from the run folder and deletes it (a file that cannot be read is deleted too, so it never repeats). The text is one line, upper case, at most <see cref="WarfrontNoticeMax"/> characters; empty when there is none.</summary>
    internal static string WarfrontTakeNotice(string dir)
    {
        string path = Path.Combine(dir, "notice.json");
        string text = "";
        try
        {
            if (!File.Exists(path)) return "";
            string json = File.ReadAllText(path);
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* shown once more at worst */ }
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text = t.GetString() ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return ""; }
        int nl = text.IndexOfAny(['\r', '\n']);
        if (nl >= 0) text = text[..nl];
        text = text.Trim().ToUpperInvariant();
        return text.Length > WarfrontNoticeMax ? text[..WarfrontNoticeMax].TrimEnd() : text;
    }
}
