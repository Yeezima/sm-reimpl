using System.Security.Cryptography;
using SmFormats;
using SmFormats.Audio;

namespace SmContent;

/// <summary>
/// The private audio cache under <see cref="GameInstall.DataRoot"/>\cache (%LOCALAPPDATA%\sm-reimpl\cache by default, SM_DATA_DIR overrides): decoded audio (<see cref="DecodedDir"/>) and
/// ww2ogg's Vorbis codebook library (<see cref="CodebookDir"/>). The codebook file is not distributed with this
/// project: it is downloaded on first use from ww2ogg's repository at a pinned commit and accepted only with the
/// expected SHA-256 (THIRD_PARTY_NOTICES.md). Nothing here is written into the repository or the game install.
/// </summary>
public static class AudioCache
{
    public const string CodebookFileName = "packed_codebooks.bin";

    /// <summary>ww2ogg 0.24 (tag "0.24", commit 4b4ca8e), the standard (non-aoTuV) library.</summary>
    public const string CodebookUrl =
        "https://raw.githubusercontent.com/hcs64/ww2ogg/4b4ca8ec4079a42488aae6344c3c52165e210eec/packed_codebooks.bin";

    public const string CodebookSha256 = "442e9442aad0bbd263df4d721d6747a74d440c899978b1fab984bcbf455fd592";
    public const long CodebookSize = 74_164;

    public static string Root => Path.Combine(GameInstall.DataRoot, "cache");
    public static string DecodedDir => Path.Combine(Root, "audio");
    public static string CodebookDir => Path.Combine(Root, "audio-codebooks");
    public static string CodebookPath(string? dir = null) => Path.Combine(dir ?? CodebookDir, CodebookFileName);

    /// <summary>True when <paramref name="data"/> has the SHA-256 <paramref name="expectedSha256"/> (hex, any case).</summary>
    public static bool HashMatches(ReadOnlySpan<byte> data, string expectedSha256) =>
        Convert.ToHexString(SHA256.HashData(data)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the cached library exists and has the expected hash.</summary>
    public static bool HasCodebooks(string? dir = null, string sha256 = CodebookSha256)
    {
        string path = CodebookPath(dir);
        try { return File.Exists(path) && HashMatches(File.ReadAllBytes(path), sha256); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The codebook library, downloading it into the cache first if it is missing or does not match the hash.
    /// Throws <see cref="IOException"/> when it cannot be downloaded and <see cref="InvalidDataException"/> when the
    /// download does not match; nothing unverified is kept.
    /// </summary>
    public static VorbisCodebookLibrary EnsureCodebooks(HttpClient? http = null, string? dir = null, string url = CodebookUrl,
        string sha256 = CodebookSha256, TextWriter? log = null)
    {
        string path = EnsureCodebooksAsync(http, dir, url, sha256, log).GetAwaiter().GetResult();
        return new VorbisCodebookLibrary(File.ReadAllBytes(path));
    }

    public static async Task<string> EnsureCodebooksAsync(HttpClient? http = null, string? dir = null, string url = CodebookUrl,
        string sha256 = CodebookSha256, TextWriter? log = null, CancellationToken cancel = default)
    {
        string path = CodebookPath(dir);
        if (HasCodebooks(dir, sha256)) return path;

        bool own = http is null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        byte[] data;
        try
        {
            log?.WriteLine($"Downloading ww2ogg's codebook library (BSD-3) from {url} ...");
            data = await http.GetByteArrayAsync(url, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new IOException($"Could not download the Vorbis codebook library from {url} ({ex.Message}). " +
                $"Check the connection, or place ww2ogg 0.24's {CodebookFileName} (SHA-256 {sha256}) in {Path.GetDirectoryName(path)}.", ex);
        }
        finally
        {
            if (own) http.Dispose();
        }
        if (!HashMatches(data, sha256))
            throw new InvalidDataException($"The downloaded codebook library does not match SHA-256 {sha256}; it was not kept.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".part";
        await File.WriteAllBytesAsync(temp, data, cancel).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Why decoded audio must not be written to <paramref name="outDir"/>, or null if it may: never inside (or above)
    /// the game install, never inside the repository.
    /// </summary>
    public static string? RefuseOutputDir(string outDir, string? gameDir, string? repoRoot)
    {
        if (gameDir is not null && InstallPaths.Overlaps(outDir, gameDir)) return "the output folder overlaps the game install";
        if (repoRoot is not null && InstallPaths.Overlaps(outDir, repoRoot)) return "decoded audio must not go into the repository";
        return null;
    }
}
