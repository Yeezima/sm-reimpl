using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SmLauncher.Core;

namespace SmLauncher.Tests;

/// <summary>A temporary folder per test, deleted afterwards.</summary>
sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smlauncher-tests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string this[string rel] => System.IO.Path.Combine(Path, rel);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (Exception) { } }
}

/// <summary>An in-process stand-in for GitHub: files by URL, Range support, scripted failures, and a record of every request.</summary>
sealed class FakeGitHub : HttpMessageHandler
{
    public sealed record Seen(Uri Url, long? RangeFrom);
    public List<Seen> Requests { get; } = [];
    readonly Dictionary<string, byte[]> _files = new();
    readonly Dictionary<string, Uri> _redirects = new();
    /// <summary>Per URL, scripted answers taken in order before the normal one: a status code, or a "drop after n bytes" (negative: -n).</summary>
    public Dictionary<string, Queue<int>> Script { get; } = new();
    public bool IgnoreRange { get; set; }
    /// <summary>Called after each read of a served file with the bytes sent so far on that connection (a test cancels from here).</summary>
    public Action<int>? OnServed { get; set; }
    public bool Offline { get; set; }

    public void Serve(string url, byte[] bytes) => _files[url] = bytes;
    public void Serve(string url, string text) => _files[url] = Encoding.UTF8.GetBytes(text);
    public void Redirect(string from, string to) => _redirects[from] = new Uri(to);
    public void Then(string url, params int[] answers)
    {
        if (!Script.TryGetValue(url, out var q)) Script[url] = q = new Queue<int>();
        foreach (var a in answers) q.Enqueue(a);
    }

    /// <summary>A scripted answer: hold the request until it is cancelled (no status, no headers).</summary>
    public const int Hang = 1;
    /// <summary>The error a dropped connection raises (default: a reset).</summary>
    public Func<Exception>? DropWith { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.AbsoluteUri;
        long? from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        lock (Requests) Requests.Add(new Seen(request.RequestUri, from));
        if (Offline) throw new HttpRequestException("No such host is known.", new IOException("offline"));
        if (_redirects.TryGetValue(url, out var to))
            return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = to } };
        int dropAfter = -1;
        if (Script.TryGetValue(url, out var q) && q.Count > 0)
        {
            int a = q.Dequeue();
            if (a == Hang) { await Task.Delay(Timeout.Infinite, ct); }
            if (a > 0) return new HttpResponseMessage((HttpStatusCode)a) { Content = new ByteArrayContent([]) };
            dropAfter = -a;
        }
        return Serve(url, from, dropAfter);
    }

    HttpResponseMessage Serve(string url, long? from, int dropAfter)
    {
        if (!_files.TryGetValue(url, out var bytes)) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        long start = 0;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (from is long f && !IgnoreRange)
        {
            if (f >= bytes.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            start = f;
            response.StatusCode = HttpStatusCode.PartialContent;
        }
        var body = new DroppingStream(bytes, (int)start, dropAfter, OnServed, DropWith);
        response.Content = new StreamContent(body);
        response.Content.Headers.ContentLength = bytes.Length - start;
        if (response.StatusCode == HttpStatusCode.PartialContent) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, bytes.Length - 1, bytes.Length);
        return response;
    }

    public int CountFor(string url) { lock (Requests) return Requests.Count(r => r.Url.AbsoluteUri == url); }
}

/// <summary>Serves bytes from an offset and, if asked, breaks the connection (IOException) after a number of bytes.</summary>
sealed class DroppingStream(byte[] data, int start, int dropAfter, Action<int>? onServed = null, Func<Exception>? dropWith = null) : Stream
{
    int _pos = start;
    int _sent;
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (dropAfter >= 0 && _sent >= dropAfter) throw dropWith?.Invoke() ?? new IOException("The connection was reset (test).");
        int n = Math.Min(count, data.Length - _pos);
        if (dropAfter >= 0) n = Math.Min(n, dropAfter - _sent);
        if (n <= 0) return 0;
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        _sent += n;
        onServed?.Invoke(_sent);
        return n;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Synthetic playtest zips in the release layout (numbered launchers, game\, server\, notes\ with VERSION.txt and CONTENTS-SHA256.txt). No original bytes.</summary>
static class Builds
{
    public const string Repo = "https://github.com/Yeezima/sm-reimpl-releases/releases/download/";

    public static string AssetName(string tag) => $"SM-Reimpl-Playtest-{tag["tester-".Length..]}-Windows.zip".Replace("--", "-");

