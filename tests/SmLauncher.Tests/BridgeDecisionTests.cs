using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SmLauncher.Tests;

/// <summary>tools/release/bridge.ps1 (which earlier releases a new build hides, which one stays as the bridge) run in Windows PowerShell with fake rows; no gh, no GitHub.</summary>
public class BridgeDecisionTests
{
    const string New = "tester-new", Parts = "parts", Old = "tester-2026-11-08-264e336", Older = "tester-2026-11-01-aaaaaaa";

    static string Row(string tag, string at, bool zip, bool parts = false) =>
        $"[pscustomobject]@{{Tag='{tag}';At='{at}';Zip=${zip.ToString().ToLower()};Parts=${parts.ToString().ToLower()}}}";

    static JsonObject Run(string rows, bool archiveOnly, bool localParts, bool shipZip)
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "SmReimpl.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string lib = Path.Combine(root!, "tools", "release", "bridge.ps1");
        string cmd = $". '{lib}'; $d = Get-BridgeDecision -Rows @({rows}) -Tag '{New}' -PartsTag '{Parts}' -HasLocalParts ${localParts.ToString().ToLower()} " +
                     $"-ShipUploadsZip ${shipZip.ToString().ToLower()} -ArchiveOnly ${archiveOnly.ToString().ToLower()}; " +
                     "@{ Hide = @($d.Hide); Bridge = $d.Bridge; Warn = $d.Warn } | ConvertTo-Json -Compress";
        var psi = new ProcessStartInfo("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", cmd })
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, e + o);
        return JsonNode.Parse(o)!.AsObject();
    }

    static string[] Hide(JsonObject r) => r["Hide"] switch { JsonArray a => a.Select(x => x!.ToString()).ToArray(), JsonValue v => new[] { v.ToString() }, _ => Array.Empty<string>() };
    static string? Str(JsonObject r, string k) => r[k]?.ToString();

    // Today's shape: an old dual-format release with a full zip, the parts release, and the new parts-only release.
    static readonly string Typical = string.Join(",", Row(Old, "2026-11-08", true, true), Row(Older, "2026-11-01", true), Row(Parts, "2026-11-10", false), Row(New, "2026-11-22", false, true));

    [Theory]
    [InlineData(true)]    // local parts present: the bridge stays
    [InlineData(false)]   // no local parts (old rule: counts as having a zip, so nothing is kept)
    public void Normal_ship_matches_the_old_rule(bool localParts)
    {
        var r = Run(Typical, false, localParts, false);
        if (localParts) { Assert.Equal(Old, Str(r, "Bridge")); Assert.Equal(new[] { Older }, Hide(r)); }
        else { Assert.Null(Str(r, "Bridge")); Assert.Equal(new[] { Old, Older }.OrderBy(x => x), Hide(r).OrderBy(x => x)); }
        Assert.Null(Str(r, "Warn"));
    }

    [Fact]
    public void Normal_ship_with_local_parts_keeps_the_bridge_and_never_the_parts_release()
    {
        var r = Run(Typical, false, true, false);
        Assert.Equal(Old, Str(r, "Bridge"));
        Assert.DoesNotContain(Parts, Hide(r));
        Assert.DoesNotContain(Old, Hide(r));
    }

    [Fact]
    public void Normal_ship_that_uploads_a_zip_hides_old_zip_builds()
    {
        var r = Run(Typical, false, true, true);
        Assert.Null(Str(r, "Bridge"));
        Assert.Equal(new[] { Old, Older }.OrderBy(x => x), Hide(r).OrderBy(x => x));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArchiveOnly_keeps_the_bridge_with_or_without_local_artifacts(bool localParts)
    {
        var r = Run(Typical, true, localParts, false);
        Assert.Equal(Old, Str(r, "Bridge"));
        Assert.Equal(new[] { Older }, Hide(r));
        Assert.DoesNotContain(Old, Hide(r));
        Assert.DoesNotContain(Parts, Hide(r));
        Assert.Null(Str(r, "Warn"));
    }

    [Fact]
    public void ArchiveOnly_ignores_a_stale_local_zip_flag()
    {
        // the published release has no zip: local "this run uploaded a zip" must not count on this path
        var r = Run(Typical, true, false, true);
        Assert.Equal(Old, Str(r, "Bridge"));
    }

    [Fact]
    public void New_release_with_its_own_zip_lets_old_zip_builds_be_hidden()
    {
        string rows = string.Join(",", Row(Old, "2026-11-08", true), Row(Parts, "2026-11-10", false), Row(New, "2026-11-22", true, true));
        foreach (var local in new[] { true, false })
        {
            var r = Run(rows, true, local, false);
            Assert.Null(Str(r, "Bridge"));
            Assert.Equal(new[] { Old }, Hide(r));
        }
        var n = Run(rows, false, true, false);   // normal path: the published zip counts too (as before)
        Assert.Null(Str(n, "Bridge"));
        Assert.Equal(new[] { Old }, Hide(n));
    }

    [Fact]
    public void ArchiveOnly_with_no_rows_hides_nothing_and_warns()
    {
        var r = Run("", true, false, false);
        Assert.Empty(Hide(r));
        Assert.Null(Str(r, "Bridge"));
        Assert.False(string.IsNullOrWhiteSpace(Str(r, "Warn")));
    }

    [Fact]
    public void ArchiveOnly_with_the_new_tag_missing_hides_nothing_and_warns_naming_it()
    {
        string rows = string.Join(",", Row(Old, "2026-11-08", true), Row(Parts, "2026-11-10", false));
        var r = Run(rows, true, false, false);
        Assert.Empty(Hide(r));
        Assert.Null(Str(r, "Bridge"));
        Assert.Contains(New, Str(r, "Warn"));
    }
}
