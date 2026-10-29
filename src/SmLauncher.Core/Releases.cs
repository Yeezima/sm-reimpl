using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmLauncher.Core;

/// <summary>The playtest zip of a release: its name, size, download link and the SHA-256 GitHub computed on upload (the asset's "digest"), if any.</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string? ApiSha256);

/// <summary>A published release of the releases repository with its one playtest zip.</summary>
public sealed record ReleaseInfo(string Tag, string Title, DateTimeOffset PublishedAt, string HtmlUrl, string Body, bool Prerelease, ReleaseAsset Asset)
{
    /// <summary>The zip's SHA-256 as the release description states it (the "Download check" section), or null.</summary>
    public string? BodySha256 => ReleaseNotes.FindZipHash(Body, Asset.Name);
    public string WhatsNew => ReleaseNotes.WhatsNew(Body);
}

/// <summary>Names that come from the network or from disk and become folder or file names: checked before use.</summary>
public static class Names
{
    // \z, not $: "$" also matches before a final newline.
    static readonly Regex TagPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant);
    static readonly Regex AssetPattern = new(@"^SM-Reimpl-Playtest-[A-Za-z0-9._-]{1,100}-Windows\.zip\z", RegexOptions.CultureInvariant);
    static readonly string[] ReservedSuffixes = [".staging", ".replaced", ".tmp", ".part"];

    /// <summary>
    /// A release tag that is safe as a folder name under versions\: no separators, no "..", no trailing dot or space (Windows drops them, so "v1." would
    /// be the folder of "v1" and the cleanup would delete the version it just installed), no device name (CON, NUL, COM1...), none of the launcher's own
    /// suffixes.
    /// </summary>
    public static bool IsTag(string? tag) =>
        tag is not null && TagPattern.IsMatch(tag) && !tag.Contains("..", StringComparison.Ordinal) && !tag.EndsWith('.') &&
        PackageLayout.SafeRelative(tag) == tag &&
        !ReservedSuffixes.Any(s => tag.EndsWith(s, StringComparison.OrdinalIgnoreCase)) && !tag.Contains(".trash", StringComparison.OrdinalIgnoreCase);

    public static string CheckTag(string tag) => IsTag(tag) ? tag : throw new InvalidDataException($"'{tag}' is not a release tag the launcher accepts.");

    /// <summary>The playtest zip's asset name (SM-Reimpl-Playtest-&lt;date&gt;-&lt;version&gt;-Windows.zip); other assets are ignored.</summary>
    public static bool IsPlaytestAsset(string? name) => name is not null && AssetPattern.IsMatch(name);
}

/// <summary>Picks the release to offer from GitHub's release list (GET /repos/{repo}/releases).</summary>
public static class ReleaseFeed
{
    /// <summary>
    /// The newest published release (drafts skipped, pre-releases included: every tester build is one) that holds exactly one playtest zip;
    /// other assets are ignored. "Newest" is the latest published_at. Null when there is none.
    /// </summary>
    public static ReleaseInfo? SelectNewest(string json) => SelectNewest(json, out _);

    /// <param name="skipped">Published releases newer than the one chosen that cannot be offered, and why (two playtest zips, an unusable tag).</param>
    public static ReleaseInfo? SelectNewest(string json, out List<string> skipped)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The release list is not a JSON array.");
        ReleaseInfo? best = null;
        var problems = new List<(DateTimeOffset At, string Why)>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var info = Parse(r, out var why, out var at);
            if (info is not null && (best is null || info.PublishedAt > best.PublishedAt)) best = info;
            if (why is not null) problems.Add((at, why));
        }
        skipped = problems.Where(p => best is null || p.At > best.PublishedAt).OrderByDescending(p => p.At).Select(p => p.Why).ToList();
        return best;
    }

    static ReleaseInfo? Parse(JsonElement r, out string? why, out DateTimeOffset published)
    {
        why = null;
        published = default;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (Bool(r, "draft") != false) return null;               // a draft, or no draft field at all: not offered
        if (!DateTimeOffset.TryParse(Str(r, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out published)) return null;
        string? tag = Str(r, "tag_name");
        if (!Names.IsTag(tag)) { why = $"the release tag '{Shorten(tag)}' is not one the launcher accepts"; return null; }
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var zips = new List<ReleaseAsset>();
        foreach (var a in assets.EnumerateArray())
        {
            string? name = Str(a, "name");
            if (!Names.IsPlaytestAsset(name)) continue;
            if (!a.TryGetProperty("size", out var sizeEl) || !sizeEl.TryGetInt64(out long size) || size <= 0) continue;
            string? url = Str(a, "browser_download_url");
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out _)) continue;
            string? state = Str(a, "state");
            if (state is not null && state != "uploaded") continue;
            zips.Add(new ReleaseAsset(name!, size, url, Hashes.FromDigest(Str(a, "digest"))));
        }
        if (zips.Count == 0) return null;                          // no playtest zip: not a tester build, nothing to say
        if (zips.Count > 1) { why = $"release {tag} has {zips.Count} playtest zips, so the launcher cannot tell which one to install"; return null; }
        return new ReleaseInfo(tag!, Str(r, "name") ?? tag!, published, Str(r, "html_url") ?? "", Str(r, "body") ?? "", Bool(r, "prerelease") == true, zips[0]);
    }

    static string Shorten(string? s) => s is null ? "" : s.Length > 40 ? s[..40] + "..." : s;
    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

