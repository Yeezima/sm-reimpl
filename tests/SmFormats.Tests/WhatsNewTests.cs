using SmTool;

namespace SmFormats.Tests;

/// <summary>The ship's "What's new" check (smtool whats-new over release/NEXT.md): tester-facing markdown passes, repository talk is refused.</summary>
public sealed class WhatsNewTests
{
    [Fact]
    public void TheRulesCommentIsDroppedAndTheBulletsKept()
    {
        string file = "<!--\nrules: no #123, no src/x.cs\n-->\n- The charge no longer needs a sprint.\n\n- Hop-dash attack in eight directions.\r\n";
        string body = WhatsNew.Body(file);
        Assert.Equal("- The charge no longer needs a sprint.\n\n- Hop-dash attack in eight directions.", body);
        Assert.Empty(WhatsNew.Problems(body));
        Assert.Equal("", WhatsNew.Body("<!-- only the rules -->\n"));
    }

    [Theory]
    [InlineData("- The client/server handshake now refuses older builds.")]
    [InlineData("- Run 2 - Play.cmd again; notes\\refine-register.md lists the new estimates.")]
    [InlineData("- Two paths merge near the lift; a branch of the corridor is blocked.")]
    [InlineData("- A long bullet\n  that continues on an indented line.\n\n### Weapons\n- Plasma vents faster.")]
    [InlineData("* Star bullets are fine too.")]
    public void TesterTextPasses(string body) => Assert.Empty(WhatsNew.Problems(body));

    [Theory]
    [InlineData("- Fixed the reload (#151).", "PR or issue")]
    [InlineData("- See src/SmSim/Combat.cs for the rule.", "file or folder")]
    [InlineData("- Changed NetworkView.Reload.cs.", "file or folder")]
    [InlineData("- Reverted in 9370eb2.", "commit hash")]
    [InlineData("- From the pull request about melee.", "git or pull requests")]
    [InlineData("- Merged into main last night.", "git or pull requests")]
    [InlineData("A plain sentence without a bullet.", "not a bullet")]
    [InlineData("  an indented line with no bullet before it", "not a bullet")]
    public void RepositoryTalkIsRefused(string body, string why) =>
        Assert.Contains(WhatsNew.Problems(body), p => p.Contains(why, StringComparison.Ordinal));

    [Fact]
    public void TheCommandWritesTheTextOrListsTheProblems()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-whatsnew-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string next = Path.Combine(dir, "NEXT.md"), outFile = Path.Combine(dir, "out.md");
            File.WriteAllText(next, "<!-- rules -->\n- Good bullet.\n");
            Assert.Equal(0, WhatsNew.Run([next, "--out", outFile]));
            Assert.Equal("- Good bullet.", File.ReadAllText(outFile));
            File.WriteAllText(next, "- Bad bullet (#12).\n");
            File.Delete(outFile);
            Assert.Equal(1, WhatsNew.Run([next, "--out", outFile]));
            Assert.False(File.Exists(outFile));
            Assert.Equal(0, WhatsNew.Run([Path.Combine(dir, "absent.md"), "--out", outFile]));   // no file: nothing new
            Assert.Equal("", File.ReadAllText(outFile));
            Assert.Equal(2, WhatsNew.Run([]));
        }
        finally { Directory.Delete(dir, true); }
    }
}
