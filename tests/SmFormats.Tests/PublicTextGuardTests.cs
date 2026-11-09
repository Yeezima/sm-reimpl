using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SmTool;

namespace SmFormats.Tests;

/// <summary>
/// The public-text guard (owner, 2026-11-10: "Scrub local pc folder paths ands any other sensitive or identifying information from public view"). The repository is public, so every
/// tracked file (code, comments, docs, spec, release notes) is checked for what would identify the owner's machine or accounts: a drive-letter path under a user's folder or into the
/// project's local media folder, a home path, an e-mail address that is not a public noreply or example address, a Discord webhook or bot token, a SteamID64, and the user name and
/// machine name of whoever runs the tests (read from the environment, so this file names nobody). A finding prints the file, the line and the rule, never the text it matched.
/// The release scan (<see cref="PackageScanner"/>, smtool scan-package) checks the packages; this test checks the tree before anything is committed or shipped.
/// </summary>
public class PublicTextGuardTests
{
    /// <summary>
    /// The allowlist: (path, rule) pairs that hold such a shape on purpose, each with its reason. Keep it small; prefer a placeholder (<c>&lt;name&gt;</c>, <c>%LOCALAPPDATA%</c>,
    /// <c>&lt;media folder&gt;</c>) or a run-time assembled fixture (<c>PackageScannerTests.Fix</c>) to a new entry.
    /// Empty since 2026-11-10: the release scan's fixtures are assembled at run time and the test paths use placeholder names (<c>test</c>, <c>&lt;name&gt;</c>).
    /// </summary>
    static readonly (string Path, string Rule, string Why)[] Allowed = [];