/// <summary>The release description (Markdown): the zip's stated hash and the "What's new" part as plain text.</summary>
public static class ReleaseNotes
{
    /// <summary>release.ps1 ends the What's new with this comment (invisible on GitHub); older descriptions are cut by their layout instead.</summary>
    public const string WhatsNewEndMarker = "<!-- whats-new-end -->";

    /// <summary>
    /// The SHA-256 the description gives for <paramref name="zipName"/>: "SHA-256 of `zip`:" followed by the hash (the template's Download check), or a
    /// table row holding the name and a hash. Null when the description states none. Other files' hashes (server packages) are never taken for it.
    /// </summary>
    public static string? FindZipHash(string body, string zipName)
    {
        if (string.IsNullOrEmpty(body)) return null;
        string name = Regex.Escape(zipName);
        var m = Regex.Match(body, @"SHA-?256 of\s*`?" + name + @"`?[^0-9a-fA-F]{0,40}?\b([0-9a-fA-F]{64})\b", RegexOptions.CultureInvariant);
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        foreach (var line in body.Split('\n'))
        {
            if (!line.Contains(zipName, StringComparison.Ordinal)) continue;
            var h = Regex.Matches(line, @"\b[0-9a-fA-F]{64}\b");
            if (h.Count == 1) return h[0].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>The "What's new" section of a description as plain text (empty when it has none).</summary>
    public static string WhatsNew(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var lines = body.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^#{1,3}\s*What'?s new", RegexOptions.IgnoreCase));
        if (start < 0) return "";
        var section = new List<string>();
        for (int i = start + 1; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.Contains(WhatsNewEndMarker, StringComparison.Ordinal)) break;
            if (Regex.IsMatch(l, @"^#{1,2}\s")) break;                                       // the next top-level section
            if (l.StartsWith("An unofficial fan project", StringComparison.Ordinal)) break;     // the template's first paragraph (no marker before it)
            section.Add(l);
        }
        return ToPlainText(string.Join("\n", section)).Trim();
    }

    /// <summary>Markdown to readable plain text: headings as lines with a colon, bullets as "•", no emphasis marks, links as their text.</summary>
    public static string ToPlainText(string markdown)
    {
        string text = Regex.Replace(markdown.Replace("\r\n", "\n"), @"<!--.*?-->", "", RegexOptions.Singleline);
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            string l = raw.TrimEnd();
            var heading = Regex.Match(l, @"^#{1,6}\s+(.*)$");
            if (heading.Success)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Inline(heading.Groups[1].Value).TrimEnd(':')).Append(":\n");
                continue;
            }
            var bullet = Regex.Match(l, @"^(\s*)[-*+]\s+(.*)$");
            if (bullet.Success) { sb.Append(bullet.Groups[1].Value).Append("• ").Append(Inline(bullet.Groups[2].Value)).Append('\n'); continue; }
            if (l.Trim() == "```") continue;
            sb.Append(Inline(l)).Append('\n');
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");
    }

    static string Inline(string s)
    {
        s = Regex.Replace(s, @"!?\[([^\]]*)\]\(([^)]*)\)", "$1");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        return s;
    }
}

public enum OfferKind { UpToDate, Newer, Older }

/// <summary>How the newest published build relates to the installed one, for the update line.</summary>
public static class UpdateOffer
{
    /// <summary>
    /// <see cref="OfferKind.Older"/> when the published build is older than the installed one (the owner took a newer build back, or the tester has a
    /// local build): by publish time, or for an adopted folder by its build date against the publish date. Anything unclear counts as newer.
    /// </summary>
    public static OfferKind Classify(string? currentTag, InstalledVersion? installed, ReleaseInfo newest)
    {
        if (currentTag == newest.Tag) return OfferKind.UpToDate;
        if (installed?.PublishedAt is { } published) return newest.PublishedAt < published ? OfferKind.Older : OfferKind.Newer;
        if (installed?.Built is { } built && DateOnly.TryParseExact(built, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
            DateOnly.FromDateTime(newest.PublishedAt.UtcDateTime) < day) return OfferKind.Older;
        return OfferKind.Newer;
    }
}
