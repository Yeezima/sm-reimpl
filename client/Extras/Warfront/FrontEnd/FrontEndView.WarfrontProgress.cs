using System;
using System.IO;
using System.Text.Json;
using Godot;
using SmClient.Customizer;
using SmClient.Extras.Warfront.Stage;
using SmContent.Extras.Warfront;

namespace SmClient.FrontEnd;

/// <summary>
/// The Warfront page's progression parts (codebase fix 1; owner decisions 2, 4 and 8): the one-line notice the server's loader leaves in the run folder (<c>notice.json</c>: <c>{kind, utc, text}</c>, read and deleted
/// when the page opens) and the "needs a newer build" line for a progress file of a newer build. RESET PROGRESSION moved to OPTIONS, INTERFACE (owner 2026-11-25; <see cref="WarfrontProgressReset"/>,
/// <c>OptionsView.WarfrontReset.cs</c>). Everything is live text on the page's own note; no painted labels.
/// </summary>
public sealed partial class FrontEndView
{
    internal const string WarfrontNewerNote = "YOUR PROGRESSION NEEDS A NEWER BUILD";
    const int WarfrontNoticeMax = 140;

    /// <summary>The notice read when the page opened and shown until the player leaves the page (the file itself is gone by then).</summary>
    string _wfNotice = "";

    /// <summary>Self-test: the progress file's store, or null when this PC has no key yet.</summary>
    internal WarfrontProgressStore? WarfrontStoreForTest => WarfrontOpenStore();

    /// <summary>This PC's progress file (read now), or null without a key file. Never creates a key.</summary>
    WarfrontProgressStore? WarfrontOpenStore() =>
        WarfrontProgressReset.OpenStore(_args.TryGetValue("warfront-key-file", out var kf) ? kf : null);

    void WarfrontFocusNo(Control? dialog) => WarfrontProgressReset.FocusNo(dialog);

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
