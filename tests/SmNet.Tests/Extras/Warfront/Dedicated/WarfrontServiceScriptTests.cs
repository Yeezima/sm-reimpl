using System.Diagnostics;

namespace SmNet.Tests.Extras.Warfront.Dedicated;

/// <summary>Warfront dedicated server S4: <c>tools/release/setup-warfront-service.sh</c> parses (<c>sh -n</c>) and its <c>--dry-run</c> prints the agreed unit. Does nothing when no <c>sh</c> is on the PATH (plain Windows). The script never runs for real here.</summary>
public class WarfrontServiceScriptTests
{
    static string Script()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found"), "tools", "release", "setup-warfront-service.sh");
    }

    static (int Code, string Out)? Sh(params string[] args)
    {
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return (-1, o); }
            return (p.ExitCode, o);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    [Fact]
    public void Script_has_valid_shell_syntax()
    {
        Assert.True(File.Exists(Script()));
        if (Sh("-n", Script()) is not { } r) return;   // no sh here
        Assert.True(r.Code == 0, r.Out);
    }

    [Fact]
    public void Dry_run_prints_the_agreed_unit_and_changes_nothing()
    {
        if (Sh(Script(), "--dry-run", "/opt/sm-test-server") is not { } r) return;   // no sh here
        Assert.True(r.Code == 0, r.Out);
        Assert.Contains("ExecStart=/opt/sm-test-server/SmServer --port 27016 --game-dir /srv/sm1 --warfront-dedicated --ban-file /var/lib/sm-warfront/bans.txt", r.Out);
        foreach (string line in new[] { "User=smwarfront", "Environment=SM_WF_DIR=/var/lib/sm-warfront", "Environment=SM_DATA_DIR=/var/lib/sm-warfront/data", "Restart=on-failure", "RestartSec=5",
                     "RestartPreventExitStatus=1 2 3 5", "KillMode=mixed", "TimeoutStopSec=40", "WorkingDirectory=/var/lib/sm-warfront" })
            Assert.Contains(line, r.Out);
        Assert.DoesNotContain("smserver.service", r.Out);
        Assert.DoesNotContain("smbot", r.Out);
    }
}
