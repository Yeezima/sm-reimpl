using System.Net;

namespace SmLauncher.Core;

/// <summary>Raised when a request or a redirect would leave the hosts the launcher may talk to. Never retried.</summary>
public sealed class NetPolicyException(string message) : Exception(message);

/// <summary>
/// Where the launcher may connect. <see cref="GitHub"/>: HTTPS only, to api.github.com (the release list), github.com (the asset link) and
/// *.githubusercontent.com (where github.com redirects asset downloads: objects. / release-assets.). Every redirect hop is checked again.
/// Tests build their own policy (a fake host, or http on 127.0.0.1).
/// </summary>
public sealed class NetPolicy
{
    public static readonly NetPolicy GitHub = new(["api.github.com", "github.com"], [".githubusercontent.com"], allowHttpLoopback: false);

    readonly HashSet<string> _hosts;
    readonly string[] _suffixes;
    readonly bool _httpLoopback;

    public NetPolicy(IEnumerable<string> hosts, IEnumerable<string> suffixes, bool allowHttpLoopback)
    {
        _hosts = new HashSet<string>(hosts.Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
        _suffixes = suffixes.Select(s => s.ToLowerInvariant()).ToArray();
        _httpLoopback = allowHttpLoopback;
    }

    public bool IsAllowed(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0) return false;
        string host = uri.IdnHost.ToLowerInvariant();
        if (uri.Scheme == Uri.UriSchemeHttp) return _httpLoopback && IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (_hosts.Contains(host)) return true;
        foreach (var s in _suffixes)
            if (host.Length > s.Length && host.EndsWith(s, StringComparison.Ordinal)) return true;
        return false;
    }

    public void Check(Uri uri)
    {
        if (!IsAllowed(uri)) throw new NetPolicyException($"The launcher only connects to GitHub over HTTPS; it refused {uri.Scheme}://{uri.Host}.");
    }
}

/// <summary>
/// One HttpClient with automatic redirects off: <see cref="SendAsync"/> follows up to 5 redirects itself and checks every URL against the policy
/// before connecting. No cookies, no credentials, no telemetry: a User-Agent (GitHub's API requires one) and the headers the caller sets.
/// </summary>
public sealed class SafeHttp : IDisposable
{
    public const int MaxRedirects = 5;
    public HttpClient Client { get; }
    public NetPolicy Policy { get; }

    public SafeHttp(NetPolicy policy, HttpMessageHandler? handler = null, string userAgent = "SM-Reimpl-Launcher")
    {
        Policy = policy;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        Client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>Sends <paramref name="make"/>(url) and follows redirects (each hop re-made with the same headers). Returns the first non-redirect response.</summary>
    public async Task<HttpResponseMessage> SendAsync(Func<Uri, HttpRequestMessage> make, Uri url, CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            Policy.Check(url);
            using var request = make(url);
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new HttpRequestException($"A redirect ({code}) without a location.");
                if (hop >= MaxRedirects) throw new HttpRequestException("Too many redirects.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            return response;
        }
    }

    public void Dispose() => Client.Dispose();
}
