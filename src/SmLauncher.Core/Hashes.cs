using System.Security.Cryptography;

namespace SmLauncher.Core;

/// <summary>Raised when a download or an install does not match its stated hashes or manifest. The file is deleted; the caller offers a retry.</summary>
public sealed class VerificationException(string message) : Exception(message);

public static class Hashes
{
    /// <summary>GitHub's asset digest ("sha256:&lt;hex&gt;") as lower-case hex, or null for none or another algorithm.</summary>
    public static string? FromDigest(string? digest)
    {
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest["sha256:".Length..].Trim();
        return IsSha256Hex(hex) ? hex.ToLowerInvariant() : null;
    }

    public static bool IsSha256Hex(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    /// <summary>SHA-256 of a file as lower-case hex; <paramref name="progress"/> gets the bytes hashed so far.</summary>
    public static async Task<string> Sha256FileAsync(string path, IProgress<long>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        long done = 0;
        int n;
        while ((n = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            done += n;
            progress?.Report(done);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>
    /// Checks a downloaded zip against every hash the release states: GitHub's asset digest and the description's. At least one must exist (no
    /// hash, no install), both must agree with each other and with the file. Throws <see cref="VerificationException"/> otherwise.
    /// </summary>
    public static void CheckZip(string actual, string? apiSha256, string? bodySha256, string zipName)
    {
        string expected = CheckStated(apiSha256, bodySha256, zipName);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new VerificationException($"The downloaded file is damaged (SHA-256 {Short(actual)} instead of {Short(expected)}). It was deleted; press Update to download it again.");
    }

    /// <summary>
    /// The hash a release states for its zip, checked before anything is downloaded: at least one (GitHub's digest or the description's), and both
    /// equal when both exist. Throws <see cref="VerificationException"/> otherwise, so a release that cannot be checked is never fetched.
    /// </summary>
    public static string CheckStated(string? apiSha256, string? bodySha256, string zipName)
    {
        if (apiSha256 is null && bodySha256 is null)
            throw new VerificationException($"The release states no SHA-256 for {zipName}, so its download could not be checked. Nothing was downloaded.");
        if (apiSha256 is not null && bodySha256 is not null && !string.Equals(apiSha256, bodySha256, StringComparison.OrdinalIgnoreCase))
            throw new VerificationException("The release page and GitHub disagree about the download's SHA-256. Nothing was installed.");
        return (apiSha256 ?? bodySha256)!;
    }

    static string Short(string h) => h.Length > 12 ? h[..12] + "…" : h;
}
