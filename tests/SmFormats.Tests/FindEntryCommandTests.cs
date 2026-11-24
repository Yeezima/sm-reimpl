using System.Text.RegularExpressions;
using SmFormats.Obpk;
using SmTool;

namespace SmFormats.Tests;

/// <summary>smtool find-entry (chaos-inventory, 2026-11-25): which synthetic archives hold an entry whose file name matches; a broken archive is reported and skipped.</summary>
public sealed class FindEntryCommandTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sm-find-entry-" + Guid.NewGuid().ToString("N"));

    public FindEntryCommandTests() => Directory.CreateDirectory(Path.Combine(_root, "data", "chunks"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    string Archive(string name, params SyntheticObpkWriter.File[] files)
    {
        string path = Path.Combine(_root, "data", "chunks", name + ".oppc");
        File.WriteAllBytes(path, SyntheticObpkWriter.Build(files));
        return path;
    }

    static Regex[] Patterns(params string[] p) => p.Select(x => new Regex(x, RegexOptions.IgnoreCase)).ToArray();

    [Fact]
    public void Finds_matching_entries_in_every_archive_by_file_name()
    {
        string a = Archive("set_a", new("dcm", "test_daemon", [1, 2, 3]), new("hkxx", "havok_test_daemon__hkxx", [4, 5]), new("dds", "unrelated_dif", [6]));
        string b = Archive("set_b", new("dcm", "Test_Daemon", [7]), new("dcm", "other", [8]));

        var hits = FindEntryCommand.Search([a, b], Patterns("test_daemon"), _root).ToList();

        Assert.Equal(3, hits.Count);
        Assert.Equal(2, hits.Count(h => h.Archive == "data/chunks/set_a.oppc"));
        Assert.Contains(hits, h => h.Archive == "data/chunks/set_b.oppc" && h.FileName == "Test_Daemon" && h.Size == 1);
        Assert.Contains(hits, h => h.EntryPath.EndsWith("havok_test_daemon__hkxx.hkxx", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, h => h.FileName is "unrelated_dif" or "other");
    }

    [Fact]
    public void Any_of_several_patterns_matches_and_none_matches_nothing()
    {
        string a = Archive("set_c", new("dcm", "alpha_mesh", [1]), new("dcm", "beta_mesh", [2]), new("dcm", "gamma_mesh", [3]));

        Assert.Equal(["alpha_mesh", "gamma_mesh"], FindEntryCommand.Search([a], Patterns("^alpha", "gamma")).Select(h => h.FileName).Order().ToArray());
        Assert.Empty(FindEntryCommand.Search([a], Patterns("delta")));
    }

    [Fact]
    public void A_broken_archive_is_reported_and_skipped()
    {
        string good = Archive("set_d", new SyntheticObpkWriter.File("dcm", "test_portal", [1]));
        string bad = Path.Combine(_root, "data", "chunks", "broken.oppc");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var warnings = new List<string>();

        var hits = FindEntryCommand.Search([bad, good], Patterns("portal"), null, warnings.Add).ToList();

        Assert.Single(hits);
        Assert.Equal("set_d.oppc", hits[0].Archive);
        Assert.Single(warnings);
        Assert.StartsWith("broken.oppc", warnings[0]);
    }
}
