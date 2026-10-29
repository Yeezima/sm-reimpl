using System.Text.RegularExpressions;

namespace SmTool;

/// <summary>
/// <c>smtool whats-new &lt;release/NEXT.md&gt; [--out &lt;file&gt;]</c>: the "What's new" of the next playtest build for the release description (ship.cmd,
/// docs/releasing.md). The file's HTML comment (the rules) is dropped; the rest must be tester-facing markdown. Exit 0 = fine (the text is written to
/// --out, or printed), 1 = problems (each printed, nothing written), 2 = usage or an unreadable file.
/// </summary>
public static class WhatsNew
{
    static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Bullet = new(@"^\s*[-*]\s+\S", RegexOptions.Compiled);
    static readonly Regex Heading = new(@"^#{2,4}\s+\S", RegexOptions.Compiled);
    static readonly Regex Continuation = new(@"^(\s{2,}|\t)\S", RegexOptions.Compiled);
    static readonly Regex IssueNumber = new(@"(?<![\w&/])#\d+\b", RegexOptions.Compiled);
    // A repository path: a known top folder followed by more path, or any name of a source or project file. "client/server" alone is plain English, and
    // the files testers see ("2 - Play.cmd", "notes\refine-register.md") are fine.
    static readonly Regex RepoPath = new(
        @"\b(src|tools|tests|spec|docs|config|release)/[\w.\-]+|\b[\w\-]+(/[\w.\-]+)*\.(cs|csproj|ps1|sh|tscn|godot|props|targets|slnx|yml)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Hash = new(@"\b(?=[0-9a-f]*\d)(?=[0-9a-f]*[a-f])[0-9a-f]{7,40}\b", RegexOptions.Compiled);
    // Git talk only: "merge" or "branch" on their own are ordinary words in a game ("the paths merge", "a branch of the map").
    static readonly Regex GitTalk = new(@"\b(pull request|PR|git|merged? (into|to) main|feature branch|commit(ted)? [0-9a-f]{4,})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The text that goes into the description: the file without its comments, trimmed.</summary>
    public static string Body(string text) => Comment.Replace(text.Replace("\r\n", "\n"), "").Trim();

    /// <summary>Why the text cannot be published as it is; empty when it can.</summary>
    public static List<string> Problems(string body)
    {
        var problems = new List<string>();
        bool inItem = false;
        foreach (var line in body.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;            // blank lines are fine anywhere
            if (Bullet.IsMatch(line)) inItem = true;
            else if (Heading.IsMatch(line)) inItem = false;
            else if (!(inItem && Continuation.IsMatch(line))) problems.Add($"not a bullet, a heading or an indented continuation of a bullet: '{line.Trim()}'");
            if (IssueNumber.IsMatch(line)) problems.Add($"names a PR or issue number: '{line.Trim()}'");
            if (RepoPath.IsMatch(line)) problems.Add($"names a file or folder of the repository: '{line.Trim()}'");
            if (Hash.IsMatch(line)) problems.Add($"holds a commit hash: '{line.Trim()}'");
            if (GitTalk.IsMatch(line)) problems.Add($"talks about git or pull requests: '{line.Trim()}'");
        }
        return problems;
    }

    public static int Run(string[] args)
    {
        string? file = null, output = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) output = args[++i];
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && file is null) file = args[i];
            else { Console.Error.WriteLine($"whats-new: unexpected '{args[i]}'"); return 2; }
        }
        if (file is null) { Console.Error.WriteLine("usage: smtool whats-new <release/NEXT.md> [--out <file>]"); return 2; }
        string text;
        try { text = File.Exists(file) ? File.ReadAllText(file) : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"whats-new: cannot read the file ({e.GetType().Name})"); return 2; }
        string body = Body(text);
        var problems = Problems(body);
        if (problems.Count > 0) { foreach (var p in problems) Console.WriteLine(p); return 1; }
        if (output is null) Console.Write(body);
        else File.WriteAllText(output, body, new System.Text.UTF8Encoding(false));
        return 0;
    }
}