    /// <summary>The files of a build with a given short sha (VERSION.txt gives tester-2026-10-29-&lt;sha&gt; as its tag).</summary>
    public static Dictionary<string, byte[]> Files(string sha, int salt = 0)
    {
        var f = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["0 - SM Reimpl Launcher.exe"] = Encoding.ASCII.GetBytes($"MZ fake launcher {sha}"),
            ["1 - READ ME FIRST.txt"] = Encoding.ASCII.GetBytes($"read me {sha}"),
            ["2 - Play.cmd"] = Encoding.ASCII.GetBytes("@echo off\r\nrem play\r\n"),
            ["3 - Host a match on this PC.cmd"] = Encoding.ASCII.GetBytes("@echo off\r\nrem host\r\n"),
            [@"game\sm-reimpl.exe"] = Rand(40_000 + salt, sha.GetHashCode() + salt),
            [@"game\data\lib.dll"] = Rand(25_000, sha.GetHashCode() * 3),
            [@"server\SmServer.exe"] = Rand(30_000, sha.GetHashCode() * 7),
            [@"notes\VERSION.txt"] = Encoding.ASCII.GetBytes($"sm-reimpl {sha}\r\ncommit: {sha}aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\nprotocol: 40\r\nbuilt: 2026-10-29 (UTC)\r\npackage: playtest-win64\r\n"),
        };
        return f;
    }

    public static string ManifestOf(Dictionary<string, byte[]> files) =>
        string.Concat(files.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{Convert.ToHexStringLower(SHA256.HashData(k.Value))}  {k.Key}\r\n"));

    /// <summary>A zip of the files plus their manifest; <paramref name="tamper"/> changes a file after the manifest was written.</summary>
    public static byte[] Zip(Dictionary<string, byte[]> files, bool manifest = true, Action<Dictionary<string, byte[]>>? tamper = null, IEnumerable<string>? extraEntries = null)
    {
        var all = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        if (manifest) all[@"notes\CONTENTS-SHA256.txt"] = Encoding.ASCII.GetBytes(ManifestOf(files));
        tamper?.Invoke(all);
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in all.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var e = zip.CreateEntry(name.Replace('\\', '/'));
                using var s = e.Open();
                s.Write(bytes);
            }
            foreach (var name in extraEntries ?? [])
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write("evil"u8);
            }
        }
        return ms.ToArray();
    }

    public static void WriteFolder(string dir, Dictionary<string, byte[]> files, bool manifest = true)
    {
        foreach (var (rel, bytes) in files)
        {
            string p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        if (manifest) File.WriteAllText(Path.Combine(dir, PackageLayout.ManifestPath), ManifestOf(files));
    }

    static byte[] Rand(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    public static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>A release for a zip, served by the fake (GitHub's digest and/or the description's hash, as asked).</summary>
    public static ReleaseInfo Release(FakeGitHub gh, string sha, byte[] zip, bool apiHash = true, bool bodyHash = true, string? wrongHash = null, int minute = 0)
    {
        string tag = $"tester-2026-10-29-{sha}";
        string name = $"SM-Reimpl-Playtest-2026-10-29-{sha}-Windows.zip";
        string url = Repo + tag + "/" + name;
        gh.Serve(url, zip);
        string hash = wrongHash ?? Sha(zip);
        string body = $"## What's new in this build\n\n- change {sha}\n\n{ReleaseNotes.WhatsNewEndMarker}\nAn unofficial fan project.\n\n## Download check\n\n" +
                      (bodyHash ? $"SHA-256 of `{name}`:\n```\n{hash}\n```\n" : "");
        return new ReleaseInfo(tag, "sm-reimpl tester build " + sha, new DateTimeOffset(2026, 10, 29, 12, minute, 0, TimeSpan.Zero),
            $"https://github.com/Yeezima/sm-reimpl-releases/releases/tag/{tag}", body, true,
            new ReleaseAsset(name, zip.Length, url, apiHash ? hash : null));
    }

    public static (Updater Updater, InstallLayout Layout, FakeGitHub GitHub, List<string> Log) NewInstall(string root, DownloadOptions? options = null, Func<string, long>? freeBytes = null)
    {
        var gh = new FakeGitHub();
        var http = new SafeHttp(NetPolicy.GitHub, gh);
        var layout = new InstallLayout(root);
        var log = new List<string>();
        var dl = new Downloader(http, options ?? Fast);
        var updater = new Updater(layout, dl, s => { lock (log) log.Add(s); }) { FreeBytes = freeBytes ?? DiskSpace.DefaultFreeBytes };
        return (updater, layout, gh, log);
    }

    public static readonly DownloadOptions Fast = new() { Backoff = _ => TimeSpan.Zero, Delay = (_, _) => Task.CompletedTask, StallTimeout = TimeSpan.FromSeconds(5) };
}
