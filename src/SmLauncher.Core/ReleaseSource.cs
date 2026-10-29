using System.Net;

namespace SmLauncher.Core;

/// <summary>The update check could not be done (offline, GitHub's limit, an unreadable answer). The installed build stays playable.</summary>
public sealed class ReleaseCheckException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the release list of the public releases repository: GET https://api.github.com/repos/{repo}/releases, without a token. Not
/// /releases/latest, which ignores pre-releases (every tester build is one). ship.cmd turns older builds into drafts, which this list does not show.
/// </summary>
public sealed class ReleaseSource(SafeHttp http, string repo = ReleaseSource.DefaultRepo, string apiBase = "https://api.github.com")
{
    public const string DefaultRepo = "Yeezima/sm-reimpl-releases";
    public const string IssuesUrl = "https://github.com/" + DefaultRepo + "/issues";
    public const string ReleasesUrl = "https://github.com/" + DefaultRepo + "/releases";
    const int MaxListBytes = 8 << 20;

    public string Repo => repo;
    /// <summary>Newer published releases the last check could not offer, and why (shown and logged by the window).</summary>
    public IReadOnlyList<string> LastSkipped { get; private set; } = [];

    public async Task<ReleaseInfo?> GetNewestAsync(CancellationToken ct)
    {
        var url = new Uri($"{apiBase.TrimEnd('/')}/repos/{repo}/releases?per_page=30");
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await http.SendAsync(u =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Get, u);
                    r.Headers.Accept.ParseAdd("application/vnd.github+json");
                    r.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                    return r;
                }, url, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    throw new ReleaseCheckException("GitHub is limiting update checks from this network for now. Try again in an hour; the installed build still plays.");
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode >= 500 && attempt < 3) { last = new HttpRequestException($"GitHub answered {(int)response.StatusCode}."); await Task.Delay(1000 * attempt, ct).ConfigureAwait(false); continue; }
                    throw new ReleaseCheckException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} to the update check.");
                }
                if (response.Content.Headers.ContentLength is > MaxListBytes) throw new ReleaseCheckException("The release list from GitHub is unexpectedly large.");
                string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                try { var newest = ReleaseFeed.SelectNewest(json, out var skipped); LastSkipped = skipped; return newest; }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { throw new ReleaseCheckException("GitHub's release list could not be read.", e); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                last = e;
                if (attempt < 3) await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
            }
        }
        throw new ReleaseCheckException("Could not reach GitHub to look for updates (offline?). You can still play the installed build.", last);
    }
}