    /// <summary>Account names too generic to tell anyone apart (a tester's machine may use one); the release scan's placeholders (<see cref="PackageScanner.IsPlaceholderUser"/>) too.</summary>
    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "guest", "owner", "player", "server", "build", "builder", "dev", "developer", "localhost", "ubuntu", "root", "vagrant", "docker",
    };

    /// <summary>A drive-letter path into the project's own folder at a drive's root, or into any <c>media\previews</c> folder (the owner's local captures, never in the repository).</summary>
    static readonly Regex MediaFolderPath = new(@"(?<![A-Za-z])[A-Za-z]:[\\/]+(?:sm-reimpl(?![A-Za-z0-9_-])|(?:[^\\/\s""'`<>|*?]+[\\/]+)*media[\\/]+previews(?![A-Za-z0-9_-]))", RegexOptions.IgnoreCase);

    /// <summary>The Git Bash form of a user folder (a slash, a drive letter, Users, a name), which the release scan's home-path rule does not see.</summary>
    static readonly Regex GitBashUserPath = new(@"(?<![\w.])/[A-Za-z]/Users/(?<name>[A-Za-z_][A-Za-z0-9_.-]{0,31})/", RegexOptions.IgnoreCase);

    /// <summary>A SteamID64 (the individual-account range 7656119...: 17 digits).</summary>
    static readonly Regex SteamId64 = new(@"(?<![0-9])7656119[0-9]{10}(?![0-9])");

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    /// <summary>The tracked files (git ls-files), or null in a tree without git (the source zip: the release scan covers it).</summary>
    static List<string>? TrackedFiles(string root)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "ls-files", "-z" }) start.ArgumentList.Add(a);
            using var git = Process.Start(start);
            if (git is null) return null;
            _ = git.StandardError.ReadToEndAsync();
            string all = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            if (git.ExitCode != 0) return null;
            return all.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>A name from the environment worth looking for: long enough, not a placeholder or a generic account.</summary>
    static string? Distinctive(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length < 4 || PackageScanner.IsPlaceholderUser(name.Trim()) || GenericNames.Contains(name.Trim()) ? null : name.Trim();

    /// <summary>
    /// The running user's name where it identifies someone, never as a plain word (review, 2026-11-10: a user called "main" or "runner" must not fail on "merge into main" or "the
    /// test runner"): as the folder after <c>\Users\</c>, <c>/Users/</c> or <c>/home/</c> (also <c>/c/Users/</c>), or as a token of an e-mail's local part (a GitHub noreply address is public).
    /// </summary>
    internal static Func<string, bool> UserNameRule(string name)
    {
        string n = Regex.Escape(name);
        var path = new Regex(@"(?:[\\/]Users[\\/]+|/home/)" + n + @"(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase);
        var token = new Regex(@"(?<![A-Za-z])" + n + @"(?![A-Za-z])", RegexOptions.IgnoreCase);
        return line =>
        {
            if (path.IsMatch(line)) return true;
            if (!line.Contains('@')) return false;
            foreach (Match m in PackageScanner.Email.Matches(line))
            {
                string domain = m.Groups["domain"].Value;
                if (domain.EndsWith("noreply.github.com", StringComparison.OrdinalIgnoreCase)) continue;
                string local = m.Value[..m.Value.IndexOf('@')];
                if (token.IsMatch(local)) return true;
            }
            return false;
        };
    }

    /// <summary>
    /// The machine's name only where it names a host, never as a plain word (a machine called "MAIN" must not fail on "the MAIN menu"): a UNC or URL host (<c>\\name\</c>, <c>//name/</c>),
    /// a local-network host name (<c>name.local</c>, <c>.lan</c>, <c>.home</c>, <c>.internal</c>, <c>.localdomain</c>), or a host, machine or computer name setting (<c>hostname: name</c>).
    /// </summary>
    internal static Func<string, bool> MachineNameRule(string name)
    {
        string n = Regex.Escape(name);
        var host = new Regex(
            @"(?:\\\\|//)" + n + @"(?=[\\/:]|\s|$)" +
            @"|(?<![A-Za-z0-9-])" + n + @"\.(?:local|lan|home|internal|localdomain)(?![A-Za-z0-9-])" +
            @"|(?:host|machine|computer)(?:[ _-]?name)?\s*[=:]\s*[""'`]?" + n + @"(?![A-Za-z0-9-])",
            RegexOptions.IgnoreCase);
        return host.IsMatch;
    }

    /// <summary>Every rule that a line of text breaks (names only).</summary>
    internal static IEnumerable<string> Rules(string line, IReadOnlyList<(string Rule, Func<string, bool> Breaks)> local)
    {
        if (line.Contains("Users", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in PackageScanner.UserPath.Matches(line))
                if (!PackageScanner.IsPlaceholderUser(m.Groups["name"].Value)) { yield return "drive-user-path"; break; }
            foreach (Match m in GitBashUserPath.Matches(line))
                if (!PackageScanner.IsPlaceholderUser(m.Groups["name"].Value)) { yield return "home-path"; break; }
        }
        if (line.Contains("/home/", StringComparison.Ordinal) || line.Contains("/Users/", StringComparison.Ordinal))
            foreach (Match m in PackageScanner.HomePath.Matches(line))
                if (!PackageScanner.IsPlaceholderUser(m.Groups["name"].Value)) { yield return "home-path"; break; }
        if (line.Contains(':') && MediaFolderPath.IsMatch(line)) yield return "media-folder-path";
        if (line.Contains('@'))
            foreach (Match m in PackageScanner.Email.Matches(line))
                if (!PackageScanner.IsPublicEmailDomain(m.Groups["domain"].Value)) { yield return "personal-email"; break; }
        if (line.Contains("webhooks/", StringComparison.OrdinalIgnoreCase) && PackageScanner.Webhook.IsMatch(line)) yield return "discord-webhook";
        if (line.Contains('.') && PackageScanner.BotToken.IsMatch(line)) yield return "discord-bot-token";
        if (line.Contains("7656119", StringComparison.Ordinal) && SteamId64.IsMatch(line)) yield return "steam-id";
        foreach (var (rule, breaks) in local)
            if (breaks(line)) yield return rule;
    }

    [Fact]
    public void No_tracked_file_holds_a_local_path_an_account_or_a_personal_address()
    {
        string root = Root();
        var files = TrackedFiles(root);
        if (files is null) return;   // no git here (the source zip): the release scan's job
        Assert.True(files.Count > 100, $"git ls-files listed {files.Count} files");

        var local = new List<(string, Func<string, bool>)>();
        if (Distinctive(Environment.UserName) is { } user) local.Add(("local-user-name", UserNameRule(user)));
        if (Distinctive(Environment.MachineName) is { } machine) local.Add(("machine-name", MachineNameRule(machine)));

        var findings = new List<string>();
        foreach (string rel in files)
        {
            string name = rel.Replace('\\', '/');
            if (name.EndsWith("/local.json", StringComparison.OrdinalIgnoreCase) || name.Equals("local.json", StringComparison.OrdinalIgnoreCase)) continue;   // never opened (git-ignored anyway)
            string path = Path.Combine(root, rel);
            if (!File.Exists(path)) continue;   // deleted in the working tree
            byte[] bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0) continue;   // binary
            string[] lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (int i = 0; i < lines.Length; i++)
                foreach (string rule in Rules(lines[i], local).Distinct())
                    if (!Allowed.Any(a => a.Path == name && a.Rule == rule)) findings.Add($"{name}:{i + 1} [{rule}]");
        }
        Assert.True(findings.Count == 0, $"{findings.Count} identifying string(s) in tracked files (text not printed; use a placeholder such as <media folder>, %LOCALAPPDATA% or <name>):\n" + string.Join("\n", findings.Take(60)));
    }

    /// <summary>The rules on synthetic lines (assembled at run time, so this file trips none of them).</summary>
    [Theory]
    [InlineData("shots in `X:%S%sm-reimpl%S%media%S%previews%S%a`", "media-folder-path")]
    [InlineData("shots in `X:%S%Captures%S%media%S%previews%S%a`", "media-folder-path")]
    [InlineData("cache at X:%S%%U%%S%jdoe%S%AppData%S%Local%S%sm-reimpl", "drive-user-path")]
    [InlineData("cd /x/%U%/jdoe/project", "home-path")]
    [InlineData("mail jdoe%AT%mail.invalid.test", "personal-email")]
    [InlineData("steam 7656119%D%", "steam-id")]
    public void A_line_with_an_identifying_shape_is_caught(string template, string rule)
    {
        string line = template.Replace("%S%", "\\").Replace("%U%", "Users").Replace("%AT%", "@").Replace("%D%", "8000000000");
        Assert.Contains(rule, Rules(line, []));
    }

    /// <summary>A synthetic line assembled at run time (%S% a backslash, %U% Users, %H% home, %AT% an at sign), so this file trips none of the rules it tests.</summary>
    static string Fix(string t) => t.Replace("%S%", "\\").Replace("%U%", "Users").Replace("%H%", "home").Replace("%AT%", "@");

    /// <summary>Review, 2026-11-10: a common-word account name (a CI user "runner", "main") or a public account name is not a finding as a plain word or in a noreply address.</summary>
    [Theory]
    [InlineData("main", "merge into main as soon as it is verified")]
    [InlineData("runner", "the test runner starts every class")]
    [InlineData("main", "X:%S%mainline%S%build and /srv/main/data")]
    [InlineData("someone", "https://github.com/someone/sm-reimpl")]
    [InlineData("someone", "Co-Authored-By: 1234+someone%AT%users.noreply.github.com")]
    [InlineData("jdoe", "jdoe's notes on the charge")]
    public void The_user_name_as_a_plain_word_is_not_caught(string name, string template) => Assert.False(UserNameRule(name)(Fix(template)));

    [Theory]
    [InlineData("jdoe", "cache at X:%S%%U%%S%jdoe%S%AppData")]
    [InlineData("jdoe", "cd /x/%U%/jdoe/project")]
    [InlineData("jdoe", "cd /%H%/jdoe/.config")]
    [InlineData("jdoe", "mail tech.jdoe%AT%mail.test")]
    public void The_user_name_in_a_user_folder_or_an_address_is_caught(string name, string template) => Assert.True(UserNameRule(name)(Fix(template)));

    /// <summary>Review, 2026-11-10: a machine called "MAIN" is not a finding as a plain word.</summary>
    [Theory]
    [InlineData("MAIN", "the MAIN menu opens")]
    [InlineData("MAIN", "merge into main; Mainframe; main.cs")]
    [InlineData("MAIN", "see https://github.com/x/main-branch and // main loop")]
    public void The_machine_name_as_a_plain_word_is_not_caught(string name, string template) => Assert.False(MachineNameRule(name)(Fix(template)));

    [Theory]
    [InlineData("BOX-1", "copy to %S%%S%box-1%S%share")]
    [InlineData("BOX-1", "open //BOX-1/share")]
    [InlineData("BOX-1", "ssh admin%AT%box-1.local")]
    [InlineData("BOX-1", "hostname: box-1")]
    public void The_machine_name_naming_a_host_is_caught(string name, string template) => Assert.True(MachineNameRule(name)(Fix(template)));

    [Theory]
    [InlineData("captures in the owner's media folder (`<media folder>%S%previews%S%a`)")]
    [InlineData("cache at %LOCALAPPDATA%%S%sm-reimpl%S%cache")]
    [InlineData("X:%S%%U%%S%test%S%AppData%S%Local")]
    [InlineData("X:%S%%U%%S%<name>%S%...")]
    [InlineData("discover.cmd -GameDir \"X:%S%path%S%to%S%game\"")]
    [InlineData("Co-Authored-By: Claude <noreply%AT%anthropic.com>")]
    [InlineData("1234+someone%AT%users.noreply.github.com")]
    [InlineData("design section D: full under 15 m")]
    public void A_placeholder_or_public_address_is_not_caught(string template)
    {
        string line = template.Replace("%S%", "\\").Replace("%U%", "Users").Replace("%AT%", "@");
        Assert.Empty(Rules(line, []));
    }
}
