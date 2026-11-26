using System.Text.RegularExpressions;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>
/// The Mapmaker batch of 2026-11-27 (owner's request): the edit path's work per edit (the edit-hang fix), the snap default, and where the Import images button lives. The editor's
/// own measurements and the multi-selection checks run in the editor (<c>--mm-perf</c>, docs/commands.md); these are the parts a plain test can hold.
/// </summary>
public class EditPathTests
{
    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    static string Editor(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "mapmaker", "addons", "sm_mapmaker", file));

    static string Box(int i) => "{\"op\":\"place\",\"name\":\"Block\",\"piece\":{\"kind\":\"box\"},\"transform\":{\"position\":[" + (i * 3) + ",0,0],\"scale\":[2,2,2]}}";

    [Fact]
    public void LastSnapshotsAreTheDocumentBeforeAndAfterEachBatch()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Assert.Null(s.LastBeforeJson);
        string[] steps =
        [
            "[" + string.Join(",", Enumerable.Range(0, 5).Select(Box)) + "]",
            """{"op":"transform","id":"o1","position":[1.5,2,0]}""",
            """{"op":"rename","id":"o2","name":"Renamed"}""",
            """{"op":"set-parent","id":"o3","parent":"o2"}""",
            """{"op":"set-visible","id":"o4","visible":false}""",
            """{"op":"duplicate","ids":["o1","o2"],"offset":[0,0,0]}""",
            """{"op":"delete","ids":["o5"]}""",
        ];
        foreach (var step in steps)
        {
            string before = s.Document.ToJson();
            var r = s.Apply(step);
            Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
            Assert.Equal(before, s.LastBeforeJson);
            Assert.Equal(s.Document.ToJson(), s.LastAfterJson);
        }
        // a refused batch and a dry run leave the last committed pair as it was
        string keptBefore = s.LastBeforeJson!, keptAfter = s.LastAfterJson!;
        Assert.False(s.Apply("""{"op":"transform","id":"nope","position":[0,0,0]}""").Ok);
        Assert.True(s.Apply("""{"op":"transform","id":"o1","position":[9,9,0]}""", dryRun: true).Ok);
        Assert.Same(keptBefore, s.LastBeforeJson);
        Assert.Same(keptAfter, s.LastAfterJson);
    }

    [Fact]
    public void AnEditReplacesOnlyTheEditedObjectRecords()
    {
        // The editor's view skips every object whose record is the one its node already shows (ReferenceEquals), so a batch must keep the records it did not change.
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Assert.True(s.Apply("[" + string.Join(",", Enumerable.Range(0, 200).Select(Box)) + "]").Ok);
        var before = s.Document.Objects.ToList();
        Assert.True(s.Apply("""[{"op":"transform","id":"o7","position":[1,1,1]},{"op":"transform","id":"o9","rotation":[0,0,45]}]""").Ok);
        var changed = s.Document.Objects.Where((o, i) => !ReferenceEquals(o, before[i])).Select(o => o.Id).ToList();
        Assert.Equal(["o7", "o9"], changed);
        Assert.True(s.Apply("""{"op":"rename","id":"o100","name":"Gate"}""").Ok);
        Assert.Single(s.Document.Objects.Where((o, i) => !ReferenceEquals(o, before[i]) && o.Id is not ("o7" or "o9")));
    }

    [Fact]
    public void SnapStartsOffForEveryKind()
    {
        // owner 2026-11-27: "by default snap mode should be off"; every session and every opened map, grid and angle alike (Godot's own Use Snap is switched off by the editor too)
        var host = Editor("MapHost.cs");
        Assert.Matches(new Regex(@"public static bool SnapGrid \{ get; set; \}\s*\r?\n"), host);
        Assert.Matches(new Regex(@"public static bool SnapAngle \{ get; set; \}\s*\r?\n"), host);
        Assert.Contains("EditorSnap.SwitchOffBuiltIn()", host);
        var dock = Editor("MapmakerDock.cs");
        Assert.DoesNotContain("ButtonPressed = true", Regex.Match(dock, @"_snapGridOn = new\(\)[^;]*;").Value);
        Assert.DoesNotContain("ButtonPressed = true", Regex.Match(dock, @"_snapAngleOn = new\(\)[^;]*;").Value);
        Assert.Contains("MapHost.ResetSnap();", Editor("MapmakerPlugin.cs"));   // on start and in Adopt (every map opened or started)
    }

    [Fact]
    public void ImportImagesIsInTheMaterialWindowOnly()
    {
        // owner 2026-11-27: "put the import image button into the material editor separate window instead of in the primary mapmaker ui window"
        Assert.Contains("Text = \"Import images...\"", Editor("MaterialEditor.cs"));
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "mapmaker", "addons", "sm_mapmaker"), "MapmakerDock*.cs"))
            Assert.DoesNotContain("Btn(\"Import images", File.ReadAllText(f));
    }
}
